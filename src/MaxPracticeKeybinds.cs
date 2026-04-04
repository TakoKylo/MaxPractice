// MaxPracticeKeybinds.cs - Keybind configuration and input handling for MaxPractice
// Modeled after PoncePlayerInput's keybind system

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace MaxPractice
{
    [Serializable]
    public class MaxPracticeKeybindConfig
    {
        // Command keybinds stored as "command:key" pairs
        // e.g., "/spawnpuck:G", "/backpass:H", "/pop:P"
        public List<string> commandBinds = new List<string>();
    }

    public static class MaxPracticeKeybindManager
    {
        public static MaxPracticeKeybindConfig Config { get; private set; } = new MaxPracticeKeybindConfig();

        private static string GameDir => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        private static string ConfigDir => Path.Combine(GameDir, "config");
        private static string ModHubDir => Path.Combine(ConfigDir, "ModHub");
        private static string MaxPracticeDir => Path.Combine(ModHubDir, "MaxPractice");
        private static string BindsPath => Path.Combine(MaxPracticeDir, "MaxPractice.Keybinds.json");

        // Parsed keybind lookup: KeyCode -> command string
        private static readonly Dictionary<KeyChord, string> _chordToCommand = new Dictionary<KeyChord, string>();

        public struct KeyChord : IEquatable<KeyChord>
        {
            public KeyCode[] Keys;
            public bool Ctrl, Shift, Alt;

            public override int GetHashCode()
            {
                int h = (Ctrl ? 1 : 0) ^ (Shift ? 2 : 0) ^ (Alt ? 4 : 0);
                if (Keys != null)
                {
                    for (int i = 0; i < Keys.Length; i++)
                        h = (h * 397) ^ (int)Keys[i];
                }
                return h;
            }

            public bool Equals(KeyChord other)
            {
                if (Ctrl != other.Ctrl || Shift != other.Shift || Alt != other.Alt) return false;
                if (Keys == null && other.Keys == null) return true;
                if (Keys == null || other.Keys == null) return false;
                if (Keys.Length != other.Keys.Length) return false;
                for (int i = 0; i < Keys.Length; i++)
                    if (Keys[i] != other.Keys[i]) return false;
                return true;
            }

            public override bool Equals(object obj) => obj is KeyChord kc && Equals(kc);
        }

        public static void Initialize()
        {
            EnsureConfigs();
            LoadConfig();
            RebuildLookups();
        }

        private static void EnsureConfigs()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                Directory.CreateDirectory(ModHubDir);
                Directory.CreateDirectory(MaxPracticeDir);

                if (!File.Exists(BindsPath))
                {
                    var c = new MaxPracticeKeybindConfig();
                    // Default binds
                    c.commandBinds.Add("/s:G");
                    c.commandBinds.Add("/backpass:H");
                    c.commandBinds.Add("/pop:V");
                    AtomicWrite(BindsPath, JsonUtility.ToJson(c, true));
                    Debug.Log("[MaxPractice] Created default keybind config: " + BindsPath);
                }
            }
            catch (Exception e) { Debug.LogException(e); }
        }

        public static void LoadConfig()
        {
            try
            {
                if (File.Exists(BindsPath))
                    Config = JsonUtility.FromJson<MaxPracticeKeybindConfig>(File.ReadAllText(BindsPath)) ?? new MaxPracticeKeybindConfig();
                if (Config == null) Config = new MaxPracticeKeybindConfig();
                if (Config.commandBinds == null) Config.commandBinds = new List<string>();
            }
            catch (Exception e)
            {
                Debug.LogError($"[MaxPractice] Failed to load keybind config: {e}");
                Config = new MaxPracticeKeybindConfig();
            }
        }

        public static void SaveConfig()
        {
            try
            {
                Directory.CreateDirectory(ModHubDir);
                Directory.CreateDirectory(MaxPracticeDir);
                AtomicWrite(BindsPath, JsonUtility.ToJson(Config, true));
                RebuildLookups();
            }
            catch (Exception e) { Debug.LogException(e); }
        }

        private static void AtomicWrite(string path, string content)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        public static void RebuildLookups()
        {
            _chordToCommand.Clear();
            foreach (var entry in Config.commandBinds)
            {
                if (!ParseCommandEntry(entry, out string cmd, out string keySpec)) continue;
                if (TryParseChord(keySpec, out KeyChord kc))
                    _chordToCommand[kc] = cmd;
            }
        }

        public static Dictionary<KeyChord, string> GetBindings() => _chordToCommand;

        private static bool ParseCommandEntry(string raw, out string command, out string keySpec)
        {
            command = null; keySpec = null;
            if (string.IsNullOrEmpty(raw)) return false;
            int colonIdx = raw.LastIndexOf(':');
            if (colonIdx < 1) return false;
            command = raw.Substring(0, colonIdx).Trim();
            keySpec = raw.Substring(colonIdx + 1).Trim();
            return !string.IsNullOrEmpty(command) && !string.IsNullOrEmpty(keySpec);
        }

        public static bool TryParseChord(string spec, out KeyChord kc)
        {
            kc = default;
            if (string.IsNullOrEmpty(spec)) return false;

            spec = spec.Trim();
            bool ctrl = false, shift = false, alt = false;
            var keys = new List<KeyCode>();

            var tokens = spec.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i].Trim();
                string up = token.ToUpperInvariant();

                if (up == "CTRL" || up == "CONTROL" || up == "CTL") { ctrl = true; continue; }
                if (up == "SHIFT") { shift = true; continue; }
                if (up == "ALT" || up == "OPTION" || up == "OPT") { alt = true; continue; }

                if (TryParseKeyCode(token, out var parsed))
                    keys.Add(parsed);
            }

            // Allow modifier-only chords
            if (keys.Count == 0 && (ctrl || shift || alt))
            {
                kc = new KeyChord { Keys = Array.Empty<KeyCode>(), Ctrl = ctrl, Shift = shift, Alt = alt };
                return true;
            }

            if (keys.Count == 0) return false;

            keys.Sort((a, b) => a.CompareTo(b));
            kc = new KeyChord { Keys = keys.ToArray(), Ctrl = ctrl, Shift = shift, Alt = alt };
            return true;
        }

        public static bool IsModifierKey(KeyCode k)
        {
            return k == KeyCode.LeftShift || k == KeyCode.RightShift ||
                   k == KeyCode.LeftControl || k == KeyCode.RightControl ||
                   k == KeyCode.LeftAlt || k == KeyCode.RightAlt;
        }

        public static bool IsAllowedKey(KeyCode k)
        {
            if (k >= KeyCode.A && k <= KeyCode.Z) return true;
            if (k >= KeyCode.Alpha0 && k <= KeyCode.Alpha9) return true;
            if (k >= KeyCode.F1 && k <= KeyCode.F12) return true;

            switch (k)
            {
                case KeyCode.Space:
                case KeyCode.Tab:
                case KeyCode.Escape:
                case KeyCode.LeftShift:
                case KeyCode.RightShift:
                case KeyCode.LeftControl:
                case KeyCode.RightControl:
                case KeyCode.LeftAlt:
                case KeyCode.RightAlt:
                case KeyCode.UpArrow:
                case KeyCode.DownArrow:
                case KeyCode.LeftArrow:
                case KeyCode.RightArrow:
                case KeyCode.BackQuote:
                case KeyCode.Minus:
                case KeyCode.Equals:
                case KeyCode.LeftBracket:
                case KeyCode.RightBracket:
                case KeyCode.Semicolon:
                case KeyCode.Quote:
                case KeyCode.Comma:
                case KeyCode.Period:
                case KeyCode.Slash:
                case KeyCode.Backslash:
                case KeyCode.Mouse0:
                case KeyCode.Mouse1:
                case KeyCode.Mouse2:
                case KeyCode.Mouse3:
                case KeyCode.Mouse4:
                case KeyCode.Keypad0:
                case KeyCode.Keypad1:
                case KeyCode.Keypad2:
                case KeyCode.Keypad3:
                case KeyCode.Keypad4:
                case KeyCode.Keypad5:
                case KeyCode.Keypad6:
                case KeyCode.Keypad7:
                case KeyCode.Keypad8:
                case KeyCode.Keypad9:
                    return true;
            }

            return false;
        }

        public static string GetFriendlyKeyName(KeyCode k)
        {
            switch (k)
            {
                case KeyCode.Mouse0: return "LMB";
                case KeyCode.Mouse1: return "RMB";
                case KeyCode.Mouse2: return "MMB";
                case KeyCode.Mouse3: return "MB4";
                case KeyCode.Mouse4: return "MB5";
                default: return k.ToString();
            }
        }

        private static bool TryParseKeyCode(string s, out KeyCode key)
        {
            key = KeyCode.None;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();

            if (s.Length == 1)
            {
                char c = s[0];
                char up = char.ToUpperInvariant(c);
                if (up >= 'A' && up <= 'Z') { key = (KeyCode)Enum.Parse(typeof(KeyCode), up.ToString()); return true; }
                if (c >= '0' && c <= '9') { key = (KeyCode)Enum.Parse(typeof(KeyCode), "Alpha" + c); return true; }
                switch (c)
                {
                    case '`': key = KeyCode.BackQuote; return true;
                    case '-': key = KeyCode.Minus; return true;
                    case '=': key = KeyCode.Equals; return true;
                    case '[': key = KeyCode.LeftBracket; return true;
                    case ']': key = KeyCode.RightBracket; return true;
                    case ';': key = KeyCode.Semicolon; return true;
                    case '\'': key = KeyCode.Quote; return true;
                    case ',': key = KeyCode.Comma; return true;
                    case '.': key = KeyCode.Period; return true;
                    case '/': key = KeyCode.Slash; return true;
                    case '\\': key = KeyCode.Backslash; return true;
                    case ' ': key = KeyCode.Space; return true;
                }
            }

            string us = s.ToUpperInvariant();
            if (us == "NUM0" || us == "NP0" || us == "KP0") { key = KeyCode.Keypad0; return true; }
            if (us == "NUM1" || us == "NP1" || us == "KP1") { key = KeyCode.Keypad1; return true; }
            if (us == "NUM2" || us == "NP2" || us == "KP2") { key = KeyCode.Keypad2; return true; }
            if (us == "NUM3" || us == "NP3" || us == "KP3") { key = KeyCode.Keypad3; return true; }
            if (us == "NUM4" || us == "NP4" || us == "KP4") { key = KeyCode.Keypad4; return true; }
            if (us == "NUM5" || us == "NP5" || us == "KP5") { key = KeyCode.Keypad5; return true; }
            if (us == "NUM6" || us == "NP6" || us == "KP6") { key = KeyCode.Keypad6; return true; }
            if (us == "NUM7" || us == "NP7" || us == "KP7") { key = KeyCode.Keypad7; return true; }
            if (us == "NUM8" || us == "NP8" || us == "KP8") { key = KeyCode.Keypad8; return true; }
            if (us == "NUM9" || us == "NP9" || us == "KP9") { key = KeyCode.Keypad9; return true; }

            if (string.Equals(s, "LMB", StringComparison.OrdinalIgnoreCase)) { key = KeyCode.Mouse0; return true; }
            if (string.Equals(s, "RMB", StringComparison.OrdinalIgnoreCase)) { key = KeyCode.Mouse1; return true; }
            if (string.Equals(s, "MMB", StringComparison.OrdinalIgnoreCase)) { key = KeyCode.Mouse2; return true; }
            if (string.Equals(s, "MB4", StringComparison.OrdinalIgnoreCase)) { key = KeyCode.Mouse3; return true; }
            if (string.Equals(s, "MB5", StringComparison.OrdinalIgnoreCase)) { key = KeyCode.Mouse4; return true; }
            if (string.Equals(s, "Mouse4", StringComparison.OrdinalIgnoreCase)) { key = KeyCode.Mouse3; return true; }
            if (string.Equals(s, "Mouse5", StringComparison.OrdinalIgnoreCase)) { key = KeyCode.Mouse4; return true; }

            if (Enum.TryParse<KeyCode>(s, true, out key))
                return IsAllowedKey(key);

            return false;
        }

        public static string ChordToString(KeyChord kc)
        {
            string result = "";
            if (kc.Ctrl) result += "Ctrl+";
            if (kc.Shift) result += "Shift+";
            if (kc.Alt) result += "Alt+";

            if (kc.Keys != null && kc.Keys.Length > 0)
                result += string.Join("+", kc.Keys.Select(GetFriendlyKeyName).ToArray());

            return result;
        }
    }
}
