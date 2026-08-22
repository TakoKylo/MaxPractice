using System;
using System.Collections;
using System.Security;
using Unity.Netcode;
using UnityEngine;
using MaxPractice;

// Warmup goal detector - deletes pucks that enter goal and announces it.
// Periodically re-checks attachment so handlers survive level/scene reloads (new GoalTrigger
// instances would otherwise be left unpatched after the one-shot startup pass).
public class WarmupGoalDetector : MonoBehaviour
{
    private float nextCheckTime = 0f;
    private const float CheckInterval = 4f;
    private int handlersAttachedSoFar = 0;

    void Update()
    {
        if (Time.time < nextCheckTime) return;
        nextCheckTime = Time.time + CheckInterval;

        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;

        // The handlers this attaches only ever fire in warmup (see the phase check in
        // WarmupGoalTriggerHandler.OnTriggerEnter), so scanning for new GoalTriggers during
        // a live game buys nothing. It is not free either: EnsureHandlersAttached runs
        // FindObjectsByType<GoalTrigger> across the whole scene, and this ran it every 4 s
        // for the entire session. The comment here always claimed to gate on phase; the
        // code only ever null-checked GameManager.
        if (!PracticeHelpers.IsWarmup) return;

        EnsureHandlersAttached();
    }

    private void EnsureHandlersAttached()
    {
        try
        {
            var goalTriggers = UnityEngine.Object.FindObjectsByType<GoalTrigger>(UnityEngine.FindObjectsSortMode.None);
            if (goalTriggers == null || goalTriggers.Length == 0) return;

            foreach (var trigger in goalTriggers)
            {
                if (trigger == null) continue;
                if (trigger.gameObject.GetComponent<WarmupGoalTriggerHandler>() == null)
                {
                    trigger.gameObject.AddComponent<WarmupGoalTriggerHandler>();
                    handlersAttachedSoFar++;
                    Debug.Log($"[MaxPractice] Attached WarmupGoalTriggerHandler to '{trigger.gameObject.name}' " +
                              $"at {trigger.transform.position} (total attached so far: {handlersAttachedSoFar})");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MaxPractice] WarmupGoalDetector.EnsureHandlersAttached: {ex.Message}");
        }
    }
}

public class WarmupGoalTriggerHandler : MonoBehaviour
{
    /// <summary>
    /// Offset of the sheet this net belongs to. Zero for the arena rink's own nets,
    /// which is every net the detector finds by itself; a cloned sheet sets it when it
    /// hangs this handler off its goal mouths.
    ///
    /// Sheets deliberately use this same handler rather than one of their own. A
    /// separate implementation was written first and immediately drifted - it ate the
    /// puck on contact with no delay, never replaced it, and skipped the goalie
    /// reaction - so a clone net behaved visibly differently from the real one. Sharing
    /// the handler makes matching behaviour the default instead of something to
    /// remember.
    /// </summary>
    public Vector3 SheetOrigin = Vector3.zero;

    private bool IsCloneSheet => SheetOrigin.sqrMagnitude > 0.0001f;

    private static void DespawnPuck(Puck puck)
    {
        if (puck == null) return;

        var puckManager = MonoBehaviourSingleton<PuckManager>.Instance;
        if (puckManager != null)
        {
            puckManager.Server_DespawnPuck(puck);
            return;
        }

        if (puck.gameObject != null)
            UnityEngine.Object.Destroy(puck.gameObject);
    }

    void OnTriggerEnter(Collider collider)
    {
        try
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;

            var gm = NetworkBehaviourSingleton<GameManager>.Instance;
            if (gm == null || gm.Phase != GamePhase.Warmup) return;

            Puck puck = collider.GetComponentInParent<Puck>();
            if (puck == null) return;

            // A sheet's nets can be told to leave pucks alone; the arena's never are.
            if (IsCloneSheet && !MaxPractice.ConfigManager.Config.RinkSheetNetsEatPucks) return;

            // Count each puck once. DeletePuckAndRespawn waits 0.25 s before despawning, so
            // the puck stays live and in play for a quarter second after the goal registers -
            // a hard shot that ricochets off the back of the net and rolls straight back in
            // used to start a second, independent coroutine. The second one finds the puck
            // already gone, skips the despawn, falls through to the respawn block anyway and
            // puts a second replacement puck at centre ice for one goal (and would fire the
            // goalie reaction twice).
            if (!ClaimRespawn(puck)) return;

            Debug.Log($"[MaxPractice] Warmup goal trigger hit by puck {puck.NetworkObjectId}");

            // Notify GoalieAIManager so the AI goalie that just got scored on plays its sad
            // reaction (and the other AI, if present, celebrates). During warmup the game's
            // normal Server_NotifyGoalScoredRpc / BlueScore-RedScore phase transitions don't
            // fire, so this trigger is the only signal that a goal happened.
            // Goal trigger at z<0 = red goal (so blue team scored), at z>0 = blue goal (red scored).
            // Only the arena rink notifies: the AI goalies all stand on it, and a goal
            // scored two sheets away should not have them dropping into a sad reaction.
            if (!IsCloneSheet)
            {
                try
                {
                    PlayerTeam scoringTeam = transform.position.z < 0f ? PlayerTeam.Blue : PlayerTeam.Red;
                    MaxPractice.GoalieAIManager.OnGoalScored(scoringTeam);
                }
                catch (Exception e) { Debug.LogError($"[MaxPractice] Warmup goal reaction error: {e.Message}"); }
            }

            // Delete the puck and respawn a new one at center
            StartCoroutine(DeletePuckAndRespawn(puck));
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MaxPractice] Warmup goal trigger error: {ex}");
        }
    }

    /// <summary>
    /// Pucks with a delete-and-respawn in flight, and when that claim lapses.
    ///
    /// An EXPIRY rather than a plain set, because the coroutine cannot be relied on to
    /// release its own claim: it is started on the goal-mouth object, and Unity drops a
    /// coroutine's iterator without running its finally when the owning GameObject is
    /// destroyed. Sheet teardown (RinkSheets.Close) and level reloads both do exactly that
    /// during the 0.25 s wait, and sheet teardown does not despawn the pucks - so a plain
    /// set would blacklist that surviving puck from every net for the rest of the process.
    /// The claim outlives the 0.25 s wait by enough to cover the ricochet it exists to
    /// swallow, and heals itself either way.
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<Puck, float> _respawnInFlight =
        new System.Collections.Generic.Dictionary<Puck, float>();

    private const float RespawnClaimSeconds = 1f;

    private static bool ClaimRespawn(Puck puck)
    {
        float now = Time.unscaledTime;

        if (_respawnInFlight.TryGetValue(puck, out float heldUntil) && now < heldUntil)
            return false;

        // Sweep lapsed claims on the way past; this dictionary only ever holds pucks that
        // scored in the last second, so it stays tiny.
        if (_respawnInFlight.Count > 0)
        {
            var stale = new System.Collections.Generic.List<Puck>();
            foreach (var kv in _respawnInFlight)
                if (now >= kv.Value) stale.Add(kv.Key);
            foreach (var p in stale) _respawnInFlight.Remove(p);
        }

        _respawnInFlight[puck] = now + RespawnClaimSeconds;
        return true;
    }

    private System.Collections.IEnumerator DeletePuckAndRespawn(Puck puck)
    {
        try
        {
            yield return DeletePuckAndRespawnCore(puck);
        }
        finally
        {
            // Best-effort early release for the normal path; the expiry above is what
            // actually guarantees the claim goes away.
            _respawnInFlight.Remove(puck);
        }
    }

    private System.Collections.IEnumerator DeletePuckAndRespawnCore(Puck puck)
    {
        // Check phase immediately - if game already started, just delete and don't respawn
        var gm = NetworkBehaviourSingleton<GameManager>.Instance;
        if (gm == null || gm.Phase != GamePhase.Warmup)
        {
            DespawnPuck(puck);
            yield break;
        }
        
        yield return new WaitForSeconds(0.25f);
        
        // Check phase AGAIN after delay - game may have started during the wait
        gm = NetworkBehaviourSingleton<GameManager>.Instance;
        if (gm == null || gm.Phase != GamePhase.Warmup)
        {
            // Game started - just delete, don't respawn
            DespawnPuck(puck);
            yield break;
        }
        
        DespawnPuck(puck);

        yield return null;
        
        // Final phase check before spawning
        gm = NetworkBehaviourSingleton<GameManager>.Instance;
        if (gm == null || gm.Phase != GamePhase.Warmup)
            yield break;
        
        try
        {
            var puckManager = MonoBehaviourSingleton<PuckManager>.Instance;
            int puckCount = 0;
            if (puckManager != null)
            {
                var pucks = puckManager.GetPucks(false);
                if (pucks != null)
                {
                    // Count only this sheet's pucks. A global tally lets a busy arena
                    // rink starve a quiet sheet of its replacement puck.
                    int mySheet = MaxPractice.RinkSheets.SheetIndexAt(SheetOrigin);
                    foreach (var p in pucks)
                    {
                        if (p == null) continue;
                        if (MaxPractice.RinkSheets.SheetIndexAt(p.transform.position) == mySheet) puckCount++;
                    }
                }
            }
            
            if (puckCount < 10 && puckManager != null && Unity.Netcode.NetworkManager.Singleton.IsServer)
            {
                float randomX = UnityEngine.Random.Range(-5f, 5f);
                float randomZ = UnityEngine.Random.Range(-8f, 8f);
                // Centre ice of THIS sheet, not the arena's - the replacement puck has to
                // land in front of whoever just scored.
                Vector3 spawnPos = SheetOrigin + new Vector3(randomX, 0.04f, randomZ);
                Debug.Log($"[MaxPractice] Respawning warmup puck at {spawnPos}");
                PracticeHelpers.SpawnPuckWithCleanup(spawnPos, Quaternion.identity, Vector3.zero, false);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MaxPractice] Warmup goal respawn error: {ex}");
        }
    }
}
