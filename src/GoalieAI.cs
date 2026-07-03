// GoalieAI.cs - Per-AI-goalie MonoBehaviour that drives PlayerInput each FixedUpdate.
// Adapted from ToastersRinkSuite reference for Puck B323 + MaxPractice. Behavior matches the
// reference 1:1 (butterfly/standing decision tree, sad reaction, idle/intermission fidgets,
// dash overshoot braking, look-RPC replication). The reference's global log filter has been
// removed — exceptions are wrapped in try/catch at the source instead of being hidden.

using System;
using System.Collections;
using Unity.Netcode;
using UnityEngine;

namespace MaxPractice
{
    public class GoalieAI : MonoBehaviour
    {
        public Player controlledPlayer;
        public PlayerTeam team;

        // B323 has no RandomGoalSpots — use the same hardcoded positions as the legacy code.
        private Vector3 redGoalPos = new Vector3(0f, 0f, -40.23f);
        private Vector3 blueGoalPos = new Vector3(0f, 0f, 40.23f);

        private PlayerBody body;
        private PlayerInput playerInput;
        private Rigidbody rb;

        private float updateInterval = 0.033f; // ~30 ticks/s
        private float nextUpdateTime = 0f;
        private float aggressionRange = 20f;
        private int _physicsResetCounter = 0;
        private float butterflyDistance = 8.0f;
        private float pokeDistance = 3.5f;
        private float minPuckSpeed = 2f;
        private float jumpHeight = 1.2f;
        private float lastPokeTime = 0f;
        private float pokeCooldown = 0.3f;
        private float goalWidth = 1.5f;

        private Puck trackedPuck = null;
        private Vector3 lastPuckPos;
        private Vector3 puckVelocity;

        private float stickSweepTime = 0f;
        private float stickSweepDuration = 0.15f;
        private bool isSweeping = false;
        private float sweepDirection = 1f;

        private float lastDashTime = 0f;
        private float dashCooldown = 0.25f;
        private float postDashStandDuration = 0.1f;
        private bool isBrakingOvershoot = false;

        private float lastJumpTime = 0f;
        private float jumpCooldown = 0.8f;

        private float noPuckReturnTimer = 0f;
        private const float NO_PUCK_RETURN_DELAY = 0.3f;
        private float slowPuckTimer = 0f;
        private const float SLOW_PUCK_IGNORE_DELAY = 1.0f;
        private float stuckBehindNetTimer = 0f;
        private const float STUCK_BEHIND_NET_TP_DELAY = 1.5f;
        private float fallenTimer = 0f;
        private const float FALLEN_RESPAWN_DELAY = 6.0f;

        private bool isRedTeam;
        private Vector3 goalPos;
        private bool isInitialized = false;
        private bool aiEnabled = false;

        private bool resetScheduled = false;

        private float idleTimer = 0f;
        private const float IDLE_DELAY = 3.5f;
        private bool isIdling = false;
        private float idlePhase = 0f;
        private int idleBehavior = 0;
        private float idleBehaviorTimer = 0f;
        private float idleBehaviorDuration = 0f;

        private bool isSad = false;
        private float sadTimer = 0f;
        private const float SAD_DURATION = 4.0f;
        private bool sadLookUp = false;

        // Celebration when own team scores. Randomly picks between two modes:
        //   0 = stick raised + waving + jumping
        //   1 = spinning in place + jumping
        private bool isCelebrating = false;
        private float celebrateTimer = 0f;
        private const float CELEBRATE_DURATION = 4.0f;
        private float celebratePhase = 0f;
        private float lastCelebrateJumpTime = 0f;
        private const float CELEBRATE_JUMP_INTERVAL = 1f;
        private int celebrateMode = 0;

        // Tracks whether the head is currently turned back to watch a puck behind the net,
        // so we know to reset LookInput when the puck comes back out front.
        private bool isLookingAtPuckBehind = false;

        private bool isIntermission = false;
        private int intermissionBehavior = -1;
        private float intermissionTimer = 0f;
        private float intermissionPhase = 0f;
        private Vector3 intermissionDirection;
        private float intermissionDashTimer = 0f;
        private bool intermissionFallen = false;

        private void Start()
        {
            try { StartCoroutine(DelayedStart()); } catch { }
        }

        private IEnumerator DelayedStart()
        {
            yield return new WaitForSeconds(1.0f);

            int attempts = 0;
            while (attempts < 20)
            {
                try { if (controlledPlayer != null && controlledPlayer.IsSpawned) break; } catch { }
                yield return new WaitForSeconds(0.1f);
                attempts++;
            }

            bool stillValid;
            try { stillValid = controlledPlayer != null && controlledPlayer.IsSpawned; }
            catch { stillValid = false; }
            if (!stillValid) { Destroy(this); yield break; }

            InitializeComponents();
            if (body == null || rb == null || playerInput == null || !isInitialized)
            {
                Destroy(this);
                yield break;
            }

            // Let NetworkVariables settle.
            yield return new WaitForSeconds(0.5f);

            bool allInputsReady = false;
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    if (playerInput.SlideInput != null &&
                        playerInput.LateralLeftInput != null &&
                        playerInput.LateralRightInput != null &&
                        playerInput.DashLeftInput != null &&
                        playerInput.DashRightInput != null &&
                        playerInput.StickRaycastOriginAngleInput != null &&
                        playerInput.JumpInput != null)
                    {
                        var _ = playerInput.SlideInput.ServerValue;
                        allInputsReady = true;
                        break;
                    }
                }
                catch { }
                yield return new WaitForSeconds(0.2f);
            }

            if (!allInputsReady) { Destroy(this); yield break; }

            ResetGoaliePhysics();
            aiEnabled = true;
        }

        private void ResetGoaliePhysics(bool logReset = true)
        {
            try
            {
                if (rb == null || body == null) return;
                rb.linearDamping = 4.0f;
                rb.angularDamping = 0.5f;

                var colliders = body.GetComponentsInChildren<Collider>();
                foreach (var col in colliders)
                {
                    if (col != null && col.material != null)
                    {
                        col.material.dynamicFriction = 0.3f;
                        col.material.staticFriction = 0.3f;
                        col.material.frictionCombine = PhysicsMaterialCombine.Average;
                    }
                }
            }
            catch { }
        }

        private void InitializeComponents()
        {
            try
            {
                if (controlledPlayer == null) return;
                body = controlledPlayer.PlayerBody;
                playerInput = controlledPlayer.PlayerInput;
                if (body != null) rb = body.GetComponent<Rigidbody>();
                UpdateTeamAlignment();
                isInitialized = true;
            }
            catch { }
        }

        private void UpdateTeamAlignment()
        {
            try
            {
                bool newIsRed = team == PlayerTeam.Red;
                if (controlledPlayer != null)
                {
                    try
                    {
                        PlayerTeam pt = controlledPlayer.Team;
                        bool playerIsRed = pt == PlayerTeam.Red;
                        if (newIsRed != playerIsRed)
                        {
                            newIsRed = playerIsRed;
                            team = playerIsRed ? PlayerTeam.Red : PlayerTeam.Blue;
                        }
                    }
                    catch { }
                }
                isRedTeam = newIsRed;
                goalPos = isRedTeam ? redGoalPos : blueGoalPos;
            }
            catch { }
        }

        private void FixedUpdate()
        {
            if (!aiEnabled) return;

            if (Time.frameCount % 100 == 0) UpdateTeamAlignment();

            try
            {
                if (controlledPlayer == null) { Destroy(this); return; }
                if (body == null) { aiEnabled = false; return; }
                if (rb == null) { aiEnabled = false; return; }
                if (playerInput == null) { aiEnabled = false; return; }
                if (!GoalieAIManager.IsAIGoalie(controlledPlayer)) { Destroy(this); return; }
                if (!controlledPlayer.IsSpawned) return;

                _physicsResetCounter++;
                if (_physicsResetCounter >= 25)
                {
                    _physicsResetCounter = 0;
                    ResetGoaliePhysics(false);
                }

                try
                {
                    if (playerInput.SlideInput == null ||
                        playerInput.LateralLeftInput == null ||
                        playerInput.LateralRightInput == null ||
                        playerInput.DashLeftInput == null ||
                        playerInput.DashRightInput == null ||
                        playerInput.StickRaycastOriginAngleInput == null)
                    {
                        return;
                    }
                }
                catch { return; }

                if (body.transform == null) return;
            }
            catch { return; }

            try
            {
                if (Time.time < nextUpdateTime) return;
                nextUpdateTime = Time.time + updateInterval;

                UpdateSadState();
                // UpdateSadState fully owns inputs during sad (slide, stick, look, move).
                // Don't ResetInputs() here or the butterfly-crouch SlideInput=true gets toggled
                // off every frame, causing visible stick/posture jitter.
                if (isSad) return;

                // Celebration when own team scores — also owns its inputs end-to-end.
                UpdateCelebrateState();
                if (isCelebrating) return;

                if (isIntermission)
                {
                    try { body.Stamina.Value = 1f; } catch { }
                    UpdateIntermission();
                    return;
                }

                Vector3 currentPos;
                try
                {
                    currentPos = body.transform.position;
                    body.Stamina.Value = 1f;
                }
                catch { return; }

                try
                {
                    if (body.HasFallen.Value || body.HasSlipped)
                    {
                        fallenTimer += updateInterval;
                        if (fallenTimer >= FALLEN_RESPAWN_DELAY)
                        {
                            Vector3 resetPos = goalPos;
                            resetPos.z += isRedTeam ? 1.2f : -1.2f;
                            resetPos.y = 0f;
                            resetPos.x = 0f;

                            Quaternion resetRot = Quaternion.LookRotation(isRedTeam ? Vector3.forward : Vector3.back);
                            body.transform.position = resetPos;
                            body.transform.rotation = resetRot;

                            if (rb != null)
                            {
                                rb.linearVelocity = Vector3.zero;
                                rb.angularVelocity = Vector3.zero;
                            }

                            body.KeepUpright.Balance = 1f;
                            body.HasFallen.Value = false;
                            body.HasSlipped = false;

                            ResetInputs();
                            fallenTimer = 0f;
                            return;
                        }
                    }
                    else
                    {
                        fallenTimer = 0f;
                    }
                }
                catch { }

                bool behindGoalLine = isRedTeam ? (currentPos.z < goalPos.z - 0.5f) : (currentPos.z > goalPos.z + 0.5f);
                bool tooFarFromCrease = Vector3.Distance(currentPos, goalPos) > 5f;
                bool tooFarLateral = Mathf.Abs(currentPos.x) > 4f;
                bool goalieOutOfPosition = behindGoalLine || tooFarFromCrease || tooFarLateral;

                if (behindGoalLine)
                {
                    stuckBehindNetTimer += updateInterval;
                    if (stuckBehindNetTimer >= STUCK_BEHIND_NET_TP_DELAY)
                    {
                        try
                        {
                            Vector3 resetPos = goalPos;
                            resetPos.z += isRedTeam ? 1.2f : -1.2f;
                            resetPos.y = 0f;
                            resetPos.x = 0f;
                            body.transform.position = resetPos;
                            body.transform.rotation = Quaternion.LookRotation(isRedTeam ? Vector3.forward : Vector3.back);
                            if (rb != null) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
                            ResetInputs();
                            stuckBehindNetTimer = 0f;
                        }
                        catch { }
                        return;
                    }
                }
                else
                {
                    stuckBehindNetTimer = 0f;
                }

                if (Vector3.Distance(currentPos, goalPos) > 10f)
                {
                    try
                    {
                        Vector3 resetPos = goalPos;
                        resetPos.z += isRedTeam ? 1.2f : -1.2f;
                        resetPos.y = 0f;
                        resetPos.x = 0f;
                        body.transform.position = resetPos;
                        body.transform.rotation = Quaternion.LookRotation(isRedTeam ? Vector3.forward : Vector3.back);
                        if (rb != null) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
                        ResetInputs();
                    }
                    catch { }
                    return;
                }

                if (goalieOutOfPosition)
                {
                    try { playerInput.StopInput.ServerValue = true; } catch { }
                    ResetInputs();
                    ReturnToCenter(currentPos, goalPos, isRedTeam);
                    return;
                }
                else
                {
                    try { playerInput.StopInput.ServerValue = false; } catch { }
                }

                Puck puck = GetClosestPuckSafe(goalPos);

                if (puck == null)
                {
                    noPuckReturnTimer += updateInterval;
                    trackedPuck = null;

                    if (noPuckReturnTimer > NO_PUCK_RETURN_DELAY)
                    {
                        ResetInputs();
                        float xBias = 0f;
                        if (TryLookAtPuckBehindNet(out Vector3 behindPos))
                        {
                            // Watching the puck behind the net — skip idle fidgeting and cheat that side.
                            xBias = ComputeBehindNetCheat(behindPos);
                        }
                        else
                        {
                            UpdateIdle();
                            if (!isIdling) ResetStickToCenter();
                        }
                        ReturnToCenter(currentPos, goalPos, isRedTeam, xBias);
                    }
                    return;
                }

                noPuckReturnTimer = 0f;
                // Don't ExitIdle() here — it zeros idleTimer every tick a puck exists, so the
                // 3.5s idle threshold never accumulates when the puck is just sitting around
                // (e.g. scattered pucks during warmup). The active-tracking block below calls
                // ExitIdle() itself when we actually start defending.

                if (trackedPuck != puck)
                {
                    trackedPuck = puck;
                    try { lastPuckPos = puck.transform.position; } catch { }
                    puckVelocity = Vector3.zero;
                }

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

                bool puckHeadingToGoal = isRedTeam ? (puckVelocity.z < -2f) : (puckVelocity.z > 2f);
                float effectiveAggressionRange = puckHeadingToGoal ? aggressionRange * 1.5f : aggressionRange;

                bool puckBehindGoalLine = isRedTeam ? (puckPos.z < goalPos.z) : (puckPos.z > goalPos.z);
                bool puckInZone = isRedTeam ? (puckPos.z < 0) : (puckPos.z > 0);
                bool puckInCrease = !puckBehindGoalLine && distToGoal < 3f;

                if (puckBehindGoalLine)
                {
                    ResetInputs();
                    ReturnToCenter(currentPos, goalPos, isRedTeam, ComputeBehindNetCheat(puckPos));
                    // Turn the head to watch the puck behind the net (body stays facing forward).
                    LookAtPuckBehindNet(puckPos, currentPos);
                    return;
                }

                if (!puckInZone || distToGoal >= effectiveAggressionRange)
                {
                    slowPuckTimer = 0f;
                    ResetInputs();
                    float xBias = 0f;
                    if (TryLookAtPuckBehindNet(out Vector3 behindPos))
                    {
                        xBias = ComputeBehindNetCheat(behindPos);
                    }
                    else
                    {
                        UpdateIdle();
                        if (!isIdling) ResetStickToCenter();
                    }
                    ReturnToCenter(currentPos, goalPos, isRedTeam, xBias);
                    return;
                }

                if (puckSpeed < minPuckSpeed && distToPuck > 3f)
                {
                    slowPuckTimer += updateInterval;
                    if (slowPuckTimer >= SLOW_PUCK_IGNORE_DELAY)
                    {
                        ResetInputs();
                        float xBias = 0f;
                        if (TryLookAtPuckBehindNet(out Vector3 behindPos))
                        {
                            xBias = ComputeBehindNetCheat(behindPos);
                        }
                        else
                        {
                            UpdateIdle();
                            if (!isIdling) ResetStickToCenter();
                        }
                        ReturnToCenter(currentPos, goalPos, isRedTeam, xBias);
                        return;
                    }
                }
                else
                {
                    slowPuckTimer = 0f;
                }

                if (puckInZone && distToGoal < effectiveAggressionRange)
                {
                    ExitIdle();
                    StopLookingAtPuckBehind(); // active threat in front — eyes forward
                    Vector3 interceptPos;

                    if (distToPuck < 0.45f)
                    {
                        Vector3 forward = body.transform.forward;
                        interceptPos = currentPos + forward * 0.15f;
                        interceptPos.y = currentPos.y;
                    }
                    else
                    {
                        Vector3 goalCenter = new Vector3(0f, 0f, goalPos.z);
                        Vector3 puckToGoal = goalCenter - puckPos;
                        puckToGoal.y = 0;
                        float puckToGoalDist = puckToGoal.magnitude;
                        if (puckToGoalDist < 0.1f) puckToGoalDist = 0.1f;

                        float comeOutDistance = Mathf.Clamp(2.5f - (distToGoal * 0.1f), 1.0f, 2.5f);
                        float ratio = Mathf.Clamp01(comeOutDistance / puckToGoalDist);
                        Vector3 interceptOnLine = Vector3.Lerp(goalCenter, puckPos, ratio);
                        float comeOutZ = isRedTeam ? (goalPos.z + comeOutDistance) : (goalPos.z - comeOutDistance);
                        interceptPos = new Vector3(interceptOnLine.x, 0, comeOutZ);
                    }

                    interceptPos.x = Mathf.Clamp(interceptPos.x, -goalWidth, goalWidth);

                    float minZ, maxZ;
                    if (isRedTeam) { minZ = goalPos.z; maxZ = goalPos.z + 3f; }
                    else { minZ = goalPos.z - 3f; maxZ = goalPos.z; }
                    interceptPos.z = Mathf.Clamp(interceptPos.z, minZ, maxZ);

                    Vector3 toIntercept = interceptPos - currentPos;
                    toIntercept.y = 0;

                    if (distToPuck < butterflyDistance)
                    {
                        float lateralX = toIntercept.x;
                        float lateralVelocity = 0f;
                        try { lateralVelocity = rb.linearVelocity.x; } catch { }

                        bool isOvershooting = Mathf.Abs(lateralX) < 1.0f &&
                                              ((lateralX > 0 && lateralVelocity > 3f) ||
                                               (lateralX < 0 && lateralVelocity < -3f));

                        if (isOvershooting) isBrakingOvershoot = true;
                        if (isBrakingOvershoot && Mathf.Abs(lateralVelocity) < 0.5f) isBrakingOvershoot = false;

                        bool isPostDashStand = (Time.time - lastDashTime) < postDashStandDuration;
                        bool isSliding = false;
                        try { isSliding = body.IsSliding.Value; } catch { }
                        bool needsCrabDash = Mathf.Abs(lateralX) > 0.3f;

                        if (!resetScheduled && distToPuck < 2.0f)
                        {
                            resetScheduled = true;
                            StartCoroutine(ResetAfterSave());
                        }

                        try { playerInput.StopInput.ServerValue = false; } catch { }

                        if (isBrakingOvershoot)
                        {
                            try { playerInput.SlideInput.ServerValue = true; } catch { }
                            try { playerInput.StopInput.ServerValue = true; } catch { }
                        }
                        else if (needsCrabDash)
                        {
                            if (isPostDashStand)
                            {
                                try { playerInput.SlideInput.ServerValue = false; } catch { }
                                try { playerInput.StopInput.ServerValue = true; } catch { }
                            }
                            else
                            {
                                try { playerInput.SlideInput.ServerValue = true; } catch { return; }
                                try { playerInput.StopInput.ServerValue = false; } catch { }

                                if (isSliding && Time.time - lastDashTime > dashCooldown)
                                {
                                    try
                                    {
                                        bool dashRight = isRedTeam ? (lateralX > 0) : (lateralX < 0);
                                        if (dashRight) body.DashRight(); else body.DashLeft();
                                        lastDashTime = Time.time;
                                    }
                                    catch { }
                                }
                            }
                        }
                        else
                        {
                            try { playerInput.SlideInput.ServerValue = true; } catch { }
                            if (Mathf.Abs(lateralVelocity) > 0.5f)
                                try { playerInput.StopInput.ServerValue = true; } catch { }
                        }
                    }
                    else
                    {
                        // Standing mode
                        float lateralX = toIntercept.x;
                        float lateralVelocity = 0f;
                        try { lateralVelocity = rb.linearVelocity.x; } catch { }

                        try
                        {
                            playerInput.LateralLeftInput.ServerValue = false;
                            playerInput.LateralRightInput.ServerValue = false;
                        }
                        catch { return; }

                        bool isOvershooting = Mathf.Abs(lateralX) < 1.5f &&
                                              ((lateralX > 0 && lateralVelocity > 3f) ||
                                               (lateralX < 0 && lateralVelocity < -3f));

                        if (isOvershooting) isBrakingOvershoot = true;
                        if (isBrakingOvershoot && Mathf.Abs(lateralVelocity) < 0.5f) isBrakingOvershoot = false;

                        bool isPostDashStand = (Time.time - lastDashTime) < postDashStandDuration;
                        bool needsCrabDash = Mathf.Abs(lateralX) > 1.0f;
                        bool isSliding = false;
                        try { isSliding = body.IsSliding.Value; } catch { }

                        try { playerInput.StopInput.ServerValue = false; } catch { }

                        if (isBrakingOvershoot)
                        {
                            try { playerInput.SlideInput.ServerValue = false; } catch { }
                            try { playerInput.StopInput.ServerValue = true; } catch { }
                        }
                        else if (needsCrabDash)
                        {
                            if (isPostDashStand)
                            {
                                try { playerInput.SlideInput.ServerValue = false; } catch { }
                                try { playerInput.StopInput.ServerValue = true; } catch { }
                            }
                            else
                            {
                                try { playerInput.SlideInput.ServerValue = true; } catch { return; }
                                try { playerInput.StopInput.ServerValue = false; } catch { }

                                if (isSliding && Time.time - lastDashTime > dashCooldown)
                                {
                                    try
                                    {
                                        bool dashRight = isRedTeam ? (lateralX > 0) : (lateralX < 0);
                                        if (dashRight) body.DashRight(); else body.DashLeft();
                                        lastDashTime = Time.time;
                                    }
                                    catch { }
                                }
                            }
                        }
                        else if (isPostDashStand)
                        {
                            try { playerInput.SlideInput.ServerValue = false; } catch { }
                            try { playerInput.StopInput.ServerValue = true; } catch { }
                        }
                        else
                        {
                            try { playerInput.SlideInput.ServerValue = false; } catch { }
                            if (Mathf.Abs(lateralVelocity) > 0.5f)
                                try { playerInput.StopInput.ServerValue = true; } catch { }
                        }

                        bool currentlySliding = false;
                        try { currentlySliding = body.IsSliding.Value; } catch { }

                        if (!currentlySliding && Mathf.Abs(toIntercept.z) > 0.1f)
                        {
                            try
                            {
                                if (toIntercept.z > 0.1f)
                                    playerInput.MoveInput.ServerValue = isRedTeam ? new Vector2(0f, 1f) : new Vector2(0f, -1f);
                                else if (toIntercept.z < -0.1f)
                                    playerInput.MoveInput.ServerValue = isRedTeam ? new Vector2(0f, -1f) : new Vector2(0f, 1f);
                            }
                            catch { return; }
                        }
                        else if (!currentlySliding)
                        {
                            try { playerInput.MoveInput.ServerValue = Vector2.zero; } catch { }
                        }
                    }

                    if (isSweeping)
                        UpdateStickSweep(puckPos, currentPos);
                    else
                        UpdateStickToTrackPuck(puckPos, currentPos);

                    float effectivePokeDistance = pokeDistance + (puckSpeed * 0.1f);
                    if (distToPuck < effectivePokeDistance && Time.time - lastPokeTime > pokeCooldown)
                    {
                        if (IsPuckDangerous(puckPos))
                        {
                            // Opposing player is right on the puck — sweep it away from danger.
                            TryPoke(puckPos, currentPos);
                            lastPokeTime = Time.time;
                        }
                        else if (distToPuck < 1.5f && puckSpeed < 4f)
                        {
                            // Safe AND puck is essentially under control — outlet pass to a teammate.
                            TryPassPuck(puckPos, currentPos);
                            lastPokeTime = Time.time;
                        }
                        // Otherwise: don't sweep. Let UpdateStickToTrackPuck keep the stick
                        // pointed at the incoming puck so the goalie intercepts on contact
                        // instead of flailing at every long shot.
                    }

                    if (puckPos.y > jumpHeight && distToPuck < 6f && puckHeadingToGoal &&
                        Time.time - lastJumpTime > jumpCooldown)
                    {
                        try
                        {
                            playerInput.SlideInput.ServerValue = false;
                            bool isSliding = false;
                            try { isSliding = body.IsSliding.Value; } catch { }
                            if (!isSliding)
                            {
                                playerInput.JumpInput.ServerValue += 1;
                                lastJumpTime = Time.time;
                            }
                        }
                        catch { }
                    }

                    // Face toward puck (in-crease) or angle toward puck from the goal line.
                    try
                    {
                        Vector3 toPuck = puckPos - currentPos;
                        toPuck.y = 0;

                        if (puckInCrease && toPuck.sqrMagnitude > 0.01f)
                        {
                            Quaternion targetRot = Quaternion.LookRotation(toPuck);
                            body.transform.rotation = Quaternion.Slerp(body.transform.rotation, targetRot, 0.08f);
                        }
                        else if (toPuck.sqrMagnitude > 0.01f)
                        {
                            Vector3 forwardDir = isRedTeam ? Vector3.forward : Vector3.back;
                            Vector3 toPuckFromGoal = puckPos - goalPos;
                            toPuckFromGoal.y = 0;
                            float angleTowardPuck = Vector3.SignedAngle(forwardDir, toPuckFromGoal.normalized, Vector3.up);
                            angleTowardPuck = Mathf.Clamp(angleTowardPuck, -45f, 45f);
                            Quaternion targetRot = Quaternion.Euler(0, isRedTeam ? angleTowardPuck : (180f + angleTowardPuck), 0);
                            body.transform.rotation = Quaternion.Slerp(body.transform.rotation, targetRot, 0.05f);
                        }
                    }
                    catch { }
                }
                else
                {
                    ResetInputs();
                    float xBias = 0f;
                    if (TryLookAtPuckBehindNet(out Vector3 behindPos))
                    {
                        xBias = ComputeBehindNetCheat(behindPos);
                    }
                    else
                    {
                        UpdateIdle();
                        if (!isIdling) ResetStickToCenter();
                    }
                    ReturnToCenter(currentPos, goalPos, isRedTeam, xBias);
                }
            }
            catch
            {
                // Swallow per-tick exceptions; we want the AI to recover rather than spam logs.
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
            catch { }
        }

        private void TryPoke(Vector3 puckPos, Vector3 currentPos)
        {
            if (playerInput == null || body == null) return;
            try
            {
                Vector3 toPuck = puckPos - currentPos;
                float angle = Vector3.SignedAngle(body.transform.forward, toPuck, Vector3.up);
                if (!isSweeping)
                {
                    isSweeping = true;
                    stickSweepTime = 0f;
                    sweepDirection = angle > 0 ? 1f : -1f;
                }
            }
            catch { }
        }

        private void UpdateStickSweep(Vector3 puckPos, Vector3 currentPos)
        {
            if (!isSweeping || playerInput == null || body == null) return;
            try
            {
                stickSweepTime += Time.fixedDeltaTime;
                float progress = stickSweepTime / stickSweepDuration;

                float sweepProgress = Mathf.Sin(progress * Mathf.PI);
                float sweepAngle = sweepDirection * (45f - sweepProgress * 90f);

                float puckHeight = puckPos.y;
                float verticalAngle;
                if (puckHeight < 0.1f) verticalAngle = 35f;
                else if (puckHeight > 1.5f) verticalAngle = -20f;
                else verticalAngle = Mathf.Lerp(35f, -20f, puckHeight / 1.5f);

                playerInput.StickRaycastOriginAngleInput.ServerValue = new Vector2(verticalAngle, sweepAngle);
                if (progress >= 1f) isSweeping = false;
            }
            catch { }
        }

        private void UpdateStickToTrackPuck(Vector3 puckPos, Vector3 currentPos)
        {
            if (playerInput == null || body == null) return;
            try
            {
                Vector3 toPuck = puckPos - currentPos;
                float horizontalAngle = Vector3.SignedAngle(body.transform.forward, new Vector3(toPuck.x, 0, toPuck.z), Vector3.up);
                horizontalAngle = Mathf.Clamp(horizontalAngle, -90f, 90f);

                float puckHeight = puckPos.y;
                float verticalAngle;
                if (puckHeight < 0.1f) verticalAngle = 35f;
                else if (puckHeight > 1.5f) verticalAngle = -20f;
                else verticalAngle = Mathf.Lerp(35f, -20f, puckHeight / 1.5f);

                playerInput.StickRaycastOriginAngleInput.ServerValue = new Vector2(verticalAngle, horizontalAngle);
            }
            catch { }
        }

        private void ResetStickToCenter()
        {
            if (playerInput == null) return;
            try { playerInput.StickRaycastOriginAngleInput.ServerValue = new Vector2(30f, 0f); } catch { }
        }

        /// <summary>
        /// Turn the head to track a puck behind the net. Body keeps facing forward (handled by
        /// ReturnToCenter); only the look angle moves. Recomputes every tick so as the body
        /// slerps back to neutral, the head stays locked on the puck's world position.
        /// LookAngleInput: x = pitch (positive looks down), y = yaw (positive looks right).
        /// </summary>
        private void LookAtPuckBehindNet(Vector3 puckPos, Vector3 currentPos)
        {
            if (playerInput == null || body == null) return;
            try
            {
                Vector3 toPuck = puckPos - currentPos;
                Vector3 toPuckFlat = new Vector3(toPuck.x, 0f, toPuck.z);
                if (toPuckFlat.sqrMagnitude < 0.0001f) return;

                // Horizontal yaw: signed angle from body forward to the puck direction.
                float yaw = Vector3.SignedAngle(body.transform.forward, toPuckFlat.normalized, Vector3.up);
                yaw = Mathf.Clamp(yaw, playerInput.MinimumLookAngle.y, playerInput.MaximumLookAngle.y);

                // Vertical pitch: positive = looking down. Puck on the ice is below eye level,
                // so the geometric angle (negative in math convention) is flipped to positive.
                const float eyeHeight = 1.5f;
                float vertDelta = puckPos.y - (currentPos.y + eyeHeight);
                float horizDist = Mathf.Max(toPuckFlat.magnitude, 0.1f);
                float pitch = -Mathf.Atan2(vertDelta, horizDist) * Mathf.Rad2Deg;
                pitch = Mathf.Clamp(pitch, playerInput.MinimumLookAngle.x, playerInput.MaximumLookAngle.x);

                playerInput.LookInput.ServerValue = true;
                playerInput.LookAngleInput.ServerValue = new Vector2(pitch, yaw);
                playerInput.Server_LookInputRpc(true, playerInput.RpcTarget.Everyone);
                short compX = NetworkingUtils.CompressFloatToShort(pitch, playerInput.MinimumLookAngle.x, playerInput.MaximumLookAngle.x);
                short compY = NetworkingUtils.CompressFloatToShort(yaw, playerInput.MinimumLookAngle.y, playerInput.MaximumLookAngle.y);
                playerInput.Server_LookAngleInputRpc(compX, compY, playerInput.RpcTarget.Everyone);
                isLookingAtPuckBehind = true;

                // Stick to the puck's side — yaw is already the body-relative angle to the puck.
                // Clamp tighter than the head (the stick physically can't rotate as far around
                // the body) and aim slightly downward since the puck is on the ice behind the net.
                float stickYaw = Mathf.Clamp(yaw, -85f, 85f);
                playerInput.StickRaycastOriginAngleInput.ServerValue = new Vector2(35f, stickYaw);
            }
            catch { }
        }

        private void StopLookingAtPuckBehind()
        {
            if (!isLookingAtPuckBehind) return;
            isLookingAtPuckBehind = false;
            try
            {
                playerInput.LookInput.ServerValue = false;
                playerInput.Server_LookInputRpc(false, playerInput.RpcTarget.Everyone);
            }
            catch { }
        }

        /// <summary>
        /// Search specifically for the closest puck BEHIND the goal line (the primary puck
        /// selector skips these because they're not threats). Limited to ~8m so the goalie
        /// doesn't twist its head trying to look at a puck on the far side of the rink.
        /// </summary>
        private Puck GetClosestPuckBehindNet(Vector3 gPos)
        {
            try
            {
                var puckManager = PuckManager.Instance;
                if (puckManager == null) return null;
                var pucks = puckManager.GetPucks(false);
                if (pucks == null || pucks.Count == 0) return null;

                Puck closest = null;
                float closestDist = float.MaxValue;

                foreach (var puck in pucks)
                {
                    if (puck == null) continue;
                    try
                    {
                        if (puck.gameObject == null || puck.transform == null) continue;
                        if (puck.IsReplay != null && puck.IsReplay.Value) continue;
                        Vector3 pp = puck.transform.position;
                        bool isBehind = isRedTeam ? (pp.z < gPos.z) : (pp.z > gPos.z);
                        if (!isBehind) continue;
                        float dist = Vector3.Distance(gPos, pp);
                        if (dist > 8f) continue;
                        if (dist < closestDist) { closestDist = dist; closest = puck; }
                    }
                    catch { continue; }
                }
                return closest;
            }
            catch { return null; }
        }

        /// <summary>
        /// Helper for idle/no-threat paths: if a puck exists behind the net, turn the head to
        /// watch it and return true (with the puck's world position via out param so callers
        /// can also bias the body toward that side). Otherwise reset any active behind-net
        /// look and return false.
        /// </summary>
        private bool TryLookAtPuckBehindNet(out Vector3 puckPosOut)
        {
            puckPosOut = Vector3.zero;
            if (body == null || body.transform == null) return false;
            Puck behind = GetClosestPuckBehindNet(goalPos);
            if (behind == null || behind.transform == null)
            {
                StopLookingAtPuckBehind();
                return false;
            }
            puckPosOut = behind.transform.position;
            LookAtPuckBehindNet(puckPosOut, body.transform.position);
            return true;
        }

        /// <summary>
        /// Compute a lateral bias toward the puck behind the net. 40% of the puck's X position,
        /// clamped to ±1.0m so the goalie still stays inside the goal width (~1.5m half-width)
        /// without exposing the far post.
        /// </summary>
        private float ComputeBehindNetCheat(Vector3 puckPos)
        {
            return Mathf.Clamp(puckPos.x * 0.4f, -1.0f, 1.0f);
        }

        private void ReturnToCenter(Vector3 currentPos, Vector3 gPos, bool redTeam, float xBias = 0f)
        {
            if (playerInput == null || rb == null || body == null) return;
            try
            {
                Vector3 targetCenter = gPos;
                targetCenter.z += redTeam ? 1.2f : -1.2f;
                targetCenter.x = xBias; // 0 = centered, ±ish = cheat toward a side

                Vector3 toCenter = targetCenter - currentPos;
                toCenter.y = 0;

                float lateralDist = toCenter.x;
                float forwardDist = Mathf.Abs(toCenter.z);
                float lateralVelocity = 0f;
                try { lateralVelocity = rb.linearVelocity.x; } catch { }

                bool isSliding = false;
                try { isSliding = body.IsSliding.Value; } catch { }

                bool isOvershooting = Mathf.Abs(lateralDist) < 1.5f &&
                                      ((lateralDist > 0 && lateralVelocity > 2f) ||
                                       (lateralDist < 0 && lateralVelocity < -2f));
                if (isOvershooting) isBrakingOvershoot = true;
                if (isBrakingOvershoot && Mathf.Abs(lateralVelocity) < 0.5f) isBrakingOvershoot = false;

                bool isPostDashStand = (Time.time - lastDashTime) < postDashStandDuration;

                if (isBrakingOvershoot || isPostDashStand)
                {
                    try { playerInput.SlideInput.ServerValue = false; } catch { }
                }
                else if (Mathf.Abs(lateralDist) > 0.5f)
                {
                    try { playerInput.SlideInput.ServerValue = true; } catch { }
                    if (isSliding && Time.time - lastDashTime > dashCooldown)
                    {
                        try
                        {
                            bool dashRight = redTeam ? (lateralDist > 0) : (lateralDist < 0);
                            if (dashRight) body.DashRight(); else body.DashLeft();
                            lastDashTime = Time.time;
                        }
                        catch { }
                    }
                }
                else
                {
                    try { playerInput.SlideInput.ServerValue = false; } catch { }
                }

                if (!isSliding && forwardDist > 0.2f)
                {
                    try
                    {
                        bool needBackward = false, needForward = false;

                        if (toCenter.z > 0.2f)
                        {
                            if (redTeam) { playerInput.MoveInput.ServerValue = new Vector2(0f, 1f); needForward = true; }
                            else { playerInput.MoveInput.ServerValue = new Vector2(0f, -1f); needBackward = true; }
                        }
                        else if (toCenter.z < -0.2f)
                        {
                            if (redTeam) { playerInput.MoveInput.ServerValue = new Vector2(0f, -1f); needBackward = true; }
                            else { playerInput.MoveInput.ServerValue = new Vector2(0f, 1f); needForward = true; }
                        }

                        if (rb != null && (needBackward || needForward))
                        {
                            float moveSpeed = 3.0f;
                            Vector3 moveDir = toCenter.normalized; moveDir.y = 0;
                            Vector3 currentVel = rb.linearVelocity;
                            Vector3 targetVel = moveDir * moveSpeed; targetVel.y = currentVel.y;
                            rb.linearVelocity = Vector3.Lerp(currentVel, targetVel, 0.1f);
                        }
                    }
                    catch { }
                }
                else if (!isSliding)
                {
                    try { playerInput.MoveInput.ServerValue = Vector2.zero; } catch { }
                }

                try
                {
                    Quaternion neutralRot = Quaternion.LookRotation(redTeam ? Vector3.forward : Vector3.back);
                    body.transform.rotation = Quaternion.Slerp(body.transform.rotation, neutralRot, 0.05f);
                }
                catch { }
            }
            catch { }
        }

        /// <summary>
        /// True if the goalie should sweep rather than try to control/intercept the puck.
        /// During Warmup (practice mode) this is always true regardless of teams — the user is
        /// shooting at the goalie and expects sweeps. In actual games, it's true when ANY
        /// non-AI opposing player is within DangerRange of the puck.
        /// </summary>
        private const float DangerRange = 25f;
        private bool IsPuckDangerous(Vector3 puckPos)
        {
            // Practice override: in warmup, every shot is "dangerous" so the goalie always sweeps.
            try
            {
                var gm = NetworkBehaviourSingleton<GameManager>.Instance;
                if (gm != null && gm.Phase == GamePhase.Warmup) return true;
            }
            catch { }

            try
            {
                var pm = PlayerManager.Instance;
                if (pm == null) return false;
                var players = pm.GetSpawnedPlayers(false);
                if (players == null) return false;

                foreach (var p in players)
                {
                    if (p == null) continue;
                    try
                    {
                        if (p.IsReplay != null && p.IsReplay.Value) continue;
                        if (p == controlledPlayer) continue;
                        if (p.Team == team) continue;            // skip teammates
                        if (p.PlayerBody == null) continue;
                        float d = Vector3.Distance(p.PlayerBody.transform.position, puckPos);
                        if (d < DangerRange) return true;
                    }
                    catch { continue; }
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Find a teammate to pass to (closest non-goalie attacker, 3-25m away). Returns null
        /// when there's nobody to pass to (e.g. single-player practice).
        /// </summary>
        private Vector3? FindPassTarget(Vector3 fromPos)
        {
            try
            {
                var pm = PlayerManager.Instance;
                if (pm == null) return null;
                var players = pm.GetSpawnedPlayers(false);
                if (players == null || players.Count == 0) return null;

                Vector3? best = null;
                float bestDist = float.MaxValue;
                foreach (var p in players)
                {
                    if (p == null) continue;
                    try
                    {
                        if (p.IsReplay != null && p.IsReplay.Value) continue;
                        if (p == controlledPlayer) continue;
                        if (p.Team != team) continue;                      // same team only
                        if (p.Role == PlayerRole.Goalie) continue;         // skip the other goalie
                        if (p.PlayerBody == null) continue;
                        Vector3 ppos = p.PlayerBody.transform.position;
                        float d = Vector3.Distance(fromPos, ppos);
                        if (d > 25f || d < 3f) continue;
                        if (d < bestDist) { bestDist = d; best = ppos; }
                    }
                    catch { continue; }
                }
                return best;
            }
            catch { return null; }
        }

        /// <summary>
        /// Outlet pass attempt: aim the stick toward the teammate and give the puck a velocity
        /// in that direction. Speed scales with distance. Only called when conditions are safe
        /// and the puck is essentially on/very-near the stick blade.
        /// </summary>
        private void TryPassPuck(Vector3 puckPos, Vector3 currentPos)
        {
            Vector3? targetOpt = FindPassTarget(currentPos);
            if (!targetOpt.HasValue) return;

            Vector3 target = targetOpt.Value;
            Vector3 toTargetFlat = new Vector3(target.x - currentPos.x, 0f, target.z - currentPos.z);
            if (toTargetFlat.sqrMagnitude < 0.01f) return;

            // Stick angle toward the teammate — clamped to physically reachable yaw.
            try
            {
                float yaw = Vector3.SignedAngle(body.transform.forward, toTargetFlat.normalized, Vector3.up);
                yaw = Mathf.Clamp(yaw, -85f, 85f);
                playerInput.StickRaycastOriginAngleInput.ServerValue = new Vector2(30f, yaw);
            }
            catch { }

            // Apply pass velocity. Faster pass for further targets, with a small lift so the
            // puck slides cleanly across the ice.
            if (trackedPuck != null && trackedPuck.Rigidbody != null)
            {
                try
                {
                    Vector3 passDir = (target - puckPos);
                    passDir.y = 0f;
                    float passDist = passDir.magnitude;
                    if (passDist > 0.1f)
                    {
                        passDir /= passDist;
                        float passSpeed = Mathf.Clamp(passDist * 1.4f, 8f, 18f);
                        trackedPuck.Rigidbody.linearVelocity = passDir * passSpeed + Vector3.up * 0.4f;
                    }
                }
                catch { }
            }
        }

        private Puck GetClosestPuckSafe(Vector3 gPos)
        {
            try
            {
                var puckManager = PuckManager.Instance;
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
                        bool puckBehindGoalLine = isRedTeam ? (puckPos.z < gPos.z) : (puckPos.z > gPos.z);
                        if (puckBehindGoalLine) continue;

                        float dist = Vector3.Distance(gPos, puckPos);
                        float puckSpeed = 0f;
                        float approachFactor = 0f;
                        if (puck.Rigidbody != null)
                        {
                            Vector3 vel = puck.Rigidbody.linearVelocity;
                            puckSpeed = vel.magnitude;
                            Vector3 toGoal = (gPos - puckPos).normalized;
                            approachFactor = Mathf.Max(0f, Vector3.Dot(vel.normalized, toGoal));
                        }

                        float lateralDist = Mathf.Abs(puckPos.x - gPos.x);
                        float depthDist = Mathf.Abs(puckPos.z - gPos.z);
                        float effectiveDist = Mathf.Sqrt(lateralDist * lateralDist * 4f + depthDist * depthDist);

                        float distScore = 100f / Mathf.Max(effectiveDist, 1f);
                        float speedScore = puckSpeed * approachFactor * 5f;
                        float score = distScore + speedScore;

                        if (score > bestScore) { bestScore = score; bestPuck = puck; }
                    }
                    catch { continue; }
                }

                return bestPuck;
            }
            catch { return null; }
        }

        private IEnumerator ResetAfterSave()
        {
            yield return new WaitForSeconds(2.0f);

            if (controlledPlayer == null || !GoalieAIManager.IsAIGoalie(controlledPlayer))
            {
                resetScheduled = false;
                yield break;
            }
            if (isSad) { resetScheduled = false; yield break; }

            try
            {
                bool isRed = team == PlayerTeam.Red;
                Vector3 gPos = isRed ? redGoalPos : blueGoalPos;
                Vector3 resetPos = gPos;
                resetPos.z += isRed ? 1.2f : -1.2f;
                resetPos.y = 0f;

                if (body != null && body.transform != null)
                {
                    body.transform.position = resetPos;
                    body.transform.rotation = Quaternion.LookRotation(isRed ? Vector3.forward : Vector3.back);
                }
                if (rb != null) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
                ResetInputs();
            }
            catch { }

            resetScheduled = false;
        }

        private void ExitIdle()
        {
            if (isIdling)
            {
                isIdling = false;
                try
                {
                    playerInput.LookInput.ServerValue = false;
                    playerInput.Server_LookInputRpc(false, playerInput.RpcTarget.Everyone);
                }
                catch { }
            }
            idleTimer = 0f;
        }

        private void UpdateIdle()
        {
            idleTimer += updateInterval;
            if (idleTimer < IDLE_DELAY) { isIdling = false; return; }

            isIdling = true;
            idlePhase += updateInterval;
            idleBehaviorTimer += updateInterval;

            if (idleBehaviorTimer >= idleBehaviorDuration)
            {
                idleBehavior = UnityEngine.Random.Range(0, 6);
                idleBehaviorDuration = UnityEngine.Random.Range(2.0f, 5.0f);
                idleBehaviorTimer = 0f;
            }

            try
            {
                float stickV = 30f, stickH = 0f, lookV = 30f, lookH = 0f;
                bool doLook = true;

                switch (idleBehavior)
                {
                    case 0:
                        stickV = 30f + Mathf.Sin(idlePhase * 1.2f) * 8f;
                        stickH = Mathf.Sin(idlePhase * 0.7f) * 35f;
                        lookH = Mathf.Sin(idlePhase * 0.3f) * 15f;
                        lookV = 32f;
                        break;
                    case 1:
                        stickV = 25f + Mathf.Sin(idlePhase * 2.0f) * 15f;
                        stickH = Mathf.Cos(idlePhase * 2.0f) * 30f;
                        lookV = 35f + Mathf.Sin(idlePhase * 0.4f) * 5f;
                        lookH = Mathf.Sin(idlePhase * 0.5f) * 10f;
                        break;
                    case 2:
                        stickV = 30f + Mathf.Sin(idlePhase * 0.6f) * 3f;
                        stickH = Mathf.Sin(idlePhase * 0.8f) * 5f;
                        lookH = Mathf.Sin(idlePhase * 1.5f) * 40f + Mathf.Sin(idlePhase * 3.2f) * 10f;
                        lookV = 28f + Mathf.Sin(idlePhase * 0.9f) * 12f;
                        break;
                    case 3:
                        stickV = 28f + Mathf.Sin(idlePhase * 1.5f) * 12f;
                        stickH = Mathf.Sin(idlePhase * 0.75f) * 35f;
                        lookH = -stickH * 0.3f;
                        lookV = 30f;
                        break;
                    case 4:
                        stickV = 30f + Mathf.Abs(Mathf.Sin(idlePhase * 3.0f)) * 10f;
                        stickH = Mathf.Sin(idlePhase * 0.4f) * 8f;
                        lookV = 30f + Mathf.Abs(Mathf.Sin(idlePhase * 3.0f)) * 5f;
                        lookH = Mathf.Sin(idlePhase * 0.6f) * 8f;
                        break;
                    case 5:
                        stickV = 30f + Mathf.Abs(Mathf.Sin(idlePhase * 8.0f)) * 18f;
                        stickH = Mathf.Sin(idlePhase * 0.3f) * 4f;
                        lookV = 40f;
                        lookH = Mathf.Sin(idlePhase * 0.5f) * 5f;
                        break;
                }

                playerInput.StickRaycastOriginAngleInput.ServerValue = new Vector2(stickV, stickH);

                if (doLook)
                {
                    playerInput.LookInput.ServerValue = true;
                    playerInput.LookAngleInput.ServerValue = new Vector2(lookV, lookH);
                    playerInput.Server_LookInputRpc(true, playerInput.RpcTarget.Everyone);
                    short compX = NetworkingUtils.CompressFloatToShort(lookV, playerInput.MinimumLookAngle.x, playerInput.MaximumLookAngle.x);
                    short compY = NetworkingUtils.CompressFloatToShort(lookH, playerInput.MinimumLookAngle.y, playerInput.MaximumLookAngle.y);
                    playerInput.Server_LookAngleInputRpc(compX, compY, playerInput.RpcTarget.Everyone);
                }
            }
            catch { }
        }

        /// <summary>
        /// Excited celebration: stick raised in the air, waving back and forth, jumping in place.
        /// Fires when the goalie's OWN team scores.
        /// </summary>
        public void TriggerCelebrate() => TriggerCelebrate(CELEBRATE_DURATION);

        public void TriggerCelebrate(float duration)
        {
            // Don't celebrate over sad — if we somehow got both (shouldn't happen), sad wins.
            if (isSad) return;
            isCelebrating = true;
            celebrateTimer = duration;
            celebratePhase = 0f;
            lastCelebrateJumpTime = 0f;
            // 50/50 between wave and spin animations.
            celebrateMode = UnityEngine.Random.value < 0.5f ? 0 : 1;
        }

        private void UpdateCelebrateState()
        {
            if (!isCelebrating) return;

            celebrateTimer -= updateInterval;
            celebratePhase += updateInterval;

            if (celebrateTimer <= 0f)
            {
                isCelebrating = false;
                try
                {
                    playerInput.LookInput.ServerValue = false;
                    playerInput.Server_LookInputRpc(false, playerInput.RpcTarget.Everyone);
                }
                catch { }
                return;
            }

            try
            {
                // Keep stamina pinned at 1 — every Jump() drains stamina, so without this the
                // goalie runs out after 2-3 jumps and stops bouncing. Also restore upright state
                // in case a prior sad reaction's 40% flop left Balance=0 / HasFallen=true.
                try { body.Stamina.Value = 1f; } catch { }
                try { if (body.KeepUpright != null) body.KeepUpright.Balance = 1f; } catch { }
                try { body.HasFallen.Value = false; body.HasSlipped = false; } catch { }

                // Zero out movement — celebrating in place.
                playerInput.MoveInput.ServerValue = Vector2.zero;
                playerInput.LateralLeftInput.ServerValue = false;
                playerInput.LateralRightInput.ServerValue = false;
                playerInput.DashLeftInput.ServerValue = 0;
                playerInput.DashRightInput.ServerValue = 0;
                playerInput.SlideInput.ServerValue = false; // standing, not butterfly
                playerInput.StopInput.ServerValue = false;

                float lookV, lookH;

                if (celebrateMode == 1)
                {
                    // SPIN mode — rotate body in place, stick held up high.
                    if (body != null && body.transform != null)
                    {
                        body.transform.Rotate(Vector3.up, 540f * updateInterval); // ~1.5 rps
                    }
                    // Stick raised, slight wobble.
                    float stickVSpin = -25f + Mathf.Sin(celebratePhase * 6f) * 5f;
                    float stickHSpin = Mathf.Sin(celebratePhase * 4f) * 20f;
                    playerInput.StickRaycastOriginAngleInput.ServerValue = new Vector2(stickVSpin, stickHSpin);
                    // Look up, head bobbing slightly.
                    lookV = -10f + Mathf.Sin(celebratePhase * 5f) * 6f;
                    lookH = 0f;
                }
                else
                {
                    // WAVE mode — stick raised high, waving back and forth.
                    float stickV = -25f + Mathf.Sin(celebratePhase * 8f) * 8f; // bob the raised stick
                    float stickH = Mathf.Sin(celebratePhase * 6f) * 60f;       // wave left/right
                    playerInput.StickRaycastOriginAngleInput.ServerValue = new Vector2(stickV, stickH);
                    // Look at the crowd, head bobs side to side.
                    lookV = -10f + Mathf.Sin(celebratePhase * 4f) * 8f;
                    lookH = Mathf.Sin(celebratePhase * 3f) * 25f;
                }

                // Look RPC replication — same for both modes.
                playerInput.LookInput.ServerValue = true;
                playerInput.LookAngleInput.ServerValue = new Vector2(lookV, lookH);
                playerInput.Server_LookInputRpc(true, playerInput.RpcTarget.Everyone);
                short compX = NetworkingUtils.CompressFloatToShort(lookV, playerInput.MinimumLookAngle.x, playerInput.MaximumLookAngle.x);
                short compY = NetworkingUtils.CompressFloatToShort(lookH, playerInput.MinimumLookAngle.y, playerInput.MaximumLookAngle.y);
                playerInput.Server_LookAngleInputRpc(compX, compY, playerInput.RpcTarget.Everyone);

                // Bounce: invoke PlayerBody.Jump() directly every CELEBRATE_JUMP_INTERVAL.
                // Going through JumpInput.ServerValue += 1 relies on the body polling the
                // input on its tick — works for in-game high-shot blocks but was sometimes
                // skipped during celebrate. Direct Jump() always fires the impulse.
                // IsJumping guard prevents stacking impulses while mid-air.
                if (Time.time - lastCelebrateJumpTime > CELEBRATE_JUMP_INTERVAL)
                {
                    bool inAir = false;
                    try { inAir = body.IsJumping; } catch { }
                    if (!inAir)
                    {
                        try { body.Jump(); } catch { }
                        try { playerInput.JumpInput.ServerValue += 1; } catch { } // also bump input for replication
                        lastCelebrateJumpTime = Time.time;
                    }
                }
            }
            catch { }
        }

        public void TriggerSad() => TriggerSad(SAD_DURATION);

        public void TriggerSad(float duration)
        {
            isSad = true;
            sadTimer = duration;

            // 40% chance to dramatically fall over.
            if (UnityEngine.Random.value < 0.4f)
            {
                try { if (body != null && body.KeepUpright != null) body.KeepUpright.Balance = 0f; } catch { }
            }
            sadLookUp = UnityEngine.Random.value < 0.3f;
        }

        private void UpdateSadState()
        {
            if (!isSad) return;
            sadTimer -= updateInterval;
            if (sadTimer <= 0f)
            {
                isSad = false;
                try
                {
                    playerInput.LookInput.ServerValue = false;
                    playerInput.Server_LookInputRpc(false, playerInput.RpcTarget.Everyone);
                }
                catch { }
                return;
            }

            try
            {
                // Zero out movement so the goalie doesn't drift while sad.
                playerInput.MoveInput.ServerValue = Vector2.zero;
                playerInput.LateralLeftInput.ServerValue = false;
                playerInput.LateralRightInput.ServerValue = false;
                playerInput.DashLeftInput.ServerValue = 0;
                playerInput.DashRightInput.ServerValue = 0;
                playerInput.StopInput.ServerValue = true;

                // Butterfly crouch.
                playerInput.SlideInput.ServerValue = true;

                // Park the stick in a neutral "down" position — otherwise it stays pointed
                // wherever the last puck-track call left it, which looks weird mid-sad.
                playerInput.StickRaycastOriginAngleInput.ServerValue = new Vector2(35f, 0f);

                // Look down (sad) or up (why me?!) and replicate via RPC.
                playerInput.LookInput.ServerValue = true;
                playerInput.Server_LookInputRpc(true, playerInput.RpcTarget.Everyone);
                Vector2 sadAngle = sadLookUp ? new Vector2(-25f, 0f) : new Vector2(75f, 0f);
                playerInput.LookAngleInput.ServerValue = sadAngle;

                short compX = NetworkingUtils.CompressFloatToShort(sadAngle.x, playerInput.MinimumLookAngle.x, playerInput.MaximumLookAngle.x);
                short compY = NetworkingUtils.CompressFloatToShort(sadAngle.y, playerInput.MinimumLookAngle.y, playerInput.MaximumLookAngle.y);
                playerInput.Server_LookAngleInputRpc(compX, compY, playerInput.RpcTarget.Everyone);
            }
            catch { }
        }

        public void TriggerIntermission()
        {
            isIntermission = true;
            intermissionTimer = 0f;
            intermissionPhase = 0f;
            intermissionDashTimer = 0f;
            intermissionFallen = false;
            ExitIdle();

            intermissionBehavior = UnityEngine.Random.Range(0, 7);
            float angle = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
            intermissionDirection = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
        }

        public void ExitIntermission()
        {
            if (!isIntermission) return;
            isIntermission = false;
            intermissionBehavior = -1;
            ResetInputs();

            if (intermissionFallen)
            {
                intermissionFallen = false;
                try
                {
                    if (body != null && body.KeepUpright != null) body.KeepUpright.Balance = 1f;
                    if (body != null) { body.HasFallen.Value = false; body.HasSlipped = false; }
                }
                catch { }
            }

            try
            {
                playerInput.LookInput.ServerValue = false;
                playerInput.Server_LookInputRpc(false, playerInput.RpcTarget.Everyone);
            }
            catch { }
        }

        private void UpdateIntermission()
        {
            if (!isIntermission || playerInput == null || body == null) return;

            intermissionTimer += updateInterval;
            intermissionPhase += updateInterval;

            try
            {
                switch (intermissionBehavior)
                {
                    case 0:
                    {
                        playerInput.SlideInput.ServerValue = false;
                        Vector2 moveDir = isRedTeam ? new Vector2(0f, 1f) : new Vector2(0f, -1f);
                        playerInput.MoveInput.ServerValue = moveDir;

                        float stickH = Mathf.Sin(intermissionPhase * 4f) * 40f;
                        playerInput.StickRaycastOriginAngleInput.ServerValue = new Vector2(25f, stickH);

                        playerInput.LookInput.ServerValue = true;
                        float lookH = Mathf.Sin(intermissionPhase * 2f) * 25f;
                        playerInput.LookAngleInput.ServerValue = new Vector2(25f, lookH);
                        playerInput.Server_LookInputRpc(true, playerInput.RpcTarget.Everyone);
                        short cx0 = NetworkingUtils.CompressFloatToShort(25f, playerInput.MinimumLookAngle.x, playerInput.MaximumLookAngle.x);
                        short cy0 = NetworkingUtils.CompressFloatToShort(lookH, playerInput.MinimumLookAngle.y, playerInput.MaximumLookAngle.y);
                        playerInput.Server_LookAngleInputRpc(cx0, cy0, playerInput.RpcTarget.Everyone);
                        break;
                    }

                    case 1:
                    {
                        playerInput.SlideInput.ServerValue = false;
                        playerInput.MoveInput.ServerValue = Vector2.zero;

                        if (rb != null) body.transform.Rotate(Vector3.up, 360f * updateInterval);

                        playerInput.StickRaycastOriginAngleInput.ServerValue = new Vector2(15f, 45f);
                        playerInput.LookInput.ServerValue = true;
                        playerInput.LookAngleInput.ServerValue = new Vector2(10f, 0f);
                        playerInput.Server_LookInputRpc(true, playerInput.RpcTarget.Everyone);
                        short cx1 = NetworkingUtils.CompressFloatToShort(10f, playerInput.MinimumLookAngle.x, playerInput.MaximumLookAngle.x);
                        short cy1 = NetworkingUtils.CompressFloatToShort(0f, playerInput.MinimumLookAngle.y, playerInput.MaximumLookAngle.y);
                        playerInput.Server_LookAngleInputRpc(cx1, cy1, playerInput.RpcTarget.Everyone);
                        break;
                    }

                    case 2:
                    {
                        if (!intermissionFallen)
                        {
                            intermissionFallen = true;
                            if (body.KeepUpright != null) body.KeepUpright.Balance = 0f;
                            if (rb != null) rb.AddForce(intermissionDirection * 2f, ForceMode.Impulse);

                            playerInput.LookInput.ServerValue = true;
                            playerInput.LookAngleInput.ServerValue = new Vector2(75f, 0f);
                            playerInput.Server_LookInputRpc(true, playerInput.RpcTarget.Everyone);
                            short cx2 = NetworkingUtils.CompressFloatToShort(75f, playerInput.MinimumLookAngle.x, playerInput.MaximumLookAngle.x);
                            short cy2 = NetworkingUtils.CompressFloatToShort(0f, playerInput.MinimumLookAngle.y, playerInput.MaximumLookAngle.y);
                            playerInput.Server_LookAngleInputRpc(cx2, cy2, playerInput.RpcTarget.Everyone);
                        }
                        ResetInputs();
                        break;
                    }

                    case 3:
                    {
                        playerInput.SlideInput.ServerValue = true;

                        float moveX = intermissionDirection.x;
                        float moveZ = intermissionDirection.z;
                        playerInput.MoveInput.ServerValue = new Vector2(
                            Mathf.Clamp(moveX, -1f, 1f),
                            Mathf.Clamp(isRedTeam ? moveZ : -moveZ, -1f, 1f));

                        intermissionDashTimer += updateInterval;
                        if (intermissionDashTimer > 0.15f)
                        {
                            intermissionDashTimer = 0f;
                            bool isSliding = false;
                            try { isSliding = body.IsSliding.Value; } catch { }
                            if (isSliding)
                            {
                                if (moveX > 0) body.DashRight(); else body.DashLeft();
                            }
                        }

                        float stickH3 = Mathf.Sin(intermissionPhase * 8f) * 50f;
                        playerInput.StickRaycastOriginAngleInput.ServerValue = new Vector2(20f, stickH3);
                        break;
                    }

                    case 4:
                    {
                        playerInput.SlideInput.ServerValue = true;

                        intermissionDashTimer += updateInterval;
                        if (intermissionDashTimer > 0.3f)
                        {
                            intermissionDashTimer = 0f;
                            bool isSliding = false;
                            try { isSliding = body.IsSliding.Value; } catch { }
                            if (isSliding)
                            {
                                if (Mathf.Sin(intermissionPhase * 3f) > 0) body.DashRight();
                                else body.DashLeft();
                            }
                        }

                        float sv4 = 20f + Mathf.Abs(Mathf.Sin(intermissionPhase * 6f)) * 20f;
                        float sh4 = Mathf.Sin(intermissionPhase * 3f) * 40f;
                        playerInput.StickRaycastOriginAngleInput.ServerValue = new Vector2(sv4, sh4);

                        playerInput.LookInput.ServerValue = true;
                        float lookV4 = 25f + Mathf.Sin(intermissionPhase * 4f) * 15f;
                        float lookH4 = Mathf.Sin(intermissionPhase * 3f) * 20f;
                        playerInput.LookAngleInput.ServerValue = new Vector2(lookV4, lookH4);
                        playerInput.Server_LookInputRpc(true, playerInput.RpcTarget.Everyone);
                        short vx4 = NetworkingUtils.CompressFloatToShort(lookV4, playerInput.MinimumLookAngle.x, playerInput.MaximumLookAngle.x);
                        short vy4 = NetworkingUtils.CompressFloatToShort(lookH4, playerInput.MinimumLookAngle.y, playerInput.MaximumLookAngle.y);
                        playerInput.Server_LookAngleInputRpc(vx4, vy4, playerInput.RpcTarget.Everyone);
                        break;
                    }

                    case 5:
                    {
                        playerInput.SlideInput.ServerValue = false;
                        playerInput.MoveInput.ServerValue = Vector2.zero;

                        float sv5 = 15f + Mathf.Sin(intermissionPhase * 4f) * 25f;
                        float sh5 = Mathf.Cos(intermissionPhase * 4f) * 50f;
                        playerInput.StickRaycastOriginAngleInput.ServerValue = new Vector2(sv5, sh5);

                        playerInput.LookInput.ServerValue = true;
                        float lookH5 = Mathf.Sin(intermissionPhase * 2.5f) * 50f;
                        float lookV5 = 20f + Mathf.Sin(intermissionPhase * 1.8f) * 20f;
                        playerInput.LookAngleInput.ServerValue = new Vector2(lookV5, lookH5);
                        playerInput.Server_LookInputRpc(true, playerInput.RpcTarget.Everyone);
                        short wx5 = NetworkingUtils.CompressFloatToShort(lookV5, playerInput.MinimumLookAngle.x, playerInput.MaximumLookAngle.x);
                        short wy5 = NetworkingUtils.CompressFloatToShort(lookH5, playerInput.MinimumLookAngle.y, playerInput.MaximumLookAngle.y);
                        playerInput.Server_LookAngleInputRpc(wx5, wy5, playerInput.RpcTarget.Everyone);
                        break;
                    }

                    case 6:
                    {
                        playerInput.MoveInput.ServerValue = Vector2.zero;
                        bool slideOn = Mathf.Sin(intermissionPhase * 5f) > 0;
                        playerInput.SlideInput.ServerValue = slideOn;

                        float sv6 = slideOn ? 40f : 20f;
                        float sh6 = Mathf.Sin(intermissionPhase * 2f) * 15f;
                        playerInput.StickRaycastOriginAngleInput.ServerValue = new Vector2(sv6, sh6);

                        playerInput.LookInput.ServerValue = true;
                        float lookV6 = slideOn ? 50f : 25f;
                        playerInput.LookAngleInput.ServerValue = new Vector2(lookV6, 0f);
                        playerInput.Server_LookInputRpc(true, playerInput.RpcTarget.Everyone);
                        short bx6 = NetworkingUtils.CompressFloatToShort(lookV6, playerInput.MinimumLookAngle.x, playerInput.MaximumLookAngle.x);
                        short by6 = NetworkingUtils.CompressFloatToShort(0f, playerInput.MinimumLookAngle.y, playerInput.MaximumLookAngle.y);
                        playerInput.Server_LookAngleInputRpc(bx6, by6, playerInput.RpcTarget.Everyone);
                        break;
                    }
                }
            }
            catch { }
        }

        private void OnDestroy()
        {
            try { ResetInputs(); } catch { }
        }
    }
}
