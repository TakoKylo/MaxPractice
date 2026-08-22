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
        /// True only while the game is in warmup - the one phase this mod is allowed to
        /// act in.
        ///
        /// Every practice command already goes through the gate in
        /// PracticeCommandPatch.HandlePracticeCommand, but that gate is checked once, at
        /// the moment the command is typed. Anything that outlives that instant - a
        /// coroutine sitting on a WaitForSeconds, a sampling loop, a periodic sweep -
        /// has to ask again, because the phase can and does change underneath it.
        /// Returning false when GameManager is missing is deliberate: no phase means no
        /// practice.
        /// </summary>
        public static bool IsWarmup
        {
            get
            {
                var gm = NetworkBehaviourSingleton<GameManager>.Instance;
                return gm != null && gm.Phase == GamePhase.Warmup;
            }
        }

        /// <summary>
        /// Get a unique ID for a player - prefers OwnerClientId for server reliability
        /// </summary>
        public static ulong GetSteamIdFromPlayer(Player p)
        {
            try
            {
                // Prefer OwnerClientId - it's the most reliable on dedicated servers.
                // Plain != null, not ?. : the null-conditional operator is a reference test
                // and deliberately bypasses UnityEngine.Object's overloaded ==, so on a
                // DESTROYED Player it proceeds to read the member and throws
                // MissingReferenceException. Unity's != short-circuits instead.
                if (p != null && p.NetworkObject != null && p.NetworkObject.OwnerClientId != 0)
                {
                    return p.NetworkObject.OwnerClientId;
                }

                // Fallback to SteamId if available
                if (p != null && p.SteamId != null)
                {
                    var steamIdValue = p.SteamId.Value;
                    if (ulong.TryParse(steamIdValue.ToString(), out ulong val) && val != 0)
                        return val;
                }
            }
            catch { }
            return 0UL;
        }
        
        // ── Pending-chat queue (queued so we can send from any thread / lifecycle moment;
        // FlushPendingChats drains it once a frame from PracticeManager.Update on the main
        // thread, after NetworkManager.Singleton.IsServer is verified).
        //
        // Uses the STRING overloads of ChatManager.Server_BroadcastChatMessage /
        // Server_SendChatMessage — the same path OpenWorldPracticeMod uses. The
        // ChatMessage-struct overload would let us set Username/SteamID/etc, but with those
        // fields null the client-side AddChatMessage drops the message silently. The string
        // overloads build a proper system ChatMessage server-side and ship it intact.
        /// <summary>
        /// "Send this to everyone" marker for the chat helpers.
        ///
        /// This used to be 0, which is not a spare value: 0 is NetworkManager.ServerClientId,
        /// and on a listen server that is the host's own real client id. So every message
        /// meant privately for the host - cooldown refusals, command errors, /practice's
        /// whole help listing - was broadcast to the entire server instead. ulong.MaxValue
        /// is never a real client id, so the two cases can no longer be confused.
        /// </summary>
        public const ulong BroadcastClientId = ulong.MaxValue;

        private struct PendingChatSend
        {
            public ulong ClientId; // BroadcastClientId = broadcast to everyone
            public string Content;
        }
        private static readonly Queue<PendingChatSend> _pendingChatSends = new Queue<PendingChatSend>();

        private static void EnqueueSystemChat(ulong clientId, string msg)
        {
            lock (_pendingChatSends)
            {
                _pendingChatSends.Enqueue(new PendingChatSend
                {
                    ClientId = clientId,
                    Content = msg ?? string.Empty,
                });
            }
        }

        public static void FlushPendingChats()
        {
            // Cheapest test first. This is pumped every frame from PracticeManager.Update on
            // the server and the queue is empty on virtually all of those frames, but the
            // manager was being resolved before anyone asked whether there was anything to
            // send - and when the singleton is not up yet (menu, level load, a dedicated
            // server before the first scene) that fell through to a whole-scene
            // FindFirstObjectByType<ChatManager>, every frame, forever.
            lock (_pendingChatSends)
            {
                if (_pendingChatSends.Count == 0) return;
            }

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
                try
                {
                    if (pending.ClientId == BroadcastClientId)
                        chatManager.Server_BroadcastChatMessage(pending.Content);
                    else
                        chatManager.Server_SendChatMessage(pending.Content, null, new ulong[] { pending.ClientId });
                }
                catch { }
            }
        }

        /// <summary>
        /// Send a system chat message. Omitting clientId (or passing <see cref="BroadcastClientId"/>)
        /// broadcasts to everyone; any other value targets that one client — including 0, which is
        /// the host's own client id on a listen server.
        /// The first parameter (UIChat) is unused — kept for back-compat with existing call sites.
        /// </summary>
        public static void SendChatMessage(UIChat ui, string message, ulong clientId = BroadcastClientId)
        {
            try { EnqueueSystemChat(clientId, message); }
            catch { }
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
                // A cached null is a remembered MISS - honour it until the cache expires
                // rather than re-scanning, which is the point of caching misses at all.
                if (ReferenceEquals(cached, null)) return null;

                // Validate cached player is still valid
                if (cached != null && cached.gameObject != null)
                    return cached;
                // Destroyed since - drop it and re-look-up.
                _playerCache.Remove(steamId);
            }
            
            // Cache miss - do lookup.
            //
            // MISSES are cached too (as null). Without that, an id that cannot resolve -
            // a player mid-respawn, or a stale entry in YoyoPlayers - re-ran the full
            // PlayerManager scan on EVERY call, and YoyoStringNetwork.BuildCurrentSet calls
            // this once per yoyo pair per frame. The scan allocates a string per player it
            // walks past, so one unresolvable id turned into a steady per-frame allocation
            // that never self-corrected. The whole cache still clears every
            // PLAYER_CACHE_DURATION, so a null entry costs at most that long.
            Player found = FindPlayerBySteamIdUncached(steamId);
            _playerCache[steamId] = found;
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
                // Skip anything belonging to a cone visual. Its renderer would read
                // as "valid puck visuals" and get cached as the known-good puck
                // mesh, which would then be stamped onto ordinary pucks.
                var meshRenderers = puck.GetComponentsInChildren<MeshRenderer>(true)
                                        .Where(mr => !ConeAsset.IsConePart(mr)).ToArray();
                var meshFilters = puck.GetComponentsInChildren<MeshFilter>(true)
                                      .Where(mf => !ConeAsset.IsConePart(mf)).ToArray();
                
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
                // Get all colliders on the handle puck. A cone puck has its own
                // colliders switched off in favour of the cone's - Physics.IgnoreCollision
                // errors out on a disabled collider, so only pass live ones.
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
                            if (hCol == null || !hCol.enabled) continue;
                            foreach (var bCol in bodyColliders)
                            {
                                if (bCol != null && bCol.enabled)
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
        /// True when nothing already placed by /cones or /minefield sits within
        /// <paramref name="minSeparation"/> of this spot.
        ///
        /// Checked against every player's handle pucks, not just the caller's - two
        /// players laying out drills in the same corner shouldn't interleave their
        /// cones. Distance is measured on the ice plane; height doesn't matter,
        /// since everything here is sitting on it.
        /// </summary>
        public static bool IsHandleSpotClear(Vector3 position, float minSeparation)
        {
            float minSqr = minSeparation * minSeparation;

            foreach (var kvp in MaxPracticePlugin.HandlePucks)
            {
                var pucks = kvp.Value;
                if (pucks == null) continue;

                foreach (var puck in pucks)
                {
                    if (puck == null) continue;

                    Vector3 delta = puck.transform.position - position;
                    delta.y = 0f;
                    if (delta.sqrMagnitude < minSqr) return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Spawn a frozen puck to act as a prop's anchor, and dress it.
        ///
        /// Unlike a cone, these are placed deliberately and have a heading that
        /// matters, so the puck is frozen immediately rather than after a settle
        /// delay - a shooter that rolled a foot before locking would be aimed at
        /// nothing. Its rotation carries the prop's heading to clients, since
        /// Netcode replicates the puck transform but not the mesh riding on it.
        /// </summary>
        public static Puck SpawnPropPuck(Vector3 position, Quaternion rotation, PropKind kind, ulong ownerSteamId)
        {
            var puck = SpawnPuckWithCleanup(position, rotation, Vector3.zero, false);
            if (puck == null) return null;

            try
            {
                var stickCollider = puck.StickCollider;
                if (stickCollider != null) stickCollider.enabled = false;
            }
            catch { }

            var rb = puck.Rigidbody;
            if (rb != null)
            {
                rb.isKinematic = true;
                rb.constraints = RigidbodyConstraints.FreezeAll;
                // The prop is posed by hand, never simulated, so interpolation has nothing
                // useful to smooth and can only smear a re-aim back toward the old pose.
                rb.interpolation = RigidbodyInterpolation.None;
            }

            // Tracked as a handle puck so /clearall and the phase-change cleanup
            // already know how to get rid of it.
            if (!MaxPracticePlugin.HandlePucks.ContainsKey(ownerSteamId))
                MaxPracticePlugin.HandlePucks[ownerSteamId] = new System.Collections.Generic.List<Puck>();
            MaxPracticePlugin.HandlePucks[ownerSteamId].Add(puck);

            MaxPracticePlugin.PropPucks[puck] = kind;
            MaxPracticePlugin.PropOwner[puck] = ownerSteamId;

            // Pass the owner through so a host builds the same nameplate and team colour
            // a remote client builds from the announcement.
            PropNetwork.ApplyLocal(puck, kind, true, FindPlayerBySteamId(ownerSteamId));
            PropNetwork.Announce();
            SetupHandlePuckCollisionIgnoring(puck);

            return puck;
        }

        /// <summary>
        /// Put the puck shooter at a player's saved pass position, aimed at them.
        /// Replaces any shooter they already had, so it follows /pass around rather
        /// than leaving a machine behind at every spot they've stood.
        /// </summary>
        /// <summary>How far the shooter tilts up for a lob pass.</summary>
        private const float LobPitchDegrees = 24f;

        /// <returns>
        /// false when the machine could not be placed. The old one is cleared first either
        /// way - it holds a puck slot, so releasing it is what lets the replacement through
        /// at the puck cap - which means a failure leaves the player with no shooter at all.
        /// Passes still work (PlayerPassSettings is separate) but fire from the bare blade
        /// point, because TryGetMuzzle has nothing to find, so the caller has to say so
        /// rather than reporting success.
        /// </returns>
        public static bool SetShooter(ulong ownerSteamId, Vector3 passFrom, Vector3 aimAt)
        {
            ClearShooter(ownerSteamId);

            Vector3 pos = passFrom;
            pos.y = RinkSheets.IceHeightAt(pos, 0.08f);

            Vector3 dir = aimAt - pos;
            dir.y = 0f;
            Quaternion rot = dir.sqrMagnitude > 1e-6f
                ? Quaternion.LookRotation(dir.normalized, Vector3.up)
                : Quaternion.identity;

            // A lob is a high pass, so the machine should be aiming high. The tilt goes on
            // the ANCHOR PUCK rather than the visual: puck rotation is already replicated,
            // so ShooterVisual picks the pitch up on every client with nothing added to the
            // prop announcement. Negative X tilts the local forward axis upward.
            if (YoyoManager.PlayerPassSettings.TryGetValue(ownerSteamId, out var passSettings)
                && passSettings != null && passSettings.IsLob)
            {
                rot *= Quaternion.Euler(-LobPitchDegrees, 0f, 0f);
            }

            var puck = SpawnPropPuck(pos, rot, PropKind.Shooter, ownerSteamId);
            if (puck == null) return false;

            MaxPracticePlugin.PlayerShooter[ownerSteamId] = puck;
            return true;
        }

        public static void ClearShooter(ulong ownerSteamId)
        {
            if (MaxPracticePlugin.PlayerShooter.TryGetValue(ownerSteamId, out var existing) && existing != null)
                MaxPracticePlugin.DestroyProp(existing);
            MaxPracticePlugin.PlayerShooter.Remove(ownerSteamId);
        }

        /// <summary>
        /// Where a player's shooter actually fires from. Passes spawn here rather
        /// than at the saved blade point, so the puck leaves the muzzle instead of
        /// appearing somewhere inside the machine.
        /// </summary>
        public static bool TryGetMuzzle(ulong ownerSteamId, out Vector3 world)
        {
            world = Vector3.zero;

            if (!MaxPracticePlugin.PlayerShooter.TryGetValue(ownerSteamId, out var puck) || puck == null)
                return false;

            var visual = puck.GetComponentInChildren<ShooterVisual>(true);
            if (visual == null) return false;

            world = visual.transform.TransformPoint(ShooterAsset.MuzzleLocal);
            return true;
        }

        /// <summary>Swing a player's shooter round to face where the next pass is going.</summary>
        public static void AimShooter(ulong ownerSteamId, Vector3 target)
        {
            if (!MaxPracticePlugin.PlayerShooter.TryGetValue(ownerSteamId, out var puck) || puck == null)
                return;

            // Take the lob tilt from the SETTINGS, which is where it is decided, rather than
            // reading it back off the transform we are about to overwrite.
            float pitch = 0f;
            if (YoyoManager.PlayerPassSettings.TryGetValue(ownerSteamId, out var settings)
                && settings != null && settings.IsLob)
            {
                pitch = -LobPitchDegrees;
            }

            AimPropAt(puck, target, false, pitch);
        }

        /// <summary>
        /// Point a prop's anchor puck at a world position. The rotation replicates, so
        /// clients see it swing round too.
        /// </summary>
        /// <param name="pitchDegrees">
        /// Tilt about the prop's local X after the yaw - negative aims up. Passed in by the
        /// caller rather than recovered from the transform: reading the old pitch back out
        /// of rotation.eulerAngles round-trips a quaternion through a decomposition that is
        /// not unique, and feeding that straight back into the same transform every pass is
        /// exactly the kind of loop that ends up pinned or flipped. Callers with no tilt
        /// (cones, nets) pass 0 and get the plain level aim this always had.
        /// </param>
        public static void AimPropAt(Puck puck, Vector3 target, bool faceAway = false, float pitchDegrees = 0f)
        {
            if (puck == null || puck.transform == null) return;

            Vector3 dir = target - puck.transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f) return;

            if (faceAway) dir = -dir;

            Quaternion aim = Mathf.Abs(pitchDegrees) < 0.01f
                ? Quaternion.LookRotation(dir.normalized, Vector3.up)
                : Quaternion.LookRotation(dir.normalized, Vector3.up) * Quaternion.Euler(pitchDegrees, 0f, 0f);

            puck.transform.rotation = aim;

            // And the RIGIDBODY, not just the transform.
            //
            // A prop anchor is frozen kinematic with FreezeAll (SpawnPropPuck), and for a
            // Rigidbody the physics sync treats rb.rotation as the authority: a
            // transform-only poke is put straight back on the next sync, and rb.rotation is
            // also the pose the game's SynchronizedObject layer samples to replicate. The
            // machine's SPAWN heading survives because it rides the spawn payload, so the
            // symptom is a shooter that stands up pointing the right way and then never
            // turns again however many passes fire.
            var rb = puck.Rigidbody;
            if (rb != null) rb.rotation = aim;
        }

        /// <summary>
        /// Spawn a handle puck that initializes normally, then gets frozen after a short delay.
        /// Collision ignoring and stick collision disabled immediately to prevent physics chaos.
        /// Puck is frozen after the delay.
        /// When <paramref name="asCone"/> is set the puck is dressed as a traffic cone
        /// (see ConeAsset) and announced to clients so everyone sees the same thing.
        /// </summary>
        public static System.Collections.IEnumerator SpawnHandlePuckDelayed(Vector3 position, ulong ownerSteamId, bool asCone = false)
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

            // Swap it before the initialization delay so nobody sees a puck sitting
            // there for a moment first. withCollision: this is the server, which
            // owns physics - the cone's own shape replaces the puck's disc.
            if (asCone)
            {
                MaxPracticePlugin.PropPucks[puck] = PropKind.Cone;
                ConeAsset.Apply(puck, true);
                PropNetwork.Announce();
            }

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
