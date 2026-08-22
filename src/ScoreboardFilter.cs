// ScoreboardFilter.cs - Keep MaxPractice's AI players off the Tab scoreboard.
//
// AI goalies, traffic dummies and passer AIs are real Player objects in PlayerManager -
// that is what lets them skate, hold position slots and show up in replays - so the
// scoreboard lists them alongside the humans. Six traffic dummies read as six players who
// just joined.
//
// GoalieAIManager already strips them from PlayerManager.GetPlayers/GetSpawnedPlayers,
// and that is where the fix looks like it should live. It isn't, for two reasons:
//
//   1. Those postfixes filter against MaxPracticePlugin.FakePlayers, a HashSet populated
//      by the SERVER-side spawn paths. On a connected client it is empty, so the filter
//      no-ops and every bot shows anyway. Only the host has ever had a clean scoreboard.
//
//   2. Widening those postfixes to match on client id instead is not safe. PlayerManager
//      .GetPlayerByClientId is implemented ON TOP of GetPlayers, so filtering by id there
//      would make the AI players unresolvable by id everywhere, on both sides. The
//      existing comment about brand-new fakes needing to survive one lookup is the same
//      hazard seen from the server end.
//
// So the count and the rows get fixed where they are actually rendered, which touches
// nothing else. The row half matters on the host too: UIScoreboardController takes the
// Player straight out of the event payload rather than from GetPlayers, so AddPlayer is
// called for a bot regardless of what GetPlayers returns. The existing patches were only
// ever fixing the header count, never the rows.
//
// Verified against the shipped metadata for the current build:
//
//   - UIScoreboard.AddPlayer is the only thing that ever creates a row, and its only
//     caller is UIScoreboardController.Event_Everyone_OnPlayerAdded. Blocking it is
//     enough on its own.
//   - StylePlayer, UpdatePlayerPing and RemovePlayer all test playerVisualElementMap
//     before touching it and return early when the row is absent, so a blocked player
//     cannot throw its way through them later. They need no patch.
//   - All three StyleServer call sites pass PlayerManager.GetPlayers(false).Count, and
//     StyleServer renders it as "{0}/{1}" against Server.MaxPlayers.

using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace MaxPractice
{
    internal static class ScoreboardFilter
    {
        private static bool _warned;

        /// <summary>
        /// Every flavour of MaxPractice fake player. Resolved from the replicated
        /// OwnerClientId and username rather than the server-only FakePlayers set, so it
        /// gives the same answer on a client as it does on the host.
        /// </summary>
        internal static bool ShouldHide(Player player)
        {
            if (player == null) return false;
            if (!ConfigManager.Config.HideAIFromScoreboard) return false;

            try { return FakePlayerDetector.IsAnyFakePlayer(player); }
            catch { return false; }
        }

        /// <summary>
        /// How many bots are still inside the count the caller just measured.
        ///
        /// Deliberately re-measured through GetPlayers rather than assumed: our own
        /// GetPlayers postfix has already removed them on a host, so counting the list as
        /// the caller sees it returns 0 there and the real number on a client. That is
        /// what keeps this from double-subtracting on the host without having to branch
        /// on IsServer - which would be the wrong test anyway, since what matters is
        /// whether the postfix had a populated FakePlayers set to work with.
        /// </summary>
        internal static int StillCounted()
        {
            try
            {
                if (!ConfigManager.Config.HideAIFromScoreboard) return 0;

                var pm = MonoBehaviourSingleton<PlayerManager>.Instance;
                if (pm == null) return 0;

                List<Player> players = pm.GetPlayers(false);
                if (players == null) return 0;

                int hidden = 0;
                for (int i = 0; i < players.Count; i++)
                    if (ShouldHide(players[i])) hidden++;

                return hidden;
            }
            catch (Exception ex)
            {
                if (!_warned)
                {
                    _warned = true;
                    Debug.LogWarning($"[MaxPractice] Could not adjust the scoreboard player count: {ex.Message}");
                }
                return 0;
            }
        }
    }

    /// <summary>Never build a scoreboard row for a bot.</summary>
    [HarmonyPatch(typeof(UIScoreboard), nameof(UIScoreboard.AddPlayer))]
    internal static class ScoreboardAddPlayerPatch
    {
        internal static bool Prepare()
        {
            if (AccessTools.Method(typeof(UIScoreboard), nameof(UIScoreboard.AddPlayer)) != null) return true;

            Debug.LogWarning("[MaxPractice] UIScoreboard.AddPlayer not found — AI goalies and traffic " +
                             "dummies will be listed on the scoreboard as players.");
            return false;
        }

        private static bool Prefix(Player player)
        {
            return !ScoreboardFilter.ShouldHide(player);
        }
    }

    /// <summary>
    /// Take the bots back off the header count, so "3/12" matches the three rows above it.
    /// </summary>
    [HarmonyPatch(typeof(UIScoreboard), nameof(UIScoreboard.StyleServer))]
    internal static class ScoreboardStyleServerPatch
    {
        internal static bool Prepare()
        {
            if (AccessTools.Method(typeof(UIScoreboard), nameof(UIScoreboard.StyleServer)) != null) return true;

            Debug.LogWarning("[MaxPractice] UIScoreboard.StyleServer not found — the scoreboard player " +
                             "count will include AI goalies and traffic dummies.");
            return false;
        }

        private static void Prefix(ref int playerCount)
        {
            int hidden = ScoreboardFilter.StillCounted();
            if (hidden <= 0) return;

            playerCount = Mathf.Max(0, playerCount - hidden);
        }
    }
}
