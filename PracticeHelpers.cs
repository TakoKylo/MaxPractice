// PracticeHelpers.cs - Utility functions for MaxPractice

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace MaxPractice
{
    public static class PracticeHelpers
    {
        private static PropertyInfo _playerTeamProperty;
        private static PropertyInfo _playerRoleProperty;
        private static PropertyInfo _playerTeamValueProperty;
        private static PropertyInfo _playerRoleValueProperty;
        private static PropertyInfo _isCharacterSpawnedProperty;
        private static PropertyInfo _isCharacterFullySpawnedProperty;

        // Player lookup cache to reduce repeated searches
        private static Dictionary<ulong, Player> _playerCache = new Dictionary<ulong, Player>();
        private static float _playerCacheExpiry = 0f;
        private const float PLAYER_CACHE_DURATION = 0.5f; // Cache valid for 0.5 seconds
        
        // Cached puck visuals for repairing broken pucks
        private static Material _cachedPuckMaterial = null;
        private static Mesh _cachedPuckMesh = null;
        private static bool _visualsCached = false;
        
        /// <summary>
        /// Get a unique ID for a player - prefers OwnerClientId for server reliability
        /// </summary>
        public static ulong GetSteamIdFromPlayer(Player p)
        {
            try
            {
                // Prefer OwnerClientId - it's the most reliable on dedicated servers
                if (p?.NetworkObject != null && p.NetworkObject.OwnerClientId != 0)
                {
                    return p.NetworkObject.OwnerClientId;
                }
                
                // Fallback to SteamId if available
                if (p?.SteamId != null)
                {
                    var steamIdValue = p.SteamId.Value;
                    if (ulong.TryParse(steamIdValue.ToString(), out ulong val) && val != 0)
                        return val;
                }
            }
            catch { }
            return 0UL;
        }
        
        /// <summary>
        // ── Pending-chat queue (MuteMod pattern for dedicated-server compatibility) ──
        private struct PendingChatSend
        {
            public ulong[] ClientIds;
            public ChatMessage Message;
        }
        private static readonly Queue<PendingChatSend> _pendingChatSends = new Queue<PendingChatSend>();

        private static void EnqueueSystemChat(ulong[] clientIds, string msg)
        {
            if (clientIds == null || clientIds.Length == 0) return;
            lock (_pendingChatSends)
            {
                _pendingChatSends.Enqueue(new PendingChatSend
                {
                    ClientIds = clientIds,
                    Message = CreateSystemChatMessage(msg),
                });
            }
        }

        public static void FlushPendingChats()
        {
            ChatManager chatManager = NetworkBehaviourSingleton<ChatManager>.Instance;
            if (chatManager == null)
                chatManager = UnityEngine.Object.FindFirstObjectByType<ChatManager>();
            if (chatManager == null) return;

            while (true)
            {
                PendingChatSend pending;
                lock (_pendingChatSends)
                {
                    if (_pendingChatSends.Count == 0) break;
                    pending = _pendingChatSends.Dequeue();
                }
                if (pending.ClientIds != null && pending.ClientIds.Length > 0)
                    chatManager.Server_SendChatMessageToClients(pending.Message, pending.ClientIds);
            }
        }

        private static ChatMessage CreateSystemChatMessage(string msg)
        {
            return new ChatMessage
            {
                Username = null,
                Content = new FixedString512Bytes(msg ?? string.Empty),
                Timestamp = Time.realtimeSinceStartupAsDouble,
                IsQuickChat = false,
                IsTeamChat = false,
                IsSystem = true,
                SteamID = null,
                Team = null,
            };
        }

        private static ulong[] GetConnectedClientIds()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return Array.Empty<ulong>();
            var ids = new ulong[nm.ConnectedClientsList.Count];
            for (int i = 0; i < nm.ConnectedClientsList.Count; i++)
                ids[i] = nm.ConnectedClientsList[i].ClientId;
            return ids;
        }

        /// B310 compatibility: Send system chat message. Gracefully handles API changes.
        /// </summary>
        public static void SendChatMessage(UIChat ui, string message, ulong clientId = 0)
        {
            try
            {
                ulong[] clientIds = clientId > 0 ? new ulong[] { clientId } : GetConnectedClientIds();
                EnqueueSystemChat(clientIds, message);
            }
            catch { } // Silently fail if chat API not available
        }

        public static PlayerTeam GetPlayerTeam(Player player)
        {
            if (player == null) return PlayerTeam.None;

            try
            {
                _playerTeamProperty ??= typeof(Player).GetProperty("Team", BindingFlags.Public | BindingFlags.Instance);
                if (_playerTeamProperty != null)
                {
                    object teamValue = _playerTeamProperty.GetValue(player);
                    if (teamValue is PlayerTeam directTeam)
                        return directTeam;

                    if (teamValue != null)
                    {
                        _playerTeamValueProperty ??= teamValue.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                        if (_playerTeamValueProperty != null)
                        {
                            object nestedValue = _playerTeamValueProperty.GetValue(teamValue);
                            if (nestedValue is PlayerTeam nestedTeam)
                                return nestedTeam;
                        }
                    }
                }
            }
            catch { }

            return PlayerTeam.None;
        }

        public static PlayerRole GetPlayerRole(Player player)
        {
            if (player == null) return PlayerRole.None;

            try
            {
                _playerRoleProperty ??= typeof(Player).GetProperty("Role", BindingFlags.Public | BindingFlags.Instance);
                if (_playerRoleProperty != null)
                {
                    object roleValue = _playerRoleProperty.GetValue(player);
                    if (roleValue is PlayerRole directRole)
                        return directRole;

                    if (roleValue != null)
                    {
                        _playerRoleValueProperty ??= roleValue.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                        if (_playerRoleValueProperty != null)
                        {
                            object nestedValue = _playerRoleValueProperty.GetValue(roleValue);
                            if (nestedValue is PlayerRole nestedRole)
                                return nestedRole;
                        }
                    }
                }
            }
            catch { }

            return PlayerRole.None;
        }

        public static bool IsCharacterSpawned(Player player)
        {
            if (player == null) return false;

            try
            {
                _isCharacterSpawnedProperty ??= typeof(Player).GetProperty("IsCharacterSpawned", BindingFlags.Public | BindingFlags.Instance);
                if (_isCharacterSpawnedProperty != null)
                    return _isCharacterSpawnedProperty.GetValue(player) is bool isSpawned && isSpawned;

                _isCharacterFullySpawnedProperty ??= typeof(Player).GetProperty("IsCharacterFullySpawned", BindingFlags.Public | BindingFlags.Instance);
                if (_isCharacterFullySpawnedProperty != null)
                    return _isCharacterFullySpawnedProperty.GetValue(player) is bool isFullySpawned && isFullySpawned;
            }
            catch { }

            return false;
        }
        
        /// <summary>
        /// Find a player by their Steam ID (cached for performance)
        /// </summary>
        public static Player FindPlayerBySteamId(ulong steamId)
        {
            // Check cache expiry
            if (Time.time > _playerCacheExpiry)
            {
                _playerCache.Clear();
                _playerCacheExpiry = Time.time + PLAYER_CACHE_DURATION;
            }
            
            // Try cache first
            if (_playerCache.TryGetValue(steamId, out Player cached))
            {
                // Validate cached player is still valid
                if (cached != null && cached.gameObject != null)
                    return cached;
                // Invalid - remove from cache
                _playerCache.Remove(steamId);
            }
            
            // Cache miss - do lookup
            Player found = FindPlayerBySteamIdUncached(steamId);
            if (found != null)
            {
                _playerCache[steamId] = found;
            }
            return found;
        }
        
        /// <summary>
        /// Clear player cache (call when players join/leave)
        /// </summary>
        public static void ClearPlayerCache()
        {
            _playerCache.Clear();
            _playerCacheExpiry = 0f;
        }
        
        /// <summary>
        /// Find a player by their client ID (or Steam ID for backwards compatibility)
        /// </summary>
        private static Player FindPlayerBySteamIdUncached(ulong clientId)
        {
            // On dedicated servers, PlayerManager is most reliable
            var pm = MonoBehaviourSingleton<PlayerManager>.Instance;
            if (pm != null)
            {
                foreach (var p in pm.GetPlayers())
                {
                    if (p == null) continue;

                    if (p.NetworkObject?.OwnerClientId == clientId)
                        return p;

                    if (GetSteamIdFromPlayer(p) == clientId)
                        return p;

                    try
                    {
                        if (p.SteamId != null && ulong.TryParse(p.SteamId.Value.ToString(), out ulong parsedSteamId) && parsedSteamId == clientId)
                            return p;
                    }
                    catch { }
                }
            }
            
            // Fallback: try NetworkManager connected clients
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.ConnectedClientsList != null)
            {
                foreach (var client in nm.ConnectedClientsList)
                {
                    // Direct client ID match
                    if (client.ClientId == clientId)
                    {
                        var p = client.PlayerObject?.GetComponent<Player>();
                        if (p != null) return p;
                    }
                }
                
                // Fallback: check player's NetworkObject owner client ID
                foreach (var client in nm.ConnectedClientsList)
                {
                    var p = client?.PlayerObject?.GetComponent<Player>();
                    if (p != null && (p.NetworkObject?.OwnerClientId == clientId || GetSteamIdFromPlayer(p) == clientId))
                        return p;
                }
            }
            
            return null;
        }
        
        /// <summary>
        /// Send a chat message to a specific player by Steam ID
        /// </summary>
        public static void SendMessageToPlayer(ulong steamId, string message)
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm != null)
                {
                    foreach (var client in nm.ConnectedClientsList)
                    {
                        var p = client?.PlayerObject?.GetComponent<Player>();
                        if (p != null && GetSteamIdFromPlayer(p) == steamId)
                        {
                            PracticeHelpers.SendChatMessage(null, message, client.ClientId);
                            break;
                        }
                    }
                }
            }
            catch { }
        }
        
        /// <summary>
        /// Check if a real goalie player exists on the specified team
        /// </summary>
        public static bool HasRealGoalieOnTeam(PlayerTeam team)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return false;
            
            foreach (var client in nm.ConnectedClientsList)
            {
                var p = client?.PlayerObject?.GetComponent<Player>();
                if (p == null) continue;
                if (MaxPracticePlugin.FakePlayers.Contains(p)) continue;
                
                if (GetPlayerRole(p) == PlayerRole.Goalie && GetPlayerTeam(p) == team)
                    return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Convert MPH to meters per second
        /// </summary>
        public static float MphToMps(float mph) => mph * 0.44704f;
        
        /// <summary>
        /// Convert meters per second to MPH
        /// </summary>
        public static float MpsToMph(float mps) => mps / 0.44704f;
        
        /// <summary>
        /// Spawn a puck and auto-cleanup if over threshold. Returns the spawned puck.
        /// </summary>
        public static Puck SpawnPuckWithCleanup(Vector3 position, Quaternion rotation, Vector3 velocity, bool isReplay = false)
        {
            var puckManager = MonoBehaviourSingleton<PuckManager>.Instance;
            if (puckManager == null) return null;
            
            // Check puck count BEFORE spawning using PuckManager's internal list (efficient)
            var pucks = puckManager.GetPucks(true); // true = include replay pucks in count
            int maxPucks = ConfigManager.Config.MaxPucksBeforeCleanup;
            
            // Count only non-handle pucks for the threshold check
            int handlePuckCount = 0;
            foreach (var kvp in MaxPracticePlugin.HandlePucks)
            {
                foreach (var hp in kvp.Value)
                    if (hp != null) handlePuckCount++;
            }
            int realPuckCount = (pucks?.Count ?? 0) - handlePuckCount;
            
            if (realPuckCount >= maxPucks - 1) // -1 because we're about to spawn one
            {
                CleanupExcessPucks(pucks);
            }
            
            var puck = puckManager.Server_SpawnPuck(position, rotation, isReplay);
            
            // Validate and repair puck visuals (fixes issue where mesh/materials become null after player join/leave)
            if (puck != null)
            {
                    if (puck.Rigidbody != null)
                    {
                        puck.Rigidbody.linearVelocity = velocity;
                    }
                ValidateAndRepairPuckVisuals(puck);
            }
            
            return puck;
        }
        
        /// <summary>
        /// Validate and repair puck visuals. Sometimes on dedicated servers, puck mesh/materials
        /// become null after players join/leave. This ensures the puck has proper visuals.
        /// </summary>
        public static void ValidateAndRepairPuckVisuals(Puck puck)
        {
            if (puck == null) return;
            
            try
            {
                var meshRenderers = puck.GetComponentsInChildren<MeshRenderer>(true);
                var meshFilters = puck.GetComponentsInChildren<MeshFilter>(true);
                
                bool hasValidVisuals = false;
                foreach (var mr in meshRenderers)
                {
                    if (mr != null && mr.enabled && mr.sharedMaterial != null)
                    {
                        var mf = mr.GetComponent<MeshFilter>();
                        if (mf != null && mf.sharedMesh != null)
                        {
                            hasValidVisuals = true;
                            
                            // Cache these visuals for future repairs
                            if (!_visualsCached)
                            {
                                _cachedPuckMaterial = mr.sharedMaterial;
                                _cachedPuckMesh = mf.sharedMesh;
                                _visualsCached = true;
                                Debug.Log("[MaxPractice] Cached puck visuals for future repairs");
                            }
                            break;
                        }
                    }
                }
                
                if (!hasValidVisuals)
                {
                    Debug.LogWarning("[MaxPractice] Puck spawned without valid visuals - attempting repair");
                    
                    // Try cached visuals first (most reliable)
                    if (_visualsCached && _cachedPuckMaterial != null && _cachedPuckMesh != null)
                    {
                        foreach (var mr in meshRenderers)
                        {
                            if (mr != null)
                            {
                                mr.sharedMaterial = _cachedPuckMaterial;
                                mr.enabled = true;
                            }
                        }
                        foreach (var mf in meshFilters)
                        {
                            if (mf != null)
                            {
                                mf.sharedMesh = _cachedPuckMesh;
                            }
                        }
                        Debug.Log("[MaxPractice] Puck visuals repaired from cache");
                        return;
                    }
                    
                    // Fallback: Try to get visuals from another valid puck
                    var puckManager = MonoBehaviourSingleton<PuckManager>.Instance;
                    if (puckManager != null)
                    {
                        var allPucks = puckManager.GetPucks(false);
                        if (allPucks != null)
                        {
                            foreach (var existingPuck in allPucks)
                            {
                                if (existingPuck == puck || existingPuck == null) continue;
                                
                                var existingMRs = existingPuck.GetComponentsInChildren<MeshRenderer>(true);
                                foreach (var existingMR in existingMRs)
                                {
                                    if (existingMR != null && existingMR.sharedMaterial != null)
                                    {
                                        var existingMF = existingMR.GetComponent<MeshFilter>();
                                        if (existingMF != null && existingMF.sharedMesh != null)
                                        {
                                            // Cache for future use
                                            _cachedPuckMaterial = existingMR.sharedMaterial;
                                            _cachedPuckMesh = existingMF.sharedMesh;
                                            _visualsCached = true;
                                            
                                            // Found valid source - copy to our puck
                                            foreach (var mr in meshRenderers)
                                            {
                                                if (mr != null)
                                                {
                                                    mr.sharedMaterial = existingMR.sharedMaterial;
                                                    mr.enabled = true;
                                                }
                                            }
                                            foreach (var mf in meshFilters)
                                            {
                                                if (mf != null && mf.gameObject.name == existingMF.gameObject.name)
                                                {
                                                    mf.sharedMesh = existingMF.sharedMesh;
                                                }
                                            }
                                            Debug.Log("[MaxPractice] Puck visuals repaired from existing puck");
                                            return;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    
                    Debug.LogWarning("[MaxPractice] Could not repair puck visuals - no valid source found");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaxPractice] Error validating puck visuals: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Strip a puck of expensive components for use as a static obstacle.
        /// Keeps colliders for puck-puck physics, but disables stick collision.
        /// This dramatically reduces CPU/GPU load for handle pucks.
        /// </summary>
        public static void StripPuckForObstacle(Puck puck)
        {
            if (puck == null) return;
            
            try
            {
                var go = puck.gameObject;
                
                // NOTE: Keep MeshRenderers enabled so pucks are visible!
                
                // Disable all audio sources
                var audioSources = go.GetComponentsInChildren<AudioSource>(true);
                foreach (var a in audioSources)
                    if (a != null) a.enabled = false;
                
                // Disable SynchronizedAudio components
                var syncAudios = go.GetComponentsInChildren<SynchronizedAudio>(true);
                foreach (var sa in syncAudios)
                    if (sa != null) sa.enabled = false;
                
                // Disable SynchronizedObject (stops network transform sync - reduces bandwidth)
                var syncObj = puck.SynchronizedObject;
                if (syncObj != null) syncObj.enabled = false;
                
                // Disable NetworkObjectCollisionRecorder (we don't need collision tracking for static obstacles)
                var collisionBuffer = puck.NetworkObjectCollisionRecorder;
                if (collisionBuffer != null) collisionBuffer.enabled = false;
                
                // Disable CollisionRecorder
                var collisionRecorder = puck.CollisionRecorder;
                if (collisionRecorder != null) collisionRecorder.enabled = false;
                
                // Disable the Puck component itself (stops FixedUpdate processing)
                puck.enabled = false;
                
                // Disable STICK collision only - keep puck-puck collision working
                // StickCollider is the collider that detects stick blade contact
                var stickCollider = puck.StickCollider;
                if (stickCollider != null) stickCollider.enabled = false;
                
                // Also disable the net sphere collider (not needed for obstacles)
                var netCollider = puck.NetSphereCollider;
                if (netCollider != null) netCollider.enabled = false;
                
                // Freeze the rigidbody completely (no physics simulation needed)
                var rb = puck.Rigidbody;
                if (rb != null)
                {
                    rb.constraints = RigidbodyConstraints.FreezeAll;
                    rb.isKinematic = true; // Even better - no physics simulation at all
                }
                
                // Destroy elevation indicator GameObjects completely (disabling doesn't stop the visual glitch)
                var elevationIndicators = go.GetComponentsInChildren<PuckElevationIndicator>(true);
                foreach (var ei in elevationIndicators)
                {
                    if (ei != null && ei.gameObject != null)
                        UnityEngine.Object.Destroy(ei.gameObject);
                }
                
                // Also destroy the controller
                var elevationControllers = go.GetComponentsInChildren<PuckElevationIndicatorController>(true);
                foreach (var ec in elevationControllers)
                {
                    if (ec != null && ec.gameObject != null)
                        UnityEngine.Object.Destroy(ec.gameObject);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaxPractice] Error stripping puck: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Disable stick collision on a handle puck and set up collision ignoring for other pucks and player bodies.
        /// This keeps the puck looking normal (texture intact) but prevents physics interactions.
        /// Called after a delay to ensure the puck is fully initialized first.
        /// </summary>
        public static void DisableHandlePuckStickCollision(Puck puck)
        {
            if (puck == null) return;
            
            try
            {
                // Disable stick collision so players can't interact with it
                var stickCollider = puck.StickCollider;
                if (stickCollider != null) stickCollider.enabled = false;
                
                // Freeze the rigidbody so pucks don't move
                var rb = puck.Rigidbody;
                if (rb != null)
                {
                    rb.constraints = RigidbodyConstraints.FreezeAll;
                    rb.isKinematic = true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaxPractice] Error disabling handle puck collision: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Set up collision ignoring between a handle puck and player bodies only.
        /// Handle pucks should still collide with regular pucks so they can be used as obstacles.
        /// </summary>
        public static void SetupHandlePuckCollisionIgnoring(Puck handlePuck)
        {
            if (handlePuck == null) return;
            
            try
            {
                // Get all colliders on the handle puck
                var handleColliders = handlePuck.GetComponentsInChildren<Collider>(true);
                if (handleColliders == null || handleColliders.Length == 0) return;
                
                // Ignore collisions with all player bodies
                var playerManager = MonoBehaviourSingleton<PlayerManager>.Instance;
                if (playerManager != null)
                {
                    foreach (var player in playerManager.GetPlayers(false))
                    {
                        if (player == null || player.PlayerBody == null) continue;
                        
                        var bodyColliders = player.PlayerBody.GetComponentsInChildren<Collider>(true);
                        if (bodyColliders == null) continue;
                        
                        foreach (var hCol in handleColliders)
                        {
                            if (hCol == null) continue;
                            foreach (var bCol in bodyColliders)
                            {
                                if (bCol != null)
                                    Physics.IgnoreCollision(hCol, bCol, true);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaxPractice] Error setting up handle puck collision ignoring: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Spawn a handle puck that initializes normally, then gets frozen after a short delay.
        /// Collision ignoring and stick collision disabled immediately to prevent physics chaos.
        /// Puck is frozen after the delay.
        /// </summary>
        public static System.Collections.IEnumerator SpawnHandlePuckDelayed(Vector3 position, ulong ownerSteamId)
        {
            var puck = SpawnPuckWithCleanup(position, Quaternion.identity, Vector3.zero, false);
            if (puck == null) yield break;
            
            // IMMEDIATELY disable stick collision so players can't interact with handle pucks
            try
            {
                var stickCollider = puck.StickCollider;
                if (stickCollider != null) stickCollider.enabled = false;
            }
            catch { }
            
            // IMMEDIATELY set up collision ignoring to prevent physics chaos
            SetupHandlePuckCollisionIgnoring(puck);
            
            // Add to the owner's handle puck list immediately so cleanup tracks it
            if (!MaxPracticePlugin.HandlePucks.ContainsKey(ownerSteamId))
                MaxPracticePlugin.HandlePucks[ownerSteamId] = new System.Collections.Generic.List<Puck>();
            MaxPracticePlugin.HandlePucks[ownerSteamId].Add(puck);
            
            // Wait a short moment for the puck to fully initialize (network sync, visuals, etc)
            yield return new WaitForSeconds(0.15f);
            
            // Now apply full modifications - disable stick collision and freeze puck
            if (puck != null && puck.gameObject != null)
            {
                DisableHandlePuckStickCollision(puck);
                
                // Re-apply collision ignoring in case new pucks/players spawned during delay
                SetupHandlePuckCollisionIgnoring(puck);
            }
        }
        
        /// <summary>
        /// Cleanup excess pucks, keeping handle pucks and closest puck to each player
        /// </summary>
        public static void CleanupExcessPucks(System.Collections.Generic.List<Puck> pucks = null)
        {
            try
            {
                var puckManager = MonoBehaviourSingleton<PuckManager>.Instance;
                if (pucks == null && puckManager != null)
                    pucks = puckManager.GetPucks(true);
                
                if (pucks == null || pucks.Count == 0) return;
                
                // Get all handle pucks to exclude them
                var allHandlePucks = new System.Collections.Generic.HashSet<Puck>();
                foreach (var kvp in MaxPracticePlugin.HandlePucks)
                {
                    foreach (var hp in kvp.Value)
                        if (hp != null) allHandlePucks.Add(hp);
                }
                
                // Find each player's 1 closest puck to preserve
                var protectedPucks = new System.Collections.Generic.HashSet<Puck>();
                var nm = NetworkManager.Singleton;
                if (nm != null)
                {
                    foreach (var client in nm.ConnectedClientsList)
                    {
                        var p = client?.PlayerObject?.GetComponent<Player>();
                        if (p?.Stick == null) continue;
                        Vector3 bladePos = p.Stick.BladeHandlePosition;
                        
                        // Find closest puck to this player
                        Puck closestPuck = null;
                        float closestDist = float.MaxValue;
                        foreach (var puck in pucks)
                        {
                            if (puck == null || allHandlePucks.Contains(puck)) continue;
                            float dist = Vector3.Distance(puck.transform.position, bladePos);
                            if (dist < closestDist)
                            {
                                closestDist = dist;
                                closestPuck = puck;
                            }
                        }
                        if (closestPuck != null)
                            protectedPucks.Add(closestPuck);
                    }
                }
                
                // Destroy pucks that aren't handle pucks or protected
                int cleared = 0;
                foreach (var puck in pucks.ToArray()) // ToArray to avoid modification during iteration
                {
                    if (puck != null && !allHandlePucks.Contains(puck) && !protectedPucks.Contains(puck))
                    {
                        UnityEngine.Object.DestroyImmediate(puck.gameObject);
                        cleared++;
                    }
                }
                
                if (cleared > 0)
                {
                    int kept = protectedPucks.Count;
                    Debug.Log($"[MaxPractice] Auto-cleared {cleared} pucks, kept {kept} closest to players");
                    PracticeHelpers.SendChatMessage(null, $"<size=70%><color=#FFFFFFFF>Auto-cleared {cleared} pucks, kept {kept} closest to players.</color></size>");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MaxPractice] Error in cleanup pucks: {ex}");
            }
        }
    }
}
