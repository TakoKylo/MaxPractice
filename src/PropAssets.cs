// PropAssets.cs - The puck shooter and the mini net.
//
// Both are built the same way the cone is: a procedural mesh parented to a frozen
// handle puck, which stays the network anchor. That buys position replication,
// tracking and cleanup for free - destroy the puck and the prop goes with it.
//
// Shared plumbing lives in PropStyle so the shooter, the net and the cone all pull
// their shader and their primitives from one place.
//
// NOTE: the bodies below were recovered from a compiled build after this file was
// damaged by an over-greedy edit. They are functionally what they were, but the
// original inline commentary in PropStyle, ShooterAsset and the upper half of
// MiniNetAsset did not survive the round trip.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxPractice
{
    public enum PropKind : byte
    {
        Cone,
        Shooter,
        MiniNet
    }
    public static class PropStyle
    {
        public sealed class MeshBuilder
        {
            public readonly List<Vector3> Verts = new List<Vector3>();

            public readonly List<Vector3> Normals = new List<Vector3>();

            public readonly List<Vector2> Uvs = new List<Vector2>();

            public readonly List<int> Tris = new List<int>();

            public void Quad(Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3, Vector3 n)
            {
                int count = Verts.Count;
                Verts.Add(v0);
                Verts.Add(v1);
                Verts.Add(v2);
                Verts.Add(v3);
                for (int i = 0; i < 4; i++)
                {
                    Normals.Add(n);
                }
                Uvs.Add(new Vector2(0f, 0f));
                Uvs.Add(new Vector2(0f, 1f));
                Uvs.Add(new Vector2(1f, 1f));
                Uvs.Add(new Vector2(1f, 0f));
                Tris.Add(count);
                Tris.Add(count + 1);
                Tris.Add(count + 2);
                Tris.Add(count);
                Tris.Add(count + 2);
                Tris.Add(count + 3);
            }

            public void Box(Vector3 min, Vector3 max)
            {
                float x = min.x;
                float y = min.y;
                float z = min.z;
                float x2 = max.x;
                float y2 = max.y;
                float z2 = max.z;
                Quad(new Vector3(x, y2, z), new Vector3(x, y2, z2), new Vector3(x2, y2, z2), new Vector3(x2, y2, z), Vector3.up);
                Quad(new Vector3(x2, y, z), new Vector3(x2, y, z2), new Vector3(x, y, z2), new Vector3(x, y, z), Vector3.down);
                Quad(new Vector3(x2, y, z), new Vector3(x2, y2, z), new Vector3(x2, y2, z2), new Vector3(x2, y, z2), Vector3.right);
                Quad(new Vector3(x, y, z2), new Vector3(x, y2, z2), new Vector3(x, y2, z), new Vector3(x, y, z), Vector3.left);
                Quad(new Vector3(x2, y, z2), new Vector3(x2, y2, z2), new Vector3(x, y2, z2), new Vector3(x, y, z2), Vector3.forward);
                Quad(new Vector3(x, y, z), new Vector3(x, y2, z), new Vector3(x2, y2, z), new Vector3(x2, y, z), Vector3.back);
            }

            public void Tube(Vector3 a, Vector3 b, float radius, int segments = 12)
            {
                Vector3 vector = b - a;
                float magnitude = vector.magnitude;
                if (!(magnitude < 1E-06f))
                {
                    vector /= magnitude;
                    Vector3 vector2 = Vector3.Normalize(Vector3.Cross((Mathf.Abs(Vector3.Dot(vector, Vector3.up)) > 0.95f) ? Vector3.forward : Vector3.up, vector));
                    Vector3 vector3 = Vector3.Cross(vector, vector2);
                    int count = Verts.Count;
                    for (int i = 0; i < segments; i++)
                    {
                        float f = (float)i / (float)segments * Mathf.PI * 2f;
                        Vector3 vector4 = vector2 * Mathf.Cos(f) + vector3 * Mathf.Sin(f);
                        Verts.Add(a + vector4 * radius);
                        Normals.Add(vector4);
                        Uvs.Add(new Vector2((float)i / (float)segments, 0f));
                        Verts.Add(b + vector4 * radius);
                        Normals.Add(vector4);
                        Uvs.Add(new Vector2((float)i / (float)segments, 1f));
                    }
                    for (int j = 0; j < segments; j++)
                    {
                        int num = count + j * 2;
                        int item = num + 1;
                        int num2 = count + (j + 1) % segments * 2;
                        int item2 = num2 + 1;
                        // Barrel, wound to match the radial normals written above. Tube's
                        // basis is sy = Cross(axis, sx), so Cross(sx,sy) = +axis, the
                        // opposite handedness to ConeAsset's (cos, y, sin) rings - which is
                        // why the loop that looks identical over there is already outward
                        // and this one was facing in. Same rule as the caps below: the front
                        // normal of (v0,v1,v2) is Cross(v1-v0, v2-v0).
                        Tris.Add(num);
                        Tris.Add(item2);
                        Tris.Add(item);
                        Tris.Add(num);
                        Tris.Add(num2);
                        Tris.Add(item2);
                    }
                    AddCap(a, -vector, vector2, vector3, radius, segments);
                    AddCap(b, vector, vector2, vector3, radius, segments);
                }
            }

            private void AddCap(Vector3 centre, Vector3 normal, Vector3 sx, Vector3 sy, float radius, int segments)
            {
                int count = Verts.Count;
                Verts.Add(centre);
                Normals.Add(normal);
                Uvs.Add(new Vector2(0.5f, 0.5f));
                int count2 = Verts.Count;
                for (int i = 0; i <= segments; i++)
                {
                    float f = (float)i / (float)segments * Mathf.PI * 2f;
                    Vector3 vector = sx * Mathf.Cos(f) + sy * Mathf.Sin(f);
                    Verts.Add(centre + vector * radius);
                    Normals.Add(normal);
                    Uvs.Add(new Vector2(0.5f + Mathf.Cos(f) * 0.5f, 0.5f + Mathf.Sin(f) * 0.5f));
                }
                // Winding, the way Unity actually reads it: the front normal of (v0,v1,v2)
                // is Cross(v1-v0, v2-v0), so the forward fan (C, P_j, P_j+1) faces
                // +Cross(sx,sy) and the reversed fan faces -Cross(sx,sy). Both branches
                // used to emit the opposite of that, which left every Tube's end discs
                // back-faced - invisible under normal culling, and lit against their own
                // written normals. ConeAsset.AddCap has always had this the right way round.
                bool alignedWithRingPlane = Vector3.Dot(Vector3.Cross(sx, sy), normal) > 0f;
                for (int j = 0; j < segments; j++)
                {
                    Tris.Add(count);
                    if (alignedWithRingPlane)
                    {
                        Tris.Add(count2 + j);
                        Tris.Add(count2 + j + 1);
                    }
                    else
                    {
                        Tris.Add(count2 + j + 1);
                        Tris.Add(count2 + j);
                    }
                }
            }

            public Mesh Build(string name)
            {
                Mesh mesh = new Mesh
                {
                    name = name,
                    hideFlags = HideFlags.HideAndDontSave
                };
                if (Verts.Count > 65000)
                {
                    mesh.indexFormat = IndexFormat.UInt32;
                }
                mesh.SetVertices(Verts);
                mesh.SetNormals(Normals);
                mesh.SetUVs(0, Uvs);
                mesh.SetTriangles(Tris, 0);
                mesh.RecalculateTangents();
                mesh.RecalculateBounds();
                return mesh;
            }
        }

        private static readonly Dictionary<string, Material> _cache = new Dictionary<string, Material>();

        public static bool HasGraphics
        {
            get
            {
                if (!Application.isBatchMode)
                {
                    return SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
                }
                return false;
            }
        }

        /// <summary>
        /// Team colours for props and their labels. Same values OpenWorldPracticeMod uses
        /// for its markers and floating text, so a red shooter reads as the same red as a
        /// red dart marker rather than a second, slightly different red.
        /// </summary>
        public static Color TeamColor(PlayerTeam team)
        {
            switch (team)
            {
                case PlayerTeam.Blue: return new Color(0.45f, 0.60f, 1.00f);
                case PlayerTeam.Red: return new Color(1.00f, 0.45f, 0.45f);
                default: return new Color(0.85f, 0.85f, 0.85f);
            }
        }

        public static Material Get(string name, Color color, float smoothness = 0.2f, float metallic = 0f)
        {
            if (_cache.TryGetValue(name, out var value) && value != null)
            {
                return value;
            }
            Shader shader = ResolveShader();
            if (shader == null)
            {
                return null;
            }
            Material material = new Material(shader)
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave
            };
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }
            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", color);
            }
            if (material.HasProperty("_Smoothness"))
            {
                material.SetFloat("_Smoothness", smoothness);
            }
            if (material.HasProperty("_Glossiness"))
            {
                material.SetFloat("_Glossiness", smoothness);
            }
            if (material.HasProperty("_Metallic"))
            {
                material.SetFloat("_Metallic", metallic);
            }
            if (material.HasProperty("_EmissionColor"))
            {
                material.SetColor("_EmissionColor", Color.black);
            }
            if (color.a < 0.999f)
            {
                MakeTransparent(material);
            }
            _cache[name] = material;
            return material;
        }

        private static void MakeTransparent(Material m)
        {
            try
            {
                if (m.HasProperty("_Surface"))
                {
                    m.SetFloat("_Surface", 1f);
                }
                if (m.HasProperty("_Blend"))
                {
                    m.SetFloat("_Blend", 0f);
                }
                if (m.HasProperty("_SrcBlend"))
                {
                    m.SetFloat("_SrcBlend", 5f);
                }
                if (m.HasProperty("_DstBlend"))
                {
                    m.SetFloat("_DstBlend", 10f);
                }
                if (m.HasProperty("_ZWrite"))
                {
                    m.SetFloat("_ZWrite", 0f);
                }
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.DisableKeyword("_ALPHATEST_ON");
                m.renderQueue = 3000;
            }
            catch
            {
            }
        }

        private static Shader ResolveShader()
        {
            string[] array = new string[4] { "Universal Render Pipeline/Lit", "Universal Render Pipeline/Simple Lit", "Standard", "Unlit/Color" };
            for (int i = 0; i < array.Length; i++)
            {
                Shader shader = Shader.Find(array[i]);
                if (shader != null)
                {
                    return shader;
                }
            }
            return null;
        }

        public static void Dispose()
        {
            foreach (KeyValuePair<string, Material> item in _cache)
            {
                if (item.Value != null)
                {
                    try
                    {
                        UnityEngine.Object.Destroy(item.Value);
                    }
                    catch
                    {
                    }
                }
            }
            _cache.Clear();
        }

        public static GameObject AddPart(GameObject parent, string name, Mesh mesh, Material material, int layer)
        {
            GameObject gameObject = new GameObject(name);
            gameObject.layer = layer;
            gameObject.transform.SetParent(parent.transform, worldPositionStays: false);
            gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer meshRenderer = gameObject.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = ShadowCastingMode.On;
            meshRenderer.receiveShadows = true;
            return gameObject;
        }
    }
    /// <summary>
    /// Every player body and stick collider on the rink, rebuilt at most once per interval
    /// and shared by every prop that needs to ignore them.
    ///
    /// Each prop used to walk every player's rigged hierarchy itself on its own 2 s timer,
    /// so the real cost was props x players x GetComponentsInChildren - and because props
    /// spawned by one command share a spawn frame, their timers stay phase-aligned and the
    /// whole set's sweep landed in one frame together. One rebuild now serves all of them.
    /// </summary>
    internal static class PlayerColliderCache
    {
        /// <summary>Matches the props' own reassert cadence, so nothing waits longer than it
        /// did before for a newly spawned player to start being ignored.</summary>
        private const float RebuildInterval = 2f;

        private static readonly List<Collider> _colliders = new List<Collider>();
        private static float _nextRebuild;

        internal static List<Collider> Get()
        {
            if (Time.unscaledTime < _nextRebuild) return _colliders;
            _nextRebuild = Time.unscaledTime + RebuildInterval;
            _lastBuildFrame = Time.frameCount;

            _colliders.Clear();
            try
            {
                var playerManager = MonoBehaviourSingleton<PlayerManager>.Instance;
                if (playerManager == null) return _colliders;

                foreach (var player in playerManager.GetPlayers(false))
                {
                    if (player == null) continue;
                    if (player.PlayerBody != null)
                        _colliders.AddRange(player.PlayerBody.GetComponentsInChildren<Collider>(true));
                    if (player.Stick != null)
                        _colliders.AddRange(player.Stick.GetComponentsInChildren<Collider>(true));
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MaxPractice] Player collider sweep failed: " + ex.Message);
            }

            return _colliders;
        }

        /// <summary>
        /// Force the next Get() to re-scan - but at most once a frame.
        ///
        /// A prop built mid-interval would otherwise register its ignores against a list up
        /// to two seconds old and then not look again for another two, so a player who
        /// spawned in between could skate through a fresh set of cones for ~4 s. Props
        /// created by one command all land in the SAME frame, so the frame guard means the
        /// first of them rebuilds and the rest reuse that one sweep.
        /// </summary>
        internal static void Invalidate()
        {
            if (_lastBuildFrame == Time.frameCount) return;
            _nextRebuild = 0f;
        }

        private static int _lastBuildFrame = -1;
    }

    public static class ShooterAsset
    {
        public const string ObjectName = "MaxPractice_Shooter";

        private const int VisualLayer = 0;

        private const float ThroatY = 0.09f;

        private const float RollerR = 0.075f;

        private const float RollerGap = 0.105f;

        private const float ChassisW = 0.42f;

        private const float ChassisBottom = 0.2f;

        private const float ChassisTop = 0.5f;

        private const float ChassisBack = -0.24f;

        private const float ChassisFront = 0.1f;

        private const float ChuteBottom = 0.185f;

        private const float MuzzleZ = 0.15f;

        public static readonly Vector3 MuzzleLocal = new Vector3(0f, 0.09f, 0f);

        private static readonly Color BodyColor = new Color(0.16f, 0.17f, 0.2f);

        private static readonly Color TrimColor = new Color(0.25f, 0.77f, 0.63f);

        private static readonly Color RubberColor = new Color(0.09f, 0.09f, 0.1f);

        private static Mesh _body;

        private static Mesh _trim;

        private static Mesh _rubber;

        /// <summary>
        /// The shooter wears its owner's team colour on the trim and their name above it.
        ///
        /// Trim rather than body: the chassis has to stay dark to read as a machine, and
        /// the trim is already the accent. Owner may be null (nobody claimed it, or the
        /// announcement arrived before the player's object did) - then it falls back to the
        /// original teal and carries no nameplate.
        /// </summary>
        public static bool Apply(Puck puck, bool withCollision, Player owner = null)
        {
            if (puck == null || puck.gameObject == null)
            {
                return false;
            }
            // A headless server still needs the COMPONENT: ShooterVisual is the only thing
            // that switches the anchor puck's own collider off. Gating on graphics alone
            // left a solid invisible disc at the machine on dedicated servers, so shots
            // deflected off it there while passing through on a listen host. Cone and mini
            // net already had this shape.
            if (!PropStyle.HasGraphics && !withCollision)
            {
                return false;
            }
            try
            {
                var existing = puck.GetComponentInChildren<ShooterVisual>(includeInactive: true);
                if (existing != null)
                {
                    // Already built. Re-point the trim and the nameplate anyway: the first
                    // announcement often lands before the owner's Player object has
                    // replicated, so this is where a shooter picks up its colour and name.
                    existing.ApplyOwner(owner);
                    return true;
                }

                GameObject gameObject = new GameObject("MaxPractice_Shooter");
                gameObject.layer = 0;
                gameObject.transform.SetParent(puck.transform, worldPositionStays: false);
                MiniNetAsset.NeutralisePuckScale(gameObject.transform, puck);

                GameObject trim = null;
                if (PropStyle.HasGraphics)
                {
                    BuildMeshes();
                    Vector3 localPosition = new Vector3(0f, 0f, -0.15f);
                    PropStyle.AddPart(gameObject, "Body", _body, PropStyle.Get("MP_ShooterBody", BodyColor, 0.35f, 0.2f), 0).transform.localPosition = localPosition;
                    trim = PropStyle.AddPart(gameObject, "Trim", _trim, TrimMaterialFor(owner), 0);
                    trim.transform.localPosition = localPosition;
                    PropStyle.AddPart(gameObject, "Rollers", _rubber, PropStyle.Get("MP_ShooterRubber", RubberColor, 0.1f), 0).transform.localPosition = localPosition;
                }

                var visual = gameObject.AddComponent<ShooterVisual>();
                visual.Init(puck);
                visual.BindTrim(trim);
                visual.ApplyOwner(owner);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MaxPractice] Failed to apply shooter: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// One cached material per team, keyed by name so PropStyle's cache hands the same
        /// instance to every shooter on that team rather than leaking one per spawn.
        /// </summary>
        internal static Material TrimMaterialFor(Player owner)
        {
            if (owner == null) return PropStyle.Get("MP_ShooterTrim", TrimColor, 0.5f);

            PlayerTeam team = PracticeHelpers.GetPlayerTeam(owner);
            return PropStyle.Get("MP_ShooterTrim_" + team, PropStyle.TeamColor(team), 0.5f);
        }

        private static void BuildMeshes()
        {
            if (!(_body != null) || !(_trim != null) || !(_rubber != null))
            {
                float num = 0.21f;
                PropStyle.MeshBuilder meshBuilder = new PropStyle.MeshBuilder();
                meshBuilder.Box(new Vector3(0f - num, 0f, -0.24f), new Vector3(0f - num + 0.06f, 0.2f, -0.17999999f));
                meshBuilder.Box(new Vector3(num - 0.06f, 0f, -0.24f), new Vector3(num, 0.2f, -0.17999999f));
                meshBuilder.Box(new Vector3(0f - num, 0f, 0.040000003f), new Vector3(0f - num + 0.06f, 0.2f, 0.1f));
                meshBuilder.Box(new Vector3(num - 0.06f, 0f, 0.040000003f), new Vector3(num, 0.2f, 0.1f));
                meshBuilder.Box(new Vector3(0f - num, 0.2f, -0.24f), new Vector3(num, 0.5f, 0.1f));
                meshBuilder.Box(new Vector3(0f - num + 0.05f, 0.5f, -0.19999999f), new Vector3(num - 0.05f, 0.68f, 0.060000002f));
                meshBuilder.Box(new Vector3(-0.105f, 0.185f, 0f), new Vector3(0.105f, 0.2f, 0.08f));
                _body = meshBuilder.Build("MaxPractice_ShooterBody");
                PropStyle.MeshBuilder meshBuilder2 = new PropStyle.MeshBuilder();
                // Skid tops at 0.16, not 0.165: the rollers' upper end caps are discs in the
                // plane y = 0.165 centred at (+/-0.18, 0.15) with r = 0.075, so they reach
                // x = 0.255 and overlapped these plates in a ~10x7 mm patch of exactly
                // coplanar, both-up-facing geometry - open to the sky, nothing in front of
                // it. Same z-fight as the chassis band below, and it became visible for the
                // same reason: dark teal against dark rubber hid it, a bright team colour
                // against dark rubber does not.
                meshBuilder2.Box(new Vector3(-0.275f, 0f, 0.1f), new Vector3(-0.24499999f, 0.16f, 0.3f));
                meshBuilder2.Box(new Vector3(0.24499999f, 0f, 0.1f), new Vector3(0.275f, 0.16f, 0.3f));
                // Proud on ALL FOUR sides, not just x. This band used to run z -0.24..0.1,
                // exactly the chassis box's own z range, so its front and back faces were
                // coplanar with the body's and z-fought - two surfaces at the same depth,
                // the GPU picking per-pixel per-frame. Invisible while both were dark; a
                // shimmering band the moment the trim became a bright team colour against
                // the dark chassis. The 5 mm it already had in x is now matched in z.
                meshBuilder2.Box(new Vector3(0f - num - 0.005f, 0.26f, -0.245f), new Vector3(num + 0.005f, 0.3f, 0.105f));
                _trim = meshBuilder2.Build("MaxPractice_ShooterTrim");
                PropStyle.MeshBuilder meshBuilder3 = new PropStyle.MeshBuilder();
                float num2 = 0.18f;
                meshBuilder3.Tube(new Vector3(0f - num2, 0.015f, 0.15f), new Vector3(0f - num2, 0.165f, 0.15f), 0.075f, 16);
                meshBuilder3.Tube(new Vector3(num2, 0.015f, 0.15f), new Vector3(num2, 0.165f, 0.15f), 0.075f, 16);
                _rubber = meshBuilder3.Build("MaxPractice_ShooterRubber");
            }
        }

        /// <summary>
        /// Destroy every live shooter first, the way ConeAsset.Dispose does, THEN drop the
        /// shared meshes. Dropping the meshes and PropStyle's materials while the visuals
        /// were still alive left the props rendering nothing over anchor pucks that stayed
        /// spawned, invisible and collider-less - and the tracking dictionaries are cleared
        /// straight after, so nothing could find them to clean up afterwards.
        /// </summary>
        public static void Dispose()
        {
            try
            {
                var visuals = UnityEngine.Object.FindObjectsByType<ShooterVisual>(FindObjectsSortMode.None);
                foreach (var visual in visuals)
                {
                    if (visual != null)
                        UnityEngine.Object.Destroy(visual.gameObject);
                }
            }
            catch { }

            DestroyMesh(ref _body);
            DestroyMesh(ref _trim);
            DestroyMesh(ref _rubber);
        }

        private static void DestroyMesh(ref Mesh m)
        {
            if (m != null)
            {
                try
                {
                    UnityEngine.Object.Destroy(m);
                }
                catch
                {
                }
            }
            m = null;
        }
    }
    public class ShooterVisual : MonoBehaviour
    {
        private Puck _puck;

        private Transform _puckTransform;

        /// <summary>Clearance above the ice, just enough to avoid z-fighting the surface.</summary>
        private const float GroundClearance = 0.002f;

        /// <summary>
        /// How far above the ice PracticeHelpers.SpawnPropPuck's callers place a prop's
        /// anchor puck - IceHeightAt(pos, 0.08f), then frozen there. Keep in step with
        /// PracticeHelpers.SetShooter and the /mininet handler.
        /// </summary>
        internal const float AnchorClearance = 0.08f;

        /// <summary>How far the chassis reaches BEHIND the visual origin: the parts are
        /// offset -0.15 in z and the chassis box runs back to -0.24, so 0.39. Used to work
        /// out how much to lift the machine when it is pitched up.</summary>
        private const float RearOverhang = 0.39f;

        private const float MaxPitchDegrees = 35f;

        private readonly List<Renderer> _hidden = new List<Renderer>();

        private readonly List<Collider> _disabled = new List<Collider>();

        private float _nextReassert;
        private GameObject _trim;

        /// <summary>Remember the trim so a later owner resolve can recolour it.</summary>
        public void BindTrim(GameObject trim)
        {
            _trim = trim;
        }

        /// <summary>
        /// Point the machine at its owner: team colour on the trim, name above it. Safe to
        /// call repeatedly - PropNetwork re-announces every 5 s, and this is how a shooter
        /// built before its owner's Player object replicated eventually gets both.
        /// </summary>
        public void ApplyOwner(Player owner)
        {
            if (owner == null) return;

            if (_trim != null)
            {
                var renderer = _trim.GetComponent<MeshRenderer>();
                if (renderer != null)
                {
                    var material = ShooterAsset.TrimMaterialFor(owner);
                    if (material != null && renderer.sharedMaterial != material)
                        renderer.sharedMaterial = material;
                }
            }

            PropLabel.Attach(base.gameObject, owner);
        }

        public void Init(Puck puck)
        {
            _puck = puck;
            _puckTransform = ((puck != null) ? puck.transform : null);
            if (_puckTransform == null)
            {
                return;
            }
            Reassert();
            Snap();
        }

        /// <summary>
        /// Re-hide the anchor puck on a timer, the way ConeVisual and MiniNetVisual already
        /// do. Hiding once in Init was not enough: the game re-enables puck visuals on some
        /// client join/leave paths and when the outline/silhouette setting is toggled, and
        /// the 15 s server repair sweep does not help either - it routes through
        /// ShooterAsset.Apply, which early-returns as soon as a ShooterVisual exists.
        /// </summary>
        public void Reassert()
        {
            if (_puck == null) return;

            // Guarded like ConeVisual.Reassert and MiniNetVisual.Reassert - this one was the
            // odd one out. A dedicated server has no renderers to hide, so it was walking the
            // anchor puck's whole hierarchy and allocating the result array every 2 seconds,
            // per shooter, to iterate nothing.
            //
            // The collider half below stays unconditional: unlike its two siblings this class
            // has no collision-ownership flag, and hiding the anchor puck's own colliders so
            // players cannot hit an invisible puck is a server-side job.
            if (PropStyle.HasGraphics)
            {
                Renderer[] componentsInChildren = _puck.GetComponentsInChildren<Renderer>(includeInactive: true);
                foreach (Renderer renderer in componentsInChildren)
                {
                    if (!(renderer == null) && renderer.enabled && !renderer.transform.IsChildOf(base.transform))
                    {
                        renderer.enabled = false;
                        if (!_hidden.Contains(renderer)) _hidden.Add(renderer);
                    }
                }
            }

            Collider[] componentsInChildren2 = _puck.GetComponentsInChildren<Collider>(includeInactive: true);
            foreach (Collider collider in componentsInChildren2)
            {
                if (!(collider == null) && collider.enabled && !collider.transform.IsChildOf(base.transform))
                {
                    collider.enabled = false;
                    if (!_disabled.Contains(collider)) _disabled.Add(collider);
                }
            }
        }

        private void LateUpdate()
        {
            if (_puckTransform == null)
            {
                UnityEngine.Object.Destroy(base.gameObject);
                return;
            }

            Snap();
            if (Time.unscaledTime >= _nextReassert)
            {
                _nextReassert = Time.unscaledTime + 2f;
                Reassert();
            }
        }

        private void Snap()
        {
            Vector3 position = _puckTransform.position;

            // Pitch comes from the anchor puck: the server tilts it for a lob, and the
            // puck's rotation is replicated, so the machine aims up on every client with
            // nothing extra on the wire. Roll is dropped - only yaw and pitch mean anything
            // for a machine standing on the ice - and the pitch is clamped so a puck that
            // somehow tumbles cannot stand the shooter on its nose.
            Vector3 euler = _puckTransform.rotation.eulerAngles;
            float pitch = Mathf.Clamp(Mathf.DeltaAngle(0f, euler.x), -MaxPitchDegrees, MaxPitchDegrees);

            // Ice level, derived from the ANCHOR PUCK rather than looked up.
            //
            // The anchor is spawned at IceHeightAt(pos, 0.08f) and then frozen kinematic, so
            // it sits exactly AnchorClearance above whatever ice it was placed on, forever.
            // Asking RinkSheets for the floor instead looked equivalent but was not:
            // SheetFloorAt returns a hard 0 once no sheet roots are standing, and sheets are
            // reaped 30 s after they empty while props are deliberately left on them - so a
            // shooter built on a sheet 40 m up teleported to y=0 over the main rink, taking
            // its nameplate and (via TryGetMuzzle) the pass origin with it. Subtracting a
            // known constant cannot do that, is right on every sheet, and costs nothing.
            float iceY = position.y - AnchorClearance;

            // Tilting about the origin swings the chassis' rear corner down through the
            // ice, so lift by exactly how far it drops.
            float lift = Mathf.Abs(Mathf.Sin(pitch * Mathf.Deg2Rad)) * RearOverhang;

            Vector3 vector = new Vector3(position.x, iceY + GroundClearance + lift, position.z);
            if ((base.transform.position - vector).sqrMagnitude > 1E-08f)
            {
                base.transform.position = vector;
            }
            Quaternion quaternion = Quaternion.Euler(pitch, euler.y, 0f);
            if (Quaternion.Angle(base.transform.rotation, quaternion) > 0.05f)
            {
                base.transform.rotation = quaternion;
            }
        }

        private void OnDestroy()
        {
            foreach (Renderer item in _hidden)
            {
                if (item != null)
                {
                    item.enabled = true;
                }
            }
            foreach (Collider item2 in _disabled)
            {
                if (item2 != null)
                {
                    item2.enabled = true;
                }
            }
            _hidden.Clear();
            _disabled.Clear();
        }
    }
    public static class MiniNetAsset
    {
        public const string ObjectName = "MaxPractice_MiniNet";

        private const string CloneName = "MaxPractice_NetClone";

        public const float Width = 1.2f;

        public const float Height = 0.8f;

        public const float Depth = 0.55f;

        private const float PostR = 0.032f;

        private static readonly Color FrameColor = new Color(0.82f, 0.11f, 0.11f);

        private static readonly Color MeshColor = new Color(0.92f, 0.92f, 0.95f, 0.34f);

        private static Mesh _frame;

        private static Mesh _netting;

        private static PropertyInfo _batchRootProperty;

        private static bool _batchRootProbed;

        private static readonly string[] ColliderGroups = new string[2] { "Goal Post", "Net" };

        private static bool _loggedDiagnostics;

        /// <summary>
        /// Like the shooter, the net wears its owner's team colour and name. See
        /// <see cref="MiniNetVisual.ApplyOwner"/> for why the colouring differs between the
        /// cloned and the fallback net.
        /// </summary>
        public static bool Apply(Puck puck, bool withCollision, Player owner = null)
        {
            if (puck == null || puck.gameObject == null)
            {
                return false;
            }
            if (!PropStyle.HasGraphics && !withCollision)
            {
                return false;
            }
            try
            {
                var existingVisual = puck.GetComponentInChildren<MiniNetVisual>(includeInactive: true);
                if (existingVisual != null)
                {
                    // Same reason as the shooter: the first announcement usually lands
                    // before the owner's Player object has replicated, so this is where an
                    // already-built net picks up its colour and name.
                    existingVisual.ApplyOwner(owner);
                    return true;
                }

                GameObject gameObject = new GameObject("MaxPractice_MiniNet");
                gameObject.transform.SetParent(puck.transform, worldPositionStays: false);
                NeutralisePuckScale(gameObject.transform, puck);
                GameObject gameObject2 = TryCloneGameNet(gameObject, ConfigManager.Config.MiniNetScale, puck.gameObject.layer, withCollision);
                GameObject fallbackFrame = null;
                if (gameObject2 == null)
                {
                    gameObject.layer = 0;
                    BuildFallbackMeshes();
                    fallbackFrame = PropStyle.AddPart(gameObject, "Frame", _frame, FrameMaterialFor(owner), 0);
                    PropStyle.AddPart(gameObject, "Netting", _netting, PropStyle.Get("MP_NetMesh", MeshColor, 0.1f), 0);
                }

                var visual = gameObject.AddComponent<MiniNetVisual>();
                visual.Init(puck, withCollision, gameObject2 != null);
                visual.BindClone(gameObject2);
                visual.BindFrame(fallbackFrame);
                visual.ApplyOwner(owner);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MaxPractice] Failed to apply mini net: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Frame material for the FALLBACK net, whose frame mesh is ours to colour outright.
        /// One cached material per team, exactly like the shooter's trim.
        /// </summary>
        internal static Material FrameMaterialFor(Player owner)
        {
            if (owner == null) return PropStyle.Get("MP_NetFrame", FrameColor, 0.4f);

            PlayerTeam team = PracticeHelpers.GetPlayerTeam(owner);
            return PropStyle.Get("MP_NetFrame_" + team, PropStyle.TeamColor(team), 0.4f);
        }

        // withCollision is the server-authority flag, not a style option. A pure client
        // gets the net's LOOK and nothing else: a second set of colliders here would
        // fight the replicated puck position, and MiniNetVisual.Reassert bails before
        // the Physics.IgnoreCollision pass when it doesn't own collision, so those
        // colliders would never even be paired off against bodies and sticks.
        private static GameObject TryCloneGameNet(GameObject parent, float scale, int physicsLayer, bool withCollision)
        {
            try
            {
                Goal goal = null;
                Goal[] array = UnityEngine.Object.FindObjectsByType<Goal>(FindObjectsSortMode.None);
                if (array != null)
                {
                    Goal[] array2 = array;
                    foreach (Goal goal2 in array2)
                    {
                        // Never copy a PRACTICE SHEET's net. Same race GoalGeometry documents
                        // and guards: a clone's Goal components are removed with Destroy, which
                        // Unity defers to end of frame, while BuildServer activates the clone
                        // root before it returns - so for the rest of that frame the clone's
                        // nets are live and come back from this search too, in unspecified
                        // order. Copying one is worse than copying the arena's, because
                        // MarkCloneNames has already renamed its children: BuildNetColliders
                        // looks for 'Goal Post' and 'Net' and would find 'MP Goal Post', so the
                        // mini net comes out with no collision at all and pucks pass through it.
                        if (goal2 != null && goal2.gameObject != null && goal2.gameObject.activeInHierarchy
                            && !IsSheetClone(goal2.transform))
                        {
                            goal = goal2;
                            break;
                        }
                    }
                }
                if (goal == null)
                {
                    Debug.Log("[MaxPractice] Mini net: no active Goal in the scene to copy; using the built-in one.");
                    return null;
                }
                Transform transform = goal.transform;
                Debug.Log("[MaxPractice] Mini net: copying '" + transform.name + "' " + $"goalLossyScale={transform.lossyScale} goalPos={transform.position} " + $"goalRot={transform.rotation.eulerAngles} MiniNetScale={scale:0.###} " + $"arenaScale={RinkSheets.ArenaScale}");
                GameObject gameObject = new GameObject("MaxPractice_NetClone");
                gameObject.transform.SetParent(parent.transform, worldPositionStays: false);
                int num = 0;
                Bounds partBounds = default(Bounds);
                bool havePartBounds = false;
                num += BuildMeshParts(transform, gameObject.transform, ref partBounds, ref havePartBounds);
                num += BuildNetting(transform, gameObject.transform, null);
                if (num == 0)
                {
                    UnityEngine.Object.DestroyImmediate(gameObject);
                    Debug.Log("[MaxPractice] Mini net: nothing usable on the rink's net to rebuild from; using the built-in one.");
                    return null;
                }
                Debug.Log($"[MaxPractice] Mini net: rebuilt {num} part(s), " + $"holderScale={gameObject.transform.localScale} " + "partBounds=" + (havePartBounds ? partBounds.size.ToString() : "none"));
                if (withCollision)
                    BuildNetColliders(transform, gameObject.transform, physicsLayer);
                float num2 = Mathf.Clamp(scale, 0.1f, 1f);
                gameObject.transform.localRotation = Quaternion.Euler(0f, MouthYaw(transform), 0f);
                Vector3 lossyScale = transform.lossyScale;
                gameObject.transform.localScale = lossyScale * num2;
                float num3 = num2 * Mathf.Abs((lossyScale.x < 0.0001f) ? 1f : lossyScale.x);
                Bounds bounds;
                if (havePartBounds)
                {
                    bounds = new Bounds(partBounds.center * num3, partBounds.size * num3);
                    bounds.center = Quaternion.Euler(0f, MouthYaw(transform), 0f) * bounds.center;
                    gameObject.transform.localPosition -= new Vector3(bounds.center.x, bounds.min.y, bounds.min.z);
                }
                else if (TryMeasure(parent, out bounds))
                {
                    gameObject.transform.localPosition -= new Vector3(bounds.center.x, bounds.min.y, bounds.min.z);
                }
                else
                {
                    bounds = new Bounds(Vector3.zero, Vector3.zero);
                }
                LogNetDiagnostics(transform, gameObject, num, num2, bounds);
                if (withCollision)
                    WarnIfCollisionMismatch(gameObject.transform);
                return gameObject;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MaxPractice] Could not rebuild the rink's net, falling back: " + ex.Message);
                return null;
            }
        }

        /// <summary>True for a net belonging to one of our practice-sheet clones.</summary>
        private static bool IsSheetClone(Transform t)
        {
            Transform root = t != null ? t.root : null;
            return root != null && root.name.StartsWith("MaxPractice_", StringComparison.Ordinal);
        }

        private static int BuildMeshParts(Transform goalT, Transform holder, ref Bounds partBounds, ref bool havePartBounds)
        {
            int num = 0;
            // Inactive renderers count here, and CompetitiveAdjustments is why.
            //
            // Its GoalFrameTweaks proxies the batched goal frame itself and leaves the
            // original renderers switched off - so an active-only search finds nothing at
            // all under a resized goal, which is why this kept reporting "rebuilt 1
            // part(s), partBounds=none" and the mini net came out as netting with no
            // frame. Their MESHES are still exactly right, and a fresh renderer is built
            // from them rather than the source being switched on, so whether the source is
            // visible does not matter.
            //
            // The material check is what the enabled check was really protecting against:
            // the goal's four editor proxy meshes have no material, and drawing those is
            // what turns the whole net magenta.
            MeshRenderer[] componentsInChildren = goalT.GetComponentsInChildren<MeshRenderer>(includeInactive: true);
            foreach (MeshRenderer meshRenderer in componentsInChildren)
            {
                if (meshRenderer == null || meshRenderer.sharedMaterial == null)
                {
                    continue;
                }
                MeshFilter component = meshRenderer.GetComponent<MeshFilter>();
                Mesh mesh = ((component != null) ? component.sharedMesh : null);
                if (mesh == null)
                {
                    continue;
                }
                GameObject gameObject = new GameObject(meshRenderer.name);
                gameObject.transform.SetParent(holder, worldPositionStays: false);
                Vector3 size = mesh.bounds.size;
                if (!(size.x > 8f) && !(size.y > 8f) && !(size.z > 8f))
                {
                    gameObject.transform.localPosition = goalT.InverseTransformPoint(meshRenderer.transform.position);
                    gameObject.transform.localRotation = Quaternion.Inverse(goalT.rotation) * meshRenderer.transform.rotation;
                    gameObject.transform.localScale = RelativeScale(meshRenderer.transform.lossyScale, goalT.lossyScale);
                    gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                    Accumulate(goalT, meshRenderer.bounds, ref partBounds, ref havePartBounds);
                    MeshRenderer meshRenderer2 = gameObject.AddComponent<MeshRenderer>();
                    meshRenderer2.sharedMaterials = meshRenderer.sharedMaterials;
                    meshRenderer2.shadowCastingMode = ShadowCastingMode.On;
                    meshRenderer2.receiveShadows = true;
                    num++;
                    continue;
                }
                int subMeshStart = GetSubMeshStart(meshRenderer);
                if (subMeshStart < 0)
                {
                    UnityEngine.Object.DestroyImmediate(gameObject);
                    Debug.LogWarning("[MaxPractice] '" + meshRenderer.name + "' is in a non-readable batch and its submesh range is unavailable, so it can't be re-drawn.");
                    continue;
                }
                int materialCount = ((meshRenderer.sharedMaterials == null) ? 1 : meshRenderer.sharedMaterials.Length);
                int num2 = ResolveSliceCount(mesh, subMeshStart, materialCount);
                if (num2 <= 0)
                {
                    UnityEngine.Object.DestroyImmediate(gameObject);
                    Debug.LogWarning("[MaxPractice] '" + meshRenderer.name + "' has a submesh slice outside the combined mesh; skipping.");
                    continue;
                }
                BatchedPartDrawer batchedPartDrawer = gameObject.AddComponent<BatchedPartDrawer>();
                batchedPartDrawer.Mesh = mesh;
                batchedPartDrawer.Materials = meshRenderer.sharedMaterials;
                batchedPartDrawer.FirstSubMesh = subMeshStart;
                batchedPartDrawer.SliceCount = num2;
                // Baked -> world for this geometry. On a resized arena that is the goal's
                // bake delta, not the batch root's matrix: CompetitiveAdjustments holds
                // the goal at vanilla size while the root is scaled, so the two disagree
                // and the frame came out at the wrong size. Falls back to the batch root
                // when there is no delta worth speaking of, which is the vanilla case.
                Matrix4x4 matrix4x = RinkCloneVisuals.ResolveBakeDelta(goalT);
                if (matrix4x == Matrix4x4.identity)
                {
                    Transform batchRoot = GetBatchRoot(meshRenderer);
                    if (batchRoot != null) matrix4x = batchRoot.localToWorldMatrix;
                }
                batchedPartDrawer.Offset = goalT.worldToLocalMatrix * matrix4x;
                batchedPartDrawer.Layer = meshRenderer.gameObject.layer;
                batchedPartDrawer.ShadowCasting = meshRenderer.shadowCastingMode;
                batchedPartDrawer.ReceiveShadows = meshRenderer.receiveShadows;
                batchedPartDrawer.SourceExtents = meshRenderer.bounds.extents;
                Accumulate(goalT, meshRenderer.bounds, ref partBounds, ref havePartBounds);
                try
                {
                    float num3 = Vector3.Distance(matrix4x.MultiplyPoint3x4(mesh.GetSubMesh(subMeshStart).bounds.center), meshRenderer.bounds.center);
                    if (num3 > 0.5f)
                    {
                        Debug.LogWarning($"[MaxPractice] '{meshRenderer.name}' submesh centre is {num3:0.00} m from its " + "world bounds centre - the draw matrix is probably wrong.");
                    }
                }
                catch
                {
                }
                Debug.Log("[MaxPractice] Re-drawing '" + meshRenderer.name + "' from the batch " + $"(submeshes {subMeshStart}..{subMeshStart + num2 - 1} of {mesh.subMeshCount}, " + "source=" + DescribeBatchSource(meshRenderer, goalT) + ").");
                num++;
            }
            return num;
        }

        private static int GetSubMeshStart(MeshRenderer mr)
        {
            try
            {
                return mr.subMeshStartIndex;
            }
            catch
            {
            }
            return -1;
        }

        private static Vector3 RelativeScale(Vector3 partLossy, Vector3 goalLossy)
        {
            return new Vector3((Mathf.Abs(goalLossy.x) < 0.0001f) ? partLossy.x : (partLossy.x / goalLossy.x), (Mathf.Abs(goalLossy.y) < 0.0001f) ? partLossy.y : (partLossy.y / goalLossy.y), (Mathf.Abs(goalLossy.z) < 0.0001f) ? partLossy.z : (partLossy.z / goalLossy.z));
        }

        /// <summary>Which mapping the frame was drawn through, for the log.</summary>
        private static string DescribeBatchSource(Renderer renderer, Transform goalT)
        {
            Matrix4x4 delta = RinkCloneVisuals.ResolveBakeDelta(goalT);
            if (delta != Matrix4x4.identity) return "goal bake delta";
            Transform root = GetBatchRoot(renderer);
            return root != null ? "batch root '" + root.name + "'" : "world";
        }

        private static Transform GetBatchRoot(Renderer renderer)
        {
            if (!_batchRootProbed)
            {
                _batchRootProbed = true;
                try
                {
                    _batchRootProperty = typeof(Renderer).GetProperty("staticBatchRootTransform", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                catch
                {
                }
            }
            if (_batchRootProperty == null || !_batchRootProperty.CanRead || renderer == null)
            {
                return null;
            }
            try
            {
                return _batchRootProperty.GetValue(renderer, null) as Transform;
            }
            catch
            {
                return null;
            }
        }

        private static int ResolveSliceCount(Mesh mesh, int start, int materialCount)
        {
            int num = Mathf.Max(1, materialCount);
            if (start + num > mesh.subMeshCount)
            {
                num = mesh.subMeshCount - start;
            }
            return Mathf.Max(0, num);
        }



        private static int BuildNetting(Transform goalT, Transform holder, List<Transform> made)
        {
            int num = 0;
            int num2 = 0;
            SkinnedMeshRenderer[] componentsInChildren = goalT.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true);
            foreach (SkinnedMeshRenderer skinnedMeshRenderer in componentsInChildren)
            {
                num2++;
                if (skinnedMeshRenderer == null || skinnedMeshRenderer.sharedMesh == null)
                {
                    Debug.LogWarning("[MaxPractice] netting '" + ((skinnedMeshRenderer != null) ? skinnedMeshRenderer.name : "null") + "' has no mesh; skipping.");
                    continue;
                }
                Mesh mesh = null;
                Mesh sharedMesh = skinnedMeshRenderer.sharedMesh;
                Vector3 size = sharedMesh.bounds.size;
                if (size.x >= 0.1f && size.x <= 8f)
                {
                    mesh = sharedMesh;
                }
                if (mesh == null)
                {
                    Mesh mesh2 = new Mesh
                    {
                        name = "MaxPractice_NetMeshBaked",
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    skinnedMeshRenderer.BakeMesh(mesh2);
                    Bounds bounds = mesh2.bounds;
                    if (bounds.size.x >= 0.1f && bounds.size.x <= 8f && bounds.center.magnitude < 8f)
                    {
                        mesh = mesh2;
                    }
                    else
                    {
                        Debug.Log("[MaxPractice] netting '" + skinnedMeshRenderer.name + "': baked mesh is in world space (centre " + bounds.center.ToString("F1") + "), not usable as local geometry.");
                        UnityEngine.Object.Destroy(mesh2);
                    }
                }
                if (mesh == null)
                {
                    Debug.LogWarning("[MaxPractice] netting '" + skinnedMeshRenderer.name + "' has no usable mesh; skipping.");
                    continue;
                }
                GameObject gameObject = new GameObject(skinnedMeshRenderer.name);
                gameObject.transform.SetParent(holder, worldPositionStays: false);
                gameObject.transform.localPosition = goalT.InverseTransformPoint(skinnedMeshRenderer.transform.position);
                gameObject.transform.localRotation = Quaternion.Inverse(goalT.rotation) * skinnedMeshRenderer.transform.rotation;
                gameObject.transform.localScale = RelativeScale(skinnedMeshRenderer.transform.lossyScale, goalT.lossyScale);
                gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                // A baked mesh is ours alone and is built fresh on every mini-net spawn, on
                // the server and on every client. HideAndDontSave also stops Unity reclaiming
                // it on scene unload, so without this it accumulates for the life of the
                // process. The sharedMesh path above belongs to the game - never destroy that.
                if (mesh != sharedMesh) gameObject.AddComponent<OwnedMesh>().Mesh = mesh;
                MeshRenderer meshRenderer = gameObject.AddComponent<MeshRenderer>();
                meshRenderer.sharedMaterials = skinnedMeshRenderer.sharedMaterials;
                meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
                made?.Add(gameObject.transform);
                num++;
            }
            if (num2 == 0)
            {
                Debug.LogWarning("[MaxPractice] No SkinnedMeshRenderer under the goal - no netting to bake.");
            }
            return num;
        }


        private static void WarnIfCollisionMismatch(Transform holder)
        {
            try
            {
                bool flag = false;
                bool flag2 = false;
                Bounds bounds = default(Bounds);
                Bounds bounds2 = default(Bounds);
                Renderer[] componentsInChildren = holder.GetComponentsInChildren<Renderer>(includeInactive: true);
                foreach (Renderer renderer in componentsInChildren)
                {
                    if (!(renderer == null) && renderer.enabled)
                    {
                        if (!flag)
                        {
                            bounds = renderer.bounds;
                            flag = true;
                        }
                        else
                        {
                            bounds.Encapsulate(renderer.bounds);
                        }
                    }
                }
                Collider[] componentsInChildren2 = holder.GetComponentsInChildren<Collider>(includeInactive: true);
                foreach (Collider collider in componentsInChildren2)
                {
                    if (!(collider == null) && !collider.isTrigger)
                    {
                        if (!flag2)
                        {
                            bounds2 = collider.bounds;
                            flag2 = true;
                        }
                        else
                        {
                            bounds2.Encapsulate(collider.bounds);
                        }
                    }
                }
                if (!flag || !flag2)
                {
                    Debug.LogWarning($"[MaxPractice] Mini net: visuals={flag} collision={flag2} — one of them is missing.");
                    return;
                }
                float num = Vector3.Distance(bounds.center, bounds2.center);
                float magnitude = (bounds.size - bounds2.size).magnitude;
                Debug.Log("[MaxPractice] Mini net: visual " + bounds.size.ToString("F2") + " @ " + bounds.center.ToString("F2") + " | collision " + bounds2.size.ToString("F2") + " @ " + bounds2.center.ToString("F2") + " | " + $"centre off {num:F2} m, size off {magnitude:F2} m" + ((num > 0.15f || magnitude > 0.25f) ? "   <-- MISMATCH" : ""));
            }
            catch
            {
            }
        }

        private static void BuildNetColliders(Transform goalT, Transform holder, int physicsLayer)
        {
            int num = 0;
            int num2 = 0;
            string[] colliderGroups = ColliderGroups;
            foreach (string text in colliderGroups)
            {
                Transform transform = null;
                Transform[] componentsInChildren = goalT.GetComponentsInChildren<Transform>(includeInactive: true);
                foreach (Transform transform2 in componentsInChildren)
                {
                    if (transform2 != null && transform2.name == text)
                    {
                        transform = transform2;
                        break;
                    }
                }
                if (transform == null)
                {
                    Debug.LogWarning("[MaxPractice] Mini net: no '" + text + "' collider group under the goal.");
                    continue;
                }
                GameObject gameObject = new GameObject(text);
                gameObject.layer = physicsLayer;
                gameObject.transform.SetParent(holder, worldPositionStays: false);
                gameObject.transform.localPosition = goalT.InverseTransformPoint(transform.position);
                gameObject.transform.localRotation = Quaternion.Inverse(goalT.rotation) * transform.rotation;
                gameObject.transform.localScale = RelativeScale(transform.lossyScale, goalT.lossyScale);
                int num3 = 0;
                Collider[] components = transform.GetComponents<Collider>();
                foreach (Collider collider in components)
                {
                    if (!(collider == null) && !collider.isTrigger && collider.enabled && collider.gameObject.activeInHierarchy && CopyCollider(collider, gameObject))
                    {
                        num3++;
                    }
                }
                if (num3 == 0)
                {
                    UnityEngine.Object.DestroyImmediate(gameObject);
                    continue;
                }
                num++;
                num2 += num3;
            }
            Debug.Log($"[MaxPractice] Mini net collision: {num2} collider(s) from {num} group(s) on layer {physicsLayer}.");
            if (num2 == 0)
            {
                Debug.LogWarning("[MaxPractice] Mini net has no collision - pucks will pass straight through.");
            }
        }

        private static bool CopyCollider(Collider src, GameObject dst)
        {
            CapsuleCollider capsuleCollider = src as CapsuleCollider;
            if (capsuleCollider != null)
            {
                CapsuleCollider capsuleCollider2 = dst.AddComponent<CapsuleCollider>();
                capsuleCollider2.center = capsuleCollider.center;
                capsuleCollider2.radius = capsuleCollider.radius;
                capsuleCollider2.height = capsuleCollider.height;
                capsuleCollider2.direction = capsuleCollider.direction;
                return true;
            }
            BoxCollider boxCollider = src as BoxCollider;
            if (boxCollider != null)
            {
                BoxCollider boxCollider2 = dst.AddComponent<BoxCollider>();
                boxCollider2.center = boxCollider.center;
                boxCollider2.size = boxCollider.size;
                return true;
            }
            SphereCollider sphereCollider = src as SphereCollider;
            if (sphereCollider != null)
            {
                SphereCollider sphereCollider2 = dst.AddComponent<SphereCollider>();
                sphereCollider2.center = sphereCollider.center;
                sphereCollider2.radius = sphereCollider.radius;
                return true;
            }
            MeshCollider meshCollider = src as MeshCollider;
            if (meshCollider != null && meshCollider.sharedMesh != null)
            {
                MeshCollider meshCollider2 = dst.AddComponent<MeshCollider>();
                meshCollider2.sharedMesh = meshCollider.sharedMesh;
                meshCollider2.convex = meshCollider.convex;
                return true;
            }
            return false;
        }

        private static float MouthYaw(Transform goalT)
        {
            try
            {
                float f = GoalGeometry.CentreIceZ - goalT.position.z;
                if (Mathf.Abs(f) < 0.01f)
                {
                    return 0f;
                }
                Vector3 vector = Quaternion.Inverse(goalT.rotation) * new Vector3(0f, 0f, Mathf.Sign(f));
                vector.y = 0f;
                if (vector.sqrMagnitude < 1E-06f)
                {
                    return 0f;
                }
                return Vector3.SignedAngle(vector.normalized, Vector3.back, Vector3.up);
            }
            catch
            {
                return 0f;
            }
        }

        private static void LogNetDiagnostics(Transform goalT, GameObject holder, int parts, float scale, Bounds local)
        {
            Debug.Log($"[MaxPractice] Mini net: rebuilt {parts} mesh part(s) from '{goalT.name}' at scale {scale:0.00} " + $"-> {local.size.x:0.00} x {local.size.y:0.00} x {local.size.z:0.00} m");
            if (_loggedDiagnostics)
            {
                return;
            }
            _loggedDiagnostics = true;
            try
            {
                StringBuilder stringBuilder = new StringBuilder();
                stringBuilder.AppendLine("[MaxPractice] ---- mini net build (once per session) ----");
                Transform[] componentsInChildren = holder.GetComponentsInChildren<Transform>(includeInactive: true);
                foreach (Transform transform in componentsInChildren)
                {
                    if (!(transform == holder.transform))
                    {
                        Renderer component = transform.GetComponent<Renderer>();
                        string arg = ((component == null) ? "-" : $"{component.GetType().Name} size={component.bounds.size.x:0.00}x{component.bounds.size.y:0.00}x{component.bounds.size.z:0.00}");
                        stringBuilder.AppendLine($"  {transform.name}  colliders={transform.GetComponents<Collider>().Length}  {arg}");
                    }
                }
                Debug.Log(stringBuilder.ToString());
            }
            catch
            {
            }
        }

        internal static void NeutralisePuckScale(Transform propRoot, Puck puck)
        {
            if (!(propRoot == null) && !(puck == null))
            {
                Vector3 lossyScale = puck.transform.lossyScale;
                propRoot.localScale = new Vector3((Mathf.Abs(lossyScale.x) < 0.0001f) ? 1f : (1f / lossyScale.x), (Mathf.Abs(lossyScale.y) < 0.0001f) ? 1f : (1f / lossyScale.y), (Mathf.Abs(lossyScale.z) < 0.0001f) ? 1f : (1f / lossyScale.z));
            }
        }

        private static void BuildFallbackMeshes()
        {
            if (!(_frame != null) || !(_netting != null))
            {
                float num = 0.6f;
                float num2 = 0.8f;
                float z = 0.55f;
                PropStyle.MeshBuilder meshBuilder = new PropStyle.MeshBuilder();
                meshBuilder.Tube(new Vector3(0f - num, 0f, 0f), new Vector3(0f - num, num2, 0f), 0.032f);
                meshBuilder.Tube(new Vector3(num, 0f, 0f), new Vector3(num, num2, 0f), 0.032f);
                meshBuilder.Tube(new Vector3(0f - num, num2, 0f), new Vector3(num, num2, 0f), 0.032f);
                meshBuilder.Tube(new Vector3(0f - num, num2, 0f), new Vector3(0f - num, num2 * 0.62f, z), 0.025600001f);
                meshBuilder.Tube(new Vector3(num, num2, 0f), new Vector3(num, num2 * 0.62f, z), 0.025600001f);
                meshBuilder.Tube(new Vector3(0f - num, num2 * 0.62f, z), new Vector3(num, num2 * 0.62f, z), 0.025600001f);
                meshBuilder.Tube(new Vector3(0f - num, num2 * 0.62f, z), new Vector3(0f - num, 0f, z), 0.025600001f);
                meshBuilder.Tube(new Vector3(num, num2 * 0.62f, z), new Vector3(num, 0f, z), 0.025600001f);
                meshBuilder.Tube(new Vector3(0f - num, 0f, z), new Vector3(num, 0f, z), 0.025600001f);
                meshBuilder.Tube(new Vector3(0f - num, 0f, 0f), new Vector3(0f - num, 0f, z), 0.025600001f);
                meshBuilder.Tube(new Vector3(num, 0f, 0f), new Vector3(num, 0f, z), 0.025600001f);
                _frame = meshBuilder.Build("MaxPractice_NetFrame");
                PropStyle.MeshBuilder meshBuilder2 = new PropStyle.MeshBuilder();
                AddPanel(meshBuilder2, new Vector3(0f - num, num2 * 0.62f, z), new Vector3(0f - num, 0f, z), new Vector3(num, 0f, z), new Vector3(num, num2 * 0.62f, z));
                AddPanel(meshBuilder2, new Vector3(0f - num, num2, 0f), new Vector3(0f - num, 0f, 0f), new Vector3(0f - num, 0f, z), new Vector3(0f - num, num2 * 0.62f, z));
                AddPanel(meshBuilder2, new Vector3(num, num2 * 0.62f, z), new Vector3(num, 0f, z), new Vector3(num, 0f, 0f), new Vector3(num, num2, 0f));
                AddPanel(meshBuilder2, new Vector3(0f - num, num2, 0f), new Vector3(0f - num, num2 * 0.62f, z), new Vector3(num, num2 * 0.62f, z), new Vector3(num, num2, 0f));
                _netting = meshBuilder2.Build("MaxPractice_NetMesh");
            }
        }

        private static void AddPanel(PropStyle.MeshBuilder b, Vector3 a, Vector3 c, Vector3 d, Vector3 e)
        {
            Vector3 vector = Vector3.Normalize(Vector3.Cross(c - a, d - a));
            b.Quad(a, c, d, e, vector);
            b.Quad(e, d, c, a, -vector);
        }

        public static void BuildColliders(GameObject root, Puck puck, int physicsLayer, List<Collider> into, bool cloned)
        {
            if (!cloned)
            {
                float num = 0.6f;
                AddBox(root, "PostL", physicsLayer, into, new Vector3(0f - num, 0.4f, 0f), new Vector3(0.064f, 0.8f, 0.064f), isTrigger: false);
                AddBox(root, "PostR", physicsLayer, into, new Vector3(num, 0.4f, 0f), new Vector3(0.064f, 0.8f, 0.064f), isTrigger: false);
                AddBox(root, "Crossbar", physicsLayer, into, new Vector3(0f, 0.8f, 0f), new Vector3(1.2f, 0.064f, 0.064f), isTrigger: false);
            }
            if (!cloned)
            {
                return;
            }
            Collider[] componentsInChildren = root.GetComponentsInChildren<Collider>(includeInactive: true);
            foreach (Collider collider in componentsInChildren)
            {
                if (collider != null && !into.Contains(collider))
                {
                    into.Add(collider);
                }
            }
        }

        private static bool TryMeasure(GameObject root, out Bounds bounds)
        {
            bounds = default(Bounds);
            bool any = false;
            MeshRenderer[] componentsInChildren = root.GetComponentsInChildren<MeshRenderer>(includeInactive: false);
            foreach (MeshRenderer meshRenderer in componentsInChildren)
            {
                if (meshRenderer != null && meshRenderer.enabled)
                {
                    Accumulate(root.transform, meshRenderer.bounds, ref bounds, ref any);
                }
            }
            if (!any)
            {
                return false;
            }
            if (bounds.size.x > 6f || bounds.size.y > 6f || bounds.size.z > 6f)
            {
                Debug.LogWarning("[MaxPractice] Ignoring implausible net bounds " + $"{bounds.size.x:0.00} x {bounds.size.y:0.00} x {bounds.size.z:0.00} m");
                return false;
            }
            if (bounds.size.x > 0.2f)
            {
                return bounds.size.y > 0.2f;
            }
            return false;
        }

        private static void Accumulate(Transform root, Bounds world, ref Bounds acc, ref bool any)
        {
            if (!(world.size.sqrMagnitude < 1E-08f))
            {
                Vector3 vector = root.InverseTransformPoint(world.min);
                Vector3 vector2 = root.InverseTransformPoint(world.max);
                Bounds bounds = new Bounds((vector + vector2) * 0.5f, new Vector3(Mathf.Abs(vector2.x - vector.x), Mathf.Abs(vector2.y - vector.y), Mathf.Abs(vector2.z - vector.z)));
                if (!any)
                {
                    acc = bounds;
                    any = true;
                }
                else
                {
                    acc.Encapsulate(bounds);
                }
            }
        }

        private static Collider AddBox(GameObject root, string name, int layer, List<Collider> into, Vector3 centre, Vector3 size, bool isTrigger)
        {
            Transform transform = root.transform.Find(name);
            GameObject gameObject;
            if (transform != null)
            {
                gameObject = transform.gameObject;
            }
            else
            {
                gameObject = new GameObject(name);
                gameObject.transform.SetParent(root.transform, worldPositionStays: false);
            }
            gameObject.layer = layer;
            BoxCollider boxCollider = gameObject.GetComponent<BoxCollider>();
            if (boxCollider == null)
            {
                boxCollider = gameObject.AddComponent<BoxCollider>();
            }
            boxCollider.center = centre;
            boxCollider.size = size;
            boxCollider.isTrigger = isTrigger;
            if (into != null && !into.Contains(boxCollider))
            {
                into.Add(boxCollider);
            }
            return boxCollider;
        }

        /// <summary>
        /// Destroy every live mini net first - see the note on ShooterAsset.Dispose. Extra
        /// bite here: a surviving MiniNetVisual keeps re-disabling the anchor puck's
        /// renderers and colliders every 2 s from LateUpdate, so the puck stayed invisible
        /// and intangible for good with the mod switched off.
        /// </summary>
        public static void Dispose()
        {
            try
            {
                var visuals = UnityEngine.Object.FindObjectsByType<MiniNetVisual>(FindObjectsSortMode.None);
                foreach (var visual in visuals)
                {
                    if (visual != null)
                        UnityEngine.Object.Destroy(visual.gameObject);
                }
            }
            catch { }

            DestroyMesh(ref _frame);
            DestroyMesh(ref _netting);
        }

        private static void DestroyMesh(ref Mesh m)
        {
            if (m != null)
            {
                try
                {
                    UnityEngine.Object.Destroy(m);
                }
                catch
                {
                }
            }
            m = null;
        }
    }
    /// <summary>
    /// Destroys a mesh this mod baked, when whatever carries it goes away. Attached only to
    /// per-spawn baked geometry, so it covers every teardown path - net replaced, anchor puck
    /// despawned, warmup cleanup - without any of them needing to know the mesh exists.
    /// </summary>
    public class OwnedMesh : MonoBehaviour
    {
        public Mesh Mesh;

        private void OnDestroy()
        {
            if (Mesh == null) return;
            try { UnityEngine.Object.Destroy(Mesh); } catch { }
            Mesh = null;
        }
    }

    public class BatchedPartDrawer : MonoBehaviour
    {
        public Mesh Mesh;

        public Material[] Materials;

        public int FirstSubMesh;

        public int SliceCount = 1;

        public Matrix4x4 Offset = Matrix4x4.identity;

        public int Layer;

        public ShadowCastingMode ShadowCasting = ShadowCastingMode.On;

        public bool ReceiveShadows = true;

        public Vector3 SourceExtents = Vector3.one;

        /// <summary>
        /// Colour overrides for this part, or null for none.
        ///
        /// A batched part is drawn by hand rather than by a Renderer, so it has no
        /// SetPropertyBlock to call - the override has to be handed to RenderParams here
        /// instead. Without it a statically batched piece is untintable, which on the real
        /// rink means the goal FRAME specifically: the game's log reports the goal as
        /// "5 mesh renderer(s), 1 statically batched, ... first 'Goal Frame'", and the
        /// other four are the materialless editor proxies BuildMeshParts skips.
        /// </summary>
        public MaterialPropertyBlock PropertyBlock;

        private void Awake()
        {
            // Nothing to draw on a dedicated server. The COMPONENT still has to exist -
            // BuildMeshParts' bounds feed the net's recentring - but issuing a RenderMesh per
            // submesh per frame into a null graphics device is pure waste.
            if (!PropStyle.HasGraphics) enabled = false;
        }

        private void LateUpdate()
        {
            if (Mesh == null || Materials == null || Materials.Length == 0)
            {
                return;
            }
            Matrix4x4 objectToWorld = base.transform.localToWorldMatrix * Offset;
            Bounds worldBounds = new Bounds(base.transform.position, SourceExtents * 2f * Mathf.Max(0.01f, base.transform.lossyScale.x));
            for (int i = 0; i < SliceCount; i++)
            {
                Material material = ((i < Materials.Length) ? Materials[i] : Materials[Materials.Length - 1]);
                if (!(material == null))
                {
                    int num = FirstSubMesh + i;
                    if (num >= 0 && num < Mesh.subMeshCount)
                    {
                        RenderParams renderParams = new RenderParams(material);
                        renderParams.layer = Layer;
                        renderParams.shadowCastingMode = ShadowCasting;
                        renderParams.receiveShadows = ReceiveShadows;
                        renderParams.lightProbeUsage = LightProbeUsage.BlendProbes;
                        renderParams.worldBounds = worldBounds;
                        renderParams.matProps = PropertyBlock;   // null = no overrides
                        RenderParams rparams = renderParams;
                        Graphics.RenderMesh(in rparams, Mesh, num, objectToWorld);
                    }
                }
            }
        }
    }
    public class MiniNetVisual : MonoBehaviour
    {
        private const float ReassertInterval = 2f;

        private Puck _puck;

        private Transform _puckTransform;

        /// <summary>Clearance above the ice, just enough to avoid z-fighting the surface.</summary>
        private const float GroundClearance = 0.002f;

        /// <summary>See <see cref="ShooterVisual.AnchorClearance"/>.</summary>
        private const float AnchorClearance = ShooterVisual.AnchorClearance;

        private float _nextReassert;

        private bool _ownsCollision;

        private bool _cloned;

        private readonly List<Renderer> _hidden = new List<Renderer>();

        private readonly List<Collider> _disabled = new List<Collider>();

        private readonly List<Collider> _mine = new List<Collider>();

        private readonly HashSet<Collider> _ignoredAgainst = new HashSet<Collider>();

        public void Init(Puck puck, bool withCollision, bool cloned)
        {
            _puck = puck;
            _ownsCollision = withCollision;
            _cloned = cloned;
            if (withCollision) PlayerColliderCache.Invalidate();
            _puckTransform = ((puck != null) ? puck.transform : null);
            if (!(_puckTransform == null))
            {
                if (_ownsCollision)
                {
                    MiniNetAsset.BuildColliders(base.gameObject, puck, puck.gameObject.layer, _mine, _cloned);
                }
                Reassert();
                Snap();
            }
        }

        private void LateUpdate()
        {
            if (_puckTransform == null)
            {
                UnityEngine.Object.Destroy(base.gameObject);
                return;
            }
            Snap();
            if (Time.unscaledTime >= _nextReassert)
            {
                _nextReassert = Time.unscaledTime + 2f;
                Reassert();
            }
        }

        private void Snap()
        {
            Vector3 position = _puckTransform.position;

            // Ice level from the anchor puck's own height - see the note in
            // ShooterVisual.Snap for why this is not a RinkSheets lookup.
            float iceY = position.y - AnchorClearance;

            Vector3 vector = new Vector3(position.x, iceY + GroundClearance, position.z);
            if ((base.transform.position - vector).sqrMagnitude > 1E-08f)
            {
                base.transform.position = vector;
            }
            Quaternion quaternion = Quaternion.Euler(0f, _puckTransform.rotation.eulerAngles.y, 0f);
            if (Quaternion.Angle(base.transform.rotation, quaternion) > 0.05f)
            {
                base.transform.rotation = quaternion;
            }
        }

        private GameObject _clone;
        private MeshRenderer _frameRenderer;   // fallback net only
        private bool _tintWarned;

        // PER INSTANCE, not static. BatchedPartDrawer keeps the block by REFERENCE and reads
        // it at draw time (RenderParams.matProps does not copy, unlike Renderer.SetPropertyBlock),
        // so one shared block meant every batched net frame drew whichever team's colour was
        // applied last - and PropNetwork re-announces all of them together every 5 s.
        private MaterialPropertyBlock _tintBlock;

        /// <summary>Remember the cloned game-net root so a later owner resolve can tint it.</summary>
        public void BindClone(GameObject clone)
        {
            _clone = clone;
        }

        /// <summary>Remember the FALLBACK net's frame so a later owner resolve can recolour it.</summary>
        public void BindFrame(GameObject frame)
        {
            _frameRenderer = frame != null ? frame.GetComponent<MeshRenderer>() : null;
        }

        /// <summary>
        /// Team colour and nameplate. Safe to call repeatedly - PropNetwork re-announces
        /// every 5 s, and this is how a net built before its owner replicated gets both.
        ///
        /// The colouring has to work two different ways because the net is built two
        /// different ways. The FALLBACK net is our own mesh with our own material, so its
        /// frame is simply swapped for the team's. The CLONED net reuses the GAME'S
        /// materials - shared with the real goals on the ice - so touching them would
        /// repaint the actual nets. That path gets a MaterialPropertyBlock instead, which
        /// overrides the colour per-renderer and leaves the shared material untouched.
        ///
        /// Only the frame is tinted, not the netting: a solid team-coloured mesh panel
        /// reads as a wall rather than a net. Parts are matched by name, and if nothing
        /// matches (a reskin, or a goal built differently) every part is tinted rather than
        /// silently leaving the net uncoloured.
        /// </summary>
        public void ApplyOwner(Player owner)
        {
            if (owner == null) return;

            PropLabel.Attach(base.gameObject, owner);

            PlayerTeam team = PracticeHelpers.GetPlayerTeam(owner);

            // Fallback net: the frame mesh and material are ours, so re-point it. This has
            // to happen on EVERY call, not just at build time - the first announcement
            // usually resolves no owner (their Player object has not replicated yet) so the
            // frame is built in the default red, and a team switch has to move it too.
            if (_clone == null)
            {
                if (_frameRenderer != null)
                {
                    var material = MiniNetAsset.FrameMaterialFor(owner);
                    if (material != null && _frameRenderer.sharedMaterial != material)
                        _frameRenderer.sharedMaterial = material;
                }
                return;
            }

            try
            {
                Color color = PropStyle.TeamColor(team);

                _tintBlock = _tintBlock ?? new MaterialPropertyBlock();
                _tintBlock.Clear();
                _tintBlock.SetColor("_BaseColor", color);
                _tintBlock.SetColor("_Color", color);

                // BOTH kinds of part. A statically batched piece has no MeshRenderer at all
                // - BuildMeshParts gives it a BatchedPartDrawer instead - and on the real
                // rink the goal FRAME is exactly that, the one batched renderer of the five.
                // Collecting only MeshRenderers therefore missed the only thing worth
                // colouring and left just the netting, which the fallback below then painted
                // solid: the precise opposite of the intent.
                int tinted = 0;

                var batched = _clone.GetComponentsInChildren<BatchedPartDrawer>(includeInactive: true);
                foreach (var part in batched)
                {
                    if (part == null || !IsFramePart(part.name)) continue;
                    part.PropertyBlock = _tintBlock;
                    tinted++;
                }

                var renderers = _clone.GetComponentsInChildren<MeshRenderer>(includeInactive: true);
                foreach (var renderer in renderers)
                {
                    if (renderer == null || !IsFramePart(renderer.name)) continue;
                    renderer.SetPropertyBlock(_tintBlock);
                    tinted++;
                }

                // Deliberately no "tint everything" fallback. The only parts that fail the
                // frame test are the netting, and painting the fabric a flat opaque team
                // colour turns the net into a solid wall - worse than an uncoloured net,
                // which the nameplate still identifies.
                if (tinted == 0 && !_tintWarned)
                {
                    _tintWarned = true;
                    Debug.LogWarning("[MaxPractice] Mini net: no frame part to carry the team colour; " +
                                     "leaving it in the goal's own colours.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MaxPractice] Could not tint the mini net: " + ex.Message);
            }
        }

        /// <summary>
        /// Frame, or fabric? Only the frame gets the team colour.
        ///
        /// Puck's goal netting is named to match the "Net" collider group and the baked
        /// netting parts are named after their SkinnedMeshRenderer, while the frame is
        /// 'Goal Frame' - so excluding "net"/"mesh" keeps the fabric out and lets the frame
        /// through.
        /// </summary>
        private static bool IsFramePart(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf("net", StringComparison.OrdinalIgnoreCase) < 0
                && name.IndexOf("mesh", StringComparison.OrdinalIgnoreCase) < 0;
        }

        public void Reassert()
        {
            if (_puck == null)
            {
                return;
            }
            if (PropStyle.HasGraphics)
            {
                Renderer[] componentsInChildren = _puck.GetComponentsInChildren<Renderer>(includeInactive: true);
                foreach (Renderer renderer in componentsInChildren)
                {
                    if (!(renderer == null) && renderer.enabled && !renderer.transform.IsChildOf(base.transform))
                    {
                        renderer.enabled = false;
                        if (!_hidden.Contains(renderer))
                        {
                            _hidden.Add(renderer);
                        }
                    }
                }
            }
            if (!_ownsCollision)
            {
                return;
            }
            Collider[] componentsInChildren2 = _puck.GetComponentsInChildren<Collider>(includeInactive: true);
            foreach (Collider collider in componentsInChildren2)
            {
                if (!(collider == null) && collider.enabled && !collider.transform.IsChildOf(base.transform))
                {
                    collider.enabled = false;
                    if (!_disabled.Contains(collider))
                    {
                        _disabled.Add(collider);
                    }
                }
            }
            try
            {
                _ignoredAgainst.RemoveWhere((Collider c) => c == null);

                // One shared sweep for every prop - see PlayerColliderCache.
                Ignore(PlayerColliderCache.Get());
            }
            catch
            {
            }
        }

        private void Ignore(IReadOnlyList<Collider> others)
        {
            if (others == null)
            {
                return;
            }
            foreach (Collider collider in others)
            {
                if (collider == null || !collider.enabled || !_ignoredAgainst.Add(collider))
                {
                    continue;
                }
                foreach (Collider item in _mine)
                {
                    if (item != null && item.enabled)
                    {
                        Physics.IgnoreCollision(item, collider, ignore: true);
                    }
                }
            }
        }

        private void OnDestroy()
        {
            foreach (Renderer item in _hidden)
            {
                if (item != null)
                {
                    item.enabled = true;
                }
            }
            foreach (Collider item2 in _disabled)
            {
                if (item2 != null)
                {
                    item2.enabled = true;
                }
            }
            _hidden.Clear();
            _disabled.Clear();
            _mine.Clear();
            _ignoredAgainst.Clear();
        }
    }
}
