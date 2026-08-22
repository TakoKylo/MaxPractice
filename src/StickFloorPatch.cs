// StickFloorPatch.cs - Let the stick blade go below world zero on a stacked sheet.
//
// StickPositioner.ShootRaycast places the blade against whatever the stick's raycast
// hits, and then does this:
//
//     val9.y = Mathf.Max(0f, val9.y);
//
// The blade target's world Y is clamped to zero, on the reasonable assumption that the
// ice is at world zero and a blade below that is a bug. Both clamps sit in the branches
// taken when the second raycast comes back with a non-up normal - which is what happens
// when the stick is against the boards. On open ice both normals point up, the clamp is
// skipped, and everything behaves.
//
// On a sheet stacked 40 m down, the blade target is legitimately at y = -40, Mathf.Max
// hoists it to 0, and the stick snaps up to the arena rink's height. The PID controller
// driving the blade has a proportional gain of 500, so it does not ease back down - it
// whips, which is the "boards send my stick flying" everyone was seeing.
//
// This is a hardcoded world-space assumption in the base game, in the same family as the
// 16-bit position encoding: no amount of fixing the clone can help, because the clone is
// not what is wrong. So rewrite the clamp to use the floor of whichever sheet the stick
// is actually on. On the arena rink that floor is zero and the behaviour is identical to
// vanilla, byte for byte - only a player standing on a clone sees any difference.
//
// These are the only two world-Y clamps in the game; pucks and players have none, which
// is why everything except the stick already worked on a stacked sheet.

using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace MaxPractice
{
    [HarmonyPatch(typeof(StickPositioner), "ShootRaycast")]
    internal static class StickFloorPatch
    {
        /// <summary>
        /// Floor for the stick currently being positioned. ShootRaycast runs inside one
        /// call, so a single field is enough - there is no interleaving to worry about.
        /// </summary>
        private static float _floor;

        private static bool _reported;

        /// <summary>Skip the patch rather than break PatchAll if the method ever moves.</summary>
        internal static bool Prepare()
        {
            MethodInfo target = AccessTools.Method(typeof(StickPositioner), "ShootRaycast");
            if (target != null) return true;

            Debug.LogWarning("[MaxPractice] StickPositioner.ShootRaycast not found — sticks will snap to " +
                             "world zero when they touch the boards on a stacked practice sheet.");
            return false;
        }

        private static void Prefix(StickPositioner __instance)
        {
            _floor = 0f;
            if (__instance == null) return;

            try { _floor = RinkSheets.SheetFloorAt(__instance.transform.position); }
            catch { _floor = 0f; }
        }

        /// <summary>
        /// Swap the two <c>Mathf.Max</c> calls for one that knows about sheets. Replacing
        /// the call rather than the constant it is handed means no walking backwards
        /// through the IL looking for the right <c>ldc.r4 0.0</c>, and these are the only
        /// Mathf.Max calls in the method.
        /// </summary>
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo vanilla = AccessTools.Method(typeof(Mathf), "Max", new[] { typeof(float), typeof(float) });
            MethodInfo replacement = AccessTools.Method(typeof(StickFloorPatch), nameof(SheetFloorMax));

            int swapped = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (vanilla != null && replacement != null &&
                    instruction.opcode == OpCodes.Call &&
                    instruction.operand as MethodInfo == vanilla)
                {
                    swapped++;
                    yield return new CodeInstruction(OpCodes.Call, replacement);
                    continue;
                }

                yield return instruction;
            }

            if (!_reported)
            {
                _reported = true;
                if (swapped == 0)
                    Debug.LogWarning("[MaxPractice] Could not rewrite the stick blade floor clamp — " +
                                     "sticks will snap to world zero at the boards on a stacked sheet.");
                else
                    ConfigManager.Log($"Stick blade floor clamp is now sheet-aware ({swapped} site(s)).");
            }
        }

        /// <summary>
        /// Stands in for <c>Mathf.Max(0f, y)</c>. The vanilla floor is added rather than
        /// discarded so this stays correct if the game ever clamps against something
        /// other than zero.
        /// </summary>
        internal static float SheetFloorMax(float vanillaFloor, float y)
        {
            return Mathf.Max(_floor + vanillaFloor, y);
        }
    }
}
