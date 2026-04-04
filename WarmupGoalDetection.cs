using System;
using System.Collections;
using System.Security;
using Unity.Netcode;
using UnityEngine;
using MaxPractice;

// Warmup goal detector - deletes pucks that enter goal and announces it
public class WarmupGoalDetector : MonoBehaviour
{
    private bool isPatched = false;
    private float checkDelay = 2f;
    
    void OnEnable()
    {
        StartCoroutine(InitializeGoalDetection());
    }
    
    System.Collections.IEnumerator InitializeGoalDetection()
    {
        yield return new WaitForSeconds(checkDelay);
        int retries = 0;
        while (!isPatched && retries < 20)
        {
            var goalTriggers = UnityEngine.Object.FindObjectsByType<GoalTrigger>(UnityEngine.FindObjectsSortMode.None);
            if (goalTriggers.Length > 0)
            {
                foreach (var trigger in goalTriggers)
                {
                    if (trigger != null && trigger.gameObject.GetComponent<WarmupGoalTriggerHandler>() == null)
                    {
                        trigger.gameObject.AddComponent<WarmupGoalTriggerHandler>();
                    }
                }
                isPatched = true;
                break;
            }
            retries++;
            yield return new WaitForSeconds(0.5f);
        }
    }
}

public class WarmupGoalTriggerHandler : MonoBehaviour
{
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

            Debug.Log($"[MaxPractice] Warmup goal trigger hit by puck {puck.NetworkObjectId}");

            // Delete the puck and respawn a new one at center
            StartCoroutine(DeletePuckAndRespawn(puck));
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MaxPractice] Warmup goal trigger error: {ex}");
        }
    }

    private System.Collections.IEnumerator DeletePuckAndRespawn(Puck puck)
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
                if (pucks != null) puckCount = pucks.Count;
            }
            
            if (puckCount < 10 && puckManager != null && Unity.Netcode.NetworkManager.Singleton.IsServer)
            {
                float randomX = UnityEngine.Random.Range(-5f, 5f);
                float randomZ = UnityEngine.Random.Range(-8f, 8f);
                Vector3 spawnPos = new Vector3(randomX, 0.04f, randomZ);
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
