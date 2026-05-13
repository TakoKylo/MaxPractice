// ModConfig.cs - Configuration system for MaxPractice mod

using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace MaxPractice
{
    [Serializable]
    public class ModConfig
    {
        // ============================================================
        // GENERAL
        // ============================================================
        public int ConfigVersion = 5;

        // ============================================================
        // LIMITS
        // ============================================================
        public int ConesPerPlayer = 1;
        public int MinefieldPerPlayer = 1;
        public int TrafficPerPlayer = 1;
        public float SavePracDurationSeconds = 120f;
        public int MaxPucksBeforeCleanup = 30;
        public bool GoalieAIPersistDuringGame = false;

        // When true, the warmup countdown is paused (timer never decrements
        // while Phase == Warmup), so practice-only servers never start games.
        public bool PauseWarmupTimer = false;

        // When true, blocks the game-starting vote commands /vs (vote-start)
        // and /vw (vote-warmup) on the server. /vk (vote-kick) is unaffected.
        public bool DisableVoting = false;
        
        // ============================================================
        // COMMAND ENABLE/DISABLE
        // ============================================================
        public bool EnableSpawnPuck = true;
        public bool EnableBackpass = true;
        public bool EnablePass = true;
        public bool EnableYoyo = true;
        public bool EnablePop = true;
        public bool EnableSavePrac = true;
        public bool EnableTipPrac = true;
        public bool EnableCones = true;
        public bool EnableMinefield = true;
        public bool EnableTraffic = true;
        public bool EnableDummy = true;
        public bool EnableInfiniteStamina = true;
        public bool EnableTapCommands = true;
    }

    /// <summary>
    /// Hardcoded constants that were previously configurable.
    /// Change these values here if you need to tune them.
    /// </summary>
    public static class PracticeConstants
    {
        // Shot speeds
        public const float FastShotMinSpeedMph = 60f;
        public const float FastShotMaxSpeedMph = 90f;
        public const float SlowShotMinSpeedMph = 40f;
        public const float SlowShotMaxSpeedMph = 59f;
        public const float FastShotChance = 0.7f;
        public const float MinTimeBetweenShots = 1.5f;
        public const float MaxTimeBetweenShots = 3.0f;
        public const float PuckRestTimeBeforeShot = 0.5f;
        
        // Yoyo
        public const float YoyoYankSpeedThreshold = 6f;
        public const float YoyoMinDistanceFromStick = 5f;
        public const float YoyoDelayAfterShot = 0.5f;
        public const float YoyoYankCooldown = 0.5f;
        
        // Puck spawning
        public const float PuckSpawnCooldown = 2.5f;
        public const float PassSpawnCooldown = 2.5f;
        public const float BackpassSpeed = 22f;
        public const float BackpassDistance = 15f;
        
        // Cleanup
        public const float SettledPuckVelocity = 0.5f;
        
        // Cones / Minefield
        public const int HandlePuckCount = 5;
        public const float HandlePuckSpacing = 2.0f;
        
        // Traffic
        public const float TrafficRecordingFps = 120f;
        
        // Tip practice
        public const float TipPracDurationSeconds = 120f;
        public const float TipShotMinSpeedMph = 45f;
        public const float TipShotMaxSpeedMph = 70f;
        public const float TipMinTimeBetweenShots = 2.0f;
        public const float TipMaxTimeBetweenShots = 4.0f;
        public const float TipPuckRestTime = 0.3f;
    }

    public static class ConfigManager
    {
        public static ModConfig Config { get; private set; } = new ModConfig();
        
        private static string _configDir = null;
        private static string _configFile = null;
        
        private const int CONFIG_VERSION = 5;
        private const string VERSION_KEY = "\"ConfigVersion\":";
        
        private static string ConfigDir
        {
            get
            {
                if (_configDir != null) return _configDir;
                // Match the standard Puck-mod convention: <cwd>/config
                // Puck (client and dedicated server) launches with the install
                // directory as the working directory. Application.dataPath
                // didn't resolve correctly on Linux dedicated servers.
                string configFolder = Path.Combine(Path.GetFullPath("."), "config");
                if (!Directory.Exists(configFolder))
                {
                    Directory.CreateDirectory(configFolder);
                }
                _configDir = configFolder;
                return _configDir;
            }
        }
        
        private static string ConfigFile
        {
            get
            {
                if (_configFile != null) return _configFile;
                _configFile = Path.Combine(ConfigDir, "maxpractice.json");
                return _configFile;
            }
        }
        
        public static void EnsureConfig()
        {
            try
            {
                if (!Directory.Exists(ConfigDir))
                    Directory.CreateDirectory(ConfigDir);
                
                if (File.Exists(ConfigFile))
                {
                    string existingContent = File.ReadAllText(ConfigFile);
                    bool isOldConfig = !existingContent.Contains(VERSION_KEY) || 
                                       !existingContent.Contains($"{VERSION_KEY} {CONFIG_VERSION}");
                    
                    if (isOldConfig)
                    {
                        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        string oldConfigPath = Path.Combine(ConfigDir, $"maxpractice_{timestamp}_old.json");
                        File.Copy(ConfigFile, oldConfigPath, true);
                        Log($"Old config backed up to: {Path.GetFileName(oldConfigPath)}");
                        
                        File.WriteAllText(ConfigFile, JsonUtility.ToJson(new ModConfig(), true));
                        Log($"Created new config (version {CONFIG_VERSION})");
                    }
                }
                else
                {
                    File.WriteAllText(ConfigFile, JsonUtility.ToJson(new ModConfig(), true));
                    Log($"Created config file at: {ConfigFile}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaxPractice] Error ensuring config: {ex.Message}");
            }
        }
        
        public static void ReloadConfig()
        {
            var cfg = new ModConfig();
            
            try
            {
                if (!File.Exists(ConfigFile))
                {
                    Log("Config file not found, using defaults");
                    Config = cfg;
                    return;
                }
                
                string raw = File.ReadAllText(ConfigFile);
                
                string clean = Regex.Replace(
                    Regex.Replace(
                        Regex.Replace(raw, @"//.*?$", "", RegexOptions.Multiline),
                        @"/\*.*?\*/", "", RegexOptions.Singleline),
                    @",\s*(\}|\])", "$1");
                
                JsonUtility.FromJsonOverwrite(clean, cfg);
                Config = cfg;
                Log("Config loaded successfully");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaxPractice] Error loading config: {ex.Message}");
                Config = new ModConfig();
            }
        }
        
        public static void Initialize()
        {
            EnsureConfig();
            ReloadConfig();
        }
        
        public static void Log(string message)
        {
            Debug.Log("[MaxPractice] " + message);
        }
        
        public static void Dbg(string message)
        {
            #if DEBUG
            Log(message);
            #endif
        }
    }
}
