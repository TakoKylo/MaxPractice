using System;
using UnityEngine;
using System.Collections.Generic;
using System.Reflection;
using Unity.Netcode;

namespace MaxPractice
{
    public class YoyoManager : MonoBehaviour
    {
        public static YoyoManager Instance { get; private set; }
        
        // Track ONE puck per player for yoyo mode (steamId -> puck)
        private Dictionary<ulong, Puck> playerYoyoPucks = new Dictionary<ulong, Puck>();
        
        // Track when puck was first touched (for delay before yank works)
        private Dictionary<ulong, float> puckTouchTime = new Dictionary<ulong, float>();
        
        // Track if puck is currently returning
        private Dictionary<Puck, bool> puckReturning = new Dictionary<Puck, bool>();
        
        // Track stick positions for yank detection
        private Dictionary<ulong, StickTracker> playerStickTrackers = new Dictionary<ulong, StickTracker>();
        
        // Pass mode settings per player
        public static Dictionary<ulong, PassSettings> PlayerPassSettings = new Dictionary<ulong, PassSettings>();
        
        // Tap pass settings per player (clientId -> settings)
        public static Dictionary<ulong, TapPassSettings> PlayerTapPassSettings = new Dictionary<ulong, TapPassSettings>();
        
        // Tap spawn enabled per player (clientId -> enabled)
        public static HashSet<ulong> TapSpawnPlayers = new HashSet<ulong>();
        
        // Tap yoyo enabled per player (clientId -> enabled) - returns puck on tap instead of yank
        public static HashSet<ulong> TapYoyoPlayers = new HashSet<ulong>();
        
        // Tap backpass enabled per player (clientId -> enabled) - spawns backpass on tap
        public static HashSet<ulong> TapBackpassPlayers = new HashSet<ulong>();
        
        // Stick tap detection state per player
        private Dictionary<ulong, StickTapTracker> playerStickTapTrackers = new Dictionary<ulong, StickTapTracker>();
        
        // Cached reflection field for collision buffer
        private static FieldInfo bufferField = null;
        
        // Config shorthand
        private static MaxPractice.ModConfig cfg => ConfigManager.Config;
        
        // Track last yank time per player
        private Dictionary<ulong, float> lastYankTime = new Dictionary<ulong, float>();
        
        public class PassSettings
        {
            public Vector3 PassFromPosition;
            public float Speed; // fast=30, normal=22, slow=14
            public bool IsLob; // high arc vs low arc
        }
        
        public class TapPassSettings
        {
            public Vector3 PassFromPosition;
            public float Speed;
            public int RequiredTaps; // 2 taps to trigger
        }
        
        /// <summary>
        /// Tracks stick taps on ice for pass triggering
        /// </summary>
        private class StickTapTracker
        {
            public float[] TapTimes = new float[5]; // Ring buffer of recent tap times
            public int TapIndex = 0;
            public bool WasGrounded; // Using Stick.IsGrounded instead of Y position
            public float LastPassTime;
            
            public void RecordTap(float time)
            {
                TapTimes[TapIndex] = time;
                TapIndex = (TapIndex + 1) % TapTimes.Length;
            }
            
            public int CountRecentTaps(float windowSeconds)
            {
                float now = Time.time;
                int count = 0;
                for (int i = 0; i < TapTimes.Length; i++)
                {
                    if (now - TapTimes[i] < windowSeconds)
                        count++;
                }
                return count;
            }
        }
        
        /// <summary>
        /// Tracks stick position/velocity for yank detection
        /// </summary>
        private class StickTracker
        {
            public Vector3 LastPosition;
            public Vector3 Velocity;
            public float LastUpdateTime;
            
            public void Update(Vector3 newPos, float deltaTime)
            {
                if (deltaTime > 0.001f)
                {
                    Velocity = (newPos - LastPosition) / deltaTime;
                }
                LastPosition = newPos;
                LastUpdateTime = Time.time;
            }
        }
        
        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
            
            if (bufferField == null)
            {
                bufferField = typeof(NetworkObjectCollisionRecorder).GetField("buffer", BindingFlags.NonPublic | BindingFlags.Instance);
            }
        }
        
        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }
        
        // Throttling for Update
        private float _nextUpdateTime = 0f;
        private const float UPDATE_INTERVAL = 0.033f; // ~30fps is enough for yank detection
        
        // Reusable list to avoid allocations
        private List<ulong> _toRemoveCache = new List<ulong>();
        
        void Update()
        {
            var gm = GameManager.Instance;
            if (gm == null || gm.Phase != GamePhase.Warmup)
            {
                // Only clear if we have data
                if (playerYoyoPucks.Count > 0 || playerStickTrackers.Count > 0 || playerStickTapTrackers.Count > 0)
                {
                    playerYoyoPucks.Clear();
                    puckTouchTime.Clear();
                    puckReturning.Clear();
                    playerStickTrackers.Clear();
                    playerStickTapTrackers.Clear();
                    lastYankTime.Clear();
                }
                return;
            }
            
            // Throttle updates to ~30fps
            if (Time.time < _nextUpdateTime)
                return;
            _nextUpdateTime = Time.time + UPDATE_INTERVAL;
            
            // Update stick tap detection for all players with stick tap enabled
            UpdateStickTapDetection();
            
            // Early exit for yoyo if no yoyo players
            if (MaxPracticePlugin.YoyoPlayers.Count == 0 && playerYoyoPucks.Count == 0)
                return;
            
            // Update stick trackers for all yoyo players
            UpdateStickTrackers();
            
            // Check for yank gestures
            UpdateYoyoDetection();
        }
        
        /// <summary>
        /// Detect stick taps on ice and trigger various actions
        /// </summary>
        private void UpdateStickTapDetection()
        {
            // Build set of all players who need tap detection
            var allTapPlayers = new HashSet<ulong>();
            foreach (var kvp in PlayerTapPassSettings) allTapPlayers.Add(kvp.Key);
            foreach (var id in TapSpawnPlayers) allTapPlayers.Add(id);
            foreach (var id in TapYoyoPlayers) allTapPlayers.Add(id);
            foreach (var id in TapBackpassPlayers) allTapPlayers.Add(id);
            
            if (allTapPlayers.Count == 0) return;
            
            foreach (var clientId in allTapPlayers)
            {
                Player player = PracticeHelpers.FindPlayerBySteamId(clientId);
                if (player == null || player.Stick == null || player.StickPositioner == null)
                {
                    continue;
                }
                
                // Get or create tracker
                if (!playerStickTapTrackers.TryGetValue(clientId, out StickTapTracker tracker))
                {
                    tracker = new StickTapTracker
                    {
                        WasGrounded = player.StickPositioner.IsGrounded,
                        LastPassTime = 0f
                    };
                    playerStickTapTrackers[clientId] = tracker;
                }
                
                // Use StickPositioner.IsGrounded - the game's built-in raycast-based ice detection
                bool isGrounded = player.StickPositioner.IsGrounded;
                
                // Detect tap: blade was NOT grounded, now IS grounded (stick just hit ice)
                if (isGrounded && !tracker.WasGrounded)
                {
                    tracker.RecordTap(Time.time);
                }
                
                tracker.WasGrounded = isGrounded;
                

                // Check for 3 taps in 1 second window
                int tapCount = tracker.CountRecentTaps(1.0f);
                if (tapCount >= 3)
                {
                    // Determine which tap mode this player has active (only one allowed)
                    bool hasTapPass = PlayerTapPassSettings.ContainsKey(clientId);
                    bool hasTapSpawn = TapSpawnPlayers.Contains(clientId);
                    bool hasTapYoyo = TapYoyoPlayers.Contains(clientId);
                    bool hasTapBackpass = TapBackpassPlayers.Contains(clientId);
                    
                    // For TapPass, TapSpawn, and TapBackpass, check cooldowns and puck touch restrictions
                    // TapYoyo does NOT need these checks - it's for returning your own puck
                    if (hasTapPass || hasTapSpawn || hasTapBackpass)
                    {
                        // Check global puck spawn cooldown
                        float currentTime = Time.realtimeSinceStartup;
                        float cooldown = PracticeConstants.PassSpawnCooldown;
                        if (MaxPracticePlugin.LastPuckSpawnTime.TryGetValue(clientId, out float lastSpawnTime))
                        {
                            if (currentTime - lastSpawnTime < cooldown)
                            {
                                // Still on cooldown - clear taps to avoid spam
                                for (int i = 0; i < tracker.TapTimes.Length; i++)
                                    tracker.TapTimes[i] = 0f;
                                continue;
                            }
                        }
                        
                        // Check if player has touched a puck within last 2.5 seconds (prevents accidental tap spawns during play)
                        if (PlayerHasTouchedPuckRecently(clientId, player, 2.5f))
                        {
                            // Player touched puck recently - clear taps and skip
                            for (int i = 0; i < tracker.TapTimes.Length; i++)
                                tracker.TapTimes[i] = 0f;
                            continue;
                        }
                    }
                    
                    // Execute the ONE tap mode this player has active
                    if (hasTapPass)
                    {
                        SpawnTapPass(clientId, PlayerTapPassSettings[clientId], player);
                    }
                    else if (hasTapSpawn)
                    {
                        SpawnTapPuck(clientId, player);
                    }
                    else if (hasTapYoyo)
                    {
                        TriggerTapYoyo(clientId, player);
                    }
                    else if (hasTapBackpass)
                    {
                        SpawnTapBackpass(clientId, player);
                    }
                    
                    // Clear tap history after action (or failed attempt)
                    for (int i = 0; i < tracker.TapTimes.Length; i++)
                        tracker.TapTimes[i] = 0f;
                }
            }
        }
        
        /// <summary>
        /// Check if a player has touched a puck within the last X seconds.
        /// Used to prevent accidental tap spawns during active play.
        /// </summary>
        private bool PlayerHasTouchedPuckRecently(ulong clientId, Player player, float windowSeconds)
        {
            if (player == null || player.Stick == null) return false;
            
            try
            {
                // B310: PuckManager is MonoBehaviourSingleton, not NetworkBehaviourSingleton
        var puckManager = MonoBehaviourSingleton<PuckManager>.Instance;
                if (puckManager == null) return false;
                
                var pucks = puckManager.GetPucks(false);
                float now = Time.time;
                
                foreach (var puck in pucks)
                {
                    if (puck == null) continue;
                    
                    var collisions = puck.GetPlayerCollisions();
                    if (collisions == null || collisions.Count == 0) continue;
                    
                    // Check if this player touched this puck recently
                    foreach (var collision in collisions)
                    {
                        if (collision.Key == player)
                        {
                            // collision.Value is the time of last touch
                            float touchTime = collision.Value;
                            if (now - touchTime < windowSeconds)
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            catch (Exception) { }
            
            return false;
        }
        
        /// <summary>
        /// Spawn a pass from tap pass settings (called from UpdateStickTapDetection)
        /// </summary>
        private static bool SpawnTapPass(ulong clientId, TapPassSettings settings, Player player)
        {
            if (!NetworkManager.Singleton.IsServer) return false;
            if (player == null || player.Stick == null) return false;
            
            Vector3 bladePos = player.Stick.BladeHandlePosition;
            float distance = Vector3.Distance(settings.PassFromPosition, bladePos);
            
            // Don't pass if too close
            if (distance < 2f) return false;
            
            Vector3 spawnPos = settings.PassFromPosition;
            spawnPos.y = 0.05f;
            
            // Use ballistic arc so pass has proper air/lift
            Vector3 velocity;
            if (Instance != null)
            {
                Vector3? ballisticVel = Instance.CalculateBallisticVelocityPublic(spawnPos, bladePos, settings.Speed, false);
                velocity = ballisticVel ?? (bladePos - spawnPos).normalized * settings.Speed;
            }
            else
            {
                velocity = (bladePos - spawnPos).normalized * settings.Speed;
            }
            
            var puck = PracticeHelpers.SpawnPuckWithCleanup(spawnPos, Quaternion.identity, velocity, false);
            if (puck != null)
            {
                RegisterPuckForPlayer(puck, player);
                MaxPracticePlugin.LastPuckSpawnTime[clientId] = Time.realtimeSinceStartup;
                return true;
            }
            return false;
        }
        
        /// <summary>
        /// Spawn a puck above stick (like /spawnpuck but triggered by tap)
        /// </summary>
        private static bool SpawnTapPuck(ulong clientId, Player player)
        {
            if (!NetworkManager.Singleton.IsServer) return false;
            if (player == null || player.Stick == null) return false;
            
            Vector3 bladePos = player.Stick.BladeHandlePosition;
            Vector3 spawnPos = bladePos + Vector3.up * 0.5f;
            
            // Get player velocity for momentum carry (like /spawnpuck)
            Vector3 playerVelocity = Vector3.zero;
            if (player.PlayerBody != null)
            {
                var rb = player.PlayerBody.GetComponent<Rigidbody>();
                if (rb != null) playerVelocity = rb.linearVelocity;
            }
            
            var puck = PracticeHelpers.SpawnPuckWithCleanup(spawnPos, Quaternion.identity, playerVelocity, false);
            if (puck != null)
            {
                RegisterPuckForPlayer(puck, player);
                MaxPracticePlugin.LastPuckSpawnTime[clientId] = Time.realtimeSinceStartup;
                return true;
            }
            return false;
        }
        
        /// <summary>
        /// Trigger yoyo return via stick tap (returns tracked puck to player)
        /// </summary>
        private bool TriggerTapYoyo(ulong clientId, Player player)
        {
            if (!NetworkManager.Singleton.IsServer) return false;
            if (player == null || player.PlayerBody == null) return false;
            
            // Check if player has a tracked yoyo puck
            if (!playerYoyoPucks.TryGetValue(clientId, out Puck puck))
                return false;
            
            if (puck == null || puck.Rigidbody == null)
            {
                playerYoyoPucks.Remove(clientId);
                return false;
            }
            
            // Check if already returning
            if (puckReturning.TryGetValue(puck, out bool isReturning) && isReturning)
                return false;
            
            // Check minimum distance
            Vector3 puckPos = puck.transform.position;
            Vector3 playerPos = player.PlayerBody.transform.position;
            float dist = Vector3.Distance(puckPos, playerPos);
            
            if (dist < PracticeConstants.YoyoMinDistanceFromStick)
                return false;
            
            // Trigger return!
            puckReturning[puck] = true;
            float returnSpeed = Mathf.Clamp(18f + dist, 18f, 35f);
            LaunchPuckToPlayer(puck, player, clientId, returnSpeed);
            
            return true;
        }
        
        /// <summary>
        /// Spawn a backpass via stick tap (like /backpass but triggered by tap)
        /// </summary>
        private bool SpawnTapBackpass(ulong clientId, Player player)
        {
            if (!NetworkManager.Singleton.IsServer) return false;
            if (player == null || player.Stick == null || player.PlayerBody == null) return false;
            
            var playerBody = player.PlayerBody;
            Vector3 stickPos = player.Stick.BladeHandlePosition;
            
            // Spawn puck behind player using config distance
            Vector3 behindPlayer = playerBody.transform.position - playerBody.transform.forward * PracticeConstants.BackpassDistance;
            behindPlayer.y = 0.05f; // Just above ice
            
            // Clamp spawn position to stay within actual ice bounds
            /* B310: LevelManager was restructured - bounds clamping disabled
            var levelMgr = NetworkBehaviourSingleton<LevelManager>.Instance;
            if (levelMgr != null)
            {
                Bounds iceBounds = levelMgr.IceBounds;
                behindPlayer.x = Mathf.Clamp(behindPlayer.x, iceBounds.min.x + 0.5f, iceBounds.max.x - 0.5f);
                behindPlayer.z = Mathf.Clamp(behindPlayer.z, iceBounds.min.z + 0.5f, iceBounds.max.z - 0.5f);
            }
            */
            
            // Calculate ballistic velocity to lob puck toward the player's stick
            Vector3 passVelocity;
            if (Instance != null)
            {
                Vector3? ballisticVel = Instance.CalculateBallisticVelocityPublic(behindPlayer, stickPos, PracticeConstants.BackpassSpeed, false);
                passVelocity = ballisticVel ?? (stickPos - behindPlayer).normalized * PracticeConstants.BackpassSpeed;
            }
            else
            {
                passVelocity = (stickPos - behindPlayer).normalized * PracticeConstants.BackpassSpeed;
            }
            
            Puck spawnedPuck = PracticeHelpers.SpawnPuckWithCleanup(behindPlayer, Quaternion.identity, passVelocity, false);
            
            // Register puck with player's stick collision buffer so they can track it
            if (spawnedPuck != null)
            {
                RegisterPuckForPlayer(spawnedPuck, player);
                MaxPracticePlugin.LastPuckSpawnTime[clientId] = Time.realtimeSinceStartup;
                return true;
            }
            return false;
        }
        
        /// <summary>
        /// Update stick position tracking for all yoyo players
        /// </summary>
        private void UpdateStickTrackers()
        {
            foreach (var steamId in MaxPracticePlugin.YoyoPlayers)
            {
                Player player = PracticeHelpers.FindPlayerBySteamId(steamId);
                if (player == null || player.Stick == null) continue;
                
                Vector3 stickPos = GetBladePosition(player);
                
                if (!playerStickTrackers.TryGetValue(steamId, out StickTracker tracker))
                {
                    tracker = new StickTracker
                    {
                        LastPosition = stickPos,
                        Velocity = Vector3.zero,
                        LastUpdateTime = Time.time
                    };
                    playerStickTrackers[steamId] = tracker;
                }
                else
                {
                    float dt = Time.time - tracker.LastUpdateTime;
                    tracker.Update(stickPos, dt);
                }
            }
        }
        
        private void UpdateYoyoDetection()
        {
            _toRemoveCache.Clear(); // Reuse list instead of allocating new one
            
            foreach (var kvp in playerYoyoPucks)
            {
                ulong steamId = kvp.Key;
                Puck puck = kvp.Value;
                
                // Skip yank detection for players using tapyoyo (they use taps instead)
                if (TapYoyoPlayers.Contains(steamId))
                    continue;
                
                // Check if puck still exists
                if (puck == null || puck.Rigidbody == null)
                {
                    _toRemoveCache.Add(steamId);
                    continue;
                }
                
                // Find player
                Player player = PracticeHelpers.FindPlayerBySteamId(steamId);
                if (player == null || player.PlayerBody == null)
                {
                    _toRemoveCache.Add(steamId);
                    continue;
                }
                
                // Check if puck is currently returning (skip yank detection while returning)
                if (puckReturning.TryGetValue(puck, out bool isReturning) && isReturning)
                {
                    // Check if puck reached player (stop returning state)
                    float distToPlayer = Vector3.Distance(puck.transform.position, player.PlayerBody.transform.position);
                    if (distToPlayer < 2f)
                    {
                        puckReturning[puck] = false;
                    }
                    continue;
                }
                
                // Check yank cooldown
                if (lastYankTime.TryGetValue(steamId, out float lastYank))
                {
                    if (Time.time - lastYank < PracticeConstants.YoyoYankCooldown)
                        continue;
                }
                
                // Get stick tracker
                if (!playerStickTrackers.TryGetValue(steamId, out StickTracker tracker))
                    continue;
                
                Vector3 puckPos = puck.transform.position;
                Vector3 playerPos = player.PlayerBody.transform.position;
                Vector3 stickPos = tracker.LastPosition;
                
                // Check delay after puck was touched (prevents follow-through)
                if (puckTouchTime.TryGetValue(steamId, out float touchTime))
                {
                    if (Time.time - touchTime < PracticeConstants.YoyoDelayAfterShot)
                        continue; // Not enough time since shot
                }
                
                // Check minimum distance from STICK BLADE
                float distFromStick = Vector3.Distance(puckPos, stickPos);
                if (distFromStick < PracticeConstants.YoyoMinDistanceFromStick)
                    continue; // Puck is too close to stick
                
                // Check stick velocity
                Vector3 stickVel = tracker.Velocity;
                float stickSpeed = stickVel.magnitude;
                
                if (stickSpeed < PracticeConstants.YoyoYankSpeedThreshold)
                    continue; // Stick not moving fast enough
                
                // Key check: stick must be moving TOWARD the player (pulling back), not away
                // This is what defines a "yank" - pulling the stick back toward your body
                Vector3 stickToPlayer = (playerPos - stickPos).normalized;
                float stickMovingTowardPlayer = Vector3.Dot(stickVel.normalized, stickToPlayer);
                
                if (stickMovingTowardPlayer > 0.3f)
                {
                    // YANK DETECTED! Stick is pulling back toward player
                    lastYankTime[steamId] = Time.time;
                    puckReturning[puck] = true;
                    
                    // Calculate return speed
                    float distFromPlayer = Vector3.Distance(puckPos, playerPos);
                    float returnSpeed = Mathf.Clamp(18f + distFromPlayer, 18f, 35f);
                    
                    LaunchPuckToPlayer(puck, player, steamId, returnSpeed);
                }
            }
            
            foreach (var steamId in _toRemoveCache)
            {
                if (playerYoyoPucks.TryGetValue(steamId, out Puck oldPuck))
                {
                    puckReturning.Remove(oldPuck);
                }
                playerYoyoPucks.Remove(steamId);
            }
        }
        
        /// <summary>
        /// Launch puck toward player's predicted stick position with ballistic arc
        /// </summary>
        private void LaunchPuckToPlayer(Puck puck, Player player, ulong steamId, float speed = 25f, bool highArc = false)
        {
            if (puck?.Rigidbody == null || player?.PlayerBody == null) return;
            
            var rb = puck.Rigidbody;
            Vector3 startPos = puck.transform.position;
            
            // Predict target position based on player BODY movement
            Vector3 targetPos = PredictStickPosition(player);
            
            // Calculate ballistic launch velocity
            Vector3? launchVel = CalculateBallisticVelocity(startPos, targetPos, speed, highArc);
            
            if (launchVel.HasValue)
            {
                rb.linearVelocity = launchVel.Value;
                rb.useGravity = true;
            }
            else
            {
                // Fallback: straight shot if ballistic solution not found
                Vector3 direction = (targetPos - startPos).normalized;
                rb.linearVelocity = direction * speed;
                rb.useGravity = true;
            }
        }
        
        /// <summary>
        /// Predict where the stick will be based on player body velocity
        /// </summary>
        private Vector3 PredictStickPosition(Player player)
        {
            Vector3 bladePos = GetBladePosition(player);
            
            if (player.PlayerBody == null) return bladePos;
            
            var bodyRb = player.PlayerBody.GetComponent<Rigidbody>();
            if (bodyRb == null) return bladePos;
            
            // Get player body velocity
            Vector3 playerVelocity = bodyRb.linearVelocity;
            
            // Estimate time for puck to arrive (rough estimate based on distance)
            float distance = 10f; // Assume average distance
            float estimatedTime = distance / 25f; // Assume ~25 speed
            
            // Predict future position
            Vector3 predictedPos = bladePos + playerVelocity * estimatedTime * 0.7f; // 0.7 factor for some damping
            
            // Keep Y at blade height (don't predict vertical movement much)
            predictedPos.y = bladePos.y;
            
            return predictedPos;
        }
        
        /// <summary>
        /// Public wrapper for ballistic velocity calculation.
        /// </summary>
        public Vector3? CalculateBallisticVelocityPublic(Vector3 start, Vector3 target, float speed, bool highArc)
        {
            return CalculateBallisticVelocity(start, target, speed, highArc);
        }
        
        /// <summary>
        /// Calculate the initial velocity needed to hit target with gravity (ballistic trajectory)
        /// </summary>
        private Vector3? CalculateBallisticVelocity(Vector3 start, Vector3 target, float speed, bool highArc)
        {
            float gravity = Mathf.Abs(Physics.gravity.y);
            
            // Horizontal displacement
            Vector3 horizontalDisp = new Vector3(target.x - start.x, 0, target.z - start.z);
            float horizontalDist = horizontalDisp.magnitude;
            
            // Vertical displacement
            float verticalDist = target.y - start.y;
            
            // If target is very close, just shoot straight
            if (horizontalDist < 1f)
            {
                return (target - start).normalized * speed;
            }
            
            // Solve for launch angle using projectile motion equations
            // Using the formula: tan(θ) = (v² ± sqrt(v⁴ - g(g*x² + 2*y*v²))) / (g*x)
            float v2 = speed * speed;
            float v4 = v2 * v2;
            float gx = gravity * horizontalDist;
            float gx2 = gravity * horizontalDist * horizontalDist;
            
            float discriminant = v4 - gravity * (gx2 + 2f * verticalDist * v2);
            
            if (discriminant < 0)
            {
                // No solution at this speed - target too far
                // Try with higher speed
                return CalculateBallisticVelocity(start, target, speed * 1.3f, highArc);
            }
            
            float sqrtDisc = Mathf.Sqrt(discriminant);
            
            // Two solutions: high arc (+) and low arc (-)
            float tanTheta;
            if (highArc)
            {
                tanTheta = (v2 + sqrtDisc) / gx;
            }
            else
            {
                tanTheta = (v2 - sqrtDisc) / gx;
            }
            
            float theta = Mathf.Atan(tanTheta);
            
            // Clamp angle to reasonable values
            if (theta < -Mathf.PI / 4f) theta = -Mathf.PI / 4f;
            if (theta > Mathf.PI / 2.5f) theta = Mathf.PI / 2.5f;
            
            // Calculate velocity components
            float horizontalSpeed = speed * Mathf.Cos(theta);
            float verticalSpeed = speed * Mathf.Sin(theta);
            
            // Horizontal direction
            Vector3 horizontalDir = horizontalDisp.normalized;
            
            // Final velocity
            Vector3 velocity = horizontalDir * horizontalSpeed + Vector3.up * verticalSpeed;
            
            return velocity;
        }
        
        private Vector3 GetBladePosition(Player player)
        {
            if (player?.Stick != null)
            {
                return player.Stick.BladeHandlePosition;
            }
            Vector3 pos = player.transform.position;
            pos.y = 0.08f;
            return pos;
        }
        
        // Called when a yoyo player touches/shoots a puck - this becomes their tracked yoyo puck
        public void OnPuckFired(Puck puck, ulong steamId)
        {
            if (puck == null || puck.Rigidbody == null) return;
            if (!MaxPracticePlugin.YoyoPlayers.Contains(steamId)) return;
            
            var gm = GameManager.Instance;
            if (gm == null || gm.Phase != GamePhase.Warmup) return;
            
            // Check if this is already our tracked puck
            if (playerYoyoPucks.TryGetValue(steamId, out Puck currentPuck) && currentPuck == puck)
            {
                // Already tracking this puck - only update touch time if puck was NOT returning
                // This prevents resetting the delay timer when catching a returning puck
                if (!puckReturning.TryGetValue(puck, out bool isReturning) || !isReturning)
                {
                    puckTouchTime[steamId] = Time.time;
                }
                // Reset returning state since player touched it
                puckReturning[puck] = false;
                return;
            }
            
            // Remove old puck tracking
            if (currentPuck != null)
            {
                puckReturning.Remove(currentPuck);
            }
            
            // Track this new puck with current time
            playerYoyoPucks[steamId] = puck;
            puckTouchTime[steamId] = Time.time;
            puckReturning[puck] = false;
        }
        
        /// <summary>
        /// Called when a player OTHER than the yoyo owner touches a puck.
        /// If this puck was someone's yoyo puck, detach it from them.
        /// If the toucher has yoyo enabled, the puck becomes theirs.
        /// </summary>
        public void OnPuckTouchedByOther(Puck puck, ulong toucherSteamId)
        {
            if (puck == null) return;
            
            // Find who this puck belongs to
            ulong ownerSteamId = 0;
            foreach (var kvp in playerYoyoPucks)
            {
                if (kvp.Value == puck)
                {
                    ownerSteamId = kvp.Key;
                    break;
                }
            }
            
            // Don't do anything if the owner touched their own puck
            if (ownerSteamId == toucherSteamId) return;
            
            // If puck is currently returning to its owner, don't let it be stolen
            // This prevents accidental interceptions while puck is coming back
            if (ownerSteamId != 0 && puckReturning.TryGetValue(puck, out bool isReturning) && isReturning)
            {
                // Allow steal only if toucher also has yoyo enabled (intentional interception)
                if (!MaxPracticePlugin.YoyoPlayers.Contains(toucherSteamId))
                    return; // Don't detach - puck is returning to owner
            }
            
            // If the toucher has yoyo enabled, give them this puck
            if (MaxPracticePlugin.YoyoPlayers.Contains(toucherSteamId))
            {
                // Detach from old owner first (if any)
                if (ownerSteamId != 0)
                {
                    playerYoyoPucks.Remove(ownerSteamId);
                    puckTouchTime.Remove(ownerSteamId);
                }
                puckReturning.Remove(puck);
                
                // Give puck to new owner
                OnPuckFired(puck, toucherSteamId);
                return;
            }
            
            // If no one owns this puck, nothing more to do
            if (ownerSteamId == 0) return;
            
            // Toucher doesn't have yoyo - detach puck from its owner
            playerYoyoPucks.Remove(ownerSteamId);
            puckTouchTime.Remove(ownerSteamId);
            puckReturning.Remove(puck);
        
        }
        
        // Spawn a pass from saved position to player's current stick
        // Returns true if pass was spawned, false if on cooldown or failed
        public static bool SpawnPass(ulong steamId)
        {
            if (!PlayerPassSettings.TryGetValue(steamId, out PassSettings settings))
                return false;
            
            // Check global puck spawn cooldown
            float currentTime = Time.realtimeSinceStartup;
            if (MaxPracticePlugin.LastPuckSpawnTime.TryGetValue(steamId, out float lastTime))
            {
                if (currentTime - lastTime < PracticeConstants.PassSpawnCooldown)
                    return false; // Still on cooldown
            }
            
            Player player = PracticeHelpers.FindPlayerBySteamId(steamId);
            if (player == null) return false;
            
            // Update global cooldown
            MaxPracticePlugin.LastPuckSpawnTime[steamId] = currentTime;
            
            Vector3 startPos = settings.PassFromPosition;
            Vector3 targetPos = Instance.PredictStickPosition(player);
            
            var pm = MonoBehaviourSingleton<PuckManager>.Instance;
            if (pm == null) return false;
            
            // Calculate ballistic velocity
            Vector3? launchVel = Instance.CalculateBallisticVelocity(startPos, targetPos, settings.Speed, settings.IsLob);
            
            Vector3 initialVelocity;
            if (launchVel.HasValue)
            {
                initialVelocity = launchVel.Value;
            }
            else
            {
                // Fallback
                initialVelocity = (targetPos - startPos).normalized * settings.Speed;
            }
            
            Puck newPuck = PracticeHelpers.SpawnPuckWithCleanup(startPos, Quaternion.identity, initialVelocity, false);
            
            if (newPuck != null)
            {
                // Gravity is ON by default - let physics handle it
                RegisterPuckForPlayer(newPuck, player);
                return true;
            }
            return false;
        }
        
        // Register puck with the player's tracking systems so spawned pucks behave
        // like the player just touched them.
        public static void RegisterPuckForPlayer(Puck puck, Player player)
        {
            if (puck == null || player == null) return;

            ulong steamId = PracticeHelpers.GetSteamIdFromPlayer(player);
            ulong ownerId = player.NetworkObject != null ? player.NetworkObject.OwnerClientId : 0;
            if (steamId == 0) steamId = ownerId;

            TrackLastTouchedPuckForPlayer(puck, player);

            if (Instance != null && MaxPracticePlugin.YoyoPlayers.Contains(steamId))
                Instance.OnPuckFired(puck, steamId);

            if (player.Stick == null) return;
            
            var collisionBuffer = player.Stick.NetworkObjectCollisionRecorder;
            if (collisionBuffer == null) return;
            
            try
            {
                var buffer = GetCollisionRecorderBuffer(collisionBuffer);
                if (buffer == null) return;
                
                NetworkObjectReference puckRef = new NetworkObjectReference(puck.NetworkObject);
                
                // Remove existing if present
                NetworkObjectCollision existing = default;
                bool found = false;
                foreach (var c in buffer)
                {
                    if (c.NetworkObjectReference.Equals(puckRef))
                    {
                        existing = c;
                        found = true;
                        break;
                    }
                }
                if (found) buffer.Remove(existing);
                if (buffer.Count >= 10) buffer.RemoveAt(0);
                
                buffer.Add(new NetworkObjectCollision
                {
                    NetworkObjectReference = puckRef,
                    Time = Time.time
                });
            }
            catch { }
        }

        private static NetworkList<NetworkObjectCollision> GetCollisionRecorderBuffer(NetworkObjectCollisionRecorder recorder)
        {
            if (recorder == null) return null;

            try
            {
                var bufferProp = typeof(NetworkObjectCollisionRecorder).GetProperty("Buffer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (bufferProp != null)
                {
                    var propValue = bufferProp.GetValue(recorder) as NetworkList<NetworkObjectCollision>;
                    if (propValue != null)
                        return propValue;
                }
            }
            catch { }

            try
            {
                if (bufferField == null)
                {
                    bufferField = typeof(NetworkObjectCollisionRecorder).GetField("buffer", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?? typeof(NetworkObjectCollisionRecorder).GetField("Buffer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }

                if (bufferField == null) return null;
                return bufferField.GetValue(recorder) as NetworkList<NetworkObjectCollision>;
            }
            catch { return null; }
        }
        
        public void CleanupAll()
        {
            playerYoyoPucks.Clear();
            puckTouchTime.Clear();
            puckReturning.Clear();
            playerStickTrackers.Clear();
            lastYankTime.Clear();
            LastTouchedPuck.Clear();
        }
        
        // Track last touched puck per player (for /pop command) - works for ALL players, not just yoyo
        public static Dictionary<ulong, Puck> LastTouchedPuck = new Dictionary<ulong, Puck>();

        public static void TrackLastTouchedPuckForPlayer(Puck puck, Player player)
        {
            if (puck == null || player == null) return;

            ulong steamId = PracticeHelpers.GetSteamIdFromPlayer(player);
            ulong ownerId = player.NetworkObject != null ? player.NetworkObject.OwnerClientId : 0;

            TrackLastTouchedPuck(puck, steamId);
            if (ownerId != steamId)
                TrackLastTouchedPuck(puck, ownerId);
        }
        
        /// <summary>
        /// Called from collision patch to track the last puck a player touched.
        /// </summary>
        public static void TrackLastTouchedPuck(Puck puck, ulong steamId)
        {
            if (puck == null) return;
            LastTouchedPuck[steamId] = puck;
        }
        
        /// <summary>
        /// Pop the last touched puck upward. Returns true if successful.
        /// </summary>
        public static bool PopLastTouchedPuck(ulong steamId, float popForce = 8f)
        {
            if (!LastTouchedPuck.TryGetValue(steamId, out Puck puck))
            {
                var p = PracticeHelpers.FindPlayerBySteamId(steamId);
                if (p != null)
                {
                    ulong altOwner = p.NetworkObject != null ? p.NetworkObject.OwnerClientId : 0;
                    ulong altSteam = PracticeHelpers.GetSteamIdFromPlayer(p);
                    if (!LastTouchedPuck.TryGetValue(altOwner, out puck) &&
                        !LastTouchedPuck.TryGetValue(altSteam, out puck))
                        return false;
                }
                else
                {
                    return false;
                }
            }
            
            if (puck == null || puck.Rigidbody == null)
            {
                LastTouchedPuck.Remove(steamId);
                return false;
            }
            
            // Pop the puck upward - add vertical velocity while preserving some horizontal momentum
            var rb = puck.Rigidbody;
            Vector3 vel = rb.linearVelocity;
            vel.y = popForce;
            rb.linearVelocity = vel;
            rb.useGravity = true;
            
            return true;
        }

        public static bool PopLastTouchedPuckForPlayer(Player player, float popForce = 8f)
        {
            if (player == null) return false;

            ulong steamId = PracticeHelpers.GetSteamIdFromPlayer(player);
            ulong ownerId = player.NetworkObject != null ? player.NetworkObject.OwnerClientId : 0;

            // Try both identity keys directly first (works for host and dedicated).
            if (PopLastTouchedPuck(steamId, popForce)) return true;
            if (ownerId != steamId && PopLastTouchedPuck(ownerId, popForce)) return true;

            return false;
        }
    }
}
