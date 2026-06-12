// MaxPracticeUI.cs - ModMenuHub UI for MaxPractice

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using UITK = UnityEngine.UIElements;

namespace MaxPractice
{
    public class MaxPracticeUI : MonoBehaviour
    {
        private UITK.UIDocument _doc;
        private UITK.VisualElement _panel;
        private UITK.VisualElement _backdrop;
        private UITK.VisualElement _commandsSectionVE;
        private UITK.VisualElement _helpSectionVE;
        private UITK.ScrollView _commandsScroll;
        private UITK.Button _tabCommands;
        private UITK.Button _tabHelp;
        private UITK.Button _tabServer;
        private UITK.VisualElement _serverSectionVE;
        private UITK.VisualElement _captureOverlay;
        private UITK.Label _captureLabel;

        private bool _isVisible;
        private bool _savedCursorState;
        private CursorLockMode _prevLockState = CursorLockMode.None;
        private bool _prevCursorVisible;

        private bool _isCapturing;
        private UITK.Button _captureButton;
        private Action<string> _captureCommit;
        private bool _captureStarted;
        private float _captureWindowEnd;
        private float _captureLastBestAt;
        private int _captureBestWeight;
        private MaxPracticeKeybindManager.KeyChord _captureBestChord;

        private enum UiTab { Commands, Help, Server }
        private UiTab _activeTab = UiTab.Commands;

        private class CmdRow
        {
            public UITK.DropdownField Dropdown;
            public string Chord;
            public UITK.VisualElement Chips;
            public UITK.VisualElement Row;
        }

        private readonly List<CmdRow> _cmdRows = new List<CmdRow>();

        private static readonly Dictionary<string, string> LABEL_TO_COMMAND = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "SPAWNPUCK", "/s" },
            { "BACKPASS", "/backpass" },
            { "PASS", "/pass" },
            { "YOYO", "/yoyo" },
            { "POP", "/pop" },
            { "CONES", "/cones" },
            { "MINEFIELD", "/minefield" },
            { "TRAFFIC", "/traffic" },
            { "INFINITESTAMINA", "/infinitestamina" },
            { "RECORDTRAFFIC", "/recordtraffic" },
        };

        private static readonly Dictionary<string, string> COMMAND_TO_LABEL = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "/spawnpuck", "SPAWNPUCK" },
            { "/s", "SPAWNPUCK" },
            { "/backpass", "BACKPASS" },
            { "/pass", "PASS" },
            { "/yoyo", "YOYO" },
            { "/pop", "POP" },
            { "/cones", "CONES" },
            { "/minefield", "MINEFIELD" },
            { "/traffic", "TRAFFIC" },
            { "/infinitestamina", "INFINITESTAMINA" },
            { "/recordtraffic", "RECORDTRAFFIC" },
        };

        private static readonly List<string> ALL_COMMAND_LABELS = new List<string>
        {
            "SPAWNPUCK", "BACKPASS", "PASS",
            "YOYO", "POP",
            "CONES", "MINEFIELD", "TRAFFIC",
            "INFINITESTAMINA", "RECORDTRAFFIC",
        };

        private static readonly Color32 TextFieldBg = new Color32(57, 57, 57, 255);
        private static readonly Color32 RowBg = new Color32(61, 61, 61, 255);
        private static readonly Color32 ButtonBg = new Color32(57, 57, 57, 255);
        private static readonly Color32 PanelBg = new Color32(48, 48, 47, 255);
        private static readonly Color32 TabActiveBg = new Color32(80, 80, 80, 255);
        private static readonly Color32 TabInactiveBg = new Color32(66, 66, 66, 255);
        private static readonly Color32 ChipBg = new Color32(80, 80, 80, 255);
        private static readonly Color32 ChipXBg = new Color32(100, 100, 100, 255);
        private static readonly Color BtnBrightGray = (Color)RowBg;

        private const float BTN_H = 36f;
        private const float BTN_W = 120f;
        private const float BTN_W_RM = 150f;
        private const float GAP = 8f;

        private static Font _uiFont;

        private static Font GetUIFont()
        {
            if (_uiFont != null) return _uiFont;
            try
            {
                var uiManager = MonoBehaviourSingleton<UIManager>.Instance;
                if (uiManager != null && uiManager.PanelSettings != null)
                {
                    var textSettings = uiManager.PanelSettings.textSettings;
                    if (textSettings != null && textSettings.defaultFontAsset != null)
                    {
                        _uiFont = textSettings.defaultFontAsset.sourceFontFile;
                        if (_uiFont != null) return _uiFont;
                    }
                }
            }
            catch { }

            try { _uiFont = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
            if (_uiFont == null)
            {
                try
                {
                    _uiFont = Font.CreateDynamicFontFromOSFont(
                        new[] { "Arial", "Helvetica Neue", "Segoe UI", "Liberation Sans", "Noto Sans" }, 16);
                }
                catch { }
            }
            return _uiFont;
        }

        private static void ForceUIFont(UITK.VisualElement ve)
        {
            var f = GetUIFont();
            if (f == null) return;
            ve.style.unityFont = f;
        }

        private static void MakeReadable(UITK.Label l)
        {
            l.style.color = Color.white;
            l.style.unityFont = GetUIFont();
        }

        private static void MakeReadable(UITK.Button b)
        {
            b.style.color = Color.white;
            b.style.unityFont = GetUIFont();
        }

        private static void MakeReadable(UITK.TextField tf)
        {
            tf.style.color = Color.white;
            tf.style.unityFont = GetUIFont();
            tf.style.backgroundColor = new UITK.StyleColor(TextFieldBg);

            var input = tf.childCount > 0 ? tf.ElementAt(0) : null;
            if (input != null)
            {
                input.style.color = Color.white;
                input.style.unityFont = GetUIFont();
                input.style.backgroundColor = new UITK.StyleColor(TextFieldBg);
            }
        }

        private void Start()
        {
            MaxPracticeKeybindManager.Initialize();
            try
            {
                PonceMods.Shared.ModMenuHub.RegisterMod("MaxPractice", "MAX PRACTICE", ToggleUI, 40);
                PonceMods.Shared.ModMenuHub.Initialize("MaxPractice");
            }
            catch (Exception e)
            {
                Debug.LogError("[MaxPractice] ModMenuHub registration failed: " + e);
            }
        }

        private void OnDestroy()
        {
            try { PonceMods.Shared.ModMenuHub.UnregisterMod("MaxPractice"); } catch { }
            _panel?.RemoveFromHierarchy();
            _backdrop?.RemoveFromHierarchy();
        }

        public void ToggleUI()
        {
            if (_isVisible) HideUI();
            else ShowUI();
        }

        private void ShowUI()
        {
            if (_panel == null) CreateUI();
            if (_panel == null) return;

            _prevLockState = UnityEngine.Cursor.lockState;
            _prevCursorVisible = UnityEngine.Cursor.visible;
            _savedCursorState = true;

            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;

            _backdrop.style.display = DisplayStyle.Flex;
            _panel.style.display = DisplayStyle.Flex;
            _isVisible = true;

            LoadRowsFromConfig();
        }

        private void HideUI()
        {
            if (_panel == null) return;
            bool wasVisible = _isVisible;

            if (_isCapturing) CancelCapture();

            _backdrop.style.display = DisplayStyle.None;
            _panel.style.display = DisplayStyle.None;
            _isVisible = false;

            if (!wasVisible) return;
            try { PonceMods.Shared.ModMenuHub.OpenPanel(); }
            catch { RestoreCursor(); }
        }

        private void FullCloseUI()
        {
            if (_panel == null) return;
            if (_isCapturing) CancelCapture();

            _backdrop.style.display = DisplayStyle.None;
            _panel.style.display = DisplayStyle.None;
            _isVisible = false;

            try { PonceMods.Shared.ModMenuHub.FullClose(); }
            catch { RestoreCursor(); }
        }

        private void RestoreCursor()
        {
            if (!_savedCursorState) return;
            UnityEngine.Cursor.lockState = _prevLockState;
            UnityEngine.Cursor.visible = _prevCursorVisible;
            _savedCursorState = false;
        }

        private void Update()
        {
            if (!_isVisible && !_isCapturing)
                ProcessKeybindInputs();

            if (!_isVisible) return;

            var kb = Keyboard.current;
            if (kb == null && !_isCapturing) return;

            if (_isCapturing)
            {
                if (kb != null && kb.escapeKey.wasPressedThisFrame)
                {
                    CancelCapture();
                    return;
                }

                if (!_captureStarted)
                {
                    if (AnyInputPressedThisFrame())
                    {
                        _captureStarted = true;
                        _captureWindowEnd = Time.unscaledTime + 1.0f;
                        if (_captureLabel != null)
                            _captureLabel.text = "Release keys/buttons to confirm binding...";
                    }
                }
                else
                {
                    var chord = SnapshotCurrentChord();
                    bool any = (chord.Keys != null && chord.Keys.Length > 0) || chord.Ctrl || chord.Shift || chord.Alt;
                    if (any)
                    {
                        int w = ChordWeight(chord);
                        if (w > _captureBestWeight)
                        {
                            _captureBestChord = chord;
                            _captureBestWeight = w;
                            _captureLastBestAt = Time.unscaledTime;
                            if (_captureLabel != null)
                                _captureLabel.text = MaxPracticeKeybindManager.ChordToString(chord) + " - release to confirm";
                        }
                    }

                    bool allReleased = !HasAnyInputDown();
                    if (_captureBestWeight >= 0 &&
                        (allReleased || Time.unscaledTime >= _captureWindowEnd || Time.unscaledTime - _captureLastBestAt > 0.15f))
                    {
                        FinishCapture(MaxPracticeKeybindManager.ChordToString(_captureBestChord));
                        return;
                    }
                }
            }
            else
            {
                if (kb.escapeKey.wasPressedThisFrame)
                {
                    FullCloseUI();
                    return;
                }
            }

            if (UnityEngine.Cursor.lockState != CursorLockMode.None)
                UnityEngine.Cursor.lockState = CursorLockMode.None;
            if (!UnityEngine.Cursor.visible)
                UnityEngine.Cursor.visible = true;
        }

        private void CreateUI()
        {
            try
            {
                var uiMgr = FindFirstObjectByType<UIManager>(FindObjectsInactive.Include);
                _doc = uiMgr != null ? uiMgr.UIDocument : FindFirstObjectByType<UITK.UIDocument>(FindObjectsInactive.Include);
                var root = _doc != null ? _doc.rootVisualElement : null;
                if (root == null) return;

                _backdrop = new UITK.VisualElement { name = "MaxPractice_Backdrop" };
                _backdrop.style.position = Position.Absolute;
                _backdrop.style.left = 0;
                _backdrop.style.top = 0;
                _backdrop.style.right = 0;
                _backdrop.style.bottom = 0;
                _backdrop.style.backgroundColor = new UITK.StyleColor(new Color(0, 0, 0, 0f));
                _backdrop.style.display = UITK.DisplayStyle.None;
                _backdrop.pickingMode = UITK.PickingMode.Position;
                _backdrop.RegisterCallback<UITK.PointerUpEvent>(_ => HideUI());

                _panel = new UITK.VisualElement { name = "MaxPractice_Panel" };
                _panel.style.position = UITK.Position.Absolute;
                _panel.style.left = new UITK.Length(50, UITK.LengthUnit.Percent);
                _panel.style.top = new UITK.Length(50, UITK.LengthUnit.Percent);
                _panel.style.translate = new UITK.Translate(
                    new UITK.Length(-50, UITK.LengthUnit.Percent),
                    new UITK.Length(-50, UITK.LengthUnit.Percent), 0f);
                int targetW = Mathf.Clamp(Mathf.RoundToInt(Screen.width * 0.58f), 680, 980);
                _panel.style.width = targetW;
                _panel.style.height = new UITK.Length(84, UITK.LengthUnit.Percent);
                _panel.style.minHeight = new UITK.Length(56, UITK.LengthUnit.Percent);
                _panel.style.maxHeight = new UITK.Length(56, UITK.LengthUnit.Percent);
                _panel.style.overflow = UITK.Overflow.Hidden;
                _panel.style.flexDirection = UITK.FlexDirection.Column;
                _panel.style.backgroundColor = new UITK.StyleColor(PanelBg);
                _panel.style.paddingLeft = 8;
                _panel.style.paddingRight = 8;
                _panel.style.paddingTop = 8;
                _panel.style.paddingBottom = 8;
                _panel.style.display = UITK.DisplayStyle.None;
                _panel.RegisterCallback<UITK.PointerUpEvent>(e => e.StopPropagation());

                var bigTitle = new UITK.Label("MAX PRACTICE");
                bigTitle.style.unityFontStyleAndWeight = FontStyle.Normal;
                bigTitle.style.fontSize = 50;
                bigTitle.style.color = Color.white;
                bigTitle.style.marginBottom = 16;
                _panel.Add(bigTitle);

                var tabBar = new UITK.VisualElement();
                tabBar.style.flexDirection = UITK.FlexDirection.Row;
                tabBar.style.marginBottom = 8;
                tabBar.style.height = 50;
                _panel.Add(tabBar);
                ForceUIFont(_panel);

                _tabCommands = MakeTab("COMMANDS", () => ShowTab(UiTab.Commands));
                _tabHelp = MakeTab("HELP", () => ShowTab(UiTab.Help));
                tabBar.Add(_tabCommands);
                tabBar.Add(_tabHelp);
                AddTabHover(_tabCommands, () => _activeTab == UiTab.Commands);
                AddTabHover(_tabHelp, () => _activeTab == UiTab.Help);
                _tabServer = MakeTab("SERVER", () => ShowTab(UiTab.Server));
                // Last tab keeps no right margin so SERVER sits flush with the
                // right edge the same way COMMANDS hugs the left.
                _tabServer.style.marginRight = 0;
                tabBar.Add(_tabServer);
                AddTabHover(_tabServer, () => _activeTab == UiTab.Server);

                var scroll = new UITK.ScrollView
                {
                    verticalScrollerVisibility = UITK.ScrollerVisibility.Auto,
                    horizontalScrollerVisibility = UITK.ScrollerVisibility.Hidden
                };
                scroll.style.flexGrow = 1;
                // A flex item's default min-height is its content size, so a
                // long list would keep the scroll view tall and push the footer
                // past the panel's clipped bottom; pin min-height to 0 so the
                // list shrinks and the footer stays in.
                scroll.style.flexShrink = 1;
                scroll.style.minHeight = 0;
                _panel.Add(scroll);

                _commandsSectionVE = new UITK.VisualElement();
                _helpSectionVE = new UITK.VisualElement();
                _serverSectionVE = new UITK.VisualElement();
                scroll.Add(_commandsSectionVE);
                scroll.Add(_helpSectionVE);
                scroll.Add(_serverSectionVE);
                BuildCommandsSection();
                BuildHelpSection();
                BuildServerSection();

                var footer = new UITK.VisualElement();
                footer.style.flexDirection = UITK.FlexDirection.Row;
                footer.style.justifyContent = UITK.Justify.SpaceBetween;
                footer.style.marginTop = 8;
                footer.style.flexShrink = 0;   // footer keeps its size; the list shrinks instead

                var coffee = MakeFooterButton("COFFEE?", () => Application.OpenURL("https://buymeacoffee.com/amikiir"));
                var rightButtons = new UITK.VisualElement();
                rightButtons.style.flexDirection = UITK.FlexDirection.Row;

                var reset = MakeFooterButton("RESET TO DEFAULT", () =>
                {
                    ResetDefaults();
                    LoadRowsFromConfig();
                });
                reset.style.marginLeft = 8;

                var close = MakeFooterButton("CLOSE", () =>
                {
                    if (TryApplyFromPanel())
                        MaxPracticeKeybindManager.SaveConfig();
                    HideUI();
                });
                close.style.marginLeft = 8;
                close.style.paddingRight = 182;

                rightButtons.Add(reset);
                rightButtons.Add(close);

                footer.Add(coffee);
                footer.Add(rightButtons);
                _panel.Add(footer);

                ShowTab(UiTab.Commands);

                root.Add(_backdrop);
                root.Add(_panel);

                // Capture overlay (full-screen key rebind prompt)
                _captureOverlay = new UITK.VisualElement { name = "MaxPractice_CaptureOverlay" };
                _captureOverlay.style.position = UITK.Position.Absolute;
                _captureOverlay.style.left = 0; _captureOverlay.style.right = 0;
                _captureOverlay.style.top = 0; _captureOverlay.style.bottom = 0;
                _captureOverlay.style.backgroundColor = new UITK.StyleColor(new Color(0.1f, 0.1f, 0.15f, 0.95f));
                _captureOverlay.style.display = UITK.DisplayStyle.None;
                _captureOverlay.pickingMode = UITK.PickingMode.Position;
                _captureOverlay.RegisterCallback<UITK.PointerUpEvent>(e => e.StopPropagation());
                ForceUIFont(_captureOverlay);

                var captureCenter = new UITK.VisualElement();
                captureCenter.style.position = UITK.Position.Absolute;
                captureCenter.style.left = new UITK.Length(50, UITK.LengthUnit.Percent);
                captureCenter.style.top = new UITK.Length(50, UITK.LengthUnit.Percent);
                captureCenter.style.translate = new UITK.Translate(
                    new UITK.Length(-50, UITK.LengthUnit.Percent),
                    new UITK.Length(-50, UITK.LengthUnit.Percent), 0f);
                captureCenter.style.alignItems = UITK.Align.Center;
                captureCenter.style.justifyContent = UITK.Justify.Center;
                captureCenter.style.flexDirection = UITK.FlexDirection.Column;

                var captureTitle = new UITK.Label("KEY REBIND");
                captureTitle.style.fontSize = 72;
                captureTitle.style.color = Color.white;
                captureTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
                captureTitle.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
                captureTitle.style.marginBottom = 32;
                ForceUIFont(captureTitle);
                captureCenter.Add(captureTitle);

                _captureLabel = new UITK.Label("Press a key or combination of keys to rebind.");
                _captureLabel.style.fontSize = 24;
                _captureLabel.style.color = new Color(0.9f, 0.9f, 0.9f, 1f);
                _captureLabel.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
                _captureLabel.style.whiteSpace = UITK.WhiteSpace.Normal;
                _captureLabel.style.maxWidth = 600;
                ForceUIFont(_captureLabel);
                captureCenter.Add(_captureLabel);

                _captureOverlay.Add(captureCenter);
                root.Add(_captureOverlay);
            }
            catch (Exception e)
            {
                Debug.LogError("[MaxPractice] CreateUI failed: " + e);
            }
        }

        private UITK.Button MakeTab(string text, Action onClick)
        {
            var b = new UITK.Button(onClick) { text = text.ToUpperInvariant() };
            b.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            b.style.height = 50;
            b.style.flexGrow = 1;
            b.style.paddingLeft = 8;
            b.style.paddingRight = 8;
            b.style.marginRight = 8;
            // Spacing below the tab strip is owned by tabBar.marginBottom; the
            // button keeps no bottom margin of its own.
            b.style.marginBottom = 0;
            b.style.fontSize = 24;
            b.style.borderTopLeftRadius = 6;
            b.style.borderTopRightRadius = 6;
            b.style.borderBottomLeftRadius = 0;
            b.style.borderBottomRightRadius = 0;
            b.style.borderBottomWidth = 0;
            b.style.borderBottomColor = new UITK.StyleColor(Color.white);
            b.style.backgroundColor = new UITK.StyleColor(TabInactiveBg);
            b.style.color = new Color(0.7f, 0.7f, 0.7f);
            ForceUIFont(b);
            return b;
        }

        // PlayerQoL footer buttons: no bottom margins of their own - the 8px
        // gap under the row comes from the panel's bottom padding.
        private UITK.Button MakeFooterButton(string text, Action onClick)
        {
            var b = new UITK.Button(onClick) { text = text.ToUpperInvariant() };
            b.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            b.style.height = 50;
            b.style.paddingLeft = 18;
            b.style.paddingRight = 18;
            b.style.backgroundColor = BtnBrightGray;
            AddButtonFlash(b);
            return b;
        }

        private void BuildCommandsSection()
        {
            _commandsSectionVE.Clear();

            var head = new UITK.Label("BIND COMMANDS");
            MakeReadable(head);
            head.style.unityFontStyleAndWeight = FontStyle.Normal;
            head.style.fontSize = 30;
            head.style.marginBottom = 10;
            _commandsSectionVE.Add(head);

            _commandsScroll = new UITK.ScrollView
            {
                verticalScrollerVisibility = UITK.ScrollerVisibility.Auto,
                horizontalScrollerVisibility = UITK.ScrollerVisibility.Hidden
            };
            _commandsScroll.style.flexGrow = 1;
            _commandsSectionVE.Add(_commandsScroll);

            var addCmd = new UITK.Button(() => AddCommandRow("/s", "")) { text = "ADD NEW COMMAND ROW" };
            addCmd.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            addCmd.style.height = 50;
            addCmd.style.marginBottom = 22;
            addCmd.style.paddingLeft = 8;
            addCmd.style.paddingRight = 8;
            addCmd.style.backgroundColor = BtnBrightGray;
            AddButtonFlash(addCmd);
            _commandsSectionVE.Add(addCmd);
        }

        private void LoadRowsFromConfig()
        {
            _cmdRows.Clear();
            _commandsScroll?.Clear();

            MaxPracticeKeybindManager.LoadConfig();
            foreach (var raw in MaxPracticeKeybindManager.Config.commandBinds)
            {
                int idx = raw.LastIndexOf(':');
                if (idx < 1) continue;
                var cmd = raw.Substring(0, idx).Trim();
                var chord = raw.Substring(idx + 1).Trim();
                AddCommandRow(cmd, chord);
            }
        }

        private void AddCommandRow(string cmd, string chordSpec)
        {
            var model = new CmdRow { Chord = chordSpec ?? "" };

            var row = new UITK.VisualElement();
            row.style.flexDirection = UITK.FlexDirection.Row;
            row.style.alignItems = UITK.Align.Center;
            row.style.height = 50;
            row.style.marginBottom = 8;
            row.style.backgroundColor = new UITK.StyleColor(RowBg);
            row.style.paddingLeft = 12;
            row.style.paddingRight = 10;
            row.style.paddingTop = 8;
            row.style.paddingBottom = 8;

            var left = new UITK.DropdownField();
            left.choices = ALL_COMMAND_LABELS;
            string normalizedCmd = string.IsNullOrWhiteSpace(cmd) ? "/spawnpuck" : (cmd.StartsWith("/") ? cmd : "/" + cmd.TrimStart('/'));
            left.value = COMMAND_TO_LABEL.TryGetValue(normalizedCmd, out var label) ? label : "SPAWNPUCK";
            StyleDropdown(left);

            var arrow = new UITK.Label(" ▼");
            arrow.style.position = Position.Absolute;
            arrow.style.right = 8;
            arrow.style.unityTextAlign = TextAnchor.MiddleRight;
            left.Add(arrow);

            row.Add(left);

            var remove = new UITK.Button(() =>
            {
                row.RemoveFromHierarchy();
                _cmdRows.Remove(model);
            });
            StyleRowButton(remove, BTN_W_RM, "REMOVE");
            remove.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            row.Add(remove);

            var chipsRoot = new UITK.VisualElement();
            chipsRoot.style.flexDirection = UITK.FlexDirection.Row;
            chipsRoot.style.justifyContent = UITK.Justify.FlexEnd;
            chipsRoot.style.alignItems = UITK.Align.Center;
            chipsRoot.style.flexGrow = 1;
            chipsRoot.style.flexShrink = 1;
            chipsRoot.style.minWidth = 0;
            chipsRoot.style.marginLeft = 4;
            chipsRoot.style.marginTop = 4;
            chipsRoot.style.marginRight = 8;
            row.Add(chipsRoot);
            model.Chips = chipsRoot;

            var right = new UITK.VisualElement();
            right.style.flexDirection = UITK.FlexDirection.Row;
            right.style.alignItems = UITK.Align.Center;
            right.style.flexShrink = 0;
            row.Add(right);

            var bind = new UITK.Button();
            bind.clicked += () =>
            {
                StartCapture(bind, spec =>
                {
                    model.Chord = spec;
                    RefreshChips(model);
                });
            };
            StyleRowButton(bind, BTN_W, "BIND");
            bind.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            right.Add(bind);

            model.Dropdown = left;
            model.Row = row;
            _cmdRows.Add(model);
            _commandsScroll.Add(row);

            RefreshChips(model);
        }

        private void RefreshChips(CmdRow model)
        {
            model.Chips.Clear();
            if (string.IsNullOrWhiteSpace(model.Chord)) return;

            model.Chips.Add(MakeChip(model.Chord, () =>
            {
                model.Chord = "";
                RefreshChips(model);
            }));
        }

        private UITK.VisualElement MakeChip(string text, Action onRemove)
        {
            var chip = new UITK.VisualElement();
            chip.style.flexDirection = UITK.FlexDirection.Row;
            chip.style.alignItems = UITK.Align.Center;
            chip.style.backgroundColor = new UITK.StyleColor(ChipBg);
            chip.style.paddingLeft = 8;
            chip.style.paddingRight = 4;
            chip.style.paddingTop = 4;
            chip.style.paddingBottom = 4;
            chip.style.marginRight = 4;
            chip.style.borderTopLeftRadius = 4;
            chip.style.borderTopRightRadius = 4;
            chip.style.borderBottomLeftRadius = 4;
            chip.style.borderBottomRightRadius = 4;

            var label = new UITK.Label(text);
            label.style.fontSize = 14;
            label.style.color = Color.white;
            MakeReadable(label);
            chip.Add(label);

            var xBtn = new UITK.Button(() => onRemove?.Invoke()) { text = "×" };
            xBtn.style.width = 20;
            xBtn.style.height = 20;
            xBtn.style.marginLeft = 4;
            xBtn.style.backgroundColor = new UITK.StyleColor(ChipXBg);
            xBtn.style.fontSize = 14;
            xBtn.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            xBtn.style.paddingLeft = 0;
            xBtn.style.paddingRight = 0;
            xBtn.style.paddingTop = 0;
            xBtn.style.paddingBottom = 0;
            xBtn.style.color = Color.white;
            MakeReadable(xBtn);
            AddChipButtonFlash(xBtn);
            chip.Add(xBtn);

            return chip;
        }

        private void BuildHelpSection()
        {
            _helpSectionVE.Clear();

            var title = new UITK.Label("HELP");
            MakeReadable(title);
            title.style.unityFontStyleAndWeight = FontStyle.Normal;
            title.style.fontSize = 30;
            title.style.marginBottom = 10;
            _helpSectionVE.Add(title);

            AddHelpRow("/spawnpuck or /s", "Spawn a puck in front of you.");
            AddHelpRow("/tapspawn", "Toggle tap spawn mode.");
            AddHelpRow("/backpass or /bp", "Toggle backpass mode.");
            AddHelpRow("/tapbackpass", "Toggle tap backpass mode.");
            AddHelpRow("/clear", "Clear all spawned pucks and drills.");
            AddHelpRow("/clearall", "Clear all spawned objects including traffic.");
            AddHelpRow("/pass /unpass", "Toggle pass spawner.");
            AddHelpRow("/tappass", "Toggle tap pass mode.");
            AddHelpRow("/yoyo", "Toggle yoyo trainer.");
            AddHelpRow("/tapyoyo", "Toggle tap yoyo mode.");
            AddHelpRow("/pop", "Pop your last touched puck upward.");
            AddHelpRow("/saveprac /stopsaveprac", "Toggle save practice drill.");
            AddHelpRow("/tipprac /stoptipprac", "Toggle tip practice drill.");
            AddHelpRow("/dummy /dummyred /dummyblue", "Toggle AI goalie commands.");
            AddHelpRow("/cones and /minefield", "Spawn stickhandling drills.");
            AddHelpRow("/traffic /cleartraffic", "Spawn or clear traffic skaters.");
            AddHelpRow("/recordtraffic /stoprecord", "Record and stop traffic paths.");
            AddHelpRow("/stamina or /is", "Toggle infinite stamina.");
        }

        private void AddHelpRow(string command, string description)
        {
            var row = new UITK.VisualElement();
            row.style.flexDirection = UITK.FlexDirection.Column;
            row.style.height = 50;
            row.style.marginBottom = 8;
            row.style.backgroundColor = new UITK.StyleColor(RowBg);
            row.style.paddingLeft = 12;
            row.style.paddingRight = 10;
            row.style.paddingTop = 8;
            row.style.paddingBottom = 8;

            var cmd = new UITK.Label(command);
            MakeReadable(cmd);
            cmd.style.fontSize = 17;
            row.Add(cmd);

            var desc = new UITK.Label(description);
            MakeReadable(desc);
            desc.style.fontSize = 13;
            desc.style.opacity = 0.8f;
            row.Add(desc);

            _helpSectionVE.Add(row);
        }

        private bool TryApplyFromPanel()
        {
            try
            {
                var outBinds = new List<string>();
                foreach (var row in _cmdRows)
                {
                    var selected = row.Dropdown?.value?.Trim();
                    if (string.IsNullOrWhiteSpace(selected)) continue;

                    string cmd;
                    if (!LABEL_TO_COMMAND.TryGetValue(selected, out cmd))
                    {
                        cmd = selected.StartsWith("/") ? selected : "/" + selected.TrimStart('/');
                    }

                    var chord = row.Chord?.Trim();
                    if (string.IsNullOrWhiteSpace(chord)) continue;
                    outBinds.Add(cmd + ":" + chord);
                }

                MaxPracticeKeybindManager.Config.commandBinds = outBinds;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[MaxPractice] Failed applying UI changes: " + e.Message);
                return false;
            }
        }

        private void ResetDefaults()
        {
            if (_isCapturing) CancelCapture();

            // No default binds - reset just clears every row.
            MaxPracticeKeybindManager.Config.commandBinds.Clear();
            MaxPracticeKeybindManager.SaveConfig();
        }

        private void StartCapture(UITK.Button button, Action<string> onCommit)
        {
            _isCapturing = true;
            _captureButton = button;
            _captureCommit = onCommit;
            _captureStarted = false;
            _captureWindowEnd = 0f;
            _captureLastBestAt = 0f;
            _captureBestWeight = -1;
            _captureBestChord = default;

            if (_captureButton != null)
            {
                _captureButton.text = "PRESS KEY";
                _captureButton.style.backgroundColor = new UITK.StyleColor(new Color32(100, 80, 40, 255));
            }

            if (_captureLabel != null)
                _captureLabel.text = "Press a key or combination of modifier + key to rebind.";
            if (_captureOverlay != null)
            {
                _captureOverlay.style.display = UITK.DisplayStyle.Flex;
                _captureOverlay.BringToFront();
            }
        }

        private void FinishCapture(string spec)
        {
            _isCapturing = false;
            if (_captureOverlay != null) _captureOverlay.style.display = UITK.DisplayStyle.None;
            _captureCommit?.Invoke(spec);

            if (_captureButton != null)
            {
                _captureButton.text = "BIND";
                _captureButton.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            }

            _captureButton = null;
            _captureCommit = null;
        }

        private void CancelCapture()
        {
            if (!_isCapturing) return;
            _isCapturing = false;
            if (_captureOverlay != null) _captureOverlay.style.display = UITK.DisplayStyle.None;

            if (_captureButton != null)
            {
                _captureButton.text = "BIND";
                _captureButton.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            }

            _captureButton = null;
            _captureCommit = null;
        }

        private int ChordWeight(MaxPracticeKeybindManager.KeyChord chord)
        {
            int w = chord.Keys != null ? chord.Keys.Length : 0;
            if (chord.Ctrl) w++;
            if (chord.Shift) w++;
            if (chord.Alt) w++;
            return w;
        }

        private MaxPracticeKeybindManager.KeyChord SnapshotCurrentChord()
        {
            var keys = new List<KeyCode>();
            bool ctrl = (Keyboard.current?.leftCtrlKey?.isPressed ?? false) || (Keyboard.current?.rightCtrlKey?.isPressed ?? false);
            bool shift = (Keyboard.current?.leftShiftKey?.isPressed ?? false) || (Keyboard.current?.rightShiftKey?.isPressed ?? false);
            bool alt = (Keyboard.current?.leftAltKey?.isPressed ?? false) || (Keyboard.current?.rightAltKey?.isPressed ?? false);

            foreach (KeyCode k in Enum.GetValues(typeof(KeyCode)))
            {
                if (!MaxPracticeKeybindManager.IsAllowedKey(k)) continue;
                if (MaxPracticeKeybindManager.IsModifierKey(k)) continue;
                if (IsKeyPressed(k)) keys.Add(k);
            }

            keys.Sort((a, b) => a.CompareTo(b));
            return new MaxPracticeKeybindManager.KeyChord
            {
                Keys = keys.ToArray(),
                Ctrl = ctrl,
                Shift = shift,
                Alt = alt,
            };
        }

        private bool AnyInputPressedThisFrame()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            return (kb != null && kb.anyKey.wasPressedThisFrame) ||
                   (mouse?.leftButton.wasPressedThisFrame ?? false) ||
                   (mouse?.rightButton.wasPressedThisFrame ?? false) ||
                   (mouse?.middleButton.wasPressedThisFrame ?? false) ||
                   (mouse?.forwardButton?.wasPressedThisFrame ?? false) ||
                   (mouse?.backButton?.wasPressedThisFrame ?? false);
        }

        private bool HasAnyInputDown()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            return (kb != null && kb.anyKey.isPressed) ||
                   (mouse != null &&
                    (mouse.leftButton.isPressed || mouse.rightButton.isPressed || mouse.middleButton.isPressed ||
                     (mouse.forwardButton?.isPressed ?? false) || (mouse.backButton?.isPressed ?? false)));
        }

        private void ProcessKeybindInputs()
        {
            var kb = Keyboard.current;
            if (kb == null && Mouse.current == null) return;
            if (IsChatOpen()) return;

            var bindings = MaxPracticeKeybindManager.GetBindings();
            foreach (var kvp in bindings)
            {
                var chord = kvp.Key;
                if (chord.Ctrl != kb.ctrlKey.isPressed) continue;
                if (chord.Shift != kb.shiftKey.isPressed) continue;
                if (chord.Alt != kb.altKey.isPressed) continue;

                bool hasKeys = chord.Keys != null && chord.Keys.Length > 0;
                bool allPressed = true;
                bool anyPressedThisFrame = false;

                if (hasKeys)
                {
                    for (int i = 0; i < chord.Keys.Length; i++)
                    {
                        var key = chord.Keys[i];
                        if (!IsKeyPressed(key)) { allPressed = false; break; }
                        if (WasKeyPressedThisFrame(key)) anyPressedThisFrame = true;
                    }
                    if (!allPressed || !anyPressedThisFrame) continue;
                }
                else
                {
                    bool modPressed = (chord.Ctrl && (kb.leftCtrlKey.wasPressedThisFrame || kb.rightCtrlKey.wasPressedThisFrame)) ||
                                      (chord.Shift && (kb.leftShiftKey.wasPressedThisFrame || kb.rightShiftKey.wasPressedThisFrame)) ||
                                      (chord.Alt && (kb.leftAltKey.wasPressedThisFrame || kb.rightAltKey.wasPressedThisFrame));
                    if (!modPressed) continue;
                }

                TrySendChat(kvp.Value);
            }
        }

        private static bool IsKeyPressed(KeyCode kc)
        {
            var mouse = Mouse.current;
            switch (kc)
            {
                case KeyCode.Mouse0: return mouse?.leftButton.isPressed ?? false;
                case KeyCode.Mouse1: return mouse?.rightButton.isPressed ?? false;
                case KeyCode.Mouse2: return mouse?.middleButton.isPressed ?? false;
                case KeyCode.Mouse3: return mouse?.forwardButton?.isPressed ?? false;
                case KeyCode.Mouse4: return mouse?.backButton?.isPressed ?? false;
            }

            var kb = Keyboard.current;
            if (kb == null) return false;
            var key = KeyCodeToKey(kc);
            return key != Key.None && kb[key].isPressed;
        }

        private static bool WasKeyPressedThisFrame(KeyCode kc)
        {
            var mouse = Mouse.current;
            switch (kc)
            {
                case KeyCode.Mouse0: return mouse?.leftButton.wasPressedThisFrame ?? false;
                case KeyCode.Mouse1: return mouse?.rightButton.wasPressedThisFrame ?? false;
                case KeyCode.Mouse2: return mouse?.middleButton.wasPressedThisFrame ?? false;
                case KeyCode.Mouse3: return mouse?.forwardButton?.wasPressedThisFrame ?? false;
                case KeyCode.Mouse4: return mouse?.backButton?.wasPressedThisFrame ?? false;
            }

            var kb = Keyboard.current;
            if (kb == null) return false;
            var key = KeyCodeToKey(kc);
            return key != Key.None && kb[key].wasPressedThisFrame;
        }

        private static Key KeyCodeToKey(KeyCode kc)
        {
            if (kc >= KeyCode.A && kc <= KeyCode.Z)
                return (Key)((int)Key.A + (kc - KeyCode.A));
            if (kc >= KeyCode.Alpha0 && kc <= KeyCode.Alpha9)
                return (Key)((int)Key.Digit0 + (kc - KeyCode.Alpha0));
            if (kc >= KeyCode.Keypad0 && kc <= KeyCode.Keypad9)
                return (Key)((int)Key.Numpad0 + (kc - KeyCode.Keypad0));
            if (kc >= KeyCode.F1 && kc <= KeyCode.F12)
                return (Key)((int)Key.F1 + (kc - KeyCode.F1));
            switch (kc)
            {
                case KeyCode.Space: return Key.Space;
                case KeyCode.Tab: return Key.Tab;
                case KeyCode.Escape: return Key.Escape;
                case KeyCode.Backspace: return Key.Backspace;
                case KeyCode.Return: return Key.Enter;
                case KeyCode.LeftControl: return Key.LeftCtrl;
                case KeyCode.RightControl: return Key.RightCtrl;
                case KeyCode.LeftShift: return Key.LeftShift;
                case KeyCode.RightShift: return Key.RightShift;
                case KeyCode.LeftAlt: return Key.LeftAlt;
                case KeyCode.RightAlt: return Key.RightAlt;
                case KeyCode.UpArrow: return Key.UpArrow;
                case KeyCode.DownArrow: return Key.DownArrow;
                case KeyCode.LeftArrow: return Key.LeftArrow;
                case KeyCode.RightArrow: return Key.RightArrow;
                case KeyCode.LeftBracket: return Key.LeftBracket;
                case KeyCode.RightBracket: return Key.RightBracket;
                case KeyCode.Semicolon: return Key.Semicolon;
                case KeyCode.Quote: return Key.Quote;
                case KeyCode.Comma: return Key.Comma;
                case KeyCode.Period: return Key.Period;
                case KeyCode.Slash: return Key.Slash;
                case KeyCode.Backslash: return Key.Backslash;
                case KeyCode.BackQuote: return Key.Backquote;
                case KeyCode.Minus: return Key.Minus;
                case KeyCode.Equals: return Key.Equals;
                default: return Key.None;
            }
        }

        private void ShowTab(UiTab t)
        {
            _activeTab = t;

            _commandsSectionVE.style.display = (t == UiTab.Commands) ? UITK.DisplayStyle.Flex : UITK.DisplayStyle.None;
            _helpSectionVE.style.display = (t == UiTab.Help) ? UITK.DisplayStyle.Flex : UITK.DisplayStyle.None;
            _serverSectionVE.style.display = (t == UiTab.Server) ? UITK.DisplayStyle.Flex : UITK.DisplayStyle.None;

            if (t == UiTab.Server) RefreshServerSection();

            SetTabVisual(_tabCommands, _activeTab == UiTab.Commands);
            SetTabVisual(_tabHelp, _activeTab == UiTab.Help);
            SetTabVisual(_tabServer, _activeTab == UiTab.Server);
        }

        private static void SetTabVisual(UITK.Button b, bool active)
        {
            b.style.backgroundColor = new UITK.StyleColor(active ? TabActiveBg : TabInactiveBg);
            b.style.color = active ? Color.white : new Color(0.7f, 0.7f, 0.7f);
            b.style.borderBottomWidth = active ? 3 : 0;
        }

        private static void AddTabHover(UITK.Button b, Func<bool> isActive)
        {
            b.focusable = false;
            void Apply() => SetTabVisual(b, isActive());
            Apply();

            b.RegisterCallback<PointerEnterEvent>(_ =>
            {
                if (!isActive())
                {
                    b.style.backgroundColor = new UITK.StyleColor(Color.white);
                    b.style.color = Color.black;
                }
            });
            b.RegisterCallback<PointerLeaveEvent>(_ => Apply());
            b.RegisterCallback<AttachToPanelEvent>(_ => Apply());
            b.RegisterCallback<GeometryChangedEvent>(_ => Apply());
        }

        private void StyleDropdown(UITK.DropdownField dd)
        {
            dd.style.height = BTN_H;
            dd.style.marginRight = GAP;
            dd.style.flexGrow = 0;
            dd.style.flexShrink = 1;
            dd.style.flexBasis = 0;
            dd.style.minWidth = 200;
            dd.style.backgroundColor = new UITK.StyleColor(TextFieldBg);
            dd.style.color = Color.white;
            dd.style.unityFont = GetUIFont();
            var lbl = dd.Q<UITK.Label>();
            if (lbl != null)
            {
                lbl.style.color = Color.white;
                lbl.style.unityFont = GetUIFont();
            }
        }

        private void StyleTextField(UITK.TextField tf)
        {
            MakeReadable(tf);
            tf.style.height = BTN_H;
            tf.style.marginRight = GAP;

            tf.style.flexGrow = 0;
            tf.style.flexShrink = 1;
            tf.style.flexBasis = 0;
            tf.style.minWidth = 160;

            var input = tf.childCount > 0 ? tf.ElementAt(0) : null;
            if (input != null)
            {
                input.style.height = BTN_H;
                input.style.flexGrow = 0;
                input.style.flexShrink = 1;
                input.style.flexBasis = 0;
                input.style.minWidth = 160;
                input.style.whiteSpace = UITK.WhiteSpace.NoWrap;
                input.style.overflow = UITK.Overflow.Hidden;
                input.style.textOverflow = UITK.TextOverflow.Clip;
            }
        }

        private void StyleRowButton(UITK.Button b, float w, string label = null)
        {
            if (!string.IsNullOrEmpty(label)) b.text = label;

            b.style.height = BTN_H;
            b.style.minWidth = w;
            b.style.maxWidth = w;
            b.style.whiteSpace = UITK.WhiteSpace.NoWrap;
            b.style.flexShrink = 0;

            b.style.marginLeft = GAP;
            b.style.paddingLeft = 12;
            b.style.paddingRight = 12;
            b.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);

            b.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            AddButtonFlashWhiteHover(b, (Color)ButtonBg);
        }

        private static void Flashable(UITK.Button b, Color baseBg, int flashMs = 140)
        {
            b.focusable = true;
            void SetBase()
            {
                b.style.backgroundColor = new UITK.StyleColor(baseBg);
                b.style.color = Color.white;
            }

            SetBase();
            bool hover = false;
            bool flashing = false;

            b.RegisterCallback<PointerEnterEvent>(_ =>
            {
                hover = true;
                b.style.backgroundColor = new UITK.StyleColor(Color.white);
                b.style.color = Color.black;
            });
            b.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                hover = false;
                if (!flashing) SetBase();
            });
            b.RegisterCallback<GeometryChangedEvent>(_ => SetBase());

            b.RegisterCallback<PointerUpEvent>(_ =>
            {
                flashing = true;
                b.style.backgroundColor = new UITK.StyleColor(Color.white);
                b.style.color = Color.black;
                b.schedule.Execute(() =>
                {
                    flashing = false;
                    if (!hover) SetBase();
                }).StartingIn(flashMs);
            });
        }

        private static void AddButtonFlash(UITK.Button b, int flashMs = 140)
        {
            var baseBg = b.style.backgroundColor.keyword != UITK.StyleKeyword.Null ? b.style.backgroundColor.value : b.resolvedStyle.backgroundColor;
            Flashable(b, baseBg, flashMs);
        }

        private static void AddButtonFlashWhiteHover(UITK.Button b, Color baseBg, int flashMs = 140)
        {
            Flashable(b, baseBg, flashMs);
        }

        private static void AddChipButtonFlash(UITK.Button btn)
        {
            btn.RegisterCallback<UITK.PointerEnterEvent>(_ =>
            {
                btn.style.backgroundColor = new UITK.StyleColor(new Color32(180, 80, 80, 255));
                btn.style.color = Color.white;
            });
            btn.RegisterCallback<UITK.PointerLeaveEvent>(_ =>
            {
                btn.style.backgroundColor = new UITK.StyleColor(ChipXBg);
                btn.style.color = Color.white;
            });
        }

        private static bool IsChatOpen()
        {
            try
            {
                var chat = MonoBehaviourSingleton<UIManager>.Instance?.Chat;
                if (chat != null && chat.IsFocused) return true;
            }
            catch { }
            return false;
        }

        private static void TrySendChat(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                var chatMgr = NetworkBehaviourSingleton<ChatManager>.Instance;
                if (chatMgr != null)
                    chatMgr.Client_SendChatMessage(text, false, false);
            }
            catch { }
        }

        private void BuildServerSection()
        {
            _serverSectionVE.Clear();

            var head = new UITK.Label("SERVER CONFIG");
            MakeReadable(head);
            head.style.unityFontStyleAndWeight = FontStyle.Normal;
            head.style.fontSize = 30;
            head.style.marginBottom = 10;
            _serverSectionVE.Add(head);

            RefreshServerSection();
        }

        private void RefreshServerSection()
        {
            // Rebuild everything after the title header (first child)
            while (_serverSectionVE.childCount > 1)
                _serverSectionVE.RemoveAt(1);

            var cfg = ConfigManager.Config;
            if (cfg == null)
            {
                var noData = new UITK.Label("No server config loaded.");
                noData.style.fontSize = 20;
                noData.style.marginTop = 20;
                noData.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
                MakeReadable(noData);
                _serverSectionVE.Add(noData);
                return;
            }

            AddServerSectionHeader("COMMANDS");
            _serverSectionVE.Add(MakeServerConfigRow("Spawn Puck", cfg.EnableSpawnPuck));
            _serverSectionVE.Add(MakeServerConfigRow("Backpass", cfg.EnableBackpass));
            _serverSectionVE.Add(MakeServerConfigRow("Pass", cfg.EnablePass));
            _serverSectionVE.Add(MakeServerConfigRow("Yoyo", cfg.EnableYoyo));
            _serverSectionVE.Add(MakeServerConfigRow("Pop", cfg.EnablePop));
            _serverSectionVE.Add(MakeServerConfigRow("Save Practice", cfg.EnableSavePrac));
            _serverSectionVE.Add(MakeServerConfigRow("Tip Practice", cfg.EnableTipPrac));
            _serverSectionVE.Add(MakeServerConfigRow("Cones", cfg.EnableCones));
            _serverSectionVE.Add(MakeServerConfigRow("Minefield", cfg.EnableMinefield));
            _serverSectionVE.Add(MakeServerConfigRow("Traffic", cfg.EnableTraffic));
            _serverSectionVE.Add(MakeServerConfigRow("Dummy", cfg.EnableDummy));
            _serverSectionVE.Add(MakeServerConfigRow("Infinite Stamina", cfg.EnableInfiniteStamina));
            _serverSectionVE.Add(MakeServerConfigRow("Tap Commands", cfg.EnableTapCommands));

            AddServerSectionHeader("MISC");
            _serverSectionVE.Add(MakeServerConfigRow("Goalie AI Persists During Game (Always-On)", cfg.GoalieAIPersistDuringGame));
            _serverSectionVE.Add(MakeServerConfigRow("Enable Goalie AI Voting (/votegoalies)", cfg.EnableGoalieVoting));
            _serverSectionVE.Add(MakeServerConfigRow("Pause Warmup Timer (Practice-Only Server)", cfg.PauseWarmupTimer));
            _serverSectionVE.Add(MakeServerConfigRow("Disable Voting (Blocks /vs and /vw)", cfg.DisableVoting));
        }

        private void AddServerSectionHeader(string text)
        {
            var header = new UITK.Label(text);
            header.style.fontSize = 20;
            header.style.marginTop = 14;
            header.style.marginBottom = 6;
            header.style.color = new Color(0.9f, 0.9f, 0.5f);
            ForceUIFont(header);
            _serverSectionVE.Add(header);
        }

        private UITK.VisualElement MakeServerConfigRow(string featureName, bool enabled)
        {
            var row = new UITK.VisualElement();
            row.style.flexDirection = UITK.FlexDirection.Row;
            row.style.alignItems = UITK.Align.Center;
            row.style.height = 40;
            row.style.marginBottom = 4;
            row.style.backgroundColor = new UITK.StyleColor(RowBg);
            row.style.paddingLeft = 12;
            row.style.paddingRight = 12;

            var label = new UITK.Label(featureName);
            label.style.flexGrow = 1;
            label.style.fontSize = 20;
            MakeReadable(label);
            row.Add(label);

            var status = new UITK.Label(enabled ? "ENABLED" : "DISABLED");
            status.style.fontSize = 20;
            status.style.unityFontStyleAndWeight = FontStyle.Bold;
            status.style.color = enabled ? new Color(0.4f, 0.9f, 0.4f) : new Color(0.9f, 0.4f, 0.4f);
            ForceUIFont(status);
            row.Add(status);

            return row;
        }
    }
}
