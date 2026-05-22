// GoalieAIManager.cs - AI goalie lifecycle, Harmony patches, and vote-driven enable/disable.
// Adapted from ToastersRinkSuite reference for Puck B323 + MaxPractice integration.

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace MaxPractice
{
    /// <summary>
    /// Manages AI goalie lifecycle: spawning, despawning, and integration with the rest of MaxPractice.
    /// Per-team /dummy* commands use SpawnAIGoalie/DespawnAIGoalie directly. When auto-management is
    /// enabled (e.g. via /votegoalies), Tick() fills empty goalie slots automatically each second.
    /// </summary>
    public static class GoalieAIManager
    {
        // Toggles automatic AI goalie management. /dummy* commands always work; this flag controls
        // whether Tick() actively spawns/despawns based on goalie slot vacancy.
        public static bool AutoEnabled = false;

        // Fake client IDs match FakePlayerDetector constants so existing CompTweaks-compat patches in
        // PracticePatches.cs (which detect bots by clientId) keep working.
        private const ulong RedClientId = FakePlayerDetector.FAKE_RED_CLIENT_ID;   // 1111112
        private const ulong BlueClientId = FakePlayerDetector.FAKE_BLUE_CLIENT_ID; // 1111111

        // Usernames also match existing FakePlayerDetector "Dummy" substring check.
        private const string RedBotName = "DummyRed";
        private const string BlueBotName = "DummyBlue";

        // Tracked AI goalie GoalieAI components (player objects are tracked via MaxPracticePlugin.RedTeamDummy/BlueTeamDummy).
        private static GoalieAI redAIComponent;
        private static GoalieAI blueAIComponent;

        // Throttle automatic evaluation.
        private static float nextEvalTime;
        private const float EvalInterval = 1.0f;

        // Prevent re-entry during spawn/despawn.
        private static bool isProcessing;

        // When true, GetPlayers/GetSpawnedPlayers postfixes don't filter AI goalies out — needed so
        // the replay recorder captures their movement.
        internal static bool bypassFilter;

        // Hardcoded goal positions. B323 has no RandomGoalSpots; matches existing GoalieAI hardcoded values.
        private static readonly Vector3 RedGoalPos = new Vector3(0f, 0f, -40.23f);
        private static readonly Vector3 BlueGoalPos = new Vector3(0f, 0f, 40.23f);

        public static Player RedAIGoalie => MaxPracticePlugin.RedTeamDummy;
        public static Player BlueAIGoalie => MaxPracticePlugin.BlueTeamDummy;

        public static bool IsAIGoalie(Player player)
        {
            if (player == null) return false;
            return player == RedAIGoalie || player == BlueAIGoalie;
        }

        public static bool IsAIGoalieClientId(ulong clientId)
        {
            return clientId == RedClientId || clientId == BlueClientId;
        }

        /// <summary>
        /// Compute crease spawn position + rotation for a team. Replaces reference's RandomGoalSpots.
        /// </summary>
        private static (Vector3 pos, Quaternion rot) GetCreasePosition(PlayerTeam team)
        {
            bool isRed = team == PlayerTeam.Red;
            Vector3 goalPos = isRed ? RedGoalPos : BlueGoalPos;
            Vector3 pos = goalPos;
            pos.z += isRed ? 1.2f : -1.2f;
            pos.y = 0f;
            pos.x = 0f;
            Quaternion rot = Quaternion.LookRotation(isRed ? Vector3.forward : Vector3.back);
            return (pos, rot);
        }

        /// <summary>
        /// Toggle auto-evaluation. When disabled, removes both AI goalies if they were auto-spawned.
        /// </summary>
        public static void SetAutoEnabled(bool value)
        {
            AutoEnabled = value;
            if (!AutoEnabled)
            {
                DespawnAll();
            }
            else
            {
                nextEvalTime = 0f; // force immediate evaluation
            }
        }

        // When > 0, GameOver fired and we're holding off DespawnAll until the win/loss
        // animation finishes. Tick checks this every frame regardless of AutoEnabled.
        private static float endOfGameDespawnTime = 0f;

        public static void Tick()
        {
            // End-of-game deferred despawn — runs even when AutoEnabled is false, since GameOver
            // can fire while a /dummy AI is alive.
            if (endOfGameDespawnTime > 0f && Time.time >= endOfGameDespawnTime)
            {
                endOfGameDespawnTime = 0f;
                DespawnAll();
            }

            if (!AutoEnabled) return;
            if (isProcessing) return;
            if (Time.time < nextEvalTime) return;
            nextEvalTime = Time.time + EvalInterval;

            try
            {
                EvaluateGoalieNeeds();
            }
            catch (Exception e)
            {
                Debug.LogError($"[MaxPractice] GoalieAIManager.Tick error: {e.Message}");
            }
        }

        private static void EvaluateGoalieNeeds()
        {
            var gm = NetworkBehaviourSingleton<GameManager>.Instance;
            if (gm == null) return;

            GamePhase phase = gm.Phase;

            // Skip during GameOver/None/Replay. Replay despawns humans, which would trick us into spawning AI.
            if (phase == GamePhase.GameOver || phase == GamePhase.None || phase == GamePhase.Replay)
                return;

            bool hasHumanRedGoalie = false;
            bool hasHumanBlueGoalie = false;

            var pm = MonoBehaviourSingleton<PlayerManager>.Instance;
            if (pm == null) return;

            foreach (Player player in pm.GetSpawnedPlayers(false))
            {
                if (player == null) continue;
                if (player.IsReplay != null && player.IsReplay.Value) continue;
                if (IsAIGoalie(player)) continue;
                if (player.Role == PlayerRole.Goalie)
                {
                    if (player.Team == PlayerTeam.Red) hasHumanRedGoalie = true;
                    else if (player.Team == PlayerTeam.Blue) hasHumanBlueGoalie = true;
                }
            }

            int humanGoalieCount = (hasHumanRedGoalie ? 1 : 0) + (hasHumanBlueGoalie ? 1 : 0);
            bool isWarmup = phase == GamePhase.Warmup;

            // Fill empty goalie slot(s). During warmup, only fill red (matches reference behavior).
            bool needRedAI = !hasHumanRedGoalie;
            bool needBlueAI = !hasHumanBlueGoalie && !isWarmup;

            if (needRedAI && RedAIGoalie == null) SpawnAIGoalie(PlayerTeam.Red);
            else if (!needRedAI && RedAIGoalie != null) DespawnAIGoalie(PlayerTeam.Red);

            if (needBlueAI && BlueAIGoalie == null) SpawnAIGoalie(PlayerTeam.Blue);
            else if (!needBlueAI && BlueAIGoalie != null) DespawnAIGoalie(PlayerTeam.Blue);
        }

        /// <summary>
        /// Spawn an AI goalie for a team. Returns true on success. Idempotent — does nothing if one
        /// already exists for that team.
        /// </summary>
        public static bool SpawnAIGoalie(PlayerTeam team)
        {
            bool isRed = team == PlayerTeam.Red;
            if ((isRed ? RedAIGoalie : BlueAIGoalie) != null) return true;

            isProcessing = true;
            try
            {
                var playerManager = MonoBehaviourSingleton<PlayerManager>.Instance;
                if (playerManager == null)
                {
                    Debug.LogError("[MaxPractice] GoalieAIManager: PlayerManager not available");
                    return false;
                }

                ulong clientId = isRed ? RedClientId : BlueClientId;
                string botName = isRed ? RedBotName : BlueBotName;

                // If a leftover Player object exists at this clientId, reuse it.
                Player existing = playerManager.GetPlayerByClientId(clientId);
                if (existing != null)
                {
                    if (!existing.IsCharacterSpawned)
                    {
                        var (creasePos, creaseRot) = GetCreasePosition(team);
                        existing.Server_SpawnCharacter(creasePos, creaseRot, PlayerRole.Goalie);
                    }
                    TrackAIGoalie(existing, team);
                    AttachAIComponent(existing, team);
                    ConfigManager.Log($"GoalieAIManager: Reattached AI to existing {botName}");
                    return true;
                }

                PlayerGameState gameState = new PlayerGameState
                {
                    Phase = PlayerPhase.Play,
                    Team = team,
                    Role = PlayerRole.Goalie
                };

                // Stock cosmetic IDs. Default-constructed PlayerCustomizationState has every ID = 0,
                // which the game's PlayerHead/PlayerTorso/PlayerGroin/StickMesh reject as "invalid" and
                // fall back to blank/white textures. -1 is the "no value, use server default" sentinel;
                // positive numbers point at specific catalog entries. The chosen IDs (513/526/2048/2621)
                // are the game's stock defaults, not Toasters-specific.
                PlayerCustomizationState customization = new PlayerCustomizationState
                {
                    FlagID = -1,
                    MustacheID = -1,
                    BeardID = -1,
                    HeadgearIDBlueAttacker = 513,
                    HeadgearIDRedAttacker = 513,
                    HeadgearIDBlueGoalie = 526,
                    HeadgearIDRedGoalie = 526,
                    JerseyIDBlueAttacker = 2048,
                    JerseyIDRedAttacker = 2048,
                    JerseyIDBlueGoalie = 2048,
                    JerseyIDRedGoalie = 2048,
                    StickSkinIDBlueAttacker = 2621,
                    StickSkinIDRedAttacker = 2621,
                    StickSkinIDBlueGoalie = 2621,
                    StickSkinIDRedGoalie = 2621,
                    StickShaftTapeIDBlueAttacker = -1,
                    StickShaftTapeIDRedAttacker = -1,
                    StickShaftTapeIDBlueGoalie = -1,
                    StickShaftTapeIDRedGoalie = -1,
                    StickBladeTapeIDBlueAttacker = -1,
                    StickBladeTapeIDRedAttacker = -1,
                    StickBladeTapeIDBlueGoalie = -1,
                    StickBladeTapeIDRedGoalie = -1,
                };

                playerManager.Server_SpawnPlayer(
                    clientId,
                    gameState,
                    customization,
                    PlayerHandedness.Right,
                    "0",          // steamID
                    botName,      // username
                    62,           // number
                    0,            // patreonLevel
                    0,            // adminLevel
                    false,        // isMuted
                    false         // isReplay
                );

                Player aiPlayer = playerManager.GetPlayerByClientId(clientId);
                if (aiPlayer == null)
                {
                    Debug.LogError($"[MaxPractice] GoalieAIManager: Failed to spawn {botName}");
                    return false;
                }

                var (pos, rot) = GetCreasePosition(team);
                aiPlayer.Server_SpawnCharacter(pos, rot, PlayerRole.Goalie);

                TrackAIGoalie(aiPlayer, team);
                AttachAIComponent(aiPlayer, team);
                ConfigManager.Log($"GoalieAIManager: Spawned {botName}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[MaxPractice] GoalieAIManager: Error spawning AI goalie for {team}: {e.Message}\n{e.StackTrace}");
                return false;
            }
            finally
            {
                isProcessing = false;
            }
        }

        private static void TrackAIGoalie(Player player, PlayerTeam team)
        {
            MaxPracticePlugin.FakePlayers.Add(player);
            if (team == PlayerTeam.Red) MaxPracticePlugin.RedTeamDummy = player;
            else MaxPracticePlugin.BlueTeamDummy = player;
        }

        private static void UntrackAIGoalie(PlayerTeam team)
        {
            bool isRed = team == PlayerTeam.Red;
            Player p = isRed ? MaxPracticePlugin.RedTeamDummy : MaxPracticePlugin.BlueTeamDummy;
            if (p != null) MaxPracticePlugin.FakePlayers.Remove(p);
            if (isRed) MaxPracticePlugin.RedTeamDummy = null;
            else MaxPracticePlugin.BlueTeamDummy = null;
        }

        private static void AttachAIComponent(Player player, PlayerTeam team)
        {
            bool isRed = team == PlayerTeam.Red;

            GoalieAI existing = isRed ? redAIComponent : blueAIComponent;
            if (existing != null)
            {
                try { UnityEngine.Object.Destroy(existing); } catch { }
            }

            if (player.PlayerBody == null) return;

            GoalieAI ai = player.PlayerBody.gameObject.AddComponent<GoalieAI>();
            ai.controlledPlayer = player;
            ai.team = team;

            if (isRed) redAIComponent = ai;
            else blueAIComponent = ai;
        }

        private static void ResetLookState(Player player)
        {
            try
            {
                if (player == null || player.PlayerInput == null) return;
                player.PlayerInput.LookInput.ServerValue = false;
                player.PlayerInput.Server_LookInputRpc(false, player.PlayerInput.RpcTarget.Everyone);
            }
            catch { }
        }

        public static bool DespawnAIGoalie(PlayerTeam team)
        {
            bool isRed = team == PlayerTeam.Red;
            Player aiPlayer = isRed ? MaxPracticePlugin.RedTeamDummy : MaxPracticePlugin.BlueTeamDummy;
            if (aiPlayer == null) return false;

            isProcessing = true;
            try
            {
                GoalieAI aiComponent = isRed ? redAIComponent : blueAIComponent;
                ResetLookState(aiPlayer);

                if (aiComponent != null)
                {
                    try { UnityEngine.Object.Destroy(aiComponent); } catch { }
                }

                try
                {
                    if (aiPlayer.IsCharacterSpawned)
                        aiPlayer.Server_DespawnCharacter();

                    if (aiPlayer.NetworkObject != null && aiPlayer.NetworkObject.IsSpawned)
                        aiPlayer.NetworkObject.Despawn();
                }
                catch (Exception e)
                {
                    Debug.LogError($"[MaxPractice] GoalieAIManager: Error despawning AI goalie: {e.Message}");
                }

                if (isRed) redAIComponent = null;
                else blueAIComponent = null;

                UntrackAIGoalie(team);
                ConfigManager.Log($"GoalieAIManager: Despawned AI goalie for {team}");
                return true;
            }
            finally
            {
                isProcessing = false;
            }
        }

        public static void DespawnAll()
        {
            if (RedAIGoalie != null) DespawnAIGoalie(PlayerTeam.Red);
            if (BlueAIGoalie != null) DespawnAIGoalie(PlayerTeam.Blue);
        }

        /// <summary>
        /// Called when a goal is scored — triggers the sad reaction on the AI goalie that got scored on.
        /// </summary>
        public static void OnGoalScored(PlayerTeam scoringTeam)
        {
            // No emotes during warmup — the goalie is a practice dummy and should stay ready,
            // not fall over or jump around when the user is trying to work on their shot.
            try
            {
                var gm = NetworkBehaviourSingleton<GameManager>.Instance;
                if (gm != null && gm.Phase == GamePhase.Warmup) return;
            }
            catch { }

            // Scored-on team's goalie: sad reaction. Scoring team's goalie: excited celebration.
            if (scoringTeam == PlayerTeam.Blue)
            {
                if (redAIComponent != null) redAIComponent.TriggerSad();
                if (blueAIComponent != null) blueAIComponent.TriggerCelebrate();
            }
            else if (scoringTeam == PlayerTeam.Red)
            {
                if (blueAIComponent != null) blueAIComponent.TriggerSad();
                if (redAIComponent != null) redAIComponent.TriggerCelebrate();
            }
        }

        /// <summary>
        /// React to game phase changes. Despawns/respawns AI goalies as needed for each phase.
        /// Also handles goal-scored detection: BlueScore/RedScore phases mean that team just scored,
        /// so the OPPOSITE team's AI goalie should trigger sad.
        /// </summary>
        public static void OnGameStateChanged(GamePhase oldPhase, GamePhase newPhase)
        {
            // Goal-scored detection — trigger sad reaction on the team that got scored on.
            if (newPhase == GamePhase.BlueScore) OnGoalScored(PlayerTeam.Blue);
            else if (newPhase == GamePhase.RedScore) OnGoalScored(PlayerTeam.Red);

            // End-of-game: fire long win/loss animations before despawning. Winner does the
            // celebrate dance, loser does the sad reaction. Despawn is deferred to Tick so the
            // animation gets to play out (otherwise DespawnAll destroys the GoalieAI components
            // and the animation never starts).
            if (newPhase == GamePhase.GameOver)
            {
                const float EndOfGameAnimDuration = 10f;
                var gmRef = NetworkBehaviourSingleton<GameManager>.Instance;
                if (gmRef != null)
                {
                    int red = gmRef.RedScore;
                    int blue = gmRef.BlueScore;
                    if (red > blue)
                    {
                        if (redAIComponent != null) redAIComponent.TriggerCelebrate(EndOfGameAnimDuration);
                        if (blueAIComponent != null) blueAIComponent.TriggerSad(EndOfGameAnimDuration);
                    }
                    else if (blue > red)
                    {
                        if (blueAIComponent != null) blueAIComponent.TriggerCelebrate(EndOfGameAnimDuration);
                        if (redAIComponent != null) redAIComponent.TriggerSad(EndOfGameAnimDuration);
                    }
                    // Ties: no special end-of-game animation, just despawn after the buffer.
                }
                endOfGameDespawnTime = Time.time + EndOfGameAnimDuration + 1f; // +1s buffer
                return;
            }

            if (newPhase == GamePhase.PostGame)
            {
                // PostGame can fire only a second or two after GameOver. Don't despawn here —
                // the deferred timer set on GameOver handles cleanup once the animation finishes.
                // If GameOver was somehow missed and no timer is pending, fall through to despawn.
                if (endOfGameDespawnTime <= 0f) DespawnAll();
                return;
            }

            // Replay: despawn AI goalie characters (but keep Player objects so the recorder can reference them).
            if (newPhase == GamePhase.Replay)
            {
                DespawnAICharactersOnly();
                return;
            }

            if (newPhase == GamePhase.Intermission)
            {
                if (redAIComponent != null) redAIComponent.TriggerIntermission();
                if (blueAIComponent != null) blueAIComponent.TriggerIntermission();
            }

            if (newPhase == GamePhase.FaceOff)
            {
                if (redAIComponent != null) redAIComponent.ExitIntermission();
                if (blueAIComponent != null) blueAIComponent.ExitIntermission();
                RespawnExistingAICharacters();
                TeleportAndFreezeExistingAI();
            }

            nextEvalTime = 0f; // force immediate re-evaluation
        }

        // ----------- Game phase change detection -----------
        // Goal-score is detected via the GamePhase.BlueScore/RedScore phase transitions in
        // OnGameStateChanged below — that path is server-driven (NotifyPhase polled from
        // PracticeManager.Update). Patching Server_NotifyGoalScoredRpc would fire on RPC
        // recipients (clients), where redAIComponent/blueAIComponent are null.

        // Tracks last seen phase so the GameManager state-changed hook can compute oldPhase.
        private static GamePhase lastPhase = GamePhase.None;

        internal static void NotifyPhase(GamePhase current)
        {
            if (current == lastPhase) return;
            GamePhase old = lastPhase;
            lastPhase = current;
            try { OnGameStateChanged(old, current); } catch (Exception e) { Debug.LogError($"[MaxPractice] OnGameStateChanged error: {e}"); }
        }

        // ----------- Phase-change helpers -----------

        private static void TeleportAndFreezeExistingAI()
        {
            TeleportOneAIGoalie(MaxPracticePlugin.RedTeamDummy, PlayerTeam.Red);
            TeleportOneAIGoalie(MaxPracticePlugin.BlueTeamDummy, PlayerTeam.Blue);
        }

        private static void TeleportOneAIGoalie(Player aiGoalie, PlayerTeam team)
        {
            if (aiGoalie == null || !aiGoalie.IsCharacterSpawned) return;
            if (aiGoalie.PlayerBody == null) return;
            var (pos, rot) = GetCreasePosition(team);
            aiGoalie.PlayerBody.Server_Teleport(pos, rot);
        }

        private static void DespawnAICharactersOnly()
        {
            try
            {
                ResetLookState(MaxPracticePlugin.RedTeamDummy);
                ResetLookState(MaxPracticePlugin.BlueTeamDummy);

                if (redAIComponent != null) { UnityEngine.Object.Destroy(redAIComponent); redAIComponent = null; }
                if (blueAIComponent != null) { UnityEngine.Object.Destroy(blueAIComponent); blueAIComponent = null; }

                var red = MaxPracticePlugin.RedTeamDummy;
                var blue = MaxPracticePlugin.BlueTeamDummy;
                if (red != null && red.IsCharacterSpawned) red.Server_DespawnCharacter();
                if (blue != null && blue.IsCharacterSpawned) blue.Server_DespawnCharacter();
            }
            catch (Exception e)
            {
                Debug.LogError($"[MaxPractice] GoalieAIManager: DespawnAICharactersOnly error: {e.Message}");
            }
        }

        private static void RespawnExistingAICharacters()
        {
            isProcessing = true;
            try { RespawnOneAIGoalie(PlayerTeam.Red); }
            catch (Exception e)
            {
                Debug.LogError($"[MaxPractice] GoalieAIManager: Error respawning red AI: {e.Message}");
                UntrackAIGoalie(PlayerTeam.Red);
                redAIComponent = null;
            }

            try { RespawnOneAIGoalie(PlayerTeam.Blue); }
            catch (Exception e)
            {
                Debug.LogError($"[MaxPractice] GoalieAIManager: Error respawning blue AI: {e.Message}");
                UntrackAIGoalie(PlayerTeam.Blue);
                blueAIComponent = null;
            }

            isProcessing = false;
        }

        private static void RespawnOneAIGoalie(PlayerTeam team)
        {
            bool isRed = team == PlayerTeam.Red;
            Player aiGoalie = isRed ? MaxPracticePlugin.RedTeamDummy : MaxPracticePlugin.BlueTeamDummy;
            if (aiGoalie == null) return;

            if ((UnityEngine.Object)aiGoalie == null)
            {
                UntrackAIGoalie(team);
                if (isRed) redAIComponent = null; else blueAIComponent = null;
                return;
            }

            if (aiGoalie.NetworkObject == null || !aiGoalie.NetworkObject.IsSpawned)
            {
                UntrackAIGoalie(team);
                if (isRed) redAIComponent = null; else blueAIComponent = null;
                return;
            }

            if (aiGoalie.IsCharacterSpawned) return;

            aiGoalie.Server_SetGameState(team: team, role: PlayerRole.Goalie, phase: PlayerPhase.Play, delay: 0f);
            var (pos, rot) = GetCreasePosition(team);
            aiGoalie.Server_SpawnCharacter(pos, rot, PlayerRole.Goalie);
            AttachAIComponent(aiGoalie, team);
        }

        // ============================================================
        // HARMONY PATCHES
        // ============================================================

        /// <summary>
        /// Suppress "has joined/left the server" chat for AI goalies.
        /// </summary>
        [HarmonyPatch(typeof(ChatManager), nameof(ChatManager.Server_BroadcastChatMessage), new[] { typeof(string), typeof(string) })]
        public static class SuppressBotChatMessagesPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(string content, string color)
            {
                if (content == null) return true;
                if ((content.Contains(RedBotName) || content.Contains(BlueBotName)) &&
                    (content.Contains("has joined the server") || content.Contains("has left the server")))
                {
                    return false;
                }
                return true;
            }
        }

        /// <summary>
        /// Filter AI goalies from PlayerManager.GetPlayers() (vote counts, pause logic, etc).
        /// PracticePatches.VoteManager_Server_AddVote_Patch already handles vote-needed
        /// counts via the FakePlayers HashSet, so this is mostly belt-and-suspenders.
        /// </summary>
        [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.GetPlayers))]
        public static class ExcludeFromPlayerListPatch
        {
            [HarmonyPostfix]
            public static void Postfix(ref List<Player> __result)
            {
                if (bypassFilter) return;
                if (RedAIGoalie == null && BlueAIGoalie == null) return;
                __result = __result.Where(p => !IsAIGoalie(p)).ToList();
            }
        }

        [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.GetSpawnedPlayers))]
        public static class ExcludeFromSpawnedPlayerListPatch
        {
            [HarmonyPostfix]
            public static void Postfix(ref List<Player> __result)
            {
                if (bypassFilter) return;
                if (RedAIGoalie == null && BlueAIGoalie == null) return;
                __result = __result.Where(p => !IsAIGoalie(p)).ToList();
            }
        }

        /// <summary>
        /// Fix TCP preview response player count by subtracting AI goalies. Uses Newtonsoft.Json
        /// (already a project reference) rather than System.Text.Json which isn't shipped.
        /// </summary>
        [HarmonyPatch(typeof(TCPServer), nameof(TCPServer.SendMessageAsync))]
        public static class FixTcpPlayerCountPatch
        {
            [HarmonyPrefix]
            public static void Prefix(ref string message)
            {
                if (message == null) return;
                if (RedAIGoalie == null && BlueAIGoalie == null) return;
                if (!message.Contains("\"players\"")) return;

                try
                {
                    var response = Newtonsoft.Json.JsonConvert.DeserializeObject<TCPServerPreviewResponse>(message);
                    if (response == null) return;
                    int botCount = (RedAIGoalie != null ? 1 : 0) + (BlueAIGoalie != null ? 1 : 0);
                    response.players = Math.Max(0, response.players - botCount);
                    message = Newtonsoft.Json.JsonConvert.SerializeObject(response);
                }
                catch { }
            }
        }

        /// <summary>
        /// Bypass GetPlayers/GetSpawnedPlayers filtering while the replay recorder runs, so AI goalies
        /// get captured in replay events.
        /// </summary>
        [HarmonyPatch(typeof(ReplayRecorder), nameof(ReplayRecorder.Server_StartRecording))]
        public static class ReplayStartRecordingPatch
        {
            [HarmonyPrefix] public static void Prefix() => bypassFilter = true;
            [HarmonyPostfix] public static void Postfix() => bypassFilter = false;
        }

        [HarmonyPatch(typeof(ReplayRecorder), "Server_Tick")]
        public static class ReplayTickPatch
        {
            [HarmonyPrefix] public static void Prefix() => bypassFilter = true;
            [HarmonyPostfix] public static void Postfix() => bypassFilter = false;
        }

        /// <summary>
        /// During phase transitions the game can clear Role to None before the recorder
        /// captures the body spawn event. Force-correct AI goalies back to Goalie role.
        /// </summary>
        [HarmonyPatch(typeof(ReplayRecorder), nameof(ReplayRecorder.Server_AddPlayerBodySpawnedEvent))]
        public static class FixReplayBodySpawnedRolePatch
        {
            [HarmonyPrefix]
            public static void Prefix(PlayerBody playerBody)
            {
                if (playerBody == null || playerBody.Player == null) return;
                if (!IsAIGoalieClientId(playerBody.OwnerClientId)) return;
                var gs = playerBody.Player.GameState.Value;
                if (gs.Role != PlayerRole.Goalie)
                {
                    gs.Role = PlayerRole.Goalie;
                    playerBody.Player.GameState.Value = gs;
                }
            }
        }

        [HarmonyPatch(typeof(ReplayRecorder), nameof(ReplayRecorder.Server_AddPlayerSpawnedEvent))]
        public static class FixReplayPlayerSpawnedRolePatch
        {
            [HarmonyPrefix]
            public static void Prefix(Player player)
            {
                if (player == null) return;
                if (!IsAIGoalieClientId(player.OwnerClientId)) return;
                var gs = player.GameState.Value;
                if (gs.Role != PlayerRole.Goalie)
                {
                    gs.Role = PlayerRole.Goalie;
                    player.GameState.Value = gs;
                }
            }
        }

        /// <summary>
        /// When a human player spawns as goalie, despawn ALL AI goalies on the same team.
        /// </summary>
        [HarmonyPatch(typeof(Player), nameof(Player.Server_SpawnCharacter))]
        public static class PlayerSpawnCharacterPatch
        {
            [HarmonyPostfix]
            public static void Postfix(Player __instance, Vector3 position, Quaternion rotation, PlayerRole role)
            {
                if (isProcessing) return;
                if (role != PlayerRole.Goalie) return;
                if (__instance == null || IsAIGoalie(__instance)) return;

                PlayerTeam team = __instance.Team;
                if (team == PlayerTeam.Red && RedAIGoalie != null) DespawnAIGoalie(PlayerTeam.Red);
                else if (team == PlayerTeam.Blue && BlueAIGoalie != null) DespawnAIGoalie(PlayerTeam.Blue);

                nextEvalTime = 0f;
            }
        }

        /// <summary>
        /// Postfix-correct the ServerFull rejection so fake-clientId AI goalies don't permanently
        /// consume real player slots. NetworkObject.Despawn doesn't remove the entry from
        /// ConnectedClientsList (there's no transport connection to close), so slots stay consumed
        /// after we despawn — until the server restarts.
        /// </summary>
        [HarmonyPatch(typeof(ConnectionApprovalManager), nameof(ConnectionApprovalManager.GetConnectionRejectionCode))]
        public static class FixServerFullCountPatch
        {
            [HarmonyPostfix]
            public static void Postfix(ConnectionApprovalManager __instance, ConnectionApproval connectionApproval, ref ConnectionRejectionCode? __result)
            {
                if (__result != ConnectionRejectionCode.ServerFull) return;
                try
                {
                    var cfg = __instance.ServerManager.ServerConfig;
                    int realCount = NetworkManager.Singleton.ConnectedClientsList.Count(c =>
                        c.ClientId != connectionApproval.ClientID && !IsAIGoalieClientId(c.ClientId));
                    if (realCount < cfg.maxPlayers) __result = null; // approve
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Puck), nameof(Puck.GetPlayerCollisions))]
        public static class FilterAIGoalieFromCollisionsPatch
        {
            [HarmonyPostfix]
            public static void Postfix(ref List<KeyValuePair<Player, float>> __result)
            {
                if (RedAIGoalie == null && BlueAIGoalie == null) return;
                __result = __result.Where(kvp => !IsAIGoalie(kvp.Key)).ToList();
            }
        }

        [HarmonyPatch(typeof(Puck), nameof(Puck.GetPlayerCollisionsByTeam))]
        public static class FilterAIGoalieFromCollisionsByTeamPatch
        {
            [HarmonyPostfix]
            public static void Postfix(ref List<KeyValuePair<Player, float>> __result)
            {
                if (RedAIGoalie == null && BlueAIGoalie == null) return;
                __result = __result.Where(kvp => !IsAIGoalie(kvp.Key)).ToList();
            }
        }
    }

    /// <summary>
    /// Lightweight vote tracker for /votegoalies. Toggles GoalieAIManager.AutoEnabled on success.
    /// Independent of the game's VoteManager (which has a fixed enum of vote types).
    /// </summary>
    public static class GoalieVote
    {
        private const float VoteDuration = 45f;

        public static bool IsActive { get; private set; }
        private static float endTime;
        private static bool targetState;  // what we're voting to set AutoEnabled to
        private static readonly HashSet<ulong> yesVotes = new HashSet<ulong>();
        private static int eligibleCount;
        private static int votesNeeded;

        /// <summary>
        /// Start a vote if one isn't active. Returns true on success.
        /// </summary>
        public static bool Start(Player initiator, out string reason)
        {
            reason = null;
            if (IsActive) { reason = "A goalie-AI vote is already in progress."; return false; }

            int real = Mathf.Max(1, CountRealPlayers());

            IsActive = true;
            endTime = Time.realtimeSinceStartup + VoteDuration;
            targetState = !GoalieAIManager.AutoEnabled;
            yesVotes.Clear();
            eligibleCount = real;
            votesNeeded = (real / 2) + 1; // simple majority — solo player passes instantly (1/1)

            // The initiator's call counts as a yes.
            if (initiator != null)
            {
                ulong initId = initiator.OwnerClientId;
                yesVotes.Add(initId);
            }

            // If the initiator alone already meets the threshold (e.g. solo player on the
            // server), pass the vote immediately rather than leaving it open for 45s.
            if (yesVotes.Count >= votesNeeded)
            {
                Finish(true);
            }
            return true;
        }

        /// <summary>
        /// Submit a yes vote. Returns (counted, finished, passed).
        /// </summary>
        public static (bool counted, bool finished, bool passed) Vote(ulong clientId)
        {
            if (!IsActive) return (false, false, false);
            if (!yesVotes.Add(clientId)) return (false, false, false);
            if (yesVotes.Count >= votesNeeded)
            {
                Finish(true);
                return (true, true, true);
            }
            return (true, false, false);
        }

        public static void Tick()
        {
            if (!IsActive) return;
            if (Time.realtimeSinceStartup >= endTime) Finish(false);
        }

        private static void Finish(bool passed)
        {
            IsActive = false;
            if (passed)
            {
                GoalieAIManager.SetAutoEnabled(targetState);
            }
            // Notify all clients (caller-side messaging via PracticeHelpers happens in the command handler too).
            string verb = targetState ? "ENABLE" : "DISABLE";
            string outcome = passed ? "<color=#00FF00>PASSED</color>" : "<color=#FF6666>FAILED</color>";
            PracticeHelpers.SendChatMessage(null, $"<size=70%>Vote to {verb} AI goalies: {outcome} ({yesVotes.Count}/{votesNeeded})</size>");
            yesVotes.Clear();
        }

        public static int CurrentYes => yesVotes.Count;
        public static int Needed => votesNeeded;
        public static bool TargetState => targetState;
        public static float SecondsRemaining => Mathf.Max(0f, endTime - Time.realtimeSinceStartup);

        private static int CountRealPlayers()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return 0;
            int n = 0;
            foreach (var c in nm.ConnectedClientsList)
            {
                if (GoalieAIManager.IsAIGoalieClientId(c.ClientId)) continue;
                if (FakePlayerDetector.IsAnyFakePlayer(c.PlayerObject?.GetComponent<Player>())) continue;
                n++;
            }
            return n;
        }
    }
}
