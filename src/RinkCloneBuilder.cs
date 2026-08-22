// RinkCloneBuilder.cs - Duplicates the level's rink at an offset.
//
// Two very different objects come out of the same templates:
//
//   Server clone  - colliders only. This is the sheet players actually skate on,
//                   so every board, post and net collider has to survive, while
//                   anything that talks to the game's own systems (Goal, Zone,
//                   NetworkBehaviour) has to be stripped. A cloned Goal would
//                   score real goals; a cloned crease Zone would report players on
//                   sheet 2 as standing in sheet 1's crease.
//
// Sheets are deliberately built at the rink's STOCK size. CompetitiveAdjustments
// resizes the arena by scaling the level root and retunes the goals on a separate
// setting, and chasing all of that kept the sheets in a fight with another mod for no
// real gain - a practice sheet does not have to be the same size as the match rink, it
// has to be a rink. Instantiate copies localScale, so simply not intervening yields
// stock geometry no matter what the arena is doing.
//
//   Client clone  - renderers only. Colliders are destroyed outright: physics is
//                   server-authoritative, and a second set of local colliders
//                   fights the replicated positions.
//
// Static batching is the catch on the client. The level's rink meshes are baked
// into shared combined meshes, and Instantiate hands the clone a MeshFilter that
// points at the whole combined mesh without the batch registration that told it
// which slice to draw - so the clone renders every other batched object's geometry
// on top of itself. The combined meshes are also not CPU-readable in player builds,
// so the mesh cannot be cut apart. RinkCloneVisuals draws the correct submesh slice
// on the GPU instead; see there.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxPractice
{
    internal static class RinkCloneBuilder
    {
        /// <summary>Level children duplicated for each sheet, in order.</summary>
        private static readonly string[] Templates = { "Rink", "Goal Blue", "Goal Red" };

        private const float DefaultIceSurfaceY = 0.03f;

        /// <summary>Roughly hip height - where a body's origin sits when standing on the ice.</summary>
        public const float PlayerHeightAboveIce = 0.9f;

        private static float _cachedIceY = float.NaN;

        /// <summary>Ice plane height of the level's own rink, measured once per level.</summary>
        public static float IceSurfaceY => float.IsNaN(_cachedIceY) ? DefaultIceSurfaceY : _cachedIceY;

        public static void ResetCache()
        {
            _cachedIceY = float.NaN;
            // Belt and braces alongside the destroyed-object check in FindLevelRoot: this is
            // called when the session ends, which is exactly when the level is going away.
            _levelRoot = null;
        }

        /// <summary>Standing height for a body dropped onto a sheet.</summary>
        public static float SpawnHeightFor(Vector3 origin) => origin.y + IceSurfaceY + PlayerHeightAboveIce;

        // ------------------------------------------------------------------
        // Templates
        // ------------------------------------------------------------------

        private static Transform _levelRoot;

        /// <summary>
        /// The arena's Level transform, found once.
        ///
        /// This is a whole-scene FindFirstObjectByType and it sits behind
        /// RinkSheets.ArenaScale and TemplatesAvailable, both of which read like cheap
        /// property lookups and are called from command handlers, the sheet-build retry and
        /// the diagnostics dump. Caching is safe because Unity's == null is true for a
        /// DESTROYED object as well as a null reference, so a level unload drops the cache on
        /// its own and the next caller re-finds the new one.
        /// </summary>
        private static Transform FindLevelRoot()
        {
            if (_levelRoot != null) return _levelRoot;

            var level = UnityEngine.Object.FindFirstObjectByType<Level>();
            _levelRoot = level != null ? level.transform : null;
            return _levelRoot;
        }

        /// <summary>True once the level exists and carries the objects we clone.</summary>
        public static bool TemplatesAvailable()
        {
            Transform root = FindLevelRoot();
            if (root == null) return false;
            return FindChild(root, Templates[0]) != null;
        }

        private static Transform FindChild(Transform root, string name)
        {
            if (root == null) return null;

            Transform direct = root.Find(name);
            if (direct != null) return direct;

            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t != null && string.Equals(t.name, name, StringComparison.OrdinalIgnoreCase))
                    return t;
            }
            return null;
        }

        /// <summary>
        /// The templates we clone are siblings under the level root in the stock arena,
        /// but the lookup falls back to a recursive search - so on a level where the goals
        /// sit under the rink, cloning both would produce two goals in the same place.
        /// Overlapping duplicate colliders is the classic way to launch a rigidbody, so
        /// resolve the whole list first and drop anything already covered by an ancestor.
        /// </summary>
        private static List<Transform> ResolveTemplates(Transform levelRoot)
        {
            var found = new List<Transform>();
            for (int i = 0; i < Templates.Length; i++)
            {
                Transform t = FindChild(levelRoot, Templates[i]);
                if (t != null && !found.Contains(t)) found.Add(t);
            }

            var kept = new List<Transform>(found.Count);
            for (int i = 0; i < found.Count; i++)
            {
                bool nested = false;
                for (int j = 0; j < found.Count; j++)
                {
                    if (i == j || found[j] == null) continue;
                    if (found[i].IsChildOf(found[j]))
                    {
                        nested = true;
                        Debug.LogWarning($"[MaxPractice] Rink template '{found[i].name}' sits inside " +
                                         $"'{found[j].name}' — cloning it separately would double its colliders, so skipping it.");
                        break;
                    }
                }
                if (!nested) kept.Add(found[i]);
            }

            return kept;
        }

        /// <summary>
        /// Stand the sheet root in for the level root, at stock size.
        ///
        /// Everything under it is then placed in level-root LOCAL coordinates, which is
        /// what makes a stock-size sheet internally consistent. Placing pieces at their
        /// world positions instead is what put the nets in the wrong spot: on a 1.2x
        /// arena the real goal sits ~49 m out, so a stock-size goal dropped at 49 m
        /// stood well behind a sheet whose own goal line is at 41.
        /// </summary>
        private static void PlaceSheetRoot(Transform root, Transform levelRoot, RinkSheetSlot slot)
        {
            root.position = levelRoot.position + slot.Origin;
            root.rotation = levelRoot.rotation;

            // The sheet carries the arena's scale, so it IS the arena's rink at an offset.
            //
            // Building sheets at stock size sounded like the safe option and is not: the
            // pieces of the arena do not all follow its scale. The rink does, but
            // CompetitiveAdjustments pins the goals to regulation size regardless - so
            // dividing the arena's scale out of everything left the rink at 1.0 and the
            // goals at 1/arenaScale. On a 3x arena that is a third-size net in a
            // full-size frame, which is precisely the "net scales down as the arena
            // scales up" that kept being reported.
            //
            // Carrying the scale instead makes each piece land at its true world size:
            // rink 3.0, goals 1.0, exactly as on the rink everyone is already playing on.
            root.localScale = levelRoot.lossyScale;
        }

        /// <summary>
        /// Place a cloned template exactly where the level root has it, expressed in
        /// level-root local space so the sheet root reproduces it at the offset.
        ///
        /// Local space rather than world space is what makes the copy exact: the root
        /// carries the arena's scale, so dividing it out here and multiplying it back in
        /// through the root leaves the piece at its true world size and its true spot on
        /// the rink - including any goal-specific resize, which is part of what the goal
        /// is rather than something to filter out.
        /// </summary>
        private static void PlaceTemplate(Transform clone, Transform src, Transform levelRoot)
        {
            clone.localPosition = levelRoot.InverseTransformPoint(src.position);
            clone.localRotation = Quaternion.Inverse(levelRoot.rotation) * src.rotation;
            clone.localScale = DivideScale(src.lossyScale, levelRoot.lossyScale);
        }

        private static Vector3 DivideScale(Vector3 value, Vector3 by)
        {
            return new Vector3(
                Mathf.Abs(by.x) < 1e-4f ? value.x : value.x / by.x,
                Mathf.Abs(by.y) < 1e-4f ? value.y : value.y / by.y,
                Mathf.Abs(by.z) < 1e-4f ? value.z : value.z / by.z);
        }


        /// <summary>
        /// Log where every cloned piece actually ended up, next to where its original is.
        ///
        /// Written after several rounds of fixing sheet placement from screenshots and
        /// getting it wrong. These four numbers per template decide whether a sheet is
        /// right, and none of them were ever in the log.
        /// </summary>
        private static void LogPlacement(GameObject root, List<Transform> templates, Transform levelRoot, RinkSheetSlot slot, string side)
        {
            try
            {
                ConfigManager.Log($"Sheet {slot.Label} [{side}] placement — " +
                                  $"levelRoot='{levelRoot.name}' pos={levelRoot.position} lossyScale={levelRoot.lossyScale}; " +
                                  $"sheetRoot pos={root.transform.position} scale={root.transform.localScale}");

                for (int i = 0; i < templates.Count; i++)
                {
                    Transform src = templates[i];
                    if (src == null) continue;

                    Transform clone = null;
                    string want = src.name.Replace(' ', '_') + "_" + slot.Id;
                    for (int c = 0; c < root.transform.childCount; c++)
                    {
                        Transform child = root.transform.GetChild(c);
                        if (child.name == want || child.name == ClonePrefix + want) { clone = child; break; }
                    }
                    if (clone == null) continue;

                    // Simply the original's world spot, shifted onto this sheet. An
                    // earlier version divided the arena scale out and forgot to put it
                    // back through the root, so a correctly placed goal reported itself
                    // 9 m adrift and sent the hunt off in the wrong direction.
                    Vector3 expected = src.position + slot.Origin;
                    ConfigManager.Log($"  '{src.name}': original pos={src.position} lossyScale={src.lossyScale} | " +
                                      $"clone pos={clone.position} lossyScale={clone.lossyScale} | " +
                                      $"expected pos={expected} | off by {(clone.position - expected).magnitude:F2} m | " +
                                      $"cloth={clone.GetComponentsInChildren<Cloth>(true).Length} " +
                                      $"skinned={clone.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length} " +
                                      $"nettingAt={DescribeNetting(clone)}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaxPractice] Placement log failed: {ex.Message}");
            }
        }








        private static string DescribeNetting(Transform clone)
        {
            try
            {
                // Size as well as position: a net in the right place at the wrong size
                // and a net at the right size in the wrong place look similar enough
                // through a screenshot, and they are not the same bug.
                string netting = "none", frame = "none";

                foreach (Renderer r in clone.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || !r.enabled || r.sharedMaterial == null) continue;

                    bool isNet = r is SkinnedMeshRenderer ||
                                 r.name.IndexOf("Net", StringComparison.OrdinalIgnoreCase) >= 0;
                    string desc = $"{r.bounds.size.ToString("F2")}@{r.bounds.center.ToString("F1")}";

                    if (isNet && netting == "none") netting = desc;
                    else if (!isNet && frame == "none") frame = desc;
                }

                return $"netting {netting} | selfDrawnFrame {frame}";
            }
            catch { }
            return "n/a";
        }

        private static void CacheIceSurfaceY(Transform levelRoot)
        {
            if (!float.IsNaN(_cachedIceY)) return;

            Transform iceTop = FindChild(levelRoot, "Ice Top");
            if (iceTop != null)
            {
                var mr = iceTop.GetComponent<MeshRenderer>();
                _cachedIceY = mr != null ? mr.bounds.center.y : iceTop.position.y;
            }
            else
            {
                _cachedIceY = DefaultIceSurfaceY;
            }
        }

        // ------------------------------------------------------------------
        // Server geometry
        // ------------------------------------------------------------------

        /// <summary>
        /// Build the collider-only copy players skate on. Returns null when the level
        /// isn't loaded yet.
        /// </summary>
        public static GameObject BuildServer(RinkSheetSlot slot)
        {
            Transform levelRoot = FindLevelRoot();
            if (levelRoot == null || slot == null) return null;

            CacheIceSurfaceY(levelRoot);

            var root = new GameObject("MaxPractice_Sheet_Server_" + slot.Id);
            root.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(root);
            PlaceSheetRoot(root.transform, levelRoot, slot);

            int cloned = 0;
            List<Transform> templates = ResolveTemplates(levelRoot);

            for (int i = 0; i < templates.Count; i++)
            {
                Transform src = templates[i];
                if (src == null) continue;

                GameObject clone = UnityEngine.Object.Instantiate(src.gameObject, root.transform);
                clone.name = src.name.Replace(' ', '_') + "_" + slot.Id;
                clone.SetActive(true);
                PlaceTemplate(clone.transform, src, levelRoot);

                StripGameplayComponents(clone, slot);
                PrepareServerVisuals(clone);
                cloned++;
            }

            if (cloned == 0)
            {
                UnityEngine.Object.Destroy(root);
                return null;
            }

            // Everything is positioned and scaled; now let Awake run.
            root.SetActive(true);
            Physics.SyncTransforms();

            EnsureIceSurface(root.transform, slot);
            MarkCloneNames(root);

            ConfigManager.Log($"Sheet {slot.Label}: server geometry built at {slot.Origin} ({cloned} template(s)).");
            LogPlacement(root, templates, levelRoot, slot, "server");
            return root;
        }

        /// <summary>
        /// Strip every game script off a clone, then hand the goal trigger volumes to
        /// our own handler.
        ///
        /// This started as a list of the components that looked dangerous - Goal, Zone,
        /// NetworkBehaviour - and that list was wrong. GoalController is an unremarkable
        /// MonoBehaviour sitting beside Goal that subscribes to the GLOBAL puck spawn and
        /// despawn events in Awake. Cloning it silently added a second pair of listeners
        /// pointed at a Goal we had just destroyed, and every puck in the session then
        /// logged two NullReferenceExceptions out of Cloth.get_sphereColliders.
        ///
        /// The lesson is that any game script on a clone is a liability, not that this
        /// particular one was: a clone is scenery, and scenery only needs its colliders
        /// and renderers, neither of which is a MonoBehaviour. So take the lot. The cost
        /// is losing local physics niceties like GoalNetCollider's in-net damping, which
        /// is a fair trade for no game script ever running twice.
        /// </summary>
        private static void StripGameplayComponents(GameObject clone, RinkSheetSlot slot)
        {
            // Remember the goal mouths before their components go: the trigger volume is
            // a plain Collider and survives, but nothing else identifies it afterwards.
            var goalMouths = new List<GameObject>();
            foreach (var trigger in clone.GetComponentsInChildren<GoalTrigger>(true))
            {
                if (trigger != null) goalMouths.Add(trigger.gameObject);
            }

            // Crease volumes likewise: note them now, clear them after the scripts are
            // gone. Zone declares [RequireComponent(typeof(Collider))], so pulling the
            // collider while the Zone is still attached is refused.
            var creaseVolumes = new List<GameObject>();
            foreach (var zone in clone.GetComponentsInChildren<Zone>(true))
            {
                if (zone != null && !goalMouths.Contains(zone.gameObject))
                    creaseVolumes.Add(zone.gameObject);
            }

            StripAllScripts(clone);

            // A crease trigger with no Zone above it does nothing but give other mods'
            // crease logic something to find and complain about.
            for (int i = 0; i < creaseVolumes.Count; i++)
            {
                GameObject volume = creaseVolumes[i];
                if (volume == null) continue;
                foreach (var c in volume.GetComponents<Collider>())
                {
                    if (c != null && c.isTrigger) SafeDestroy(c);
                }
            }

            // Cloth is a built-in component rather than a script, so it survives the
            // strip. The server has no use for a per-frame cloth sim.
            foreach (var cloth in clone.GetComponentsInChildren<Cloth>(true)) SafeDestroy(cloth);

            // Put back the one behaviour a practice net does want, using the very same
            // handler the arena's nets run rather than a lookalike: puck eaten after the
            // same delay, replacement dropped at this sheet's centre ice, same phase
            // checks. A separate implementation drifted from the original immediately.
            for (int i = 0; i < goalMouths.Count; i++)
            {
                GameObject mouth = goalMouths[i];
                if (mouth == null || mouth.GetComponent<WarmupGoalTriggerHandler>() != null) continue;
                mouth.AddComponent<WarmupGoalTriggerHandler>().SheetOrigin = slot.Origin;
            }
        }

        /// <summary>
        /// Destroy every MonoBehaviour in the clone. Netcode components go first and in
        /// order - a NetworkBehaviour outliving its NetworkObject complains - and our own
        /// components are spared so this can be re-run without eating them.
        /// </summary>
        private static void StripAllScripts(GameObject clone)
        {
            foreach (var nb in clone.GetComponentsInChildren<Unity.Netcode.NetworkBehaviour>(true))
                SafeDestroy(nb);
            foreach (var no in clone.GetComponentsInChildren<Unity.Netcode.NetworkObject>(true))
                SafeDestroy(no);

            foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                if (mb is WarmupGoalTriggerHandler) continue;
                SafeDestroy(mb);
            }
        }

        /// <summary>
        /// Rename every object inside a clone so nothing else can find it by name.
        ///
        /// Stripping the scripts is not enough on its own: other mods look scenery up by
        /// GameObject name, and a clone answering to "Crease Zone" or "Ice Top" hijacks
        /// lookups that were meant for the real rink. CompAdjust's crease protection was
        /// warning ten times a session about a crease zone with no goal above it - ours.
        ///
        /// Must run AFTER RinkCloneVisuals has matched the clone against its template,
        /// since that pairing walks the hierarchy by path.
        /// </summary>
        private static void MarkCloneNames(GameObject root)
        {
            if (root == null) return;

            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t == root.transform) continue;
                if (t.name.StartsWith(ClonePrefix, StringComparison.Ordinal)) continue;
                t.name = ClonePrefix + t.name;
            }
        }

        /// <summary>
        /// Prefixed onto every object inside a finished clone. Internal rather than private
        /// because anything matching a clone against its template has to strip it — see
        /// RinkSheetDiagnostics.FindCloneOf, which used to hardcode the bare name and so
        /// matched nothing.
        /// </summary>
        internal const string ClonePrefix = "MP ";

        /// <summary>
        /// Renderers are switched off rather than destroyed, even on a dedicated server.
        /// A disabled renderer costs nothing per frame, and some of them (the net's
        /// SkinnedMeshRenderer) are the target of a RequireComponent, so tearing them out
        /// is a good way to earn a stream of engine warnings for no real saving.
        /// </summary>
        private static void PrepareServerVisuals(GameObject clone)
        {
            foreach (Renderer r in clone.GetComponentsInChildren<Renderer>(true))
            {
                if (r != null) r.enabled = false;
            }

            foreach (Light l in clone.GetComponentsInChildren<Light>(true))
            {
                if (l != null) l.enabled = false;
            }

            // Colliders are deliberately left exactly as the template had them.
            //
            // This used to force every collider on, reasoning that a sheet you skate on
            // wants all of its collision. That was wrong. The arena ships collision
            // volumes that are switched off, and CompAdjust switches more of them off
            // when it resizes the rink - it patches 25 methods and captures an arena
            // baseline precisely to manage this. Turning the lot back on gave the clone
            // board collision the real rink does not have, layered over the collision it
            // does. A stick is driven by a PID controller with a gain of 500; it does not
            // gently resolve being wedged inside two overlapping colliders, it launches.
        }

        /// <summary>
        /// The rink template carries its own ice collider, so normally there is nothing
        /// to do. Rather than guess from layers or component names, drop a ray onto the
        /// sheet and see whether anything is actually there to stand on - a sheet with no
        /// surface drops every player who steps onto it straight through, and that is not
        /// a thing to find out in play.
        /// </summary>
        private static void EnsureIceSurface(Transform parent, RinkSheetSlot slot)
        {
            if (HasSurfaceUnder(parent, slot)) return;

            int iceLayer = LayerMask.NameToLayer("Ice");
            var iceObj = new GameObject("IceSurface_" + slot.Id);
            iceObj.transform.SetParent(parent, false);
            iceObj.transform.position = parent.position + new Vector3(0f, IceSurfaceY, 0f);
            iceObj.transform.rotation = Quaternion.identity;

            var box = iceObj.AddComponent<BoxCollider>();
            box.size = new Vector3(45f, 0.2f, 91.5f);
            // Top face flush with the ice plane - centring the box on it would raise the
            // surface by half the box and float everything 10 cm above the sheet.
            box.center = new Vector3(0f, -box.size.y * 0.5f, 0f);
            if (iceLayer >= 0) iceObj.layer = iceLayer;

            Debug.LogWarning($"[MaxPractice] Sheet {slot.Label}: the rink clone came up with nothing to skate on, added a fallback ice surface.");
        }

        private static readonly RaycastHit[] _surfaceHits = new RaycastHit[16];

        private static bool HasSurfaceUnder(Transform parent, RinkSheetSlot slot)
        {
            try
            {
                // Colliders instantiated this frame aren't in the physics scene until the
                // transforms are pushed across.
                Physics.SyncTransforms();

                Vector3 from = parent.position + new Vector3(0f, IceSurfaceY + 5f, 0f);
                int count = Physics.RaycastNonAlloc(
                    from, Vector3.down, _surfaceHits, 10f, ~0, QueryTriggerInteraction.Ignore);

                for (int i = 0; i < count; i++)
                {
                    Transform hit = _surfaceHits[i].collider != null ? _surfaceHits[i].collider.transform : null;
                    // Only geometry belonging to this sheet counts - a player standing in
                    // the way, or the rink above a stacked sheet, proves nothing.
                    if (hit != null && hit.IsChildOf(parent)) return true;
                }
            }
            catch (Exception ex)
            {
                // Assume the template is fine rather than stacking a second surface on top
                // of a working one because a raycast misbehaved.
                Debug.LogWarning($"[MaxPractice] Sheet {slot.Label}: ice surface probe failed ({ex.Message}), assuming the clone is intact.");
                return true;
            }

            return false;
        }

        // ------------------------------------------------------------------
        // Client geometry
        // ------------------------------------------------------------------

        /// <summary>
        /// Build the renderer-only copy. Registers static-batched geometry with
        /// <see cref="RinkCloneVisuals"/>, which draws it for real.
        /// </summary>
        public static GameObject BuildClient(RinkSheetSlot slot)
        {
            Transform levelRoot = FindLevelRoot();
            if (levelRoot == null || slot == null) return null;

            CacheIceSurfaceY(levelRoot);

            var root = new GameObject("MaxPractice_Sheet_Client_" + slot.Id);
            root.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(root);
            PlaceSheetRoot(root.transform, levelRoot, slot);

            int cloned = 0;
            List<Transform> templates = ResolveTemplates(levelRoot);

            for (int i = 0; i < templates.Count; i++)
            {
                Transform src = templates[i];
                if (src == null) continue;

                GameObject clone = UnityEngine.Object.Instantiate(src.gameObject, root.transform);
                clone.name = src.name.Replace(' ', '_') + "_" + slot.Id;
                clone.SetActive(true);
                PlaceTemplate(clone.transform, src, levelRoot);

                // Same reasoning as the server clone: no game script belongs on scenery.
                // GoalController is the one that bit us, and it is client-side logic.
                StripAllScripts(clone);

                // Cloth BEFORE colliders, and for the same reason StripGameplayComponents
                // does it on the server clone: Cloth is a built-in component, not a script, so
                // StripAllScripts leaves it behind. This path was destroying every collider
                // underneath a cloth solver that referenced them and then leaving the solver
                // running - a per-frame simulation on each clone net that nobody wanted, and
                // the source of the NullReferenceExceptions out of Cloth.get_sphereColliders
                // noted further up this file. Ordering matters: strip the solver first so it
                // never observes a half-destroyed collider set.
                foreach (Cloth cloth in clone.GetComponentsInChildren<Cloth>(true)) SafeDestroy(cloth);

                foreach (Collider c in clone.GetComponentsInChildren<Collider>(true)) SafeDestroy(c);

                RinkCloneVisuals.RegisterClone(slot, clone, src.gameObject);
                cloned++;
            }

            if (cloned == 0)
            {
                UnityEngine.Object.Destroy(root);
                return null;
            }

            root.SetActive(true);


            // Only once the template pairing above is done - it matches by path.
            MarkCloneNames(root);
            RinkCloneVisuals.AddFillLights(slot, root.transform);
            RinkCloneAudio.Apply(slot, root.transform);

            // Renderer census: the one number that says whether a sheet is actually
            // drawing anything. "Not rendering" has been reported twice now with no way
            // to tell a build failure from a sheet that built and drew nothing.
            int total = 0, drawingSelf = 0, handedToProxy = 0, noMaterial = 0;
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                total++;
                if (r.sharedMaterial == null) noMaterial++;
                else if (r.enabled) drawingSelf++;
                else handedToProxy++;
            }

            ConfigManager.Log($"Sheet {slot.Label}: client visuals built at {slot.Origin} — " +
                              $"{total} renderer(s): {drawingSelf} drawing themselves, {handedToProxy} off " +
                              $"(proxy entries now {RinkCloneVisuals.DrawCount}), {noMaterial} with no material." +
                              (drawingSelf == 0 && RinkCloneVisuals.DrawCount == 0 ? "   <-- NOTHING WILL DRAW" : ""));
            LogPlacement(root, templates, levelRoot, slot, "client");
            return root;
        }

        // ------------------------------------------------------------------

        public static void DestroyRoot(GameObject root)
        {
            if (root == null) return;
            try { UnityEngine.Object.Destroy(root); }
            catch { }
        }

        private static void SafeDestroy(Component c)
        {
            if (c == null) return;
            try { UnityEngine.Object.Destroy(c); }
            catch { }
        }

        internal static Transform DiagnosticLevelRoot() => FindLevelRoot();

        internal static List<Transform> DiagnosticTemplates(Transform levelRoot)
        {
            return levelRoot != null ? ResolveTemplates(levelRoot) : new List<Transform>();
        }

        internal static bool IsHeadless()
        {
            return Application.isBatchMode &&
                   SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
        }
    }

}
