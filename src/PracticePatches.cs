// PracticePatches.cs - Harmony patches for practice features

using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using MaxPractice;

/// <summary>
/// Detects if a CompTweaks-style mod is loaded.
/// This includes the original CompetitivePuckTweaks mod and the newer CompetitiveAdjustments package.
/// </summary>
public static class CompTweaksDetector
{
    private static bool? _isCompTweaksLoaded = null;
    
    /// <summary>
    /// Check if a CompTweaks-style mod is loaded. Result is cached after first check.
    /// </summary>
    public static bool IsCompTweaksLoaded
    {
        get
        {
            if (_isCompTweaksLoaded.HasValue)
                return _isCompTweaksLoaded.Value;
            
            _isCompTweaksLoaded = DetectCompTweaks();
            return _isCompTweaksLoaded.Value;
        }
    }
    
    private static bool DetectCompTweaks()
    {
        try
        {
            // Check for known assemblies from the old and new comp tweak packs.
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.GetName().Name;
                if (name.Contains("CompetitivePuckTweaks") ||
                    name.Contains("CompetitiveAdjustments") ||
                    name.Contains("CPT") ||
                    name.Contains("COMPADJUST"))
                    return true;
            }
            
            // Also check for types from the old standalone mod and the new combined mod.
            var knownTypeNames = new[]
            {
                "CompetitivePuckTweaks.src.PluginCore, CompetitivePuckTweaks",
                "CompetitivePuckTweaks.src.PluginCore, CompetitiveAdjustments",
                "CompetitiveAdjustmentsGameMod, CompetitiveAdjustments",
                "CompetitiveCompanion.PluginCore, CompetitiveAdjustments"
            };

            foreach (var typeName in knownTypeNames)
            {
                if (Type.GetType(typeName) != null)
                    return true;
            }
        }
        catch { }
        
        return false;
    }
    
    /// <summary>
    /// Force re-check for CompTweaks (useful if mods load after our check)
    /// </summary>
    public static void Reset()
    {
        _isCompTweaksLoaded = null;
    }
}

/// <summary>
/// PUBLIC API: Helper to detect if a player is a fake AI player from MaxPractice.
/// Other mods (like CurvedStick) can use this to skip their patches for our fake players.
/// </summary>
public static class FakePlayerDetector
{
    // Fake player client IDs used by MaxPractice for AI GOALIES only
    public const ulong FAKE_RED_CLIENT_ID = 1111112;
    public const ulong FAKE_BLUE_CLIENT_ID = 1111111;
    
    // Client ID range for traffic dummies (3333333 to 3333499)
    public const ulong TRAFFIC_CLIENT_ID_START = 3333333;
    public const ulong TRAFFIC_CLIENT_ID_END = 3333500;
    
    // Client ID range for passer AIs (4444444 to 4444599)
    public const ulong PASSER_CLIENT_ID_START = 4444444;
    public const ulong PASSER_CLIENT_ID_END = 4444600;
    
    /// <summary>
    /// PUBLIC API: Check if a player is ANY type of MaxPractice fake player.
    /// Use this to skip patches/modifications for AI players.
    /// Returns true for: AI goalies (DummyRed/DummyBlue), Traffic dummies, Passer AIs
    /// </summary>
    public static bool IsMaxPracticeFakePlayer(Player player)
    {
        return IsAnyFakePlayer(player);
    }
    
    /// <summary>
    /// PUBLIC API: Check if a player body belongs to a MaxPractice fake player.
    /// </summary>
    public static bool IsMaxPracticeFakePlayerBody(object body)
    {
        return IsAnyFakePlayerBody(body);
    }
    
    /// <summary>
    /// PUBLIC API: Check if a stick belongs to a MaxPractice fake player.
    /// </summary>
    public static bool IsMaxPracticeFakePlayerStick(Stick stick)
    {
        if (stick == null) return false;
        try { return IsAnyFakePlayer(stick.Player); }
        catch { return false; }
    }
    
    public static bool IsFakePlayer(Player player)
    {
        if (player == null) return false;
        
        // Check by client ID first (fastest) - only AI goalie IDs
        try
        {
            ulong clientId = player.OwnerClientId;
            if (clientId == FAKE_RED_CLIENT_ID || clientId == FAKE_BLUE_CLIENT_ID)
                return true;
        }
        catch { }
        
        // Check by username containing "Dummy" (AI goalie names are DummyRed/DummyBlue)
        // Traffic dummies have names like "Traffic1", "Traffic2" etc - we DON'T want those
        try
        {
            if (player.Username != null)
            {
                string username = player.Username.Value.ToString();
                if (username != null && username.Contains("Dummy"))
                    return true;
            }
        }
        catch { }
        
        return false;
    }
    
    public static bool IsFakePlayerBody(object body)
    {
        if (body == null) return false;
        try
        {
            // Get Player property from body (works for both PlayerBodyV2 and PlayerBody)
            var playerProp = body.GetType().GetProperty("Player");
            if (playerProp == null) return false;
            Player player = (Player)playerProp.GetValue(body);
            return IsFakePlayer(player);
        }
        catch { return false; }
    }
    
    public static bool IsFakePlayerMovement(Movement movement)
    {
        if (movement == null) return false;
        try
        {
            return IsFakePlayerBody(movement.PlayerBody);
        }
        catch { return false; }
    }
    
    public static bool IsFakePlayerStickPositioner(StickPositioner positioner)
    {
        if (positioner == null) return false;
        try
        {
            return IsFakePlayer(positioner.Player);
        }
        catch { return false; }
    }
    
    /// <summary>
    /// Check if a player is a traffic dummy or passer (not AI goalie)
    /// </summary>
    public static bool IsTrafficDummy(Player player)
    {
        if (player == null) return false;
        try
        {
            // Traffic dummies use client IDs starting at 3333333
            // Passers use client IDs starting at 4444444
            ulong clientId = player.OwnerClientId;
            if (clientId >= TRAFFIC_CLIENT_ID_START && clientId < TRAFFIC_CLIENT_ID_END)
                return true;
            if (clientId >= PASSER_CLIENT_ID_START && clientId < PASSER_CLIENT_ID_END)
                return true;
                
            // Or check username
            if (player.Username != null)
            {
                string username = player.Username.Value.ToString();
                if (username != null && (username.Contains("Traffic") || username.Contains("Passer")))
                    return true;
            }
        }
        catch { }
        return false;
    }
    
    public static bool IsTrafficDummyBody(object body)
    {
        if (body == null) return false;
        try
        {
            // Get Player property from body (works for both PlayerBodyV2 and PlayerBody)
            var playerProp = body.GetType().GetProperty("Player");
            if (playerProp == null) return false;
            Player player = (Player)playerProp.GetValue(body);
            return IsTrafficDummy(player);
        }
        catch { return false; }
    }
    
    /// <summary>
    /// Check if a player is ANY fake player (goalie or traffic)
    /// </summary>
    public static bool IsAnyFakePlayer(Player player)
    {
        return IsFakePlayer(player) || IsTrafficDummy(player);
    }
    
    public static bool IsAnyFakePlayerBody(object body)
    {
        if (body == null) return false;
        try
        {
            // Get Player property from body (works for both PlayerBodyV2 and PlayerBody)
            var playerProp = body.GetType().GetProperty("Player");
            if (playerProp == null) return false;
            Player player = (Player)playerProp.GetValue(body);
            return IsAnyFakePlayer(player);
        }
        catch { return false; }
    }
}

// ============================================================================
// SKIP COMPTWEAKS PATCHES FOR FAKE PLAYERS (AI Goalies AND Traffic Dummies)
// These use FINALIZERS to catch exceptions from CompTweaks postfixes
// since returning false from prefix would skip the original method we need.
// 
// The physics adjustments (drag) only apply to AI GOALIES, not traffic.
// ============================================================================

/*  DISABLED for B310: These patch classes reference PlayerBodyV2 which doesn't exist.
    They are kept for B202 compatibility but will not compile on B310.
    Dynamic patching in MaxPracticePlugin.cs handles B310 versions.

/// <summary>
/// Catch exceptions from CompetitivePuckTweaks' PlayerBodyPatch (OnNetworkPostSpawn) for fake players.
/// CompTweaks accesses Player.IsReplay.Value which throws null for fake players.
/// We use a finalizer to let the original run but catch CompTweaks exceptions.
/// NOTE: PlayerBodyV2 doesn't exist in B310 (renamed to PlayerBody).
/// This patch is applied dynamically in MaxPracticePlugin.cs OnEnable() instead of via compile-time attribute.
/// </summary>
// [HarmonyPatch(typeof(PlayerBodyV2), "OnNetworkPostSpawn")]  // DISABLED: Dynamic patching in MaxPracticePlugin
public static class SkipCompTweaks_PlayerBodySpawnPatch
{
    [HarmonyFinalizer]
    public static Exception Finalizer(object __instance, Exception __exception)
    {
        // If there was an exception and this is ANY fake player (goalie or traffic), suppress it
        if (__exception != null)
        {
            try
            {
                if (FakePlayerDetector.IsAnyFakePlayerBody(__instance))
                {
                    // Apply physics settings only for AI goalie, not traffic
                    if (FakePlayerDetector.IsFakePlayerBody(__instance))
                    {
                        ApplyAIGoaliePhysics(__instance);
                    }
                    return null; // Suppress the exception for all fake players
                }
            }
            catch { }
        }
        return __exception;
    }
    
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    public static void Postfix(object __instance)
    {
        // Apply physics as a postfix only for AI goalie (not traffic)
        try
        {
            if (FakePlayerDetector.IsFakePlayerBody(__instance))
            {
                ApplyAIGoaliePhysics(__instance);
            }
        }
        catch { }
    }
    
    private static void ApplyAIGoaliePhysics(object body)
    {
        // Only apply physics changes if CompetitivePuckTweaks is loaded
        if (!CompTweaksDetector.IsCompTweaksLoaded) return;
        
        try
        {
            var rb = body.GetComponent<Rigidbody>();
            if (rb != null)
            {
                // High damping to counteract CompetitivePuckTweaks' zero drag settings
                rb.linearDamping = 4.0f;
                rb.angularDamping = 1.0f;
            }
        }
        catch { }
    }
}
*/

/// <summary>
/// Skip CompetitivePuckTweaks' MovementPatch (Start) for fake players.
/// CompTweaks accesses Player.IsReplay.Value which throws null for fake players.
/// </summary>
[HarmonyPatch(typeof(Movement), "Start")]
[HarmonyPriority(Priority.First)]
public static class SkipCompTweaks_MovementStartPatch
{
    public static bool Prefix(Movement __instance)
    {
        try
        {
            if (FakePlayerDetector.IsFakePlayerMovement(__instance))
            {
                // Apply our own movement settings for the AI goalie
                ApplyAIGoalieMovement(__instance);
                return false; // Skip original AND CompetitivePuckTweaks patch
            }
        }
        catch { }
        return true;
    }
    
    private static void ApplyAIGoalieMovement(Movement movement)
    {
        try
        {
            // Set reasonable goalie movement values (similar to what CompTweaks would set)
            // These are accessed via reflection since they're private
            var type = typeof(Movement);
            
            // Use reasonable defaults for goalie movement
            SetPrivateField(type, movement, "maxBackwardsSpeed", 4.5f);
            SetPrivateField(type, movement, "maxBackwardsSprintSpeed", 5.5f);
            SetPrivateField(type, movement, "maxForwardsSpeed", 4.5f);
            SetPrivateField(type, movement, "maxForwardsSprintSpeed", 5.5f);
            SetPrivateField(type, movement, "turnMaxSpeed", 180f);
            SetPrivateField(type, movement, "turnAcceleration", 720f);
            SetPrivateField(type, movement, "turnBrakeAcceleration", 720f);
            SetPrivateField(type, movement, "turnDrag", 10f);
        }
        catch { }
    }
    
    private static void SetPrivateField(Type type, object obj, string fieldName, float value)
    {
        try
        {
            var field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                field.SetValue(obj, value);
            }
        }
        catch { }
    }
}

/// <summary>
/// Catch exceptions from CompetitivePuckTweaks' ScalingTurnSpeedPatch for fake players.
/// We use a Finalizer to catch the exception but let the original method run.
/// </summary>
[HarmonyPatch(typeof(Movement), "FixedUpdate")]
public static class SkipCompTweaks_MovementFixedUpdatePatch
{
    [HarmonyFinalizer]
    public static Exception Finalizer(Movement __instance, Exception __exception)
    {
        // If there was an exception and this is ANY fake player (goalie or traffic), suppress it
        if (__exception != null)
        {
            try
            {
                if (__instance?.PlayerBody != null && FakePlayerDetector.IsAnyFakePlayerBody(__instance.PlayerBody))
                {
                    return null; // Suppress the exception for fake players
                }
            }
            catch { }
        }
        return __exception; // Let other exceptions through
    }
}

/*  DISABLED for B310: Patch classes that reference PlayerBodyV2
/// <summary>
/// Catch exceptions from CompetitivePuckTweaks' PlayerBodyFixedUpdatePatch for fake players.
/// CompTweaks accesses IsUpright and IsSideways which may throw for fake players.
/// NOTE: PlayerBodyV2 doesn't exist in B310. Dynamic patching in MaxPracticePlugin.cs handles this.
/// </summary>
// [HarmonyPatch(typeof(PlayerBodyV2), "FixedUpdate")]  // DISABLED: Dynamic patching in MaxPracticePlugin
public static class SkipCompTweaks_PlayerBodyFixedUpdatePatch
{
    [HarmonyFinalizer]
    public static Exception Finalizer(object __instance, Exception __exception)
    {
        // If there was an exception and this is ANY fake player (goalie or traffic), suppress it
        if (__exception != null)
        {
            try
            {
                if (FakePlayerDetector.IsAnyFakePlayerBody(__instance))
                {
                    return null; // Suppress the exception for fake players
                }
            }
            catch { }
        }
        return __exception; // Let other exceptions through
    }
    
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    public static void Postfix(object __instance)
    {
        // Reset physics for AI GOALIE only (not traffic) after everything runs
        try
        {
            if (!NetworkManager.Singleton.IsServer) return;
            if (__instance == null) return;
            
            // Only apply drag to AI goalie, NOT traffic dummies
            if (FakePlayerDetector.IsFakePlayerBody(__instance))
            {
                var rb = __instance.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    // High damping to counteract CompetitivePuckTweaks' zero drag - reset every frame
                    rb.linearDamping = 4.0f;
                    rb.angularDamping = 1.0f;
                }
            }
        }
        catch { }
    }
}

/// <summary>
/// Catch exceptions from CompetitivePuckTweaks' PlayerBodyDashLeftPatch for fake players.
/// NOTE: PlayerBodyV2 doesn't exist in B310. Dynamic patching in MaxPracticePlugin.cs handles this.
/// </summary>
// [HarmonyPatch(typeof(PlayerBodyV2), "DashLeft")]  // DISABLED: Dynamic patching in MaxPracticePlugin
public static class SkipCompTweaks_DashLeftPatch
{
    [HarmonyFinalizer]
    public static Exception Finalizer(object __instance, Exception __exception)
    {
        if (__exception != null)
        {
            try
            {
                if (FakePlayerDetector.IsAnyFakePlayerBody(__instance))
                {
                    return null; // Suppress the exception for fake players
                }
            }
            catch { }
        }
        return __exception;
    }
}

/// <summary>
/// Catch exceptions from CompetitivePuckTweaks' PlayerBodyDashRightPatch for fake players.
/// NOTE: PlayerBodyV2 doesn't exist in B310. Dynamic patching in MaxPracticePlugin.cs handles this.
/// </summary>
// [HarmonyPatch(typeof(PlayerBodyV2), "DashRight")]  // DISABLED: Dynamic patching in MaxPracticePlugin
public static class SkipCompTweaks_DashRightPatch
{
    [HarmonyFinalizer]
    public static Exception Finalizer(object __instance, Exception __exception)
    {
        if (__exception != null)
        {
            try
            {
                if (FakePlayerDetector.IsAnyFakePlayerBody(__instance))
                {
                    return null; // Suppress the exception for fake players
                }
            }
            catch { }
        }
        return __exception;
    }
}
*/

public static class PracticeStaminaPatch
{
    public static void Postfix(object __instance)
    {
        try
        {
            if (__instance == null) return;
            if (!NetworkManager.Singleton.IsServer) return;

            var body = __instance as PlayerBody;
            if (body == null) return;
            var player = body.Player;
            if (player == null) return;

            ulong steamId = PracticeHelpers.GetSteamIdFromPlayer(player);
            if (MaxPracticePlugin.InfiniteStaminaPlayers.Contains(steamId))
            {
                body.Stamina.Value = 1f;
            }
        }
        catch (Exception) { }
    }
}

// Patch to detect when a puck ENTERS contact with a stick - for initial yoyo tracking
[HarmonyPatch(typeof(Puck), "OnCollisionEnter")]
public static class PuckCollisionEnterPatch
{
    public static void Postfix(Puck __instance, Collision collision)
    {
        try
        {
            if (__instance == null) return;
            if (!NetworkManager.Singleton.IsServer) return;
            
            // Check if collision was with a stick
            Stick stick = collision.gameObject.GetComponent<Stick>();
            if (stick == null) return;
            
            // Get the player who owns this stick
            var player = stick.Player;
            if (player == null) return;
            
            // Get the player's steam ID
            ulong steamId = PracticeHelpers.GetSteamIdFromPlayer(player);
            ulong ownerId = player.NetworkObject != null ? player.NetworkObject.OwnerClientId : 0;
            if (steamId == 0)
                steamId = ownerId;
            
            // Always track last touched puck for /pop command
            YoyoManager.TrackLastTouchedPuckForPlayer(__instance, player);
            
            // Check if this player has yoyo mode enabled - if so, track their puck
            if (MaxPracticePlugin.YoyoPlayers.Contains(steamId))
            {
                // Notify the YoyoManager that this player touched this puck
                if (YoyoManager.Instance != null)
                {
                    YoyoManager.Instance.OnPuckFired(__instance, steamId);
                }
            }
            else
            {
                // Another player touched this puck - if they touch it, it becomes THEIR puck
                // and detaches from the previous owner
                if (YoyoManager.Instance != null)
                {
                    YoyoManager.Instance.OnPuckTouchedByOther(__instance, steamId);
                }
            }
        }
        catch (Exception) { }
    }
}

// Patch to detect when a puck leaves a stick (shot) for yoyo mode
[HarmonyPatch(typeof(Puck), "OnCollisionExit")]
public static class PuckCollisionExitPatch
{
    public static void Postfix(Puck __instance, Collision collision)
    {
        try
        {
            if (__instance == null) return;
            if (!NetworkManager.Singleton.IsServer) return;
            
            // Check if collision was with a stick
            Stick stick = collision.gameObject.GetComponent<Stick>();
            if (stick == null) return;
            
            // Get the player who owns this stick
            var player = stick.Player;
            if (player == null) return;
            
            // Get the player's steam ID
            ulong steamId = PracticeHelpers.GetSteamIdFromPlayer(player);
            if (steamId == 0 && player.NetworkObject != null)
                steamId = player.NetworkObject.OwnerClientId;
            
            // Check if this player has yoyo mode enabled - if so, ensure their puck is tracked
            if (MaxPracticePlugin.YoyoPlayers.Contains(steamId))
            {
                // Notify the YoyoManager that this player fired this puck
                if (YoyoManager.Instance != null)
                {
                    YoyoManager.Instance.OnPuckFired(__instance, steamId);
                }
            }
            // Note: Don't detach on exit - only on enter by another player
        }
        catch (Exception) { }
    }
}

// ============================================================================
// CURVEDSTICK MOD COMPATIBILITY - Ensure stick collision for fake players
// CurvedStick may disable colliders or modify physics during stick initialization.
// These patches run AFTER CurvedStick to restore proper collision for our AI players.
// ============================================================================

/// <summary>
/// Ensure fake player sticks have proper collision enabled after all other mods process them.
/// Runs at Priority.Last to execute after CurvedStick's modifications.
/// </summary>
[HarmonyPatch(typeof(Stick), "OnNetworkPostSpawn")]
public static class FakePlayerStickCollisionPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    public static void Postfix(Stick __instance)
    {
        try
        {
            if (__instance == null) return;
            if (!NetworkManager.Singleton.IsServer) return;
            
            // Check if this stick belongs to a MaxPractice fake player
            var player = __instance.Player;
            if (player == null)
            {
                ConfigManager.Dbg("[MaxPractice] FakePlayerStickCollisionPatch: player is null");
                return;
            }
            
            bool isFake = FakePlayerDetector.IsAnyFakePlayer(player);
            string username = "unknown";
            try { username = player.Username.Value.ToString(); } catch { }
            
            ConfigManager.Dbg($"[MaxPractice] FakePlayerStickCollisionPatch: Stick spawned for {username}, isFake={isFake}, clientId={player.OwnerClientId}");
            
            if (!isFake)
                return;
            
            // Re-enable all colliders on this stick to fix CurvedStick disabling them
            ConfigManager.Log($"[MaxPractice] Enabling stick collision for fake player: {username}");
            EnableStickCollision(__instance);
        }
        catch (Exception e) { ConfigManager.Dbg($"[MaxPractice] FakePlayerStickCollisionPatch error: {e.Message}"); }
    }
    
    /// <summary>
    /// Re-enables all colliders on a stick and ensures proper physics setup.
    /// Specifically targets the blade and shaft colliders in StickMesh.
    /// Also handles CurvedStick mod which may replace the blade mesh.
    /// </summary>
    public static void EnableStickCollision(Stick stick)
    {
        if (stick == null) return;
        
        try
        {
            int enabledCount = 0;
            
            // Get the StickMesh which contains the blade and shaft colliders
            var stickMesh = stick.StickMesh;
            if (stickMesh != null)
            {
                // Explicitly enable blade collider
                var bladeCollider = stickMesh.BladeCollider;
                if (bladeCollider != null)
                {
                    bladeCollider.enabled = true;
                    bladeCollider.isTrigger = false; // Ensure it's not a trigger (solid collision)
                    enabledCount++;
                    
                    // If it's a MeshCollider with null mesh, restore from another player's stick
                    var meshCol = bladeCollider as MeshCollider;
                    if (meshCol != null && meshCol.sharedMesh == null)
                    {
                        ConfigManager.Dbg("[MaxPractice] Blade MeshCollider has NULL mesh - searching for valid mesh from other sticks");
                        
                        // Find a valid blade mesh from another player's stick
                        Mesh validBladeMesh = FindValidBladeMesh(stick);
                        if (validBladeMesh != null)
                        {
                            meshCol.sharedMesh = validBladeMesh;
                            meshCol.convex = true;
                            ConfigManager.Dbg($"[MaxPractice] Restored blade mesh from another stick: {validBladeMesh.name}");
                        }
                        else
                        {
                            ConfigManager.Dbg("[MaxPractice] Could not find valid blade mesh from any stick!");
                        }
                    }
                    else if (meshCol != null)
                    {
                        meshCol.convex = true;
                        ConfigManager.Dbg($"[MaxPractice] Blade MeshCollider: convex={meshCol.convex}, mesh={meshCol.sharedMesh?.name}");
                    }
                    
                    ConfigManager.Dbg($"[MaxPractice] Blade collider: {bladeCollider.name}, type={bladeCollider.GetType().Name}, enabled={bladeCollider.enabled}");
                }
                else
                {
                    ConfigManager.Dbg("[MaxPractice] BladeCollider is null - CurvedStick may have replaced it");
                }
                
                // Explicitly enable shaft collider
                var shaftCollider = stickMesh.ShaftCollider;
                if (shaftCollider != null)
                {
                    shaftCollider.enabled = true;
                    shaftCollider.isTrigger = false;
                    enabledCount++;
                    
                    var meshCol = shaftCollider as MeshCollider;
                    if (meshCol != null)
                    {
                        meshCol.convex = true;
                    }
                    ConfigManager.Dbg($"[MaxPractice] Enabled shaft collider: {shaftCollider.name}, type={shaftCollider.GetType().Name}");
                }
            }
            else
            {
                ConfigManager.Dbg("[MaxPractice] StickMesh is null!");
            }
            
            // Enable ALL colliders on the stick and its children
            var allColliders = stick.GetComponentsInChildren<Collider>(true);
            foreach (var col in allColliders)
            {
                if (col != null)
                {
                    bool wasDisabled = !col.enabled;
                    col.enabled = true;
                    col.isTrigger = false;
                    
                    var meshCol = col as MeshCollider;
                    if (meshCol != null && meshCol.sharedMesh != null)
                    {
                        meshCol.convex = true;
                    }
                    
                    if (wasDisabled)
                    {
                        enabledCount++;
                        ConfigManager.Dbg($"[MaxPractice] Enabled collider: {col.name}, type={col.GetType().Name}");
                    }
                }
            }
            
            ConfigManager.Dbg($"[MaxPractice] Total colliders on stick: {allColliders.Length}, newly enabled: {enabledCount}");
            
            // Ensure rigidbody is set up properly
            var rb = stick.Rigidbody;
            if (rb != null)
            {
                rb.isKinematic = false;
                rb.detectCollisions = true;
            }
        }
        catch (Exception e) { ConfigManager.Dbg($"[MaxPractice] EnableStickCollision error: {e.Message}"); }
    }
    
    /// <summary>
    /// Cache for the valid blade mesh so we don't search every time
    /// </summary>
    public static Mesh _cachedBladeMesh = null;
    
    /// <summary>
    /// Find a valid blade mesh from another player's stick
    /// </summary>
    private static Mesh FindValidBladeMesh(Stick excludeStick)
    {
        // Return cached mesh if we already found one
        if (_cachedBladeMesh != null)
            return _cachedBladeMesh;
        
        try
        {
            // Search all sticks in the scene
            var allSticks = UnityEngine.Object.FindObjectsOfType<Stick>();
            foreach (var otherStick in allSticks)
            {
                if (otherStick == null || otherStick == excludeStick)
                    continue;
                
                // Skip fake player sticks - they might also have null mesh
                if (otherStick.Player != null && FakePlayerDetector.IsAnyFakePlayer(otherStick.Player))
                    continue;
                
                var otherStickMesh = otherStick.StickMesh;
                if (otherStickMesh == null)
                    continue;
                
                var otherBladeCollider = otherStickMesh.BladeCollider as MeshCollider;
                if (otherBladeCollider != null && otherBladeCollider.sharedMesh != null)
                {
                    _cachedBladeMesh = otherBladeCollider.sharedMesh;
                    ConfigManager.Dbg($"[MaxPractice] Found valid blade mesh from {otherStick.Player?.Username.Value}: {_cachedBladeMesh.name}");
                    return _cachedBladeMesh;
                }
            }
        }
        catch (Exception e)
        {
            ConfigManager.Dbg($"[MaxPractice] Error finding valid blade mesh: {e.Message}");
        }
        
        return null;
    }
}

/// <summary>
/// Periodically re-check stick collision for fake players in case other mods disable it.
/// Only runs once every ~3 seconds (150 frames at 50fps) to minimize performance impact.
/// </summary>
[HarmonyPatch(typeof(Stick), "FixedUpdate")]
public static class FakePlayerStickFixedUpdatePatch
{
    private static int frameCounter = 0;
    private const int CHECK_INTERVAL = 150; // ~3 seconds at 50fps
    
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    public static void Postfix(Stick __instance)
    {
        frameCounter++;
        if (frameCounter % CHECK_INTERVAL != 0) return;
        
        try
        {
            if (__instance == null) return;
            if (!NetworkManager.Singleton.IsServer) return;
            
            var player = __instance.Player;
            if (player == null || !FakePlayerDetector.IsAnyFakePlayer(player))
                return;
            
            // Re-enable collision (silent - no logging in periodic check)
            EnableStickCollisionSilent(__instance);
        }
        catch { }
    }
    
    /// <summary>
    /// Silent version of EnableStickCollision without debug logging for periodic checks
    /// </summary>
    private static void EnableStickCollisionSilent(Stick stick)
    {
        if (stick == null) return;
        
        try
        {
            var stickMesh = stick.StickMesh;
            if (stickMesh != null)
            {
                var bladeCollider = stickMesh.BladeCollider;
                if (bladeCollider != null)
                {
                    bladeCollider.enabled = true;
                    bladeCollider.isTrigger = false;
                    
                    var meshCol = bladeCollider as MeshCollider;
                    if (meshCol != null)
                    {
                        if (meshCol.sharedMesh == null && FakePlayerStickCollisionPatch._cachedBladeMesh != null)
                            meshCol.sharedMesh = FakePlayerStickCollisionPatch._cachedBladeMesh;
                        if (meshCol.sharedMesh != null)
                            meshCol.convex = true;
                    }
                }
                
                var shaftCollider = stickMesh.ShaftCollider;
                if (shaftCollider != null)
                {
                    shaftCollider.enabled = true;
                    shaftCollider.isTrigger = false;
                }
            }
            
            var rb = stick.Rigidbody;
            if (rb != null)
            {
                rb.isKinematic = false;
                rb.detectCollisions = true;
            }        }
        catch { }
    }
}

// ============================================================================
// VOTE SYSTEM PATCHES - Exclude fake players from vote calculations
// The game calculates votes needed using PlayerManager.GetPlayers().Count
// We patch VoteManagerController to exclude our fake players from that count.
// ============================================================================

/// <summary>
/// Patch the Vote calculation to be aware of fake players (AI goalies and traffic dummies).
/// Instead of patching the event handler, we patch VoteManager.Server_CreateVote to correct votesNeeded.
/// </summary>
[HarmonyPatch(typeof(VoteManager), "Server_CreateVote")]
public static class VoteManager_Server_CreateVote_Patch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    public static void Prefix(VoteManager __instance, VoteType voteType, ref int votesNeeded, Player startedBy, object data = null)
    {
        try
        {
            // Only run on server
            if (!NetworkManager.Singleton.IsServer)
                return;
            
            // If no fake players, let it pass through unchanged
            if (MaxPracticePlugin.FakePlayers.Count == 0)
                return;
            
            Debug.Log($"[MaxPractice] Detected vote creation with votesNeeded={votesNeeded}");
            
            // Get real player count by excluding fake players
            var playerManager = MonoBehaviourSingleton<PlayerManager>.Instance;
            if (playerManager == null)
                return;
            
            var allPlayers = playerManager.GetPlayers(false);
            int fakeCount = 0;
            
            // Count AI goalies
            fakeCount += MaxPracticePlugin.FakePlayers.Count;
            
            // Also check for traffic dummies
            foreach (var player in allPlayers)
            {
                if (FakePlayerDetector.IsTrafficDummy(player))
                {
                    fakeCount++;
                    break; // Just need to know if any exist
                }
            }
            
            if (fakeCount == 0)
                return; // No fake players detected
            
            Debug.Log($"[MaxPractice] Found {fakeCount} fake players out of {allPlayers.Count} total");
            
            // Recalculate votesNeeded: subtract EACH fake player from the count
            int realPlayerCount = allPlayers.Count - fakeCount;
            if (realPlayerCount < 1) realPlayerCount = 1;
            
            int correctedVotesNeeded = UnityEngine.Mathf.RoundToInt((float)realPlayerCount / 2f + 0.5f);
            if (correctedVotesNeeded < 1) correctedVotesNeeded = 1;
            
            Debug.Log($"[MaxPractice] Correcting votesNeeded: {votesNeeded} → {correctedVotesNeeded} (real players: {realPlayerCount})");
            votesNeeded = correctedVotesNeeded;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[MaxPractice] VoteManager.Server_CreateVote patch error: {ex.Message}\n{ex.StackTrace}");
        }
    }
}

/// <summary>
/// Patch PlayerManager.GetPlayerSteamIds to exclude fake players from server browser player list.
/// The master server uses this list to show player count - we don't want AI players showing.
/// </summary>
/* B310: PlayerManager.GetPlayerSteamIds no longer exists
[HarmonyPatch(typeof(PlayerManager), "GetPlayerSteamIds")]
public static class PlayerManager_GetPlayerSteamIds_Patch
{
    /// <summary>
    /// Postfix to filter out fake player steam IDs from the returned array.
    /// </summary>
    [HarmonyPostfix]
    public static void Postfix(ref string[] __result)
    {
        try
        {
            // Only run on server
            if (!NetworkManager.Singleton.IsServer) return;
            
            // If no fake players, no filtering needed
            if (MaxPracticePlugin.FakePlayers.Count == 0) return;
            
            // Get the steam IDs of fake players to exclude
            var fakePlayerSteamIds = new System.Collections.Generic.HashSet<string>();
            foreach (var fakePlayer in MaxPracticePlugin.FakePlayers)
            {
                if (fakePlayer != null)
                {
                    try
                    {
                        string steamId = fakePlayer.SteamId.Value.ToString();
                        if (!string.IsNullOrEmpty(steamId))
                        {
                            fakePlayerSteamIds.Add(steamId);
                        }
                    }
                    catch { }
                }
            }
            
            if (fakePlayerSteamIds.Count == 0) return;
            
            // Filter the result array to exclude fake player steam IDs
            __result = __result.Where(steamId => !fakePlayerSteamIds.Contains(steamId)).ToArray();
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[MaxPractice] GetPlayerSteamIds patch error: {ex.Message}");
        }
    }
}
*/