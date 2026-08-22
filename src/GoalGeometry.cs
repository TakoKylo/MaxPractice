// GoalGeometry.cs - Resolves where each team's goal line actually is, right now.
//
// GoalieAI used to hardcode the vanilla goal line at z = ±40.23. CompAdjust
// (CompetitiveAdjustments) resizes the rink by scaling the 'Level Default' scene root, and
// the goals ride that root, so on a resized rink the real goal line is nowhere near ±40.23.
// The goalie would then set up in open ice with the net metres behind it, and every
// out-of-position check below (behind-the-goal-line, distance-to-crease, the 10 m teleport)
// would drag it back to the wrong spot and hold it there.
//
// This measures the net where it is rather than recomputing CompAdjust's arithmetic from its
// config, which picks up everything at once: EnableArenaTweaks + ArenaScale, ArenaOffset,
// GoalSizeScale, the mouth-to-goal-line pin CompAdjust applies on a resized rink,
// GoalBackOffset, and any other mod or custom scenery that moves a net. It also needs no
// reference to, or reflection into, CompAdjust itself, so it degrades to plain vanilla
// behaviour when that mod isn't loaded.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace MaxPractice
{
    internal static class GoalGeometry
    {
        // Vanilla goal line. Used as the seed and the last-resort fallback, so a server where
        // the nets can't be found behaves exactly as this mod did before.
        public const float VanillaGoalLineZ = 40.23f;

        // Regulation pivot-to-post-plane depth, used only for a net with no measurable post
        // colliders. A regulation net is ~1.12 m deep and its pivot sits ~1.15 m behind the
        // plane of the posts.
        private const float FallbackPivotToFront = 1.15f;

        // A measured goal line has to sit between the net's own pivot and centre ice, and no
        // further from the pivot than a net is deep, or it isn't the plane of the posts.
        private const float MaxPivotToFront = 3f;

        // Below this the net is close enough to centre ice that there's no reliable mouth
        // direction to read off the sign of its Z. Low enough to still accept a rink shrunk to
        // CompAdjust's minimum arena scale, where the nets legitimately sit a couple of metres
        // out from centre.
        public const float MinGoalLineZ = 2f;

        // The nets only move when an operator changes the arena, so a slow poll is plenty.
        private const float RefreshInterval = 2f;

        private static Vector3 _red = new Vector3(0f, 0f, -VanillaGoalLineZ);
        private static Vector3 _blue = new Vector3(0f, 0f, VanillaGoalLineZ);
        private static float _nextRefresh;

        // Reused by Refresh so the 2 s poll allocates nothing.
        private static readonly List<Goal> _goalScratch = new List<Goal>(4);

        /// <summary>True for a net belonging to one of our practice-sheet clones.</summary>
        private static bool IsSheetClone(Transform t)
        {
            Transform root = t != null ? t.root : null;
            return root != null && root.name.StartsWith("MaxPractice_", StringComparison.Ordinal);
        }

        /// <summary>Force the next Get() to re-measure (level reload, arena resize).</summary>
        public static void Invalidate()
        {
            _nextRefresh = 0f;
        }

        /// <summary>
        /// World position of the centre of a team's goal line: x = the net's lateral centre,
        /// y = 0, z = the plane of the posts. This is the reference the goalie positions
        /// against, so "1.2 m in front of goalPos" means 1.2 m out from the goal line.
        /// </summary>
        public static Vector3 Get(PlayerTeam team)
        {
            Refresh();
            return team == PlayerTeam.Red ? _red : _blue;
        }

        /// <summary>
        /// World Z halfway between the two goal lines. Centre ice is only world 0 when
        /// ArenaOffsetZ is 0, so "is the puck in my half" has to be measured against this.
        /// </summary>
        public static float CentreIceZ
        {
            get
            {
                Refresh();
                return (_red.z + _blue.z) * 0.5f;
            }
        }

        private static void Refresh()
        {
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + RefreshInterval;

            try
            {
                var found = UnityEngine.Object.FindObjectsByType<Goal>(FindObjectsSortMode.None);
                // Between a level unload and the next spawn there are no nets to measure.
                // Keeping the last known values beats snapping the goalie back to vanilla.
                if (found == null || found.Length == 0) return;

                // Only the arena's own nets. A practice sheet's Goal components are stripped
                // with Destroy, which Unity defers to end of frame, and BuildServer activates
                // the clone root before it returns - so for the rest of that frame the clone's
                // nets are live and returned here too, in unspecified order. TryResolve only
                // rejects on Z, so on a horizontal layout a clone would be accepted with the
                // sheet's X and park the arena's goalies a chunk-width off the rink until the
                // next refresh two seconds later.
                var goals = _goalScratch;
                goals.Clear();
                for (int i = 0; i < found.Length; i++)
                {
                    if (found[i] != null && !IsSheetClone(found[i].transform)) goals.Add(found[i]);
                }
                if (goals.Count == 0) return;

                Goal red = null, blue = null;
                foreach (var goal in goals)
                {
                    if (goal == null) continue;
                    // Goal.Team is the team that DEFENDS the net: StandardGameMode scores
                    // GetOpposingTeam(team) when a puck enters it.
                    if (goal.Team == PlayerTeam.Red) { if (red == null) red = goal; }
                    else if (goal.Team == PlayerTeam.Blue) { if (blue == null) blue = goal; }
                }

                // Fall back to which end of the sheet each net sits on, for a scene where Team
                // isn't set the way the base game sets it (custom scenery, a third net).
                if (red == null || blue == null)
                {
                    Goal lowest = null, highest = null;
                    foreach (var goal in goals)
                    {
                        if (goal == null) continue;
                        float z = goal.transform.position.z;
                        if (lowest == null || z < lowest.transform.position.z) lowest = goal;
                        if (highest == null || z > highest.transform.position.z) highest = goal;
                    }
                    if (red == null) red = lowest;
                    if (blue == null) blue = highest;
                }

                if (TryResolve(red, -1f, out Vector3 redLine)) _red = redLine;
                if (TryResolve(blue, 1f, out Vector3 blueLine)) _blue = blueLine;
            }
            catch { }
        }

        /// <param name="sideSign">-1 for the net at negative world Z, +1 for the positive one.</param>
        private static bool TryResolve(Goal goal, float sideSign, out Vector3 result)
        {
            result = Vector3.zero;
            if (goal == null || goal.transform == null) return false;

            Vector3 pivot = goal.transform.position;
            // A net that measured onto the wrong half, or onto centre ice, is rejected rather
            // than trusted: a goal line on the wrong side of the rink inverts every
            // behind-the-net test the goalie makes.
            if (Mathf.Abs(pivot.z) < MinGoalLineZ) return false;
            if (Mathf.Sign(pivot.z) != sideSign) return false;

            float inward = -sideSign; // toward centre ice

            float lineZ;
            if (TryMeasurePostPlaneZ(goal.transform, inward, out float postZ)
                && (postZ - pivot.z) * inward >= 0f
                && Mathf.Abs(postZ - pivot.z) <= MaxPivotToFront)
            {
                lineZ = postZ;
            }
            else
            {
                lineZ = pivot.z + inward * FallbackPivotToFront * Mathf.Abs(goal.transform.lossyScale.z);
            }

            result = new Vector3(pivot.x, 0f, lineZ);
            return true;
        }

        /// <summary>
        /// The plane of the posts is the goal line. Taken from the capsule CENTRES — i.e. the
        /// post axes — rather than their bounds, so CompAdjust's GoalThicknessScale, which
        /// inflates the capsule radii, can't drag the measurement toward centre ice.
        /// </summary>
        private static bool TryMeasurePostPlaneZ(Transform goalRoot, float inward, out float z)
        {
            z = 0f;

            Transform posts = goalRoot.Find("Goal Post Collider");
            if (posts == null) return false;

            var capsules = posts.GetComponents<CapsuleCollider>();
            if (capsules == null || capsules.Length == 0) return false;

            bool found = false;
            foreach (var capsule in capsules)
            {
                if (capsule == null) continue;
                float capsuleZ = capsule.transform.TransformPoint(capsule.center).z;
                // Front-most post axis: the extreme nearest centre ice.
                if (!found || capsuleZ * inward > z * inward)
                {
                    z = capsuleZ;
                    found = true;
                }
            }
            return found;
        }
    }
}
