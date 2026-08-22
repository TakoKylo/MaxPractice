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

        // Players whose YoyoPlayers entry was added BY /tapyoyo rather than by /yoyo, so
        // leaving tap yoyo can undo exactly what it turned on and no more.
        private static readonly HashSet<ulong> _implicitYoyo = new HashSet<ulong>();

        /// <summary>Turn on plain yoyo tracking on tap yoyo's behalf.</summary>
        public static void AcquireImplicitYoyo(ulong steamId)
        {
            if (MaxPracticePlugin.YoyoPlayers.Add(steamId))
                _implicitYoyo.Add(steamId);
        }

        /// <summary>
        /// Undo <see cref="AcquireImplicitYoyo"/>. A no-op when the player had /yoyo on in
        /// their own right - tearing that down was never tap yoyo's to do.
        /// </summary>
        public static void ReleaseImplicitYoyo(ulong steamId)
        {
            if (_implicitYoyo.Remove(steamId))
                MaxPracticePlugin.YoyoPlayers.Remove(steamId);
        }

        /// <summary>
        /// Turn plain yoyo on or off because the player asked for it directly, and take the
        /// implicit claim off it either way.
        ///
        /// /yoyo used to add to and remove from YoyoPlayers itself, which left the ownership
        /// record lying: turn on /tapyoyo (which acquires an implicit entry), then toggle /yoyo
        /// twice, and the player now holds yoyo in their own right while _implicitYoyo still
        /// says tap yoyo put it there. The next /tapyoyo off then silently took away the yoyo
        /// they had explicitly asked for. An explicit toggle is the player speaking for
        /// themselves, so it always ends the implicit claim.
        /// </summary>
        /// <returns>true if yoyo is now on for this player.</returns>
        public static bool SetExplicitYoyo(ulong steamId, bool on)
        {
            _implicitYoyo.Remove(steamId);

            if (on) MaxPracticePlugin.YoyoPlayers.Add(steamId);
            else MaxPracticePlugin.YoyoPlayers.Remove(steamId);

            return on;
        }

        /// <summary>
        /// Drop every per-player yoyo, pass and tap setting.
        ///
        /// All of these are warmup-only tools, and several of them point at something the
        /// warmup exit destroys - PlayerPassSettings and PlayerTapPassSettings reference a
        /// shooter prop that CleanupWarmupFeatures has already torn down, so keeping them
        /// carried a dangling intent into the next warmup while the prop it named was gone.
        /// Clearing them makes the mod's per-player state match its per-player objects.
        /// </summary>
        public static void ResetAllPlayerState()
        {
            MaxPracticePlugin.YoyoPlayers.Clear();
            _implicitYoyo.Clear();

            PlayerPassSettings.Clear();
            PlayerTapPassSettings.Clear();
            TapSpawnPlayers.Clear();
            TapYoyoPlayers.Clear();
            TapBackpassPlayers.Clear();
            LastTouchedPuck.Clear();
        }
        
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
        /// Tracks stick position/velocity for yank detection. Velocity is measured in
        /// PlayerBody-local space so the player's own skating/spinning velocity drops
        /// out of the yank signal — only the stick's motion relative to the body
        /// counts. World-space LastPosition is kept for distance-to-puck checks.
        /// </summary>
        private class StickTracker
        {
            public Vector3 LastPosition;       // world-space blade position (for distance checks)
            public Vector3 LastLocalPosition;  // PlayerBody-local blade position (for velocity)
            public Vector3 LocalVelocity;      // body-relative blade velocity
            public float LastUpdateTime;

            public void Update(Vector3 worldPos, Vector3 localPos, float deltaTime)
            {
                if (deltaTime > 0.001f)
                {
                    LocalVelocity = (localPos - LastLocalPosition) / deltaTime;
                }
                LastPosition = worldPos;
                LastLocalPosition = localPos;
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
        
        // Throttling for Update. 60Hz: the yank gesture is short (~80–150ms);
        // 30Hz risked missing the velocity peak between samples.
        private float _nextUpdateTime = 0f;
        private const float UPDATE_INTERVAL = 0.0166f;

        // Reusable lists to avoid per-frame allocations.
        private List<ulong> _toRemoveCache = new List<ulong>();
        private List<ulong> _ownerCache = new List<ulong>();
        private List<ulong> _detachCache = new List<ulong>();
        private readonly HashSet<ulong> _tapPlayerCache = new HashSet<ulong>();
        
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
            // Reused, not allocated. This runs at 60 Hz for the whole of Warmup - above
            // the "no yoyo players" early-out - and the old `new HashSet<ulong>()` was built
            // BEFORE the "nothing to do" bail below, so it produced 60 throwaway sets a
            // second even on a server where no tap command was in use. The file already uses
            // reusable caches for exactly this (_toRemoveCache and friends).
            var allTapPlayers = _tapPlayerCache;
            allTapPlayers.Clear();
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
        /// Fire a tap-triggered pass.
        ///
        /// Hands straight over to SpawnPass, the same routine /pass uses, rather than
        /// keeping a second implementation. That one swings the shooter round to face
        /// where the pass is going and fires from its muzzle; this one used to spawn the
        /// puck at a bare position instead, so a tap pass came out of thin air slightly
        /// beside the machine that was supposedly throwing it - and drifted apart from
        /// /pass every time either was touched.
        /// </summary>
        private static bool SpawnTapPass(ulong clientId, TapPassSettings settings, Player player)
        {
            if (!NetworkManager.Singleton.IsServer) return false;
            if (player == null || player.Stick == null) return false;

            // Still worth refusing a pass from under the player's own feet.
            float distance = Vector3.Distance(settings.PassFromPosition, player.Stick.BladeHandlePosition);
            if (distance < 2f) return false;

            return SpawnPass(clientId);
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
                // Drop the returning flag with it. The key is the Puck reference, so an
                // entry left behind here is never reachable again and just grows the
                // dictionary for the life of the process.
                if (!ReferenceEquals(puck, null)) puckReturning.Remove(puck);
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
            behindPlayer.y = RinkSheets.IceHeightAt(behindPlayer, 0.05f); // Just above ice
            
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
                if (player == null || player.Stick == null || player.PlayerBody == null) continue;

                Vector3 stickWorldPos = GetBladePosition(player);
                Vector3 stickLocalPos = player.PlayerBody.transform.InverseTransformPoint(stickWorldPos);

                if (!playerStickTrackers.TryGetValue(steamId, out StickTracker tracker))
                {
                    tracker = new StickTracker
                    {
                        LastPosition = stickWorldPos,
                        LastLocalPosition = stickLocalPos,
                        LocalVelocity = Vector3.zero,
                        LastUpdateTime = Time.time
                    };
                    playerStickTrackers[steamId] = tracker;
                }
                else
                {
                    float dt = Time.time - tracker.LastUpdateTime;
                    tracker.Update(stickWorldPos, stickLocalPos, dt);
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
                
                bool usesTapYoyo = TapYoyoPlayers.Contains(steamId);

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
                
                // Check if puck is currently returning (skip yank detection while returning).
                //
                // This has to run for tap-yoyo players too, which is why the tap-yoyo skip
                // below sits AFTER it rather than at the top of the loop. TriggerTapYoyo
                // latches the same puckReturning flag on, and this block is the only thing
                // that ever clears it - so skipping tap players here left the flag stuck on
                // after the first return that did not land, and TriggerTapYoyo's own
                // "already returning" guard then refused every future tap for that puck.
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

                // Tap-yoyo players return the puck by tapping the ice, not by yanking the
                // stick, so everything past here is not theirs.
                if (usesTapYoyo)
                    continue;

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

                // Body-relative stick motion. Working in PlayerBody-local space removes the
                // player's own skating/spinning velocity from the signal: a real yank moves
                // the stick toward the body origin regardless of how the body is translating.
                Vector3 localVel = tracker.LocalVelocity;
                float localSpeed = localVel.magnitude;
                if (localSpeed < PracticeConstants.YoyoYankSpeedThreshold)
                    continue;

                // "Toward player" in local space = toward the body origin.
                Vector3 stickToBodyLocal = (-tracker.LastLocalPosition).normalized;
                float towardPlayer = Vector3.Dot(localVel.normalized, stickToBodyLocal);

                if (towardPlayer > 0.3f)
                {
                    // YANK DETECTED — stick pulled back toward body.
                    lastYankTime[steamId] = Time.time;
                    puckReturning[puck] = true;

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

            // Predict target position based on player BODY movement, scaled by the
            // actual flight time (puck distance ÷ launch speed) rather than a fixed
            // 10m assumption.
            float flightDistance = Vector3.Distance(startPos, player.PlayerBody.transform.position);
            Vector3 targetPos = PredictStickPosition(player, flightDistance, speed);

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
        /// Predict where the stick will be when the puck arrives. Lead time is
        /// (distance ÷ speed) so the prediction scales with how far the puck
        /// has to travel — fixes a stale 10m / 25 m·s⁻¹ hardcode that produced
        /// off-target leads on short or long shots.
        /// </summary>
        private Vector3 PredictStickPosition(Player player, float distance = 10f, float speed = 25f)
        {
            Vector3 bladePos = GetBladePosition(player);

            if (player.PlayerBody == null) return bladePos;

            var bodyRb = player.PlayerBody.GetComponent<Rigidbody>();
            if (bodyRb == null) return bladePos;

            Vector3 playerVelocity = bodyRb.linearVelocity;
            float estimatedTime = speed > 0.1f ? distance / speed : 0.4f;

            // 0.7 damping keeps the prediction conservative — overshooting feels worse
            // than slightly trailing, since trailing pucks still meet the stick.
            Vector3 predictedPos = bladePos + playerVelocity * estimatedTime * 0.7f;
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
            pos.y = RinkSheets.IceHeightAt(pos, 0.08f);
            return pos;
        }
        
        // Called when a yoyo player touches/shoots a puck - this becomes their tracked yoyo puck
        public void OnPuckFired(Puck puck, ulong steamId)
        {
            if (puck == null || puck.Rigidbody == null) return;
            if (!MaxPracticePlugin.YoyoPlayers.Contains(steamId)) return;

            var gm = GameManager.Instance;
            if (gm == null || gm.Phase != GamePhase.Warmup) return;

            // Already tracking this exact puck for this player — just refresh touch time
            // (skip the refresh if the puck is mid-return, so the post-shot delay timer
            // doesn't reset when the player catches their own incoming puck).
            if (playerYoyoPucks.TryGetValue(steamId, out Puck currentPuck) && currentPuck == puck)
            {
                if (!puckReturning.TryGetValue(puck, out bool isReturning) || !isReturning)
                    puckTouchTime[steamId] = Time.time;
                puckReturning[puck] = false;
                return;
            }

            // This player previously tracked a different puck — release it.
            if (currentPuck != null)
                puckReturning.Remove(currentPuck);

            // Strip this puck from every other player tracking it. Without this, two
            // yoyo players touching the same puck both retain ownership and both
            // yank-detection loops fire on the next gesture.
            _detachCache.Clear();
            foreach (var kvp in playerYoyoPucks)
            {
                if (kvp.Key != steamId && kvp.Value == puck)
                    _detachCache.Add(kvp.Key);
            }
            foreach (var otherSteamId in _detachCache)
            {
                playerYoyoPucks.Remove(otherSteamId);
                puckTouchTime.Remove(otherSteamId);
            }
            _detachCache.Clear();

            playerYoyoPucks[steamId] = puck;
            puckTouchTime[steamId] = Time.time;
            puckReturning[puck] = false;
        }
        
        /// <summary>
        /// Called when a NON-yoyo player touches a puck. The collision patch already
        /// routes yoyo touchers to OnPuckFired, so the call-site invariant here is
        /// `toucherSteamId is not in YoyoPlayers`. We just need to detach the puck
        /// from every current owner (defensive sweep — there should be at most one,
        /// but historical multi-owner state would still get cleaned up).
        /// </summary>
        public void OnPuckTouchedByOther(Puck puck, ulong toucherSteamId)
        {
            if (puck == null) return;

            _ownerCache.Clear();
            foreach (var kvp in playerYoyoPucks)
            {
                if (kvp.Value == puck) _ownerCache.Add(kvp.Key);
            }

            // No one tracks this puck — nothing to detach.
            if (_ownerCache.Count == 0) return;

            // If puck is mid-return to its owner, don't let it be intercepted.
            if (puckReturning.TryGetValue(puck, out bool isReturning) && isReturning)
            {
                _ownerCache.Clear();
                return;
            }

            foreach (var ownerId in _ownerCache)
            {
                playerYoyoPucks.Remove(ownerId);
                puckTouchTime.Remove(ownerId);
            }
            _ownerCache.Clear();
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

            // Swing the shooter round so it's pointing where this pass is going,
            // then fire from its muzzle. Aiming first matters: the muzzle sits on
            // the shooter's pivot, but reading it before the turn would still be a
            // frame behind on the y/z the barrel ends up at.
            PracticeHelpers.AimShooter(steamId, targetPos);
            if (PracticeHelpers.TryGetMuzzle(steamId, out Vector3 muzzle))
                startPos = muzzle;
            
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
        
        /// <summary>
        /// Snapshot of who currently has a puck on their string, for YoyoStringNetwork.
        ///
        /// A snapshot the network layer polls, rather than an event every writer has to
        /// remember to raise: playerYoyoPucks is written from touches, yanks, detaches,
        /// disconnects and phase cleanup, and a poll cannot miss one of them.
        /// </summary>
        public static void CollectActivePairs(List<KeyValuePair<ulong, Puck>> into)
        {
            if (into == null) return;
            into.Clear();

            var self = Instance;
            if (self == null) return;

            foreach (var kv in self.playerYoyoPucks)
            {
                if (kv.Value == null) continue;
                into.Add(kv);
            }
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

        /// <summary>
        /// Wipe all per-player yoyo state. Called on disconnect. Tries both keys
        /// (steamId and clientId) because GetSteamIdFromPlayer can resolve to
        /// either depending on listen-server vs dedicated.
        /// </summary>
        public void CleanupForPlayer(ulong steamId, ulong clientId)
        {
            CleanupKey(steamId);
            if (clientId != steamId) CleanupKey(clientId);

            // Also static dicts. _implicitYoyo was missing from this list, so a player who
            // disconnected with /tapyoyo on left an entry behind for the life of the server.
            LastTouchedPuck.Remove(steamId);
            PlayerPassSettings.Remove(steamId);
            PracticeHelpers.ClearShooter(steamId);
            PlayerTapPassSettings.Remove(steamId);
            TapSpawnPlayers.Remove(steamId);
            TapYoyoPlayers.Remove(steamId);
            TapBackpassPlayers.Remove(steamId);
            _implicitYoyo.Remove(steamId);
            if (clientId != steamId)
            {
                LastTouchedPuck.Remove(clientId);
                PlayerPassSettings.Remove(clientId);
                PlayerTapPassSettings.Remove(clientId);
                TapSpawnPlayers.Remove(clientId);
                TapYoyoPlayers.Remove(clientId);
                TapBackpassPlayers.Remove(clientId);
                _implicitYoyo.Remove(clientId);
            }
        }

        private void CleanupKey(ulong key)
        {
            if (playerYoyoPucks.TryGetValue(key, out Puck puck) && puck != null)
                puckReturning.Remove(puck);
            playerYoyoPucks.Remove(key);
            puckTouchTime.Remove(key);
            playerStickTrackers.Remove(key);
            playerStickTapTrackers.Remove(key);
            lastYankTime.Remove(key);
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
