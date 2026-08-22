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
        public int ConfigVersion = 14;

        // ============================================================
        // PRACTICE SHEETS (extra rinks, built on demand)
        // ============================================================
        // /rink spawns a clone of the arena's rink and moves the player onto it, so a
        // group can run drills without three other people skating through them. The
        // clone is built the first time someone asks for it and torn down again once
        // nobody is standing on it.
        public bool EnableRinkSheets = true;

        // Total sheets INCLUDING the main rink, so 3 = the arena rink plus two clones.
        // Clamped to whatever the layout can actually carry (see SheetLayout).
        public int MaxRinkSheets = 3;

        // "vertical" or "horizontal".
        //
        // vertical   - clones are stacked alternately below and above the arena rink
        //              (-40, +40, -80 ...). X and Z never change, which keeps every
        //              coordinate inside the ±50 m box the game's position encoding can
        //              represent, so the netcode is left completely untouched. Each
        //              client draws only the sheet it is standing on, so nobody sees the
        //              others. This is the safe default.
        //
        // horizontal - clones are laid out on a grid beside the arena rink, like a real
        //              multi-sheet facility. Positions out there overflow the encoding,
        //              so the mod re-encodes them relative to a 32 m chunk grid. That
        //              means patching the position sync path: it works, and the main
        //              rink still encodes exactly like vanilla, but it is the riskier
        //              of the two and players without MaxPractice will see anyone on a
        //              clone sheet in the wrong place.
        public string RinkSheetLayout = "vertical";

        // Spacing between stacked sheets. 20 m put them close enough that one read as
        // hanging just under the other, so the default is 40.
        //
        // This trades against sheet count: every sheet has to stay inside the ±50 m box.
        // Sheets alternate below and above the rink, so 40 m fits two clones and 20 m
        // fits four. MaxRinkSheets is clamped to whatever the spacing allows, and the
        // clamp is logged.
        public float RinkSheetVerticalSpacing = 40f;

        // Grid spacing for the horizontal layout. Both are snapped to the 32 m chunk grid.
        public float RinkSheetGridSpacingX = 64f;
        public float RinkSheetGridSpacingZ = 128f;

        // Seconds an empty sheet is kept standing before it's torn down. The clock
        // starts the moment the last player leaves, and rebuilding takes about ten
        // milliseconds, so this only needs to be long enough to survive someone
        // respawning or briefly stepping off. 0 keeps every sheet up until the phase ends.
        public float RinkSheetIdleTimeoutSeconds = 30f;

        // Whether a puck shot into a clone sheet's net is swallowed the way it is on the
        // main rink. Nothing is ever scored either way.
        public bool RinkSheetNetsEatPucks = true;

        // Brightness of a clone sheet's fill lights.
        //
        // This arena has no runtime light fixtures at all - its whole look is baked - so
        // there is usually nothing to clone and the sheet falls back to a small synthetic
        // overhead rig on top of the baked lighting. A stacked sheet sits in the arena's
        // shadow and gets a stronger rig automatically. Raise this if your sheets read
        // dark, lower it if they look washed out; 1.0 is the tuned default.
        public float RinkSheetFillLightGain = 1f;

        // How the sheet's static-batched geometry samples the arena's baked lightmap.
        // If the boards come out blotched with black patches, this is the setting to
        // change - try each and keep whichever looks like the real rink.
        //
        //   "identity" - assume static batching already folded each renderer's lightmap
        //                tile/offset into the combined mesh's UVs (the default)
        //   "renderer" - apply each renderer's own lightmapScaleOffset on top
        //   "off"      - no baked lighting on the batched geometry; it falls back to flat
        //                ambient, which is uniform and dull but never blotchy
        //
        // Only affects the sheets. The arena rink is untouched either way.
        public string RinkSheetProxyLightmaps = "identity";

        // Whether a custom arena loaded by Dem's Scenery Loader (SceneryChanger) is moved
        // onto whichever sheet you're standing on, instead of staying around the main rink
        // while your sheet stands in nothing.
        //
        // The scenery is decoration and isn't networked, and each client only draws its
        // own sheet, so this moves the one local copy rather than cloning it per sheet -
        // it costs a single transform write per sheet change and nobody else's view is
        // affected. Does nothing when that mod isn't installed. Set false to leave the
        // scenery exactly where SceneryChanger put it.
        public bool RinkSheetSceneryFollows = true;

        // ============================================================
        // LIMITS
        // ============================================================
        public int ConesPerPlayer = 1;
        public int MinefieldPerPlayer = 1;
        public int TrafficPerPlayer = 1;
        public int MiniNetsPerPlayer = 1;

        // How far down the game's own net /mininet shrinks it. 1.0 is a full-size
        // net; 0.275 is a quarter-size practice target. Clamped to 0.1-1.0.
        public float MiniNetScale = 0.275f;
        public float SavePracDurationSeconds = 120f;
        public int MaxPucksBeforeCleanup = 30;
        // Existing always-on AI goalie behavior — server admin force.
        // When true, PracticeManager auto-spawns AI goalies during warmup AND keeps them
        // filling empty goalie slots through every phase.
        public bool GoalieAIPersistDuringGame = false;

        // Whether players may /votegoalies (/vg) to toggle mid-game AI goalie filling.
        // Independent of GoalieAIPersistDuringGame: a server can enable always-on, voting,
        // both, or neither. Voting is also blocked by DisableVoting (same as /vs and /vw).
        // Defaults to false — admins opt in explicitly.
        public bool EnableGoalieVoting = false;

        // When true, the warmup countdown is paused (timer never decrements
        // while Phase == Warmup), so practice-only servers never start games.
        public bool PauseWarmupTimer = false;

        // When true, blocks the game-starting vote commands /vs (vote-start)
        // and /vw (vote-warmup) on the server. /vk (vote-kick) is unaffected.
        public bool DisableVoting = false;

        // Whether AI goalies, traffic dummies and passer AIs are kept off the Tab
        // scoreboard and out of its player count. They are real Player objects, so left
        // alone a handful of traffic dummies read as a handful of players who just
        // joined.
        //
        // Worth turning off on a server that runs /votegoalies through real games: those
        // goalies play the whole match, and people generally want to see who is in net.
        // Everything else this hides is a practice prop that never plays.
        //
        // Read PER MACHINE rather than from the server, because the scoreboard is drawn
        // client-side and there is no channel that carries settings to a client today.
        // The default is what practice servers want, so turning it off on the server only
        // changes what the host itself sees; connected clients keep hiding. Making it
        // server-authoritative means announcing it, the way ConeVisuals is announced.
        public bool HideAIFromScoreboard = true;

        // When true, /cones dresses its frozen pucks in the procedural traffic-cone
        // mesh (ConeAsset) and tells clients to do the same. Set false to go back to
        // plain pucks. Server-side setting - clients follow whatever the server says.
        public bool ConeVisuals = true;

        // ============================================================
        // ANTI-GRIEF
        // ============================================================
        // Per-player cooldown (seconds) shared across EVERY clear command
        // (/clearall, /clearpucks, /clear, /c, /emptypucks, /clearcones,
        // /clearminefield, /cleartraffic…). One shared bucket per player so
        // alternating clear commands can't sidestep the limit. Players were
        // spamming these to repeatedly wipe everyone's pucks and flood chat.
        // Set to 0 to disable the cooldown entirely.
        public float ClearCommandCooldownSeconds = 30f;

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

        // /mininet - a small shooting-practice net that eats the pucks it catches.
        public bool EnableMiniNet = true;

        // Draw a string from a yoyo player's stick blade to the puck they're tracking, so
        // the yank has something visible attached to it. Purely cosmetic - it changes no
        // physics and no yoyo behaviour - but it is announced by the server, so this
        // switches it off for everyone rather than per client.
        public bool EnableYoyoString = true;
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
        
        // Mini net: how far in front of the player it lands, and the shortest
        // shot that's still worth taking.
        public const float MiniNetSpawnDistance = 7.0f;
        public const float MiniNetMinDistance = 3.0f;

        // Cones / Minefield
        public const int HandlePuckCount = 5;
        public const float HandlePuckSpacing = 2.0f;

        // Closest two cones may sit, centre to centre. A cone is 0.32 m across, so
        // this leaves ~0.9 m of clear ice between them - enough to stickhandle
        // through, and enough that /minefield's random scatter can't drop two on
        // top of each other.
        public const float MinConeSeparation = 1.25f;

        // How many times to re-roll a blocked spot before giving up on that cone.
        public const int ConeSpotAttempts = 24;

        // How far along the line to push a blocked /cones slot per attempt.
        public const float ConeLineNudge = 0.4f;
        
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
        
        private const int CONFIG_VERSION = 14;
        
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

                    // Read the version out of the JSON rather than grepping the raw text.
                    // The old test was `Contains("\"ConfigVersion\": 14")`, which only ever
                    // matched the exact spacing JsonUtility happens to emit - so a config
                    // that had been hand-edited and reformatted (no space after the colon,
                    // reordered keys, minified) failed the match and every setting in it was
                    // silently reset to defaults.
                    bool isOldConfig;
                    try
                    {
                        ModConfig existing = JsonUtility.FromJson<ModConfig>(existingContent);
                        isOldConfig = existing == null || existing.ConfigVersion != CONFIG_VERSION;
                    }
                    catch
                    {
                        // Unparseable - genuinely nothing to preserve.
                        isOldConfig = true;
                    }


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
