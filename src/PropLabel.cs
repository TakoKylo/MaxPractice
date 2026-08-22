// PropLabel.cs - the floating name over a prop, so you can tell whose shooter is whose.
//
// Modelled on OpenWorldPracticeMod's FloatingWorldText (src/UI/FloatingWorldText.cs), which
// is the mod suite's proven world-space text: a world-space TextMeshPro, screen-aligned
// every LateUpdate, biased toward the camera so it clears the surface it sits on, with a
// black outline so it stays legible against the ice and a legacy TextMesh + URP/Unlit
// fallback for when no TMP font asset can be resolved.
//
// Differences from OWP's version, all because this label is a persistent fixture rather
// than a transient "+57" pop:
//   * no rise/fade/lifetime - it lives and dies with the prop it is parented to;
//   * no tag registry - the prop owns exactly one, found via GetComponentInChildren;
//   * it re-reads the owner's Username and Team every refresh instead of being handed a
//     baked string, so a rename or a team switch is picked up with nothing re-announced.
//
// Like OWP's, the label is NOT parented to the thing it labels - it follows it in world
// space. Parenting it was tried and was wrong twice over: prop roots carry
// NeutralisePuckScale's 1/puckScale correction, which is a LARGE multiplier (a puck is
// small), so an inherited scale blew the text up to metres tall; and a prop root's origin
// is not its visual centre, because TryCloneGameNet re-centres the net within the root, so
// the name sat off to one side of the net it named.

using System;
using TMPro;
using UnityEngine;

namespace MaxPractice
{
    public class PropLabel : MonoBehaviour
    {
        public const string ObjectName = "MaxPractice_PropLabel";

        /// <summary>Clearance above the tallest part of the prop.</summary>
        private const float HeadroomMetres = 0.28f;

        /// <summary>Used when a prop has nothing measurable to sit above.</summary>
        private const float FallbackHeight = 0.95f;

        private const float TmpFontSize = 36f;
        private const float WorldScale = 0.075f;

        /// <summary>
        /// Pull toward the camera so the text clears the machine it labels instead of
        /// half-disappearing into the chassis. Straight from OWP's CameraBiasMeters - the
        /// text quad has real extent, so sitting it exactly at the anchor lets the prop
        /// geometry intersect it from most angles.
        /// </summary>
        private const float CameraBiasMeters = 0.25f;

        /// <summary>Re-reading two NetworkVariables and a string compare, four times a
        /// second, is plenty to catch a rename or a team switch.</summary>
        private const float RefreshInterval = 0.25f;

        private static TMP_FontAsset _font;
        private static bool _fontResolved;

        // Labels are unparented, so GetComponentInChildren can no longer find a prop's own.
        // Keyed by the prop root, the way OWP keys its persistent labels by tag.
        private static readonly System.Collections.Generic.Dictionary<Transform, PropLabel> _byProp
            = new System.Collections.Generic.Dictionary<Transform, PropLabel>();

        private TextMeshPro _tmp;
        private TextMesh _legacy;
        private MeshRenderer _legacyRenderer;
        private Material _legacyMaterial;
        private Material _tmpMaterial;

        private Player _owner;
        private Camera _cam;
        // Measured once at attach: how far above the prop's own origin to float. A fixed
        // value cannot serve both props - the shooter chassis tops out around 0.7 m while a
        // mini net at full MiniNetScale is roughly twice that, so a height that clears one
        // sits inside the other.
        private float _height = FallbackHeight;

        // The prop we hover over, and the top-centre of its visible geometry in that prop's
        // LOCAL space - so the label tracks the prop as it moves AND as it rotates.
        private Transform _follow;
        private Vector3 _localTop;
        private bool _haveLocalTop;
        private float _nextRefresh;
        private string _shownName;
        private string _lastRawName;
        private string _sanitisedName;
        private Color _shownColor = Color.clear;

        /// <summary>Attach (or re-point) the label on a prop's visual root.</summary>
        public static PropLabel Attach(GameObject propRoot, Player owner)
        {
            if (propRoot == null || owner == null) return null;
            if (!PropStyle.HasGraphics) return null;

            try
            {
                var root = propRoot.transform;

                if (_byProp.TryGetValue(root, out var existing))
                {
                    if (existing != null)
                    {
                        existing.SetOwner(owner);
                        return existing;
                    }
                    _byProp.Remove(root);   // stale entry, object destroyed elsewhere
                }

                // Unparented: world scale is exactly WorldScale whatever the prop root is
                // scaled to, and the anchor is computed from the prop's visible bounds
                // rather than assumed to be its origin.
                var go = new GameObject(ObjectName);

                // Build/SetOwner reach into TMP and the renderer and can throw. The outer
                // catch logged that and returned, but `go` had already been created and was
                // not yet in _byProp, so nothing owned it and nothing would ever destroy it -
                // and Attach is re-entered on every 5 s prop re-announce, so a persistent
                // failure leaked one abandoned GameObject every five seconds for the rest of
                // the session.
                try
                {
                    var label = go.AddComponent<PropLabel>();
                    label._follow = root;
                    label._haveLocalTop = TryMeasureLocalTop(propRoot, out label._localTop);
                    label.Build();
                    label.SetOwner(owner);

                    _byProp[root] = label;
                    return label;
                }
                catch
                {
                    UnityEngine.Object.Destroy(go);
                    throw;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MaxPractice] Could not attach the prop label: " + ex.Message);
                return null;
            }
        }

        /// <summary>Tear down every label. Unparented, so prop teardown does not do it.</summary>
        public static void DestroyAll()
        {
            try
            {
                var all = UnityEngine.Object.FindObjectsByType<PropLabel>(FindObjectsSortMode.None);
                foreach (var label in all)
                {
                    if (label != null) UnityEngine.Object.Destroy(label.gameObject);
                }
            }
            catch { }
            _byProp.Clear();
        }

        public void SetOwner(Player owner)
        {
            _owner = owner;
            _nextRefresh = 0f;
            Refresh();
        }

        private void Build()
        {
            EnsureFont();

            if (_font != null)
            {
                _tmp = gameObject.AddComponent<TextMeshPro>();
                _tmp.font = _font;
                _tmp.fontSize = TmpFontSize;
                _tmp.alignment = TextAlignmentOptions.Center;
                _tmp.textWrappingMode = TextWrappingModes.NoWrap;
                _tmp.overflowMode = TextOverflowModes.Overflow;
                _tmp.fontStyle = FontStyles.Bold;
                _tmp.raycastTarget = false;

                // The ONLY string this ever renders is a Steam display name the player
                // chooses, and TMP parses markup by default. Without this, "<size=1200%>x"
                // as a username draws a giant glyph over the rink on everyone's screen, and
                // "<color=#fff>" defeats the team colour this feature exists to show. The
                // TextMesh branch below was already hardened; this one is the branch that
                // actually runs. (OWP's original is fed server-composed strings like "+57",
                // so it never had untrusted input to guard against.)
                _tmp.richText = false;

                _tmp.outlineColor = Color.black;
                _tmp.outlineWidth = 0.18f;
                // outlineWidth assigns through Renderer.material, which instances a
                // material TMP's own OnDestroy does not free. Grab it now so ours can.
                _tmpMaterial = _tmp.fontMaterial;

                _tmp.rectTransform.sizeDelta = new Vector2(8f, 2f);
                transform.localScale = Vector3.one * WorldScale;
            }
            else
            {
                _legacy = gameObject.AddComponent<TextMesh>();

                // A TextMesh added at RUNTIME has no font - Unity only wires up the builtin
                // when the component is created from the editor menu - and with a null font
                // it produces no mesh and the auto-added MeshRenderer has no material, so
                // FixLegacyMaterial bails and the label is silently invisible. The whole
                // fallback was a no-op without this (OWP's has the same gap).
                try
                {
                    var arial = Resources.GetBuiltinResource<Font>("Arial.ttf");
                    if (arial != null)
                    {
                        _legacy.font = arial;
                        var mr = GetComponent<MeshRenderer>();
                        if (mr != null) mr.sharedMaterial = arial.material;
                    }
                }
                catch { }

                _legacy.anchor = TextAnchor.MiddleCenter;
                _legacy.alignment = TextAlignment.Center;
                _legacy.fontSize = 64;
                _legacy.characterSize = 0.05f;
                _legacy.fontStyle = FontStyle.Bold;
                _legacy.richText = false;
                _legacyRenderer = GetComponent<MeshRenderer>();
                FixLegacyMaterial();
                transform.localScale = Vector3.one;
            }
        }

        private void LateUpdate()
        {
            // The prop is gone. Unparented labels do not die with it, so this is what ends
            // them - covers a cleared prop, a phase wipe and a plugin disable alike.
            if (_follow == null)
            {
                UnityEngine.Object.Destroy(gameObject);
                return;
            }

            // The owner left, or their object went away: no name to show.
            if (_owner == null)
            {
                SetVisible(false);
                return;
            }

            if (Time.unscaledTime >= _nextRefresh)
            {
                _nextRefresh = Time.unscaledTime + RefreshInterval;
                // Camera.main is two native calls (the tag lookup plus the alive-check) and
                // re-runs the whole FindGameObjectsWithTag scan whenever nothing is tagged.
                // It changes about once a match, so the 4 Hz tick is plenty; only a null
                // cache forces an immediate re-resolve above.
                ResolveCamera();
                Refresh();
            }

            if (_cam == null) ResolveCamera();
            if (_cam == null) return;

            // Sit above the prop, then step toward the camera so the chassis cannot
            // intersect the text quad. Never step more than halfway there, or a camera
            // right on top of the prop would pull the label behind itself.
            //
            // TransformPoint, not a world offset: the anchor is stored in the prop's local
            // space so the label stays over the net's middle as the prop rotates, instead
            // of swinging out to one side.
            Vector3 anchor = _haveLocalTop
                ? _follow.TransformPoint(_localTop)
                : _follow.position + Vector3.up * _height;

            anchor += Vector3.up * HeadroomMetres;

            Vector3 toCam = _cam.transform.position - anchor;
            float dist = toCam.magnitude;
            if (dist > 1e-4f)
                anchor += (toCam / dist) * Mathf.Min(CameraBiasMeters, dist * 0.5f);

            transform.position = anchor;

            // Match the CAMERA's orientation rather than looking at it, so the text stays
            // upright and unskewed when the label is off to the side of the screen.
            transform.rotation = Quaternion.LookRotation(_cam.transform.forward, _cam.transform.up);
        }

        private void ResolveCamera()
        {
            var cam = Camera.main;
            if (cam != null) _cam = cam;
        }

        private void Refresh()
        {
            if (_owner == null) { SetVisible(false); return; }

            // Compare the RAW name first. Sanitise allocates a StringBuilder, its buffer
            // and a result string, and GetPlayerTeam goes through two reflection GetValue
            // calls that box the enum - all of it thrown away unchanged on almost every
            // refresh, because names do not change.
            string raw = ResolveRawName(_owner);
            if (raw != _lastRawName)
            {
                _lastRawName = raw;
                _sanitisedName = Sanitise(raw);
            }

            string name = _sanitisedName;
            if (string.IsNullOrEmpty(name)) { SetVisible(false); return; }

            Color color = PropStyle.TeamColor(PracticeHelpers.GetPlayerTeam(_owner));

            SetVisible(true);
            if (name != _shownName)
            {
                _shownName = name;
                if (_tmp != null) _tmp.text = name;
                else if (_legacy != null) _legacy.text = name;
            }

            if (color != _shownColor)
            {
                _shownColor = color;
                if (_tmp != null) _tmp.color = color;
                else if (_legacy != null)
                {
                    _legacy.color = color;
                    if (_legacyRenderer != null && _legacyRenderer.material != null)
                    {
                        try { _legacyRenderer.material.color = color; } catch { }
                    }
                }
            }
        }

        /// <summary>
        /// Top-centre of the prop's visible geometry, in the PROP'S local space.
        ///
        /// Centre, not origin: TryCloneGameNet re-centres the cloned net inside its root, so
        /// the root's origin can be well off to one side of the net you can actually see.
        /// Local, not world: stored once and run back through TransformPoint each frame, so
        /// it follows the prop's rotation for free.
        ///
        /// Statically batched parts have no Renderer and so do not contribute - on the mini
        /// net that is the frame, leaving the netting to define the bounds, which is the
        /// bulk of the net and close enough to centre the name over.
        /// </summary>
        private static bool TryMeasureLocalTop(GameObject propRoot, out Vector3 localTop)
        {
            localTop = Vector3.zero;

            try
            {
                var renderers = propRoot.GetComponentsInChildren<Renderer>(includeInactive: true);
                if (renderers == null || renderers.Length == 0) return false;

                Bounds combined = default(Bounds);
                bool any = false;

                foreach (var renderer in renderers)
                {
                    if (renderer == null || renderer is LineRenderer) continue;
                    if (!any) { combined = renderer.bounds; any = true; }
                    else combined.Encapsulate(renderer.bounds);
                }

                if (!any) return false;

                Vector3 worldTop = new Vector3(combined.center.x, combined.max.y, combined.center.z);
                localTop = propRoot.transform.InverseTransformPoint(worldTop);
                return true;
            }
            catch { return false; }
        }

        private static string ResolveRawName(Player player)
        {
            try
            {
                if (player.Username == null) return null;
                return player.Username.Value.ToString();
            }
            catch { return null; }
        }

        /// <summary>Longest name drawn. Past this it is cut and ellipsed.</summary>
        private const int MaxNameChars = 16;

        /// <summary>
        /// Strip what the font cannot draw, and bound the length.
        ///
        /// Steam names are full Unicode and people use emoji freely. TMP renders a glyph the
        /// font asset does not have as the missing-glyph box, so an emoji name comes out as a
        /// row of squares floating over the rink. Everything outside the Basic Multilingual
        /// Plane is dropped (that is all of the emoji blocks, which live in the astral planes
        /// and arrive as surrogate PAIRS), along with the BMP symbol/dingbat ranges and the
        /// joiners and variation selectors that glue emoji sequences together - otherwise a
        /// stripped sequence leaves its punctuation behind.
        ///
        /// The length cap is the other half: nothing stops a 32-character name, and at this
        /// text size that would stretch several metres across the ice.
        /// </summary>
        internal static string Sanitise(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;

            var sb = new System.Text.StringBuilder(raw.Length);

            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];

                // Surrogate pair => a non-BMP character: every emoji block lives up there.
                // Skip both halves; a lone half is unrenderable too.
                if (char.IsSurrogate(c)) continue;

                if (c < ' ') continue;                            // control characters
                if (c == '‍') continue;                      // zero-width joiner
                if (c == '︎' || c == '️') continue;     // variation selectors
                if (c == '⃣') continue;                      // combining keycap

                // BMP pictographs: misc symbols, dingbats, and the symbol blocks that
                // carry emoji presentation.
                if (c >= '☀' && c <= '➿') continue;
                if (c >= '⬀' && c <= '⯿') continue;
                if (c >= '' && c <= '') continue;     // private use

                sb.Append(c);
            }

            string name = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(name)) return null;   // name was ALL emoji

            if (name.Length > MaxNameChars)
                name = name.Substring(0, MaxNameChars).TrimEnd() + "…";

            return name;
        }

        private void SetVisible(bool visible)
        {
            if (_tmp != null && _tmp.enabled != visible) _tmp.enabled = visible;
            if (_legacyRenderer != null && _legacyRenderer.enabled != visible) _legacyRenderer.enabled = visible;
        }

        private void OnDestroy()
        {
            // Drop our registry entry so a re-applied prop builds a fresh label rather than
            // finding a destroyed one. ReferenceEquals because Unity's == calls a destroyed
            // object null, which would skip the removal.
            // ReferenceEquals, not !=. In the normal order the PROP dies first, LateUpdate
            // sees _follow as Unity-null and destroys us, and a `_follow != null` guard is
            // then false - so the entry was never removed on the one path that always runs,
            // leaking a dead Transform/PropLabel pair per prop spawn. The dictionary lookup
            // itself still works: Unity keys off the instance id, which outlives destruction.
            if (!ReferenceEquals(_follow, null)
                && _byProp.TryGetValue(_follow, out var current)
                && ReferenceEquals(current, this))
            {
                _byProp.Remove(_follow);
            }

            // Both paths instance a material Unity will not free with the GameObject:
            // MeshRenderer.material for the legacy branch, and TMP's fontMaterial (which
            // its own OnDestroy leaves alone) for the branch that actually runs.
            if (_legacyMaterial != null)
            {
                try { UnityEngine.Object.Destroy(_legacyMaterial); } catch { }
                _legacyMaterial = null;
            }

            if (_tmpMaterial != null)
            {
                try { UnityEngine.Object.Destroy(_tmpMaterial); } catch { }
                _tmpMaterial = null;
            }
        }

        private static void EnsureFont()
        {
            if (_fontResolved) return;
            _fontResolved = true;

            try
            {
                _font = TMP_Settings.defaultFontAsset;
                if (_font == null)
                {
                    // No TMP default configured - borrow whatever the game already loaded
                    // for its own UI.
                    var all = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                    if (all != null && all.Length > 0) _font = all[0];
                }
            }
            catch { _font = null; }

            if (_font == null)
                Debug.LogWarning("[MaxPractice] PropLabel: no TMP font asset; falling back to TextMesh.");
        }

        /// <summary>
        /// The legacy TextMesh ships with a shader URP does not render. Swap it for
        /// URP/Unlit and rebind the glyph atlas, the same fix OWP uses.
        /// </summary>
        private void FixLegacyMaterial()
        {
            if (_legacyRenderer == null || _legacyRenderer.material == null) return;

            var mat = _legacyRenderer.material;
            _legacyMaterial = mat;

            Shader urpUnlit = Shader.Find("Universal Render Pipeline/Unlit");
            if (urpUnlit == null || mat.shader == urpUnlit) return;

            var fontTex = mat.mainTexture;
            mat.shader = urpUnlit;
            if (fontTex != null && mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", fontTex);
            if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);   // transparent
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            mat.renderQueue = 4000;
        }
    }
}
