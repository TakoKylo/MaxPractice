// MinimapSheetView.cs - Point the minimap at the sheet you're standing on.
//
// The minimap plots every dot by normalising a world position against UIMinimap.Bounds,
// which is the arena rink's box. It is a top-down XZ view, and the default sheet layout
// stacks its clones straight up and down - X and Z never change - so four players spread
// across four sheets all plot on the SAME spot. The map says everyone is standing on top
// of you and there is no way to tell which dot is on your ice.
//
// Horizontal layouts fail the other way: sheets sit out at x/z the arena box never
// covers, so those dots plot outside the map graphic entirely.
//
// One private method positions every dot the map draws - players, pucks and sticks all
// funnel through UIMinimap.ApplyMinimapTranslate(VisualElement, Vector3) - so a single
// prefix fixes both cases:
//
//   - a dot on someone else's sheet is hidden outright
//   - a dot on YOUR sheet is rebased into rink-local coordinates, so it plots exactly
//     where the same play would plot on the arena rink
//
// Switching sheets therefore switches the minimap with you, for free.
//
// Note that MaxPractice never widens Level.Bounds, so UIMinimap.Bounds is still the
// vanilla single-rink box and rebasing is all that's needed. Anything that DID widen it
// would also have to restore the narrow box here, or every dot would come out squashed
// toward the middle of the map.
//
// Rebasing the full Vector3 rather than just X and Z matters more than it looks: the
// vanilla method also feeds worldPosition.y to UIMinimap.HeightToScale, which shrinks a
// dot the higher it sits. Left alone, everyone on a sheet 40 m down would be drawn at
// the wrong size as well as the wrong place.
//
// Client-side only. UIMinimap.Update returns immediately on a dedicated server, so this
// never runs there.

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UIElements;

namespace MaxPractice
{
    [HarmonyPatch(typeof(UIMinimap), "ApplyMinimapTranslate")]
    internal static class MinimapSheetView
    {
        /// <summary>Sheet the local viewpoint is on, recomputed at most once per frame.</summary>
        private static int _localSheet;
        private static int _localSheetFrame = -1;

        /// <summary>
        /// Dots we are currently holding hidden, so they can be put back.
        ///
        /// Nothing else would: the game only ever writes display on the stick dots, so a
        /// player dot left hidden when the sheets come down - or when the mod is disabled
        /// - would stay hidden for the rest of the session even though its owner is back
        /// on the arena rink.
        ///
        /// Entries for dots whose owner has since disconnected are never revisited and
        /// sit here until the next restore. That is a few dozen bytes each and the set is
        /// emptied every time the last sheet goes away.
        /// </summary>
        private static readonly HashSet<VisualElement> _hidden = new HashSet<VisualElement>();

        private static bool _warned;

        /// <summary>Skip the patch rather than break PatchAll if the method ever moves.</summary>
        internal static bool Prepare()
        {
            MethodInfo target = AccessTools.Method(typeof(UIMinimap), "ApplyMinimapTranslate");
            if (target != null) return true;

            Debug.LogWarning("[MaxPractice] UIMinimap.ApplyMinimapTranslate not found — the minimap will " +
                             "show every practice sheet's players stacked on one another.");
            return false;
        }

        /// <summary>Put every dot back and forget where we were. Safe to call twice.</summary>
        internal static void Reset()
        {
            RestoreAll();
            _localSheet = 0;
            _localSheetFrame = -1;
        }

        private static bool Prefix(VisualElement element, ref Vector3 worldPosition)
        {
            try
            {
                if (!RinkSheets.HasAnnouncedSheets)
                {
                    // Last sheet just came down - everyone is back on the arena rink.
                    if (_hidden.Count > 0) RestoreAll();
                    return true;
                }

                int local = LocalSheet();
                int dot = RinkSheets.VisibleSheetFor(worldPosition);

                if (dot != local)
                {
                    Hide(element);
                    // Skip the vanilla translate too - a hidden dot does not need placing.
                    return false;
                }

                Show(element);
                worldPosition -= RinkSheets.AnnouncedOrigin(local);
                return true;
            }
            catch (Exception ex)
            {
                if (!_warned)
                {
                    _warned = true;
                    Debug.LogWarning("[MaxPractice] Minimap sheet view failed, falling back to the " +
                                     "vanilla minimap: " + ex.Message);
                }
                return true;
            }
        }

        /// <summary>
        /// Read from the camera rather than the local player's body, for the same reason
        /// RinkCloneVisuals does: it follows the player anyway, and it keeps working while
        /// spectating, in a replay, or before a body has spawned.
        /// </summary>
        private static int LocalSheet()
        {
            if (_localSheetFrame == Time.frameCount) return _localSheet;
            _localSheetFrame = Time.frameCount;

            Camera cam = Camera.main;
            // No camera for a frame is not a reason to throw every dot on the map onto the
            // arena rink - keep the last answer.
            if (cam != null) _localSheet = RinkSheets.VisibleSheetFor(cam.transform.position);
            return _localSheet;
        }

        /// <summary>
        /// Written every pass rather than only on the transition, because the stick dots
        /// are not ours alone: UIMinimap.Update sets the stick's parent back to Flex from
        /// the ShowMinimapSticks setting immediately before calling the method we are
        /// prefixing. Ours lands after theirs, so re-asserting is what keeps an off-sheet
        /// stick hidden past the first frame.
        ///
        /// When sticks are switched off the game sets None and skips the translate
        /// entirely, so this never overrides that setting.
        /// </summary>
        private static void Hide(VisualElement element)
        {
            if (element == null) return;
            _hidden.Add(element);
            element.style.display = DisplayStyle.None;
        }

        /// <summary>
        /// Only touch display for a dot we are actually holding down. Dots that were never
        /// hidden are left exactly as the game styled them.
        /// </summary>
        private static void Show(VisualElement element)
        {
            if (element == null) return;
            if (!_hidden.Remove(element)) return;
            element.style.display = DisplayStyle.Flex;
        }

        private static void RestoreAll()
        {
            foreach (VisualElement element in _hidden)
            {
                if (element == null) continue;
                try { element.style.display = DisplayStyle.Flex; }
                catch { }
            }
            _hidden.Clear();
        }
    }
}
