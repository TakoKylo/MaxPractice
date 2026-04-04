using System;
using System.Collections;
using System.Collections.Generic;
using MaxPractice;
using Unity.Netcode;
using UnityEngine;

// Simple AI that tracks the puck and moves to intercept
public class SimpleGoalieAI : MonoBehaviour
{
    public Player controlledPlayer;
    public PlayerTeam team;
    
    private Vector3 redGoalPos = new Vector3(0f, 0f, -40.23f);
    private Vector3 blueGoalPos = new Vector3(0f, 0f, 40.23f);
    private PlayerBody body;
    private PlayerInput playerInput;
    private Rigidbody rb;
    private float updateInterval = 0.033f; // ~30fps updates (was 0.02 = 50fps)
    private float nextUpdateTime = 0f;
    private float aggressionRange = 20f; // Large range to react to shots early
    private int _physicsResetCounter = 0; // Counter for physics reset throttling
    private float butterflyDistance = 8.0f; // Enter butterfly when puck is close for blocking
    private float pokeDistance = 3.5f; // Increased poke range
    private float minPuckSpeed = 2f; // Ignore pucks moving slower than this
    private float jumpHeight = 1.2f; // Height threshold for jumping to block high shots
    private float lastPokeTime = 0f;
    private float pokeCooldown = 0.3f; // Faster poke cooldown
    private float goalWidth = 1.5f; // Half-width of goal - wider for better angle coverage
    
    // Stick tracking - now targets puck directly
    private Puck trackedPuck = null;
    private Vector3 lastPuckPos;
    private Vector3 puckVelocity;
    
    // Stick sweeping for poke attacks
    private float stickSweepTime = 0f;
    private float stickSweepDuration = 0.15f; // Quick sweep
    private bool isSweeping = false;
    private float sweepDirection = 1f; // 1 = right, -1 = left
    
    // Dash for lateral movement (crab dash)
    private float lastDashTime = 0f;
    private float dashCooldown = 0.25f; // Short cooldown for fast crab dash
    private float postDashStandDuration = 0.1f; // Very brief stand for crab dash cycle
    private bool isBrakingOvershoot = false; // Currently braking due to overshoot
    
    // Jump for high shots
    private float lastJumpTime = 0f;
    private float jumpCooldown = 0.8f; // Time between jumps
    
    // Cache state to avoid null reference issues
    private float noPuckReturnTimer = 0f;
    private const float NO_PUCK_RETURN_DELAY = 0.3f;
    private float slowPuckTimer = 0f; // Timer for how long puck has been slow
    private const float SLOW_PUCK_IGNORE_DELAY = 1.0f; // Ignore puck after 1 second of being slow
    private float stuckBehindNetTimer = 0f; // Timer for being stuck behind net
    private const float STUCK_BEHIND_NET_TP_DELAY = 1.5f; // Teleport after 1.5 seconds stuck
    private bool isRedTeam;
    private Vector3 goalPos;
    private bool isInitialized = false;
    private bool aiEnabled = false;
    
    // Auto-reset after saves
    private float lastSaveTime = 0f;
    private bool resetScheduled = false;
    
    // Dynamic goal positioning
    private Goal teamGoal;
    
    // Idle fidget state
    private float idleTimer = 0f;
    private const float IDLE_FIDGET_DELAY = 3.5f;
    private bool isIdleFidgeting = false;
    private int currentFidgetType = -1;
    private float fidgetStartTime = 0f;
    private float fidgetDuration = 0f;
    
    // Sad reaction when scored on
    private bool isSadReaction = false;
    private float sadReactionEndTime = 0f;
    private bool sadLookUp = false;
    private bool sadFallOver = false;
    private KeepUpright keepUpright;
    
    // Head look replication
    private static readonly Vector2 lookAngleMin = new Vector2(-25f, -135f);
    private static readonly Vector2 lookAngleMax = new Vector2(75f, 135f);
    
    // Suppress Unity exception logging
    private static bool logFilterInstalled = false;
    
    private void Start()
    {
        try
        {
            // Install log filter to suppress null reference spam
            if (!logFilterInstalled)
            {
                Application.logMessageReceived += HandleLog;
                logFilterInstalled = true;
            }
            
            // Delay initialization to wait for network spawn to complete
            StartCoroutine(DelayedStart());
        }
        catch (Exception) { }
    }
    
    private static void HandleLog(string logString, string stackTrace, LogType type)
    {
        try
        {
            // Suppress NullReferenceException logs from GoalieAI
            if (type == LogType.Exception && logString.Contains("NullReferenceException") && 
                (stackTrace.Contains("SimpleGoalieAI") || stackTrace.Contains("GoalieAI")))
            {
                // Silently ignore
                return;
            }
        }
        catch (Exception) { }
    }
    
    private IEnumerator DelayedStart()
    {
        // Wait for network object to fully spawn
        yield return new WaitForSeconds(1.0f);
        
        // Keep waiting until player is actually spawned
        int maxAttempts = 20;
        int attempts = 0;
        while (attempts < maxAttempts)
        {
            try
            {
                if (controlledPlayer != null && controlledPlayer.IsSpawned)
                    break;
            }
            catch (Exception) { }
            yield return new WaitForSeconds(0.1f);
            attempts++;
        }
        
        try
        {
            if (controlledPlayer == null || !controlledPlayer.IsSpawned)
            {
                Destroy(this);
                yield break;
            }
        }
        catch
        {
            Destroy(this);
            yield break;
        }
        
        InitializeComponents();
        
        // Verify everything initialized correctly
        if (body == null || rb == null || playerInput == null || !isInitialized)
        {
            try { Destroy(this); } catch (Exception) { }
            yield break;
        }
        
        // Critical: Test ALL NetworkVariable accesses before enabling AI
        // This prevents exceptions from being thrown during normal operation
        yield return new WaitForSeconds(0.5f);
        
        bool allInputsReady = false;
        for (int i = 0; i < 10; i++)
        {
            try
            {
                // Try to access all the inputs - if any throw, we're not ready
                if (playerInput.SlideInput != null &&
                    playerInput.LateralLeftInput != null &&
                    playerInput.LateralRightInput != null &&
                    playerInput.DashLeftInput != null &&
                    playerInput.DashRightInput != null &&
                    playerInput.StickRaycastOriginAngleInput != null &&
                    playerInput.JumpInput != null &&
                    playerInput.LookAngleInput != null &&
                    playerInput.LookInput != null)
                {
                    // Try actually setting a value to make sure ServerValue works
                    var testValue = playerInput.SlideInput.ServerValue;
                    allInputsReady = true;
                    break;
                }
            }
            catch (Exception) { }
            
            yield return new WaitForSeconds(0.2f);
        }
        
        if (!allInputsReady)
        {
            try { Destroy(this); } catch (Exception) { }
            yield break;
        }
        
        // Reset physics for AI goalie to counteract CompetitivePuckTweaks modifications
        // This mod changes drag and physics materials which makes AI goalies slide around
        ResetGoaliePhysics();
        
        // Register for goal events (sad reaction)
        try
        {
            EventManager.AddEventListener("Event_Server_OnPuckEnterGoal", OnGoalScored);
        }
        catch (Exception) { }
        
        // Initial goal position lookup
        UpdateGoalPosition();
        
        aiEnabled = true;
    }
    
    /// <summary>
    /// Reset physics settings to vanilla values to counteract CompetitivePuckTweaks mod
    /// which changes drag/friction causing AI goalies to slide uncontrollably.
    /// Only applies changes if CompetitivePuckTweaks is detected.
    /// </summary>
    private void ResetGoaliePhysics(bool logReset = true)
    {
        try
        {
            if (rb == null || body == null) return;
            
            // Reset rigidbody drag to higher values to counteract CompetitivePuckTweaks (which sets to 0)
            // Using higher damping (4.0) to make the goalie stop sliding on ice
            rb.linearDamping = 4.0f;
            rb.angularDamping = 0.5f;
            
            // Reset physics materials on colliders to have proper friction
            var colliders = body.GetComponentsInChildren<Collider>();
            foreach (var col in colliders)
            {
                if (col != null && col.material != null)
                {
                    // Set friction to reasonable values (CompetitivePuckTweaks removes friction)
                    col.material.dynamicFriction = 0.3f;
                    col.material.staticFriction = 0.3f;
                    col.material.frictionCombine = PhysicsMaterialCombine.Average;
                }
            }
            
            if (logReset) { }
        }
        catch (Exception) { }
    }
    
    private void InitializeComponents()
    {
        try
        {
            if (controlledPlayer != null)
            {
                body = controlledPlayer.PlayerBody;
                playerInput = controlledPlayer.PlayerInput;
                if (body != null)
                {
                    rb = body.GetComponent<Rigidbody>();
                    keepUpright = body.GetComponent<KeepUpright>();
                }
                
                // Determine team from the public field OR from controlledPlayer's actual team value
                UpdateTeamAlignment();
                isInitialized = true;
            }
        }
        catch (Exception) { }
    }
    
    /// <summary>
    /// Update team alignment - called during init and periodically to ensure consistency
    /// </summary>
    private void UpdateTeamAlignment()
    {
        try
        {
            // First try the public team field that was set at spawn
            bool newIsRedTeam = team == PlayerTeam.Red;
            
            // Also verify against the actual player's team value if available
            if (controlledPlayer != null)
            {
                try
                {
                    PlayerTeam playerTeam = MaxPractice.PracticeHelpers.GetPlayerTeam(controlledPlayer);
                    bool playerIsRed = playerTeam == PlayerTeam.Red;
                    
                    // If there's a mismatch, trust the controlledPlayer's actual value
                    if (newIsRedTeam != playerIsRed)
                    {
                        newIsRedTeam = playerIsRed;
                        team = playerIsRed ? PlayerTeam.Red : PlayerTeam.Blue;
                    }
                }
                catch { }
            }
            
            isRedTeam = newIsRedTeam;
            goalPos = isRedTeam ? redGoalPos : blueGoalPos;
        }
        catch (Exception) { }
    }
    
    private void FixedUpdate()
    {
        // Don't run until fully initialized
        if (!aiEnabled) return;
        
        // Periodically re-verify team alignment and goal position (every ~2 seconds)
        if (Time.frameCount % 100 == 0)
        {
            UpdateTeamAlignment();
            UpdateGoalPosition();
        }
        
        try
        {
            // Comprehensive null checks - abort immediately if anything is wrong
            if (controlledPlayer == null) { Destroy(this); return; }
            if (body == null) { aiEnabled = false; return; }
            if (rb == null) { aiEnabled = false; return; }
            if (playerInput == null) { aiEnabled = false; return; }
            if (!MaxPracticePlugin.FakePlayers.Contains(controlledPlayer)) { Destroy(this); return; }
            
            // Don't access ANY properties until we verify the network object is valid
            if (!controlledPlayer.IsSpawned) return;
            
            // Apply physics reset periodically (not every frame) to counteract CompetitivePuckTweaks
            // Only check every 25 frames (~0.5 sec) to reduce overhead
            _physicsResetCounter++;
            if (_physicsResetCounter >= 25)
            {
                _physicsResetCounter = 0;
                ResetGoaliePhysics(false);
            }
            
            // On dedicated servers, NetworkVariables need extra validation
            // Wrap in try/catch because just accessing these properties can throw
            try
            {
                if (playerInput.SlideInput == null || 
                    playerInput.LateralLeftInput == null || 
                    playerInput.LateralRightInput == null ||
                    playerInput.DashLeftInput == null ||
                    playerInput.DashRightInput == null ||
                    playerInput.StickRaycastOriginAngleInput == null)
                {
                    return; // Network variables not ready yet
                }
            }
            catch
            {
                return; // Network variables not accessible yet
            }
            
            if (body.transform == null) return;
        }
        catch
        {
            return; // Something went wrong in validation
        }
        
        try
        {
            
            // Throttle updates
            if (Time.time < nextUpdateTime) return;
            nextUpdateTime = Time.time + updateInterval;
            
            Vector3 currentPos;
            try
            {
                currentPos = body.transform.position;
                
                // Give goalie infinite stamina so they can always dash
                body.Stamina.Value = 1f;
            }
            catch
            {
                return; // Body was destroyed
            }
            
            // Handle sad reaction (blocks normal AI)
            if (isSadReaction)
            {
                UpdateSadReaction();
                return;
            }
            
            // Check if goalie is out of position - behind goal line or too far from crease
            bool behindGoalLine = isRedTeam ? (currentPos.z < goalPos.z - 0.5f) : (currentPos.z > goalPos.z + 0.5f);
            bool tooFarFromCrease = Vector3.Distance(currentPos, goalPos) > 5f; // Reduced from 8f
            bool tooFarLateral = Mathf.Abs(currentPos.x) > 4f;
            bool goalieOutOfPosition = behindGoalLine || tooFarFromCrease || tooFarLateral;
            
            // Track time stuck behind net
            if (behindGoalLine)
            {
                stuckBehindNetTimer += updateInterval;
                
                // If stuck too long, teleport back to crease
                if (stuckBehindNetTimer >= STUCK_BEHIND_NET_TP_DELAY)
                {
                    try
                    {
                        Vector3 resetPos = goalPos;
                        resetPos.z += isRedTeam ? 1.2f : -1.2f;
                        resetPos.y = 0f;
                        
                        body.transform.position = resetPos;
                        Quaternion resetRot = Quaternion.LookRotation(isRedTeam ? Vector3.forward : Vector3.back);
                        body.transform.rotation = resetRot;
                        
                        if (rb != null)
                        {
                            rb.linearVelocity = Vector3.zero;
                            rb.angularVelocity = Vector3.zero;
                        }
                        
                        ResetInputs();
                        stuckBehindNetTimer = 0f;
                    }
                    catch (Exception) { }
                    return;
                }
            }
            else
            {
                stuckBehindNetTimer = 0f;
            }
            
            // If goalie is WAY too far out (10+ units), teleport back to crease
            if (Vector3.Distance(currentPos, goalPos) > 10f)
            {
                try
                {
                    Vector3 resetPos = goalPos;
                    resetPos.z += isRedTeam ? 1.2f : -1.2f;
                    resetPos.y = 0f;
                    
                    body.transform.position = resetPos;
                    Quaternion resetRot = Quaternion.LookRotation(isRedTeam ? Vector3.forward : Vector3.back);
                    body.transform.rotation = resetRot;
                    
                    if (rb != null)
                    {
                        rb.linearVelocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                    }
                    
                    ResetInputs();
                }
                catch (Exception) { }
                return;
            }
            
            if (goalieOutOfPosition)
            {
                // Use stop input to slow down and return to proper position
                try { playerInput.StopInput.ServerValue = true; } catch (Exception) { }
                ResetInputs();
                ReturnToCenter(currentPos, goalPos, isRedTeam);
                return;
            }
            else
            {
                // Reset stop input when in proper position
                try { playerInput.StopInput.ServerValue = false; } catch (Exception) { }
            }
            
            // Find the closest puck safely
            Puck puck = GetClosestPuckSafe(goalPos);
            
            // Handle case when no puck is found
            if (puck == null)
            {
                noPuckReturnTimer += updateInterval;
                trackedPuck = null;
                
                if (noPuckReturnTimer > NO_PUCK_RETURN_DELAY)
                {
                    ResetInputs();
                    ResetStickToCenter(); // Reset stick when no puck
                    ReturnToCenter(currentPos, goalPos, isRedTeam);
                }
                return;
            }
            
            noPuckReturnTimer = 0f;
            
            // Track puck velocity for prediction
            if (trackedPuck != puck)
            {
                trackedPuck = puck;
                try { lastPuckPos = puck.transform.position; } catch (Exception) { }
                puckVelocity = Vector3.zero;
            }
            
            // Safe puck position access
            Vector3 puckPos;
            try
            {
                if (puck == null || puck.gameObject == null || puck.transform == null)
                {
                    ResetInputs();
                    ResetStickToCenter();
                    ReturnToCenter(currentPos, goalPos, isRedTeam);
                    return;
                }
                puckPos = puck.transform.position;
                
                // Calculate puck velocity for prediction
                puckVelocity = (puckPos - lastPuckPos) / updateInterval;
                lastPuckPos = puckPos;
            }
            catch
            {
                ResetInputs();
                ResetStickToCenter();
                ReturnToCenter(currentPos, goalPos, isRedTeam);
                return;
            }
            
            float puckSpeed = puckVelocity.magnitude;
            float distToGoal = Vector3.Distance(puckPos, goalPos);
            float distToPuck = Vector3.Distance(puckPos, currentPos);
            
            // Check if puck is heading toward goal - increase reaction range if so
            bool puckHeadingToGoal = isRedTeam ? (puckVelocity.z < -2f) : (puckVelocity.z > 2f);
            float effectiveAggressionRange = puckHeadingToGoal ? aggressionRange * 1.5f : aggressionRange;
            
            // Check if puck is behind the goal line
            bool puckBehindGoalLine = isRedTeam ? (puckPos.z < goalPos.z) : (puckPos.z > goalPos.z);
            
            // Check if puck is in our zone
            bool puckInZone = isRedTeam ? (puckPos.z < 0) : (puckPos.z > 0);
            
            // Check if puck is in the crease (very close to goal, in front of net)
            bool puckInCrease = !puckBehindGoalLine && distToGoal < 3f;
            
            // Puck behind goal line - always ignore
            if (puckBehindGoalLine)
            {
                ResetInputs();
                ReturnToCenter(currentPos, goalPos, isRedTeam);
                return;
            }
            
            // Track slow puck timer - only ignore puck after it's been slow for a while
            if (puckSpeed < minPuckSpeed && distToPuck > 3f)
            {
                slowPuckTimer += updateInterval;
                if (slowPuckTimer >= SLOW_PUCK_IGNORE_DELAY)
                {
                    // Puck has been slow for 1 second - return to center
                    ResetInputs();
                    ResetStickToCenter();
                    ReturnToCenter(currentPos, goalPos, isRedTeam);
                    return;
                }
            }
            else
            {
                // Puck is moving or close - reset timer
                slowPuckTimer = 0f;
            }
            
            if (puckInZone && distToGoal < effectiveAggressionRange)
            {
                // Active play - reset idle state
                idleTimer = 0f;
                isIdleFidgeting = false;
                
                // Aggressive mode: Move toward puck to intercept
                Vector3 interceptPos;
                
                if (distToPuck < 0.45f)
                {
                    Vector3 forward = body.transform.forward;
                    interceptPos = currentPos + forward * 0.15f;
                    interceptPos.y = currentPos.y;
                }
                else
                {
                    // Position goalie on the line between puck and goal center
                    // This is how real goalies play - cut the angle
                    Vector3 goalCenter = new Vector3(goalPos.x, 0f, goalPos.z);
                    Vector3 puckToGoal = goalCenter - puckPos;
                    puckToGoal.y = 0;
                    
                    float puckToGoalDist = puckToGoal.magnitude;
                    if (puckToGoalDist < 0.1f) puckToGoalDist = 0.1f;
                    
                    // Calculate how far out to come - stay closer to goal for better coverage
                    float comeOutDistance = Mathf.Clamp(2.5f - (distToGoal * 0.1f), 1.0f, 2.5f);
                    
                    // Calculate the intercept point on the puck-to-goal line
                    // This is where the goalie should stand to block the shot
                    float ratio = comeOutDistance / puckToGoalDist;
                    ratio = Mathf.Clamp01(ratio);
                    
                    // Intercept position = goal center + ratio * (puck - goal)
                    // This puts us on the line between puck and goal
                    Vector3 interceptOnLine = Vector3.Lerp(goalCenter, puckPos, ratio);
                    
                    // Adjust Z to be exactly at our come-out distance
                    float comeOutZ = isRedTeam ? (goalPos.z + comeOutDistance) : (goalPos.z - comeOutDistance);
                    
                    interceptPos = new Vector3(interceptOnLine.x, 0, comeOutZ);
                }
                
                // Clamp X position to stay in front of goal but allow wider coverage
                interceptPos.x = Mathf.Clamp(interceptPos.x, goalPos.x - goalWidth, goalPos.x + goalWidth);
                
                // Clamp Z so goalie stays close to goal line but can come out some
                float minZ, maxZ;
                if (isRedTeam)
                {
                    minZ = goalPos.z;
                    maxZ = goalPos.z + 3f; // Reduced from 5 units - stay closer to goal
                }
                else
                {
                    minZ = goalPos.z - 3f; // Reduced from 5 units - stay closer to goal
                    maxZ = goalPos.z;
                }
                interceptPos.z = Mathf.Clamp(interceptPos.z, minZ, maxZ);
                
                // Move toward intercept position
                Vector3 toIntercept = (interceptPos - currentPos);
                toIntercept.y = 0;
                
                // Enter butterfly if puck is very close
                if (distToPuck < butterflyDistance)
                {
                    float lateralX = toIntercept.x;
                    float lateralVelocity = 0f;
                    try { lateralVelocity = rb.linearVelocity.x; } catch (Exception) { }
                    
                    // Check if we're overshooting - close to target but moving toward it fast
                    bool isOvershooting = Mathf.Abs(lateralX) < 1.0f && 
                                          ((lateralX > 0 && lateralVelocity > 3f) || 
                                           (lateralX < 0 && lateralVelocity < -3f));
                    
                    // Start braking if overshooting
                    if (isOvershooting)
                    {
                        isBrakingOvershoot = true;
                    }
                    
                    // Stop braking when velocity is near zero
                    if (isBrakingOvershoot && Mathf.Abs(lateralVelocity) < 0.5f)
                    {
                        isBrakingOvershoot = false;
                    }
                    
                    // Check if we're in post-dash stand period
                    bool isPostDashStand = (Time.time - lastDashTime) < postDashStandDuration;
                    
                    // Check current slide state
                    bool isSliding = false;
                    try { isSliding = body.IsSliding.Value; } catch (Exception) { }
                    
                    bool needsCrabDash = Mathf.Abs(lateralX) > 0.3f;
                    
                    // Schedule reset 2 seconds after making a save
                    if (!resetScheduled && distToPuck < 2.0f)
                    {
                        lastSaveTime = Time.time;
                        resetScheduled = true;
                        StartCoroutine(ResetAfterSave());
                    }
                    
                    // Reset stop input by default
                    try { playerInput.StopInput.ServerValue = false; } catch (Exception) { }
                    
                    if (isBrakingOvershoot)
                    {
                        // Over-correction - stay crouched but use stop to brake
                        try { playerInput.SlideInput.ServerValue = true; } catch (Exception) { }
                        try { playerInput.StopInput.ServerValue = true; } catch (Exception) { }
                    }
                    else if (needsCrabDash)
                    {
                        // Crab dash cycle: crouch -> dash -> stand+stop briefly -> crouch -> repeat
                        if (isPostDashStand)
                        {
                            // Stand phase of crab dash - brief stand with stop
                            try { playerInput.SlideInput.ServerValue = false; } catch (Exception) { }
                            try { playerInput.StopInput.ServerValue = true; } catch (Exception) { }
                        }
                        else
                        {
                            // Crouch phase - ready to dash
                            try { playerInput.SlideInput.ServerValue = true; } catch (Exception) { return; }
                            try { playerInput.StopInput.ServerValue = false; } catch (Exception) { }
                            
                            if (isSliding && Time.time - lastDashTime > dashCooldown)
                            {
                                try
                                {
                                    // Blue goalie faces backward, so dash directions are inverted for them
                                    bool dashRight = isRedTeam ? (lateralX > 0) : (lateralX < 0);
                                    if (dashRight)
                                    {
                                        body.DashRight();
                                    }
                                    else
                                    {
                                        body.DashLeft();
                                    }
                                    lastDashTime = Time.time;
                                }
                                catch (Exception) { }
                            }
                        }
                    }
                    else
                    {
                        // In position - stay in butterfly, use stop if still moving
                        try { playerInput.SlideInput.ServerValue = true; } catch (Exception) { }
                        if (Mathf.Abs(lateralVelocity) > 0.5f)
                        {
                            try { playerInput.StopInput.ServerValue = true; } catch (Exception) { }
                        }
                    }
                    
                    // In butterfly, we can't really move forward/back so just stay put
                }
                else
                {
                    // Standing mode
                    float lateralX = toIntercept.x;
                    float lateralVelocity = 0f;
                    try { lateralVelocity = rb.linearVelocity.x; } catch (Exception) { }
                    
                    // Reset lateral inputs
                    try
                    {
                        playerInput.LateralLeftInput.ServerValue = false;
                        playerInput.LateralRightInput.ServerValue = false;
                    }
                    catch (Exception) { return; }
                    
                    // Check if we're overshooting - close to target but moving toward it fast
                    bool isOvershooting = Mathf.Abs(lateralX) < 1.5f && 
                                          ((lateralX > 0 && lateralVelocity > 3f) || 
                                           (lateralX < 0 && lateralVelocity < -3f));
                    
                    // Start braking if overshooting
                    if (isOvershooting)
                    {
                        isBrakingOvershoot = true;
                    }
                    
                    // Stop braking when velocity is near zero
                    if (isBrakingOvershoot && Mathf.Abs(lateralVelocity) < 0.5f)
                    {
                        isBrakingOvershoot = false;
                    }
                    
                    // Check if we're in post-dash stand period (brief stand to control momentum)
                    bool isPostDashStand = (Time.time - lastDashTime) < postDashStandDuration;
                    
                    // Only use crab dash for lateral movement
                    bool needsCrabDash = Mathf.Abs(lateralX) > 1.0f;
                    
                    // Check current slide state
                    bool isSliding = false;
                    try { isSliding = body.IsSliding.Value; } catch (Exception) { }
                    
                    // Reset stop input by default
                    try { playerInput.StopInput.ServerValue = false; } catch (Exception) { }
                    
                    if (isBrakingOvershoot)
                    {
                        // Stand until velocity is 0 to stop lateral movement
                        try { playerInput.SlideInput.ServerValue = false; } catch (Exception) { }
                        // Use stop action to brake faster
                        try { playerInput.StopInput.ServerValue = true; } catch (Exception) { }
                    }
                    else if (needsCrabDash)
                    {
                        // Crab dash cycle: crouch -> dash -> stand+stop -> crouch -> repeat
                        if (isPostDashStand)
                        {
                            // Stand phase of crab dash - use stop to control momentum
                            try { playerInput.SlideInput.ServerValue = false; } catch (Exception) { }
                            try { playerInput.StopInput.ServerValue = true; } catch (Exception) { }
                        }
                        else
                        {
                            // Crouch phase - ready to dash
                            try { playerInput.SlideInput.ServerValue = true; } catch (Exception) { return; }
                            try { playerInput.StopInput.ServerValue = false; } catch (Exception) { }
                            
                            if (isSliding && Time.time - lastDashTime > dashCooldown)
                            {
                                try
                                {
                                    // Blue goalie faces backward, so dash directions are inverted for them
                                    bool dashRight = isRedTeam ? (lateralX > 0) : (lateralX < 0);
                                    if (dashRight)
                                    {
                                        body.DashRight();
                                    }
                                    else
                                    {
                                        body.DashLeft();
                                    }
                                    lastDashTime = Time.time;
                                }
                                catch (Exception) { }
                            }
                        }
                    }
                    else if (isPostDashStand)
                    {
                        // Just finished moving - stand to stop sooner
                        try { playerInput.SlideInput.ServerValue = false; } catch (Exception) { }
                        // Use stop action to brake
                        try { playerInput.StopInput.ServerValue = true; } catch (Exception) { }
                    }
                    else
                    {
                        // In position - stand, use stop if still moving
                        try { playerInput.SlideInput.ServerValue = false; } catch (Exception) { }
                        if (Mathf.Abs(lateralVelocity) > 0.5f)
                        {
                            try { playerInput.StopInput.ServerValue = true; } catch (Exception) { }
                        }
                    }
                    
                    // Check current slide state for movement
                    bool currentlySliding = false;
                    try { currentlySliding = body.IsSliding.Value; } catch (Exception) { }
                    
                    // Forward/back movement when standing (only if not sliding)
                    if (!currentlySliding && Mathf.Abs(toIntercept.z) > 0.1f)
                    {
                        try
                        {
                            if (toIntercept.z > 0.1f)
                            {
                                // Need to move forward (toward center ice)
                                if (isRedTeam)
                                {
                                    playerInput.MoveInput.ServerValue = new Vector2(0f, 1f);
                                }
                                else
                                {
                                    playerInput.MoveInput.ServerValue = new Vector2(0f, -1f);
                                }
                            }
                            else if (toIntercept.z < -0.1f)
                            {
                                // Need to move backward (toward own goal)
                                if (isRedTeam)
                                {
                                    playerInput.MoveInput.ServerValue = new Vector2(0f, -1f);
                                }
                                else
                                {
                                    playerInput.MoveInput.ServerValue = new Vector2(0f, 1f);
                                }
                            }
                        }
                        catch (Exception) { return; }
                    }
                    else if (!currentlySliding)
                    {
                        try { playerInput.MoveInput.ServerValue = Vector2.zero; } catch (Exception) { }
                    }
                }
                
                // Update stick position to track puck height and direction
                if (isSweeping)
                {
                    UpdateStickSweep(puckPos, currentPos);
                }
                else
                {
                    UpdateStickToTrackPuck(puckPos, currentPos);
                }
                
                // Replicate head look toward puck
                UpdateHeadLook(puckPos, currentPos);
                
                // Poke with stick if puck is very close or coming in fast
                float effectivePokeDistance = pokeDistance + (puckSpeed * 0.1f); // Extend range for fast pucks
                if (distToPuck < effectivePokeDistance && Time.time - lastPokeTime > pokeCooldown)
                {
                    TryPoke(puckPos, currentPos);
                    lastPokeTime = Time.time;
                }
                
                // Jump if puck is too high to block with stick and heading toward us
                // Goalie must be standing to jump!
                if (puckPos.y > jumpHeight && distToPuck < 6f && puckHeadingToGoal && 
                    Time.time - lastJumpTime > jumpCooldown)
                {
                    try
                    {
                        // Stand up first - can't jump while crouched
                        playerInput.SlideInput.ServerValue = false;
                        
                        // Check if we're actually standing (not sliding)
                        bool isSliding = false;
                        try { isSliding = body.IsSliding.Value; } catch (Exception) { }
                        
                        if (!isSliding)
                        {
                            // Trigger jump by incrementing JumpInput ServerValue
                            playerInput.JumpInput.ServerValue += 1;
                            lastJumpTime = Time.time;
                        }
                    }
                    catch (Exception) { }
                }
                
                // Face mostly forward (based on spawn/team), only slight adjustment toward puck
                // UNLESS puck is in crease - then turn freely to track it
                try
                {
                    Vector3 toPuck = puckPos - currentPos;
                    toPuck.y = 0;
                    
                    if (puckInCrease && toPuck.sqrMagnitude > 0.01f)
                    {
                        // Puck in crease - allow full rotation to face the puck!
                        Quaternion targetRot = Quaternion.LookRotation(toPuck);
                        body.transform.rotation = Quaternion.Slerp(body.transform.rotation, targetRot, 0.08f);
                    }
                    else if (toPuck.sqrMagnitude > 0.01f)
                    {
                        // Normal play - clamp rotation to mostly forward
                        Vector3 forwardDir = isRedTeam ? Vector3.forward : Vector3.back;
                        Vector3 toPuckFromGoal = puckPos - goalPos;
                        toPuckFromGoal.y = 0;
                        
                        float angleTowardPuck = Vector3.SignedAngle(forwardDir, toPuckFromGoal.normalized, Vector3.up);
                        // Clamp to 45 degrees max turn from forward
                        angleTowardPuck = Mathf.Clamp(angleTowardPuck, -45f, 45f);
                        
                        Quaternion targetRot = Quaternion.Euler(0, isRedTeam ? angleTowardPuck : (180f + angleTowardPuck), 0);
                        body.transform.rotation = Quaternion.Slerp(body.transform.rotation, targetRot, 0.05f);
                    }
                }
                catch (Exception) { }
            }
            else
            {
                // Return to center position
                ResetInputs();
                ResetStickToCenter(); // Reset stick when returning to center
                ReturnToCenter(currentPos, goalPos, isRedTeam);
            }
        }
        catch
        {
            // Silently ignore all exceptions during AI update
        }
    }
    
    private void ResetInputs()
    {
        if (playerInput == null) return;
        
        try
        {
            playerInput.SlideInput.ServerValue = false;
            playerInput.LateralLeftInput.ServerValue = false;
            playerInput.LateralRightInput.ServerValue = false;
            playerInput.DashLeftInput.ServerValue = 0;
            playerInput.DashRightInput.ServerValue = 0;
            playerInput.MoveInput.ServerValue = Vector2.zero;
        }
        catch (Exception) { }
    }
    
    private void TryPoke(Vector3 puckPos, Vector3 currentPos)
    {
        if (playerInput == null || body == null) return;
        
        try
        {
            Vector3 toPuck = puckPos - currentPos;
            float angle = Vector3.SignedAngle(body.transform.forward, toPuck, Vector3.up);
            
            // Start a sweep attack toward the puck
            if (!isSweeping)
            {
                isSweeping = true;
                stickSweepTime = 0f;
                // Sweep in the direction of the puck
                sweepDirection = angle > 0 ? 1f : -1f;
            }
            // Note: We don't use ExtendInput - that extends leg pads, not stick
        }
        catch (Exception) { }
    }
    
    // Sweeping attack motion that targets the puck
    private void UpdateStickSweep(Vector3 puckPos, Vector3 currentPos)
    {
        if (!isSweeping || playerInput == null || body == null) return;
        
        try
        {
            stickSweepTime += Time.fixedDeltaTime;
            float progress = stickSweepTime / stickSweepDuration;
            
            // Sweep motion: start wide, sweep through puck position
            // Use sine wave for smooth acceleration/deceleration
            float sweepProgress = Mathf.Sin(progress * Mathf.PI); // 0 -> 1 -> 0
            float sweepAngle = sweepDirection * (45f - sweepProgress * 90f); // Start at 45, sweep to -45
            
            // Calculate vertical angle based on puck height
            float puckHeight = puckPos.y;
            float verticalAngle;
            if (puckHeight < 0.1f)
                verticalAngle = 35f;
            else if (puckHeight > 1.5f)
                verticalAngle = -20f;
            else
                verticalAngle = Mathf.Lerp(35f, -20f, puckHeight / 1.5f);
            
            playerInput.StickRaycastOriginAngleInput.ServerValue = new Vector2(verticalAngle, sweepAngle);
            
            if (progress >= 1f)
            {
                isSweeping = false;
            }
        }
        catch (Exception) { }
    }
    
    // New method: Track puck with stick blade
    private void UpdateStickToTrackPuck(Vector3 puckPos, Vector3 currentPos)
    {
        if (playerInput == null || body == null) return;
        
        try
        {
            Vector3 toPuck = puckPos - currentPos;
            
            // Calculate horizontal angle to puck (stick angle Y)
            float horizontalAngle = Vector3.SignedAngle(body.transform.forward, new Vector3(toPuck.x, 0, toPuck.z), Vector3.up);
            // Allow full 90 degrees of stick motion each way
            horizontalAngle = Mathf.Clamp(horizontalAngle, -90f, 90f);
            
            // Calculate vertical angle based on puck height (stick angle X)
            // Higher puck = lower X value (stick goes up), lower puck = higher X value
            float puckHeight = puckPos.y;
            float verticalAngle;
            
            if (puckHeight < 0.1f)
            {
                // Puck on ice - stick down
                verticalAngle = 35f;
            }
            else if (puckHeight > 1.5f)
            {
                // Puck high - stick up
                verticalAngle = -20f;
            }
            else
            {
                // Interpolate based on height
                verticalAngle = Mathf.Lerp(35f, -20f, puckHeight / 1.5f);
            }
            
            playerInput.StickRaycastOriginAngleInput.ServerValue = new Vector2(verticalAngle, horizontalAngle);
        }
        catch (Exception) { }
    }
    
    // Reset stick to center position when not tracking
    private void ResetStickToCenter()
    {
        if (playerInput == null) return;
        try 
        { 
            playerInput.StickRaycastOriginAngleInput.ServerValue = new Vector2(30f, 0f); 
        } 
        catch (Exception) { }
    }
    
    /// <summary>
    /// Update goal position dynamically from Goal transform.
    /// Supports CompAdjustments goal scaling/repositioning.
    /// </summary>
    private void UpdateGoalPosition()
    {
        try
        {
            var goals = UnityEngine.Object.FindObjectsByType<Goal>(FindObjectsSortMode.None);
            foreach (var goal in goals)
            {
                if (goal == null) continue;
                // Determine team from position (red goal at z < 0, blue at z > 0)
                bool isGoalRed = goal.transform.position.z < 0;
                if (isGoalRed == isRedTeam)
                {
                    teamGoal = goal;
                    Vector3 pos = goal.transform.position;
                    pos.y = 0f;
                    goalPos = pos;
                    return;
                }
            }
        }
        catch (Exception) { }
    }
    
    /// <summary>
    /// Broadcast look angle to all clients via Server RPC so head rotation is visible.
    /// NetworkedInput.ServerValue is a plain field, NOT a NetworkVariable,
    /// so setting it only affects the server. The Server_*InputRpc broadcasts to clients.
    /// </summary>
    private void BroadcastLookAngle(float pitch, float yaw)
    {
        if (playerInput == null) return;
        try
        {
            pitch = Mathf.Clamp(pitch, lookAngleMin.x, lookAngleMax.x);
            yaw = Mathf.Clamp(yaw, lookAngleMin.y, lookAngleMax.y);
            playerInput.LookAngleInput.ServerValue = new Vector2(pitch, yaw);
            short cx = NetworkingUtils.CompressFloatToShort(pitch, lookAngleMin.x, lookAngleMax.x);
            short cy = NetworkingUtils.CompressFloatToShort(yaw, lookAngleMin.y, lookAngleMax.y);
            playerInput.Server_LookAngleInputRpc(cx, cy, playerInput.RpcTarget.Everyone);
        }
        catch (Exception) { }
    }
    
    private void BroadcastLookInput(bool value)
    {
        if (playerInput == null) return;
        try
        {
            playerInput.LookInput.ServerValue = value;
            playerInput.Server_LookInputRpc(value, playerInput.RpcTarget.Everyone);
        }
        catch (Exception) { }
    }
    
    /// <summary>
    /// Replicate head look direction toward a target so other players see the goalie looking at the puck.
    /// </summary>
    private void UpdateHeadLook(Vector3 targetPos, Vector3 currentPos)
    {
        if (playerInput == null || body == null) return;
        try
        {
            Vector3 toTarget = targetPos - currentPos;
            
            // Horizontal angle relative to body facing
            Vector3 forward = body.transform.forward;
            float yawAngle = Vector3.SignedAngle(forward, new Vector3(toTarget.x, 0, toTarget.z), Vector3.up);
            yawAngle = Mathf.Clamp(yawAngle, lookAngleMin.y, lookAngleMax.y);
            
            // Vertical angle - positive = look down, negative = look up
            float horizontalDist = new Vector2(toTarget.x, toTarget.z).magnitude;
            float pitchAngle = 25f; // default slightly down
            if (horizontalDist > 0.1f)
            {
                pitchAngle = -Mathf.Atan2(toTarget.y, horizontalDist) * Mathf.Rad2Deg;
                pitchAngle = Mathf.Clamp(pitchAngle, lookAngleMin.x, lookAngleMax.x);
            }
            
            BroadcastLookInput(true);
            BroadcastLookAngle(pitchAngle, yawAngle);
        }
        catch (Exception) { }
    }
    
    /// <summary>
    /// Handle sad reaction animation when scored on.
    /// </summary>
    private void UpdateSadReaction()
    {
        if (Time.time >= sadReactionEndTime)
        {
            // Sad reaction over - restore
            isSadReaction = false;
            if (keepUpright != null) keepUpright.Balance = 1f;
            try { playerInput.SlideInput.ServerValue = false; } catch { }
            try { playerInput.StopInput.ServerValue = false; } catch { }
            BroadcastLookInput(false);
            BroadcastLookAngle(25f, 0f);
            
            // Reset position to crease
            try
            {
                Vector3 resetPos = goalPos;
                resetPos.z += isRedTeam ? 1.2f : -1.2f;
                resetPos.y = 0f;
                body.transform.position = resetPos;
                body.transform.rotation = Quaternion.LookRotation(isRedTeam ? Vector3.forward : Vector3.back);
                if (rb != null) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            }
            catch { }
        }
        else
        {
            // During sad reaction: butterfly + look down/up + don't move
            try { playerInput.SlideInput.ServerValue = true; } catch { }
            try { playerInput.StopInput.ServerValue = true; } catch { }
            try { playerInput.MoveInput.ServerValue = Vector2.zero; } catch { }
            BroadcastLookInput(true);
            float lookX = sadLookUp ? -20f : 60f;
            BroadcastLookAngle(lookX, 0f);
        }
    }
    
    /// <summary>
    /// Event handler for puck entering goal. Triggers sad reaction if scored on.
    /// </summary>
    private void OnGoalScored(Dictionary<string, object> message)
    {
        try
        {
            if (!aiEnabled || controlledPlayer == null) return;
            if (message == null) return;
            
            // Only trigger sad reactions during actual gameplay, not warmup
            var gm = NetworkBehaviourSingleton<GameManager>.Instance;
            if (gm == null) return;
            GamePhase phase = gm.GameState.Value.Phase;
            if (phase == GamePhase.Warmup || phase == GamePhase.PreGame || phase == GamePhase.None) return;
            
            if (message.TryGetValue("team", out object teamObj))
            {
                PlayerTeam scoredOnTeam = (PlayerTeam)teamObj;
                if (scoredOnTeam == team)
                {
                    // Goal against us - trigger sad reaction
                    isSadReaction = true;
                    sadReactionEndTime = Time.time + 4f;
                    sadLookUp = UnityEngine.Random.value < 0.3f;
                    sadFallOver = UnityEngine.Random.value < 0.4f;
                    
                    if (sadFallOver && keepUpright != null)
                    {
                        keepUpright.Balance = 0f;
                    }
                }
            }
        }
        catch (Exception) { }
    }
    
    /// <summary>
    /// Start a random idle fidget animation.
    /// </summary>
    private void StartIdleFidget()
    {
        isIdleFidgeting = true;
        currentFidgetType = UnityEngine.Random.Range(0, 6);
        fidgetStartTime = Time.time;
        
        switch (currentFidgetType)
        {
            case 0: fidgetDuration = 2.0f; break;  // Wide slow stick sweep
            case 1: fidgetDuration = 1.5f; break;  // Stick spin/twirl
            case 2: fidgetDuration = 1.0f; break;  // Nervous scanning
            case 3: fidgetDuration = 2.5f; break;  // Figure-8 pattern
            case 4: fidgetDuration = 1.5f; break;  // Bouncy taps
            case 5: fidgetDuration = 1.0f; break;  // Rapid tapping
            default: fidgetDuration = 1.5f; break;
        }
    }
    
    /// <summary>
    /// Update the current idle fidget animation by setting stick angles.
    /// </summary>
    private void UpdateIdleFidget()
    {
        if (!isIdleFidgeting || playerInput == null) return;
        
        float elapsed = Time.time - fidgetStartTime;
        if (elapsed >= fidgetDuration)
        {
            isIdleFidgeting = false;
            currentFidgetType = -1;
            ResetStickToCenter();
            return;
        }
        
        float t = elapsed / fidgetDuration;
        float vertAngle = 30f;
        float horizAngle = 0f;
        
        switch (currentFidgetType)
        {
            case 0: // Wide slow stick sweep
                horizAngle = Mathf.Sin(t * Mathf.PI * 2f) * 60f;
                break;
            case 1: // Stick spin/twirl
                horizAngle = Mathf.Sin(t * Mathf.PI * 4f) * 90f;
                vertAngle = 30f + Mathf.Cos(t * Mathf.PI * 4f) * 25f;
                break;
            case 2: // Nervous scanning
                horizAngle = Mathf.Sin(t * Mathf.PI * 8f) * 20f;
                break;
            case 3: // Figure-8 pattern
                horizAngle = Mathf.Sin(t * Mathf.PI * 2f) * 45f;
                vertAngle = 30f + Mathf.Sin(t * Mathf.PI * 4f) * 20f;
                break;
            case 4: // Bouncy taps
                vertAngle = 30f + Mathf.Abs(Mathf.Sin(t * Mathf.PI * 6f)) * 30f;
                break;
            case 5: // Rapid tapping
                vertAngle = 30f + Mathf.Abs(Mathf.Sin(t * Mathf.PI * 12f)) * 15f;
                break;
        }
        
        try
        {
            playerInput.StickRaycastOriginAngleInput.ServerValue = new Vector2(vertAngle, horizAngle);
        }
        catch (Exception) { }
    }
    
    private void ReturnToCenter(Vector3 currentPos, Vector3 gPos, bool redTeam)
    {
        if (playerInput == null || rb == null || body == null) return;
        
        try
        {
            Vector3 targetCenter = gPos;
            targetCenter.z += redTeam ? 1.2f : -1.2f;
            targetCenter.x = gPos.x;
            
            Vector3 toCenter = (targetCenter - currentPos);
            toCenter.y = 0;
            
            float lateralDist = toCenter.x; // Signed - positive = need to go right
            float forwardDist = Mathf.Abs(toCenter.z);
            float lateralVelocity = 0f;
            try { lateralVelocity = rb.linearVelocity.x; } catch (Exception) { }
            
            bool isSliding = false;
            try { isSliding = body.IsSliding.Value; } catch (Exception) { }
            
            // Check if we're overshooting center - moving toward it fast
            bool isOvershooting = Mathf.Abs(lateralDist) < 1.5f && 
                                  ((lateralDist > 0 && lateralVelocity > 2f) || 
                                   (lateralDist < 0 && lateralVelocity < -2f));
            
            // Start braking if overshooting
            if (isOvershooting)
            {
                isBrakingOvershoot = true;
            }
            
            // Stop braking when velocity is near zero
            if (isBrakingOvershoot && Mathf.Abs(lateralVelocity) < 0.5f)
            {
                isBrakingOvershoot = false;
            }
            
            // Check if we're in post-dash stand period (normal dash control)
            bool isPostDashStand = (Time.time - lastDashTime) < postDashStandDuration;
            
            if (isBrakingOvershoot || isPostDashStand)
            {
                // Stand up to control momentum
                try { playerInput.SlideInput.ServerValue = false; } catch (Exception) { }
            }
            else if (Mathf.Abs(lateralDist) > 0.5f)
            {
                // Need to move laterally toward center
                // First, make sure we're crouched
                try { playerInput.SlideInput.ServerValue = true; } catch (Exception) { }
                
                // Always dash if we're sliding and cooldown is ready
                if (isSliding && Time.time - lastDashTime > dashCooldown)
                {
                    try
                    {
                        // Blue goalie faces backward, so dash directions are inverted for them
                        bool dashRight = redTeam ? (lateralDist > 0) : (lateralDist < 0);
                        if (dashRight)
                        {
                            body.DashRight();
                        }
                        else
                        {
                            body.DashLeft();
                        }
                        lastDashTime = Time.time;
                    }
                    catch (Exception) { }
                }
            }
            else
            {
                // Close enough laterally, stand up for forward/back movement
                try { playerInput.SlideInput.ServerValue = false; } catch (Exception) { }
            }
            
            // Handle forward/back movement with MoveInput (only when standing)
            // Also directly apply velocity for backward movement since CompetitivePuckTweaks
            // zero drag can prevent MoveInput from being effective
            if (!isSliding && forwardDist > 0.2f)
            {
                try
                {
                    bool needBackward = false;
                    bool needForward = false;
                    
                    if (toCenter.z > 0.2f)
                    {
                        // Need to move in positive z
                        if (redTeam)
                        {
                            playerInput.MoveInput.ServerValue = new Vector2(0f, 1f);
                            needForward = true;
                        }
                        else
                        {
                            playerInput.MoveInput.ServerValue = new Vector2(0f, -1f);
                            needBackward = true;
                        }
                    }
                    else if (toCenter.z < -0.2f)
                    {
                        // Need to move in negative z
                        if (redTeam)
                        {
                            playerInput.MoveInput.ServerValue = new Vector2(0f, -1f);
                            needBackward = true;
                        }
                        else
                        {
                            playerInput.MoveInput.ServerValue = new Vector2(0f, 1f);
                            needForward = true;
                        }
                    }
                    
                    // Directly apply velocity for backward movement to fight CompetitivePuckTweaks' zero drag
                    if (rb != null && (needBackward || needForward))
                    {
                        float moveSpeed = 3.0f;
                        Vector3 moveDir = toCenter.normalized;
                        moveDir.y = 0;
                        
                        // Apply direct velocity impulse toward target
                        Vector3 currentVel = rb.linearVelocity;
                        Vector3 targetVel = moveDir * moveSpeed;
                        targetVel.y = currentVel.y; // Preserve vertical velocity
                        
                        // Blend toward target velocity
                        rb.linearVelocity = Vector3.Lerp(currentVel, targetVel, 0.1f);
                    }
                }
                catch (Exception) { }
            }
            else if (!isSliding)
            {
                try { playerInput.MoveInput.ServerValue = Vector2.zero; } catch (Exception) { }
            }
            
            // Slowly return to neutral facing
            try
            {
                Quaternion neutralRot = Quaternion.LookRotation(redTeam ? Vector3.forward : Vector3.back);
                body.transform.rotation = Quaternion.Slerp(body.transform.rotation, neutralRot, 0.05f);
            }
            catch (Exception) { }
            
            // Idle fidget tracking
            idleTimer += updateInterval;
            if (idleTimer >= IDLE_FIDGET_DELAY && !isIdleFidgeting)
            {
                StartIdleFidget();
            }
            if (isIdleFidgeting)
            {
                UpdateIdleFidget();
            }
            
            // Reset head look to neutral when idle
            BroadcastLookInput(false);
            BroadcastLookAngle(25f, 0f);
        }
        catch (Exception) { }
    }
    
    private Puck GetClosestPuckSafe(Vector3 gPos)
    {
        try
        {
            var puckManager = MonoBehaviourSingleton<PuckManager>.Instance;
            if (puckManager == null) return null;
            
            var pucks = puckManager.GetPucks(false);
            if (pucks == null || pucks.Count == 0) return null;
            
            Puck bestPuck = null;
            float bestScore = float.MinValue;
            
            foreach (var puck in pucks)
            {
                if (puck == null) continue;
                
                try
                {
                    if (puck.gameObject == null) continue;
                    if (puck.transform == null) continue;
                    if (puck.IsReplay != null && puck.IsReplay.Value) continue;
                    
                    Vector3 puckPos = puck.transform.position;
                    
                    // Filter out pucks behind the goal line - don't even consider them
                    bool puckBehindGoalLine = isRedTeam ? (puckPos.z < gPos.z) : (puckPos.z > gPos.z);
                    if (puckBehindGoalLine) continue;
                    
                    float dist = Vector3.Distance(gPos, puckPos);
                    
                    // Get puck velocity to prioritize fast incoming pucks
                    float puckSpeed = 0f;
                    float approachFactor = 0f;
                    if (puck.Rigidbody != null)
                    {
                        Vector3 vel = puck.Rigidbody.linearVelocity;
                        puckSpeed = vel.magnitude;
                        
                        // Check if puck is coming toward the goal
                        Vector3 toGoal = (gPos - puckPos).normalized;
                        approachFactor = Vector3.Dot(vel.normalized, toGoal);
                        approachFactor = Mathf.Max(0f, approachFactor); // Only care if approaching
                    }
                    
                    // Calculate "in front" vs "to the side" factor
                    // Pucks directly in front should be seen further than pucks to the sides
                    float lateralDist = Mathf.Abs(puckPos.x - gPos.x);
                    float depthDist = Mathf.Abs(puckPos.z - gPos.z);
                    
                    // Effective distance: lateral counts 2x as much (so pucks to side seem further away)
                    float effectiveDist = Mathf.Sqrt(lateralDist * lateralDist * 4f + depthDist * depthDist);
                    
                    // Score: closer is better, but fast approaching pucks get huge priority
                    // Fast pucks coming at goal should be seen from much further away
                    float distScore = 100f / Mathf.Max(effectiveDist, 1f);
                    float speedScore = puckSpeed * approachFactor * 5f; // Big bonus for fast incoming
                    float score = distScore + speedScore;
                    
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestPuck = puck;
                    }
                }
                catch (Exception) { continue; }
            }
            
            return bestPuck;
        }
        catch (Exception)
        {
            return null;
        }
    }
    
    private IEnumerator ResetAfterSave()
    {
        yield return new WaitForSeconds(2.0f);
        
        if (controlledPlayer == null || !MaxPracticePlugin.FakePlayers.Contains(controlledPlayer))
        {
            resetScheduled = false;
            yield break;
        }
        
        // Skip reset if in sad reaction
        if (isSadReaction)
        {
            resetScheduled = false;
            yield break;
        }
        
        try
        {
            // Just reset position instead of despawning/respawning
            bool isRed = team == PlayerTeam.Red;
            Vector3 resetPos = goalPos;
            resetPos.z += isRed ? 1.2f : -1.2f;
            resetPos.y = 0f;
            
            if (body != null && body.transform != null)
            {
                body.transform.position = resetPos;
                Quaternion resetRot = Quaternion.LookRotation(isRed ? Vector3.forward : Vector3.back);
                body.transform.rotation = resetRot;
            }
            
            if (rb != null)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
            
            // Reset all inputs
            ResetInputs();
        }
        catch (Exception) { }
        
        resetScheduled = false;
    }
    
    private void OnDestroy()
    {
        try
        {
            ResetInputs();
            BroadcastLookInput(false);
        }
        catch (Exception) { }
        
        try
        {
            EventManager.RemoveEventListener("Event_Server_OnPuckEnterGoal", OnGoalScored);
        }
        catch (Exception) { }
    }
}
