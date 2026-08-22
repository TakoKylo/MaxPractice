// PracticeManager.cs - Handles warmup phase management and cleanup

using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using MaxPractice;

public class PracticeManager : MonoBehaviour
{
    private float nextCheckTime = 0f;
    private float nextPuckCheckTime = 0f;
    private float nextPuckVisualCheckTime = 0f; // For validating cone/minefield puck visuals
    private GamePhase lastPhase = GamePhase.None;
    
    // Track if goalie AI was despawned during replay (to respawn after)
    private bool redGoalieAIDespawnedForReplay = false;
    private bool blueGoalieAIDespawnedForReplay = false;
    
    // Config shorthand
    private static MaxPractice.ModConfig cfg => ConfigManager.Config;

    void Start()
    {
        // Listen for when a player claims a position to instantly remove dummy if needed
        try
        {
            EventManager.AddEventListener("Event_OnPlayerPositionClaimedByChanged", OnPlayerPositionClaimed);
            EventManager.AddEventListener("Event_Everyone_OnPlayerPositionClaimedByPlayerChanged", OnPlayerPositionClaimed);
            Debug.Log("[MaxPractice] Added position claim listener");
        }
        catch (Exception) { }
        
        // Listen for game phase changes to cleanup dummies immediately
        try
        {
            EventManager.AddEventListener("Event_OnGamePhaseChanged", OnGamePhaseChanged);
            EventManager.AddEventListener("Event_Everyone_OnGameStateChanged", OnGamePhaseChanged);
            Debug.Log("[MaxPractice] Added game phase listener");
        }
        catch (Exception) { }
        
        // Listen for player disconnect to cleanup their traffic/passers
        try
        {
            EventManager.AddEventListener("Event_OnClientDisconnected", OnClientDisconnected);
            EventManager.AddEventListener("Event_Everyone_OnClientDisconnected", OnClientDisconnected);
            Debug.Log("[MaxPractice] Added client disconnect listener");
        }
        catch (Exception) { }
    }

    void OnDestroy()
    {
        try
        {
            EventManager.RemoveEventListener("Event_OnPlayerPositionClaimedByChanged", OnPlayerPositionClaimed);
            EventManager.RemoveEventListener("Event_Everyone_OnPlayerPositionClaimedByPlayerChanged", OnPlayerPositionClaimed);
        }
        catch (Exception) { }
        
        try
        {
            EventManager.RemoveEventListener("Event_OnGamePhaseChanged", OnGamePhaseChanged);
            EventManager.RemoveEventListener("Event_Everyone_OnGameStateChanged", OnGamePhaseChanged);
        }
        catch (Exception) { }
        
        try
        {
            EventManager.RemoveEventListener("Event_OnClientDisconnected", OnClientDisconnected);
            EventManager.RemoveEventListener("Event_Everyone_OnClientDisconnected", OnClientDisconnected);
        }
        catch (Exception) { }
    }
    
    private void OnClientDisconnected(Dictionary<string, object> message)
    {
        try
        {
            if (!NetworkManager.Singleton.IsServer) return;
            
            ulong clientId = (ulong)message["clientId"];

            // Keyed by client id alone, so they run first and unconditionally.
            //
            // These two used to sit at the BOTTOM of this method, behind three early returns
            // - no PlayerManager, no Player for the id, no resolvable steamId. On a real
            // disconnect the Player is usually already despawned when this event arrives, so
            // `player == null` was the ordinary path rather than the exceptional one, and the
            // whole cleanup below was being skipped along with them.
            RinkSheets.OnClientDisconnected(clientId);
            ClientVersionCheck.ForgetClient(clientId);

            // The other key per-player state may be filed under. GetSteamIdFromPlayer prefers
            // OwnerClientId, so when the Player has gone and we cannot ask, clientId is not a
            // guess - it is the value those entries were most likely written with.
            ulong steamId = clientId;
            var playerManager = MonoBehaviourSingleton<PlayerManager>.Instance;
            var player = playerManager != null ? playerManager.GetPlayerByClientId(clientId) : null;
            if (player != null)
            {
                ulong resolved = PracticeHelpers.GetSteamIdFromPlayer(player);
                if (resolved != 0) steamId = resolved;
            }

            // Clean up traffic and props owned by this player
            MaxPracticePlugin.CleanupPlayerTraffic(steamId);
            MaxPracticePlugin.CleanupPlayerProps(steamId);

            // Wipe per-player command/yoyo state so HashSets/Dicts don't accumulate
            // dead entries over a long-running server session. Pass both keys —
            // entries may have been written under either depending on whether
            // GetSteamIdFromPlayer resolved to OwnerClientId or SteamId.
            MaxPracticePlugin.CleanupPlayerState(steamId, clientId);
            if (YoyoManager.Instance != null)
                YoyoManager.Instance.CleanupForPlayer(steamId, clientId);
            SkaterAI.ForgetRecordings(steamId, clientId);
        }
        catch (Exception) { }
    }

    private void OnPlayerPositionClaimed(Dictionary<string, object> message)
    {
        try
        {
            if (!NetworkManager.Singleton.IsServer) return;
            
            var playerPosition = message.ContainsKey("playerPosition")
                ? message["playerPosition"] as PlayerPosition
                : null;
            var oldClaimedBy = message.ContainsKey("oldClaimedBy")
                ? message["oldClaimedBy"] as Player
                : (message.ContainsKey("oldClaimedByPlayer") ? message["oldClaimedByPlayer"] as Player : null);
            var newClaimedBy = message.ContainsKey("newClaimedBy")
                ? message["newClaimedBy"] as Player
                : (message.ContainsKey("newClaimedByPlayer") ? message["newClaimedByPlayer"] as Player : null);
            
            if (playerPosition == null) return;
            
            // Check if a player LEFT their position (oldClaimedBy exists but newClaimedBy is different/null)
            if (oldClaimedBy != null && oldClaimedBy != newClaimedBy)
            {
                // Skip if it's a fake player (our dummy)
                if (!MaxPracticePlugin.FakePlayers.Contains(oldClaimedBy))
                {
                    // Real player left their position - clean up their traffic/passers
                    ulong steamId = PracticeHelpers.GetSteamIdFromPlayer(oldClaimedBy);
                    if (steamId != 0)
                    {
                        MaxPracticePlugin.CleanupPlayerTraffic(steamId);
                        MaxPracticePlugin.CleanupPlayerProps(steamId);
                        Debug.Log($"[MaxPractice] Cleaned up traffic and props for player who left position: {steamId}");
                    }
                }
            }
            
            // Check if someone claimed a goalie position
            if (newClaimedBy != null && playerPosition.Role == PlayerRole.Goalie)
            {
                // Skip if the claimer is a fake player (our dummy)
                if (MaxPracticePlugin.FakePlayers.Contains(newClaimedBy)) return;
                
                // Real player claimed goalie - remove the dummy for that team
                if (playerPosition.Team == PlayerTeam.Red && MaxPracticePlugin.RedTeamDummy != null)
                {
                    MaxPracticePlugin.FakePlayers.Remove(MaxPracticePlugin.RedTeamDummy);
                    if (MaxPracticePlugin.RedTeamDummy.NetworkObject?.IsSpawned == true)
                        MaxPracticePlugin.RedTeamDummy.NetworkObject.Despawn(true);
                    MaxPracticePlugin.RedTeamDummy = null;
                }
                else if (playerPosition.Team == PlayerTeam.Blue && MaxPracticePlugin.BlueTeamDummy != null)
                {
                    MaxPracticePlugin.FakePlayers.Remove(MaxPracticePlugin.BlueTeamDummy);
                    if (MaxPracticePlugin.BlueTeamDummy.NetworkObject?.IsSpawned == true)
                        MaxPracticePlugin.BlueTeamDummy.NetworkObject.Despawn(true);
                    MaxPracticePlugin.BlueTeamDummy = null;
                }
            }
        }
        catch (Exception) { }
    }

    private void OnGamePhaseChanged(Dictionary<string, object> message)
    {
        try
        {
            if (!NetworkManager.Singleton.IsServer) return;
            
            GamePhase newPhase;
            GamePhase oldPhase;

            if (message.ContainsKey("newGamePhase") && message.ContainsKey("oldGamePhase"))
            {
                newPhase = (GamePhase)message["newGamePhase"];
                oldPhase = (GamePhase)message["oldGamePhase"];
            }
            else if (message.ContainsKey("newGameState") && message.ContainsKey("oldGameState"))
            {
                newPhase = ((GameState)message["newGameState"]).Phase;
                oldPhase = ((GameState)message["oldGameState"]).Phase;
            }
            else
            {
                return;
            }
            
            // If leaving warmup, cleanup warmup features (but goalie AI can persist based on config)
            if (oldPhase == GamePhase.Warmup && newPhase != GamePhase.Warmup)
            {
                CleanupWarmupFeatures();
            }
            
            // Handle replay phase - despawn goalie AI during replay, respawn after
            if (cfg.GoalieAIPersistDuringGame)
            {
                // Entering replay - despawn goalie AI temporarily
                if (newPhase == GamePhase.Replay)
                {
                    DespawnGoalieAIForReplay();
                }
                // Leaving replay - respawn goalie AI if it was despawned
                else if (oldPhase == GamePhase.Replay)
                {
                    RespawnGoalieAIAfterReplay();
                }
            }
        }
        catch (Exception) { }
    }
    
    private void DespawnGoalieAIForReplay()
    {
        try
        {
            // Despawn red goalie AI if exists
            if (MaxPracticePlugin.RedTeamDummy != null && MaxPracticePlugin.RedTeamDummy.NetworkObject?.IsSpawned == true)
            {
                MaxPracticePlugin.FakePlayers.Remove(MaxPracticePlugin.RedTeamDummy);
                MaxPracticePlugin.RedTeamDummy.NetworkObject.Despawn(true);
                MaxPracticePlugin.RedTeamDummy = null;
                redGoalieAIDespawnedForReplay = true;
                ConfigManager.Dbg("[MaxPractice] Despawned red goalie AI for replay");
            }
            
            // Despawn blue goalie AI if exists
            if (MaxPracticePlugin.BlueTeamDummy != null && MaxPracticePlugin.BlueTeamDummy.NetworkObject?.IsSpawned == true)
            {
                MaxPracticePlugin.FakePlayers.Remove(MaxPracticePlugin.BlueTeamDummy);
                MaxPracticePlugin.BlueTeamDummy.NetworkObject.Despawn(true);
                MaxPracticePlugin.BlueTeamDummy = null;
                blueGoalieAIDespawnedForReplay = true;
                ConfigManager.Dbg("[MaxPractice] Despawned blue goalie AI for replay");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MaxPractice] Error despawning goalie AI for replay: {ex}");
        }
    }
    
    private void RespawnGoalieAIAfterReplay()
    {
        try
        {
            StartCoroutine(RespawnGoalieAIDelayed());
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MaxPractice] Error respawning goalie AI after replay: {ex}");
        }
    }
    
    private IEnumerator RespawnGoalieAIDelayed()
    {
        // Wait a bit for game state to settle
        yield return new WaitForSeconds(0.5f);

        // (A FindFirstObjectByType<UIChat> used to sit here. Nothing below reads it, and on a
        // dedicated server there is no UIChat to find, so it was a whole-scene scan for a
        // value that was discarded.)

        // Respawn red goalie AI if it was despawned and no real goalie exists
        if (redGoalieAIDespawnedForReplay)
        {
            redGoalieAIDespawnedForReplay = false;
            if (!HasRealGoalie(PlayerTeam.Red))
            {
                SpawnGoalieAI(PlayerTeam.Red);
            }
        }
        
        // Respawn blue goalie AI if it was despawned and no real goalie exists
        if (blueGoalieAIDespawnedForReplay)
        {
            blueGoalieAIDespawnedForReplay = false;
            if (!HasRealGoalie(PlayerTeam.Blue))
            {
                SpawnGoalieAI(PlayerTeam.Blue);
            }
        }
    }

    void Update()
    {
        // Runs on clients too - this is what puts the cone visual on their screen,
        // so it has to sit above the server-only guard.
        PropNetwork.Tick();

        // Same: the yoyo string is drawn client-side from a server announcement.
        YoyoStringNetwork.Tick();

        // Same deal: a client builds its own copy of every practice sheet the server
        // announces, so this cannot sit behind the server guard either.
        RinkSheets.Tick();
        ClientVersionCheck.Tick();

        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;

        PracticeHelpers.FlushPendingChats();

        // Goalie AI lifecycle + vote tracking every frame (cheap; internal throttles guard the work).
        GoalieAIManager.Tick();
        GoalieVote.Tick();

        // Notify the goalie AI manager of phase changes so it can run intermission/sad/teleport hooks.
        var gmPhase = NetworkBehaviourSingleton<GameManager>.Instance;
        if (gmPhase != null) GoalieAIManager.NotifyPhase(gmPhase.Phase);

        // These two carry their own intervals and so have to be sampled every frame to
        // honour them. They used to sit BELOW the 10 s gate, which is a coarser sieve than
        // either of them: the 5 s goalie check only ever got asked on a 10 s boundary, so
        // it ran every 10 s, and the 15 s visual check - needing 15 s elapsed but only ever
        // asked every 10 s - actually ran every 20 s.
        if (gmPhase != null && gmPhase.Phase == GamePhase.Warmup)
        {
            if (Time.realtimeSinceStartup >= nextPuckCheckTime)
            {
                nextPuckCheckTime = Time.realtimeSinceStartup + 5f;
                CheckForRealGoalies();
            }

            // Periodically validate cone/minefield puck visuals (fixes mesh/material loss
            // on player join/leave)
            if (Time.realtimeSinceStartup >= nextPuckVisualCheckTime)
            {
                nextPuckVisualCheckTime = Time.realtimeSinceStartup + 15f;
                ValidateHandlePuckVisuals();
            }
        }

        if (Time.realtimeSinceStartup < nextCheckTime) return;
        nextCheckTime = Time.realtimeSinceStartup + 10f;

        var gm = NetworkBehaviourSingleton<GameManager>.Instance;
        if (gm != null)
        {
            if (lastPhase != gm.Phase)
            {
                lastPhase = gm.Phase;
                if (gm.Phase != GamePhase.Warmup)
                {
                    CleanupWarmupFeatures();
                }
            }
        }

    }
    
    private void ValidateHandlePuckVisuals()
    {
        try
        {
            // Check all handle pucks (cones/minefield) for valid visuals
            foreach (var kvp in MaxPracticePlugin.HandlePucks)
            {
                var puckList = kvp.Value;
                if (puckList == null) continue;
                
                foreach (var puck in puckList)
                {
                    if (puck == null || puck.gameObject == null) continue;

                    // Cone pucks want the cone back, not the puck mesh. Apply is a
                    // no-op when the cone is still there.
                    // ConeVisual re-asserts its own renderers, colliders, and
                    // player/stick collision ignores every 2s, so this only has to
                    // rebuild a cone that lost its GameObject entirely.
                    if (MaxPracticePlugin.PropPucks.TryGetValue(puck, out var kind))
                    {
                        // Hand the owner back in: a shooter rebuilt without one loses its
                        // team colour and its nameplate, and this sweep runs every 15 s.
                        Player propOwner = null;
                        if (MaxPracticePlugin.PropOwner.TryGetValue(puck, out ulong ownerSteamId))
                            propOwner = PracticeHelpers.FindPlayerBySteamId(ownerSteamId);

                        PropNetwork.ApplyLocal(puck, kind, true, propOwner);
                        continue;
                    }

                    PracticeHelpers.ValidateAndRepairPuckVisuals(puck);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[MaxPractice] Error validating handle puck visuals: {ex.Message}");
        }
    }

    private void CleanupWarmupFeatures()
    {
        MaxPracticePlugin.InfiniteStaminaPlayers.Clear();
        
        // Both session dictionaries, not just the shooter one. The tip-practice routine
        // has no phase check of its own - it only ends on its 120 s timer - so leaving it
        // running kept firing pucks through the crease all through the faceoff and live
        // play, and the stale entry made the player's next /tipprac read as "stop".
        var gameMgr = NetworkBehaviourSingleton<GameManager>.Instance;
        foreach (var kv in MaxPracticePlugin.ActiveShooterSessions)
        {
            if (gameMgr != null)
                gameMgr.StopCoroutine(kv.Value);
        }
        MaxPracticePlugin.ActiveShooterSessions.Clear();

        foreach (var kv in MaxPracticePlugin.ActiveTipPracSessions)
        {
            if (gameMgr != null)
                gameMgr.StopCoroutine(kv.Value);
        }
        MaxPracticePlugin.ActiveTipPracSessions.Clear();

        foreach (var kv in MaxPracticePlugin.SpawnedPucks)
        {
            foreach (var p in kv.Value)
            {
                if (p != null && p.gameObject != null)
                    try { UnityEngine.Object.Destroy(p.gameObject); } catch { }
            }
        }
        MaxPracticePlugin.SpawnedPucks.Clear();
        
        // Clear handle pucks (cones/minefield) on phase change
        foreach (var kv in MaxPracticePlugin.HandlePucks)
        {
            foreach (var p in kv.Value)
            {
                if (p != null && p.gameObject != null)
                    try { UnityEngine.Object.Destroy(p.gameObject); } catch { }
            }
        }
        MaxPracticePlugin.HandlePucks.Clear();
        MaxPracticePlugin.HandleUseCount.Clear();
        MaxPracticePlugin.MinefieldUseCount.Clear();
        MaxPracticePlugin.PropPucks.Clear();
        MaxPracticePlugin.PropOwner.Clear();
        MaxPracticePlugin.PlayerShooter.Clear();
        MaxPracticePlugin.PlayerMiniNet.Clear();

        // Keep goalie dummies through phase transitions if EITHER the always-on config OR a
        // passed /votegoalies vote (GoalieAIManager.AutoEnabled) says they should persist.
        // Otherwise CleanupDummies wipes them on the Warmup→PreGame transition and we end up
        // with no AI goalies until they're re-evaluated 1s later.
        if (!cfg.GoalieAIPersistDuringGame && !GoalieAIManager.AutoEnabled)
        {
            MaxPracticePlugin.CleanupDummies();
        }
        
        // Extra sheets are a warmup thing. Leaving them standing through a game would
        // strand anyone on one somewhere the game's own spawn logic knows nothing about.
        RinkSheets.CloseAll("left warmup");

        // Yoyo, pass and tap settings are warmup tools too, and several of them name a
        // shooter prop that the HandlePucks sweep above has already destroyed.
        MaxPractice.YoyoManager.ResetAllPlayerState();

        // Traffic RECORDING is not an AI skater, so ClearAllAISkaters never touched it and
        // nothing else did either: RecordMovement loops on this dictionary, so a live
        // recording kept sampling straight through the faceoff and into play. Clearing the
        // dictionary is what ends the coroutine - it has no handle to stop.
        SkaterAI.ActiveRecordings.Clear();

        // Always clear traffic and passers on phase change (they're not goalies)
        SkaterAI.ClearAllAISkaters();
        MaxPracticePlugin.TrafficDummies.Clear();
        MaxPracticePlugin.PlayerOwnedTraffic.Clear();
    }

    private void CheckForRealGoalies()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;
        
        var gm = NetworkBehaviourSingleton<GameManager>.Instance;
        if (gm == null) return;
        
        // Don't check during replay phase
        if (gm.Phase == GamePhase.Replay) return;

        bool hasRealRedGoalie = HasRealGoalie(PlayerTeam.Red);
        bool hasRealBlueGoalie = HasRealGoalie(PlayerTeam.Blue);

        // Remove goalie AI if real goalie exists
        if (hasRealRedGoalie && MaxPracticePlugin.RedTeamDummy != null)
        {
            MaxPracticePlugin.FakePlayers.Remove(MaxPracticePlugin.RedTeamDummy);
            if (MaxPracticePlugin.RedTeamDummy.NetworkObject?.IsSpawned == true)
                MaxPracticePlugin.RedTeamDummy.NetworkObject.Despawn(true);
            MaxPracticePlugin.RedTeamDummy = null;
        }

        if (hasRealBlueGoalie && MaxPracticePlugin.BlueTeamDummy != null)
        {
            MaxPracticePlugin.FakePlayers.Remove(MaxPracticePlugin.BlueTeamDummy);
            if (MaxPracticePlugin.BlueTeamDummy.NetworkObject?.IsSpawned == true)
                MaxPracticePlugin.BlueTeamDummy.NetworkObject.Despawn(true);
            MaxPracticePlugin.BlueTeamDummy = null;
        }
        
        // Auto-spawn goalie AI if no real goalie exists (when GoalieAIPersistDuringGame is enabled)
        if (cfg.GoalieAIPersistDuringGame)
        {
            // Auto-spawn red goalie AI if no real red goalie and no AI exists
            if (!hasRealRedGoalie && MaxPracticePlugin.RedTeamDummy == null)
            {
                SpawnGoalieAI(PlayerTeam.Red);
            }
            
            // Auto-spawn blue goalie AI if no real blue goalie and no AI exists
            if (!hasRealBlueGoalie && MaxPracticePlugin.BlueTeamDummy == null)
            {
                SpawnGoalieAI(PlayerTeam.Blue);
            }
        }
    }
    
    private bool HasRealGoalie(PlayerTeam team)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return false;
        
        foreach (var client in nm.ConnectedClientsList)
        {
            var p = client?.PlayerObject?.GetComponent<Player>();
            if (p == null) continue;
            if (MaxPracticePlugin.FakePlayers.Contains(p)) continue;
            
            if (PracticeHelpers.GetPlayerRole(p) == PlayerRole.Goalie && PracticeHelpers.GetPlayerTeam(p) == team)
            {
                return true;
            }
        }
        return false;
    }
    
    private void SpawnGoalieAI(PlayerTeam team)
    {
        try
        {
            GoalieAIManager.SpawnAIGoalie(team);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MaxPractice] Error auto-spawning goalie: {ex}");
        }
    }
}
