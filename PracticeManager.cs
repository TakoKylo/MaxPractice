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
            
            // Look up the steamId for this clientId
            var playerManager = MonoBehaviourSingleton<PlayerManager>.Instance;
            if (playerManager == null) return;
            
            var player = playerManager.GetPlayerByClientId(clientId);
            if (player == null) return;
            
            // Get steamId using helper method (handles FixedString conversion)
            ulong steamId = PracticeHelpers.GetSteamIdFromPlayer(player);
            if (steamId == 0) return;
            
            // Clean up traffic owned by this player
            MaxPracticePlugin.CleanupPlayerTraffic(steamId);
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
                        Debug.Log($"[MaxPractice] Cleaned up traffic for player who left position: {steamId}");
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
        
        var ui = UnityEngine.Object.FindFirstObjectByType<UIChat>();
        
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
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;

        PracticeHelpers.FlushPendingChats();

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

        // Check for real goalies periodically
        if (gm != null && gm.Phase == GamePhase.Warmup && Time.realtimeSinceStartup >= nextPuckCheckTime)
        {
            nextPuckCheckTime = Time.realtimeSinceStartup + 5f;
            CheckForRealGoalies();
        }
        
        // Periodically validate cone/minefield puck visuals (fixes mesh/material loss on player join/leave)
        if (gm != null && gm.Phase == GamePhase.Warmup && Time.realtimeSinceStartup >= nextPuckVisualCheckTime)
        {
            nextPuckVisualCheckTime = Time.realtimeSinceStartup + 15f; // Check every 15 seconds
            ValidateHandlePuckVisuals();
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
        
        foreach (var kv in MaxPracticePlugin.ActiveShooterSessions)
        {
            var gm = NetworkBehaviourSingleton<GameManager>.Instance;
            if (gm != null)
                gm.StopCoroutine(kv.Value);
        }
        MaxPracticePlugin.ActiveShooterSessions.Clear();
        
        foreach (var kv in MaxPracticePlugin.SpawnedPucks)
        {
            foreach (var p in kv.Value)
            {
                if (p?.gameObject != null)
                    try { UnityEngine.Object.Destroy(p.gameObject); } catch { }
            }
        }
        MaxPracticePlugin.SpawnedPucks.Clear();
        
        // Clear handle pucks (cones/minefield) on phase change
        foreach (var kv in MaxPracticePlugin.HandlePucks)
        {
            foreach (var p in kv.Value)
            {
                if (p?.gameObject != null)
                    try { UnityEngine.Object.Destroy(p.gameObject); } catch { }
            }
        }
        MaxPracticePlugin.HandlePucks.Clear();
        MaxPracticePlugin.HandleUseCount.Clear();
        
        // Only cleanup goalie AI if config says so (default: cleanup on phase change)
        if (!cfg.GoalieAIPersistDuringGame)
        {
            MaxPracticePlugin.CleanupDummies();
        }
        
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
            bool isRed = team == PlayerTeam.Red;
            
            var playerManager = MonoBehaviourSingleton<PlayerManager>.Instance;
            if (playerManager == null) return;

            var playerPrefabField = typeof(PlayerManager).GetField("playerPrefab", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Player playerPrefab = (Player)playerPrefabField?.GetValue(playerManager);
            if (playerPrefab == null) return;

            Player botPlayer = UnityEngine.Object.Instantiate(playerPrefab);
            NetworkObject netObj = botPlayer.GetComponent<NetworkObject>();

            ulong fakeClientId = 1111111UL + (ulong)(isRed ? 1 : 0);
            netObj.SpawnWithOwnership(fakeClientId, true);

            botPlayer.Username.Value = new Unity.Collections.FixedString32Bytes(isRed ? "DummyRed" : "DummyBlue");
            botPlayer.SteamId.Value = new Unity.Collections.FixedString32Bytes(isRed ? "DummyRed" : "DummyBlue"); // Set SteamId for filtering
            botPlayer.Server_SetGameState(phase: null, team: team, role: PlayerRole.Goalie, delay: 0f);
            botPlayer.Number.Value = 62;
            // B310: Cosmetics are now int-based IDs - but properties don't exist in current build
            // try { botPlayer.GoalieRedSkinID.Value = 0; } catch (Exception) { }
            // try { botPlayer.GoalieBlueSkinID.Value = 0; } catch (Exception) { }

            // Dynamic goal position (supports CompAdjustments goal scaling)
            Vector3 goalPos = isRed ? new Vector3(0f, 0f, -40.23f) : new Vector3(0f, 0f, 40.23f);
            try
            {
                var goals = UnityEngine.Object.FindObjectsByType<Goal>(FindObjectsSortMode.None);
                foreach (var g in goals)
                {
                    if (g == null) continue;
                    // Determine team from position (red goal at z < 0, blue at z > 0)
                    bool isGoalRed = g.transform.position.z < 0;
                    if (isGoalRed == isRed)
                    {
                        goalPos = g.transform.position;
                        goalPos.y = 0f;
                        break;
                    }
                }
            }
            catch (Exception) { }
            Vector3 spawnPos = goalPos;
            spawnPos.z += isRed ? 1.5f : -1.5f;
            spawnPos.y = 0f;

            Quaternion spawnRot = Quaternion.LookRotation(isRed ? Vector3.forward : Vector3.back);
            botPlayer.Server_SpawnCharacter(spawnPos, spawnRot, PlayerRole.Goalie);

            MaxPracticePlugin.FakePlayers.Add(botPlayer);
            if (isRed)
                MaxPracticePlugin.RedTeamDummy = botPlayer;
            else
                MaxPracticePlugin.BlueTeamDummy = botPlayer;

            SimpleGoalieAI ai = botPlayer.gameObject.AddComponent<SimpleGoalieAI>();
            ai.controlledPlayer = botPlayer;
            ai.team = team;

                ConfigManager.Dbg($"[MaxPractice] Auto-spawned {(isRed ? "red" : "blue")} goalie dummy with AI attached");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MaxPractice] Error auto-spawning goalie: {ex}");
        }
    }
}
