// ModMenuHub.cs - Unified Modifications Menu for Ponce Mods
// Uses a shared file for cross-assembly data sharing with file locking.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEngine;
using UnityEngine.UIElements;
using UITK = UnityEngine.UIElements;

namespace PonceMods.Shared
{
    /// <summary>
    /// Static helper class for registering mods with the shared menu.
    /// </summary>
    public static class ModMenuHub
    {
        private const string HUB_OBJECT_NAME = "__PonceMods_ModMenuHub__";
        private const string DATA_FILE_NAME = "PonceMods_ModMenuHub.json";
        private static string _sharedDataFile;
        private static readonly object _fileLock = new object();
        
        // Local storage for THIS mod's callback
        private static readonly Dictionary<string, Action> _localCallbacks = new Dictionary<string, Action>();
        
        // Local runner reference
        private static ModMenuHubRunner _localRunner;
        
        // Cached dedicated server check
        private static bool? _isDedicatedServer = null;
        
        /// <summary>
        /// Check if running on a dedicated server (no UI needed)
        /// </summary>
        private static bool IsDedicatedServer()
        {
            if (_isDedicatedServer == null)
            {
                // Check for headless/batch mode or dedicated server indicators
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
                // Use game's config folder which is more stable than temp
                var configDir = Path.Combine(Application.persistentDataPath, "PonceMods");
                if (!Directory.Exists(configDir))
                {
                    Directory.CreateDirectory(configDir);
                }
                _sharedDataFile = Path.Combine(configDir, DATA_FILE_NAME);
            }
            return _sharedDataFile;
        }
        
        /// <summary>
        /// Register a mod's button entry.
        /// </summary>
        public static void RegisterMod(string modName, string buttonText, Action onClick, int priority = 100)
        {
            // Skip on dedicated servers - no UI needed
            if (IsDedicatedServer())
            {
                Debug.Log($"[ModMenuHub] Skipping RegisterMod on dedicated server: {modName}");
                return;
            }
            
            Debug.Log($"[ModMenuHub] RegisterMod called: {modName}");
            
            // Store callback locally
            _localCallbacks[modName] = onClick;
            
            // Add entry to shared data via file (with locking)
            lock (_fileLock)
            {
                var entries = LoadSharedEntriesInternal();
                Debug.Log($"[ModMenuHub] Before add - entries count: {entries.Count}");
                
                entries.RemoveAll(e => e.ModName == modName);
                entries.Add(new ModEntryData { ModName = modName, ButtonText = buttonText, Priority = priority });
                entries.Sort((a, b) => a.Priority.CompareTo(b.Priority));
                SaveSharedEntriesInternal(entries);
                
                Debug.Log($"[ModMenuHub] After add - entries count: {entries.Count}, file: {GetSharedDataFile()}");
            }
            
            // Ensure runner exists and refresh ALL runners across assemblies
            EnsureRunnerExists();
            RefreshAllRunners();
            
            Debug.Log($"[ModMenuHub] Registered mod: {modName} ({buttonText})");
        }
        
        /// <summary>
        /// Unregister a mod when it's disabled.
        /// </summary>
        public static void UnregisterMod(string modName)
        {
            // Skip on dedicated servers
            if (IsDedicatedServer()) return;
            
            _localCallbacks.Remove(modName);
            
            lock (_fileLock)
            {
                var entries = LoadSharedEntriesInternal();
                entries.RemoveAll(e => e.ModName == modName);
                SaveSharedEntriesInternal(entries);
            }
            
            RefreshAllRunners();
            
            Debug.Log($"[ModMenuHub] Unregistered mod: {modName}");
        }
        
        /// <summary>
        /// Initialize the hub.
        /// </summary>
        public static void Initialize(string modName)
        {
            // Skip on dedicated servers
            if (IsDedicatedServer())
            {
                Debug.Log($"[ModMenuHub] Skipping Initialize on dedicated server: {modName}");
                return;
            }
            
            EnsureRunnerExists();
            Debug.Log($"[ModMenuHub] Initialize called by: {modName}");
        }
        
        /// <summary>
        /// Try to invoke a callback for a mod.
        /// </summary>
        public static bool TryInvokeCallback(string modName)
        {
            if (_localCallbacks.TryGetValue(modName, out var callback) && callback != null)
            {
                Debug.Log($"[ModMenuHub] Invoking callback for: {modName}");
                callback.Invoke();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true when any loaded ModMenuHub instance has a live callback for this mod.
        /// Used to prune stale persisted entries from previous sessions.
        /// </summary>
        public static bool HasLiveCallback(string modName)
        {
            if (string.IsNullOrWhiteSpace(modName)) return false;

            if (_localCallbacks.TryGetValue(modName, out var localCb) && localCb != null)
                return true;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var hubType = asm.GetType("PonceMods.Shared.ModMenuHub");
                    if (hubType == null) continue;

                    var callbacksField = hubType.GetField("_localCallbacks", BindingFlags.Static | BindingFlags.NonPublic);
                    if (callbacksField?.GetValue(null) is Dictionary<string, Action> callbacks &&
                        callbacks.TryGetValue(modName, out var cb) && cb != null)
                    {
                        return true;
                    }
                }
                catch { }
            }

            return false;
        }
        
        public static bool IsPanelOpen => _localRunner != null && _localRunner.IsPanelOpen;
        
        /// <summary>
        /// Open the ModMenuHub panel programmatically.
        /// </summary>
        public static void OpenPanel()
        {
            Debug.Log("[ModMenuHub] OpenPanel called");
            
            // First try our local runner
            if (_localRunner != null && _localRunner.IsPrimary)
            {
                Debug.Log("[ModMenuHub] Using local primary runner");
                _localRunner.OpenModPanelPublic();
                return;
            }
            
            // Find the primary runner across all assemblies
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var hubType = asm.GetType("PonceMods.Shared.ModMenuHub");
                    if (hubType == null) continue;
                    
                    var runnerField = hubType.GetField("_localRunner", BindingFlags.Static | BindingFlags.NonPublic);
                    if (runnerField == null) continue;
                    
                    var runner = runnerField.GetValue(null);
                    if (runner == null) continue;
                    
                    // Check if this runner is primary
                    var isPrimaryProp = runner.GetType().GetProperty("IsPrimary", BindingFlags.Instance | BindingFlags.Public);
                    if (isPrimaryProp != null)
                    {
                        var isPrimary = (bool)isPrimaryProp.GetValue(runner);
                        if (isPrimary)
                        {
                            Debug.Log($"[ModMenuHub] Found primary runner in {asm.GetName().Name}");
                            var openMethod = runner.GetType().GetMethod("OpenModPanelPublic", BindingFlags.Instance | BindingFlags.Public);
                            openMethod?.Invoke(runner, null);
                            return;
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ModMenuHub] Error finding primary runner: {e.Message}");
                }
            }
            
            // Fallback: just use our local runner even if not primary
            if (_localRunner != null)
            {
                Debug.Log("[ModMenuHub] Fallback: using local runner");
                _localRunner.OpenModPanelPublic();
            }
            else
            {
                Debug.LogError("[ModMenuHub] No runner available to open panel");
            }
        }
        
        public static void Cleanup(string modName) => UnregisterMod(modName);
        public static void Update() { }
        
        /// <summary>
        /// Close all mod panels completely (ESC behavior - like closing pause menu).
        /// Will restore menu buttons and handle cursor based on game state.
        /// </summary>
        public static void FullClose()
        {
            // First try our local runner
            if (_localRunner != null && _localRunner.IsPrimary)
            {
                _localRunner.FullClosePublic();
                return;
            }
            
            // Find the primary runner across all assemblies
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var hubType = asm.GetType("PonceMods.Shared.ModMenuHub");
                    if (hubType == null) continue;
                    
                    var runnerField = hubType.GetField("_localRunner", BindingFlags.Static | BindingFlags.NonPublic);
                    if (runnerField == null) continue;
                    
                    var runner = runnerField.GetValue(null);
                    if (runner == null) continue;
                    
                    var isPrimaryProp = runner.GetType().GetProperty("IsPrimary", BindingFlags.Instance | BindingFlags.Public);
                    if (isPrimaryProp != null)
                    {
                        var isPrimary = (bool)isPrimaryProp.GetValue(runner);
                        if (isPrimary)
                        {
                            var fullCloseMethod = runner.GetType().GetMethod("FullClosePublic", BindingFlags.Instance | BindingFlags.Public);
                            fullCloseMethod?.Invoke(runner, null);
                            return;
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ModMenuHub] Error finding primary runner for FullClose: {e.Message}");
                }
            }
            
            // Fallback: just use our local runner
            if (_localRunner != null)
            {
                _localRunner.FullClosePublic();
            }
        }
        
        public static void ForceRefresh()
        {
            RefreshAllRunners();
        }
        
        private static void RefreshAllRunners()
        {
            // Refresh local runner
            if (_localRunner != null)
            {
                _localRunner.RefreshFromSharedData();
            }
            
            // Also try to refresh runners in other assemblies via reflection
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var hubType = asm.GetType("PonceMods.Shared.ModMenuHub");
                    if (hubType == null) continue;
                    
                    var runnerField = hubType.GetField("_localRunner", BindingFlags.Static | BindingFlags.NonPublic);
                    if (runnerField == null) continue;
                    
                    var runner = runnerField.GetValue(null);
                    if (runner == null) continue;
                    
                    var refreshMethod = runner.GetType().GetMethod("RefreshFromSharedData", BindingFlags.Instance | BindingFlags.Public);
                    refreshMethod?.Invoke(runner, null);
                }
                catch { }
            }
        }
        
        private static void EnsureRunnerExists()
        {
            // Check if we already have a local runner
            if (_localRunner != null) return;
            
            // Check if another mod already created the hub GameObject
            var existingHub = GameObject.Find(HUB_OBJECT_NAME);
            if (existingHub != null)
            {
                // Hub exists - add our own runner component (but mark as non-primary)
                _localRunner = existingHub.AddComponent<ModMenuHubRunner>();
                _localRunner.SetPrimary(false);
                return;
            }
            
            // Create new hub (this one is primary)
            var go = new GameObject(HUB_OBJECT_NAME);
            UnityEngine.Object.DontDestroyOnLoad(go);
            _localRunner = go.AddComponent<ModMenuHubRunner>();
            _localRunner.SetPrimary(true);
            
            Debug.Log("[ModMenuHub] Created shared hub GameObject (primary)");
        }
        
        // Shared data storage using a simple text file format (one entry per line)
        // Format: ModName|ButtonText|Priority
        public class ModEntryData
        {
            public string ModName;
            public string ButtonText;
            public int Priority;
            
            public string ToLine() => $"{ModName}|{ButtonText}|{Priority}";
            
            public static ModEntryData FromLine(string line)
            {
                var parts = line.Split('|');
                if (parts.Length >= 3 && int.TryParse(parts[2], out int prio))
                {
                    return new ModEntryData { ModName = parts[0], ButtonText = parts[1], Priority = prio };
                }
                return null;
            }
        }
        
        // Internal load without lock (caller must hold lock)
        private static List<ModEntryData> LoadSharedEntriesInternal()
        {
            var result = new List<ModEntryData>();
            try
            {
                var filePath = GetSharedDataFile();
                if (!File.Exists(filePath))
                {
                    Debug.Log($"[ModMenuHub] Load: file does not exist");
                    return result;
                }
                
                var lines = File.ReadAllLines(filePath);
                Debug.Log($"[ModMenuHub] Load: read {lines.Length} lines from file");
                
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var entry = ModEntryData.FromLine(line);
                    if (entry != null) result.Add(entry);
                }
                
                Debug.Log($"[ModMenuHub] Load: parsed {result.Count} entries");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ModMenuHub] Error loading entries: {e.Message}");
            }
            return result;
        }
        
        // Internal save without lock (caller must hold lock)
        private static void SaveSharedEntriesInternal(List<ModEntryData> entries)
        {
            try
            {
                var filePath = GetSharedDataFile();
                var lines = new List<string>();
                foreach (var entry in entries)
                {
                    lines.Add(entry.ToLine());
                }
                File.WriteAllLines(filePath, lines);
                Debug.Log($"[ModMenuHub] Save: wrote {lines.Count} entries to file");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ModMenuHub] Error saving entries: {e.Message}");
            }
        }
        
        // Public load (acquires lock)
        public static List<ModEntryData> LoadSharedEntries()
        {
            lock (_fileLock)
            {
                var entries = LoadSharedEntriesInternal();
                if (entries.Count == 0) return entries;

                var filtered = entries.Where(e => HasLiveCallback(e.ModName)).ToList();
                if (filtered.Count != entries.Count)
                {
                    SaveSharedEntriesInternal(filtered);
                    Debug.Log($"[ModMenuHub] Pruned stale entries: {entries.Count - filtered.Count}");
                }

                return filtered;
            }
        }
        
        /// <summary>
        /// Clear all entries - useful at game startup to reset stale data.
        /// </summary>
        public static void ClearAllEntries()
        {
            try
            {
                var filePath = GetSharedDataFile();
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch { }
        }
    }
    
    /// <summary>
    /// MonoBehaviour that manages the UI.
    /// </summary>
    public class ModMenuHubRunner : MonoBehaviour
    {
        // UI state
        private UITK.UIDocument _doc;
        private UITK.VisualElement _lastRoot;
        private UITK.Button _mainMenuBtn;
        private UITK.Button _pauseMenuBtn;
        private UITK.VisualElement _modPanel;
        private UITK.VisualElement _modBackdrop;
        private UITK.ScrollView _modButtonContainer;
        private bool _buttonsWired = false;
        private bool _panelBuilt = false;
        
        // Font cache
        private static Font _uiFont;
        private static Font GetUIFont()
        {
            if (_uiFont != null) return _uiFont;
            try
            {
                var ps = UnityEngine.Object.FindFirstObjectByType<UITK.PanelSettings>(FindObjectsInactive.Include);
                if (ps != null)
                {
                    var fi = typeof(UITK.PanelSettings).GetField("m_FallbackDynamicFonts", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fi?.GetValue(ps) is System.Collections.IList list && list.Count > 0)
                        _uiFont = list[0] as Font;
                }
                if (_uiFont == null)
                    _uiFont = Resources.FindObjectsOfTypeAll<Font>().FirstOrDefault(f => f.name.Contains("Puck") || f.name.Contains("Montserrat")) ?? Font.CreateDynamicFontFromOSFont("Arial", 16);
            }
            catch { }
            return _uiFont;
        }
        
        private static void ForceUIFont(UITK.VisualElement ve)
        {
            var f = GetUIFont();
            if (f != null) ve.style.unityFont = f;
        }
        
        // Current entries
        private List<ModMenuHub.ModEntryData> _entries = new List<ModMenuHub.ModEntryData>();
        
        // Cursor state
        private bool _savedCursorState = false;
        private CursorLockMode _prevLockState = CursorLockMode.None;
        private bool _prevCursorVisible = false;
        private bool _prevMouseActive = false;
        
        // Throttling for expensive lookups
        private float _lastMenuHookTime = 0f;
        private const float MENU_HOOK_INTERVAL = 0.5f;
        private UIManager _cachedUIManager;
        
        // Reflection for menu access
        private FieldInfo _fiMainSettings;
        private FieldInfo _fiPauseSettings;
        
        // UI colors
        private static readonly Color32 ButtonBg = new Color32(57, 57, 57, 255);
        
        // Track if we're the primary runner
        private bool _isPrimary = false;
        
        public bool IsPanelOpen => _modPanel != null && _modPanel.style.display == UITK.DisplayStyle.Flex;
        
        public bool IsPrimary => _isPrimary;
        
        public void SetPrimary(bool isPrimary)
        {
            _isPrimary = isPrimary;
            Debug.Log($"[ModMenuHub] Runner marked as primary: {isPrimary}");
        }
        
        private void Awake()
        {
            _fiMainSettings = typeof(UIMainMenu).GetField("settingsButton", BindingFlags.Instance | BindingFlags.NonPublic);
            _fiPauseSettings = typeof(UIPauseMenu).GetField("settingsButton", BindingFlags.Instance | BindingFlags.NonPublic);
        }
        
        private void Update()
        {
            // Only the primary runner manages UI
            if (!_isPrimary) return;
            
            TryHookMenus();
            
            // Handle ESC key ONLY when hub panel is actually visible and displayed
            // Don't intercept ESC otherwise - let the game handle pause menu
            if (_modPanel != null && _modPanel.resolvedStyle.display == UITK.DisplayStyle.Flex)
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null && kb.escapeKey.wasPressedThisFrame)
                {
                    FullClose();
                }
            }
        }
        
        public void RefreshFromSharedData()
        {
            _entries = ModMenuHub.LoadSharedEntries();
            Debug.Log($"[ModMenuHub] RefreshFromSharedData: loaded {_entries.Count} entries, isPrimary: {_isPrimary}");
            if (_isPrimary)
            {
                RefreshModButtons();
            }
        }
        
        public void ForceRefresh()
        {
            if (!_isPrimary) return;
            _buttonsWired = false;
            _lastRoot = null;
        }
        
        private void TryHookMenus()
        {
            // Throttle expensive lookups - only run every 0.5 seconds unless we need to setup
            float now = Time.unscaledTime;
            bool needsSetup = !_buttonsWired || !_panelBuilt;
            if (!needsSetup && now - _lastMenuHookTime < MENU_HOOK_INTERVAL) return;
            _lastMenuHookTime = now;
            
            try
            {
                // Cache UIManager lookup
                if (_cachedUIManager == null)
                    _cachedUIManager = FindFirstObjectByType<UIManager>(FindObjectsInactive.Include);
                
                _doc = _cachedUIManager != null ? _cachedUIManager.UIDocument : FindFirstObjectByType<UITK.UIDocument>(FindObjectsInactive.Include);
                var root = _doc?.rootVisualElement;
                if (root == null) return;
                
                if (_lastRoot != root)
                {
                    _lastRoot = root;
                    _buttonsWired = false;
                    _panelBuilt = false;
                    _modBackdrop = null;
                    _modPanel = null;
                    _modButtonContainer = null;
                    _mainMenuBtn = null;
                    _pauseMenuBtn = null;
                }
                
                if (!_buttonsWired) TryWireButtons(root);
                if (!_panelBuilt) BuildModPanel(root);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
        
        private void TryWireButtons(UITK.VisualElement root)
        {
            if (root == null || _buttonsWired) return;
            
            var main = MonoBehaviourSingleton<UIManager>.Instance?.MainMenu;
            var pause = MonoBehaviourSingleton<UIManager>.Instance?.PauseMenu;
            
            // MAIN menu button
            if (main != null && _fiMainSettings != null && _mainMenuBtn == null)
            {
                var refBtn = _fiMainSettings.GetValue(main) as UITK.Button;
                if (refBtn?.parent != null)
                {
                    var existingBtn = refBtn.parent.Q<UITK.Button>("MOD_HUB_ModMenuHub");
                    if (existingBtn == null)
                    {
                        _mainMenuBtn = CreateMenuButton(refBtn, "MOD HUB", OpenModPanel);
                        int insertAt = Math.Min(4, refBtn.parent.childCount);
                        refBtn.parent.Insert(insertAt, _mainMenuBtn);
                        Debug.Log("[ModMenuHub] Added MOD HUB button to main menu");
                    }
                    else
                    {
                        _mainMenuBtn = existingBtn;
                    }
                    
                }
            }
            
            // PAUSE menu button
            if (pause != null && _fiPauseSettings != null && _pauseMenuBtn == null)
            {
                var refBtn = _fiPauseSettings.GetValue(pause) as UITK.Button;
                if (refBtn?.parent != null)
                {
                    var existingBtn = refBtn.parent.Q<UITK.Button>("MOD_HUB_ModMenuHub");
                    if (existingBtn == null)
                    {
                        _pauseMenuBtn = CreateMenuButton(refBtn, "MOD HUB", OpenModPanel);
                        int insertAt = Math.Min(2, refBtn.parent.childCount);
                        refBtn.parent.Insert(insertAt, _pauseMenuBtn);
                        Debug.Log("[ModMenuHub] Added MOD HUB button to pause menu");
                    }
                    else
                    {
                        _pauseMenuBtn = existingBtn;
                    }
                    
                }
            }
            
            _buttonsWired = (_mainMenuBtn != null) || (_pauseMenuBtn != null);
        }
        
        private UITK.Button CreateMenuButton(UITK.Button reference, string text, Action onClick)
        {
            var b = new UITK.Button(onClick) { text = text };
            CopyClasses(reference, b);
            b.name = text.Replace(" ", "_") + "_ModMenuHub";
            b.pickingMode = UITK.PickingMode.Position;

            // Match reference button layout so hub button spacing is consistent in menu stacks.
            b.style.width = reference.style.width;
            b.style.minWidth = reference.style.minWidth;
            b.style.maxWidth = reference.style.maxWidth;
            b.style.height = reference.style.height;
            b.style.minHeight = reference.style.minHeight;
            b.style.maxHeight = reference.style.maxHeight;
            b.style.marginLeft = reference.style.marginLeft;
            b.style.marginRight = reference.style.marginRight;
            b.style.marginTop = reference.style.marginTop;
            b.style.marginBottom = reference.style.marginBottom;
            b.style.paddingLeft = reference.style.paddingLeft;
            b.style.paddingRight = reference.style.paddingRight;
            b.style.paddingTop = reference.style.paddingTop;
            b.style.paddingBottom = reference.style.paddingBottom;
            b.style.unityTextAlign = reference.style.unityTextAlign;

            // Match exact current visual colors from the menu's native button.
            var refBg = reference.resolvedStyle.backgroundColor;
            if (refBg.a <= 0f)
            {
                if (reference.style.backgroundColor.keyword != UITK.StyleKeyword.Null)
                    refBg = reference.style.backgroundColor.value;
                else
                    refBg = ButtonBg;
            }
            b.style.backgroundColor = new UITK.StyleColor(refBg);

            var refTextColor = reference.resolvedStyle.color;
            if (refTextColor.a > 0f)
                b.style.color = new UITK.StyleColor(refTextColor);

            ForceUIFont(b);

            // Add hover effects like the game's buttons
            AddButtonFlash(b);

            return b;
        }
        
        private static void CopyClasses(UITK.VisualElement from, UITK.VisualElement to)
        {
            if (from == null || to == null) return;
            foreach (var cls in from.GetClasses()) to.AddToClassList(cls);
        }
        
        private void BuildModPanel(UITK.VisualElement root)
        {
            if (_panelBuilt || _modPanel != null) return;
            if (root == null) return;
            
            var attachRoot = _doc?.rootVisualElement ?? root;
            
            // Remove any existing elements
            attachRoot.Q<UITK.VisualElement>("ModMenuHub_Backdrop")?.RemoveFromHierarchy();
            attachRoot.Q<UITK.VisualElement>("ModMenuHub_Panel")?.RemoveFromHierarchy();
            
            // Create backdrop (transparent - clicking outside closes panel)
            _modBackdrop = new UITK.VisualElement { name = "ModMenuHub_Backdrop" };
            _modBackdrop.style.position = UITK.Position.Absolute;
            _modBackdrop.style.left = 0;
            _modBackdrop.style.top = 0;
            _modBackdrop.style.right = 0;
            _modBackdrop.style.bottom = 0;
            _modBackdrop.style.backgroundColor = new UITK.StyleColor(new Color(0, 0, 0, 0f));
            _modBackdrop.style.display = UITK.DisplayStyle.None;
            _modBackdrop.pickingMode = UITK.PickingMode.Position;
            // Clicking backdrop = same as BACK button, restore menu buttons
            _modBackdrop.RegisterCallback<UITK.PointerUpEvent>(e => CloseModPanel(true));
            attachRoot.Add(_modBackdrop);
            
            // Create main panel
            _modPanel = new UITK.VisualElement { name = "ModMenuHub_Panel" };
            _modPanel.style.position = UITK.Position.Absolute;
            _modPanel.style.left = new UITK.Length(50, UITK.LengthUnit.Percent);
            _modPanel.style.top = new UITK.Length(50, UITK.LengthUnit.Percent);
            _modPanel.style.translate = new UITK.Translate(
                new UITK.Length(-50, UITK.LengthUnit.Percent),
                new UITK.Length(-50, UITK.LengthUnit.Percent), 0f);
            _modPanel.style.minWidth = 300;
            _modPanel.style.maxHeight = new UITK.Length(70, UITK.LengthUnit.Percent);
            _modPanel.style.backgroundColor = new UITK.StyleColor(new Color(0, 0, 0, 0f));
            _modPanel.style.flexDirection = UITK.FlexDirection.Column;
            _modPanel.style.alignItems = UITK.Align.Stretch;
            _modPanel.style.display = UITK.DisplayStyle.None;
            _modPanel.pickingMode = UITK.PickingMode.Position;
            _modPanel.RegisterCallback<UITK.PointerUpEvent>(e => e.StopPropagation());
            
            // Scrollable container for mod buttons
            _modButtonContainer = new UITK.ScrollView
            {
                name = "ModButtonContainer",
                verticalScrollerVisibility = UITK.ScrollerVisibility.Auto,
                horizontalScrollerVisibility = UITK.ScrollerVisibility.Hidden
            };
            _modButtonContainer.style.flexGrow = 1;
            _modPanel.Add(_modButtonContainer);
            
            // Back button - restore menu buttons when closing
            var backBtn = CreatePanelButton("BACK", () => CloseModPanel(true));
            backBtn.style.marginTop = 8;
            _modPanel.Add(backBtn);
            
            attachRoot.Add(_modPanel);
            
            RefreshFromSharedData();
            _panelBuilt = true;
            
            Debug.Log("[ModMenuHub] Built modifications panel");
        }
        
        private void RefreshModButtons()
        {
            if (_modButtonContainer == null) return;
            
            _modButtonContainer.Clear();
            
            if (_entries.Count == 0)
            {
                var noModsLabel = new UITK.Label("No modifications registered");
                noModsLabel.style.color = new Color(0.6f, 0.6f, 0.6f, 1f);
                noModsLabel.style.fontSize = 16;
                noModsLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
                noModsLabel.style.marginTop = 20;
                noModsLabel.style.marginBottom = 20;
                _modButtonContainer.Add(noModsLabel);
                return;
            }
            
            // Add registered mod entries
            foreach (var entry in _entries)
            {
                var modName = entry.ModName;
                var btn = CreatePanelButton(entry.ButtonText, () => OnModButtonClicked(modName));
                btn.name = $"ModEntry_{entry.ModName}";
                _modButtonContainer.Add(btn);
            }
            
            Debug.Log($"[ModMenuHub] Refreshed mod buttons, count: {_entries.Count}");
        }
        
        private void OnModButtonClicked(string modName)
        {
            Debug.Log($"[ModMenuHub] Clicked mod button: {modName}");
            // Close panel but DON'T restore menu buttons - the mod's UI will manage them
            // and when returning to hub, we still have _hiddenMenuElements to restore later
            CloseModPanel(false);
            
            // Try local callback first
            if (ModMenuHub.TryInvokeCallback(modName))
                return;
            
            // Try to find callback in other assemblies
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var hubType = asm.GetType("PonceMods.Shared.ModMenuHub");
                    if (hubType == null) continue;
                    
                    var method = hubType.GetMethod("TryInvokeCallback", BindingFlags.Static | BindingFlags.Public);
                    if (method == null) continue;
                    
                    var result = method.Invoke(null, new object[] { modName });
                    if (result is bool b && b)
                        return;
                }
                catch { }
            }
            
            Debug.LogWarning($"[ModMenuHub] No callback found for: {modName}");
        }
        
        private UITK.Button CreatePanelButton(string text, Action onClick)
        {
            var btn = new UITK.Button(onClick) { text = text };
            
            btn.style.height = 45;
            btn.style.marginBottom = 6;
            btn.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            btn.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleLeft);
            btn.style.paddingLeft = 15;
            btn.style.paddingRight = 15;
            btn.style.paddingTop = 10;
            btn.style.paddingBottom = 10;
            btn.style.borderTopWidth = 0;
            btn.style.borderBottomWidth = 0;
            btn.style.borderLeftWidth = 0;
            btn.style.borderRightWidth = 0;
            
            // Force font and color
            ForceUIFont(btn);
            btn.style.color = new UITK.StyleColor(Color.white);
            btn.style.fontSize = 24;
            
            // Use AddButtonFlash which handles colors correctly
            AddButtonFlash(btn);
            
            return btn;
        }
        
        // Button flash effect - copied from UI.Panel.cs pattern
        private static void AddButtonFlash(UITK.Button b, int flashMs = 140)
        {
            var baseBg = b.style.backgroundColor.keyword != UITK.StyleKeyword.Null 
                ? b.style.backgroundColor.value 
                : (Color)ButtonBg;
            
            b.focusable = true;
            
            void SetBase() 
            { 
                b.style.backgroundColor = new UITK.StyleColor(baseBg); 
                b.style.color = new UITK.StyleColor(Color.white); 
            }
            
            SetBase();
            bool hover = false, flashing = false;
            
            b.RegisterCallback<UITK.PointerEnterEvent>(_ => 
            { 
                hover = true; 
                b.style.backgroundColor = new UITK.StyleColor(Color.white); 
                b.style.color = new UITK.StyleColor(Color.black); 
            });
            
            b.RegisterCallback<UITK.PointerLeaveEvent>(_ => 
            { 
                hover = false; 
                if (!flashing) SetBase(); 
            });
            
            b.RegisterCallback<UITK.GeometryChangedEvent>(_ => SetBase());
            
            b.RegisterCallback<UITK.PointerUpEvent>(_ =>
            {
                flashing = true;
                b.style.backgroundColor = new UITK.StyleColor(Color.white);
                b.style.color = new UITK.StyleColor(Color.black);
                b.schedule.Execute(() => { flashing = false; if (!hover) SetBase(); }).StartingIn(flashMs);
            });
        }
        
        /// <summary>
        /// Public wrapper for OpenModPanel, callable from ModMenuHub static class.
        /// </summary>
        public void OpenModPanelPublic()
        {
            OpenModPanel();
        }
        
        private void OpenModPanel()
        {
            if (_modPanel == null) return;
            
            // Save cursor state
            var ui = MonoBehaviourSingleton<UIManager>.Instance;
            if (ui != null)
            {
                _prevMouseActive = GlobalStateManager.UIState.IsMouseRequired;
                var uiState = GlobalStateManager.UIState;
                uiState.IsMouseRequired = true;
                GlobalStateManager.UIState = uiState;
            }
            _prevLockState = UnityEngine.Cursor.lockState;
            _prevCursorVisible = UnityEngine.Cursor.visible;
            _savedCursorState = true;
            
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;
            
            // Hide menu buttons when opening the hub
            HideMenuButtons(true);
            
            // Also explicitly hide MOD HUB buttons by querying for them directly
            // (instance variables may not be set if another mod instance created the button)
            var main = MonoBehaviourSingleton<UIManager>.Instance?.MainMenu;
            var pause = MonoBehaviourSingleton<UIManager>.Instance?.PauseMenu;
            if (main != null && _fiMainSettings != null)
            {
                var refBtn = _fiMainSettings.GetValue(main) as UITK.Button;
                var hubBtn = refBtn?.parent?.Q<UITK.Button>("MOD_HUB_ModMenuHub");
                if (hubBtn != null) hubBtn.style.display = UITK.DisplayStyle.None;
            }
            if (pause != null && _fiPauseSettings != null)
            {
                var refBtn = _fiPauseSettings.GetValue(pause) as UITK.Button;
                var hubBtn = refBtn?.parent?.Q<UITK.Button>("MOD_HUB_ModMenuHub");
                if (hubBtn != null) hubBtn.style.display = UITK.DisplayStyle.None;
            }
            
            // Bring to front
            var root = _doc?.rootVisualElement ?? _lastRoot;
            if (root != null)
            {
                _modBackdrop?.RemoveFromHierarchy();
                _modPanel?.RemoveFromHierarchy();
                root.Add(_modBackdrop);
                root.Add(_modPanel);
            }
            
            // Show panel
            if (_modBackdrop != null) _modBackdrop.style.display = UITK.DisplayStyle.Flex;
            _modPanel.style.display = UITK.DisplayStyle.Flex;
            
            // Refresh from shared data
            RefreshFromSharedData();
            
            Debug.Log($"[ModMenuHub] Opened modifications panel with {_entries.Count} entries");
        }
        
        private void CloseModPanel(bool restoreMenuButtons = true)
        {
            if (_modPanel == null) return;
            
            _modPanel.style.display = UITK.DisplayStyle.None;
            if (_modBackdrop != null) _modBackdrop.style.display = UITK.DisplayStyle.None;
            
            // Only restore menu buttons if requested (not when going to a mod's UI)
            if (restoreMenuButtons)
                HideMenuButtons(false);
            
            // Restore cursor state
            if (_savedCursorState)
            {
                var uiState = GlobalStateManager.UIState;
                uiState.IsMouseRequired = _prevMouseActive;
                GlobalStateManager.UIState = uiState;
                UnityEngine.Cursor.lockState = _prevLockState;
                UnityEngine.Cursor.visible = _prevCursorVisible;
                _savedCursorState = false;
            }
            
            Debug.Log("[ModMenuHub] Closed modifications panel");
        }
        
        /// <summary>
        /// Public wrapper for FullClose, callable from ModMenuHub static class.
        /// Closes everything and handles cursor based on game context.
        /// </summary>
        public void FullClosePublic()
        {
            FullClose();
        }
        
        /// <summary>
        /// Fully close all mod panels - like pressing ESC to close pause menu.
        /// Context-aware: hides cursor if on ice, shows menu if at menu.
        /// </summary>
        private void FullClose()
        {
            // Close the hub panel
            if (_modPanel != null) _modPanel.style.display = UITK.DisplayStyle.None;
            if (_modBackdrop != null) _modBackdrop.style.display = UITK.DisplayStyle.None;
            
            // Always restore menu buttons
            HideMenuButtons(false);
            
            // Determine context and handle cursor appropriately
            var nm = Unity.Netcode.NetworkManager.Singleton;
            bool isConnectedToServer = nm != null && nm.IsConnectedClient;
            bool isOnIce = isConnectedToServer && IsLocalPlayerOnIce();
            
            if (isOnIce)
            {
                // On ice: lock cursor and hide it (game state)
                UnityEngine.Cursor.lockState = CursorLockMode.Locked;
                UnityEngine.Cursor.visible = false;
                var uiState = GlobalStateManager.UIState;
                uiState.IsMouseRequired = false;
                GlobalStateManager.UIState = uiState;
            }
            else if (isConnectedToServer)
            {
                // Connected but not on ice (e.g., bench/spectating): show cursor but unlocked
                UnityEngine.Cursor.lockState = CursorLockMode.None;
                UnityEngine.Cursor.visible = true;
            }
            // else: main menu - cursor already managed by game
            
            _savedCursorState = false;
            
            Debug.Log($"[ModMenuHub] Full close - isConnected: {isConnectedToServer}, isOnIce: {isOnIce}");
        }
        
        /// <summary>
        /// Check if local player is currently on the ice (playing).
        /// </summary>
        private bool IsLocalPlayerOnIce()
        {
            try
            {
                // Simple heuristic: if cursor was locked before opening menu, player is on ice
                // _prevLockState is saved when we open a panel, so if it was Locked, we were playing
                if (_savedCursorState && _prevLockState == CursorLockMode.Locked)
                    return true;
                    
                // If time scale is 1 and we're connected, likely on ice
                var nm = Unity.Netcode.NetworkManager.Singleton;
                if (nm != null && nm.IsConnectedClient && Time.timeScale > 0)
                {
                    // Check if there's a PlayerBody in the scene which indicates active play
                    var bodies = FindObjectsByType<PlayerBody>(FindObjectsSortMode.None);
                    foreach (var body in bodies)
                    {
                        if (body != null && body.IsOwner)
                            return true;
                    }
                }
            }
            catch { }
            
            return false;
        }

        private void HideMenuButtons(bool hide)
        {
            // Only hide/show Button children, not all elements (avoids hiding the whole menu container)
            var displayStyle = hide ? UITK.DisplayStyle.None : UITK.DisplayStyle.Flex;
            int count = 0;
            
            // Main menu buttons only
            var main = MonoBehaviourSingleton<UIManager>.Instance?.MainMenu;
            if (main != null && _fiMainSettings != null)
            {
                var refBtn = _fiMainSettings.GetValue(main) as UITK.Button;
                if (refBtn?.parent != null)
                {
                    foreach (var child in refBtn.parent.Children())
                    {
                        // Only toggle Button elements, not container elements
                        if (child is UITK.Button)
                        {
                            child.style.display = displayStyle;
                            count++;
                        }
                    }
                }
            }
            
            // Pause menu buttons only
            var pause = MonoBehaviourSingleton<UIManager>.Instance?.PauseMenu;
            if (pause != null && _fiPauseSettings != null)
            {
                var refBtn = _fiPauseSettings.GetValue(pause) as UITK.Button;
                if (refBtn?.parent != null)
                {
                    foreach (var child in refBtn.parent.Children())
                    {
                        // Only toggle Button elements, not container elements
                        if (child is UITK.Button)
                        {
                            child.style.display = displayStyle;
                            count++;
                        }
                    }
                }
            }
            
            Debug.Log($"[ModMenuHub] {(hide ? "Hid" : "Restored")} {count} menu buttons");
        }
        
        private void OnDestroy()
        {
            // Restore any hidden buttons before destroying
            HideMenuButtons(false);
            
            _modBackdrop?.RemoveFromHierarchy();
            _modPanel?.RemoveFromHierarchy();
        }
    }
}
