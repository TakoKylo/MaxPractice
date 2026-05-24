// PracticeCommands.cs - Chat command handler for practice features

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MaxPractice;
using Unity.Netcode;
using UnityEngine;

[HarmonyPatch(typeof(ChatManager), "Client_SendChatMessageRpc")]
public static class PracticeCommandPatch
{
    internal const string WHITE = "<color=#FFFFFFFF>";
    internal const string ORANGE = "<color=#FF9500FF>";
    internal const string RED = "<color=#FF0000FF>";
    internal const string BLUE = "<color=#0066CCFF>";
    internal const string GREY = "<color=#808080FF>";
    internal const string GREEN = "<color=#00FF00FF>";
    internal const string B = "<b>", EB = "</b>", EC = "</color>";
    
    // Config shorthand
    private static MaxPractice.ModConfig cfg => ConfigManager.Config;

    [HarmonyPrefix]
    public static bool Prefix(ChatManager __instance, string content, bool isQuickChat, bool isTeamChat, RpcParams rpcParams)
    {
        try
        {
            if (!NetworkManager.Singleton.IsServer) return true;
            if (string.IsNullOrEmpty(content) || content[0] != '/') return true;

            // Extract clientId from RPC params
            ulong clientId = rpcParams.Receive.SenderClientId;

            // Look up the Player from clientId
            var playerManager = MonoBehaviourSingleton<PlayerManager>.Instance;
            Player player = playerManager?.GetPlayerByClientId(clientId);
            if (player == null) return true;

            // Find UIChat for legacy helper compatibility
            var ui = UnityEngine.Object.FindFirstObjectByType<UIChat>();

            string message = content;

            string[] parts = message.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
            string cmd = parts[0].ToLowerInvariant();
            // Use the same identifier as runtime ownership/lookup systems.
            ulong senderSteam = PracticeHelpers.GetSteamIdFromPlayer(player);
            if (senderSteam == 0)
                senderSteam = clientId;

            // Check for numbered clear commands (e.g., /cleartraffic1, /clearpasser2)
            bool isNumberedClearCmd = cmd.StartsWith("/cleartraffic") || cmd.StartsWith("/clearpasser");

            // /votegoalies — toggle AI goalies on/off via majority vote. Works in any phase (not warmup-gated).
            if (cmd == "/votegoalies" || cmd == "/vg")
            {
                HandleVoteGoalies(ui, player, clientId);
                return false;
            }

            bool practiceCmd = cmd == "/infinitestamina" || cmd == "/is" ||
                         cmd == "/spawnpuck" || cmd == "/s" ||
                         cmd == "/clearpucks" || cmd == "/clear" || cmd == "/c" || cmd == "/emptypucks" ||
                         cmd == "/clearall" || cmd == "/emptyall" ||
                         cmd == "/clearminefield" || cmd == "/clearcones" ||
                         cmd == "/dummy" || cmd == "/dummie" ||
                         cmd == "/dummyred" || cmd == "/dummyblue" ||
                         cmd == "/removedummy" ||
                         cmd == "/minefield" || cmd == "/cones" ||
                         cmd == "/backpass" || cmd == "/bp" ||
                         cmd == "/pass" || cmd == "/unpass" ||
                         cmd == "/tappass" || cmd == "/tapspawn" || cmd == "/tapyoyo" || cmd == "/tapbackpass" ||
                         cmd == "/traffic" || cmd == "/cleartraffic" ||
                         cmd == "/recordtraffic" || cmd == "/trafficrecord" || cmd == "/stoprecord" ||
                         cmd == "/passer" || cmd == "/clearpasser" ||
                         cmd == "/yoyo" ||
                         cmd == "/saveprac" || cmd == "/stopsaveprac" ||
                         cmd == "/tipprac" || cmd == "/stoptipprac" ||
                         cmd == "/pop" ||
                         cmd == "/practice" ||
                         isNumberedClearCmd;

            if (practiceCmd)
                return HandlePracticeCommand(ui, player, clientId, cmd, senderSteam, parts);

            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[MaxPractice] Error in command handler: {e}");
            return true;
        }
    }

    private static void SendPrivate(UIChat ui, ulong clientId, string msg)
    {
        // Chat disabled for B310 API compatibility
        PracticeHelpers.SendChatMessage(ui, "<size=70%>" + msg + "</size>", clientId);
    }

    private static void Broadcast(UIChat ui, string msg)
    {
        // Chat disabled for B310 API compatibility
        PracticeHelpers.SendChatMessage(ui, "<size=70%>" + msg + "</size>");
    }

    private static string TeamColor(Player p)
    {
        if (p == null) return GREY;
        switch (PracticeHelpers.GetPlayerTeam(p))
        {
            case PlayerTeam.Red: return RED;
            case PlayerTeam.Blue: return BLUE;
            default: return GREY;
        }
    }

    private static bool HandlePracticeCommand(UIChat ui, Player player, ulong clientId, string cmd, ulong senderSteam, string[] parts)
    {
        try
        {
            var gm = NetworkBehaviourSingleton<GameManager>.Instance;
            if (gm == null || gm.Phase != GamePhase.Warmup)
            {
                SendPrivate(ui, clientId, WHITE + "This command is only available during warmup phase." + EC);
                return false;
            }

            // /practice - list commands
            if (cmd == "/practice")
            {
                SendPrivate(ui, clientId, ORANGE + B + "Practice Commands (Warmup Only):" + EB + EC);
                SendPrivate(ui, clientId, WHITE + "/saveprac" + EC + GREY + " - Toggle save practice (2 min, shoots at your net)" + EC);
                SendPrivate(ui, clientId, WHITE + "/tipprac" + EC + GREY + " - Toggle tip practice (pucks fly through crease to redirect)" + EC);
                SendPrivate(ui, clientId, WHITE + "/dummyred" + EC + GREY + " - Toggle AI goalie for red team" + EC);
                SendPrivate(ui, clientId, WHITE + "/dummyblue" + EC + GREY + " - Toggle AI goalie for blue team" + EC);
                SendPrivate(ui, clientId, WHITE + "/dummy" + EC + GREY + " - Toggle AI goalie for your team" + EC);
                SendPrivate(ui, clientId, WHITE + "/minefield" + EC + GREY + " - Spawn 10 random pucks around you for stickhandling" + EC);
                SendPrivate(ui, clientId, WHITE + "/cones" + EC + GREY + " - Spawn 5 cones (pucks) in a line in front of you" + EC);
                SendPrivate(ui, clientId, WHITE + "/spawnpuck (/s)" + EC + GREY + " - Spawn a puck above your stick" + EC);
                SendPrivate(ui, clientId, WHITE + "/backpass (/bp)" + EC + GREY + " - Spawn puck behind you that passes to your stick" + EC);
                SendPrivate(ui, clientId, WHITE + "/recordtraffic" + EC + GREY + " - Start recording your movement for traffic" + EC);
                SendPrivate(ui, clientId, WHITE + "/stoprecord" + EC + GREY + " - Stop recording and save for /traffic" + EC);
                SendPrivate(ui, clientId, WHITE + "/traffic" + EC + GREY + " - Spawn traffic (5s delay, plays recording if exists)" + EC);
                SendPrivate(ui, clientId, WHITE + "/cleartraffic" + EC + GREY + " - Remove all traffic dummies" + EC);
                SendPrivate(ui, clientId, WHITE + "/clearpucks" + EC + GREY + " - Clear all pucks and spawn one per player" + EC);
                SendPrivate(ui, clientId, WHITE + "/infinitestamina (/is)" + EC + GREY + " - Toggle infinite stamina" + EC);
                SendPrivate(ui, clientId, WHITE + "/yoyo" + EC + GREY + " - Yank stick back after a shot to return puck to blade" + EC);
                SendPrivate(ui, clientId, WHITE + "/pass [fast|slow|lob]" + EC + GREY + " - Set pass point, then /pass to spawn passes" + EC);
                SendPrivate(ui, clientId, WHITE + "/unpass" + EC + GREY + " - Clear pass point" + EC);
                SendPrivate(ui, clientId, WHITE + "/pop" + EC + GREY + " - Pop the last puck you touched upward" + EC);
                SendPrivate(ui, clientId, WHITE + "/tappass [fast|slow]" + EC + GREY + " - Toggle tap pass (tap stick 3x for pass)" + EC);
                SendPrivate(ui, clientId, WHITE + "/tapspawn" + EC + GREY + " - Toggle tap spawn (tap stick 3x to spawn puck)" + EC);
                SendPrivate(ui, clientId, WHITE + "/tapyoyo" + EC + GREY + " - Toggle tap yoyo (tap stick 3x to return puck)" + EC);
                return false;
            }
            // /infinitestamina
            if (cmd == "/infinitestamina" || cmd == "/is")
            {
                if (!cfg.EnableInfiniteStamina)
                {
                    return false;
                }
                if (MaxPracticePlugin.InfiniteStaminaPlayers.Contains(senderSteam))
                {
                    MaxPracticePlugin.InfiniteStaminaPlayers.Remove(senderSteam);
                    SendPrivate(ui, clientId, WHITE + "Infinite stamina " + RED + B + "disabled" + EB + EC + WHITE + "." + EC);
                }
                else
                {
                    MaxPracticePlugin.InfiniteStaminaPlayers.Add(senderSteam);
                    SendPrivate(ui, clientId, WHITE + "Infinite stamina " + GREEN + B + "enabled" + EB + EC + WHITE + "." + EC);
                }
                return false;
            }

            // /yoyo - toggle yoyo mode
            if (cmd == "/yoyo")
            {
                if (!cfg.EnableYoyo)
                {
                    return false;
                }
                if (MaxPracticePlugin.YoyoPlayers.Contains(senderSteam))
                {
                    MaxPracticePlugin.YoyoPlayers.Remove(senderSteam);
                    SendPrivate(ui, clientId, WHITE + "Yoyo mode " + RED + B + "disabled" + EB + EC + WHITE + "." + EC);
                }
                else
                {
                    MaxPracticePlugin.YoyoPlayers.Add(senderSteam);
                    SendPrivate(ui, clientId, WHITE + "Yoyo mode " + GREEN + B + "enabled" + EB + EC + WHITE + " - Shoot a puck, then yank your stick back to return it!" + EC);
                }
                return false;
            }

            // /spawnpuck
            if (cmd == "/spawnpuck" || cmd == "/s")
            {
                if (!cfg.EnableSpawnPuck)
                {
                    return false;
                }
                float currentTime = Time.realtimeSinceStartup;
                if (MaxPracticePlugin.LastPuckSpawnTime.TryGetValue(senderSteam, out float lastSpawnTime))
                {
                    float cooldown = PracticeConstants.PuckSpawnCooldown;
                    if (currentTime - lastSpawnTime < cooldown)
                    {
                        SendPrivate(ui, clientId, RED + $"Puck spawn cooldown: {cooldown - (currentTime - lastSpawnTime):F1}s remaining" + EC);
                        return false;
                    }
                }

                if (!PracticeHelpers.IsCharacterSpawned(player) || player.Stick == null)
                {
                    SendPrivate(ui, clientId, WHITE + "Could not find player stick." + EC);
                    return false;
                }

                var playerBody = player.PlayerBody;
                var rb = playerBody != null ? playerBody.GetComponent<Rigidbody>() : null;
                Vector3 playerVelocity = rb != null ? rb.linearVelocity : Vector3.zero;
                Vector3 spawnPos = player.Stick.BladeHandlePosition + Vector3.up * 0.5f;
                var spawnedPuck = PracticeHelpers.SpawnPuckWithCleanup(spawnPos, Quaternion.identity, playerVelocity, false);
                if (spawnedPuck != null)
                    MaxPractice.YoyoManager.RegisterPuckForPlayer(spawnedPuck, player);
                MaxPracticePlugin.LastPuckSpawnTime[senderSteam] = currentTime;
                SendPrivate(ui, clientId, WHITE + "Puck spawned." + EC);
                return false;
            }

            // /backpass - spawn puck behind player and shoot toward their stick
            if (cmd == "/backpass" || cmd == "/bp")
            {
                if (!cfg.EnableBackpass)
                {
                    return false;
                }
                float currentTime = Time.realtimeSinceStartup;
                if (MaxPracticePlugin.LastPuckSpawnTime.TryGetValue(senderSteam, out float lastSpawnTime))
                {
                    float cooldown = PracticeConstants.PuckSpawnCooldown;
                    if (currentTime - lastSpawnTime < cooldown)
                    {
                        SendPrivate(ui, clientId, RED + $"Backpass cooldown: {cooldown - (currentTime - lastSpawnTime):F1}s remaining" + EC);
                        return false;
                    }
                }

                if (!PracticeHelpers.IsCharacterSpawned(player) || player.Stick == null || player.PlayerBody == null)
                {
                    SendPrivate(ui, clientId, WHITE + "Could not find player." + EC);
                    return false;
                }

                var playerBody2 = player.PlayerBody;
                Vector3 stickPos = player.Stick.BladeHandlePosition;
                
                // Spawn puck behind player using config distance
                Vector3 behindPlayer = playerBody2.transform.position - playerBody2.transform.forward * PracticeConstants.BackpassDistance;
                behindPlayer.y = 0.05f; // Just above ice
                
                // Clamp spawn position to stay within actual ice bounds
                /* B310: LevelManager was restructured. Bounds clamping disabled.
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
                if (MaxPractice.YoyoManager.Instance != null)
                {
                    Vector3? ballisticVel = MaxPractice.YoyoManager.Instance.CalculateBallisticVelocityPublic(behindPlayer, stickPos, PracticeConstants.BackpassSpeed, false);
                    passVelocity = ballisticVel ?? (stickPos - behindPlayer).normalized * PracticeConstants.BackpassSpeed;
                }
                else
                {
                    Vector3 direction = (stickPos - behindPlayer).normalized;
                    passVelocity = direction * PracticeConstants.BackpassSpeed;
                }
                
                Puck spawnedPuck = PracticeHelpers.SpawnPuckWithCleanup(behindPlayer, Quaternion.identity, passVelocity, false);
                
                // Register puck with player's stick collision buffer so they can track it
                if (spawnedPuck != null)
                {
                    MaxPractice.YoyoManager.RegisterPuckForPlayer(spawnedPuck, player);
                }
                
                MaxPracticePlugin.LastPuckSpawnTime[senderSteam] = currentTime;
                SendPrivate(ui, clientId, WHITE + "Backpass incoming!" + EC);
                return false;
            }

            // /pass - Set pass position at stick blade, or spawn pass if already set
            // /pass fast - Set fast speed (30)
            // /pass slow - Set slow speed (14)  
            // /pass [no arg] - Normal speed (22) or spawn pass if position set
            if (cmd == "/pass")
            {
                if (!cfg.EnablePass)
                {
                    return false;
                }
                if (!PracticeHelpers.IsCharacterSpawned(player) || player.Stick == null || player.PlayerBody == null)
                {
                    SendPrivate(ui, clientId, WHITE + "Could not find player." + EC);
                    return false;
                }

                string speedArg = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
                
                // Check if setting speed or position
                if (speedArg == "fast" || speedArg == "slow" || speedArg == "lob")
                {
                    float speed = speedArg == "fast" ? 30f : (speedArg == "slow" ? 14f : 18f);
                    Vector3 bladePos = player.Stick.BladeHandlePosition;
                    
                    MaxPractice.YoyoManager.PlayerPassSettings[senderSteam] = new MaxPractice.YoyoManager.PassSettings
                    {
                        PassFromPosition = bladePos,
                        Speed = speed,
                        IsLob = (speedArg == "lob")
                    };
                    
                    SendPrivate(ui, clientId, GREEN + $"Pass position set! Speed: {speedArg}. Use /pass to spawn passes." + EC);
                    return false;
                }
                
                // Check if we have a saved position
                if (!MaxPractice.YoyoManager.PlayerPassSettings.TryGetValue(senderSteam, out var settings))
                {
                    // No position saved - save current position with normal speed
                    Vector3 bladePos = player.Stick.BladeHandlePosition;
                    MaxPractice.YoyoManager.PlayerPassSettings[senderSteam] = new MaxPractice.YoyoManager.PassSettings
                    {
                        PassFromPosition = bladePos,
                        Speed = 22f
                    };
                    SendPrivate(ui, clientId, GREEN + "Pass position set! Use /pass again to spawn passes, or /pass fast|slow|lob to change speed." + EC);
                    return false;
                }
                
                // Spawn a pass from saved position
                if (MaxPractice.YoyoManager.SpawnPass(senderSteam))
                {
                    SendPrivate(ui, clientId, WHITE + "Pass incoming!" + EC);
                }
                else
                {
                    float currentTime = Time.realtimeSinceStartup;
                    if (MaxPracticePlugin.LastPuckSpawnTime.TryGetValue(senderSteam, out float lastTime))
                    {
                        float remaining = 1f - (currentTime - lastTime);
                        if (remaining > 0)
                            SendPrivate(ui, clientId, RED + $"Pass cooldown: {remaining:F1}s remaining" + EC);
                    }
                }
                return false;
            }

            // /unpass - Clear pass position
            if (cmd == "/unpass")
            {
                if (MaxPractice.YoyoManager.PlayerPassSettings.Remove(senderSteam))
                {
                    SendPrivate(ui, clientId, WHITE + "Pass position " + RED + B + "cleared" + EB + EC + WHITE + "." + EC);
                }
                else
                {
                    SendPrivate(ui, clientId, WHITE + "No pass position set." + EC);
                }
                return false;
            }

            // /tappass - Toggle stick tap triggered passes
            if (cmd == "/tappass")
            {
                if (!cfg.EnableTapCommands)
                {
                    return false;
                }
                // Check if already enabled - toggle off
                if (MaxPractice.YoyoManager.PlayerTapPassSettings.ContainsKey(senderSteam))
                {
                    MaxPractice.YoyoManager.PlayerTapPassSettings.Remove(senderSteam);
                    SendPrivate(ui, clientId, WHITE + "Tap pass " + RED + B + "disabled" + EB + EC + WHITE + "." + EC);
                    return false;
                }
                
                // Disable other tap modes first (only one tap command at a time)
                MaxPractice.YoyoManager.TapSpawnPlayers.Remove(senderSteam);
                MaxPractice.YoyoManager.TapYoyoPlayers.Remove(senderSteam);
                MaxPractice.YoyoManager.TapBackpassPlayers.Remove(senderSteam);
                
                // Parse speed argument
                string speedArg = "normal";
                float speed = 22f;
                
                if (parts.Length > 1)
                {
                    speedArg = parts[1].ToLowerInvariant();
                    if (speedArg == "fast") speed = 32f;
                    else if (speedArg == "slow") speed = 14f;
                    else speedArg = "normal";
                }
                
                Vector3 bladePos = player.Stick.BladeHandlePosition;
                MaxPractice.YoyoManager.PlayerTapPassSettings[senderSteam] = new MaxPractice.YoyoManager.TapPassSettings
                {
                    PassFromPosition = bladePos,
                    Speed = speed,
                    RequiredTaps = 3
                };
                
                SendPrivate(ui, clientId, GREEN + $"Tap pass " + B + "enabled" + EB + "! " + EC + WHITE + $"Speed: {speedArg}. Tap stick on ice 3x to get a pass. Use /tappass again to disable." + EC);
                return false;
            }

            // /tapspawn - Toggle stick tap triggered puck spawn
            if (cmd == "/tapspawn")
            {
                if (!cfg.EnableTapCommands)
                {
                    return false;
                }
                if (MaxPractice.YoyoManager.TapSpawnPlayers.Contains(senderSteam))
                {
                    MaxPractice.YoyoManager.TapSpawnPlayers.Remove(senderSteam);
                    SendPrivate(ui, clientId, WHITE + "Tap spawn " + RED + B + "disabled" + EB + EC + WHITE + "." + EC);
                }
                else
                {
                    // Disable other tap modes first (only one tap command at a time)
                    MaxPractice.YoyoManager.PlayerTapPassSettings.Remove(senderSteam);
                    MaxPractice.YoyoManager.TapYoyoPlayers.Remove(senderSteam);
                    MaxPractice.YoyoManager.TapBackpassPlayers.Remove(senderSteam);
                    
                    MaxPractice.YoyoManager.TapSpawnPlayers.Add(senderSteam);
                    SendPrivate(ui, clientId, GREEN + "Tap spawn " + B + "enabled" + EB + "! " + EC + WHITE + "Tap stick on ice 3x to spawn a puck. Use /tapspawn again to disable." + EC);
                }
                return false;
            }

            // /tapyoyo - Toggle stick tap triggered yoyo return
            if (cmd == "/tapyoyo")
            {
                if (!cfg.EnableTapCommands)
                {
                    return false;
                }
                if (MaxPractice.YoyoManager.TapYoyoPlayers.Contains(senderSteam))
                {
                    MaxPractice.YoyoManager.TapYoyoPlayers.Remove(senderSteam);
                    SendPrivate(ui, clientId, WHITE + "Tap yoyo " + RED + B + "disabled" + EB + EC + WHITE + "." + EC);
                }
                else
                {
                    // Disable other tap modes first (only one tap command at a time)
                    MaxPractice.YoyoManager.PlayerTapPassSettings.Remove(senderSteam);
                    MaxPractice.YoyoManager.TapSpawnPlayers.Remove(senderSteam);
                    MaxPractice.YoyoManager.TapBackpassPlayers.Remove(senderSteam);
                    
                    // Also enable regular yoyo tracking so we track the puck
                    if (!MaxPracticePlugin.YoyoPlayers.Contains(senderSteam))
                        MaxPracticePlugin.YoyoPlayers.Add(senderSteam);
                    
                    MaxPractice.YoyoManager.TapYoyoPlayers.Add(senderSteam);
                    SendPrivate(ui, clientId, GREEN + "Tap yoyo " + B + "enabled" + EB + "! " + EC + WHITE + "Shoot the puck, then tap stick 3x to return it. Use /tapyoyo again to disable." + EC);
                }
                return false;
            }

            // /tapbackpass - Toggle stick tap triggered backpass
            if (cmd == "/tapbackpass")
            {
                if (!cfg.EnableTapCommands)
                {
                    return false;
                }
                if (MaxPractice.YoyoManager.TapBackpassPlayers.Contains(senderSteam))
                {
                    MaxPractice.YoyoManager.TapBackpassPlayers.Remove(senderSteam);
                    SendPrivate(ui, clientId, WHITE + "Tap backpass " + RED + B + "disabled" + EB + EC + WHITE + "." + EC);
                }
                else
                {
                    // Disable other tap modes first (only one tap command at a time)
                    MaxPractice.YoyoManager.PlayerTapPassSettings.Remove(senderSteam);
                    MaxPractice.YoyoManager.TapSpawnPlayers.Remove(senderSteam);
                    MaxPractice.YoyoManager.TapYoyoPlayers.Remove(senderSteam);
                    
                    MaxPractice.YoyoManager.TapBackpassPlayers.Add(senderSteam);
                    SendPrivate(ui, clientId, GREEN + "Tap backpass " + B + "enabled" + EB + "! " + EC + WHITE + "Tap stick 3x on ice to get a backpass. Use /tapbackpass again to disable." + EC);
                }
                return false;
            }

            // /recordtraffic - Start recording player movement for traffic playback
            if (cmd == "/recordtraffic" || cmd == "/trafficrecord")
            {
                if (!cfg.EnableTraffic)
                {
                    return false;
                }
                var gameMgr = NetworkBehaviourSingleton<GameManager>.Instance;
                if (gameMgr == null)
                {
                    SendPrivate(ui, clientId, RED + "Could not start recording." + EC);
                    return false;
                }

                if (!PracticeHelpers.IsCharacterSpawned(player) || player.PlayerBody == null)
                {
                    SendPrivate(ui, clientId, RED + "You need to be spawned to record traffic." + EC);
                    return false;
                }

                if (SkaterAI.ActiveRecordings.ContainsKey(senderSteam))
                {
                    SendPrivate(ui, clientId, ORANGE + "Already recording traffic. Use /stoprecord first." + EC);
                    return false;
                }

                var recording = new SkaterRecording();
                recording.StartRecording(PracticeHelpers.GetPlayerTeam(player));
                SkaterAI.ActiveRecordings[senderSteam] = recording;
                gameMgr.StartCoroutine(SkaterAI.RecordMovement(player, senderSteam, ui, clientId));
                SendPrivate(ui, clientId, GREEN + "Traffic recording started. Use /stoprecord to save it." + EC);
                return false;
            }
            
            // /stoprecord - Stop recording and save for /traffic
            if (cmd == "/stoprecord")
            {
                if (!SkaterAI.ActiveRecordings.TryGetValue(senderSteam, out var recording))
                {
                    SendPrivate(ui, clientId, ORANGE + "No active traffic recording." + EC);
                    return false;
                }

                recording.StopRecording();
                SkaterAI.CompletedRecordings[senderSteam] = recording;
                SkaterAI.ActiveRecordings.Remove(senderSteam);
                SendPrivate(ui, clientId, GREEN + $"Traffic recording saved ({recording.Frames.Count} frames). Use /traffic to spawn it." + EC);
                return false;
            }

            // /traffic - spawn traffic with 5 second delay, plays recording if one exists
            if (cmd == "/traffic")
            {
                if (!cfg.EnableTraffic)
                {
                    return false;
                }
                var gameMgr = NetworkBehaviourSingleton<GameManager>.Instance;
                if (gameMgr == null)
                {
                    SendPrivate(ui, clientId, RED + "Could not spawn traffic." + EC);
                    return false;
                }

                if (!PracticeHelpers.IsCharacterSpawned(player) || player.PlayerBody == null)
                {
                    SendPrivate(ui, clientId, RED + "You need to be spawned to place traffic." + EC);
                    return false;
                }

                bool isRedTeam = PracticeHelpers.GetPlayerTeam(player) == PlayerTeam.Red;
                Vector3 spawnPos = player.PlayerBody.transform.position;
                spawnPos.y = 0f;
                Quaternion spawnRot = player.PlayerBody.transform.rotation;

                Vector3 stickOffsetFromBody = Vector3.zero;
                Quaternion stickRotOffset = Quaternion.identity;
                sbyte bladeAngle = 0;
                bool hasStickData = false;

                if (player.Stick != null)
                {
                    stickOffsetFromBody = Quaternion.Inverse(spawnRot) * (player.Stick.transform.position - spawnPos);
                    stickRotOffset = Quaternion.Inverse(spawnRot) * player.Stick.transform.rotation;
                    hasStickData = true;
                }

                if (player.PlayerInput != null)
                {
                    try { bladeAngle = player.PlayerInput.BladeAngleInput.ServerValue; } catch (Exception) { }
                }

                bool hasRecording = SkaterAI.CompletedRecordings.TryGetValue(senderSteam, out var completedRecording) &&
                    completedRecording != null && completedRecording.Frames.Count > 0;

                gameMgr.StartCoroutine(SkaterAI.SpawnTrafficDelayed(
                    ui,
                    clientId,
                    player,
                    senderSteam,
                    isRedTeam,
                    0,
                    spawnPos,
                    spawnRot,
                    stickOffsetFromBody,
                    stickRotOffset,
                    bladeAngle,
                    hasStickData,
                    completedRecording,
                    hasRecording));

                SendPrivate(ui, clientId, GREEN + $"Traffic spawn queued{(hasRecording ? " with recording" : string.Empty)}." + EC);
                return false;
            }

            // /cleartraffic - remove all traffic dummies OR /cleartraffic# to clear specific one
            if (cmd == "/cleartraffic" || cmd.StartsWith("/cleartraffic"))
            {
                string suffix = cmd.Length > "/cleartraffic".Length ? cmd.Substring("/cleartraffic".Length) : string.Empty;
                if (!string.IsNullOrWhiteSpace(suffix) && int.TryParse(suffix, out int trafficNumber))
                {
                    if (SkaterAI.ClearTrafficByNumber(trafficNumber))
                        SendPrivate(ui, clientId, GREEN + $"Cleared traffic {trafficNumber}." + EC);
                    else
                        SendPrivate(ui, clientId, ORANGE + $"Traffic {trafficNumber} not found." + EC);
                    return false;
                }

                int cleared = 0;
                if (MaxPracticePlugin.PlayerOwnedTraffic.TryGetValue(senderSteam, out var ownedTraffic))
                {
                    foreach (var trafficPlayer in ownedTraffic.ToArray())
                    {
                        if (trafficPlayer == null) continue;

                        MaxPracticePlugin.FakePlayers.Remove(trafficPlayer);
                        MaxPracticePlugin.TrafficDummies.Remove(trafficPlayer);
                        SkaterAI.AISkaters.Remove(trafficPlayer);
                        SkaterAI.RedAISkaters.Remove(trafficPlayer);
                        SkaterAI.BlueAISkaters.Remove(trafficPlayer);

                        foreach (var kvp in SkaterAI.TrafficByNumber.Where(kvp => kvp.Value == trafficPlayer).ToArray())
                            SkaterAI.TrafficByNumber.Remove(kvp.Key);

                        if (trafficPlayer.NetworkObject != null && trafficPlayer.NetworkObject.IsSpawned)
                        {
                            trafficPlayer.NetworkObject.Despawn(true);
                            cleared++;
                        }
                    }

                    ownedTraffic.Clear();
                    MaxPracticePlugin.PlayerOwnedTraffic.Remove(senderSteam);
                }

                SendPrivate(ui, clientId, WHITE + $"Cleared {cleared} traffic skater(s)." + EC);
                return false;
            }
            
            // /clearpasser - passer AI is disabled in B310, but keep the command reserved to avoid chat spam.
            if (cmd == "/clearpasser" || cmd.StartsWith("/clearpasser"))
            {
                return false;
            }

            // /passer - disabled for B310
            if (cmd == "/passer")
            {
                return false;
            }

            // /clearall - clears EVERYTHING: pucks, handles, traffic, dummies, passers
            if (cmd == "/clearall")
            {
                int puckCount = 0;
                int handleCount = 0;
                int trafficCount = 0;
                int dummyCount = 0;
                
                // Build set of handle pucks to exclude from regular puck count
                var handlePuckSet = new System.Collections.Generic.HashSet<Puck>();
                foreach (var kvp in MaxPracticePlugin.HandlePucks)
                {
                    handleCount += kvp.Value.Count;
                    foreach (var hp in kvp.Value)
                        if (hp != null) handlePuckSet.Add(hp);
                }
                
                // Clear all pucks using PuckManager's list (efficient)
                var puckManager = MonoBehaviourSingleton<PuckManager>.Instance;
                if (puckManager != null)
                {
                    var pucks = puckManager.GetPucks(true).ToArray(); // ToArray to avoid modification during iteration
                    foreach (var puck in pucks)
                    {
                        if (puck != null)
                        {
                            // Only count as regular puck if not a handle puck
                            if (!handlePuckSet.Contains(puck))
                                puckCount++;
                            UnityEngine.Object.Destroy(puck.gameObject);
                        }
                    }
                }
                
                // Clear handle pucks tracking (already counted and destroyed above)
                MaxPracticePlugin.HandlePucks.Clear();
                MaxPracticePlugin.HandleUseCount.Clear(); // Reset all use counters
                
                // Count traffic before clearing
                trafficCount = SkaterAI.AISkaters.Count;
                
                // Clear ALL AI skaters (traffic + passers) and legacy traffic objects
                foreach (var obj in MaxPracticePlugin.TrafficObjects)
                {
                    if (obj != null) UnityEngine.Object.Destroy(obj);
                }
                MaxPracticePlugin.TrafficObjects.Clear();
                SkaterAI.ClearAllAISkaters();
                MaxPracticePlugin.TrafficDummies.Clear();
                MaxPracticePlugin.PlayerOwnedTraffic.Clear();
                
                // Clear dummy goalies
                if (MaxPracticePlugin.RedTeamDummy != null)
                {
                    MaxPracticePlugin.FakePlayers.Remove(MaxPracticePlugin.RedTeamDummy);
                    if (MaxPracticePlugin.RedTeamDummy.NetworkObject != null && MaxPracticePlugin.RedTeamDummy.NetworkObject.IsSpawned)
                        MaxPracticePlugin.RedTeamDummy.NetworkObject.Despawn(true);
                    MaxPracticePlugin.RedTeamDummy = null;
                    dummyCount++;
                }
                if (MaxPracticePlugin.BlueTeamDummy != null)
                {
                    MaxPracticePlugin.FakePlayers.Remove(MaxPracticePlugin.BlueTeamDummy);
                    if (MaxPracticePlugin.BlueTeamDummy.NetworkObject != null && MaxPracticePlugin.BlueTeamDummy.NetworkObject.IsSpawned)
                        MaxPracticePlugin.BlueTeamDummy.NetworkObject.Despawn(true);
                    MaxPracticePlugin.BlueTeamDummy = null;
                    dummyCount++;
                }
                
                // Clear any remaining fake players
                foreach (var fakePlayer in MaxPracticePlugin.FakePlayers.ToArray())
                {
                    if (fakePlayer != null && fakePlayer.NetworkObject != null && fakePlayer.NetworkObject.IsSpawned)
                    {
                        fakePlayer.NetworkObject.Despawn(true);
                        dummyCount++;
                    }
                }
                MaxPracticePlugin.FakePlayers.Clear();
                
                Broadcast(ui, TeamColor(player) + B + player.Username.Value + EB + EC + WHITE + $" cleared everything ({puckCount} pucks, {handleCount} cones, {trafficCount} traffic, {dummyCount} dummies)" + EC);
                return false;
            }

            // /clearminefield or /clearcones - clear only this player's minefield pucks/cones and reset use counter
            if (cmd == "/clearminefield" || cmd == "/clearcones")
            {
                int count = 0;
                if (MaxPracticePlugin.HandlePucks.TryGetValue(senderSteam, out var handlePucks))
                {
                    foreach (var puck in handlePucks)
                    {
                        if (puck != null)
                        {
                            UnityEngine.Object.Destroy(puck.gameObject);
                            count++;
                        }
                    }
                    handlePucks.Clear();
                }
                // Reset both use counters (cones uses senderSteam, handle uses senderSteam+1)
                MaxPracticePlugin.HandleUseCount[senderSteam] = 0;
                MaxPracticePlugin.HandleUseCount[senderSteam + 1] = 0;
                SendPrivate(ui, clientId, WHITE + $"Cleared {count} puck(s)/cone(s)." + EC);
                return false;
            }

            // /clearpucks - clears pucks but NOT handle pucks or closest puck to each player
            if (cmd == "/clearpucks" || cmd == "/clear" || cmd == "/c" || cmd == "/emptypucks")
            {
                var puckManager = MonoBehaviourSingleton<PuckManager>.Instance;
                if (puckManager == null)
                {
                    SendPrivate(ui, clientId, RED + "Could not find puck manager." + EC);
                    return false;
                }
                
                var pucks = puckManager.GetPucks(true);
                int beforeCount = pucks.Count;
                
                // Use shared cleanup logic (keeps 1 closest puck per player, preserves handle pucks)
                PracticeHelpers.CleanupExcessPucks(pucks);
                
                // Recalculate how many were cleared
                int afterCount = puckManager.GetPucks(true).Count;
                int cleared = beforeCount - afterCount;
                
                Broadcast(ui, TeamColor(player) + B + player.Username.Value + EB + EC + WHITE + " cleared " + cleared + " puck(s), kept " + afterCount + " closest to players");
                return false;
            }

            // /dummyred
            if (cmd == "/dummyred")
            {
                if (!cfg.EnableDummy)
                {
                    return false;
                }
                return HandleDummyCommand(ui, clientId, PlayerTeam.Red);
            }

            // /dummyblue
            if (cmd == "/dummyblue")
            {
                if (!cfg.EnableDummy)
                {
                    return false;
                }
                return HandleDummyCommand(ui, clientId, PlayerTeam.Blue);
            }

            // /dummy
            if (cmd == "/dummy")
            {
                if (!cfg.EnableDummy)
                {
                    return false;
                }
                
                bool isRedTeam = PracticeHelpers.GetPlayerTeam(player) == PlayerTeam.Red;
                return HandleDummyCommand(ui, clientId, isRedTeam ? PlayerTeam.Red : PlayerTeam.Blue);
            }

            // /removedummy
            if (cmd == "/removedummy")
            {
                bool isRedTeam = PracticeHelpers.GetPlayerTeam(player) == PlayerTeam.Red;
                Player dummyToRemove = isRedTeam ? MaxPracticePlugin.RedTeamDummy : MaxPracticePlugin.BlueTeamDummy;
                
                if (dummyToRemove == null || dummyToRemove.NetworkObject == null || !dummyToRemove.NetworkObject.IsSpawned)
                {
                    SendPrivate(ui, clientId, ORANGE + "No dummy goalie exists for your team." + EC);
                    return false;
                }

                MaxPracticePlugin.FakePlayers.Remove(dummyToRemove);
                if (dummyToRemove.NetworkObject.IsSpawned)
                    dummyToRemove.NetworkObject.Despawn(true);
                
                if (isRedTeam)
                    MaxPracticePlugin.RedTeamDummy = null;
                else
                    MaxPracticePlugin.BlueTeamDummy = null;

                SendPrivate(ui, clientId, GREEN + "Dummy goalie removed!" + EC);
                return false;
            }

            // /cones - spawn cones (pucks in a line) - limited to 2 uses
            if (cmd == "/cones")
            {
                if (!cfg.EnableCones)
                {
                    return false;
                }
                
                // Check how many times player has used /cones
                int useCount = 0;
                if (MaxPracticePlugin.HandleUseCount.TryGetValue(senderSteam, out int count))
                    useCount = count;
                
                int maxUses = cfg.ConesPerPlayer;
                if (useCount >= maxUses)
                {
                    SendPrivate(ui, clientId, RED + $"You've used /cones {useCount}/{maxUses} times! Use /clearcones first." + EC);
                    return false;
                }
                
                var playerBody = player.PlayerBody;
                if (playerBody == null)
                {
                    SendPrivate(ui, clientId, WHITE + "Could not find player body." + EC);
                    return false;
                }

                Vector3 playerPos = playerBody.transform.position;
                Vector3 forward = playerBody.transform.forward;
                int spawned = 0;
                int puckCount = PracticeConstants.HandlePuckCount; // 5 cones
                float spacing = PracticeConstants.HandlePuckSpacing;

                // Get actual ice bounds from LevelManager
                /* B310: LevelManager was restructured - bounds clamping disabled
                var levelMgr = NetworkBehaviourSingleton<LevelManager>.Instance;
                Bounds iceBounds = levelMgr != null ? levelMgr.IceBounds : default;
                */
                // B310: Use hardcoded standard rink bounds
                float xMin = -26.0f;
                float xMax = 26.0f;
                float zMin = -42.0f;
                float zMax = 42.0f;
                // Initialize handle pucks list for this player (don't clear - we append)
                if (!MaxPracticePlugin.HandlePucks.ContainsKey(senderSteam))
                    MaxPracticePlugin.HandlePucks[senderSteam] = new List<Puck>();

                // Get GameManager to run coroutines
                var gameMgr = NetworkBehaviourSingleton<GameManager>.Instance;
                if (gameMgr == null)
                {
                    SendPrivate(ui, clientId, RED + "Could not spawn cones." + EC);
                    return false;
                }

                // Spawn pucks in a straight line in front of player (with delayed freeze)
                for (int i = 0; i < puckCount; i++)
                {
                    float distance = 2f + (i * spacing);
                    Vector3 spawnPos = playerPos + forward * distance;
                    spawnPos.y = 0.08f; // Proper height so pucks sit on ice
                    
                    spawnPos.x = Mathf.Clamp(spawnPos.x, xMin, xMax);
                    spawnPos.z = Mathf.Clamp(spawnPos.z, zMin, zMax);

                    // Use delayed spawn - puck spawns normal, then freezes after 0.15s
                    gameMgr.StartCoroutine(PracticeHelpers.SpawnHandlePuckDelayed(spawnPos, senderSteam));
                    spawned++;
                }

                // Increment use counter
                MaxPracticePlugin.HandleUseCount[senderSteam] = useCount + 1;
                int newUseCount = useCount + 1;
                
                SendPrivate(ui, clientId, GREEN + $"Spawned {spawned} cones! ({newUseCount}/{maxUses} uses) Use /clearcones to remove." + EC);
                return false;
            }

            // /handle - spawn 10 random pucks around player for stickhandling - limited to 2 uses
            if (cmd == "/minefield" || cmd == "/handle")
            {
                if (!cfg.EnableMinefield)
                {
                    return false;
                }
                
                // Check how many times player has used /handle (separate counter)
                int useCount = 0;
                ulong handleKey = senderSteam + 1; // Use different key for /handle vs /cones
                if (MaxPracticePlugin.HandleUseCount.TryGetValue(handleKey, out int count))
                    useCount = count;
                
                int maxUses = cfg.MinefieldPerPlayer;
                if (useCount >= maxUses)
                {
                    SendPrivate(ui, clientId, RED + $"You've used /minefield {useCount}/{maxUses} times! Use /clearminefield first." + EC);
                    return false;
                }
                
                var playerBody = player.PlayerBody;
                if (playerBody == null)
                {
                    SendPrivate(ui, clientId, WHITE + "Could not find player body." + EC);
                    return false;
                }

                Vector3 playerPos = playerBody.transform.position;
                int spawned = 0;
                int puckCount = 10; // Always 10 random pucks

                // B310: Use hardcoded standard rink bounds
                float xMin = -26.0f;
                float xMax = 26.0f;
                float zMin = -42.0f;
                float zMax = 42.0f;

                // Initialize handle pucks list for this player (don't clear - we append)
                if (!MaxPracticePlugin.HandlePucks.ContainsKey(senderSteam))
                    MaxPracticePlugin.HandlePucks[senderSteam] = new List<Puck>();

                // Get GameManager to run coroutines
                var gameMgr = NetworkBehaviourSingleton<GameManager>.Instance;
                if (gameMgr == null)
                {
                    SendPrivate(ui, clientId, RED + "Could not spawn minefield." + EC);
                    return false;
                }

                // Spawn 10 pucks randomly around the player (3-6m radius) with delayed freeze
                for (int i = 0; i < puckCount; i++)
                {
                    float angle = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
                    float distance = UnityEngine.Random.Range(3f, 6f);
                    Vector3 spawnPos = playerPos + new Vector3(Mathf.Cos(angle) * distance, 0f, Mathf.Sin(angle) * distance);
                    spawnPos.y = 0.08f; // Proper height so pucks sit on ice
                    
                    spawnPos.x = Mathf.Clamp(spawnPos.x, xMin, xMax);
                    spawnPos.z = Mathf.Clamp(spawnPos.z, zMin, zMax);

                    // Use delayed spawn - puck spawns normal, then freezes after 0.15s
                    gameMgr.StartCoroutine(PracticeHelpers.SpawnHandlePuckDelayed(spawnPos, senderSteam));
                    spawned++;
                }

                // Increment use counter (using separate key)
                MaxPracticePlugin.HandleUseCount[handleKey] = useCount + 1;
                int newUseCount = useCount + 1;
                
                SendPrivate(ui, clientId, GREEN + $"Spawned {spawned} handling pucks! ({newUseCount}/{maxUses} uses) Use /clearminefield to remove." + EC);
                return false;
            }

            // /pop - pop the last touched puck upward
            if (cmd == "/pop")
            {
                if (!cfg.EnablePop)
                {
                    return false;
                }
                if (MaxPractice.YoyoManager.PopLastTouchedPuckForPlayer(player) ||
                    MaxPractice.YoyoManager.PopLastTouchedPuck(senderSteam) ||
                    MaxPractice.YoyoManager.PopLastTouchedPuck(clientId))
                {
                    SendPrivate(ui, clientId, WHITE + "Puck popped!" + EC);
                }
                else
                {
                    SendPrivate(ui, clientId, RED + "No puck to pop! Touch a puck first." + EC);
                }
                return false;
            }

            // /saveprac
            if (cmd == "/saveprac")
            {
                if (!cfg.EnableSavePrac)
                {
                    return false;
                }
                // Only goalies can use saveprac
                if (PracticeHelpers.GetPlayerRole(player) != PlayerRole.Goalie)
                {
                    SendPrivate(ui, clientId, RED + "Only goalies can use /saveprac!" + EC);
                    return false;
                }
                
                if (MaxPracticePlugin.ActiveShooterSessions.TryGetValue(senderSteam, out var existingCoroutine))
                {
                    var gameMgr = NetworkBehaviourSingleton<GameManager>.Instance;
                    if (gameMgr != null)
                        gameMgr.StopCoroutine(existingCoroutine);
                    MaxPracticePlugin.ActiveShooterSessions.Remove(senderSteam);
                    
                    if (MaxPracticePlugin.SpawnedPucks.ContainsKey(senderSteam))
                    {
                        foreach (var p in MaxPracticePlugin.SpawnedPucks[senderSteam])
                            if (p?.gameObject != null)
                                try { UnityEngine.Object.Destroy(p.gameObject); } catch { }
                        MaxPracticePlugin.SpawnedPucks.Remove(senderSteam);
                    }
                    
                    SendPrivate(ui, clientId, GREEN + "Save practice stopped." + EC);
                    return false;
                }

                var playerBody2 = player.PlayerBody;
                if (playerBody2 == null)
                {
                    SendPrivate(ui, clientId, WHITE + "Could not find player body." + EC);
                    return false;
                }

                bool isRedTeam = PracticeHelpers.GetPlayerTeam(player) == PlayerTeam.Red;
                Vector3 targetGoalPos = isRedTeam ? new Vector3(0, 0, -40.23f) : new Vector3(0, 0, 40.23f);

                var coroutine = gm.StartCoroutine(MaxPracticePlugin.Instance.ShooterRoutine(senderSteam, targetGoalPos));
                MaxPracticePlugin.ActiveShooterSessions[senderSteam] = coroutine;

                SendPrivate(ui, clientId, GREEN + $"Save practice started on {(isRedTeam ? "red" : "blue")} net! Pucks will shoot for 2 minutes. Use /saveprac again to stop." + EC);
                return false;
            }

            // /stopsaveprac
            if (cmd == "/stopsaveprac")
            {
                if (MaxPracticePlugin.ActiveShooterSessions.TryGetValue(senderSteam, out var coroutine))
                {
                    var gameMgr = NetworkBehaviourSingleton<GameManager>.Instance;
                    if (gameMgr != null)
                        gameMgr.StopCoroutine(coroutine);
                    MaxPracticePlugin.ActiveShooterSessions.Remove(senderSteam);
                    MaxPracticePlugin.SpawnedPucks.Remove(senderSteam);
                    SendPrivate(ui, clientId, GREEN + "Save practice stopped." + EC);
                }
                else
                {
                    SendPrivate(ui, clientId, RED + "You don't have an active save practice session." + EC);
                }
                return false;
            }

            // /tipprac
            if (cmd == "/tipprac")
            {
                if (!cfg.EnableTipPrac)
                {
                    return false;
                }
                if (MaxPracticePlugin.ActiveTipPracSessions.TryGetValue(senderSteam, out var existingCoroutine))
                {
                    var gameMgr = NetworkBehaviourSingleton<GameManager>.Instance;
                    if (gameMgr != null)
                        gameMgr.StopCoroutine(existingCoroutine);
                    MaxPracticePlugin.ActiveTipPracSessions.Remove(senderSteam);
                    
                    if (MaxPracticePlugin.SpawnedPucks.ContainsKey(senderSteam))
                    {
                        foreach (var p in MaxPracticePlugin.SpawnedPucks[senderSteam])
                            if (p?.gameObject != null)
                                try { UnityEngine.Object.Destroy(p.gameObject); } catch { }
                        MaxPracticePlugin.SpawnedPucks.Remove(senderSteam);
                    }
                    
                    SendPrivate(ui, clientId, GREEN + "Tip practice stopped." + EC);
                    return false;
                }

                var playerBody2 = player.PlayerBody;
                if (playerBody2 == null)
                {
                    SendPrivate(ui, clientId, WHITE + "Could not find player body." + EC);
                    return false;
                }

                bool isRedTeam = PracticeHelpers.GetPlayerTeam(player) == PlayerTeam.Red;
                Vector3 targetGoalPos = isRedTeam ? new Vector3(0, 0, -40.23f) : new Vector3(0, 0, 40.23f);

                var coroutine = gm.StartCoroutine(MaxPracticePlugin.Instance.TipPracRoutine(senderSteam, targetGoalPos));
                MaxPracticePlugin.ActiveTipPracSessions[senderSteam] = coroutine;

                SendPrivate(ui, clientId, GREEN + $"Tip practice started near {(isRedTeam ? "red" : "blue")} net! Stand near the crease to redirect pucks. Use /tipprac again to stop." + EC);
                return false;
            }

            // /stoptipprac
            if (cmd == "/stoptipprac")
            {
                if (MaxPracticePlugin.ActiveTipPracSessions.TryGetValue(senderSteam, out var coroutine))
                {
                    var gameMgr = NetworkBehaviourSingleton<GameManager>.Instance;
                    if (gameMgr != null)
                        gameMgr.StopCoroutine(coroutine);
                    MaxPracticePlugin.ActiveTipPracSessions.Remove(senderSteam);
                    MaxPracticePlugin.SpawnedPucks.Remove(senderSteam);
                    SendPrivate(ui, clientId, GREEN + "Tip practice stopped." + EC);
                }
                else
                {
                    SendPrivate(ui, clientId, RED + "You don't have an active tip practice session." + EC);
                }
                return false;
            }

            return false;
        }
        catch (Exception e)
        {
            Debug.LogError($"[MaxPractice] Error in HandlePracticeCommand: {e}");
            SendPrivate(ui, clientId, RED + "An error occurred." + EC);
            return false;
        }
    }

    private static void HandleVoteGoalies(UIChat ui, Player player, ulong clientId)
    {
        // Two independent gates:
        // - EnableGoalieVoting: server admin's opt-in for mid-game AI goalie voting (default true).
        // - DisableVoting: global vote killswitch shared with /vs and /vw.
        if (!cfg.EnableGoalieVoting)
        {
            return;
        }
        if (cfg.DisableVoting)
        {
            return;
        }

        if (!GoalieVote.IsActive)
        {
            if (!GoalieVote.Start(player, out string reason))
            {
                PracticeHelpers.SendChatMessage(ui, RED + reason + EC, clientId);
                return;
            }
            // If Start passed the vote instantly (e.g. solo player → 1/1), Finish() already
            // broadcast the result and IsActive is now false. Skip the "started a vote" announce.
            if (!GoalieVote.IsActive) return;

            string verb = GoalieVote.TargetState ? "ENABLE" : "DISABLE";
            PracticeHelpers.SendChatMessage(ui, ORANGE + B + player.Username.Value + EB + EC + WHITE +
                $" started a vote to {verb} AI goalies. Type /votegoalies (or /vg) to vote yes. {GoalieVote.CurrentYes}/{GoalieVote.Needed} so far, {(int)GoalieVote.SecondsRemaining}s remaining." + EC);
            return;
        }

        // Vote already active — register this player's yes.
        var (counted, finished, passed) = GoalieVote.Vote(player.OwnerClientId);
        if (!counted)
        {
            PracticeHelpers.SendChatMessage(ui, ORANGE + "You already voted." + EC, clientId);
            return;
        }
        if (finished)
        {
            // Result message is broadcast by GoalieVote.Finish() itself.
            return;
        }
        PracticeHelpers.SendChatMessage(ui, WHITE + $"Vote progress: {GoalieVote.CurrentYes}/{GoalieVote.Needed} yes ({(int)GoalieVote.SecondsRemaining}s left)" + EC);
    }

    private static bool HandleDummyCommand(UIChat ui, ulong clientId, PlayerTeam team)
    {
        try
        {
            bool isRed = team == PlayerTeam.Red;

            // Toggle: if AI goalie exists for this team, despawn it.
            if ((isRed ? MaxPracticePlugin.RedTeamDummy : MaxPracticePlugin.BlueTeamDummy) != null)
            {
                if (GoalieAIManager.DespawnAIGoalie(team))
                    PracticeHelpers.SendChatMessage(ui, GREEN + $"{(isRed ? "Red" : "Blue")} dummy goalie removed!" + EC, clientId);
                return false;
            }

            // Refuse if a real goalie already occupies that slot.
            if (PracticeHelpers.HasRealGoalieOnTeam(team))
            {
                PracticeHelpers.SendChatMessage(ui, RED + $"Cannot spawn {(isRed ? "red" : "blue")} dummy - a real goalie is already on that team!" + EC, clientId);
                return false;
            }

            if (GoalieAIManager.SpawnAIGoalie(team))
                PracticeHelpers.SendChatMessage(ui, GREEN + $"{(isRed ? "Red" : "Blue")} AI goalie spawned!" + EC, clientId);
            else
                PracticeHelpers.SendChatMessage(ui, RED + "Failed to spawn AI goalie (see server log)." + EC, clientId);
        }
        catch (Exception ex)
        {
            Debug.LogError("Dummy spawn error: " + ex.ToString());
        }

        return false;
    }
}