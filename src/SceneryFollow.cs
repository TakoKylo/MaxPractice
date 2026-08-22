// SceneryFollow.cs - Bring Dem's Scenery Loader arena with you onto a practice sheet.
//
// SceneryChanger (Workshop 3566470321) swaps the arena's surroundings for a prefab out of
// an AssetBundle - an outdoor pond, a different building - and parents the whole thing
// under one GameObject called "SceneryLoaderContent". It builds that once, around the
// level's own rink, because until MaxPractice there was only ever one rink to build it
// around. Skate onto a stacked sheet and you leave the scenery behind: the sheet is
// correct but it stands in nothing.
//
// The scenery is decoration. It is client-side, it is not networked, and no server logic
// reads it - so the cheapest correct fix is not to clone it per sheet but to MOVE the one
// copy this client already has to whichever sheet this client is standing on. One
// transform write per sheet change, no extra draw calls, no extra memory, and the
// bundle's own lights and ambient audio travel with it.
//
// That works because of something already true of practice sheets: each client only ever
// draws the sheet it is standing on. Nobody else's view is affected by this client's copy
// moving, because nobody else can see this client's sheet in the first place.
//
// The offset is always applied against the position the container was FIRST seen at
// rather than its current one, so repeated sheet changes cannot accumulate drift. If
// SceneryChanger reloads its scene the container is a new object, and the base position
// is captured again from that.
//
// Set RinkSheetSceneryFollows to false to leave the scenery where SceneryChanger put it.

using System;
using UnityEngine;

namespace MaxPractice
{
    internal static class SceneryFollow
    {
        /// <summary>
        /// SceneryChanger's own container, created in RinkSceneLoader.EnsureContainer.
        /// Matching on the name is the whole integration - no assembly reference, no
        /// reflection against their types, and nothing to break if they reorganise
        /// everything under it.
        /// </summary>
        private const string ContainerName = "SceneryLoaderContent";

        /// <summary>
        /// GameObject.Find walks the scene, so it is not something to do every frame while
        /// the scenery is still loading (it is an async bundle load) or simply absent
        /// because the player does not run the mod.
        /// </summary>
        private const float FindInterval = 2f;

        private static GameObject _container;

        /// <summary>
        /// The offset we currently have applied to <see cref="_container"/>, and the whole
        /// of what we know about its position.
        ///
        /// Everything is done as a delta against this rather than against a remembered
        /// "base" position, because there is no moment at which we can be sure the
        /// position we are reading is a pristine one. Capturing a base assumes the
        /// container has never been offset when we first see it, and that is exactly false
        /// after any teardown that did not run our Reset - the next capture would bake the
        /// old offset into the new base and the scenery would walk further away on every
        /// session. Deltas also leave alone any move SceneryChanger makes itself, instead
        /// of dragging its content back to a position we cached minutes ago.
        /// </summary>
        private static Vector3 _appliedOffset = Vector3.zero;

        private static int _appliedSheet = -1;
        private static float _nextFind;
        private static bool _announced;
        private static bool _warned;

        internal static void Tick(int visibleSheet)
        {
            try
            {
                if (!ConfigManager.Config.RinkSheetSceneryFollows)
                {
                    // Switched off after we had already moved it - don't strand it.
                    if (_container != null) Restore();
                    return;
                }

                if (!RinkSheets.HasAnnouncedSheets)
                {
                    // No sheets standing. Put it back if we moved it, then stop looking.
                    if (_container != null) Restore();
                    return;
                }

                if (!Resolve()) return;

                if (_appliedSheet == visibleSheet) return;

                Announce();
                ApplyOffset(RinkSheets.AnnouncedOrigin(visibleSheet));
                _appliedSheet = visibleSheet;
            }
            catch (Exception ex)
            {
                if (!_warned)
                {
                    _warned = true;
                    Debug.LogWarning("[MaxPractice] Could not move the Scenery Loader arena onto this " +
                                     "sheet, leaving it on the main rink: " + ex.Message);
                }
            }
        }

        /// <summary>Put the scenery back where SceneryChanger built it. Safe to call twice.</summary>
        internal static void Reset()
        {
            try { Restore(); }
            catch { }

            _container = null;
            _appliedSheet = -1;
            _nextFind = 0f;
            _announced = false;
        }

        // ------------------------------------------------------------------

        private static void Restore()
        {
            ApplyOffset(Vector3.zero);
            _appliedSheet = -1;
        }

        /// <summary>
        /// Move the container so that exactly <paramref name="wanted"/> of our offset is
        /// applied to it, whatever it is currently carrying. Passing zero puts it back.
        /// </summary>
        private static void ApplyOffset(Vector3 wanted)
        {
            if (_container == null)
            {
                // The object went away mid-session. Our offset went with it, so forget it
                // rather than subtracting it from whatever turns up next.
                _appliedOffset = Vector3.zero;
                return;
            }

            if (wanted == _appliedOffset) return;

            _container.transform.position += wanted - _appliedOffset;
            _appliedOffset = wanted;
        }

        /// <summary>
        /// One line the first time we move it, naming what we found and whether it carries
        /// colliders.
        ///
        /// The colliders are the part worth seeing in a log. This moves a client-side copy
        /// of another mod's content, which is safe precisely because scenery is decoration
        /// - but a bundle that ships ACTIVE colliders is not purely decoration, and moving
        /// those puts solid geometry somewhere the server does not agree it is. Counting
        /// them costs one hierarchy walk, once, and turns that from an unknown into
        /// something the log answers.
        /// </summary>
        private static void Announce()
        {
            if (_announced || _container == null) return;
            _announced = true;

            int active = 0;
            try
            {
                Collider[] colliders = _container.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < colliders.Length; i++)
                    if (colliders[i] != null && colliders[i].enabled) active++;
            }
            catch { }

            if (active == 0)
            {
                ConfigManager.Log("Scenery Loader detected — its arena will follow you between practice " +
                                  "sheets. No active colliders under it, so this is decoration only.");
                return;
            }

            Debug.LogWarning($"[MaxPractice] Scenery Loader detected — its arena will follow you between " +
                             $"practice sheets, but it carries {active} active collider(s). Those move with " +
                             $"it on THIS client only, so anything solid in the scenery will sit where the " +
                             $"server does not think it is. Set RinkSheetSceneryFollows=false if you hit " +
                             $"collision that disagrees with what you can see.");
        }

        /// <summary>
        /// True when <see cref="_container"/> is a live object. Handles the scenery
        /// arriving late (it is an async bundle load), being unloaded, and being reloaded
        /// as a different object.
        /// </summary>
        private static bool Resolve()
        {
            if (_container != null) return true;

            if (Time.unscaledTime < _nextFind) return false;
            _nextFind = Time.unscaledTime + FindInterval;

            // Only finds ACTIVE objects, which is what we want: a container that has been
            // switched off is mid-unload and moving it would be undone anyway.
            GameObject found = GameObject.Find(ContainerName);
            if (found == null) return false;

            _container = found;
            // A fresh object carries none of our offset, whatever the last one carried.
            _appliedOffset = Vector3.zero;
            _appliedSheet = -1;
            return true;
        }
    }
}
