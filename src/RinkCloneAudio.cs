// RinkCloneAudio.cs - Carry the arena's sound out to a practice sheet.
//
// Puck's arena audio is ordinary 3D AudioSources sitting at the rink, and a 3D source
// goes silent past its maxDistance. A sheet stacked 40 m below is well outside that for
// most of them, so a player out there skates in near silence - no rink ambience, no
// horn, none of the crowd.
//
// Two different problems, two different answers:
//
//   Looping ambience is a bed of sound that should feel like it is around you, so the
//   sheet gets its own copy playing at its own centre. Stretching the original's range
//   instead would make the rink hum audibly drift 40 m upward as you skated.
//
//   One-shot sources - horn, whistle, goal - are events happening at the arena. Those
//   should still sound like they came from over there, just audibly, so their reach is
//   widened enough to cover the sheet and restored when the sheet comes down.
//
// The originals are only ever borrowed. Every widened source is recorded with its old
// range and put back on teardown, because leaving the arena's audio permanently altered
// for everyone still on the main rink would be its own bug.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace MaxPractice
{
    internal static class RinkCloneAudio
    {
        /// <summary>How far from the main rink a source still counts as arena audio.</summary>
        private static readonly Vector3 ArenaSearchExtents = new Vector3(90f, 90f, 110f);

        /// <summary>Headroom past the sheet so it sits inside the falloff, not on its edge.</summary>
        private const float ReachMargin = 25f;

        private struct WidenedSource
        {
            public AudioSource Source;
            public float OriginalMaxDistance;
        }

        private static readonly List<WidenedSource> _widened = new List<WidenedSource>();

        /// <summary>Copy the arena's ambience onto a sheet and widen the rest to reach it.</summary>
        internal static void Apply(RinkSheetSlot slot, Transform parent)
        {
            if (slot == null || parent == null) return;
            if (slot.Origin.sqrMagnitude < 0.0001f) return;
            if (RinkCloneBuilder.IsHeadless()) return;      // dedicated servers hear nothing

            int zones = CloneReverbZones(slot, parent);

            int copied = 0, widened = 0, skipped = 0;
            float distance = slot.Origin.magnitude;

            try
            {
                AudioSource[] all = UnityEngine.Object.FindObjectsByType<AudioSource>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);

                for (int i = 0; i < all.Length; i++)
                {
                    AudioSource src = all[i];
                    if (src == null || IsOurs(src.transform)) continue;
                    if (!IsNearMainRink(src.transform.position)) continue;

                    // 2D sources are already everywhere; distance means nothing to them.
                    if (src.spatialBlend <= 0.01f) continue;

                    if (src.loop)
                    {
                        if (CopyLoopingSource(src, slot, parent)) copied++;
                    }
                    else if (src.rolloffMode != AudioRolloffMode.Logarithmic)
                    {
                        // Only a logarithmic source can be widened without being heard.
                        //
                        // Logarithmic attenuation is defined by minDistance; maxDistance
                        // only says where the falloff stops, so pushing it out extends the
                        // reach and leaves every distance inside the old range sounding
                        // exactly as it did.
                        //
                        // Linear does not work that way - it interpolates from full volume
                        // at minDistance to silence at maxDistance, so moving maxDistance
                        // re-scales the entire curve and the horn gets louder at every
                        // distance for everyone still standing on the main rink. A Custom
                        // curve is evaluated over distance/maxDistance and stretches the
                        // same way. Neither can be extended without altering the arena, so
                        // leave them exactly as found: a one-shot the far sheet cannot hear
                        // is a much smaller bug than the arena's audio changing under
                        // everyone who never asked for a second sheet.
                        skipped++;
                    }
                    else if (Widen(src, distance))
                    {
                        widened++;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaxPractice] Sheet {slot.Label}: could not carry arena audio across: {ex.Message}");
                return;
            }

            ConfigManager.Log($"Sheet {slot.Label}: audio — {zones} reverb zone(s) copied, " +
                              $"{copied} ambience source(s) copied, {widened} arena source(s) widened to reach it" +
                              (skipped > 0
                                  ? $", {skipped} left alone (non-logarithmic rolloff — widening would change how they sound on the main rink)."
                                  : "."));
        }

        /// <summary>
        /// Copy the arena's reverb zones onto the sheet.
        ///
        /// This is the "echo dampener". A reverb zone is a pure scene component - no code
        /// in the game assembly ever references one - which is why searching the
        /// decompiled game for the audio system turned up only mixer volumes and missed
        /// it entirely. The arena's zone is centred on the main rink with a max distance
        /// that reaches a sheet 40 m below at its centre but falls short at the ends,
        /// which is exactly the "not overlapping the bottom rink ends" symptom.
        ///
        /// Copying the zone to the sheet's own centre beats widening the original: a
        /// stretched zone would also bleed the arena's reverb across everything between
        /// the two sheets, and the falloff would still be centred in the wrong place.
        /// </summary>
        private static int CloneReverbZones(RinkSheetSlot slot, Transform parent)
        {
            int cloned = 0;

            try
            {
                AudioReverbZone[] all = UnityEngine.Object.FindObjectsByType<AudioReverbZone>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);

                for (int i = 0; i < all.Length; i++)
                {
                    AudioReverbZone src = all[i];
                    if (src == null || IsOurs(src.transform)) continue;
                    if (!IsNearMainRink(src.transform.position)) continue;

                    var go = new GameObject("Reverb_" + slot.Id + "_" + src.name);
                    go.transform.SetParent(parent, false);
                    go.transform.position = src.transform.position + slot.Origin;
                    go.transform.rotation = src.transform.rotation;

                    AudioReverbZone dst = go.AddComponent<AudioReverbZone>();
                    dst.minDistance = src.minDistance;
                    dst.maxDistance = src.maxDistance;
                    dst.reverbPreset = src.reverbPreset;

                    // Only meaningful on a User preset, but copying them is harmless
                    // otherwise and saves a surprise if the arena ever switches to one.
                    if (src.reverbPreset == AudioReverbPreset.User)
                    {
                        dst.room = src.room;
                        dst.roomHF = src.roomHF;
                        dst.roomLF = src.roomLF;
                        dst.decayTime = src.decayTime;
                        dst.decayHFRatio = src.decayHFRatio;
                        dst.reflections = src.reflections;
                        dst.reflectionsDelay = src.reflectionsDelay;
                        dst.reverb = src.reverb;
                        dst.reverbDelay = src.reverbDelay;
                        dst.HFReference = src.HFReference;
                        dst.LFReference = src.LFReference;
                        dst.diffusion = src.diffusion;
                        dst.density = src.density;
                    }

                    dst.enabled = src.enabled;
                    cloned++;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaxPractice] Sheet {slot.Label}: could not copy reverb zones: {ex.Message}");
            }

            return cloned;
        }

        /// <summary>
        /// Give the sheet its own copy of a looping bed. A fresh GameObject with a plain
        /// AudioSource, deliberately not a clone of the original object: those carry
        /// SynchronizedAudio, which is a NetworkBehaviour and has no business being
        /// duplicated client-side.
        /// </summary>
        private static bool CopyLoopingSource(AudioSource src, RinkSheetSlot slot, Transform parent)
        {
            if (src.clip == null) return false;

            var go = new GameObject("Ambience_" + slot.Id + "_" + src.name);
            go.transform.SetParent(parent, false);
            go.transform.position = src.transform.position + slot.Origin;

            AudioSource dst = go.AddComponent<AudioSource>();
            dst.clip = src.clip;
            dst.outputAudioMixerGroup = src.outputAudioMixerGroup;
            dst.loop = true;
            dst.playOnAwake = false;
            dst.volume = src.volume;
            dst.pitch = src.pitch;
            dst.spatialBlend = src.spatialBlend;
            dst.rolloffMode = src.rolloffMode;
            dst.minDistance = src.minDistance;
            dst.maxDistance = src.maxDistance;
            dst.dopplerLevel = src.dopplerLevel;
            dst.spread = src.spread;
            dst.priority = src.priority;
            dst.bypassEffects = src.bypassEffects;
            dst.bypassListenerEffects = src.bypassListenerEffects;
            dst.bypassReverbZones = src.bypassReverbZones;
            if (src.rolloffMode == AudioRolloffMode.Custom)
                dst.SetCustomCurve(AudioSourceCurveType.CustomRolloff, src.GetCustomCurve(AudioSourceCurveType.CustomRolloff));

            // Match the original's playhead so the two beds are not audibly out of phase
            // for anyone who can hear both.
            if (src.isPlaying)
            {
                dst.Play();
                try { dst.timeSamples = src.timeSamples; }
                catch { }
            }

            return true;
        }

        private static bool Widen(AudioSource src, float sheetDistance)
        {
            float needed = sheetDistance + ReachMargin;
            if (src.maxDistance >= needed) return false;

            // Record the ORIGINAL once. Apply runs per sheet with a different distance, so
            // widening the same source a second time used to capture the value the first
            // widening wrote - Restore then replayed both in order, the widened one won,
            // and the true original was cleared away with no record left.
            bool known = false;
            for (int i = 0; i < _widened.Count; i++)
            {
                if (_widened[i].Source == src) { known = true; break; }
            }
            if (!known)
                _widened.Add(new WidenedSource { Source = src, OriginalMaxDistance = src.maxDistance });

            src.maxDistance = needed;
            return true;
        }

        /// <summary>Hand the arena's own sources back exactly as they were found.</summary>
        internal static void Restore()
        {
            for (int i = 0; i < _widened.Count; i++)
            {
                AudioSource src = _widened[i].Source;
                if (src != null) src.maxDistance = _widened[i].OriginalMaxDistance;
            }

            if (_widened.Count > 0)
                ConfigManager.Log($"Restored {_widened.Count} arena audio source(s) to their original range.");

            _widened.Clear();
        }

        private static bool IsNearMainRink(Vector3 worldPos)
        {
            return Mathf.Abs(worldPos.x) <= ArenaSearchExtents.x
                && Mathf.Abs(worldPos.y) <= ArenaSearchExtents.y
                && Mathf.Abs(worldPos.z) <= ArenaSearchExtents.z;
        }

        private static bool IsOurs(Transform t)
        {
            Transform root = t != null ? t.root : null;
            return root != null && root.name.StartsWith("MaxPractice_", StringComparison.Ordinal);
        }
    }
}
