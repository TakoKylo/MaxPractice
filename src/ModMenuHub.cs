// ModMenuHub.cs - one-time migration shim, and nothing else.
//
// MaxPractice used to register a MAX PRACTICE button with the shared PonceMods hub. It
// doesn't any more - the menu is our own F3 panel (see MaxPracticeUI). What survived that
// switch was a full copy of the hub: the registration API, the runner MonoBehaviour, the
// panel, the main/pause-menu button injection, ~1000 lines of it. None of it was reachable
// from this assembly - EnsureRunnerExists() is only called from RegisterMod/Initialize/
// OpenPanel and MaxPractice calls none of them - and none of it was reachable from other
// Ponce mods either: they reflect into the private statics _localRunner and _localCallbacks
// on every PonceMods.Shared.ModMenuHub type they can find, and in our copy both were
// permanently null/empty, so we were always skipped.
//
// So all that is left here is UnregisterMod: a client that hasn't launched since the switch
// still has a MAX PRACTICE entry in the shared file, pointing at a callback that no longer
// exists, and every other Ponce mod's hub would draw it as a button that does nothing. One
// launch clears it. Once the file no longer mentions us this writes nothing and touches no
// other mod, so it costs a single small read per launch until then.
//
// Safe to delete this file - together with the call in MaxPracticePlugin.OnEnable - once
// enough time has passed that nobody is still carrying that stale entry.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace PonceMods.Shared
{
    public static class ModMenuHub
    {
        private const string DATA_FILE_NAME = "PonceMods_ModMenuHub.json";

        private static string _sharedDataFile;
        private static readonly object _fileLock = new object();
        private static bool? _isDedicatedServer;

        /// <summary>
        /// Drop this mod's entry from the shared hub file, if it is still in there.
        /// </summary>
        public static void UnregisterMod(string modName)
        {
            // No hub, no button, nothing to clean up.
            if (IsDedicatedServer()) return;

            lock (_fileLock)
            {
                var entries = LoadEntries();
                if (entries.RemoveAll(e => e != null && e.ModName == modName) == 0)
                    return;   // nothing of ours in there: no write, no cross-mod refresh

                SaveEntries(entries);
                Debug.Log($"[ModMenuHub] Removed the stale {modName} entry from the shared hub.");
            }

            RefreshOtherRunners();
        }

        private static bool IsDedicatedServer()
        {
            if (_isDedicatedServer == null)
            {
                _isDedicatedServer = Application.isBatchMode ||
                                     SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null ||
                                     (Unity.Netcode.NetworkManager.Singleton != null &&
                                      Unity.Netcode.NetworkManager.Singleton.IsServer &&
                                      !Unity.Netcode.NetworkManager.Singleton.IsClient);
            }
            return _isDedicatedServer.Value;
        }

        private static string GetSharedDataFile()
        {
            if (_sharedDataFile == null)
            {
                var configDir = Path.Combine(Application.persistentDataPath, "PonceMods");
                if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);
                _sharedDataFile = Path.Combine(configDir, DATA_FILE_NAME);
            }
            return _sharedDataFile;
        }

        /// <summary>
        /// Tell the mods that still own a hub runner to redraw, so the button disappears in
        /// this session rather than at their next launch. Reflection because each Ponce mod
        /// ships its own copy of the hub type - there is no shared assembly to reference.
        /// </summary>
        private static void RefreshOtherRunners()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var hubType = asm.GetType("PonceMods.Shared.ModMenuHub");
                    if (hubType == null) continue;

                    var runnerField = hubType.GetField("_localRunner", BindingFlags.Static | BindingFlags.NonPublic);
                    var runner = runnerField?.GetValue(null);
                    if (runner == null) continue;

                    runner.GetType()
                          .GetMethod("RefreshFromSharedData", BindingFlags.Instance | BindingFlags.Public)
                          ?.Invoke(runner, null);
                }
                catch { }
            }
        }

        // Shared file format, one entry per line: ModName|ButtonText|Priority
        private sealed class ModEntryData
        {
            public string ModName;
            public string ButtonText;
            public int Priority;

            public string ToLine() => $"{ModName}|{ButtonText}|{Priority}";

            public static ModEntryData FromLine(string line)
            {
                var parts = line.Split('|');
                if (parts.Length >= 3 && int.TryParse(parts[2], out int prio))
                    return new ModEntryData { ModName = parts[0], ButtonText = parts[1], Priority = prio };
                return null;
            }
        }

        private static List<ModEntryData> LoadEntries()
        {
            var result = new List<ModEntryData>();
            try
            {
                var filePath = GetSharedDataFile();
                if (!File.Exists(filePath)) return result;

                foreach (var line in File.ReadAllLines(filePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var entry = ModEntryData.FromLine(line);
                    if (entry != null) result.Add(entry);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ModMenuHub] Error loading entries: {e.Message}");
            }
            return result;
        }

        private static void SaveEntries(List<ModEntryData> entries)
        {
            try
            {
                var lines = new List<string>(entries.Count);
                foreach (var entry in entries) lines.Add(entry.ToLine());
                File.WriteAllLines(GetSharedDataFile(), lines);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ModMenuHub] Error saving entries: {e.Message}");
            }
        }
    }
}
