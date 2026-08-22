// ConeAsset.cs - Procedural traffic-cone asset that replaces the frozen puck used by /cones.
//
// The mod ships as a code-only plugin (no AssetBundle), so the cone is built from
// scratch at runtime: a round base disc plus a tapered body with a white
// reflective band, split into two submeshes so the band gets its own material.
//
// The cone is attached as a child of the handle puck's NetworkObject rather than
// replacing it. The puck stays the network anchor (frozen, kinematic) and we hide
// its renderers and switch off its colliders - so nothing about spawning,
// tracking, or clearing cones has to change, but the thing you collide with is
// the cone's own shape rather than a puck-sized disc at its centre.
//
// Two details that are easy to get wrong:
//
//   * The visual sits on the Default layer, NOT the game's "Puck" layer. Puck
//     rendering includes a see-through silhouette pass that filters on that layer,
//     and a cone on it shows through the boards and the net.
//   * The colliders DO sit on the puck's layer, on their own child objects, so
//     they keep colliding with pucks. They're paired off against every player body
//     and stick with Physics.IgnoreCollision, so cones stop pucks without catching
//     your stick mid-drill.
//
// Physics is server-authoritative, so colliders are only ever built on the server
// (including a headless one, which builds them without any visual).

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxPractice
{
    public static class ConeAsset
    {
        public const string ConeObjectName = "MaxPractice_Cone";
        private const string BodyColliderName = "MaxPractice_ConeBody";
        private const string BaseColliderName = "MaxPractice_ConeBase";

        // Ordinary scene-geometry layer. Anything but the game's "Puck" layer,
        // which would drag the cone into the puck silhouette pass.
        private const int VisualLayer = 0;

        // Dimensions in metres: a stubby training cone, 36 cm tall on a 32 cm round
        // base - barely taller than it is wide, so it reads as something you skate
        // around rather than a highway cone. Tweak these to resize; the colliders
        // are generated from the same numbers, so the two can't drift apart.
        //
        // BaseHeight must stay above a puck's 2.5 cm thickness. The cone body starts
        // where the base ends, so a shorter base would let pucks slide underneath it
        // and hit nothing.
        private const float BaseRadius = 0.16f;
        private const float BaseHeight = 0.035f;
        private const float ConeHeight = 0.325f;
        private const float ConeBaseRadius = 0.125f;
        private const float ConeTipRadius = 0.020f;
        private const int RadialSegments = 20;

        // Normalised heights of the white band on the cone body.
        private const float BandBottom = 0.42f;
        private const float BandTop = 0.60f;

        private static readonly Color BodyColor = new Color(0.95f, 0.31f, 0.04f);
        private static readonly Color BandColor = new Color(0.94f, 0.94f, 0.91f);

        private static Mesh _mesh;
        private static Mesh _bodyHull;
        private static Mesh _baseHull;
        private static Material _bodyMaterial;
        private static Material _bandMaterial;

        /// <summary>
        /// Dedicated servers run headless - building meshes and materials there is
        /// pointless (and Shader.Find has nothing to return). The server still
        /// builds the cone's colliders and announces it to clients.
        /// </summary>
        public static bool HasGraphics =>
            !Application.isBatchMode &&
            SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        /// <summary>
        /// Turn a puck into a cone. Idempotent - calling it on a puck that is
        /// already a cone just re-asserts the swap.
        /// </summary>
        /// <param name="withCollision">
        /// True on the server, which owns physics: swaps the puck's colliders for
        /// the cone's own. False on clients, which get the visual only.
        /// </param>
        public static bool Apply(Puck puck, bool withCollision)
        {
            if (puck == null || puck.gameObject == null) return false;
            if (!HasGraphics && !withCollision) return false;

            try
            {
                var existing = puck.GetComponentInChildren<ConeVisual>(true);
                if (existing != null)
                {
                    existing.Reassert();
                    return true;
                }

                var go = new GameObject(ConeObjectName);
                go.layer = VisualLayer;
                go.transform.SetParent(puck.transform, false);
                // Pucks get resized by CompetitiveAdjustments; a cone should not.
                MaxPractice.MiniNetAsset.NeutralisePuckScale(go.transform, puck);

                if (HasGraphics)
                {
                    var mesh = GetMesh();
                    var body = GetBodyMaterial(puck);
                    var band = GetBandMaterial(puck);
                    if (mesh == null || body == null || band == null)
                    {
                        UnityEngine.Object.Destroy(go);
                        return false;
                    }

                    var filter = go.AddComponent<MeshFilter>();
                    filter.sharedMesh = mesh;

                    var renderer = go.AddComponent<MeshRenderer>();
                    renderer.sharedMaterials = new[] { body, band };
                    renderer.shadowCastingMode = ShadowCastingMode.On;
                    renderer.receiveShadows = true;
                }

                go.AddComponent<ConeVisual>().Init(puck, withCollision);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaxPractice] Failed to apply cone: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Strip the cone off a puck, restoring its normal mesh and colliders.
        /// </summary>
        public static void Remove(Puck puck)
        {
            if (puck == null) return;

            try
            {
                var visual = puck.GetComponentInChildren<ConeVisual>(true);
                if (visual != null)
                    UnityEngine.Object.Destroy(visual.gameObject);
            }
            catch { }
        }

        /// <summary>
        /// True when the component belongs to a cone rather than the puck under it.
        /// Puck visual repair must skip these, otherwise it caches the cone mesh as
        /// the "known good" puck mesh and stamps it onto ordinary pucks - and the
        /// collider sweep must skip them so a cone doesn't disable itself.
        /// </summary>
        public static bool IsConePart(Component component)
        {
            if (component == null) return false;

            var t = component.transform;
            while (t != null)
            {
                if (t.GetComponent<ConeVisual>() != null) return true;
                t = t.parent;
            }
            return false;
        }

        /// <summary>
        /// Destroy every live cone (restoring the pucks underneath) and drop the
        /// cached meshes/materials. Called when the plugin is disabled.
        /// </summary>
        public static void Dispose()
        {
            try
            {
                var visuals = UnityEngine.Object.FindObjectsByType<ConeVisual>(FindObjectsSortMode.None);
                foreach (var visual in visuals)
                {
                    if (visual != null)
                        UnityEngine.Object.Destroy(visual.gameObject);
                }
            }
            catch { }

            DestroyCached(ref _bodyMaterial);
            DestroyCached(ref _bandMaterial);
            DestroyCached(ref _mesh);
            DestroyCached(ref _bodyHull);
            DestroyCached(ref _baseHull);
        }

        private static void DestroyCached<T>(ref T asset) where T : UnityEngine.Object
        {
            if (asset != null)
            {
                try { UnityEngine.Object.Destroy(asset); } catch { }
            }
            asset = null;
        }

        // ------------------------------------------------------------------
        // Materials
        // ------------------------------------------------------------------

        private static Material GetBodyMaterial(Puck sample)
        {
            if (_bodyMaterial == null)
                _bodyMaterial = BuildMaterial(sample, BodyColor, "MaxPractice_ConeBody", 0.18f);
            return _bodyMaterial;
        }

        private static Material GetBandMaterial(Puck sample)
        {
            if (_bandMaterial == null)
                _bandMaterial = BuildMaterial(sample, BandColor, "MaxPractice_ConeBand", 0.45f);
            return _bandMaterial;
        }

        private static Material BuildMaterial(Puck sample, Color color, string name, float smoothness)
        {
            var shader = ResolveShader(sample);
            if (shader == null)
            {
                Debug.LogWarning("[MaxPractice] No usable shader found for the cone material");
                return null;
            }

            var material = new Material(shader)
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave
            };

            // Set both the URP and the built-in colour properties so the material
            // is correct whichever shader we landed on.
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", smoothness);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
            if (material.HasProperty("_EmissionColor")) material.SetColor("_EmissionColor", Color.black);

            return material;
        }

        private static Shader ResolveShader(Puck sample)
        {
            // The game renders with URP, so its Lit shader is guaranteed to be in
            // the build. Fall back through the other stock shaders, then to
            // whatever the puck itself uses, so a pipeline change can't leave the
            // cone unrenderable.
            string[] preferred =
            {
                "Universal Render Pipeline/Lit",
                "Universal Render Pipeline/Simple Lit",
                "Standard"
            };

            foreach (var shaderName in preferred)
            {
                var shader = Shader.Find(shaderName);
                if (shader != null) return shader;
            }

            try
            {
                if (sample != null)
                {
                    foreach (var mr in sample.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        if (mr == null || IsConePart(mr)) continue;
                        var mat = mr.sharedMaterial;
                        if (mat != null && mat.shader != null) return mat.shader;
                    }
                }
            }
            catch { }

            return Shader.Find("Unlit/Color");
        }

        // ------------------------------------------------------------------
        // Colliders
        // ------------------------------------------------------------------

        /// <summary>
        /// Give the cone its own collision: one convex hull for the base disc and
        /// one for the tapered body, each on its own child object so both can be a
        /// MeshCollider (a GameObject only gets one).
        ///
        /// Two hulls rather than one over the whole cone: the base is wider than the
        /// body sitting on it, and hulling them together fills that step with an
        /// invisible slope for pucks to bounce off.
        ///
        /// The children take the puck's layer - that's what makes pucks collide with
        /// them - while the visual parent stays on the Default layer.
        /// </summary>
        public static void BuildColliders(GameObject coneRoot, int physicsLayer, List<Collider> into)
        {
            if (coneRoot == null) return;

            AddHullChild(coneRoot, BaseColliderName, GetBaseHull(), physicsLayer, into);
            AddHullChild(coneRoot, BodyColliderName, GetBodyHull(), physicsLayer, into);
        }

        private static void AddHullChild(GameObject parent, string name, Mesh hull, int layer, List<Collider> into)
        {
            if (hull == null) return;

            var child = parent.transform.Find(name);
            if (child == null)
            {
                var go = new GameObject(name);
                go.transform.SetParent(parent.transform, false);
                child = go.transform;
            }

            child.gameObject.layer = layer;

            var mc = child.GetComponent<MeshCollider>();
            if (mc == null)
            {
                mc = child.gameObject.AddComponent<MeshCollider>();
                mc.convex = true;
                mc.sharedMesh = hull;
            }

            if (into != null && !into.Contains(mc)) into.Add(mc);
        }

        // ------------------------------------------------------------------
        // Meshes
        // ------------------------------------------------------------------

        public static Mesh GetMesh()
        {
            if (_mesh == null) _mesh = Keep(BuildMesh());
            return _mesh;
        }

        /// <summary>Convex hull of the tapered body, from the top of the base up.</summary>
        public static Mesh GetBodyHull()
        {
            if (_bodyHull == null)
                _bodyHull = Keep(BuildFrustum("MaxPractice_ConeBodyHull",
                    BaseHeight, ConeBaseRadius, BaseHeight + ConeHeight, ConeTipRadius));
            return _bodyHull;
        }

        /// <summary>Convex hull of the round base disc.</summary>
        public static Mesh GetBaseHull()
        {
            if (_baseHull == null)
                _baseHull = Keep(BuildFrustum("MaxPractice_ConeBaseHull",
                    0f, BaseRadius, BaseHeight, BaseRadius));
            return _baseHull;
        }

        private static Mesh Keep(Mesh mesh)
        {
            if (mesh != null) mesh.hideFlags = HideFlags.HideAndDontSave;
            return mesh;
        }

        /// <summary>
        /// Build the cone: round base disc + tapered body + top cap, with the band
        /// triangles broken out into submesh 1 so they can take the white material.
        /// Normals are written directly (no RecalculateNormals) so the curved
        /// surfaces shade smoothly while the disc keeps hard edges.
        /// </summary>
        private static Mesh BuildMesh()
        {
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var bodyTris = new List<int>();
            var bandTris = new List<int>();

            AddDisc(verts, normals, uvs, bodyTris, BaseRadius, BaseHeight);

            // Ring heights: base, band bottom, band top, tip.
            float[] rings = { 0f, BandBottom, BandTop, 1f };
            var ringStart = new int[rings.Length];

            // Outward normal in the (radial, vertical) plane - perpendicular to the
            // cone's slant, so it tilts upward as the body tapers.
            var slant = new Vector2(ConeHeight, ConeBaseRadius - ConeTipRadius).normalized;

            for (int r = 0; r < rings.Length; r++)
            {
                float t = rings[r];
                float y = BaseHeight + t * ConeHeight;
                float radius = Mathf.Lerp(ConeBaseRadius, ConeTipRadius, t);
                ringStart[r] = verts.Count;

                // <= segments: the seam vertex is duplicated so UVs wrap cleanly.
                for (int s = 0; s <= RadialSegments; s++)
                {
                    float angle = (s / (float)RadialSegments) * Mathf.PI * 2f;
                    float cos = Mathf.Cos(angle);
                    float sin = Mathf.Sin(angle);

                    verts.Add(new Vector3(cos * radius, y, sin * radius));
                    normals.Add(new Vector3(cos * slant.x, slant.y, sin * slant.x));
                    uvs.Add(new Vector2(s / (float)RadialSegments, t));
                }
            }

            for (int r = 0; r < rings.Length - 1; r++)
            {
                var target = r == 1 ? bandTris : bodyTris;
                int lower = ringStart[r];
                int upper = ringStart[r + 1];

                for (int s = 0; s < RadialSegments; s++)
                {
                    int a0 = lower + s, a1 = lower + s + 1;
                    int b0 = upper + s, b1 = upper + s + 1;

                    target.Add(a0); target.Add(b0); target.Add(b1);
                    target.Add(a0); target.Add(b1); target.Add(a1);
                }
            }

            AddCap(verts, normals, uvs, bodyTris,
                BaseHeight + ConeHeight, ConeTipRadius, true);

            var mesh = new Mesh { name = "MaxPractice_ConeMesh" };
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(bodyTris, 0);
            mesh.SetTriangles(bandTris, 1);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>The round base the cone stands on: a short closed cylinder.</summary>
        private static void AddDisc(List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs,
                                    List<int> tris, float radius, float height)
        {
            int lower = verts.Count;
            for (int s = 0; s <= RadialSegments; s++)
                AddRingVertex(verts, normals, uvs, s, radius, 0f, 0f);

            int upper = verts.Count;
            for (int s = 0; s <= RadialSegments; s++)
                AddRingVertex(verts, normals, uvs, s, radius, height, 1f);

            for (int s = 0; s < RadialSegments; s++)
            {
                int a0 = lower + s, a1 = lower + s + 1;
                int b0 = upper + s, b1 = upper + s + 1;

                tris.Add(a0); tris.Add(b0); tris.Add(b1);
                tris.Add(a0); tris.Add(b1); tris.Add(a1);
            }

            AddCap(verts, normals, uvs, tris, height, radius, true);
            AddCap(verts, normals, uvs, tris, 0f, radius, false);
        }

        /// <summary>A side-wall ring vertex with an outward radial normal.</summary>
        private static void AddRingVertex(List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs,
                                          int segment, float radius, float y, float v)
        {
            float angle = (segment / (float)RadialSegments) * Mathf.PI * 2f;
            float cos = Mathf.Cos(angle);
            float sin = Mathf.Sin(angle);

            verts.Add(new Vector3(cos * radius, y, sin * radius));
            normals.Add(new Vector3(cos, 0f, sin));
            uvs.Add(new Vector2(segment / (float)RadialSegments, v));
        }

        /// <summary>
        /// A flat disc closing off the top or bottom of a ring. Winding flips with
        /// the facing: increasing angle reads clockwise from below, anticlockwise
        /// from above, and Unity treats clockwise-from-the-front as front-facing.
        /// </summary>
        private static void AddCap(List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs,
                                   List<int> tris, float y, float radius, bool facingUp)
        {
            var normal = facingUp ? Vector3.up : Vector3.down;

            int center = verts.Count;
            verts.Add(new Vector3(0f, y, 0f));
            normals.Add(normal);
            uvs.Add(new Vector2(0.5f, 0.5f));

            int start = verts.Count;
            for (int s = 0; s <= RadialSegments; s++)
            {
                float angle = (s / (float)RadialSegments) * Mathf.PI * 2f;
                float cos = Mathf.Cos(angle);
                float sin = Mathf.Sin(angle);

                verts.Add(new Vector3(cos * radius, y, sin * radius));
                normals.Add(normal);
                uvs.Add(new Vector2(0.5f + cos * 0.5f, 0.5f + sin * 0.5f));
            }

            for (int s = 0; s < RadialSegments; s++)
            {
                tris.Add(center);
                if (facingUp)
                {
                    tris.Add(start + s + 1);
                    tris.Add(start + s);
                }
                else
                {
                    tris.Add(start + s);
                    tris.Add(start + s + 1);
                }
            }
        }

        /// <summary>
        /// A closed truncated cone for collision. Equal radii give a cylinder, so
        /// this covers both the base disc and the tapered body. Convex by
        /// construction, so the hull PhysX cooks matches what you can see.
        /// </summary>
        private static Mesh BuildFrustum(string name, float y0, float r0, float y1, float r1)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();

            for (int s = 0; s < RadialSegments; s++)
            {
                float angle = (s / (float)RadialSegments) * Mathf.PI * 2f;
                float cos = Mathf.Cos(angle);
                float sin = Mathf.Sin(angle);

                verts.Add(new Vector3(cos * r0, y0, sin * r0));
                verts.Add(new Vector3(cos * r1, y1, sin * r1));
            }

            for (int s = 0; s < RadialSegments; s++)
            {
                int a0 = s * 2;
                int a1 = a0 + 1;
                int b0 = ((s + 1) % RadialSegments) * 2;
                int b1 = b0 + 1;

                tris.Add(a0); tris.Add(a1); tris.Add(b1);
                tris.Add(a0); tris.Add(b1); tris.Add(b0);
            }

            int bottomCenter = verts.Count;
            verts.Add(new Vector3(0f, y0, 0f));
            int topCenter = verts.Count;
            verts.Add(new Vector3(0f, y1, 0f));

            for (int s = 0; s < RadialSegments; s++)
            {
                int a0 = s * 2;
                int b0 = ((s + 1) % RadialSegments) * 2;

                tris.Add(bottomCenter); tris.Add(a0); tris.Add(b0);
                tris.Add(topCenter); tris.Add(b0 + 1); tris.Add(a0 + 1);
            }

            var mesh = new Mesh { name = name };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }

    /// <summary>
    /// Keeps a cone glued upright on top of its puck, keeps the puck's own
    /// renderers hidden, and (on the server) keeps the puck's own colliders off so
    /// the cone's shape is what pucks hit. Destroying this component - or the puck
    /// it rides on - puts the puck back exactly as it was.
    /// </summary>
    public class ConeVisual : MonoBehaviour
    {
        private const float ReassertInterval = 2f;
        private const float DefaultGroundOffset = -0.0125f;

        // A puck is ~2.5 cm thick. Anything outside this range means we measured
        // something that isn't the puck body, so fall back to the nominal offset.
        private const float MinGroundOffset = -0.15f;

        private Puck _puck;
        private Transform _puckTransform;
        private float _groundOffset = DefaultGroundOffset;
        private float _nextReassert;
        private bool _ownsCollision;

        private readonly List<Renderer> _hiddenRenderers = new List<Renderer>();
        private readonly List<Collider> _disabledColliders = new List<Collider>();
        private readonly List<Collider> _coneColliders = new List<Collider>();

        // Colliders we've already paired off against, so the periodic refresh only
        // pays for players and sticks that turned up since last time.
        private readonly HashSet<Collider> _ignoredAgainst = new HashSet<Collider>();

        public void Init(Puck puck, bool withCollision)
        {
            // A brand-new cone wants a live sweep, not whatever the last prop's tick
            // produced - see PlayerColliderCache.Invalidate.
            PlayerColliderCache.Invalidate();
            _puck = puck;
            _ownsCollision = withCollision;
            _puckTransform = puck != null ? puck.transform : null;
            if (_puckTransform == null) return;

            // Cancel out any scaling on the puck so the cone is true size.
            var parentScale = _puckTransform.lossyScale;
            transform.localScale = new Vector3(
                Mathf.Approximately(parentScale.x, 0f) ? 1f : 1f / parentScale.x,
                Mathf.Approximately(parentScale.y, 0f) ? 1f : 1f / parentScale.y,
                Mathf.Approximately(parentScale.z, 0f) ? 1f : 1f / parentScale.z);

            // The cone is modelled with its base at y = 0, so anchor it to the
            // bottom of the puck - that's where the ice is, whatever height the
            // puck settled at. Measure only live renderers: an inactive one reports
            // stale or origin-centred bounds and would drag the cone underground.
            float bottom = float.MaxValue;
            foreach (var r in puck.GetComponentsInChildren<Renderer>(false))
            {
                if (r == null || !r.enabled || ConeAsset.IsConePart(r)) continue;
                bottom = Mathf.Min(bottom, r.bounds.min.y);
            }

            if (bottom < float.MaxValue)
            {
                float offset = bottom - _puckTransform.position.y;
                if (offset >= MinGroundOffset && offset <= 0f)
                    _groundOffset = offset;
            }

            // Build collision before the first snap so the hulls are cooked in
            // their final position. They go on the puck's own layer - that's what
            // makes pucks collide with them.
            if (_ownsCollision)
                ConeAsset.BuildColliders(gameObject, puck.gameObject.layer, _coneColliders);

            Reassert();
            SnapToPuck(true);
        }

        private void LateUpdate()
        {
            if (_puckTransform == null)
            {
                UnityEngine.Object.Destroy(gameObject);
                return;
            }

            SnapToPuck(false);

            if (Time.unscaledTime >= _nextReassert)
            {
                _nextReassert = Time.unscaledTime + ReassertInterval;
                Reassert();
            }
        }

        private void SnapToPuck(bool force)
        {
            var p = _puckTransform.position;
            var target = new Vector3(p.x, p.y + _groundOffset, p.z);

            // Only write when it actually moved. The cone carries colliders, and
            // touching the transform makes PhysX re-cook the puck rigidbody's
            // compound collider - every frame, for every cone, if we're careless.
            if (force || (transform.position - target).sqrMagnitude > 1e-8f)
                transform.position = target;

            if (force || Quaternion.Angle(transform.rotation, Quaternion.identity) > 0.01f)
                transform.rotation = Quaternion.identity;
        }

        /// <summary>
        /// Re-hide the puck's renderers, re-disable its colliders, and pair the
        /// cone off against any player or stick that turned up since last time.
        /// Runs on an interval because the game re-enables puck visuals on some
        /// client join/leave paths (and toggles the outline/silhouette renderers
        /// from settings), MaxPractice has its own puck-visual repair pass, and
        /// sticks are destroyed and rebuilt whenever someone changes position.
        /// </summary>
        public void Reassert()
        {
            if (_puck == null) return;

            for (int i = _hiddenRenderers.Count - 1; i >= 0; i--)
            {
                if (_hiddenRenderers[i] == null) _hiddenRenderers.RemoveAt(i);
            }

            if (ConeAsset.HasGraphics)
            {
                foreach (var r in _puck.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || !r.enabled || ConeAsset.IsConePart(r)) continue;

                    r.enabled = false;
                    if (!_hiddenRenderers.Contains(r)) _hiddenRenderers.Add(r);
                }
            }

            if (!_ownsCollision) return;

            for (int i = _disabledColliders.Count - 1; i >= 0; i--)
            {
                if (_disabledColliders[i] == null) _disabledColliders.RemoveAt(i);
            }

            // The puck's own disc collider would sit invisibly inside the cone and
            // catch anything that got past the cone's shape.
            foreach (var c in _puck.GetComponentsInChildren<Collider>(true))
            {
                if (c == null || !c.enabled || ConeAsset.IsConePart(c)) continue;

                c.enabled = false;
                if (!_disabledColliders.Contains(c)) _disabledColliders.Add(c);
            }

            RefreshCollisionIgnores();
        }

        /// <summary>
        /// A cone should stop a puck, not a player or a stick. Skating through one
        /// is the behaviour handle pucks always had, and a stick catching on a cone
        /// would wreck the drill the cones are there for.
        /// </summary>
        private void RefreshCollisionIgnores()
        {
            if (_coneColliders.Count == 0) return;

            try
            {
                _ignoredAgainst.RemoveWhere(c => c == null);

                for (int i = _coneColliders.Count - 1; i >= 0; i--)
                {
                    if (_coneColliders[i] == null) _coneColliders.RemoveAt(i);
                }

                // One shared sweep for every prop rather than one per cone - see
                // PlayerColliderCache. With a full minefield out this walked every player's
                // rigged hierarchy fifteen times over in a single frame.
                IgnoreAgainst(PlayerColliderCache.Get());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaxPractice] Cone collision-ignore refresh failed: {ex.Message}");
            }
        }

        private void IgnoreAgainst(IReadOnlyList<Collider> others)
        {
            if (others == null) return;

            foreach (var other in others)
            {
                // Physics.IgnoreCollision errors on a disabled collider, and a
                // disabled one can't collide anyway - it gets picked up on a later
                // pass if it comes back.
                if (other == null || !other.enabled) continue;
                if (!_ignoredAgainst.Add(other)) continue;

                foreach (var mine in _coneColliders)
                {
                    if (mine != null && mine.enabled)
                        Physics.IgnoreCollision(mine, other, true);
                }
            }
        }

        private void OnDestroy()
        {
            foreach (var r in _hiddenRenderers)
            {
                if (r != null) r.enabled = true;
            }
            _hiddenRenderers.Clear();

            foreach (var c in _disabledColliders)
            {
                if (c != null) c.enabled = true;
            }
            _disabledColliders.Clear();

            _coneColliders.Clear();
            _ignoredAgainst.Clear();
        }
    }
}
