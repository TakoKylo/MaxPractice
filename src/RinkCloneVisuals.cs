// RinkCloneVisuals.cs - Makes a cloned sheet actually look like a rink.
//
// The level's rink is static-batched: dozens of MeshRenderers share a handful of
// combined meshes, and each renderer knows which submesh range of that shared mesh
// belongs to it. Instantiate copies the MeshFilter (pointing at the whole combined
// mesh) but not the batch registration, so a naive clone draws submesh 0 of the
// combined mesh - i.e. some unrelated slab of arena - at every rink piece.
//
// The combined meshes are also not CPU-readable in player builds: GetTriangles and
// .vertices come back empty, so cutting the right slice out of the mesh silently
// produces nothing. What does work is asking the GPU to draw the exact submesh:
// Graphics.DrawMesh takes a submesh index, needs no vertex access, and because
// static-batched vertices are baked in world space, the draw matrix is a pure
// translation by the sheet offset.
//
// Everything that was not batched (glass, nets) clones correctly and keeps its baked
// lighting: lightmaps are sampled through per-vertex UV2, so a translated clone reads
// the same texels as the original and looks like the real rink for free.
//
// Lighting the rest is the interesting part. The proxy draws cannot sample a lightmap
// at all - the shader variant is chosen per renderer, not per draw - so the sheet gets
// the arena's actual fixtures and reflection probes cloned onto it at the same offset,
// plus a flat ambient floor so the proxy geometry is not lit by nothing. An invented
// three-point rig was tried first and lit the sheet without ever looking like the rink;
// it survives only as the fallback for an arena with no fixtures worth copying.

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxPractice
{
    internal sealed class RinkCloneVisuals : MonoBehaviour
    {
        private struct DrawEntry
        {
            public int Sheet;
            public MeshRenderer Source;   // resolved live so material swaps follow
            public int MaterialSlot;
            public Mesh Mesh;
            public int SubMesh;
            public Matrix4x4 Matrix;
            public int Layer;

            /// <summary>Sheet offset, kept so the matrix can be rebuilt on an arena resize.</summary>
            public Vector3 Origin;

            /// <summary>Per-draw block carrying this renderer's lightmap, or the flat ambient.</summary>
            public MaterialPropertyBlock Block;

            /// <summary>Set when the block carries a lightmap and the material needs LIGHTMAP_ON.</summary>
            public bool Lightmapped;
        }

        private struct MirrorPair
        {
            public int Sheet;
            public Renderer Source;
            public Renderer Clone;
        }

        private static RinkCloneVisuals _instance;

        /// <summary>
        /// Per-sheet lighting rig roots, so visibility can switch a whole sheet's fixtures in
        /// one call rather than tracking each Light. Keyed by sheet index; see AddFillLights.
        /// </summary>
        private static readonly Dictionary<int, GameObject> _lightRigs = new Dictionary<int, GameObject>();

        private readonly List<DrawEntry> _draws = new List<DrawEntry>();
        private readonly List<MirrorPair> _mirrors = new List<MirrorPair>();

        /// <summary>Set when a mirror is added or dropped, so the next visibility pass runs
        /// even though the focused sheet has not changed.</summary>
        private bool _mirrorsDirty;

        private static PropertyInfo _subMeshStartIndexProp;
        private static bool _subMeshStartIndexResolved;

        private static MaterialPropertyBlock _ambientBlock;
        private static Color _ambientApplied = new Color(-1f, -1f, -1f, -1f);

        /// <summary>
        /// Clone geometry has no usable baked lighting, so the flat ambient is the floor
        /// it renders against. Full strength: the proxy draws and the surviving clone
        /// renderers both sample this, and anything less makes the sheet read as a dark
        /// slab next to the real rink.
        /// </summary>
        private const float CloneAmbientScale = 1f;

        private static float _nextMirrorRefresh;
        private const float MirrorRefreshSeconds = 2f;

        // Only the sheet the local viewpoint is standing on gets drawn. Sheets are
        // stacked above as well as below now, and nobody on the arena rink wants a second
        // rink hanging in the sky over them - they are separate sheets, so seeing the
        // others buys nothing and costs a few hundred draw calls each.
        private static int _visibleSheet;
        private static float _nextFocusCheck;
        private const float FocusCheckSeconds = 0.25f;

        internal static int DrawCount => _instance != null ? _instance._draws.Count : 0;

        // ------------------------------------------------------------------

        private static RinkCloneVisuals Ensure()
        {
            if (_instance != null) return _instance;

            var go = new GameObject("MaxPractice_CloneVisuals");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            _instance = go.AddComponent<RinkCloneVisuals>();
            return _instance;
        }

        /// <summary>
        /// Walk a freshly built client clone against the template it came from and
        /// decide, renderer by renderer, whether it can draw itself or needs a GPU
        /// submesh draw standing in for it.
        /// </summary>
        internal static void RegisterClone(RinkSheetSlot slot, GameObject clone, GameObject source)
        {
            if (slot == null || clone == null || source == null) return;
            if (slot.Origin.sqrMagnitude < 0.0001f) return;   // main rink draws itself

            RinkCloneVisuals self = Ensure();

            var sourceByPath = new Dictionary<string, Renderer>(StringComparer.OrdinalIgnoreCase);
            Transform sourceRoot = source.transform;
            foreach (Renderer r in source.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                string path = RelativePath(sourceRoot, r.transform);
                if (!string.IsNullOrEmpty(path)) sourceByPath[path] = r;
            }

            // Draw batched geometry the way CompetitiveAdjustments does, then offset it.
            //
            // Its GoalFrameTweaks hit this exact wall and wrote it down: the goal frame is
            // statically batched, so scaling a goal moves its colliders, its cloth net and
            // its trigger while the frame stays at vanilla size. Their fix is to hand the
            // batched renderers a world-space delta between where the geometry was BAKED
            // and where the object sits now -
            //
            //     baked = TRS(baseline position, rotation, baseline scale)
            //     delta = transform.localToWorldMatrix * baked.inverse
            //
            // - which picks up the object's own resize and the arena resize together,
            // because both land in localToWorldMatrix. A sheet wants precisely that,
            // shifted by the sheet offset.
            //
            // The baseline lives on an ArenaBaselineMarker that CompetitiveAdjustments
            // attaches, read here by name so there is no hard dependency on that mod. With
            // no marker the object's current local TRS IS its baseline, the delta collapses
            // to the arena's own matrix, and a vanilla server behaves exactly as before.
            Matrix4x4 translate = Matrix4x4.Translate(slot.Origin) * ResolveBakeDelta(source.transform);

            Transform cloneRoot = clone.transform;

            foreach (Renderer cloneR in clone.GetComponentsInChildren<Renderer>(true))
            {
                if (cloneR == null) continue;

                // Collider-visualisation meshes carry no material; force-drawing them
                // paints a black slab at ice height across the sheet.
                if (cloneR.sharedMaterial == null)
                {
                    cloneR.enabled = false;
                    continue;
                }

                // Leave this renderer's lighting exactly as it was cloned.
                //
                // Instantiate copies lightmapIndex, and lightmaps are sampled through
                // per-vertex UV2 - so a translated clone reads the same texels as the
                // original and comes out looking identical to the real rink. That is the
                // base-game look, for free, and an earlier pass here threw it away in the
                // name of matching the flat-lit proxy geometry. Wrong trade: the answer
                // is to bring the proxy geometry UP to this, not drag this down.
                //
                // Probes stay on for the same reason. They resolve against the cloned
                // reflection probe placed on this sheet, which carries the arena probe's
                // own cubemap - without that the ice loses its gloss and reads as matte
                // grey, which is exactly the "reflections aren't working" complaint.
                cloneR.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;

                // Casting stays off: a sheet stacked under the arena would otherwise
                // drop its own shadow across the rink above it.
                cloneR.shadowCastingMode = ShadowCastingMode.Off;

                // Receiving stays off too, and this is where the black patches on the
                // boards came from. A sheet 40 m under the arena sits squarely inside the
                // shadow the arena casts, so every clone surface that accepted shadows
                // got painted with the underside of the rink above it.
                cloneR.receiveShadows = false;

                // Light probes are the other half of it. The probe volume was baked
                // around the arena rink; a clone outside it resolves against whatever
                // tetrahedron happens to be nearest and samples darkness. Lightmapped
                // renderers ignore probes anyway, so this only affects the ones that
                // would otherwise go looking - and they are better off on flat ambient.
                if (!IsLightmapped(cloneR)) cloneR.lightProbeUsage = LightProbeUsage.Off;

                string path = RelativePath(cloneRoot, cloneR.transform);
                if (string.IsNullOrEmpty(path) || !sourceByPath.TryGetValue(path, out Renderer sourceR))
                    continue;

                var sourceMr = sourceR as MeshRenderer;
                MeshFilter sourceMf = sourceMr != null ? sourceMr.GetComponent<MeshFilter>() : null;

                if (sourceMr != null && sourceMr.isPartOfStaticBatch &&
                    sourceMf != null && sourceMf.sharedMesh != null &&
                    TryGetBatchSlice(sourceMr, out int firstSub, out int subCount))
                {
                    // Lost its batch registration, so it would draw the wrong slice of
                    // the shared mesh. Silence it and hand the slice to the GPU instead.
                    cloneR.enabled = false;

                    // No liveness test here, and that is deliberate.
                    //
                    // Skipping sources that look switched off was tried, to stop a retired
                    // goal frame being drawn at the wrong scale. It made the sheets vanish
                    // outright: the census read "8 off (proxy entries now 0)" - every
                    // batched renderer failed the test and nothing replaced them. Whatever
                    // enabled/activeInHierarchy report for these, it does not correspond
                    // to what the arena actually draws, so the sheet is better off copying
                    // everything than second-guessing which pieces are live.

                    Mesh mesh = sourceMf.sharedMesh;
                    for (int k = 0; k < subCount; k++)
                    {
                        int sub = firstSub + k;
                        if (sub < 0 || sub >= mesh.subMeshCount) continue;

                        var entry = new DrawEntry
                        {
                            Sheet = slot.Index,
                            Source = sourceMr,
                            MaterialSlot = k,
                            Mesh = mesh,
                            SubMesh = sub,
                            Matrix = translate,
                            Layer = cloneR.gameObject.layer,
                            Origin = slot.Origin,
                        };
                        BuildDrawBlock(ref entry, sourceMr);
                        self._draws.Add(entry);
                    }
                }
                else
                {
                    // Draws itself fine; keep its materials pointed at the template's so
                    // a reskin mod editing the real rink shows up on the sheets too.
                    self._mirrors.Add(new MirrorPair { Sheet = slot.Index, Source = sourceR, Clone = cloneR });
                    self._mirrorsDirty = true;
                    // Run the pass on the NEXT frame, not up to FocusCheckSeconds later:
                    // these clones are enabled the moment they are instantiated, so any
                    // delay is a window where the new sheet's glass and nets draw in the
                    // sky over whoever is standing on the arena rink.
                    _nextFocusCheck = 0f;
                }
            }
        }

        /// <summary>
        /// A pure translation, because sheets are built at the rink's stock size.
        ///
        /// Static-batched vertices are baked in the level root's space at its stock
        /// transform, and the clone's colliders come from the template's own local
        /// scale - neither follows the arena being resized, so neither should this.
        /// </summary>

        /// <summary>
        /// World-space delta between where an object's batched geometry was baked and
        /// where the object sits now. Identity on an untouched arena.
        /// </summary>
        internal static Matrix4x4 ResolveBakeDelta(Transform source)
        {
            if (source == null) return Matrix4x4.identity;

            Vector3 basePosition = source.localPosition;
            Vector3 baseScale = source.localScale;
            TryReadArenaBaseline(source, ref basePosition, ref baseScale);

            Matrix4x4 baked = Matrix4x4.TRS(basePosition, source.localRotation, baseScale);
            return source.localToWorldMatrix * baked.inverse;
        }

        // Resolved by name: CompetitiveAdjustments' marker is internal to its own
        // assembly, and a practice mod should not need a reference to it to co-operate.
        private static readonly Dictionary<Type, PropertyInfo[]> _baselineMembers =
            new Dictionary<Type, PropertyInfo[]>();

        private static bool TryReadArenaBaseline(Transform source, ref Vector3 position, ref Vector3 scale)
        {
            try
            {
                foreach (MonoBehaviour mb in source.GetComponents<MonoBehaviour>())
                {
                    if (mb == null) continue;

                    Type t = mb.GetType();
                    if (t.Name != "ArenaBaselineMarker") continue;

                    if (!_baselineMembers.TryGetValue(t, out PropertyInfo[] members))
                    {
                        const BindingFlags flags =
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                        members = new[]
                        {
                            t.GetProperty("BasePosition", flags),
                            t.GetProperty("BaseScale", flags),
                        };
                        _baselineMembers[t] = members;
                    }

                    if (members[0] == null || members[1] == null) return false;

                    position = (Vector3)members[0].GetValue(mb, null);
                    scale = (Vector3)members[1].GetValue(mb, null);
                    return true;
                }
            }
            catch { }

            return false;
        }

        internal static void UnregisterSheet(int sheetIndex)
        {
            // Before the _instance guard: the rig belongs to the sheet root, which is being
            // destroyed either way, so the entry has to go even if no pump was ever created.
            _lightRigs.Remove(sheetIndex);

            if (_instance == null) return;
            _instance._draws.RemoveAll(e => e.Sheet == sheetIndex);
            _instance._mirrors.RemoveAll(m => m.Sheet == sheetIndex);
            _instance._mirrorsDirty = true;
            _nextFocusCheck = 0f;
        }

        internal static void Clear()
        {
            if (_instance == null) return;
            _instance._draws.Clear();
            _instance._mirrors.Clear();
        }

        internal static void Shutdown()
        {
            _lightRigs.Clear();

            if (_instance == null) return;
            // Before the pump goes away: a renderer we left with shadows off has nothing
            // to turn them back on again, and another mod's scenery must not be abandoned
            // out at a sheet offset that no longer exists.
            try { SheetBleed.Reset(); }
            catch { }
            try { SceneryFollow.Reset(); }
            catch { }
            try { Destroy(_instance.gameObject); }
            catch { }
            ClearLightmappedMaterials();
            _instance = null;
            // Static, so it outlives the instance and would carry a stale focus index
            // into the next session, suppressing the first visibility pass there too.
            _visibleSheet = 0;
            _nextFocusCheck = 0f;
            _nextMirrorRefresh = 0f;
            _ambientBlock = null;
            _ambientApplied = new Color(-1f, -1f, -1f, -1f);
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// Sheet this client is drawing. Anything scoped to the local sheet that runs off
        /// this pump should read this rather than resolving its own, so the geometry, the
        /// suppressed shadows and the puck elevation markers cannot disagree about which
        /// rink you are on.
        ///
        /// MinimapSheetView deliberately does NOT: it resolves per frame, both because it
        /// has no dependency on clone visuals having been built and because it draws at
        /// its own rate. That leaves the map up to <see cref="FocusCheckSeconds"/> ahead
        /// of the world after a sheet change, which is a quarter second of the minimap
        /// being right first.
        /// </summary>
        internal static int VisibleSheet => _visibleSheet;

        private void LateUpdate()
        {
            // Resolve focus FIRST. Everything below reads _visibleSheet, and doing this
            // after them left them a frame behind - but the real bug was that it sat past
            // the early-out: a session where this sheet contributed no proxy draws and no
            // mirrors never re-resolved focus at all, so _visibleSheet stayed pinned at 0
            // while the player walked onto sheet 2 and the movers were all judged against
            // the wrong rink.
            if (Time.unscaledTime >= _nextFocusCheck)
            {
                _nextFocusCheck = Time.unscaledTime + FocusCheckSeconds;
                RefreshVisibleSheet();
            }

            // Ahead of the early-out below: neither the movers nor another mod's scenery
            // are ours, so both need scoping whether or not this sheet contributed any
            // proxy draws of its own.
            SheetBleed.Tick();
            SceneryFollow.Tick(_visibleSheet);

            if (_draws.Count == 0 && _mirrors.Count == 0) return;

            MaterialPropertyBlock block = AmbientBlock();

            for (int i = _draws.Count - 1; i >= 0; i--)
            {
                DrawEntry e = _draws[i];
                if (e.Mesh == null || e.Source == null)
                {
                    _draws.RemoveAt(i);
                    continue;
                }

                if (e.Sheet != _visibleSheet) continue;

                Material mat = ResolveMaterial(e.Source, e.MaterialSlot);
                if (mat == null) continue;

                // Lightmapped draws need the variant that samples one, plus their own
                // block; everything else shares the flat ambient block.
                if (e.Lightmapped)
                {
                    mat = LightmappedVariant(mat);
                    if (mat == null) continue;
                }

                // receiveShadows false for the same reason as the real clone renderers:
                // a stacked sheet lives in the arena's shadow, and accepting it stains
                // the boards black.
                Graphics.DrawMesh(
                    e.Mesh, e.Matrix, mat, e.Layer, null, e.SubMesh, e.Block ?? block,
                    ShadowCastingMode.Off, false, null, LightProbeUsage.Off, null);
            }

            if (Time.unscaledTime >= _nextMirrorRefresh)
            {
                _nextMirrorRefresh = Time.unscaledTime + MirrorRefreshSeconds;
                RefreshMirrors();
            }
        }

        /// <summary>
        /// Work out which sheet to draw from the camera rather than the local player:
        /// it follows the player anyway, and it keeps working for spectators and replays.
        /// </summary>
        private void RefreshVisibleSheet()
        {
            Camera cam = Camera.main;
            int resolved = cam == null ? 0 : RinkSheets.VisibleSheetFor(cam.transform.position);

            // The pass has to run when a sheet is BUILT, not only when focus moves.
            // Clone renderers come out of Instantiate enabled and nothing else turns
            // them off, so keying purely off a change of _visibleSheet left a sheet
            // announced while the player was already parked on that index fully visible
            // - the second rink hanging in the sky this class exists to prevent - until
            // they happened to walk onto another sheet and back.
            if (resolved == _visibleSheet && !_mirrorsDirty) return;
            _visibleSheet = resolved;
            _mirrorsDirty = false;

            // The self-drawing renderers have to follow the proxy draws.
            for (int i = _mirrors.Count - 1; i >= 0; i--)
            {
                MirrorPair m = _mirrors[i];
                if (m.Clone == null) { _mirrors.RemoveAt(i); continue; }

                bool on = m.Sheet == _visibleSheet;
                if (m.Clone.enabled != on) m.Clone.enabled = on;
            }

            // And so do the lights, or a sheet you are not on keeps lighting the one you are.
            foreach (var kvp in _lightRigs)
            {
                GameObject rig = kvp.Value;
                if (rig == null) continue;

                bool on = kvp.Key == _visibleSheet;
                if (rig.activeSelf != on) rig.SetActive(on);
            }
        }

        private void RefreshMirrors()
        {
            for (int i = _mirrors.Count - 1; i >= 0; i--)
            {
                MirrorPair m = _mirrors[i];
                if (m.Source == null || m.Clone == null)
                {
                    _mirrors.RemoveAt(i);
                    continue;
                }

                // Visibility is owned by RefreshVisibleSheet; don't fight it here.
                if (m.Sheet != _visibleSheet) continue;

                // GetSharedMaterials into reused lists - the sharedMaterials GETTER marshals
                // a fresh Material[] out of native code on every call, and this comparison
                // needs no allocation at all. ResolveMaterial in this same file already
                // carries a comment about this trap; this loop had been missed.
                m.Source.GetSharedMaterials(_mirrorSrcScratch);
                m.Clone.GetSharedMaterials(_mirrorDstScratch);
                if (_mirrorSrcScratch.Count != _mirrorDstScratch.Count) continue;

                bool same = true;
                for (int k = 0; k < _mirrorSrcScratch.Count; k++)
                {
                    if (!ReferenceEquals(_mirrorSrcScratch[k], _mirrorDstScratch[k])) { same = false; break; }
                }
                if (!same) m.Clone.sharedMaterials = _mirrorSrcScratch.ToArray();
            }
        }

        /// <summary>
        /// Give a proxy draw the same baked lighting the renderer it stands in for has.
        ///
        /// This arena has no runtime light fixtures at all - the log says so plainly, the
        /// fixture scan finds nothing to clone - so the rink's entire look is baked into
        /// lightmaps. Any sheet lit by ambient and invented fill lights was therefore
        /// never going to match it, which is why two rounds of tuning the fill rig got
        /// nowhere.
        ///
        /// Graphics.DrawMesh cannot pick a shader variant, but it can take a property
        /// block, and lightmap sampling is driven by exactly three per-renderer values:
        /// the lightmap texture, its direction map, and this renderer's tile/offset into
        /// it. Feed those through the block and use a material copy with LIGHTMAP_ON
        /// enabled, and the proxy geometry samples the same baked lighting as the
        /// original - the clone is a pure translation, and lightmap UVs are per-vertex,
        /// so it reads the very same texels.
        /// </summary>
        private static void BuildDrawBlock(ref DrawEntry entry, MeshRenderer source)
        {
            string mode = (ConfigManager.Config.RinkSheetProxyLightmaps ?? "identity").Trim().ToLowerInvariant();
            if (mode == "off")
            {
                entry.Lightmapped = false;
                entry.Block = null;
                return;
            }

            int index = source.lightmapIndex;
            LightmapData[] maps = LightmapSettings.lightmaps;

            if (index < 0 || index >= 65534 || maps == null || index >= maps.Length)
            {
                entry.Lightmapped = false;
                entry.Block = null;          // falls back to the shared ambient block
                return;
            }

            LightmapData map = maps[index];
            if (map == null || map.lightmapColor == null)
            {
                entry.Lightmapped = false;
                entry.Block = null;
                return;
            }

            // Which tile/offset to sample the atlas with is the open question, and it
            // decides whether the boards come out lit or blotched with black.
            //
            // "renderer" hands over this renderer's own lightmapScaleOffset, which is the
            // right answer for an ordinary renderer. "identity" assumes static batching
            // already folded that transform into the combined mesh's UV2 - in which case
            // applying it again lands on the wrong texels, which looks exactly like the
            // black patches on the dasher boards.
            //
            // Both are defensible readings of how Unity batches lightmapped geometry and
            // it is not something that can be settled by reading the mesh, since the
            // combined meshes are not CPU-readable. So it is a setting: flip it, look at
            // the boards, keep the one that is right.
            Vector4 st = mode == "renderer"
                ? source.lightmapScaleOffset
                : new Vector4(1f, 1f, 0f, 0f);

            var block = new MaterialPropertyBlock();
            block.SetTexture(LightmapTexId, map.lightmapColor);
            if (map.lightmapDir != null) block.SetTexture(LightmapIndId, map.lightmapDir);
            block.SetVector(LightmapStId, st);

            entry.Block = block;
            entry.Lightmapped = true;
        }

        private static bool IsLightmapped(Renderer r)
        {
            return r != null && r.lightmapIndex >= 0 && r.lightmapIndex < 65534;
        }

        private static readonly int LightmapTexId = Shader.PropertyToID("unity_Lightmap");
        private static readonly int LightmapIndId = Shader.PropertyToID("unity_LightmapInd");
        private static readonly int LightmapStId = Shader.PropertyToID("unity_LightmapST");

        // Lightmapped variants of the arena's materials. Keyed by the source material so
        // a reskin swapping the original still produces a fresh copy here.
        private static readonly Dictionary<Material, Material> _lightmappedMaterials =
            new Dictionary<Material, Material>();

        private static Material LightmappedVariant(Material source)
        {
            if (source == null) return null;

            if (_lightmappedMaterials.TryGetValue(source, out Material cached) && cached != null)
                return cached;

            var copy = new Material(source) { name = source.name + " (MP lightmapped)" };
            copy.EnableKeyword("LIGHTMAP_ON");
            if (LightmapSettings.lightmaps != null &&
                LightmapSettings.lightmaps.Length > 0 &&
                LightmapSettings.lightmaps[0] != null &&
                LightmapSettings.lightmaps[0].lightmapDir != null)
            {
                copy.EnableKeyword("DIRLIGHTMAP_COMBINED");
            }

            _lightmappedMaterials[source] = copy;
            return copy;
        }

        private static void ClearLightmappedMaterials()
        {
            foreach (var kvp in _lightmappedMaterials)
            {
                if (kvp.Value != null) Destroy(kvp.Value);
            }
            _lightmappedMaterials.Clear();
        }

        /// <summary>Reused by <see cref="ResolveMaterial"/>; see the note there.</summary>
        private static readonly List<Material> _materialScratch = new List<Material>(8);

        /// <summary>Reused by RefreshMirrors; see the note there.</summary>
        private static readonly List<Material> _mirrorSrcScratch = new List<Material>(8);
        private static readonly List<Material> _mirrorDstScratch = new List<Material>(8);

        private static Material ResolveMaterial(MeshRenderer source, int slot)
        {
            if (source == null) return null;
            if (slot <= 0) return source.sharedMaterial;

            // GetSharedMaterials fills a list we own; the sharedMaterials GETTER marshals a
            // fresh Material[] out of native code on every call, and LateUpdate reaches
            // here once per multi-material draw entry per frame - boards with separate trim,
            // ice with logo submeshes - so that was hundreds of throwaway arrays a frame.
            source.GetSharedMaterials(_materialScratch);
            return slot < _materialScratch.Count ? _materialScratch[slot] : null;
        }

        /// <summary>
        /// URP's SampleSH evaluates dot(unity_SHAr, float4(normal, 1)); putting the
        /// colour in .w and zeroing the rest gives a flat ambient the DrawMesh calls
        /// can rely on, since they deliberately sample no probes.
        /// </summary>
        private static MaterialPropertyBlock AmbientBlock()
        {
            Color ambient = SceneAmbient();
            if (_ambientBlock != null && _ambientApplied == ambient) return _ambientBlock;

            _ambientApplied = ambient;
            _ambientBlock = _ambientBlock ?? new MaterialPropertyBlock();

            Color c = ambient * CloneAmbientScale;
            _ambientBlock.SetVector("unity_SHAr", new Vector4(0f, 0f, 0f, c.r));
            _ambientBlock.SetVector("unity_SHAg", new Vector4(0f, 0f, 0f, c.g));
            _ambientBlock.SetVector("unity_SHAb", new Vector4(0f, 0f, 0f, c.b));
            _ambientBlock.SetVector("unity_SHBr", Vector4.zero);
            _ambientBlock.SetVector("unity_SHBg", Vector4.zero);
            _ambientBlock.SetVector("unity_SHBb", Vector4.zero);
            _ambientBlock.SetVector("unity_SHC", Vector4.zero);
            return _ambientBlock;
        }

        private static readonly Vector3[] _probeDirs = { Vector3.up };
        private static readonly Color[] _probeResult = new Color[1];

        private static Color SceneAmbient()
        {
            try
            {
                // Works whichever ambient mode the level uses: with Skybox ambient the
                // flat colour is meaningless and only the probe carries anything.
                SphericalHarmonicsL2 probe = RenderSettings.ambientProbe;
                probe.Evaluate(_probeDirs, _probeResult);
                Color c = _probeResult[0];
                if (c.r > 0.001f || c.g > 0.001f || c.b > 0.001f) return c;
            }
            catch { }

            Color flat = RenderSettings.ambientLight;
            return flat.maxColorComponent > 0.001f ? flat : new Color(0.45f, 0.47f, 0.5f);
        }

        // ------------------------------------------------------------------
        // Fill lights
        // ------------------------------------------------------------------

        /// <summary>Above this, duplicating the arena's whole rig would wreck light culling.</summary>
        private const int MaxClonedFixtures = 16;

        /// <summary>How far from the main rink a light or probe still counts as arena kit.</summary>
        private static readonly Vector3 ArenaSearchExtents = new Vector3(80f, 80f, 90f);

        /// <summary>
        /// Reproduce the arena's own lighting on a sheet, rather than approximating it.
        ///
        /// The first version of this invented a three-point rig, which lit the sheet but
        /// never looked like the rink - the arena's fixtures sit where they sit for a
        /// reason, and a generic overhead wash is not the same thing. So clone the real
        /// fixtures and the real reflection probes to the sheet offset, and only fall
        /// back to the invented rig when the arena has no usable fixtures to copy.
        /// </summary>
        internal static void AddFillLights(RinkSheetSlot slot, Transform parent)
        {
            if (slot == null || parent == null) return;
            if (slot.Origin.sqrMagnitude < 0.0001f) return;

            var rig = new GameObject("Lighting_" + slot.Id);
            rig.transform.SetParent(parent, false);
            rig.transform.position = slot.Origin;

            // Scoped to the visible sheet, exactly like the geometry it lights.
            //
            // The proxy draw loop already skips entries whose Sheet is not _visibleSheet, and
            // RefreshVisibleSheet switches the self-drawing mirror renderers off the same way -
            // but the lighting rigs were left on for every sheet at once. With ranges of 55-70 m
            // and sheets stacked around 40 m apart, each sheet's rig reached down onto the arena
            // rink, so opening sheets visibly lifted and tinted the main ice, and every extra
            // sheet added another full fixture set to the frame.
            _lightRigs[slot.Index] = rig;
            rig.SetActive(slot.Index == _visibleSheet);

            float gain = Mathf.Max(0f, ConfigManager.Config.RinkSheetFillLightGain);
            int probes = CloneReflectionProbes(slot, rig.transform);
            int fixtures = CloneArenaFixtures(slot, rig.transform, gain);

            if (fixtures == 0)
            {
                AddSyntheticRig(slot, rig.transform, gain);
                // Say WHICH of the two happened. Reporting the cap as "none found" sent
                // an earlier round of this chasing an arena with no lights, when in fact
                // it has 87 of them and they were all rejected for being too many.
                int nearby = CountNearbyFixtures();
                string why = nearby > MaxClonedFixtures
                    ? $"{nearby} arena fixtures is over the {MaxClonedFixtures} cap"
                    : "no arena fixtures to copy";
                ConfigManager.Log($"Sheet {slot.Label}: {why}, using a synthetic rig ({probes} reflection probe(s)).");
            }
            else
            {
                ConfigManager.Log($"Sheet {slot.Label}: lit by {fixtures} cloned arena fixture(s) and {probes} reflection probe(s).");
            }
        }

        /// <summary>
        /// Copy the arena's reflection probes onto the sheet, reusing the cubemap the
        /// original already baked. Reflections are what make the ice read as ice, and a
        /// sheet 40 m below the arena sits outside every existing probe's box - so
        /// without its own it falls back to the skybox and goes flat.
        /// </summary>
        private static int CloneReflectionProbes(RinkSheetSlot slot, Transform parent)
        {
            int cloned = 0;

            try
            {
                ReflectionProbe[] all = UnityEngine.Object.FindObjectsByType<ReflectionProbe>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);

                for (int i = 0; i < all.Length; i++)
                {
                    ReflectionProbe src = all[i];
                    if (src == null || IsOurs(src.transform)) continue;
                    if (!IsNearMainRink(src.transform.position)) continue;

                    var go = new GameObject("Probe_" + slot.Id + "_" + src.name);
                    go.transform.SetParent(parent, false);
                    go.transform.position = src.transform.position + slot.Origin;
                    go.transform.rotation = src.transform.rotation;

                    ReflectionProbe dst = go.AddComponent<ReflectionProbe>();
                    dst.size = src.size;
                    dst.center = src.center;
                    dst.intensity = src.intensity;
                    dst.blendDistance = src.blendDistance;
                    dst.boxProjection = src.boxProjection;
                    dst.importance = src.importance;
                    dst.hdr = src.hdr;
                    dst.resolution = src.resolution;
                    dst.cullingMask = src.cullingMask;
                    dst.clearFlags = src.clearFlags;
                    dst.backgroundColor = src.backgroundColor;

                    // Custom + the original's own texture: no render cost, and the sheet
                    // reflects exactly what the arena reflects. Re-rendering a realtime
                    // probe down here would capture the empty void around the sheet.
                    Texture baked = src.texture != null ? src.texture : src.bakedTexture;
                    if (baked != null)
                    {
                        dst.mode = UnityEngine.Rendering.ReflectionProbeMode.Custom;
                        dst.customBakedTexture = baked;
                    }
                    else
                    {
                        dst.mode = UnityEngine.Rendering.ReflectionProbeMode.Realtime;
                        dst.refreshMode = UnityEngine.Rendering.ReflectionProbeRefreshMode.ViaScripting;
                        dst.RenderProbe();
                    }

                    cloned++;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaxPractice] Sheet {slot.Label}: could not clone reflection probes: {ex.Message}");
            }

            return cloned;
        }

        /// <summary>
        /// Clone the arena's point and spot fixtures to the sheet. Baked fixtures emit
        /// nothing at runtime but still describe where the arena's light comes from, so
        /// they are revived as realtime at a reduced intensity rather than skipped.
        /// </summary>
        private static int CloneArenaFixtures(RinkSheetSlot slot, Transform parent, float gain)
        {
            var sources = new List<Light>();

            try
            {
                Light[] all = UnityEngine.Object.FindObjectsByType<Light>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);

                for (int i = 0; i < all.Length; i++)
                {
                    Light l = all[i];
                    // The sun is global - it already reaches the sheet, and duplicating a
                    // directional light just doubles the whole scene's exposure.
                    if (l == null || l.type == LightType.Directional) continue;
                    if (IsOurs(l.transform)) continue;
                    if (!IsNearMainRink(l.transform.position)) continue;
                    sources.Add(l);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaxPractice] Sheet {slot.Label}: could not scan arena fixtures: {ex.Message}");
                return 0;
            }

            if (sources.Count == 0 || sources.Count > MaxClonedFixtures) return 0;

            int cloned = 0;
            for (int i = 0; i < sources.Count; i++)
            {
                Light src = sources[i];
                if (src == null) continue;

                var go = new GameObject("Light_" + slot.Id + "_" + src.name);
                go.transform.SetParent(parent, false);
                go.transform.position = src.transform.position + slot.Origin;
                go.transform.rotation = src.transform.rotation;

                Light dst = go.AddComponent<Light>();
                dst.type = src.type;
                dst.color = src.color;
                dst.range = src.range;
                dst.spotAngle = src.spotAngle;
                dst.innerSpotAngle = src.innerSpotAngle;
                dst.cullingMask = src.cullingMask;
                dst.bounceIntensity = 0f;
                // Shadowless: a full arena rig per sheet, casting, is a real frame cost
                // and the sheet has no crowd or roof geometry for them to land on.
                dst.shadows = LightShadows.None;

                // A baked fixture reads 0-ish intensity at runtime because its light went
                // into the lightmap. Revive it at a sane level instead of cloning a light
                // that does nothing.
                float intensity = src.intensity;
                if ((!src.enabled || src.bakingOutput.isBaked) && intensity < 0.05f) intensity = 0.35f;
                else intensity *= 0.45f;

                dst.intensity = intensity * gain;
                dst.enabled = true;
                cloned++;
            }

            return cloned;
        }

        private static int CountNearbyFixtures()
        {
            try
            {
                int n = 0;
                Light[] all = UnityEngine.Object.FindObjectsByType<Light>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                for (int i = 0; i < all.Length; i++)
                {
                    Light l = all[i];
                    if (l == null || l.type == LightType.Directional) continue;
                    if (IsOurs(l.transform)) continue;
                    if (IsNearMainRink(l.transform.position)) n++;
                }
                return n;
            }
            catch { return 0; }
        }

        /// <summary>
        /// True for anything MaxPractice built. Every scan below has to skip these or a
        /// sheet re-clones the PREVIOUS sheet's kit: our fill lights and probes sit inside
        /// the ±80 m search box at any legal spacing, and a second clone is offset again,
        /// landing back on top of the arena's own fixtures and changing how the real rink
        /// looks. Both sheet roots are named MaxPractice_Sheet_*, so one test covers the
        /// server clone (host) and the client clone alike.
        /// </summary>
        private static bool IsOurs(Transform t)
        {
            Transform root = t != null ? t.root : null;
            return root != null && root.name.StartsWith("MaxPractice_", StringComparison.Ordinal);
        }

        private static bool IsNearMainRink(Vector3 worldPos)
        {
            return Mathf.Abs(worldPos.x) <= ArenaSearchExtents.x
                && Mathf.Abs(worldPos.y) <= ArenaSearchExtents.y
                && Mathf.Abs(worldPos.z) <= ArenaSearchExtents.z;
        }

        /// <summary>Last resort for an arena with no fixtures worth copying.</summary>
        private static void AddSyntheticRig(RinkSheetSlot slot, Transform parent, float gain)
        {
            float scale = (slot.Origin.y < -1f ? 2f : 1f) * gain;

            // Neutral white - any tint reads as a colour cast on the skaters.
            Color bulb = new Color(1f, 0.99f, 0.96f);
            AddFillLight(parent, slot.Origin + new Vector3(0f, 22f, 0f), 0.42f * scale, 70f, bulb);
            AddFillLight(parent, slot.Origin + new Vector3(0f, 18f, -28f), 0.28f * scale, 55f, bulb);
            AddFillLight(parent, slot.Origin + new Vector3(0f, 18f, 28f), 0.28f * scale, 55f, bulb);
        }

        private static void AddFillLight(Transform parent, Vector3 worldPos, float intensity, float range, Color color)
        {
            var go = new GameObject("Fill");
            go.transform.SetParent(parent, false);
            go.transform.position = worldPos;

            Light light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.intensity = intensity;
            light.range = range;
            light.shadows = LightShadows.None;
            light.renderMode = LightRenderMode.ForceVertex;
            light.enabled = true;
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// Which slice of the shared combined mesh this renderer owns:
        /// [subMeshStartIndex, subMeshStartIndex + materialCount).
        /// </summary>
        private static bool TryGetBatchSlice(MeshRenderer mr, out int firstSubMesh, out int subMeshCount)
        {
            firstSubMesh = 0;
            subMeshCount = 0;
            if (mr == null || !mr.isPartOfStaticBatch) return false;

            firstSubMesh = SubMeshStartIndex(mr);
            Material[] mats = mr.sharedMaterials;
            subMeshCount = mats != null && mats.Length > 0 ? mats.Length : 1;
            return true;
        }

        private static int SubMeshStartIndex(MeshRenderer mr)
        {
            try
            {
                if (!_subMeshStartIndexResolved)
                {
                    _subMeshStartIndexResolved = true;
                    _subMeshStartIndexProp = typeof(MeshRenderer).GetProperty(
                        "subMeshStartIndex",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }

                if (_subMeshStartIndexProp != null)
                    return (int)_subMeshStartIndexProp.GetValue(mr);
            }
            catch { }

            return 0;
        }

        private static string RelativePath(Transform root, Transform node)
        {
            if (root == null || node == null || node == root) return string.Empty;

            var parts = new List<string>();
            Transform current = node;
            while (current != null && current != root)
            {
                parts.Add(current.name);
                current = current.parent;
            }
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }
    }
}
