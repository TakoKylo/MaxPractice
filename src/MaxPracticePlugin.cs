// MaxPractice Plugin - Practice mode features for Puck game
// Handles: Save practice, dummy goalies, infinite stamina, puck spawning, handling drills

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using MaxPractice;

public class MaxPracticePlugin : IPuckMod
{
    public static MaxPracticePlugin Instance { get; private set; }
    private Harmony harmony;

    // Track active practice sessions (steamId -> coroutine)
    public static Dictionary<ulong, Coroutine> ActiveShooterSessions = new Dictionary<ulong, Coroutine>();
    
    // Track active tip practice sessions (steamId -> coroutine)
    public static Dictionary<ulong, Coroutine> ActiveTipPracSessions = new Dictionary<ulong, Coroutine>();
    
    // Track spawned pucks per session
    public static Dictionary<ulong, List<Puck>> SpawnedPucks = new Dictionary<ulong, List<Puck>>();

    // Track spawned dummy goalies per team
    public static Player RedTeamDummy = null;
    public static Player BlueTeamDummy = null;

    // Fake player registry
    public static HashSet<Player> FakePlayers = new HashSet<Player>();
    
    // Infinite stamina tracking (only during warmup)
    public static HashSet<ulong> InfiniteStaminaPlayers = new HashSet<ulong>();
    
    // Track last puck spawn time per player (for cooldown)
    public static Dictionary<ulong, float> LastPuckSpawnTime = new Dictionary<ulong, float>();
    
    // Track last handle command time per player (for cooldown)
    public static Dictionary<ulong, float> LastHandleSpawnTime = new Dictionary<ulong, float>();
    
    // Track handle pucks per player (steamId -> list of pucks)
    public static Dictionary<ulong, List<Puck>> HandlePucks = new Dictionary<ulong, List<Puck>>();
    
    // Track handle command uses per player (steamId -> use count)
    public static Dictionary<ulong, int> HandleUseCount = new Dictionary<ulong, int>();
    
    // Track yoyo mode per player (steamId -> enabled)
    public static HashSet<ulong> YoyoPlayers = new HashSet<ulong>();
    
    // Track active pass mode per player (steamId -> enabled) - keeps spawning passes until /unpass
    public static HashSet<ulong> ActivePassPlayers = new HashSet<ulong>();
    
    // Track traffic dummies (legacy - use SkaterAI.AISkaters instead)
    public static List<Player> TrafficDummies = new List<Player>();
    
    // Track traffic objects (simple capsules for traffic)
    public static List<GameObject> TrafficObjects = new List<GameObject>();
    
    // Track which player owns which traffic/passers (steamId -> list of AI players)
    public static Dictionary<ulong, List<Player>> PlayerOwnedTraffic = new Dictionary<ulong, List<Player>>();
    
    // NullRef suppression for fake players using ILogHandler
    private static bool _nullRefHandlerRegistered = false;
    private static NullRefSuppressingLogHandler _logHandler;
    
    /// <summary>
    /// Suppress NullReferenceException spam from game components on fake players.
    /// Call with frames > 0 to suppress for that many frames.
    /// </summary>
    public static void SuppressNullRefsFor(int frames)
    {
        if (_logHandler != null)
            _logHandler.SuppressFrameCount = frames;
    }
    
    private static void RegisterNullRefSuppression()
    {
        if (_nullRefHandlerRegistered) return;
        
        try
        {
            var defaultHandler = Debug.unityLogger.logHandler;
            _logHandler = new NullRefSuppressingLogHandler(defaultHandler);
            Debug.unityLogger.logHandler = _logHandler;
            _nullRefHandlerRegistered = true;
        }
        catch (Exception) { }
    }
    
    /// <summary>
    /// Custom ILogHandler that filters out NullReferenceExceptions from fake player components
    /// </summary>
    private class NullRefSuppressingLogHandler : ILogHandler
    {
        private readonly ILogHandler _defaultHandler;
        public int SuppressFrameCount = 0;
        
        public NullRefSuppressingLogHandler(ILogHandler defaultHandler)
        {
            _defaultHandler = defaultHandler;
        }
        
        public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
        {
            _defaultHandler.LogFormat(logType, context, format, args);
        }
        
        public void LogException(Exception exception, UnityEngine.Object context)
        {
            // Always suppress NullReferenceExceptions from known sources
            if (exception is NullReferenceException)
            {
                string stackTrace = exception.StackTrace ?? "";
                
                // Decrement frame counter
                if (SuppressFrameCount > 0)
                    SuppressFrameCount--;
                
                // During suppression window, suppress all NullRefs
                if (SuppressFrameCount > 0)
                    return;
                
                // Always suppress NullRefs from known problematic sources on fake players
                if (stackTrace.Contains("StickPositioner") || stackTrace.Contains("PlayerInput") ||
                    stackTrace.Contains("PlayerBodyV2") || stackTrace.Contains("PlayerBody") || stackTrace.Contains("Movement") ||
                    stackTrace.Contains("Stick.") || stackTrace.Contains("CompetitivePuckTweaks") ||
                    stackTrace.Contains("CompetitiveAdjustments") || stackTrace.Contains("COMPADJUST") ||
                    stackTrace.Contains("SkaterAI") || stackTrace.Contains("MaxPractice") ||
                    stackTrace.Contains("GoalieAI") || stackTrace.Contains("NetworkVariable") ||
                    stackTrace.Contains("ServerValue"))
                    return;
            }
            
            // Pass through all other exceptions
            _defaultHandler.LogException(exception, context);
        }
    }

    public MaxPracticePlugin()
    {
        Instance = this;
        harmony = new Harmony("GAFURIX.MaxPracticePlugin");
        ConfigManager.Log("MaxPracticePlugin constructed");
    }

    public bool OnEnable()
    {
        try
        {
            // Initialize config system first
            ConfigManager.Initialize();
            
            harmony.PatchAll();
            
            // Register NullRef suppression handler EARLY - before any fake players spawn
            RegisterNullRefSuppression();

            var go = new GameObject("MaxPracticeManager");
            go.AddComponent<PracticeManager>();
            go.AddComponent<WarmupGoalDetector>();
            go.AddComponent<YoyoManager>();
            
            // Add UI only on client (not dedicated server)
            if (!Application.isBatchMode && 
                UnityEngine.SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                go.AddComponent<MaxPracticeUI>();
            }
            
            UnityEngine.Object.DontDestroyOnLoad(go);
            ConfigManager.Log("Added PracticeManager, WarmupGoalDetector, and YoyoManager components");

            // Patch stamina depletion
            var playerBodyType = AccessTools.TypeByName("PlayerBody") ?? AccessTools.TypeByName("PlayerBodyV2");
            var staminaMethod = playerBodyType != null ? AccessTools.DeclaredMethod(playerBodyType, "FixedUpdate") : null;
            if (staminaMethod != null)
            {
                harmony.Patch(staminaMethod, postfix: new HarmonyMethod(typeof(PracticeStaminaPatch), "Postfix"));
                ConfigManager.Log("Patched PlayerBody.FixedUpdate (infinite stamina)");
            }

            ConfigManager.Log("Enabled and patched");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError("[MaxPractice] Failed to enable: " + e);
            return false;
        }
    }

    public bool OnDisable()
    {
        try
        {
            var manager = GameObject.Find("MaxPracticeManager");
            if (manager != null) UnityEngine.Object.Destroy(manager);

            // Cleanup dummy goalies
            CleanupDummies();
            
            harmony.UnpatchSelf();
            ConfigManager.Log("Disabled");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError("[MaxPractice] Failed to disable: " + e);
            return false;
        }
    }

    public static void CleanupDummies()
    {
        if (RedTeamDummy != null)
        {
            FakePlayers.Remove(RedTeamDummy);
            if (RedTeamDummy.NetworkObject != null && RedTeamDummy.NetworkObject.IsSpawned)
                RedTeamDummy.NetworkObject.Despawn(true);
            RedTeamDummy = null;
        }
        if (BlueTeamDummy != null)
        {
            FakePlayers.Remove(BlueTeamDummy);
            if (BlueTeamDummy.NetworkObject != null && BlueTeamDummy.NetworkObject.IsSpawned)
                BlueTeamDummy.NetworkObject.Despawn(true);
            BlueTeamDummy = null;
        }
        // Clean up traffic dummies (legacy player-based)
        foreach (var trafficPlayer in TrafficDummies)
        {
            if (trafficPlayer != null)
            {
                FakePlayers.Remove(trafficPlayer);
                if (trafficPlayer.NetworkObject != null && trafficPlayer.NetworkObject.IsSpawned)
                    trafficPlayer.NetworkObject.Despawn(true);
            }
        }
        TrafficDummies.Clear();
        
        // Clean up traffic objects (simple capsules)
        foreach (var trafficObj in TrafficObjects)
        {
            if (trafficObj != null)
                UnityEngine.Object.Destroy(trafficObj);
        }
        TrafficObjects.Clear();
        
        // Clear ownership tracking
        PlayerOwnedTraffic.Clear();
    }
    
    /// <summary>
    /// Clean up traffic/passers owned by a specific player (call on disconnect or position leave)
    /// Also respawns a puck for the player if they're still connected
    /// </summary>
    public static void CleanupPlayerTraffic(ulong steamId)
    {
        if (!PlayerOwnedTraffic.TryGetValue(steamId, out var ownedTraffic))
            return;
        
        int count = 0;
        foreach (var trafficPlayer in ownedTraffic.ToArray())
        {
            if (trafficPlayer == null) continue;
            
            try
            {
                // Remove from all tracking lists
                FakePlayers.Remove(trafficPlayer);
                TrafficDummies.Remove(trafficPlayer);
                MaxPractice.SkaterAI.AISkaters.Remove(trafficPlayer);
                MaxPractice.SkaterAI.RedAISkaters.Remove(trafficPlayer);
                MaxPractice.SkaterAI.BlueAISkaters.Remove(trafficPlayer);
                
                // Despawn
                if (trafficPlayer.NetworkObject != null && trafficPlayer.NetworkObject.IsSpawned)
                {
                    trafficPlayer.NetworkObject.Despawn(true);
                    count++;
                }
            }
            catch (Exception) { }
        }
        
        ownedTraffic.Clear();
        PlayerOwnedTraffic.Remove(steamId);
        
        if (count > 0)
        {
            ConfigManager.Dbg($"[MaxPractice] Cleaned up {count} traffic/passers for player {steamId}");
            
            // Respawn a puck for the player if they're still connected
            try
            {
                var playerManager = MonoBehaviourSingleton<PlayerManager>.Instance;
                var puckManager = MonoBehaviourSingleton<PuckManager>.Instance;
                if (playerManager != null && puckManager != null)
                {
                    // Find the player by steamId
                    var steamIdString = new Unity.Collections.FixedString32Bytes(steamId.ToString());
                    Player foundPlayer = playerManager.GetPlayerBySteamId(steamIdString);
                    
                    if (foundPlayer?.PlayerBody != null)
                    {
                        // Get spawn position in front of player
                        var prb = foundPlayer.PlayerBody.GetComponent<Rigidbody>();
                        Vector3 vel = prb != null ? prb.linearVelocity : Vector3.zero;
                        Vector3 pos = foundPlayer.PlayerBody.transform.position + foundPlayer.PlayerBody.transform.forward * 2f + Vector3.up * 0.1f;
                        Quaternion rot = Quaternion.identity;
                        
                        var respawnedPuck = PracticeHelpers.SpawnPuckWithCleanup(pos, rot, vel, false);
                        if (respawnedPuck != null)
                            YoyoManager.RegisterPuckForPlayer(respawnedPuck, foundPlayer);
                        ConfigManager.Dbg($"[MaxPractice] Respawned puck for player {steamId}");
                    }
                }
            }
            catch (Exception) { }
        }
    }

    // Coroutine for shooting pucks at the goal
    public IEnumerator ShooterRoutine(ulong steamId, Vector3 targetGoalPos)
    {
        var cfg = ConfigManager.Config;
        ConfigManager.Dbg($"ShooterRoutine started for {steamId}, targetGoal={targetGoalPos}");
        
        int shotCount = 0;
        int goalsScored = 0;
        float endTime = Time.time + cfg.SavePracDurationSeconds;
        bool isShootingAtRedGoal = targetGoalPos.z < 0;
        
        if (!SpawnedPucks.ContainsKey(steamId))
            SpawnedPucks[steamId] = new List<Puck>();

        while (Time.time < endTime)
        {
            // Check if player is still connected and in position
            Player player = PracticeHelpers.FindPlayerBySteamId(steamId);
            if (player == null || player.PlayerBody == null)
            {
                ConfigManager.Dbg($"ShooterRoutine ended - player {steamId} disconnected");
                break;
            }
            
            // Check if player is still a goalie
            if (PracticeHelpers.GetPlayerRole(player) != PlayerRole.Goalie)
            {
                ConfigManager.Dbg($"ShooterRoutine ended - player {steamId} is no longer goalie");
                PracticeHelpers.SendMessageToPlayer(steamId, "<size=70%><color=#FF6666>Save practice stopped - you're no longer a goalie!</color></size>");
                break;
            }
            
            shotCount++;
            
            bool isFastShot = UnityEngine.Random.value < PracticeConstants.FastShotChance;
            float distance = UnityEngine.Random.Range(8f, 28f);       // wide depth range
            float lateralOffset = UnityEngine.Random.Range(-15f, 15f); // all angles
            
            // Spawn puck on the ice first (not in air)
            Vector3 spawnPos = targetGoalPos + new Vector3(
                lateralOffset, 0.05f,
                targetGoalPos.z > 0 ? -distance : distance);

            // 10% grounded (zones 15-17), 90% bar-down/corners/post-in/5-hole/mid (zones 0-14)
            int targetZone = UnityEngine.Random.value < 0.10f
                ? UnityEngine.Random.Range(15, 18)
                : UnityEngine.Random.Range(0, 15);
            Vector3 targetOffset = GetTargetOffset(targetZone);
            Vector3 aimPoint = targetGoalPos + targetOffset;

            // Spawn puck stationary first - let it rest so goalie can see it
            var spawnedPuck = PracticeHelpers.SpawnPuckWithCleanup(
                spawnPos, Quaternion.identity, Vector3.zero, false);
            
            if (spawnedPuck != null && spawnedPuck.Rigidbody != null)
            {
                // Freeze puck briefly so goalie can react
                spawnedPuck.Rigidbody.linearVelocity = Vector3.zero;
                spawnedPuck.Rigidbody.isKinematic = true;
            }
            
            // Wait for rest time before shooting
            yield return new WaitForSeconds(PracticeConstants.PuckRestTimeBeforeShot);
            
            // Now shoot the puck
            if (spawnedPuck != null && spawnedPuck.Rigidbody != null)
            {
                spawnedPuck.Rigidbody.isKinematic = false;
                Vector3 velocity = CalculateShotVelocity(spawnPos, aimPoint, isFastShot);
                spawnedPuck.Rigidbody.linearVelocity = velocity;
            }
            
            if (spawnedPuck != null)
            {
                if (SpawnedPucks.ContainsKey(steamId))
                    SpawnedPucks[steamId].Add(spawnedPuck);
                
                // Register puck with player so it behaves like their last touched puck immediately
                YoyoManager.RegisterPuckForPlayer(spawnedPuck, player);
            }
            else
            {
                // Puck was destroyed during rest, skip this shot
                continue;
            }

            float waitTime = UnityEngine.Random.Range(PracticeConstants.MinTimeBetweenShots, PracticeConstants.MaxTimeBetweenShots);
            
            // Check and cleanup pucks more frequently while waiting
            float waitedTime = 0f;
            while (waitedTime < waitTime && Time.time < endTime)
            {
                yield return new WaitForSeconds(0.5f);
                waitedTime += 0.5f;
                goalsScored += CheckAndCleanupPucks(steamId, isShootingAtRedGoal, targetGoalPos);
            }
        }
        
        int saves = shotCount - goalsScored;
        float savePercentage = shotCount > 0 ? (saves * 100f / shotCount) : 0f;
        
        CleanupSessionPucks(steamId);
        ActiveShooterSessions.Remove(steamId);
        
        SendSessionResults(steamId, shotCount, goalsScored, saves, savePercentage);
    }
    
    private static Player FindPlayerBySteamId(ulong steamId)
    {
        return PracticeHelpers.FindPlayerBySteamId(steamId);
    }
    
    private void SendMessageToPlayer(ulong steamId, string message)
    {
        PracticeHelpers.SendMessageToPlayer(steamId, message);
    }

    private Vector3 GetTargetOffset(int targetZone)
    {
        switch (targetZone)
        {
            // ── BAR DOWN (crossbar / just below) ──────────────────────────
            case 0:  return new Vector3(UnityEngine.Random.Range(-0.88f, -0.65f), UnityEngine.Random.Range(1.10f, 1.22f), 0f); // bar down glove
            case 1:  return new Vector3(UnityEngine.Random.Range( 0.65f,  0.88f), UnityEngine.Random.Range(1.10f, 1.22f), 0f); // bar down blocker
            case 2:  return new Vector3(UnityEngine.Random.Range(-0.20f,  0.20f), UnityEngine.Random.Range(1.05f, 1.20f), 0f); // bar down center
            case 3:  return new Vector3(UnityEngine.Random.Range(-0.95f, -0.72f), UnityEngine.Random.Range(1.08f, 1.22f), 0f); // bar down glove post
            case 4:  return new Vector3(UnityEngine.Random.Range( 0.72f,  0.95f), UnityEngine.Random.Range(1.08f, 1.22f), 0f); // bar down blocker post
            // ── TOP CORNERS ───────────────────────────────────────────────
            case 5:  return new Vector3(UnityEngine.Random.Range(-1.05f, -0.80f), UnityEngine.Random.Range(0.88f, 1.10f), 0f); // top glove corner
            case 6:  return new Vector3(UnityEngine.Random.Range( 0.80f,  1.05f), UnityEngine.Random.Range(0.88f, 1.10f), 0f); // top blocker corner
            case 7:  return new Vector3(UnityEngine.Random.Range(-0.88f, -0.65f), UnityEngine.Random.Range(0.80f, 1.05f), 0f); // high glove
            // ── POST-IN MID HEIGHT ────────────────────────────────────────
            case 8:  return new Vector3(UnityEngine.Random.Range(-1.08f, -0.75f), UnityEngine.Random.Range(0.48f, 0.85f), 0f); // post-in glove
            case 9:  return new Vector3(UnityEngine.Random.Range( 0.75f,  1.08f), UnityEngine.Random.Range(0.48f, 0.85f), 0f); // post-in blocker
            // ── 5-HOLE ────────────────────────────────────────────────────
            case 10: return new Vector3(UnityEngine.Random.Range(-0.28f,  0.28f), UnityEngine.Random.Range(0.10f, 0.42f), 0f); // 5-hole
            case 11: return new Vector3(UnityEngine.Random.Range(-0.16f,  0.16f), UnityEngine.Random.Range(0.12f, 0.35f), 0f); // 5-hole tight
            // ── MID NET ───────────────────────────────────────────────────
            case 12: return new Vector3(UnityEngine.Random.Range(-0.72f, -0.40f), UnityEngine.Random.Range(0.52f, 0.90f), 0f); // mid glove
            case 13: return new Vector3(UnityEngine.Random.Range( 0.40f,  0.72f), UnityEngine.Random.Range(0.52f, 0.90f), 0f); // mid blocker
            case 14: return new Vector3(UnityEngine.Random.Range(-0.25f,  0.25f), UnityEngine.Random.Range(0.50f, 0.82f), 0f); // mid center
            // ── LOW / GROUNDED (rare — zones 15-17 only ~10% of shots) ──
            case 15: return new Vector3(UnityEngine.Random.Range(-1.00f, -0.65f), UnityEngine.Random.Range(0.05f, 0.20f), 0f); // low glove
            case 16: return new Vector3(UnityEngine.Random.Range( 0.65f,  1.00f), UnityEngine.Random.Range(0.05f, 0.20f), 0f); // low blocker
            case 17: return new Vector3(UnityEngine.Random.Range(-0.28f,  0.28f), UnityEngine.Random.Range(0.05f, 0.15f), 0f); // low center
            default: return new Vector3(UnityEngine.Random.Range(-0.20f,  0.20f), UnityEngine.Random.Range(1.05f, 1.20f), 0f); // fallback bar down
        }
    }

    private Vector3 CalculateShotVelocity(Vector3 spawnPos, Vector3 aimPoint, bool isFastShot)
    {
        float speedMph = isFastShot
            ? UnityEngine.Random.Range(PracticeConstants.FastShotMinSpeedMph, PracticeConstants.FastShotMaxSpeedMph)
            : UnityEngine.Random.Range(PracticeConstants.SlowShotMinSpeedMph, PracticeConstants.SlowShotMaxSpeedMph);
        float horizontalSpeed = PracticeHelpers.MphToMps(speedMph);

        Vector3 horizontal = aimPoint - spawnPos;
        horizontal.y = 0f;
        float horizontalDist = horizontal.magnitude;
        if (horizontalDist < 0.01f) horizontalDist = 0.01f;

        // Physics-based vertical: solve for vy so puck reaches aimPoint.y at time T
        // T = horizontalDist / horizontalSpeed
        // vy = (dy + 0.5 * g * T^2) / T
        float T = horizontalDist / horizontalSpeed;
        float dy = aimPoint.y - spawnPos.y;
        float g = Mathf.Abs(Physics.gravity.y);  // ~9.81
        float vy = (dy + 0.5f * g * T * T) / T;

        // Clamp vy — very low targets (5-hole) should stay flat
        if (aimPoint.y < 0.3f)
            vy = Mathf.Clamp(vy, 0f, 4f);

        return horizontal.normalized * horizontalSpeed + Vector3.up * vy;
    }

    private int CheckAndCleanupPucks(ulong steamId, bool isShootingAtRedGoal, Vector3 targetGoalPos)
    {
        int goals = 0;
        if (!SpawnedPucks.ContainsKey(steamId)) return goals;

        var puckList = SpawnedPucks[steamId];
        for (int i = puckList.Count - 1; i >= 0; i--)
        {
            var p = puckList[i];
            if (p == null || p.gameObject == null)
            {
                goals++;
                puckList.RemoveAt(i);
                continue;
            }
            
            try
            {
                Vector3 puckPos = p.transform.position;
                bool puckPastGoalLine = isShootingAtRedGoal 
                    ? (puckPos.z < targetGoalPos.z - 2f)
                    : (puckPos.z > targetGoalPos.z + 2f);
                
                if (puckPastGoalLine)
                {
                    goals++;
                    UnityEngine.Object.Destroy(p.gameObject);
                    puckList.RemoveAt(i);
                    continue;
                }
                
                // Delete settled pucks (very low velocity)
                if (p.Rigidbody != null && p.Rigidbody.linearVelocity.magnitude < PracticeConstants.SettledPuckVelocity)
                {
                    UnityEngine.Object.Destroy(p.gameObject);
                    puckList.RemoveAt(i);
                    continue;
                }
            }
            catch { puckList.RemoveAt(i); }
        }
        
        while (puckList.Count > 6)
        {
            if (puckList[0] != null)
                UnityEngine.Object.Destroy(puckList[0].gameObject);
            puckList.RemoveAt(0);
        }
        
        return goals;
    }

    // Tip practice - pucks fly through the crease area for the player to redirect
    public IEnumerator TipPracRoutine(ulong steamId, Vector3 targetGoalPos)
    {
        ConfigManager.Dbg($"TipPracRoutine started for {steamId}, targetGoal={targetGoalPos}");
        
        int shotCount = 0;
        int tipsScored = 0;
        float endTime = Time.time + ConfigManager.Config.SavePracDurationSeconds;
        bool isRedGoal = targetGoalPos.z < 0;
        float goalZ = targetGoalPos.z;
        const float rinkXMin = -26.0f;
        const float rinkXMax = 26.0f;
        const float rinkZMin = -42.0f;
        const float rinkZMax = 42.0f;
        
        if (!SpawnedPucks.ContainsKey(steamId))
            SpawnedPucks[steamId] = new List<Puck>();

        while (Time.time < endTime)
        {
            Player player = PracticeHelpers.FindPlayerBySteamId(steamId);
            if (player == null || player.PlayerBody == null)
                break;

            shotCount++;

            // Use the tipper's current position as the pass-through focus.
            // All spawns are from positions visible to the tipper (in front of the net, never behind it).
            Vector3 tipperPos = player.PlayerBody.transform.position;

            // 10% grounded, 90% mid-to-high for tip practice
            float tipH = UnityEngine.Random.value < 0.10f
                ? UnityEngine.Random.Range(0.05f, 0.22f)    // rare low ball
                : UnityEngine.Random.Range(0.40f, 1.55f);   // mostly shoulder/waist/high

            // tip target = a point near the tipper at the chosen height
            Vector3 passTarget = new Vector3(
                tipperPos.x + UnityEngine.Random.Range(-0.8f, 0.8f),
                tipH,
                tipperPos.z + (isRedGoal ? UnityEngine.Random.Range(-0.8f, 0.8f) : UnityEngine.Random.Range(-0.8f, 0.8f)));

            // --- Shooter position: always in front of the tipper (tipper can see the puck) ---
            // isRedGoal: goal is at negative Z, so "in front" = positive Z of the tipper (farther from goal)
            int shotType = UnityEngine.Random.Range(0, 6);
            Vector3 spawnPos;
            float inFront = isRedGoal ? 1f : -1f; // direction away from goal

            switch (shotType)
            {
                case 0: // Slot / point shot — directly behind tipper, center
                {
                    float back = UnityEngine.Random.Range(12f, 22f);
                    spawnPos = new Vector3(
                        UnityEngine.Random.Range(-4f, 4f), 0.05f,
                        tipperPos.z + inFront * back);
                    break;
                }
                case 1: // Left half-boards — comes from left side across the slot
                {
                    float side = UnityEngine.Random.Range(8f, 16f);
                    float back = UnityEngine.Random.Range(4f, 14f);
                    spawnPos = new Vector3(-side, 0.05f, tipperPos.z + inFront * back);
                    // aim slightly past the tipper toward the near post
                    passTarget.x = UnityEngine.Random.Range(-0.9f, 0.2f);
                    break;
                }
                case 2: // Right half-boards
                {
                    float side = UnityEngine.Random.Range(8f, 16f);
                    float back = UnityEngine.Random.Range(4f, 14f);
                    spawnPos = new Vector3(side, 0.05f, tipperPos.z + inFront * back);
                    passTarget.x = UnityEngine.Random.Range(-0.2f, 0.9f);
                    break;
                }
                case 3: // Left point — from the blue line area, far left
                {
                    float back = UnityEngine.Random.Range(16f, 26f);
                    spawnPos = new Vector3(
                        -UnityEngine.Random.Range(4f, 10f), 0.05f,
                        tipperPos.z + inFront * back);
                    tipH = Mathf.Max(tipH, UnityEngine.Random.Range(0.6f, 1.5f)); // point shots usually high
                    passTarget.y = tipH;
                    break;
                }
                case 4: // Right point
                {
                    float back = UnityEngine.Random.Range(16f, 26f);
                    spawnPos = new Vector3(
                        UnityEngine.Random.Range(4f, 10f), 0.05f,
                        tipperPos.z + inFront * back);
                    tipH = Mathf.Max(tipH, UnityEngine.Random.Range(0.6f, 1.5f));
                    passTarget.y = tipH;
                    break;
                }
                default: // Tight angle — just off the side boards, close in
                {
                    float side = UnityEngine.Random.value > 0.5f ? 1f : -1f;
                    spawnPos = new Vector3(
                        side * UnityEngine.Random.Range(12f, 20f), 0.05f,
                        tipperPos.z + inFront * UnityEngine.Random.Range(2f, 8f));
                    passTarget.x = UnityEngine.Random.Range(-side * 1.0f, side * 0.2f);
                    break;
                }
            }

            // Keep tip-practice spawns inside rink bounds.
            spawnPos.x = Mathf.Clamp(spawnPos.x, rinkXMin, rinkXMax);
            spawnPos.z = Mathf.Clamp(spawnPos.z, rinkZMin, rinkZMax);

            // Spawn puck stationary first so tipper can see it coming
            var spawnedPuck = PracticeHelpers.SpawnPuckWithCleanup(spawnPos, Quaternion.identity, Vector3.zero, false);
            
            if (spawnedPuck != null && spawnedPuck.Rigidbody != null)
            {
                spawnedPuck.Rigidbody.linearVelocity = Vector3.zero;
                spawnedPuck.Rigidbody.isKinematic = true;
            }
            
            yield return new WaitForSeconds(PracticeConstants.TipPuckRestTime);
            
            // Shoot the puck through the tipper's position using physics-accurate arc
            if (spawnedPuck != null && spawnedPuck.Rigidbody != null)
            {
                spawnedPuck.Rigidbody.isKinematic = false;

                float speedMph = UnityEngine.Random.Range(PracticeConstants.TipShotMinSpeedMph, PracticeConstants.TipShotMaxSpeedMph);
                float horizontalSpeed = PracticeHelpers.MphToMps(speedMph);

                Vector3 horizontal = passTarget - spawnPos;
                horizontal.y = 0f;
                float hDist = Mathf.Max(horizontal.magnitude, 0.01f);
                float T = hDist / horizontalSpeed;
                float dyTarget = passTarget.y - spawnPos.y;
                float g = Mathf.Abs(Physics.gravity.y);
                float vy = (dyTarget + 0.5f * g * T * T) / T;
                if (passTarget.y < 0.25f) vy = Mathf.Clamp(vy, 0f, 3.5f); // keep grounders flat

                Vector3 velocity = horizontal.normalized * horizontalSpeed + Vector3.up * vy;
                spawnedPuck.Rigidbody.linearVelocity = velocity;
            }
            
            if (spawnedPuck != null)
            {
                if (SpawnedPucks.ContainsKey(steamId))
                    SpawnedPucks[steamId].Add(spawnedPuck);
                
                YoyoManager.RegisterPuckForPlayer(spawnedPuck, player);
            }
            else
            {
                continue;
            }

            float waitTime = UnityEngine.Random.Range(PracticeConstants.TipMinTimeBetweenShots, PracticeConstants.TipMaxTimeBetweenShots);
            
            float waitedTime = 0f;
            while (waitedTime < waitTime && Time.time < endTime)
            {
                yield return new WaitForSeconds(0.5f);
                waitedTime += 0.5f;
                tipsScored += CheckAndCleanupPucks(steamId, isRedGoal, targetGoalPos);
            }
        }
        
        int misses = shotCount - tipsScored;
        float tipPercentage = shotCount > 0 ? (tipsScored * 100f / shotCount) : 0f;
        
        CleanupSessionPucks(steamId);
        ActiveTipPracSessions.Remove(steamId);
        
        PracticeHelpers.SendMessageToPlayer(steamId,
            $"<size=70%><color=#FFFFFF>Tip practice ended! Shots: {shotCount}, Tips: {tipsScored}, Misses: {misses}, Tip%: {tipPercentage:F1}%</color></size>");
    }

    private void CleanupSessionPucks(ulong steamId)
    {
        if (SpawnedPucks.ContainsKey(steamId))
        {
            foreach (var p in SpawnedPucks[steamId])
            {
                if (p != null && p.gameObject != null)
                    try { UnityEngine.Object.Destroy(p.gameObject); } catch { }
            }
            SpawnedPucks.Remove(steamId);
        }
    }

    private void SendSessionResults(ulong steamId, int shotCount, int goalsScored, int saves, float savePercentage)
    {
        PracticeHelpers.SendMessageToPlayer(steamId,
            $"<size=70%><color=#FFFFFF>Save practice ended! Shots: {shotCount}, Goals: {goalsScored}, Saves: {saves}, Save%: {savePercentage:F1}%</color></size>");
    }
}