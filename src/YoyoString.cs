// YoyoString.cs - the string between a yoyo player's stick blade and their puck.
//
// Unlike a cone, a shooter or a mini net, this is not a mesh bolted onto a frozen
// anchor puck. Both of its ends move every frame and they belong to two different
// networked objects, so there is nothing to bake: it is a LineRenderer that is
// re-laid-out in LateUpdate from wherever the blade and the puck happen to be.
//
// That is also why it needs its own replication (YoyoStringNetwork) rather than
// riding PropNetwork: a prop announcement is (puck -> kind), and a string is a PAIR
// of objects, so a client has to be told which player is on the other end.
//
// The GameObject is parented to the puck the way the other props are, so a despawned
// puck takes its string with it without anyone having to notice.

using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxPractice
{
    /// <summary>Shared material and the one place a string gets built.</summary>
    public static class YoyoStringAsset
    {
        public const string ObjectName = "MaxPractice_YoyoString";

        /// <summary>
        /// Black. Not literally 0,0,0 - a hair of lift keeps it from vanishing into the
        /// boards and the shadowed end of the rink, and at this value it still reads as
        /// black everywhere else.
        /// </summary>
        private static readonly Color StringColor = new Color(0.04f, 0.04f, 0.04f, 1f);

        private static Material _material;

        /// <summary>
        /// Unlit by preference. A string is a two-triangle-wide ribbon facing the camera;
        /// a lit shader gives it a shading gradient along its length that reads as the
        /// string changing colour as the puck moves, which looks worse than no shading.
        /// </summary>
        private static Shader ResolveShader()
        {
            string[] names =
            {
                "Universal Render Pipeline/Unlit",
                "Unlit/Color",
                "Sprites/Default",
                "Universal Render Pipeline/Simple Lit",
            };

            for (int i = 0; i < names.Length; i++)
            {
                var shader = Shader.Find(names[i]);
                if (shader != null) return shader;
            }
            return null;
        }

        public static Material Material
        {
            get
            {
                if (_material != null) return _material;

                var shader = ResolveShader();
                if (shader == null) return null;

                _material = new Material(shader)
                {
                    name = "MP_YoyoString",
                    hideFlags = HideFlags.HideAndDontSave
                };
                if (_material.HasProperty("_BaseColor")) _material.SetColor("_BaseColor", StringColor);
                if (_material.HasProperty("_Color")) _material.SetColor("_Color", StringColor);

                return _material;
            }
        }

        /// <summary>
        /// Put a string on <paramref name="puck"/>, running to <paramref name="owner"/>'s blade.
        /// Re-binding an existing one rather than rebuilding keeps the puck from
        /// accumulating a string per announcement.
        /// </summary>
        public static YoyoStringVisual Attach(Player owner, Puck puck)
        {
            if (puck == null || owner == null) return null;
            if (!PropStyle.HasGraphics) return null;

            try
            {
                var existing = puck.GetComponentInChildren<YoyoStringVisual>(includeInactive: true);
                if (existing != null)
                {
                    existing.Bind(owner, puck);
                    return existing;
                }

                var material = Material;
                if (material == null) return null;

                var go = new GameObject(ObjectName);
                go.transform.SetParent(puck.transform, worldPositionStays: false);

                var visual = go.AddComponent<YoyoStringVisual>();
                visual.Bind(owner, puck);
                return visual;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MaxPractice] Could not attach the yoyo string: " + ex.Message);
                return null;
            }
        }

        /// <summary>Tear down every live string, then drop the shared material.</summary>
        public static void Dispose()
        {
            try
            {
                var visuals = UnityEngine.Object.FindObjectsByType<YoyoStringVisual>(FindObjectsSortMode.None);
                foreach (var visual in visuals)
                {
                    if (visual != null) UnityEngine.Object.Destroy(visual.gameObject);
                }
            }
            catch { }

            if (_material != null)
            {
                try { UnityEngine.Object.Destroy(_material); } catch { }
                _material = null;
            }
        }
    }

    /// <summary>
    /// A verlet-simulated string, not a curve drawn between two points.
    ///
    /// The parabola this started as had no lag: swing the blade left and every point of
    /// the string was already on the new straight line that same frame, which reads as a
    /// rigid rod. Real string trails - the far end keeps going while the near end has
    /// already turned, then whips back. Verlet gives that for free: each node carries its
    /// own momentum in (position - previousPosition), the two ends are pinned to the blade
    /// and the puck, and a few relaxation passes stop the segments stretching. The lateral
    /// lag the parabola was missing IS that momentum.
    /// </summary>
    public class YoyoStringVisual : MonoBehaviour
    {
        /// <summary>
        /// Nodes including both pinned ends; segments = Nodes - 1. Raised along with the
        /// slack: a hanging loop is a real curve to describe, and 16 straight chords made
        /// a deep drape look faceted. More nodes also means a constraint correction takes
        /// more frames to walk the length, which adds to the trailing.
        /// </summary>
        private const int Nodes = 24;

        private const float Width = 0.022f;

        /// <summary>
        /// Velocity kept per 60 Hz frame. Lower = the string settles sooner.
        ///
        /// Expressed per-frame because that is how it was tuned, but it is APPLIED per
        /// second (see Simulate). Used raw it would make the string's feel depend on
        /// framerate: 0.93 a frame is 0.013 a second at 60 fps but 0.00003 a second at
        /// 144 fps, so a high-refresh client would see the trailing die roughly twice as
        /// fast and the string would read as stiffer on a better machine.
        /// </summary>
        private const float Damping = 0.93f;

        /// <summary>The rate <see cref="Damping"/> is quoted at.</summary>
        private const float DampingReferenceHz = 60f;

        private const float Gravity = -9.81f;

        /// <summary>
        /// Relaxation passes. Enough that a taut string doesn't visibly stretch under a
        /// hard yank; more than this just costs time nobody can see.
        /// </summary>
        private const int Relaxations = 6;

        /// <summary>
        /// How much longer the string is than the straight span, and the ceiling on it.
        ///
        /// This is THE dial for how alive the string looks. A near-taut string genuinely
        /// does follow the blade almost rigidly - all the trailing and swing you see on a
        /// real one comes from the slack - so too small and the simulation is invisible
        /// however good it is. Both ends sit on the ice, so the slack cannot hang: it
        /// buckles sideways and bows across the ice instead.
        ///
        /// Tuned in game. 0.22 / 2 m read as too loose - a lot of rope bowing over the ice
        /// - so this is half of that, which keeps the swing and trailing without the string
        /// looking dropped rather than held.
        /// </summary>
        private const float SlackFraction = 0.08f;
        private const float MaxSlack = 0.90f;

        /// <summary>Longest step the simulation will take. Verlet with a variable timestep
        /// goes unstable on a frame spike, and a stringy artefact is not worth a hitch.</summary>
        private const float MaxStep = 1f / 30f;

        /// <summary>
        /// A one-frame jump in the puck's speed this large reads as an impulse rather than
        /// ordinary sliding - i.e. the yank. Detected here rather than announced, because
        /// LaunchPuckToPlayer sets the puck's velocity outright and the resulting speed
        /// change is replicated to every client anyway: no message needed for the string to
        /// react on the same frame everyone sees the puck leave.
        /// </summary>
        private const float YankSpeedJump = 6f;

        /// <summary>Dead taut for this long after a yank, then eased back over the next.</summary>
        private const float TautHoldSeconds = 0.18f;
        private const float TautEaseSeconds = 0.5f;

        /// <summary>An end moving further than this in one frame is a teleport, not a
        /// swing - respawn, a /rink move, a puck being repositioned - so the string is
        /// re-laid rather than whipped across the map.</summary>
        private const float TeleportDistance = 6f;

        private Player _owner;
        private Puck _puck;
        private LineRenderer _line;

        private readonly Vector3[] _pos = new Vector3[Nodes];
        private readonly Vector3[] _prev = new Vector3[Nodes];
        private bool _settled;

        // Puck motion history, for spotting the yank.
        private Vector3 _lastPuckPos;
        private float _lastPuckSpeed;
        private bool _havePuckHistory;
        private float _tautAt = float.NegativeInfinity;

        /// <summary>
        /// Point the string at a blade and a puck.
        ///
        /// Only resets the simulation when the PAIR actually changes. Re-laying it straight
        /// on every call was what made the whole thing render as a rigid line: on a listen
        /// host YoyoStringNetwork.ApplySet runs once a frame, so Bind was called once a
        /// frame, so the rope was reset to a straight line before it ever got a second
        /// frame to integrate.
        /// </summary>
        public void Bind(Player owner, Puck puck)
        {
            bool changed = _owner != owner || _puck != puck;

            _owner = owner;
            _puck = puck;
            EnsureLine();

            if (!changed) return;

            _settled = false;
            Redraw();
        }

        private void EnsureLine()
        {
            if (_line != null) return;

            _line = gameObject.GetComponent<LineRenderer>();
            if (_line == null) _line = gameObject.AddComponent<LineRenderer>();

            _line.useWorldSpace = true;            // both ends live on other objects
            _line.positionCount = Nodes;
            _line.startWidth = Width;
            _line.endWidth = Width;
            _line.numCapVertices = 0;
            _line.numCornerVertices = 0;
            _line.alignment = LineAlignment.View;  // a string has no thickness to orient
            _line.textureMode = LineTextureMode.Stretch;
            _line.shadowCastingMode = ShadowCastingMode.Off;
            _line.receiveShadows = false;
            _line.lightProbeUsage = LightProbeUsage.Off;
            _line.reflectionProbeUsage = ReflectionProbeUsage.Off;
            _line.sharedMaterial = YoyoStringAsset.Material;
        }

        private void LateUpdate()
        {
            // The string is parented to the puck, so a despawn takes it with us; this
            // covers the puck being destroyed without the parent chain going first.
            if (_puck == null)
            {
                UnityEngine.Object.Destroy(gameObject);
                return;
            }

            Redraw();
        }

        private void Redraw()
        {
            if (_line == null) return;

            // No owner, or their character is not spawned: keep the object (the pairing is
            // still live server-side and they may respawn) but draw nothing, rather than
            // anchoring the string to the world origin. Drop _settled so it is re-laid from
            // wherever the blade turns up rather than whipped there from the old position.
            if (!TryGetBlade(out Vector3 blade))
            {
                if (_line.enabled) _line.enabled = false;
                _settled = false;
                return;
            }

            if (!_line.enabled) _line.enabled = true;

            Vector3 puckPos = _puck.transform.position;

            if (!_settled || IsTeleport(blade, puckPos))
            {
                LayStraight(blade, puckPos);
            }
            else
            {
                Simulate(blade, puckPos);
            }

            _line.SetPositions(_pos);
        }

        /// <summary>
        /// Reset to a near-straight line with no stored motion.
        ///
        /// "Near", not exactly: the interior nodes get a millimetre of alternating sideways
        /// offset. A perfectly collinear rope is an equilibrium the distance constraints
        /// will happily keep - segments shorter than their rest length just push each other
        /// further apart ALONG the line, with no reason to leave it - so a dead-straight
        /// start plus a floor that blocks gravity leaves the slack nowhere to buckle and
        /// the string stays a ruler. This seed decides which way it bows; the simulation
        /// takes it from there.
        /// </summary>
        private void LayStraight(Vector3 blade, Vector3 puckPos)
        {
            Vector3 span = puckPos - blade;
            Vector3 sideways = Vector3.Cross(span.sqrMagnitude > 1e-6f ? span.normalized : Vector3.forward,
                                             Vector3.up);
            if (sideways.sqrMagnitude < 1e-6f) sideways = Vector3.right;
            sideways = sideways.normalized * 0.001f;

            for (int i = 0; i < Nodes; i++)
            {
                _pos[i] = Vector3.Lerp(blade, puckPos, (float)i / (Nodes - 1));
                if (i > 0 && i < Nodes - 1) _pos[i] += sideways * ((i % 2 == 0) ? 1f : -1f);
                _prev[i] = _pos[i];
            }
            _settled = true;
            _havePuckHistory = false;   // a re-lay must not read as an impulse
            _tautAt = float.NegativeInfinity;
        }

        /// <summary>
        /// An end that jumped further than a swing could carry it. Checked against the
        /// node that was pinned to it last frame, so it catches both a teleported player
        /// and a repositioned puck.
        /// </summary>
        private bool IsTeleport(Vector3 blade, Vector3 puckPos)
        {
            return (blade - _pos[0]).sqrMagnitude > TeleportDistance * TeleportDistance
                || (puckPos - _pos[Nodes - 1]).sqrMagnitude > TeleportDistance * TeleportDistance;
        }

        private void Simulate(Vector3 blade, Vector3 puckPos)
        {
            float dt = Mathf.Min(Time.deltaTime, MaxStep);
            if (dt <= 0f) return;

            // The TRUE frame time, not the clamped integration step. Dividing a full
            // frame's displacement by the clamped dt inflated the measured speed by
            // realDt/MaxStep, so any hitch longer than 33 ms read as a yank and snapped the
            // string taut with nothing having pulled it.
            TrackPuckImpulse(puckPos, Mathf.Max(1e-4f, Time.deltaTime));

            // Interior nodes only - the ends are driven by the blade and the puck.
            float damp = Mathf.Pow(Damping, dt * DampingReferenceHz);

            Vector3 accel = new Vector3(0f, Gravity * dt * dt, 0f);
            for (int i = 1; i < Nodes - 1; i++)
            {
                Vector3 velocity = (_pos[i] - _prev[i]) * damp;
                _prev[i] = _pos[i];
                _pos[i] += velocity + accel;
            }

            // Pin the ends. Their _prev follows them so the pinning itself injects no
            // velocity - the string is dragged along by the constraint passes below, which
            // is exactly where the trailing comes from.
            _pos[0] = blade;
            _prev[0] = blade;
            _pos[Nodes - 1] = puckPos;
            _prev[Nodes - 1] = puckPos;

            float span = Vector3.Distance(blade, puckPos);

            // A yanked string snaps straight before it drags the puck. Pull the slack out
            // for a moment, then let it back in, so the pull reads as a pull.
            float slack = Mathf.Min(MaxSlack, span * SlackFraction) * Tautness();
            float segment = (span + slack) / (Nodes - 1);

            // The floor is the ICE, not the lower of the two anchors.
            //
            // Anchoring it to the endpoints was self-defeating: a blade a few centimetres
            // off the ice and a puck sitting on it meant the clamp allowed about 2 cm of
            // total sag, so gravity was cancelled the frame after it acted and the rope
            // could never leave the straight line between its ends. IceHeightAt is a layout
            // lookup rather than a raycast, so one call a frame at the midpoint is cheap,
            // and using the midpoint keeps it right on a practice sheet as well as the
            // arena rink.
            Vector3 middle = (blade + puckPos) * 0.5f;
            float floor = RinkSheets.IceHeightAt(middle, 0.01f);

            for (int pass = 0; pass < Relaxations; pass++)
            {
                for (int i = 0; i < Nodes - 1; i++)
                {
                    Vector3 delta = _pos[i + 1] - _pos[i];
                    float length = delta.magnitude;
                    if (length < 1e-6f) continue;

                    float correction = (length - segment) / length * 0.5f;
                    Vector3 shift = delta * correction;

                    // A pinned end cannot move, so its neighbour takes the whole correction.
                    bool firstPinned = i == 0;
                    bool secondPinned = i + 1 == Nodes - 1;

                    if (firstPinned && secondPinned) continue;
                    if (firstPinned) { _pos[i + 1] -= shift * 2f; }
                    else if (secondPinned) { _pos[i] += shift * 2f; }
                    else { _pos[i] += shift; _pos[i + 1] -= shift; }
                }

                for (int i = 1; i < Nodes - 1; i++)
                {
                    if (_pos[i].y < floor) _pos[i].y = floor;
                }
            }

            // Kill the vertical velocity of anything resting on the floor. Without this the
            // clamp leaves _prev below _pos, so the node re-reads that as upward motion,
            // gravity pulls it back, and a settled string buzzes about a centimetre a frame.
            for (int i = 1; i < Nodes - 1; i++)
            {
                if (_pos[i].y <= floor + 1e-4f) _prev[i].y = _pos[i].y;
            }
        }

        /// <summary>
        /// Watch the puck for the velocity step the yank applies. Speed alone is not enough
        /// - a puck sliding fast across the ice is not being pulled - so it is the CHANGE
        /// in speed over one frame that trips it.
        /// </summary>
        private void TrackPuckImpulse(Vector3 puckPos, float dt)
        {
            if (_havePuckHistory)
            {
                float speed = (puckPos - _lastPuckPos).magnitude / dt;
                if (speed - _lastPuckSpeed > YankSpeedJump)
                    _tautAt = Time.unscaledTime;
                _lastPuckSpeed = speed;
            }

            _lastPuckPos = puckPos;
            _havePuckHistory = true;
        }

        /// <summary>0 = dead taut, 1 = the string's normal slack.</summary>
        private float Tautness()
        {
            float since = Time.unscaledTime - _tautAt;
            if (since < 0f || float.IsInfinity(since)) return 1f;
            if (since < TautHoldSeconds) return 0f;

            float t = (since - TautHoldSeconds) / TautEaseSeconds;
            return t >= 1f ? 1f : Mathf.SmoothStep(0f, 1f, t);
        }

        private bool TryGetBlade(out Vector3 blade)
        {
            blade = Vector3.zero;
            if (_owner == null) return false;

            var stick = _owner.Stick;
            if (stick == null) return false;

            blade = stick.BladeHandlePosition;
            return true;
        }
    }
}
