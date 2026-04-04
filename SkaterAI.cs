using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace MaxPractice
{
    /// <summary>
    /// Single frame of recorded skater movement
    /// </summary>
    public struct SkaterFrame
    {
        public float Time;
        public Vector3 BodyPosition;
        public Quaternion BodyRotation;
        public Vector3 StickPosition;           // World position (for fallback)
        public Quaternion StickRotation;        // World rotation (for fallback)
        public Vector3 StickOffsetFromBody;     // Stick position relative to body (in body's local space)
        public Quaternion StickRotOffset;       // Stick rotation relative to body rotation
        public sbyte BladeAngle;                // Blade angle value from PlayerInput
        
        public SkaterFrame(float time, Vector3 bodyPos, Quaternion bodyRot, Vector3 stickPos, Quaternion stickRot, Vector3 stickOffset, Quaternion stickRotOffset, sbyte bladeAngle)
        {
            Time = time;
            BodyPosition = bodyPos;
            BodyRotation = bodyRot;
            StickPosition = stickPos;
            StickRotation = stickRot;
            StickOffsetFromBody = stickOffset;
            StickRotOffset = stickRotOffset;
            BladeAngle = bladeAngle;
        }
    }

    /// <summary>
    /// Recorded skater movement sequence for playback
    /// </summary>
    public class SkaterRecording
    {
        public List<SkaterFrame> Frames = new List<SkaterFrame>();
        public float StartTime;
        public float Duration => Frames.Count > 0 ? Frames[Frames.Count - 1].Time : 0f;
        public float MaxDuration = 20f;
        public bool IsRecording = false;
        public PlayerTeam Team;
        
        public void StartRecording(PlayerTeam team)
        {
            Frames.Clear();
            StartTime = Time.time;
            IsRecording = true;
            Team = team;
        }
        
        public void AddFrame(Player player)
        {
            if (!IsRecording || player == null) return;
            
            float frameTime = Time.time - StartTime;
            Vector3 bodyPos = Vector3.zero;
            Quaternion bodyRot = Quaternion.identity;
            Vector3 stickPos = Vector3.zero;
            Quaternion stickRot = Quaternion.identity;
            Vector3 stickOffsetFromBody = Vector3.zero;
            Quaternion stickRotOffset = Quaternion.identity;
            sbyte bladeAngle = 0;
            
            try
            {
                if (player.PlayerBody != null)
                {
                    bodyPos = player.PlayerBody.transform.position;
                    bodyRot = player.PlayerBody.transform.rotation;
                }
                if (player.Stick != null && player.PlayerBody != null)
                {
                    stickPos = player.Stick.transform.position;
                    stickRot = player.Stick.transform.rotation;
                    
                    // Calculate stick position/rotation relative to body
                    // This allows correct positioning when playing back on a traffic player at a different location
                    stickOffsetFromBody = Quaternion.Inverse(bodyRot) * (stickPos - bodyPos);
                    stickRotOffset = Quaternion.Inverse(bodyRot) * stickRot;
                }
                if (player.PlayerInput != null)
                {
                    try { bladeAngle = player.PlayerInput.BladeAngleInput.ServerValue; } catch (Exception) { }
                }
            }
            catch { }
            
            Frames.Add(new SkaterFrame(frameTime, bodyPos, bodyRot, stickPos, stickRot, stickOffsetFromBody, stickRotOffset, bladeAngle));
        }
        
        public void StopRecording()
        {
            IsRecording = false;
        }
        
        public SkaterFrame GetFrameAtTime(float time)
        {
            if (Frames.Count == 0) return default;
            if (Frames.Count == 1) return Frames[0];
            
            // Loop time back to start
            float loopedTime = time % Duration;
            
            // Find the two frames to interpolate between
            for (int i = 0; i < Frames.Count - 1; i++)
            {
                if (loopedTime >= Frames[i].Time && loopedTime < Frames[i + 1].Time)
                {
                    float t = (loopedTime - Frames[i].Time) / (Frames[i + 1].Time - Frames[i].Time);
                    return LerpFrame(Frames[i], Frames[i + 1], t);
                }
            }
            
            return Frames[Frames.Count - 1];
        }
        
        private SkaterFrame LerpFrame(SkaterFrame a, SkaterFrame b, float t)
        {
            return new SkaterFrame(
                Mathf.Lerp(a.Time, b.Time, t),
                Vector3.Lerp(a.BodyPosition, b.BodyPosition, t),
                Quaternion.Slerp(a.BodyRotation, b.BodyRotation, t),
                Vector3.Lerp(a.StickPosition, b.StickPosition, t),
                Quaternion.Slerp(a.StickRotation, b.StickRotation, t),
                Vector3.Lerp(a.StickOffsetFromBody, b.StickOffsetFromBody, t),
                Quaternion.Slerp(a.StickRotOffset, b.StickRotOffset, t),
                t < 0.5f ? a.BladeAngle : b.BladeAngle // Use nearest blade angle (no interpolation needed)
            );
        }
    }

    /// <summary>
    /// AI Skater that can receive passes and pass back to the player
    /// Now uses PlayerInput like the goalie AI for dynamic movement
    /// </summary>
    public class SkaterAI : MonoBehaviour
    {
        // Static storage
        public static Dictionary<ulong, SkaterRecording> ActiveRecordings = new Dictionary<ulong, SkaterRecording>();
        public static Dictionary<ulong, SkaterRecording> CompletedRecordings = new Dictionary<ulong, SkaterRecording>();
        public static Dictionary<Player, Coroutine> PlaybackCoroutines = new Dictionary<Player, Coroutine>();
        public static List<Player> AISkaters = new List<Player>();
        public static List<Player> RedAISkaters = new List<Player>();
        public static List<Player> BlueAISkaters = new List<Player>();
        
        // Unique numbering for traffic/passers (for individual clearing)
        private static int _nextTrafficNumber = 1;
        private static int _nextPasserNumber = 1;
        
        // Track AI by their assigned number for individual clearing
        public static Dictionary<int, Player> TrafficByNumber = new Dictionary<int, Player>();
        public static Dictionary<int, Player> PasserByNumber = new Dictionary<int, Player>();
        
        // Instance fields
        public Player SkaterPlayer;
        private ulong _targetPlayerSteamId; // Store steamId for server stability (handles reconnects)
        private Player _targetPlayerRef; // Direct reference as fallback
        public bool IsPassingAI = false;
        public float PassForce = 15f;
        public float PassCooldown = 0.5f;
        
        /// <summary>
        /// Get the target player - tries steamId lookup first, falls back to direct reference
        /// </summary>
        public Player TargetPlayer 
        { 
            get 
            {
                // Try steamId lookup first (handles reconnects)
                if (_targetPlayerSteamId != 0)
                {
                    var player = PracticeHelpers.FindPlayerBySteamId(_targetPlayerSteamId);
                    if (player != null) return player;
                }
                // Fallback to direct reference (may be stale but better than nothing)
                if (_targetPlayerRef != null && _targetPlayerRef.gameObject != null)
                    return _targetPlayerRef;
                return null;
            }
        }
        
        // Follow behavior settings
        public float FollowDistance = 12f;  // Ideal distance to maintain from player
        public float FollowDeadzone = 4f;  // Don't move if within this range of ideal distance
        public float MaxChaseRange = 24f;  // Max range to detect pucks
        public float PlayerPuckProximity = 6f;  // If puck is this close to target player, don't intercept
        
        private Puck _currentPuck;
        private float _lastPassTime = 0f;
        
        // Components for input-based control
        private PlayerBody _body;
        private PlayerInput _playerInput;
        private Rigidbody _bodyRb;
        private Stick _stick;
        private StickPositioner _stickPositioner;
        
        // Position tracking
        private Vector3 _spawnPosition;
        private bool _isInitialized = false;
        private bool _aiEnabled = false;
        private float _updateIntervalActive = 0.033f; // ~30fps when active (puck nearby)
        private float _updateIntervalIdle = 0.1f;     // ~10fps when idle (just following player)
        private float _updateInterval = 0.033f;       // Current interval
        private float _nextUpdateTime = 0f;
        private int _physicsResetCounter = 0; // Counter for physics reset throttling
        
        // Blade angle direct control (bypasses PlayerInput for server compatibility)
        private GameObject _rotationContainer;
        private static FieldInfo _rotationContainerField;
        private sbyte _targetBladeAngle = 0; // Track desired blade angle for LateUpdate
        
        // AI State (simplified - we fire immediately on contact)
        public enum AIState { Idle, WaitingForPass, ReceivingPuck }
        public AIState CurrentState = AIState.Idle;
        
        void Start()
        {
            try
            {
                StartCoroutine(DelayedStart());
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SkaterAI] Error in Start: {ex.Message}");
            }
        }
        
        IEnumerator DelayedStart()
        {
            // Wait for network object to fully spawn
            yield return new WaitForSeconds(0.5f);
            
            int maxAttempts = 20;
            int attempts = 0;
            while (attempts < maxAttempts)
            {
                try
                {
                    if (SkaterPlayer != null && SkaterPlayer.IsSpawned)
                        break;
                }
                catch (Exception) { }
                yield return new WaitForSeconds(0.1f);
                attempts++;
            }
            
            if (SkaterPlayer == null || !SkaterPlayer.IsSpawned)
            {
                Destroy(this);
                yield break;
            }
            
            InitializeComponents();
            
            if (_body == null || _bodyRb == null || _playerInput == null || !_isInitialized)
            {
                Debug.LogError("[SkaterAI] Failed to initialize components");
                Destroy(this);
                yield break;
            }
            
            // Verify inputs are ready
            yield return new WaitForSeconds(0.5f);
            
            bool allInputsReady = false;
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    if (_playerInput.StickRaycastOriginAngleInput != null)
                    {
                        var testValue = _playerInput.StickRaycastOriginAngleInput.ServerValue;
                        allInputsReady = true;
                        break;
                    }
                }
                catch (Exception) { }
                yield return new WaitForSeconds(0.2f);
            }
            
            if (!allInputsReady)
            {
                Destroy(this);
                yield break;
            }
            
            _spawnPosition = _body.transform.position;
            
            // Apply drag to counteract CompetitivePuckTweaks (like GoalieAI)
            // Only called once at init - throttled check happens in FixedUpdate
            ResetSkaterPhysics();
            
            _aiEnabled = true;
        }
        
        /// <summary>
        /// Reset physics settings to counteract CompetitivePuckTweaks mod
        /// which sets drag to 0, causing AI players to slide around.
        /// </summary>
        void ResetSkaterPhysics()
        {
            if (_bodyRb == null) return;
            
            // Only apply if CompTweaks is loaded - check cached value
            if (!CompTweaksDetector.IsCompTweaksLoaded) return;
            
            // Apply drag (CompTweaks sets to 0)
            _bodyRb.linearDamping = 4.0f;
            _bodyRb.angularDamping = 0.5f;
        }
        
        void InitializeComponents()
        {
            try
            {
                if (SkaterPlayer != null)
                {
                    _body = SkaterPlayer.PlayerBody;
                    _playerInput = SkaterPlayer.PlayerInput;
                    _stick = SkaterPlayer.Stick;
                    _stickPositioner = SkaterPlayer.StickPositioner;
                    
                    if (_body != null)
                    {
                        _bodyRb = _body.GetComponent<Rigidbody>();
                    }
                    
                    // Get rotationContainer for direct blade angle control
                    TryGetRotationContainer();
                    
                    _isInitialized = true;
                }
            }
            catch (Exception) { }
        }
        
        void FixedUpdate()
        {
            if (!_aiEnabled || !IsPassingAI) return;
            
            try
            {
                if (SkaterPlayer == null || _body == null || _bodyRb == null || _playerInput == null)
                {
                    return;
                }
                
                if (!SkaterPlayer.IsSpawned) return;
                if (!MaxPracticePlugin.FakePlayers.Contains(SkaterPlayer)) { Destroy(this); return; }
                
                // Periodic physics reset to counteract CompetitivePuckTweaks (every ~0.5s)
                _physicsResetCounter++;
                if (_physicsResetCounter >= 25)
                {
                    _physicsResetCounter = 0;
                    ResetSkaterPhysics();
                }
                
                // Throttle updates - use adaptive interval
                if (Time.time < _nextUpdateTime) return;
                _nextUpdateTime = Time.time + _updateInterval;
                
                // Give infinite stamina
                _body.Stamina.Value = 1f;
                
                // Follow player at medium range (includes facing target)
                FollowPlayer();
                
                // Handle passer state machine - we fire immediately on contact now
                if (CurrentState == AIState.WaitingForPass)
                {
                    bool puckNearby = CheckForIncomingPuck();
                    // Adaptive interval: faster updates when puck is incoming
                    _updateInterval = puckNearby ? _updateIntervalActive : _updateIntervalIdle;
                }
            }
            catch (Exception) { }
        }
        
        void FollowPlayer()
        {
            if (_body == null || _bodyRb == null || _playerInput == null) return;
            
            var target = TargetPlayer;
            if (target?.PlayerBody == null) 
            {
                // Debug: log why we can't follow (only occasionally to avoid spam)
                if (Time.frameCount % 300 == 0)
                {
                    Debug.LogWarning($"[SkaterAI] FollowPlayer: No target - steamId={_targetPlayerSteamId}, hasRef={_targetPlayerRef != null}, target={target}");
                }
                return;
            }
            
            try
            {
                Vector3 currentPos = _body.transform.position;
                Vector3 targetPos = target.PlayerBody.transform.position;
                
                // Always follow player - don't chase pucks
                // Calculate direction to player for facing
                Vector3 dirToTarget = targetPos - currentPos;
                dirToTarget.y = 0;
                
                float targetAngle = Mathf.Atan2(dirToTarget.x, dirToTarget.z) * Mathf.Rad2Deg;
                float currentAngle = _body.transform.eulerAngles.y;
                float angleDiff = Mathf.DeltaAngle(currentAngle, targetAngle);
                
                // FORCE direct rotation to face player (bypasses CompTweaks interference)
                // This ensures the passer always faces their target regardless of other mods
                if (Mathf.Abs(angleDiff) > 5f)
                {
                    // Gradually rotate toward target (smooth but direct)
                    float rotationSpeed = 180f * Time.fixedDeltaTime; // 180 deg/sec
                    float rotationStep = Mathf.Clamp(angleDiff, -rotationSpeed, rotationSpeed);
                    Quaternion newRot = Quaternion.Euler(0f, currentAngle + rotationStep, 0f);
                    _body.transform.rotation = newRot;
                    
                    // Also notify via rigidbody if available
                    if (_bodyRb != null)
                    {
                        _bodyRb.MoveRotation(newRot);
                    }
                }
                
                // No puck to chase - follow player at medium range
                Vector3 diff = targetPos - currentPos;
                diff.y = 0;
                float dist = diff.magnitude;
                
                // Calculate how far we are from ideal follow distance
                float distanceError = dist - FollowDistance;
                
                // If within deadzone, just idle but face target
                if (Mathf.Abs(distanceError) < FollowDeadzone)
                {
                    // Idle - stop moving but face target
                    try { _playerInput.StopInput.ServerValue = true; } catch (Exception) { }
                    try { _playerInput.SprintInput.ServerValue = false; } catch (Exception) { }
                    
                    // Turn to face target while idle (using inputs as fallback)
                    if (Mathf.Abs(angleDiff) > 3f)
                    {
                        try { _playerInput.SlideInput.ServerValue = true; } catch (Exception) { }
                        float turnAmount = Mathf.Clamp(angleDiff / 10f, -1f, 1f);
                        Vector2 turnInput = new Vector2(turnAmount, 0f);
                        try 
                        { 
                            _playerInput.MoveInput.ServerValue = turnInput;
                        } 
                        catch (Exception) { }
                    }
                    else
                    {
                        try { _playerInput.SlideInput.ServerValue = false; } catch (Exception) { }
                        try 
                        { 
                            _playerInput.MoveInput.ServerValue = Vector2.zero;
                        } 
                        catch (Exception) { }
                    }
                    return;
                }
                
                // Need to move - release stop input
                try { _playerInput.StopInput.ServerValue = false; } catch (Exception) { }
                
                // Determine direction: positive = too far (move closer), negative = too close (back up)
                Vector3 moveDirection;
                if (distanceError > 0)
                {
                    // Too far - move toward player
                    moveDirection = diff.normalized;
                }
                else
                {
                    // Too close - back away from player
                    moveDirection = -diff.normalized;
                }
                
                // Calculate input relative to bot's facing direction
                Vector3 forward = _body.transform.forward;
                Vector3 right = _body.transform.right;
                
                float dotForward = Vector3.Dot(moveDirection, forward);
                float dotRight = Vector3.Dot(moveDirection, right);
                
                // Set movement inputs (Y = forward/back, X = strafe)
                // Scale based on how far from ideal we are
                float intensity = Mathf.Clamp01(Mathf.Abs(distanceError) / 5f);
                
                float moveY = dotForward * intensity;
                float moveX = dotRight * intensity * 0.5f; // Less strafe
                
                // Clamp inputs
                moveY = Mathf.Clamp(moveY, -1f, 1f);
                moveX = Mathf.Clamp(moveX, -0.5f, 0.5f);
                
                Vector2 moveVal = new Vector2(moveX, moveY);
                try 
                { 
                    _playerInput.MoveInput.ServerValue = moveVal;
                } 
                catch (Exception) { }
            }
            catch (Exception) { }
        }
        
        void ResetInputs()
        {
            try
            {
                if (_playerInput == null) return;
                try { _playerInput.MoveInput.ServerValue = Vector2.zero; } catch (Exception) { }
            }
            catch (Exception) { }
        }
        
        /// <summary>
        /// Set blade angle directly via transform (works on dedicated servers)
        /// The actual transform is applied in LateUpdate to override the game's FixedUpdate
        /// </summary>
        void SetBladeAngleDirect(sbyte bladeAngle)
        {
            _targetBladeAngle = bladeAngle;
        }
        
        /// <summary>
        /// Try to get the rotationContainer from Stick via reflection
        /// </summary>
        void TryGetRotationContainer()
        {
            if (_rotationContainer != null) return;
            if (_stick == null) _stick = SkaterPlayer?.Stick;
            if (_stick == null) return;
            
            try
            {
                if (_rotationContainerField == null)
                {
                    _rotationContainerField = typeof(Stick).GetField("rotationContainer", BindingFlags.NonPublic | BindingFlags.Instance);
                }
                if (_rotationContainerField != null)
                {
                    _rotationContainer = _rotationContainerField.GetValue(_stick) as GameObject;
                }
            }
            catch (Exception) { }
        }
        
        /// <summary>
        /// LateUpdate runs after all FixedUpdate calls, so we apply blade angle here
        /// to ensure it overrides the game's Stick.FixedUpdate that reads BladeAngleInput
        /// </summary>
        void LateUpdate()
        {
            if (!_aiEnabled || !IsPassingAI) return;
            
            try
            {
                // Retry getting rotationContainer if we don't have it yet
                if (_rotationContainer == null)
                {
                    TryGetRotationContainer();
                    if (_rotationContainer == null) return;
                }
                
                // Apply blade angle directly to transform, overriding game's value
                float bladeAngleDegrees = _targetBladeAngle * 12.5f;
                _rotationContainer.transform.localRotation = Quaternion.AngleAxis(bladeAngleDegrees, Vector3.forward);
            }
            catch (Exception) { }
        }
        
        // Handedness tracking
        private PlayerHandedness _currentHandedness = PlayerHandedness.Right;
        private float _lastHandednessSwitch = 0f;
        private const float HANDEDNESS_SWITCH_COOLDOWN = 0.03f; // Minimal cooldown for fast switching
        
        /// <summary>
        /// Check for incoming pucks and handle reception.
        /// Returns true if a puck is within tracking range (used for adaptive update rate).
        /// </summary>
        bool CheckForIncomingPuck()
        {
            if (SkaterPlayer?.PlayerBody == null) return false;
            
            // Don't process during pass cooldown
            if (Time.time - _lastPassTime < PassCooldown)
            {
                return false;
            }
            
            var puckMgr = MonoBehaviourSingleton<PuckManager>.Instance;
            if (puckMgr == null) return false;
            
            var pucks = puckMgr.GetPucks(false);
            Puck closestPuck = null;
            float closestDistSqr = float.MaxValue;
            Vector3 closestVel = Vector3.zero;
            Vector3 bodyPos = _body.transform.position;
            
            // Find closest puck that was last touched by TargetPlayer
            foreach (var puck in pucks)
            {
                if (puck == null) continue;
                
                // Check if this puck was last touched by our TargetPlayer
                if (TargetPlayer != null)
                {
                    var collisions = puck.GetPlayerCollisions();
                    if (collisions.Count > 0)
                    {
                        var lastToucher = collisions[collisions.Count - 1].Key;
                        if (lastToucher != TargetPlayer)
                            continue; // Skip pucks not from our target player
                    }
                    else
                    {
                        continue; // No collision history - skip
                    }
                }
                
                Vector3 diff = puck.transform.position - bodyPos;
                float distSqr = diff.sqrMagnitude;
                
                if (distSqr < closestDistSqr)
                {
                    closestDistSqr = distSqr;
                    closestPuck = puck;
                    var rb = puck.GetComponent<Rigidbody>();
                    closestVel = (rb != null) ? rb.linearVelocity : Vector3.zero;
                }
            }
            
            float closestDist = closestPuck != null ? Mathf.Sqrt(closestDistSqr) : float.MaxValue;
            
            // No valid puck found or too far
            if (closestPuck == null || closestDist > MaxChaseRange)
            {
                return false; // No puck nearby - can use slower update rate
            }
            
            // Don't intercept if puck is close to target player
            if (TargetPlayer?.PlayerBody != null)
            {
                float distToPlayer = Vector3.Distance(closestPuck.transform.position, TargetPlayer.PlayerBody.transform.position);
                if (distToPlayer < PlayerPuckProximity)
                    return false; // Puck is with player - can use slower update rate
            }
            
            // Track puck with stick if within range
            if (closestDist < 15f)
            {
                // Switch handedness based on puck position
                UpdateHandednessForPuck(closestPuck.transform.position, closestVel);
                
                // Position stick to intercept
                MoveStickTowardsPuck(closestPuck.transform.position, closestVel, closestDist);
                
                // Trigger pass when puck is close
                if (closestDist < 1.5f)
                {
                    ReceivePuck(closestPuck);
                }
                
                return true; // Puck is being tracked - use fast update rate
            }
            
            return true; // Puck exists but not tracking yet - stay alert
        }
        
        /// <summary>
        /// Switch handedness based on which side the puck is approaching from.
        /// </summary>
        void UpdateHandednessForPuck(Vector3 puckPos, Vector3 puckVel)
        {
            if (SkaterPlayer == null || _body == null) return;
            
            // Minimal cooldown - only prevent same-frame double switches
            if (Time.time - _lastHandednessSwitch < HANDEDNESS_SWITCH_COOLDOWN) return;
            
            Vector3 bodyPos = _body.transform.position;
            Vector3 bodyRight = _body.transform.right;
            
            // PREDICT where puck will be - look further ahead for faster reaction
            Vector3 evaluatePos = puckPos;
            
            if (puckVel.sqrMagnitude > 0.5f)
            {
                // Calculate where puck will be when it reaches us
                Vector3 toPuckCurrent = puckPos - bodyPos;
                toPuckCurrent.y = 0;
                float dist = toPuckCurrent.magnitude;
                float puckSpeed = puckVel.magnitude;
                
                if (puckSpeed > 0.5f)
                {
                    // Predict further ahead - up to 0.5 seconds
                    float lookAhead = Mathf.Clamp(dist / puckSpeed, 0.1f, 0.5f);
                    evaluatePos = puckPos + puckVel * lookAhead;
                }
            }
            
            // Calculate side based on PREDICTED position
            Vector3 toPuck = evaluatePos - bodyPos;
            toPuck.y = 0;
            
            if (toPuck.sqrMagnitude < 0.01f) return; // Too close to evaluate
            
            float sideValue = Vector3.Dot(toPuck.normalized, bodyRight);
            
            // Very small dead zone - react quickly
            PlayerHandedness desiredHand;
            float threshold = 0.05f;
            
            if (sideValue < -threshold)
            {
                desiredHand = PlayerHandedness.Left;
            }
            else if (sideValue > threshold)
            {
                desiredHand = PlayerHandedness.Right;
            }
            else
            {
                return; // Dead zone
            }
            
            // Switch immediately if different
            if (desiredHand != _currentHandedness)
            {
                _currentHandedness = desiredHand;
                try { SkaterPlayer.Handedness.Value = desiredHand; } catch (Exception) { }
                _lastHandednessSwitch = Time.time;
            }
        }
        
        void ReceivePuck(Puck puck)
        {
            _currentPuck = puck;
            StartCoroutine(SnapAndFirePuck(puck));
        }
        
        IEnumerator SnapAndFirePuck(Puck puck)
        {
            if (puck == null || _body == null)
            {
                CurrentState = AIState.WaitingForPass;
                yield break;
            }
            
            var puckRb = puck.GetComponent<Rigidbody>();
            if (puckRb == null)
            {
                CurrentState = AIState.WaitingForPass;
                yield break;
            }
            
            // Get target position
            Vector3 currentPuckPos = puck.transform.position;
            Vector3 bodyPos = _body.transform.position;
            Vector3 targetPos = TargetPlayer?.PlayerBody != null 
                ? TargetPlayer.PlayerBody.transform.position 
                : (TargetPlayer != null ? TargetPlayer.transform.position : bodyPos + _body.transform.forward * 10f);
            
            Vector3 passDirection = (targetPos - currentPuckPos).normalized;
            passDirection.y = 0;
            
            // Add lead if target is moving
            if (TargetPlayer?.PlayerBody != null)
            {
                var targetRb = TargetPlayer.PlayerBody.GetComponent<Rigidbody>();
                if (targetRb != null)
                {
                    Vector3 targetVel = targetRb.linearVelocity;
                    targetVel.y = 0;
                    
                    if (targetVel.magnitude > 0.5f)
                    {
                        float distToTarget = Vector3.Distance(currentPuckPos, targetPos);
                        float travelTime = distToTarget / PassForce;
                        Vector3 leadOffset = targetVel * travelTime * 0.5f;
                        Vector3 leadTarget = targetPos + leadOffset;
                        passDirection = (leadTarget - currentPuckPos).normalized;
                        passDirection.y = 0;
                    }
                }
            }
            
            // Calculate stick swing angle toward target
            float bodyAngle = _body.transform.eulerAngles.y;
            float targetAngle = Mathf.Atan2(passDirection.x, passDirection.z) * Mathf.Rad2Deg;
            float swingAngle = Mathf.DeltaAngle(bodyAngle, targetAngle);
            swingAngle = Mathf.Clamp(swingAngle, -60f, 60f);
            
            // Swing stick toward target
            try 
            { 
                _playerInput.StickRaycastOriginAngleInput.ServerValue = new Vector2(35f, swingAngle);
            } 
            catch (Exception) { }
            
            // Apply pass velocity IMMEDIATELY
            Vector3 passVelocity = passDirection * PassForce;
            puckRb.linearVelocity = passVelocity;
            puckRb.angularVelocity = Vector3.zero;
            
            _currentPuck = null;
            _lastPassTime = Time.time;
            
            // Quick reset
            yield return null;
            
            CurrentState = AIState.WaitingForPass;
            try 
            { 
                _playerInput.StickRaycastOriginAngleInput.ServerValue = new Vector2(35f, 0f);
            } 
            catch (Exception) { }
        }
        
        /// <summary>
        /// Position stick to intercept/catch puck. Uses prediction and GoalieAI-style Y-axis tracking.
        /// </summary>
        void MoveStickTowardsPuck(Vector3 puckPos, Vector3 puckVelocity, float distToPuck)
        {
            if (_playerInput == null || _body == null) return;
            
            try
            {
                Vector3 bodyPos = _body.transform.position;
                float bodyAngle = _body.transform.eulerAngles.y;
                
                // === PREDICT PUCK POSITION (keeping existing logic) ===
                Vector3 predictedPos = puckPos;
                float puckSpeedSqr = puckVelocity.sqrMagnitude;
                
                if (puckSpeedSqr > 0.1f)
                {
                    Vector3 toPuckCurrent = puckPos - bodyPos;
                    toPuckCurrent.y = 0;
                    float puckSpeed = Mathf.Sqrt(puckSpeedSqr);
                    
                    if (puckSpeed > 0.3f)
                    {
                        Vector3 puckDir = puckVelocity.normalized;
                        puckDir.y = 0;
                        float dotToUs = Vector3.Dot(-toPuckCurrent.normalized, puckDir);
                        
                        if (dotToUs > 0.1f) // Puck heading toward us
                        {
                            float maxLookAhead = (distToPuck > 5f) ? 0.4f : 0.8f;
                            float lookAheadTime = Mathf.Clamp(distToPuck / puckSpeed, 0.1f, maxLookAhead);
                            predictedPos = puckPos + puckVelocity * lookAheadTime;
                            // Account for gravity on airborne pucks
                            predictedPos.y = Mathf.Max(0.05f, predictedPos.y - 9.81f * lookAheadTime * lookAheadTime * 0.5f);
                        }
                        else if (dotToUs > -0.3f)
                        {
                            float lookAheadTime = Mathf.Clamp(distToPuck / puckSpeed, 0.05f, 0.3f);
                            predictedPos = puckPos + puckVelocity * lookAheadTime;
                            predictedPos.y = Mathf.Max(0.05f, predictedPos.y - 9.81f * lookAheadTime * lookAheadTime * 0.5f);
                        }
                    }
                }
                
                // === CALCULATE HORIZONTAL ANGLE ===
                Vector3 toPuck = predictedPos - bodyPos;
                float puckAngleWorld = Mathf.Atan2(toPuck.x, toPuck.z) * Mathf.Rad2Deg;
                float stickAngle = puckAngleWorld - bodyAngle;
                
                while (stickAngle > 180f) stickAngle -= 360f;
                while (stickAngle < -180f) stickAngle += 360f;
                
                float horizontalAngle = Mathf.Clamp(stickAngle, -90f, 90f);
                
                // === VERTICAL ANGLE - GoalieAI style Y-axis tracking ===
                // Track puck height for mid-air pucks!
                float puckHeight = predictedPos.y;
                float verticalAngle;
                
                if (puckHeight < 0.15f)
                {
                    // Puck on ice - lower stick more aggressively to get under the puck
                    float effectiveDist = Mathf.Min(distToPuck, toPuck.magnitude);
                    if (effectiveDist < 2f)
                    {
                        // Close pucks: lower stick significantly (35 -> 70 degrees)
                        float closenessFactor = 1f - (effectiveDist / 2f);
                        verticalAngle = Mathf.Lerp(35f, 70f, closenessFactor);
                    }
                    else
                    {
                        verticalAngle = 35f;
                    }
                }
                else if (puckHeight > 1.5f)
                {
                    // Puck high - stick up
                    verticalAngle = -20f;
                }
                else
                {
                    // Mid-air puck - interpolate like GoalieAI
                    verticalAngle = Mathf.Lerp(35f, -20f, puckHeight / 1.5f);
                }
                
                try 
                { 
                    _playerInput.StickRaycastOriginAngleInput.ServerValue = new Vector2(verticalAngle, horizontalAngle);
                } 
                catch (Exception) { }
                
                // === BLADE ANGLE - angle blade to receive puck ===
                sbyte bladeAngle = 0;
                
                if (puckVelocity.sqrMagnitude > 0.25f)
                {
                    Vector3 puckDir = puckVelocity.normalized;
                    puckDir.y = 0;
                    float puckTravelAngle = Mathf.Atan2(puckDir.x, puckDir.z) * Mathf.Rad2Deg;
                    float stickWorldAngle = bodyAngle + stickAngle;
                    
                    float angleDiff = puckTravelAngle - stickWorldAngle;
                    while (angleDiff > 180f) angleDiff -= 360f;
                    while (angleDiff < -180f) angleDiff += 360f;
                    
                    if (Mathf.Abs(angleDiff) <= 135f)
                    {
                        float normalizedAngle = angleDiff / 135f;
                        bladeAngle = (sbyte)Mathf.Clamp(Mathf.RoundToInt(normalizedAngle * 4f), -4, 4);
                    }
                }
                
                try 
                { 
                    _playerInput.BladeAngleInput.ServerValue = bladeAngle;
                    _playerInput.Server_BladeAngleInputRpc(bladeAngle, _playerInput.RpcTarget.NotServer);
                } 
                catch (Exception) { }
                
                SetBladeAngleDirect(bladeAngle);
            }
            catch (Exception) { }
        }
        
        public void EnablePassingMode(Player target, ulong targetSteamId = 0)
        {
            // Prefer the explicitly passed steamId (more reliable on dedicated servers)
            if (targetSteamId != 0)
            {
                _targetPlayerSteamId = targetSteamId;
            }
            else
            {
                _targetPlayerSteamId = PracticeHelpers.GetSteamIdFromPlayer(target);
            }
            _targetPlayerRef = target; // Keep direct reference as fallback
            IsPassingAI = true;
            CurrentState = AIState.WaitingForPass;
            Debug.Log($"[SkaterAI] EnablePassingMode: steamId={_targetPlayerSteamId}, hasRef={target != null}");
        }
        
        public void DisablePassingMode()
        {
            IsPassingAI = false;
            CurrentState = AIState.Idle;
            _targetPlayerSteamId = 0;
            _targetPlayerRef = null;
            ResetInputs();
        }
        
        // =====================================================
        // STATIC METHODS - Traffic/Recording functionality
        // =====================================================
        
        /// <summary>
        /// Spawn a traffic dummy with optional recording playback
        /// </summary>
        public static IEnumerator SpawnTrafficDelayed(
            UIChat ui, ulong clientId, Player spawningPlayer, ulong senderSteam,
            bool isRedTeam, int trafficNum,
            Vector3 spawnPos, Quaternion spawnRot, Vector3 stickOffsetFromBody, Quaternion stickRotOffset, sbyte bladeAngle, bool hasStickData,
            SkaterRecording recording, bool hasRecording)
        {
            // 5 second delay
            yield return new WaitForSeconds(3f);
            
            var playerManager = MonoBehaviourSingleton<PlayerManager>.Instance;
            if (playerManager == null) yield break;
            
            var playerPrefabField = typeof(PlayerManager).GetField("playerPrefab", BindingFlags.NonPublic | BindingFlags.Instance);
            Player playerPrefab = (Player)playerPrefabField?.GetValue(playerManager);
            if (playerPrefab == null) yield break;
            
            var teamList = isRedTeam ? RedAISkaters : BlueAISkaters;
            
            // Re-check limit after delay using config
            int maxPerTeam = MaxPractice.ConfigManager.Config.TrafficPerPlayer;
            if (teamList.Count >= maxPerTeam) yield break;
            
            // Get unique traffic number (persists across clears for consistent naming)
            int uniqueTrafficNum = _nextTrafficNumber++;
            
            Player trafficPlayer = null;
            bool spawnSuccess = false;
            
            // Suppress NullRef spam during spawn (about 100 frames = ~2 seconds at 50fps)
            MaxPracticePlugin.SuppressNullRefsFor(100);
            
            try
            {
                ulong fakeClientId = 3333333UL + (ulong)uniqueTrafficNum + (ulong)(isRedTeam ? 0 : 1000);
                
                trafficPlayer = UnityEngine.Object.Instantiate(playerPrefab);
                NetworkObject netObj = trafficPlayer.GetComponent<NetworkObject>();
                
                netObj.SpawnWithOwnership(fakeClientId, true);
                
                try { trafficPlayer.Username.Value = new FixedString32Bytes($"Traffic{uniqueTrafficNum}"); } catch (Exception) { }
                try { trafficPlayer.SteamId.Value = new FixedString32Bytes($"Traffic{uniqueTrafficNum}"); } catch (Exception) { } // Set SteamId for filtering
                try { trafficPlayer.Server_SetGameState(phase: null, team: isRedTeam ? PlayerTeam.Red : PlayerTeam.Blue, role: PlayerRole.Attacker, delay: 0f); } catch (Exception) { }
                try { trafficPlayer.Number.Value = (byte)(70 + (uniqueTrafficNum % 30)); } catch (Exception) { }
                
                // Use first frame position if we have a recording
                if (hasRecording && recording != null && recording.Frames.Count > 0)
                {
                    var firstFrame = recording.Frames[0];
                    spawnPos = firstFrame.BodyPosition;
                    spawnPos.y = 0f;
                    spawnRot = firstFrame.BodyRotation;
                }
                
                trafficPlayer.Server_SpawnCharacter(spawnPos, spawnRot, PlayerRole.Attacker);
                
                MaxPracticePlugin.FakePlayers.Add(trafficPlayer);
                MaxPracticePlugin.TrafficDummies.Add(trafficPlayer);
                AISkaters.Add(trafficPlayer);
                teamList.Add(trafficPlayer);
                
                // Track by unique number for individual clearing
                TrafficByNumber[uniqueTrafficNum] = trafficPlayer;
                
                // Track ownership so we can clean up when player leaves
                if (!MaxPracticePlugin.PlayerOwnedTraffic.ContainsKey(senderSteam))
                    MaxPracticePlugin.PlayerOwnedTraffic[senderSteam] = new List<Player>();
                MaxPracticePlugin.PlayerOwnedTraffic[senderSteam].Add(trafficPlayer);
                
                spawnSuccess = true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SkaterAI] Error spawning traffic: {ex.Message}");
            }
            
            if (!spawnSuccess || trafficPlayer == null) yield break;
            
            yield return new WaitForSeconds(0.1f);
            
            if (trafficPlayer == null) yield break;
            
            if (hasRecording && recording != null && recording.Frames.Count > 0)
            {
                var gameMgr = NetworkBehaviourSingleton<GameManager>.Instance;
                if (gameMgr != null)
                {
                    var playbackCoroutine = gameMgr.StartCoroutine(PlaybackRecording(trafficPlayer, recording));
                    PlaybackCoroutines[trafficPlayer] = playbackCoroutine;
                }
                
                PracticeHelpers.SendChatMessage(ui, $"<size=70%><color=#00FF00FF>Traffic{uniqueTrafficNum} spawned with recorded movement! Use /cleartraffic{uniqueTrafficNum} to remove.</color></size>", clientId);
            }
            else
            {
                var gameMgr = NetworkBehaviourSingleton<GameManager>.Instance;
                if (gameMgr != null)
                {
                    gameMgr.StartCoroutine(FreezeAndCopyPose(trafficPlayer, spawnRot, stickOffsetFromBody, stickRotOffset, bladeAngle, hasStickData));
                }
                
                PracticeHelpers.SendChatMessage(ui, $"<size=70%><color=#00FF00FF>Traffic{uniqueTrafficNum} spawned (static)! Use /cleartraffic{uniqueTrafficNum} to remove.</color></size>", clientId);
            }
        }

        /// <summary>
        /// Spawn a passing AI skater
        /// </summary>
        public static IEnumerator SpawnPasserDelayed(
            UIChat ui, ulong clientId, Player humanPlayer, ulong senderSteam,
            bool isRedTeam, Vector3 spawnPos, Quaternion spawnRot)
        {
            // 3 second delay for passer
            yield return new WaitForSeconds(3f);
            
            var playerManager = MonoBehaviourSingleton<PlayerManager>.Instance;
            if (playerManager == null) yield break;
            
            var playerPrefabField = typeof(PlayerManager).GetField("playerPrefab", BindingFlags.NonPublic | BindingFlags.Instance);
            Player playerPrefab = (Player)playerPrefabField?.GetValue(playerManager);
            if (playerPrefab == null) yield break;
            
            var teamList = isRedTeam ? RedAISkaters : BlueAISkaters;
            
            // Check limit using config
            int maxPerTeam = MaxPractice.ConfigManager.Config.TrafficPerPlayer;
            if (teamList.Count >= maxPerTeam) yield break;
            
            // Get unique passer number (persists across clears for consistent naming)
            int uniquePasserNum = _nextPasserNumber++;
            
            Player passerPlayer = null;
            bool spawnSuccess = false;
            
            // Suppress NullRef spam during spawn (about 100 frames = ~2 seconds at 50fps)
            MaxPracticePlugin.SuppressNullRefsFor(100);
            
            try
            {
                ulong fakeClientId = 4444444UL + (ulong)uniquePasserNum + (ulong)(isRedTeam ? 0 : 1000);
                
                passerPlayer = UnityEngine.Object.Instantiate(playerPrefab);
                NetworkObject netObj = passerPlayer.GetComponent<NetworkObject>();
                
                netObj.SpawnWithOwnership(fakeClientId, true);
                
                try { passerPlayer.Username.Value = new FixedString32Bytes($"Passer{uniquePasserNum}"); } catch (Exception) { }
                try { passerPlayer.SteamId.Value = new FixedString32Bytes($"Passer{uniquePasserNum}"); } catch (Exception) { } // Set SteamId for filtering
                try { passerPlayer.Server_SetGameState(phase: null, team: isRedTeam ? PlayerTeam.Red : PlayerTeam.Blue, role: PlayerRole.Attacker, delay: 0f); } catch (Exception) { }
                try { passerPlayer.Number.Value = (byte)(80 + (uniquePasserNum % 20)); } catch (Exception) { }
                
                passerPlayer.Server_SpawnCharacter(spawnPos, spawnRot, PlayerRole.Attacker);
                
                MaxPracticePlugin.FakePlayers.Add(passerPlayer);
                AISkaters.Add(passerPlayer);
                teamList.Add(passerPlayer);
                
                // Track by unique number for individual clearing
                PasserByNumber[uniquePasserNum] = passerPlayer;
                
                // Track ownership so we can clean up when player leaves
                if (!MaxPracticePlugin.PlayerOwnedTraffic.ContainsKey(senderSteam))
                    MaxPracticePlugin.PlayerOwnedTraffic[senderSteam] = new List<Player>();
                MaxPracticePlugin.PlayerOwnedTraffic[senderSteam].Add(passerPlayer);
                
                spawnSuccess = true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SkaterAI] Error spawning passer: {ex.Message}");
            }
            
            if (!spawnSuccess || passerPlayer == null) yield break;
            
            yield return new WaitForSeconds(0.1f);
            
            if (passerPlayer == null) yield break;
            
            // PASSER is DYNAMIC - uses PlayerInput for stick control
            // Do NOT disable StickPositioner - it's needed for stick tracking!
            // The NullRefs from game components are suppressed by the plugin's log handler
            
            // Add AI component - this handles the input-based movement
            var ai = passerPlayer.gameObject.AddComponent<SkaterAI>();
            ai.SkaterPlayer = passerPlayer;
            ai.EnablePassingMode(humanPlayer, senderSteam); // Pass steamId explicitly for server reliability
            
            PracticeHelpers.SendChatMessage(ui, $"<size=70%><color=#00FF00FF>Passer{uniquePasserNum} spawned! Pass the puck to them and they'll pass it back. Use /clearpasser{uniquePasserNum} to remove.</color></size>", clientId);
        }

        /// Freeze traffic and copy player's pose
        /// stickOffsetFromBody: stick position relative to body (in body's local space)
        /// stickRotOffset: stick rotation relative to body rotation
        /// bladeAngle: the blade angle value from PlayerInput
        public static IEnumerator FreezeAndCopyPose(Player trafficPlayer, Quaternion bodyRot, Vector3 stickOffsetFromBody, Quaternion stickRotOffset, sbyte bladeAngle, bool hasStickData)
        {
            yield return new WaitForSeconds(0.1f);
            
            if (trafficPlayer == null) yield break;
            
            try
            {
                // Disable all physics components
                if (trafficPlayer.StickPositioner != null) trafficPlayer.StickPositioner.enabled = false;
                if (trafficPlayer.Stick != null) trafficPlayer.Stick.enabled = false;
                
                if (trafficPlayer.PlayerBody != null)
                {
                    trafficPlayer.PlayerBody.enabled = false;
                    
                    var velocityLean = trafficPlayer.PlayerBody.GetComponent<VelocityLean>();
                    if (velocityLean != null) velocityLean.enabled = false;
                    
                    var movement = trafficPlayer.PlayerBody.GetComponent<Movement>();
                    if (movement != null) movement.enabled = false;
                    
                    var keepUpright = trafficPlayer.PlayerBody.GetComponent<KeepUpright>();
                    if (keepUpright != null) keepUpright.enabled = false;
                    
                    var hover = trafficPlayer.PlayerBody.GetComponent<Hover>();
                    if (hover != null) hover.enabled = false;
                    
                    foreach (var skate in trafficPlayer.PlayerBody.GetComponentsInChildren<Skate>())
                        skate.enabled = false;
                    
                    foreach (var sc in trafficPlayer.PlayerBody.GetComponentsInChildren<SoftCollider>())
                        sc.enabled = false;
                }
                
                Vector3 bodyWorldPos = Vector3.zero;
                if (trafficPlayer.PlayerBody != null)
                {
                    var bodyRb = trafficPlayer.PlayerBody.GetComponent<Rigidbody>();
                    if (bodyRb != null)
                    {
                        bodyRb.isKinematic = true;
                        bodyRb.useGravity = false;
                        trafficPlayer.PlayerBody.transform.rotation = bodyRot;
                        bodyRb.constraints = RigidbodyConstraints.FreezeAll;
                        bodyWorldPos = trafficPlayer.PlayerBody.transform.position;
                    }
                }
                
                if (hasStickData && trafficPlayer.Stick != null && trafficPlayer.PlayerBody != null)
                {
                    var stickRb = trafficPlayer.Stick.GetComponent<Rigidbody>();
                    if (stickRb != null)
                    {
                        stickRb.isKinematic = true;
                        stickRb.useGravity = false;
                        
                        Vector3 stickWorldPos = bodyWorldPos + (bodyRot * stickOffsetFromBody);
                        Quaternion stickWorldRot = bodyRot * stickRotOffset;
                        
                        trafficPlayer.Stick.transform.position = stickWorldPos;
                        trafficPlayer.Stick.transform.rotation = stickWorldRot;
                        stickRb.constraints = RigidbodyConstraints.FreezeAll;
                        
                        // Set blade angle
                        var rotationContainerField = typeof(Stick).GetField("rotationContainer", BindingFlags.NonPublic | BindingFlags.Instance);
                        if (rotationContainerField != null)
                        {
                            var rotationContainer = rotationContainerField.GetValue(trafficPlayer.Stick) as GameObject;
                            if (rotationContainer != null)
                            {
                                rotationContainer.transform.localRotation = Quaternion.AngleAxis(bladeAngle * 12.5f, Vector3.forward);
                            }
                        }
                        
                        // Broadcast blade angle to clients
                        if (trafficPlayer.PlayerInput != null)
                        {
                            try
                            {
                                trafficPlayer.PlayerInput.BladeAngleInput.ServerValue = bladeAngle;
                                trafficPlayer.PlayerInput.Server_BladeAngleInputRpc(bladeAngle, trafficPlayer.PlayerInput.RpcTarget.NotServer);
                            }
                            catch (Exception) { }
                        }
                    }
                }
                else if (trafficPlayer.Stick != null)
                {
                    var stickRb = trafficPlayer.Stick.GetComponent<Rigidbody>();
                    if (stickRb != null)
                    {
                        stickRb.isKinematic = true;
                        stickRb.useGravity = false;
                        stickRb.constraints = RigidbodyConstraints.FreezeAll;
                    }
                }
            }
            catch (Exception) { }
        }
        
        /// <summary>
        /// Playback recorded movement in a loop
        /// </summary>
        public static IEnumerator PlaybackRecording(Player trafficPlayer, SkaterRecording recording)
        {
            yield return null;
            
            if (trafficPlayer == null || trafficPlayer.PlayerBody == null) yield break;
            
            try
            {
                // Disable all physics components
                if (trafficPlayer.StickPositioner != null) trafficPlayer.StickPositioner.enabled = false;
                if (trafficPlayer.Stick != null) trafficPlayer.Stick.enabled = false;
                
                if (trafficPlayer.PlayerBody != null)
                {
                    trafficPlayer.PlayerBody.enabled = false;
                    
                    var velocityLean = trafficPlayer.PlayerBody.GetComponent<VelocityLean>();
                    if (velocityLean != null) velocityLean.enabled = false;
                    
                    var movement = trafficPlayer.PlayerBody.GetComponent<Movement>();
                    if (movement != null) movement.enabled = false;
                    
                    var keepUpright = trafficPlayer.PlayerBody.GetComponent<KeepUpright>();
                    if (keepUpright != null) keepUpright.enabled = false;
                    
                    var hover = trafficPlayer.PlayerBody.GetComponent<Hover>();
                    if (hover != null) hover.enabled = false;
                    
                    foreach (var skate in trafficPlayer.PlayerBody.GetComponentsInChildren<Skate>())
                        skate.enabled = false;
                    
                    foreach (var sc in trafficPlayer.PlayerBody.GetComponentsInChildren<SoftCollider>())
                        sc.enabled = false;
                }
                
                var bodyRb = trafficPlayer.PlayerBody.GetComponent<Rigidbody>();
                var stickRb = trafficPlayer.Stick?.GetComponent<Rigidbody>();
                
                if (bodyRb != null)
                {
                    bodyRb.isKinematic = true;
                    bodyRb.useGravity = false;
                }
                if (stickRb != null)
                {
                    stickRb.isKinematic = true;
                    stickRb.useGravity = false;
                }
            }
            catch (Exception)
            {
                yield break;
            }
            
            float playbackStartTime = Time.time;
            
            while (true)
            {
                // Safe null checks inside try block
                try
                {
                    if (trafficPlayer == null) yield break;
                    if (trafficPlayer.PlayerBody == null) yield break;
                    
                    float elapsedTime = Time.time - playbackStartTime;
                    SkaterFrame frame = recording.GetFrameAtTime(elapsedTime);
                    
                    Vector3 bodyWorldPos = frame.BodyPosition;
                    Quaternion bodyWorldRot = frame.BodyRotation;
                    
                    if (trafficPlayer.PlayerBody != null)
                    {
                        trafficPlayer.PlayerBody.transform.position = bodyWorldPos;
                        
                        if (bodyWorldRot != Quaternion.identity && bodyWorldRot.w != 0)
                        {
                            trafficPlayer.PlayerBody.transform.rotation = bodyWorldRot;
                        }
                    }
                    else
                    {
                        yield break;
                    }
                    
                    if (trafficPlayer.Stick != null)
                    {
                        // Calculate stick world position from body-relative offset
                        Vector3 stickWorldPos = bodyWorldPos + (bodyWorldRot * frame.StickOffsetFromBody);
                        Quaternion stickWorldRot = bodyWorldRot * frame.StickRotOffset;
                        
                        trafficPlayer.Stick.transform.position = stickWorldPos;
                        
                        if (stickWorldRot != Quaternion.identity && stickWorldRot.w != 0)
                        {
                            trafficPlayer.Stick.transform.rotation = stickWorldRot;
                        }
                        
                        // Apply blade angle using reflection AND broadcast via RPC for clients
                        try
                        {
                            var rotationContainerField = typeof(Stick).GetField("rotationContainer", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (rotationContainerField != null)
                            {
                                var rotationContainer = rotationContainerField.GetValue(trafficPlayer.Stick) as GameObject;
                                if (rotationContainer != null)
                                {
                                    float bladeAngleDegrees = frame.BladeAngle * 12.5f;
                                    rotationContainer.transform.localRotation = Quaternion.AngleAxis(bladeAngleDegrees, Vector3.forward);
                                }
                            }
                            
                            // Broadcast blade angle to clients via RPC
                            if (trafficPlayer.PlayerInput != null)
                            {
                                trafficPlayer.PlayerInput.BladeAngleInput.ServerValue = frame.BladeAngle;
                                trafficPlayer.PlayerInput.Server_BladeAngleInputRpc(frame.BladeAngle, trafficPlayer.PlayerInput.RpcTarget.NotServer);
                            }
                        }
                        catch (Exception) { }
                    }
                }
                catch (Exception)
                {
                    // Player might have been destroyed, exit gracefully
                    yield break;
                }
                
                // Run at ~30fps instead of every frame to reduce CPU load
                yield return new WaitForSeconds(0.033f);
            }
        }
        
        /// <summary>
        /// Record player movement
        /// </summary>
        public static IEnumerator RecordMovement(Player player, ulong steamId, UIChat ui, ulong clientId)
        {
            // Get framerate from config
            float fps = MaxPractice.PracticeConstants.TrafficRecordingFps;
            float frameDelay = fps > 0 ? 1f / fps : 0.033f;
            
            while (ActiveRecordings.ContainsKey(steamId))
            {
                var recording = ActiveRecordings[steamId];
                if (recording != null && recording.IsRecording)
                {
                    recording.AddFrame(player);
                    
                    if (recording.Duration >= recording.MaxDuration)
                    {
                        recording.StopRecording();
                        int frameCount = recording.Frames.Count;
                        
                        CompletedRecordings[steamId] = recording;
                        ActiveRecordings.Remove(steamId);
                        
                        PracticeHelpers.SendChatMessage(ui, $"<size=70%><color=#FFA500FF>Recording auto-stopped (max 20s)! ({frameCount} frames) Use /traffic to spawn.</color></size>", clientId);
                        yield break;
                    }
                }
                else
                {
                    break;
                }
                
                yield return new WaitForSeconds(frameDelay);
            }
        }
        
        /// <summary>
        /// Clear all AI skaters
        /// </summary>
        public static void ClearAllAISkaters()
        {
            try
            {
                var gameMgr = NetworkBehaviourSingleton<GameManager>.Instance;
                
                // Stop playback coroutines
                foreach (var kvp in PlaybackCoroutines.ToArray())
                {
                    try
                    {
                        if (gameMgr != null && kvp.Value != null)
                            gameMgr.StopCoroutine(kvp.Value);
                    }
                    catch (Exception) { }
                }
                PlaybackCoroutines.Clear();
                
                // Destroy AI skaters
                foreach (var skater in AISkaters.ToArray())
                {
                    try
                    {
                        if (skater != null)
                        {
                            MaxPracticePlugin.FakePlayers.Remove(skater);
                            var netObj = skater.GetComponent<NetworkObject>();
                            if (netObj != null && netObj.IsSpawned)
                                netObj.Despawn(true);
                        }
                    }
                    catch (Exception) { }
                }
                
                AISkaters.Clear();
                RedAISkaters.Clear();
                BlueAISkaters.Clear();
                TrafficByNumber.Clear();
                PasserByNumber.Clear();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SkaterAI] Error clearing AI skaters: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Clear a specific traffic by number
        /// </summary>
        public static bool ClearTrafficByNumber(int number)
        {
            if (!TrafficByNumber.TryGetValue(number, out Player traffic))
                return false;
            
            try
            {
                if (traffic != null)
                {
                    MaxPracticePlugin.FakePlayers.Remove(traffic);
                    MaxPracticePlugin.TrafficDummies.Remove(traffic);
                    AISkaters.Remove(traffic);
                    RedAISkaters.Remove(traffic);
                    BlueAISkaters.Remove(traffic);
                    
                    // Stop playback if any
                    if (PlaybackCoroutines.TryGetValue(traffic, out var coroutine))
                    {
                        var gameMgr = NetworkBehaviourSingleton<GameManager>.Instance;
                        if (gameMgr != null && coroutine != null)
                            gameMgr.StopCoroutine(coroutine);
                        PlaybackCoroutines.Remove(traffic);
                    }
                    
                    var netObj = traffic.GetComponent<NetworkObject>();
                    if (netObj != null && netObj.IsSpawned)
                        netObj.Despawn(true);
                }
                
                TrafficByNumber.Remove(number);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SkaterAI] Error clearing traffic {number}: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Clear a specific passer by number
        /// </summary>
        public static bool ClearPasserByNumber(int number)
        {
            if (!PasserByNumber.TryGetValue(number, out Player passer))
                return false;
            
            try
            {
                if (passer != null)
                {
                    MaxPracticePlugin.FakePlayers.Remove(passer);
                    AISkaters.Remove(passer);
                    RedAISkaters.Remove(passer);
                    BlueAISkaters.Remove(passer);
                    
                    var netObj = passer.GetComponent<NetworkObject>();
                    if (netObj != null && netObj.IsSpawned)
                        netObj.Despawn(true);
                }
                
                PasserByNumber.Remove(number);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SkaterAI] Error clearing passer {number}: {ex.Message}");
                return false;
            }
        }
    }
}
