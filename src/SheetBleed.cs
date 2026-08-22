// SheetBleed.cs - Stop other practice sheets showing through onto yours.
//
// RinkCloneVisuals already scopes the sheet GEOMETRY to the one you're standing on, so
// nobody sees a second rink hanging in the sky. What it never covered is the things that
// move: players, sticks and pucks are ordinary networked objects sitting 40 m above or
// below you, and two of the ways the renderer draws them reach across that gap.
//
// SHADOWS. A sheet stacked above yours is lit, and its players and pucks drop shadows
// straight down the 40 m onto your ice. You get a game of shadow hockey played by nobody
// on an empty rink, which is a good deal more distracting than the real thing.
//
// PUCK ELEVATION INDICATORS. PuckElevationIndicator.Update raycasts
// Vector3.down * infinity and plants a marker plane wherever it lands, so the indicator
// for a puck on the sheet above walks around on YOUR ice. Infinity is the whole problem:
// on a single rink the first thing under a puck is always that rink's floor, and the
// vanilla code is right to assume it.
//
// Both are fixed the same way the minimap is - by asking which sheet the thing is on and
// scoping it to the local one. Neither changes physics or anything the server sees; a
// suppressed shadow is still a real puck on a real sheet, just one you can't see the
// underside of.
//
// Client-side only. A dedicated server draws nothing and PuckElevationIndicator bails on
// IsDedicatedGameServer before any of this matters.

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxPractice
{
    internal static class SheetBleed
    {
        /// <summary>
        /// How often the object set is re-checked. Shadow casting only has to change when
        /// something crosses to another sheet, which is a /rink command rather than
        /// anything continuous, so this can be lazy.
        /// </summary>
        private const float ScanInterval = 0.25f;

        private static float _nextScan;
        private static bool _warned;

        /// <summary>
        /// Whether each tracked object is currently suppressed, keyed by instance id.
        ///
        /// This records what we ACTUALLY DID, not which sheet the object was on. Caching
        /// the sheet instead looks equivalent and isn't: when the local player changes
        /// sheet, every object's verdict flips at once while none of their own positions
        /// changed, and a cache keyed on their sheet reports no work to do for any of
        /// them - so the whole rink keeps whatever shadows it had.
        ///
        /// Rebuilt each pass into <see cref="_appliedNext"/> and swapped, which drops
        /// entries for despawned objects instead of leaking their instance ids.
        /// </summary>
        private static Dictionary<int, bool> _applied = new Dictionary<int, bool>();
        private static Dictionary<int, bool> _appliedNext = new Dictionary<int, bool>();

        /// <summary>
        /// Renderers we switched off, holding whatever they were set to before. Restoring
        /// from this rather than assuming ShadowCastingMode.On matters for anything that
        /// was deliberately ShadowsOnly or Off already - the local player's own body is
        /// both, depending on the camera.
        /// </summary>
        private static readonly Dictionary<Renderer, ShadowCastingMode> _suppressed =
            new Dictionary<Renderer, ShadowCastingMode>();

        private static readonly List<GameObject> _scratch = new List<GameObject>();
        private static readonly List<Renderer> _stale = new List<Renderer>();

        /// <summary>Pumped from RinkCloneVisuals.LateUpdate, which runs only on a client.</summary>
        internal static void Tick()
        {
            try
            {
                if (!RinkSheets.HasAnnouncedSheets)
                {
                    // Last sheet came down - everything is back on one rink and allowed to
                    // cast again.
                    if (_suppressed.Count > 0) Reset();
                    return;
                }

                if (Time.unscaledTime < _nextScan) return;
                _nextScan = Time.unscaledTime + ScanInterval;

                Scan();
            }
            catch (Exception ex)
            {
                if (!_warned)
                {
                    _warned = true;
                    Debug.LogWarning("[MaxPractice] Cross-sheet shadow suppression failed, leaving shadows " +
                                     "as the game set them: " + ex.Message);
                }
            }
        }

        /// <summary>Give every renderer back its own shadow setting. Safe to call twice.</summary>
        internal static void Reset()
        {
            foreach (KeyValuePair<Renderer, ShadowCastingMode> kvp in _suppressed)
            {
                if (kvp.Key == null) continue;
                try { kvp.Key.shadowCastingMode = kvp.Value; }
                catch { }
            }

            _suppressed.Clear();
            _applied.Clear();
            _appliedNext.Clear();
            _nextScan = 0f;
        }

        // ------------------------------------------------------------------

        private static void Scan()
        {
            int local = RinkCloneVisuals.VisibleSheet;

            _scratch.Clear();
            CollectMovers(_scratch);

            _appliedNext.Clear();

            for (int i = 0; i < _scratch.Count; i++)
            {
                GameObject go = _scratch[i];
                if (go == null) continue;

                int id = go.GetInstanceID();
                bool suppress = RinkSheets.VisibleSheetFor(go.transform.position) != local;
                _appliedNext[id] = suppress;

                // Walking the hierarchy for renderers is the expensive half, so only do it
                // when this object's verdict is new or has actually changed.
                if (_applied.TryGetValue(id, out bool was) && was == suppress) continue;

                ApplyShadows(go, castsShadows: !suppress);
            }

            // Swap rather than assign: the outgoing dictionary is reused as next pass's
            // buffer, so neither allocates after the first scan.
            Dictionary<int, bool> spent = _applied;
            _applied = _appliedNext;
            _appliedNext = spent;

            PruneDestroyed();
        }

        /// <summary>
        /// Everything that moves between sheets and is drawn: player bodies, their sticks,
        /// and every puck.
        ///
        /// Player bodies are gathered from GetPlayers plus MaxPractice's own fake-player
        /// set, because our GoalieAIManager postfix strips AI goalies and traffic dummies
        /// out of GetPlayers on the server - and an AI goalie standing in a crease one
        /// sheet up casts exactly the same shadow a human does.
        /// </summary>
        private static void CollectMovers(List<GameObject> into)
        {
            var pm = MonoBehaviourSingleton<PlayerManager>.Instance;
            if (pm != null)
            {
                List<Player> players = pm.GetPlayers(false);
                if (players != null)
                    for (int i = 0; i < players.Count; i++) AddPlayer(into, players[i]);

                foreach (Player fake in MaxPracticePlugin.FakePlayers)
                    AddPlayer(into, fake);
            }

            var puckManager = MonoBehaviourSingleton<PuckManager>.Instance;
            if (puckManager == null) return;

            List<Puck> pucks = puckManager.GetPucks(false);
            if (pucks == null) return;

            for (int i = 0; i < pucks.Count; i++)
                if (pucks[i] != null) into.Add(pucks[i].gameObject);
        }

        private static void AddPlayer(List<GameObject> into, Player player)
        {
            if (player == null) return;

            try
            {
                if (player.PlayerBody != null) into.Add(player.PlayerBody.gameObject);
                if (player.Stick != null) into.Add(player.Stick.gameObject);
            }
            catch { }
        }

        private static void ApplyShadows(GameObject go, bool castsShadows)
        {
            Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null) continue;

                if (!castsShadows)
                {
                    // Remember the ORIGINAL, not whatever a second pass would read back
                    // off a renderer we already switched off.
                    if (!_suppressed.ContainsKey(r)) _suppressed[r] = r.shadowCastingMode;
                    r.shadowCastingMode = ShadowCastingMode.Off;
                    continue;
                }

                if (!_suppressed.TryGetValue(r, out ShadowCastingMode original)) continue;
                r.shadowCastingMode = original;
                _suppressed.Remove(r);
            }
        }

        /// <summary>
        /// Drop renderers whose object has been destroyed. Nothing will ever restore them
        /// and holding the reference keeps a dead renderer alive.
        /// </summary>
        private static void PruneDestroyed()
        {
            _stale.Clear();
            foreach (KeyValuePair<Renderer, ShadowCastingMode> kvp in _suppressed)
                if (kvp.Key == null) _stale.Add(kvp.Key);

            for (int i = 0; i < _stale.Count; i++) _suppressed.Remove(_stale[i]);
            _stale.Clear();
        }
    }

    /// <summary>
    /// Keep a puck's elevation marker on the puck's own sheet.
    ///
    /// The vanilla Update raycasts down with an infinite distance, so a puck one sheet up
    /// finds this sheet's ice and draws its plane and its line here. Skipping the method
    /// outright for an off-sheet puck also saves the raycast, which is the expensive part
    /// and was never going to produce a usable answer.
    /// </summary>
    [HarmonyPatch(typeof(PuckElevationIndicator), "Update")]
    internal static class PuckElevationSheetPatch
    {
        private static readonly FieldInfo PlaneField =
            AccessTools.Field(typeof(PuckElevationIndicator), "planeMeshRenderer");
        private static readonly FieldInfo LineField =
            AccessTools.Field(typeof(PuckElevationIndicator), "lineRenderer");

        internal static bool Prepare()
        {
            if (AccessTools.Method(typeof(PuckElevationIndicator), "Update") != null) return true;

            Debug.LogWarning("[MaxPractice] PuckElevationIndicator.Update not found — pucks on other " +
                             "practice sheets will drop elevation markers onto yours.");
            return false;
        }

        private static bool Prefix(PuckElevationIndicator __instance)
        {
            try
            {
                // The player has these switched off entirely. Vanilla returns immediately
                // and both renderers are already disabled - nothing for us to re-enable.
                if (!__instance.IsVisible) return true;

                // Note the deliberate order: "no sheets standing" must still fall through
                // to the re-enable below rather than returning early. Vanilla Update never
                // touches renderer.enabled - only the IsVisible setter does - so a marker
                // we suppressed while sheets were up has nothing else to turn it back on,
                // and returning here left every one of them dark for the rest of the
                // session the moment the last sheet was torn down.
                bool onLocalSheet =
                    !RinkSheets.HasAnnouncedSheets ||
                    RinkSheets.VisibleSheetFor(__instance.transform.position) == RinkCloneVisuals.VisibleSheet;

                SetRenderer(PlaneField, __instance, onLocalSheet);
                SetRenderer(LineField, __instance, onLocalSheet);

                return onLocalSheet;
            }
            catch
            {
                return true;
            }
        }

        private static void SetRenderer(FieldInfo field, PuckElevationIndicator instance, bool enabled)
        {
            if (field == null) return;

            try
            {
                if (field.GetValue(instance) is Renderer r && r != null && r.enabled != enabled)
                    r.enabled = enabled;
            }
            catch { }
        }
    }
}
