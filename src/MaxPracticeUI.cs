// MaxPracticeUI.cs - F3 practice menu.
//
// Styled to match Ponce Arena's panel so a server running both doesn't feel like
// two different mods: same centred modal geometry, same palette, same section /
// row / tab construction, same game-font enforcement.
//
// Opens on F3. The old PonceMods.Shared.ModMenuHub registration is gone - this
// panel is the only way in, so it owns its own cursor and player-input handling.
//
// Content is organised by what you're trying to do rather than by mechanism: each
// command is a fixed row carrying its own description and its own bind slots, so
// the separate "Help" tab that used to restate the same list is no longer needed.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using UITK = UnityEngine.UIElements;

namespace MaxPractice
{
    public class MaxPracticeUI : MonoBehaviour
    {
        // ------------------------------------------------------------------
        // Design system - values lifted from Ponce Arena's panel
        // ------------------------------------------------------------------

        private static readonly Color PanelBg = new Color32(20, 20, 24, 245);
        private static readonly Color PanelBorder = new Color32(48, 48, 56, 255);
        private static readonly Color SectionBg = new Color32(26, 26, 32, 255);
        private static readonly Color SectionBorder = new Color32(38, 38, 46, 255);
        private static readonly Color RowBg = new Color32(34, 34, 40, 255);
        private static readonly Color RowBgHover = new Color32(44, 44, 52, 255);
        private static readonly Color ButtonBg = new Color32(48, 48, 56, 255);
        private static readonly Color TabActiveBg = new Color32(34, 34, 40, 255);
        private static readonly Color TabInactiveBg = new Color32(24, 24, 30, 255);
        private static readonly Color Backdrop = new Color32(0, 0, 0, 140);
        private static readonly Color Accent = new Color32(220, 80, 80, 255);
        private static readonly Color AccentDim = new Color32(146, 53, 53, 255);
        private static readonly Color Danger = new Color32(220, 80, 80, 255);
        private static readonly Color TextPrimary = new Color32(240, 240, 245, 255);
        private static readonly Color TextMuted = new Color32(155, 155, 165, 255);
        private static readonly Color TextDim = new Color32(110, 110, 120, 255);
        private static readonly Color CaptureBg = new Color32(100, 80, 40, 255);

        private const float ROW_RADIUS = 6f;
        private const float BTN_RADIUS = 5f;

        // ------------------------------------------------------------------
        // Command catalogue
        // ------------------------------------------------------------------

        private sealed class CmdDef
        {
            public string Name;
            public string Command;
            public string Detail;
            public Func<bool> ServerEnabled;   // null = the server can't switch it off
        }

        private sealed class CmdGroup
        {
            public string Title;
            public string Subtitle;
            public CmdDef[] Items;
            public string Footnote;   // credit / caveat printed under the group's rows
        }

        private static MaxPractice.ModConfig Cfg => ConfigManager.Config;

        private static readonly CmdGroup[] CATALOGUE =
        {
            new CmdGroup
            {
                Title = "Puck spawning",
                Items = new[]
                {
                    new CmdDef { Name = "Spawn puck", Command = "/s", Detail = "Spawn a puck above your stick.", ServerEnabled = () => Cfg.EnableSpawnPuck },
                    new CmdDef { Name = "Backpass", Command = "/backpass", Detail = "Spawn a puck behind you that passes onto your stick.", ServerEnabled = () => Cfg.EnableBackpass },
                    new CmdDef { Name = "Pass", Command = "/pass", Detail = "Set a pass position with your blade. A puck shooter appears there and feeds you. Run again to fire one.", ServerEnabled = () => Cfg.EnablePass },
                    new CmdDef { Name = "Clear pass", Command = "/unpass", Detail = "Forget the pass position and remove the shooter.", ServerEnabled = () => Cfg.EnablePass },
                    new CmdDef { Name = "Pop", Command = "/pop", Detail = "Pop the last puck you touched straight up.", ServerEnabled = () => Cfg.EnablePop },
                }
            },
            new CmdGroup
            {
                Title = "Stickhandling",
                Subtitle = "Frozen cones you can skate and stickhandle around. They stop pucks, not you.",
                Items = new[]
                {
                    new CmdDef { Name = "Cones", Command = "/cones", Detail = "Five cones in a line in front of you.", ServerEnabled = () => Cfg.EnableCones },
                    new CmdDef { Name = "Minefield", Command = "/minefield", Detail = "Ten cones scattered around you.", ServerEnabled = () => Cfg.EnableMinefield },
                    new CmdDef { Name = "Clear cones", Command = "/clearcones", Detail = "Remove your cones and reset the use counter." },
                    new CmdDef { Name = "Clear minefield", Command = "/clearminefield", Detail = "Remove your scattered cones." },
                }
            },
            new CmdGroup
            {
                Title = "Shooting",
                Subtitle = "A net to shoot at, one per player.",
                Items = new[]
                {
                    new CmdDef { Name = "Mini net", Command = "/mininet", Detail = "Drop a small net ahead of you. It swallows any puck you put in it, so the ice stays clear.", ServerEnabled = () => Cfg.EnableMiniNet },
                    new CmdDef { Name = "Clear mini net", Command = "/clearmininet", Detail = "Take your net away.", ServerEnabled = () => Cfg.EnableMiniNet },
                }
            },
            new CmdGroup
            {
                Title = "Drills",
                Items = new[]
                {
                    new CmdDef { Name = "Save practice", Command = "/saveprac", Detail = "Two minutes of shots at varying angles, speeds and targets. Goalies only.", ServerEnabled = () => Cfg.EnableSavePrac },
                    new CmdDef { Name = "Stop save practice", Command = "/stopsaveprac", Detail = "End the drill early.", ServerEnabled = () => Cfg.EnableSavePrac },
                    new CmdDef { Name = "Tip practice", Command = "/tipprac", Detail = "Pucks fly through your crease for you to redirect.", ServerEnabled = () => Cfg.EnableTipPrac },
                    new CmdDef { Name = "Stop tip practice", Command = "/stoptipprac", Detail = "End the drill early.", ServerEnabled = () => Cfg.EnableTipPrac },
                    new CmdDef { Name = "AI goalie", Command = "/dummy", Detail = "Spawn an AI goalie for your team.", ServerEnabled = () => Cfg.EnableDummy },
                    new CmdDef { Name = "Vote for AI goalies", Command = "/votegoalies", Detail = "Majority vote to fill empty goalie slots for the rest of the session.", ServerEnabled = () => Cfg.EnableGoalieVoting },
                }
            },
            new CmdGroup
            {
                Title = "Practice sheets",
                Subtitle = "Extra copies of the rink, built when someone asks for one and taken down once they're empty.",
                Items = new[]
                {
                    new CmdDef { Name = "Rink list", Command = "/rinks", Detail = "Show every practice rink and who's on it.", ServerEnabled = () => Cfg.EnableRinkSheets },
                    new CmdDef { Name = "New rink", Command = "/rink new", Detail = "Move to an empty practice rink. If there isn't one yet, the server builds it.", ServerEnabled = () => Cfg.EnableRinkSheets },
                    new CmdDef { Name = "Join rink 2", Command = "/rink 2", Detail = "Join a specific rink by number. Rink 1 is the arena's own.", ServerEnabled = () => Cfg.EnableRinkSheets && Cfg.MaxRinkSheets >= 2 },
                    new CmdDef { Name = "Join rink 3", Command = "/rink 3", Detail = "Join the third sheet. The server builds it on demand like the rest.", ServerEnabled = () => Cfg.EnableRinkSheets && Cfg.MaxRinkSheets >= 3 },
                    new CmdDef { Name = "Back to main", Command = "/mainrink", Detail = "Return to the arena rink.", ServerEnabled = () => Cfg.EnableRinkSheets },
                },
                Footnote = "Dalf worked the rink cloning out first in Puck-MultiSheet, and we learned it " +
                           "from his code. Thanks, Dalf: github.com/Dalfan4Puck/Puck-MultiSheet",
            },
            new CmdGroup
            {
                Title = "Traffic",
                Subtitle = "Record a skating line, then replay it as an AI skater to work around.",
                Items = new[]
                {
                    new CmdDef { Name = "Record traffic", Command = "/recordtraffic", Detail = "Start recording your movement.", ServerEnabled = () => Cfg.EnableTraffic },
                    new CmdDef { Name = "Stop recording", Command = "/stoprecord", Detail = "Save what you just skated.", ServerEnabled = () => Cfg.EnableTraffic },
                    new CmdDef { Name = "Spawn traffic", Command = "/traffic", Detail = "Spawn an AI skater. It plays back your recording if you have one.", ServerEnabled = () => Cfg.EnableTraffic },
                    new CmdDef { Name = "Clear traffic", Command = "/cleartraffic", Detail = "Remove all of your traffic.", ServerEnabled = () => Cfg.EnableTraffic },
                }
            },
            new CmdGroup
            {
                Title = "Stick taps",
                Subtitle = "Tap your stick on the ice three times to fire these. Handy when you can't reach the keyboard.",
                Items = new[]
                {
                    new CmdDef { Name = "Tap to pass", Command = "/tappass", Detail = "Pass from your saved pass position.", ServerEnabled = () => Cfg.EnableTapCommands },
                    new CmdDef { Name = "Tap to spawn", Command = "/tapspawn", Detail = "Spawn a puck above your stick.", ServerEnabled = () => Cfg.EnableTapCommands },
                    new CmdDef { Name = "Tap to yoyo", Command = "/tapyoyo", Detail = "Yoyo-return the last puck you shot.", ServerEnabled = () => Cfg.EnableTapCommands },
                    new CmdDef { Name = "Tap to backpass", Command = "/tapbackpass", Detail = "Backpass to yourself.", ServerEnabled = () => Cfg.EnableTapCommands },
                }
            },
            new CmdGroup
            {
                Title = "Utilities",
                Items = new[]
                {
                    new CmdDef { Name = "Yoyo", Command = "/yoyo", Detail = "After shooting, yank your stick back to magnetically return the puck.", ServerEnabled = () => Cfg.EnableYoyo },
                    new CmdDef { Name = "Infinite stamina", Command = "/infinitestamina", Detail = "Toggle infinite stamina for yourself.", ServerEnabled = () => Cfg.EnableInfiniteStamina },
                    new CmdDef { Name = "Clear pucks", Command = "/clearpucks", Detail = "Clear loose pucks but keep one near each player." },
                    new CmdDef { Name = "Clear everything", Command = "/clearall", Detail = "Pucks, cones, traffic, AI goalies. All of it." },
                    new CmdDef { Name = "Print commands", Command = "/practice", Detail = "Dump the full command list into chat." },
                }
            },
        };

        // ------------------------------------------------------------------
        // State
        // ------------------------------------------------------------------

        private enum UiTab { Actions, Commands, Server }

        private UITK.UIDocument _doc;
        private UITK.VisualElement _panel;
        private UITK.VisualElement _backdrop;
        private UITK.ScrollView _actionsBody;
        private UITK.ScrollView _commandsBody;
        private UITK.ScrollView _serverBody;
        private UITK.Button _tabActions;
        private UITK.Button _tabCommands;
        private UITK.Button _tabServer;
        private UITK.VisualElement _captureOverlay;
        private UITK.Label _captureLabel;

        private UiTab _activeTab = UiTab.Actions;

        /// <summary>An Actions-tab button and the label showing its current bind.</summary>
        private sealed class ActionTile
        {
            public string Command;
            public UITK.Button Button;
            public UITK.Label Chord;
        }

        private readonly List<ActionTile> _actionTiles = new List<ActionTile>();
        private bool _isVisible;

        /// <summary>
        /// True while the panel is up and therefore owns the keyboard.
        ///
        /// Static because the thing that needs to read it is a Harmony prefix on
        /// UIManager's chat-open input callbacks (see <see cref="MenuInputBlock"/>),
        /// which has no route to this instance. Kept in lockstep with
        /// <c>_isVisible</c> by <see cref="ShowUI"/> / <see cref="HideUI"/>, and
        /// force-cleared in <see cref="OnDestroy"/> so a mod reload can't strand the
        /// game with chat permanently blocked.
        /// </summary>
        internal static bool MenuOwnsKeyboard { get; private set; }

        private bool _savedCursorState;
        private CursorLockMode _prevLockState = CursorLockMode.None;
        private bool _prevCursorVisible;
        private bool _savedMouseRequired;
        private bool _prevIsMouseRequired;

        private bool _isCapturing;
        private UITK.Button _captureButton;
        private Action<string> _captureCommit;
        private bool _captureStarted;
        private float _captureWindowEnd;
        private float _captureLastBestAt;
        private int _captureBestWeight;
        private MaxPracticeKeybindManager.KeyChord _captureBestChord;

        // Bind chip strips, keyed by command, so a rebind only redraws its own row.
        private readonly Dictionary<string, UITK.VisualElement> _bindStrips =
            new Dictionary<string, UITK.VisualElement>(StringComparer.OrdinalIgnoreCase);

        private static Font _uiFont;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void Start()
        {
            try { MaxPracticeKeybindManager.Initialize(); }
            catch (Exception e) { Debug.LogError("[MaxPractice] Keybind init failed: " + e); }
        }

        private void OnDestroy()
        {
            // Unconditionally, and first: this runs on mod disable and on session end, and
            // a stuck `true` here would leave the player unable to open chat at all with no
            // panel on screen to explain why.
            MenuOwnsKeyboard = false;

            // Same deal as HideUI: once the game has recomputed, replaying the lock state we
            // captured at ShowUI would undo it. That matters most here - being destroyed
            // usually means the mod was disabled or the session ended, i.e. exactly when the
            // requirement has changed since we opened.
            if (RestorePlayerInput()) _savedCursorState = false;
            else RestoreCursor();

            if (_backdrop != null) _backdrop.RemoveFromHierarchy();
            if (_panel != null) _panel.RemoveFromHierarchy();
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

            SuppressPlayerInput();

            // Rebuild on open: the config can be reloaded between visits, and the
            // enabled/disabled state is baked into the rows and tiles.
            BuildActionsBody();
            BuildCommandsBody();
            BuildServerBody();
            RefreshBinds();

            _backdrop.style.display = UITK.DisplayStyle.Flex;
            _panel.style.display = UITK.DisplayStyle.Flex;
            _backdrop.BringToFront();
            _panel.BringToFront();
            _isVisible = true;
            MenuOwnsKeyboard = true;

            // Chat may already have been open when F3 was pressed. Blocking the open
            // action from here on isn't enough on its own - the text field would keep
            // focus and eat every keystroke aimed at the panel.
            CloseGameChat();
        }

        private void HideUI()
        {
            if (_panel == null) return;
            if (_isCapturing) CancelCapture();

            _backdrop.style.display = UITK.DisplayStyle.None;
            _panel.style.display = UITK.DisplayStyle.None;
            _isVisible = false;
            MenuOwnsKeyboard = false;

            // When the game recomputed the requirement it also drives the cursor through
            // ApplicationManager.Event_OnUIStateChanged, so replaying our captured lock
            // state on top would re-hide the cursor over whatever it just opened.
            bool gameOwnsCursor = RestorePlayerInput();
            if (gameOwnsCursor) _savedCursorState = false;
            else RestoreCursor();
        }

        /// <summary>
        /// Tell the game a menu wants the mouse, so skating input stops while the
        /// panel is up. Without it you steer the player through the menu.
        /// </summary>
        private void SuppressPlayerInput()
        {
            try
            {
                _prevIsMouseRequired = GlobalStateManager.UIState.IsMouseRequired;
                _savedMouseRequired = true;
                if (!_prevIsMouseRequired)
                    GlobalStateManager.SetUIState(new Dictionary<string, object> { { "isMouseRequired", true } });
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MaxPractice] SuppressPlayerInput failed: " + e.Message);
            }
        }

        /// <summary>
        /// Hand the mouse back — by asking the game to recompute it, not by replaying the
        /// value we captured on open.
        ///
        /// Esc reaches both of us. The Input System dispatches PauseAction (and so
        /// UIManager.OnPauseActionPerformed -> PauseMenu.Show() -> CheckMouseRequirement,
        /// which sets isMouseRequired = true) in the input update that runs BEFORE
        /// MonoBehaviour.Update - that is the same reason escapeKey.wasPressedThisFrame is
        /// true for us at all. Writing our remembered false back over that left the pause
        /// menu up with a hidden, locked cursor and input routed to the skater, so you had
        /// to blind-press Esc a second time.
        ///
        /// Recomputing is also order-independent: if the game shows the pause menu after
        /// us instead, its own visibility change recomputes again and still wins.
        /// </summary>
        /// <returns>true when the game recomputed and now owns the mouse/cursor state.</returns>
        private bool RestorePlayerInput()
        {
            if (!_savedMouseRequired) return false;
            _savedMouseRequired = false;
            try
            {
                if (TryGameRecomputeMouseRequirement()) return true;
                GlobalStateManager.SetUIState(new Dictionary<string, object> { { "isMouseRequired", _prevIsMouseRequired } });
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MaxPractice] RestorePlayerInput failed: " + e.Message);
            }
            return false;
        }

        private static System.Reflection.MethodInfo _checkMouseRequirement;
        private static bool _checkMouseRequirementResolved;

        /// <summary>
        /// Invoke the game's own mouse-requirement recompute. Private on UIManager, hence
        /// the reflection; returns false if the game ever drops it, so the caller can fall
        /// back to the remembered value.
        /// </summary>
        private static bool TryGameRecomputeMouseRequirement()
        {
            if (!_checkMouseRequirementResolved)
            {
                _checkMouseRequirementResolved = true;
                try
                {
                    _checkMouseRequirement = typeof(UIManager).GetMethod(
                        "CheckMouseRequirement",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic,
                        null, Type.EmptyTypes, null);
                }
                catch { _checkMouseRequirement = null; }

                if (_checkMouseRequirement == null)
                    Debug.LogWarning("[MaxPractice] UIManager.CheckMouseRequirement not found; " +
                                     "falling back to restoring the captured mouse state.");
            }

            if (_checkMouseRequirement == null) return false;

            var mgr = MonoBehaviourSingleton<UIManager>.Instance;
            if (mgr == null) return false;

            _checkMouseRequirement.Invoke(mgr, null);
            return true;
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
            {
                ProcessKeybindInputs();

                var hk = Keyboard.current;
                if (hk != null && !IsChatOpen() && hk.f3Key.wasPressedThisFrame)
                {
                    ShowUI();
                    return;
                }
            }

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
                            _captureLabel.text = "Release to confirm";
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
                                _captureLabel.text = MaxPracticeKeybindManager.ChordToString(chord) + "  ·  release to confirm";
                        }
                    }

                    // The capture arms on ANY input, but only keys that pass IsAllowedKey can
                    // form a chord - so starting it with an unbindable key left _captureBestWeight
                    // at -1, which the exit test below requires to be >= 0. The capture then sat
                    // there re-snapshotting every frame, showing "Release to confirm" while
                    // nothing could ever confirm it, until the player found Escape. Re-arm when
                    // the window closes with nothing usable, and say why.
                    if (_captureBestWeight < 0 && Time.unscaledTime >= _captureWindowEnd)
                    {
                        _captureStarted = false;
                        if (_captureLabel != null)
                            _captureLabel.text = "That key can't be bound. Try another, or Esc to cancel.";
                        return;
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
            else if (kb != null)
            {
                if (kb.escapeKey.wasPressedThisFrame || kb.f3Key.wasPressedThisFrame)
                {
                    HideUI();
                    return;
                }
            }

            if (UnityEngine.Cursor.lockState != CursorLockMode.None)
                UnityEngine.Cursor.lockState = CursorLockMode.None;
            if (!UnityEngine.Cursor.visible)
                UnityEngine.Cursor.visible = true;
        }

        // ------------------------------------------------------------------
        // Panel construction
        // ------------------------------------------------------------------

        private void CreateUI()
        {
            try
            {
                var uiMgr = FindFirstObjectByType<UIManager>(FindObjectsInactive.Include);
                _doc = uiMgr != null ? uiMgr.UIDocument : FindFirstObjectByType<UITK.UIDocument>(FindObjectsInactive.Include);
                var root = _doc != null ? _doc.rootVisualElement : null;
                if (root == null) return;

                _backdrop = new UITK.VisualElement { name = "MaxPractice_Backdrop" };
                _backdrop.style.position = UITK.Position.Absolute;
                _backdrop.style.left = 0;
                _backdrop.style.top = 0;
                _backdrop.style.right = 0;
                _backdrop.style.bottom = 0;
                _backdrop.style.backgroundColor = new UITK.StyleColor(Backdrop);
                _backdrop.style.display = UITK.DisplayStyle.None;
                _backdrop.pickingMode = UITK.PickingMode.Position;
                _backdrop.RegisterCallback<UITK.PointerUpEvent>(_ => HideUI());
                root.Add(_backdrop);

                _panel = new UITK.VisualElement { name = "MaxPractice_Panel" };
                var ps = _panel.style;
                ps.position = UITK.Position.Absolute;
                ps.left = new UITK.Length(50, UITK.LengthUnit.Percent);
                ps.top = new UITK.Length(50, UITK.LengthUnit.Percent);
                ps.translate = new UITK.Translate(
                    new UITK.Length(-50, UITK.LengthUnit.Percent),
                    new UITK.Length(-50, UITK.LengthUnit.Percent), 0f);
                ps.width = Mathf.Clamp(Mathf.RoundToInt(Screen.width * 0.58f), 720, 1040);
                ps.height = new UITK.Length(84, UITK.LengthUnit.Percent);
                ps.minHeight = new UITK.Length(60, UITK.LengthUnit.Percent);
                ps.maxHeight = new UITK.Length(78, UITK.LengthUnit.Percent);
                ps.overflow = UITK.Overflow.Hidden;
                ps.flexDirection = UITK.FlexDirection.Column;
                ps.backgroundColor = new UITK.StyleColor(PanelBg);
                ps.paddingLeft = 18;
                ps.paddingRight = 18;
                ps.paddingTop = 16;
                ps.paddingBottom = 14;
                SetRadius(_panel, 12f);
                SetBorderWidth(_panel, 1f);
                SetBorderColor(_panel, PanelBorder);
                ps.display = UITK.DisplayStyle.None;
                // Clicks inside the panel must not reach the backdrop's close handler.
                _panel.RegisterCallback<UITK.PointerUpEvent>(e => e.StopPropagation());
                root.Add(_panel);

                var titleRow = new UITK.VisualElement();
                titleRow.style.flexDirection = UITK.FlexDirection.Row;
                titleRow.style.alignItems = UITK.Align.Center;
                titleRow.style.marginBottom = 4;
                titleRow.Add(Title("MAX", TextPrimary));
                titleRow.Add(Title(" PRACTICE", Accent));
                _panel.Add(titleRow);

                var subtitle = new UITK.Label("Practice tools  ·  F3 to toggle  ·  Esc to close");
                subtitle.style.fontSize = 12;
                subtitle.style.color = new UITK.StyleColor(TextMuted);
                subtitle.style.marginBottom = 12;
                ForceUIFont(subtitle);
                _panel.Add(subtitle);

                var tabBar = new UITK.VisualElement();
                tabBar.style.flexDirection = UITK.FlexDirection.Row;
                _panel.Add(tabBar);

                _tabActions = MakeTab("Actions", () => ShowTab(UiTab.Actions), () => _activeTab == UiTab.Actions);
                _tabCommands = MakeTab("Binds", () => ShowTab(UiTab.Commands), () => _activeTab == UiTab.Commands);
                _tabServer = MakeTab("Server", () => ShowTab(UiTab.Server), () => _activeTab == UiTab.Server);
                _tabServer.style.marginRight = 0;
                tabBar.Add(_tabActions);
                tabBar.Add(_tabCommands);
                tabBar.Add(_tabServer);

                _actionsBody = MakeBody();
                _commandsBody = MakeBody();
                _serverBody = MakeBody();
                _panel.Add(_actionsBody);
                _panel.Add(_commandsBody);
                _panel.Add(_serverBody);

                // The three bodies are deliberately left empty here. CreateUI has exactly one
                // caller - ShowUI, immediately before it fills them itself so the rows pick up
                // any config reloaded since the last visit - so building them here only ever
                // produced a full set of rows, tiles and bind chips that were thrown away
                // microseconds later on the very first F3 press.

                var footer = new UITK.VisualElement();
                footer.style.flexDirection = UITK.FlexDirection.Row;
                footer.style.justifyContent = UITK.Justify.FlexEnd;
                footer.style.marginTop = 12;
                _panel.Add(footer);

                footer.Add(MakeFooterButton("Clear all binds", () =>
                {
                    if (_isCapturing) CancelCapture();
                    MaxPracticeKeybindManager.Config.commandBinds.Clear();
                    MaxPracticeKeybindManager.SaveConfig();
                    RefreshBinds();
                }));
                footer.Add(MakeFooterButton("Close", HideUI));

                BuildCaptureOverlay();
                ShowTab(_activeTab);
            }
            catch (Exception e)
            {
                Debug.LogError("[MaxPractice] Failed to build the menu: " + e);
            }
        }

        private void BuildCaptureOverlay()
        {
            _captureOverlay = new UITK.VisualElement();
            _captureOverlay.style.position = UITK.Position.Absolute;
            _captureOverlay.style.left = 0;
            _captureOverlay.style.top = 0;
            _captureOverlay.style.right = 0;
            _captureOverlay.style.bottom = 0;
            _captureOverlay.style.backgroundColor = new UITK.StyleColor(new Color(0f, 0f, 0f, 0.82f));
            _captureOverlay.style.alignItems = UITK.Align.Center;
            _captureOverlay.style.justifyContent = UITK.Justify.Center;
            _captureOverlay.style.display = UITK.DisplayStyle.None;
            _captureOverlay.pickingMode = UITK.PickingMode.Position;

            var heading = new UITK.Label("PRESS A KEY");
            heading.style.fontSize = 28;
            heading.style.color = new UITK.StyleColor(Accent);
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            heading.style.letterSpacing = 3;
            heading.style.marginBottom = 10;
            ForceUIFont(heading);
            _captureOverlay.Add(heading);

            _captureLabel = new UITK.Label("Press a key, or a modifier plus a key.");
            _captureLabel.style.fontSize = 14;
            _captureLabel.style.color = new UITK.StyleColor(TextMuted);
            ForceUIFont(_captureLabel);
            _captureOverlay.Add(_captureLabel);

            var hint = new UITK.Label("Esc to cancel");
            hint.style.fontSize = 12;
            hint.style.color = new UITK.StyleColor(TextDim);
            hint.style.marginTop = 14;
            ForceUIFont(hint);
            _captureOverlay.Add(hint);

            _panel.Add(_captureOverlay);
        }

        /// <summary>
        /// The quick-launch grid: every command as a button you can just click.
        /// Anything that can be bound to a key can be fired from here instead, so
        /// the mod is fully usable without binding anything or typing in chat.
        /// The panel stays open on a click so you can lay out a whole drill -
        /// cones, then traffic, then a goalie - in one visit.
        /// </summary>
        private void BuildActionsBody()
        {
            _actionsBody.Clear();
            _actionTiles.Clear();

            _actionsBody.Add(Note(
                "Click to run. Each button shows its bind, or the command if there isn' + chr(39) + 't one; set binds " +
                "on the Binds tab. The menu stays open, so you can lay a whole drill out in one go.", TextMuted));

            foreach (var group in CATALOGUE)
            {
                var section = AddSection(_actionsBody, group.Title, group.Subtitle);

                var grid = new UITK.VisualElement();
                grid.style.flexDirection = UITK.FlexDirection.Row;
                grid.style.flexWrap = UITK.Wrap.Wrap;
                grid.style.marginBottom = 8;
                section.Add(grid);

                foreach (var def in group.Items)
                    grid.Add(MakeActionTile(def));

                if (!string.IsNullOrEmpty(group.Footnote))
                    section.Add(SectionFootnote(group.Footnote));
            }
        }

        private UITK.VisualElement MakeActionTile(CmdDef def)
        {
            bool enabled = true;
            try { enabled = def.ServerEnabled == null || def.ServerEnabled(); }
            catch { }

            var button = new UITK.Button();
            button.style.height = 52;
            button.style.minWidth = 158;
            button.style.flexGrow = 1;
            button.style.flexBasis = 158;
            button.style.marginLeft = 0;
            button.style.marginTop = 0;
            button.style.marginRight = 8;
            button.style.marginBottom = 8;
            button.style.paddingLeft = 12;
            button.style.paddingRight = 12;
            button.style.paddingTop = 6;
            button.style.paddingBottom = 6;
            button.style.backgroundColor = new UITK.StyleColor(RowBg);
            button.style.alignItems = UITK.Align.FlexStart;
            button.style.justifyContent = UITK.Justify.Center;
            SetRadius(button, ROW_RADIUS);
            SetBorderWidth(button, 1f);
            SetBorderColor(button, SectionBorder);
            button.focusable = false;

            var name = new UITK.Label(def.Name);
            name.style.fontSize = 14;
            name.style.color = new UITK.StyleColor(enabled ? TextPrimary : TextDim);
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.whiteSpace = UITK.WhiteSpace.Normal;
            ForceUIFont(name);
            button.Add(name);

            var chord = new UITK.Label(string.Empty);
            chord.style.fontSize = 11;
            chord.style.color = new UITK.StyleColor(enabled ? Accent : TextDim);
            chord.style.marginTop = 2;
            ForceUIFont(chord);
            button.Add(chord);

            if (!enabled)
            {
                // The server has this switched off - the command would no-op, so
                // don't pretend the button does anything.
                chord.text = "disabled on this server";
                chord.style.color = new UITK.StyleColor(TextDim);
                button.style.backgroundColor = new UITK.StyleColor(SectionBg);
                button.SetEnabled(false);
            }
            else
            {
                button.clicked += () => TrySendChat(def.Command);
                AddButtonFlash(button, RowBg);
            }

            _actionTiles.Add(new ActionTile { Command = def.Command, Button = button, Chord = chord });
            return button;
        }

        /// <summary>Show each action's current bind on its tile.</summary>
        private void RefreshActionChords()
        {
            foreach (var tile in _actionTiles)
            {
                if (tile == null || tile.Chord == null) continue;
                if (!tile.Button.enabledSelf) continue;   // keeps the "disabled" note

                var specs = new List<string>();
                foreach (var raw in MaxPracticeKeybindManager.Config.commandBinds)
                {
                    if (string.IsNullOrEmpty(raw)) continue;
                    int colon = raw.LastIndexOf(':');
                    if (colon < 1) continue;
                    if (!string.Equals(raw.Substring(0, colon).Trim(), tile.Command, StringComparison.OrdinalIgnoreCase)) continue;
                    specs.Add(raw.Substring(colon + 1).Trim());
                }

                if (specs.Count > 0)
                {
                    tile.Chord.text = string.Join("  /  ", specs.ToArray());
                    tile.Chord.style.color = new UITK.StyleColor(Accent);
                }
                else
                {
                    tile.Chord.text = tile.Command;
                    tile.Chord.style.color = new UITK.StyleColor(TextDim);
                }
            }
        }

        private void BuildCommandsBody()
        {
            _commandsBody.Clear();
            _bindStrips.Clear();

            _commandsBody.Add(Note(
                "Run a command from here, or give it a key and fire it without opening the menu. " +
                "Most of these only work during warmup.", TextMuted));

            foreach (var group in CATALOGUE)
            {
                var section = AddSection(_commandsBody, group.Title, group.Subtitle);
                foreach (var def in group.Items)
                    section.Add(MakeCommandRow(def));

                if (!string.IsNullOrEmpty(group.Footnote))
                    section.Add(SectionFootnote(group.Footnote));
            }
        }

        private UITK.VisualElement MakeCommandRow(CmdDef def)
        {
            var row = NewRow();

            var text = new UITK.VisualElement();
            text.style.flexGrow = 1;
            text.style.flexShrink = 1;
            text.style.marginRight = 10;
            row.Add(text);

            var nameRow = new UITK.VisualElement();
            nameRow.style.flexDirection = UITK.FlexDirection.Row;
            nameRow.style.alignItems = UITK.Align.Center;
            text.Add(nameRow);

            var name = new UITK.Label(def.Name);
            name.style.fontSize = 15;
            name.style.color = new UITK.StyleColor(TextPrimary);
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            ForceUIFont(name);
            nameRow.Add(name);

            var slash = new UITK.Label(def.Command);
            slash.style.fontSize = 12;
            slash.style.color = new UITK.StyleColor(AccentDim);
            slash.style.marginLeft = 10;
            ForceUIFont(slash);
            nameRow.Add(slash);

            // A server can switch most of these off; saying so beats a command that
            // silently does nothing.
            bool enabled = true;
            try { enabled = def.ServerEnabled == null || def.ServerEnabled(); }
            catch { }

            if (!enabled)
            {
                var off = new UITK.Label("OFF");
                off.style.fontSize = 10;
                off.style.color = new UITK.StyleColor(TextDim);
                off.style.unityFontStyleAndWeight = FontStyle.Bold;
                off.style.letterSpacing = 1;
                off.style.marginLeft = 10;
                off.style.paddingLeft = 6;
                off.style.paddingRight = 6;
                off.style.paddingTop = 2;
                off.style.paddingBottom = 2;
                off.style.backgroundColor = new UITK.StyleColor(new Color(1f, 1f, 1f, 0.05f));
                SetRadius(off, 3f);
                ForceUIFont(off);
                nameRow.Add(off);
            }

            if (!string.IsNullOrEmpty(def.Detail))
            {
                var detail = new UITK.Label(def.Detail);
                detail.style.fontSize = 12;
                detail.style.color = new UITK.StyleColor(enabled ? TextMuted : TextDim);
                detail.style.whiteSpace = UITK.WhiteSpace.Normal;
                detail.style.marginTop = 3;
                ForceUIFont(detail);
                text.Add(detail);
            }

            var strip = new UITK.VisualElement();
            strip.style.flexDirection = UITK.FlexDirection.Row;
            strip.style.alignItems = UITK.Align.Center;
            strip.style.flexShrink = 0;
            _bindStrips[def.Command] = strip;
            row.Add(strip);

            UITK.Button bind = null;
            // The panel repaints this one to CaptureBg while it is listening for a key,
            // so the hover handlers have to keep their hands off it until capture ends.
            bind = MakeRowButton("Bind", 74f, ButtonBg, () => _captureButton == bind);
            bind.clicked += () => StartCapture(bind, spec =>
            {
                if (string.IsNullOrEmpty(spec)) return;
                string displaced = MaxPracticeKeybindManager.BindCommand(def.Command, spec);
                RefreshBinds();
                if (!string.IsNullOrEmpty(displaced))
                    Debug.Log($"[MaxPractice] {spec} was bound to {displaced}; moved it to {def.Command}.");
            });
            row.Add(bind);

            var run = MakeRowButton("Run", 74f, ButtonBg);
            run.clicked += () => TrySendChat(def.Command);
            run.style.marginLeft = 6;
            if (!enabled) run.SetEnabled(false);
            row.Add(run);

            AddHover(row);
            RefreshStrip(def.Command);
            return row;
        }

        /// <summary>Redraw every row's bind chips and every action tile's bind label.</summary>
        private void RefreshBinds()
        {
            foreach (var kvp in _bindStrips)
                RefreshStrip(kvp.Key);

            RefreshActionChords();
        }

        private void RefreshStrip(string command)
        {
            if (!_bindStrips.TryGetValue(command, out var strip) || strip == null) return;
            strip.Clear();

            var binds = MaxPracticeKeybindManager.Config.commandBinds;
            for (int i = 0; i < binds.Count; i++)
            {
                string raw = binds[i];
                if (string.IsNullOrEmpty(raw)) continue;

                int colon = raw.LastIndexOf(':');
                if (colon < 1) continue;
                if (!string.Equals(raw.Substring(0, colon).Trim(), command, StringComparison.OrdinalIgnoreCase)) continue;

                string spec = raw.Substring(colon + 1).Trim();
                string entry = raw;
                strip.Add(MakeChip(spec, () =>
                {
                    MaxPracticeKeybindManager.Config.commandBinds.Remove(entry);
                    MaxPracticeKeybindManager.SaveConfig();
                    RefreshBinds();
                }));
            }
        }

        private UITK.VisualElement MakeChip(string text, Action onRemove)
        {
            var chip = new UITK.VisualElement();
            chip.style.flexDirection = UITK.FlexDirection.Row;
            chip.style.alignItems = UITK.Align.Center;
            chip.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            chip.style.paddingLeft = 8;
            chip.style.paddingRight = 3;
            chip.style.paddingTop = 3;
            chip.style.paddingBottom = 3;
            chip.style.marginRight = 6;
            SetRadius(chip, 4f);
            SetBorderWidth(chip, 1f);
            SetBorderColor(chip, PanelBorder);

            var label = new UITK.Label(text);
            label.style.fontSize = 12;
            label.style.color = new UITK.StyleColor(Accent);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            ForceUIFont(label);
            chip.Add(label);

            var x = new UITK.Button(onRemove) { text = "×" };
            x.style.width = 18;
            x.style.height = 18;
            x.style.marginLeft = 6;
            x.style.marginRight = 0;
            x.style.marginTop = 0;
            x.style.marginBottom = 0;
            x.style.paddingLeft = 0;
            x.style.paddingRight = 0;
            x.style.paddingTop = 0;
            x.style.paddingBottom = 0;
            x.style.fontSize = 13;
            x.style.color = new UITK.StyleColor(TextMuted);
            x.style.backgroundColor = new UITK.StyleColor(Color.clear);
            x.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            SetBorderWidth(x, 0f);
            x.focusable = false;
            ForceUIFont(x);
            x.RegisterCallback<UITK.PointerEnterEvent>(_ => x.style.color = new UITK.StyleColor(Danger));
            x.RegisterCallback<UITK.PointerLeaveEvent>(_ => x.style.color = new UITK.StyleColor(TextMuted));
            chip.Add(x);

            return chip;
        }

        // ------------------------------------------------------------------
        // Server tab
        // ------------------------------------------------------------------

        private void BuildServerBody()
        {
            if (_serverBody == null) return;
            _serverBody.Clear();

            var cfg = ConfigManager.Config;
            if (cfg == null)
            {
                _serverBody.Add(Note("No config loaded.", TextMuted));
                return;
            }

            _serverBody.Add(Note(
                "What this MaxPractice install has switched on, read from config/maxpractice.json. " +
                "On a listen server that is the live server config. If you joined someone else's " +
                "server, this is only your own copy and the host's may differ.", TextMuted));

            var commands = AddSection(_serverBody, "Commands");
            commands.Add(MakeStateRow("Spawn puck", cfg.EnableSpawnPuck));
            commands.Add(MakeStateRow("Backpass", cfg.EnableBackpass));
            commands.Add(MakeStateRow("Pass", cfg.EnablePass));
            commands.Add(MakeStateRow("Yoyo", cfg.EnableYoyo));
            commands.Add(MakeStateRow("Pop", cfg.EnablePop));
            commands.Add(MakeStateRow("Save practice", cfg.EnableSavePrac));
            commands.Add(MakeStateRow("Tip practice", cfg.EnableTipPrac));
            commands.Add(MakeStateRow("Cones", cfg.EnableCones));
            commands.Add(MakeStateRow("Minefield", cfg.EnableMinefield));
            commands.Add(MakeStateRow("Traffic", cfg.EnableTraffic));
            commands.Add(MakeStateRow("AI goalie", cfg.EnableDummy));
            commands.Add(MakeStateRow("Infinite stamina", cfg.EnableInfiniteStamina));
            commands.Add(MakeStateRow("Stick taps", cfg.EnableTapCommands));
            commands.Add(MakeStateRow("Practice sheets", cfg.EnableRinkSheets));

            var limits = AddSection(_serverBody, "Limits");
            limits.Add(MakeValueRow("Cone sets per player", cfg.ConesPerPlayer.ToString()));
            limits.Add(MakeValueRow("Minefields per player", cfg.MinefieldPerPlayer.ToString()));
            limits.Add(MakeValueRow("Traffic per player", cfg.TrafficPerPlayer.ToString()));
            limits.Add(MakeValueRow("Save practice length", cfg.SavePracDurationSeconds + "s"));
            limits.Add(MakeValueRow("Puck cleanup threshold", cfg.MaxPucksBeforeCleanup.ToString()));
            limits.Add(MakeValueRow("Clear-command cooldown",
                cfg.ClearCommandCooldownSeconds <= 0f ? "off" : cfg.ClearCommandCooldownSeconds + "s"));

            var behaviour = AddSection(_serverBody, "Behaviour");
            behaviour.Add(MakeStateRow("Cone visuals", cfg.ConeVisuals));
            behaviour.Add(MakeStateRow("AI goalies persist through every phase", cfg.GoalieAIPersistDuringGame));
            behaviour.Add(MakeStateRow("Vote for AI goalies", cfg.EnableGoalieVoting));
            behaviour.Add(MakeStateRow("Warmup timer paused", cfg.PauseWarmupTimer));
            behaviour.Add(MakeStateRow("Game-start voting blocked", cfg.DisableVoting));
            behaviour.Add(MakeStateRow("AI hidden from the scoreboard", cfg.HideAIFromScoreboard));

            if (cfg.EnableRinkSheets)
            {
                var sheets = AddSection(_serverBody, "Practice sheets");
                sheets.Add(MakeValueRow("Rinks (including the arena's)", cfg.MaxRinkSheets.ToString()));
                sheets.Add(MakeValueRow("Layout", cfg.RinkSheetLayout));
                sheets.Add(MakeValueRow("Spacing",
                    MaxPractice.SheetLayout.ParseMode(cfg.RinkSheetLayout) == MaxPractice.SheetLayoutMode.Vertical
                        ? cfg.RinkSheetVerticalSpacing + "m down"
                        : cfg.RinkSheetGridSpacingX + "m x " + cfg.RinkSheetGridSpacingZ + "m"));
                sheets.Add(MakeValueRow("Empty rink torn down after",
                    cfg.RinkSheetIdleTimeoutSeconds <= 0f ? "never" : cfg.RinkSheetIdleTimeoutSeconds + "s"));
            }
        }

        private UITK.VisualElement MakeStateRow(string label, bool on)
        {
            var row = NewRow();

            var name = new UITK.Label(label);
            name.style.flexGrow = 1;
            name.style.fontSize = 14;
            name.style.color = new UITK.StyleColor(on ? TextPrimary : TextMuted);
            name.style.whiteSpace = UITK.WhiteSpace.Normal;
            ForceUIFont(name);
            row.Add(name);

            var pill = new UITK.Label(on ? "ON" : "OFF");
            pill.style.fontSize = 11;
            pill.style.unityFontStyleAndWeight = FontStyle.Bold;
            pill.style.letterSpacing = 1;
            pill.style.color = new UITK.StyleColor(on ? Accent : TextDim);
            pill.style.minWidth = 42;
            pill.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            pill.style.paddingTop = 4;
            pill.style.paddingBottom = 4;
            pill.style.backgroundColor = new UITK.StyleColor(
                on ? new Color(0.863f, 0.314f, 0.314f, 0.15f) : new Color(1f, 1f, 1f, 0.05f));
            SetRadius(pill, 4f);
            ForceUIFont(pill);
            row.Add(pill);

            AddHover(row);
            return row;
        }

        private UITK.VisualElement MakeValueRow(string label, string value)
        {
            var row = NewRow();

            var name = new UITK.Label(label);
            name.style.flexGrow = 1;
            name.style.fontSize = 14;
            name.style.color = new UITK.StyleColor(TextPrimary);
            name.style.whiteSpace = UITK.WhiteSpace.Normal;
            ForceUIFont(name);
            row.Add(name);

            var val = new UITK.Label(value);
            val.style.fontSize = 13;
            val.style.unityFontStyleAndWeight = FontStyle.Bold;
            val.style.color = new UITK.StyleColor(Accent);
            val.style.minWidth = 42;
            val.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleRight);
            ForceUIFont(val);
            row.Add(val);

            AddHover(row);
            return row;
        }

        // ------------------------------------------------------------------
        // Widgets
        // ------------------------------------------------------------------

        private static UITK.Label Title(string text, Color c)
        {
            var l = new UITK.Label(text);
            l.style.fontSize = 42;
            l.style.color = new UITK.StyleColor(c);
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.letterSpacing = 2;
            ForceUIFont(l);
            return l;
        }

        private static UITK.ScrollView MakeBody()
        {
            var sv = new UITK.ScrollView(UITK.ScrollViewMode.Vertical);
            sv.style.flexGrow = 1;
            sv.style.display = UITK.DisplayStyle.None;
            return sv;
        }

        private UITK.Button MakeTab(string text, Action onClick, Func<bool> isActive)
        {
            var b = new UITK.Button(onClick) { text = text.ToUpperInvariant() };
            b.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            b.style.height = 44;
            b.style.flexGrow = 1;
            b.style.paddingLeft = 14;
            b.style.paddingRight = 14;
            b.style.marginRight = 6;
            b.style.marginBottom = 14;
            b.style.fontSize = 17;
            b.style.unityFontStyleAndWeight = FontStyle.Bold;
            b.style.letterSpacing = 2;
            b.style.borderTopLeftRadius = 8;
            b.style.borderTopRightRadius = 8;
            b.style.borderBottomLeftRadius = 0;
            b.style.borderBottomRightRadius = 0;
            SetBorderWidth(b, 0f);
            b.style.borderBottomColor = new UITK.StyleColor(Accent);
            b.style.backgroundColor = new UITK.StyleColor(TabInactiveBg);
            b.style.color = new UITK.StyleColor(TextMuted);
            b.focusable = false;
            ForceUIFont(b);
            AddTabHover(b, isActive);
            return b;
        }

        private static void SetTabVisual(UITK.Button b, bool active)
        {
            if (b == null) return;
            b.style.backgroundColor = new UITK.StyleColor(active ? TabActiveBg : TabInactiveBg);
            b.style.color = new UITK.StyleColor(active ? TextPrimary : TextMuted);
            b.style.borderBottomWidth = active ? 3 : 0;
            b.style.borderBottomColor = new UITK.StyleColor(Accent);
        }

        private void ShowTab(UiTab t)
        {
            _activeTab = t;
            if (_actionsBody != null)
                _actionsBody.style.display = t == UiTab.Actions ? UITK.DisplayStyle.Flex : UITK.DisplayStyle.None;
            if (_commandsBody != null)
                _commandsBody.style.display = t == UiTab.Commands ? UITK.DisplayStyle.Flex : UITK.DisplayStyle.None;
            if (_serverBody != null)
                _serverBody.style.display = t == UiTab.Server ? UITK.DisplayStyle.Flex : UITK.DisplayStyle.None;

            SetTabVisual(_tabActions, t == UiTab.Actions);
            SetTabVisual(_tabCommands, t == UiTab.Commands);
            SetTabVisual(_tabServer, t == UiTab.Server);
        }

        private static UITK.VisualElement AddSection(UITK.VisualElement parent, string label, string subtitle = null)
        {
            var box = new UITK.VisualElement();
            var s = box.style;
            s.flexDirection = UITK.FlexDirection.Column;
            s.marginBottom = 14;
            s.paddingLeft = 12;
            s.paddingRight = 12;
            s.paddingTop = 12;
            s.paddingBottom = 4;
            s.backgroundColor = new UITK.StyleColor(SectionBg);
            SetRadius(box, 8f);
            SetBorderWidth(box, 1f);
            SetBorderColor(box, SectionBorder);
            box.Add(MakeSectionHeader(label, subtitle));
            parent.Add(box);
            return box;
        }

        private static UITK.VisualElement MakeSectionHeader(string label, string subtitle = null)
        {
            var wrap = new UITK.VisualElement();
            wrap.style.flexDirection = UITK.FlexDirection.Column;
            wrap.style.marginBottom = 12;

            var line = new UITK.VisualElement();
            line.style.flexDirection = UITK.FlexDirection.Row;
            line.style.alignItems = UITK.Align.Center;
            line.style.marginBottom = 4;

            var bar = new UITK.VisualElement();
            bar.style.width = 4;
            bar.style.height = 18;
            bar.style.backgroundColor = new UITK.StyleColor(Accent);
            bar.style.marginRight = 10;
            SetRadius(bar, 2f);
            line.Add(bar);

            var title = new UITK.Label(label.ToUpperInvariant());
            title.style.color = new UITK.StyleColor(TextPrimary);
            title.style.fontSize = 16;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.letterSpacing = 3;
            ForceUIFont(title);
            line.Add(title);
            wrap.Add(line);

            if (!string.IsNullOrEmpty(subtitle))
            {
                var sub = new UITK.Label(subtitle);
                sub.style.color = new UITK.StyleColor(TextMuted);
                sub.style.fontSize = 12;
                sub.style.marginLeft = 14;
                sub.style.whiteSpace = UITK.WhiteSpace.Normal;
                ForceUIFont(sub);
                wrap.Add(sub);
            }

            return wrap;
        }

        private static UITK.VisualElement NewRow()
        {
            var row = new UITK.VisualElement();
            row.style.flexDirection = UITK.FlexDirection.Row;
            row.style.alignItems = UITK.Align.Center;
            row.style.marginBottom = 8;
            row.style.backgroundColor = new UITK.StyleColor(RowBg);
            row.style.paddingLeft = 14;
            row.style.paddingRight = 10;
            row.style.paddingTop = 10;
            row.style.paddingBottom = 10;
            SetRadius(row, ROW_RADIUS);
            return row;
        }

        private static void AddHover(UITK.VisualElement row)
        {
            row.RegisterCallback<UITK.PointerEnterEvent>(_ => row.style.backgroundColor = new UITK.StyleColor(RowBgHover));
            row.RegisterCallback<UITK.PointerLeaveEvent>(_ => row.style.backgroundColor = new UITK.StyleColor(RowBg));
        }

        /// <summary>
        /// Hover and click feedback for a button, ported from CompAdjust's panel so the two mods
        /// feel the same under the cursor. Both states brighten toward the accent rather than
        /// washing out to white, and the base fill is restored on leave and on every geometry
        /// change so a relayout cannot strand a button in its hover colour.
        ///
        /// <paramref name="holdsOwnFill"/> is for a button the panel repaints to show a state,
        /// such as Bind while it listens for a key: while it returns true every handler stands
        /// down instead of painting over that state.
        /// </summary>
        private static void AddButtonFlash(UITK.Button b, Color baseBg, Func<bool> holdsOwnFill = null, int flashMs = 170)
        {
            if (b == null) return;

            Color hoverBg = Color.Lerp(baseBg, Accent, 0.35f);
            Color flashBg = Color.Lerp(baseBg, Accent, 0.6f);

            Func<bool> held = () =>
            {
                try { return holdsOwnFill != null && holdsOwnFill(); }
                catch { return false; }
            };

            Action setBase = () =>
            {
                if (held()) return;
                b.style.backgroundColor = new UITK.StyleColor(baseBg);
            };

            bool hovering = false, flashing = false;

            b.RegisterCallback<UITK.PointerEnterEvent>(_ =>
            {
                hovering = true;
                if (held()) return;
                b.style.backgroundColor = new UITK.StyleColor(hoverBg);
            });
            b.RegisterCallback<UITK.PointerLeaveEvent>(_ =>
            {
                hovering = false;
                if (!flashing) setBase();
            });
            b.RegisterCallback<UITK.GeometryChangedEvent>(_ =>
            {
                if (!hovering && !flashing) setBase();
            });
            b.RegisterCallback<UITK.PointerUpEvent>(_ =>
            {
                if (held()) return;
                flashing = true;
                b.style.backgroundColor = new UITK.StyleColor(flashBg);
                b.schedule.Execute(() =>
                {
                    flashing = false;
                    if (hovering && !held())
                        b.style.backgroundColor = new UITK.StyleColor(hoverBg);
                    else
                        setBase();
                }).ExecuteLater(flashMs);
            });
        }

        /// <summary>
        /// Hover for a tab. A tab already carries two painted states, so this lifts an inactive
        /// tab toward the row-hover fill and brightens its text, then hands the paint back to
        /// SetTabVisual on leave, attach and relayout. The active tab is left alone.
        /// </summary>
        private static void AddTabHover(UITK.Button b, Func<bool> isActive)
        {
            if (b == null || isActive == null) return;

            Action apply = () =>
            {
                bool active;
                try { active = isActive(); }
                catch { return; }
                SetTabVisual(b, active);
            };
            apply();

            b.RegisterCallback<UITK.PointerEnterEvent>(_ =>
            {
                bool active;
                try { active = isActive(); }
                catch { return; }
                if (active) return;
                b.style.backgroundColor = new UITK.StyleColor(RowBgHover);
                b.style.color = new UITK.StyleColor(TextPrimary);
            });
            b.RegisterCallback<UITK.PointerLeaveEvent>(_ => apply());
            b.RegisterCallback<UITK.AttachToPanelEvent>(_ => apply());
            b.RegisterCallback<UITK.GeometryChangedEvent>(_ => apply());
        }

        /// <summary>Small dim line under a section's rows - credits, caveats.</summary>
        private static UITK.Label SectionFootnote(string text)
        {
            var l = new UITK.Label(text);
            l.style.fontSize = 11;
            l.style.color = new UITK.StyleColor(TextDim);
            l.style.unityFontStyleAndWeight = FontStyle.Italic;
            l.style.whiteSpace = UITK.WhiteSpace.Normal;
            l.style.marginLeft = 14;
            l.style.marginTop = 2;
            l.style.marginBottom = 8;
            ForceUIFont(l);
            return l;
        }

        private static UITK.Label Note(string text, Color color)
        {
            var l = new UITK.Label(text);
            l.style.fontSize = 12;
            l.style.color = new UITK.StyleColor(color);
            l.style.whiteSpace = UITK.WhiteSpace.Normal;
            l.style.marginBottom = 12;
            ForceUIFont(l);
            return l;
        }

        private UITK.Button MakeFooterButton(string text, Action onClick)
        {
            var b = new UITK.Button(onClick) { text = text.ToUpperInvariant() };
            b.style.height = 38;
            b.style.minWidth = 124;
            b.style.marginLeft = 8;
            b.style.fontSize = 13;
            b.style.unityFontStyleAndWeight = FontStyle.Bold;
            b.style.letterSpacing = 1;
            b.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            b.style.color = new UITK.StyleColor(TextPrimary);
            b.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            SetRadius(b, BTN_RADIUS);
            SetBorderWidth(b, 1f);
            SetBorderColor(b, PanelBorder);
            b.focusable = false;
            ForceUIFont(b);
            AddButtonFlash(b, ButtonBg);
            return b;
        }

        private UITK.Button MakeRowButton(string text, float width, Color bg, Func<bool> holdsOwnFill = null)
        {
            var b = new UITK.Button { text = text.ToUpperInvariant() };
            b.style.height = 30;
            b.style.width = width;
            b.style.flexShrink = 0;
            b.style.marginLeft = 0;
            b.style.marginRight = 0;
            b.style.marginTop = 0;
            b.style.marginBottom = 0;
            b.style.paddingLeft = 0;
            b.style.paddingRight = 0;
            b.style.fontSize = 12;
            b.style.unityFontStyleAndWeight = FontStyle.Bold;
            b.style.letterSpacing = 1;
            b.style.backgroundColor = new UITK.StyleColor(bg);
            b.style.color = new UITK.StyleColor(TextPrimary);
            b.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            SetRadius(b, BTN_RADIUS);
            SetBorderWidth(b, 1f);
            SetBorderColor(b, PanelBorder);
            b.focusable = false;
            ForceUIFont(b);
            AddButtonFlash(b, bg, holdsOwnFill);
            return b;
        }

        private static void SetRadius(UITK.VisualElement e, float r)
        {
            e.style.borderTopLeftRadius = r;
            e.style.borderTopRightRadius = r;
            e.style.borderBottomLeftRadius = r;
            e.style.borderBottomRightRadius = r;
        }

        private static void SetBorderColor(UITK.VisualElement e, Color c)
        {
            e.style.borderTopColor = new UITK.StyleColor(c);
            e.style.borderBottomColor = new UITK.StyleColor(c);
            e.style.borderLeftColor = new UITK.StyleColor(c);
            e.style.borderRightColor = new UITK.StyleColor(c);
        }

        private static void SetBorderWidth(UITK.VisualElement e, float w)
        {
            e.style.borderTopWidth = w;
            e.style.borderBottomWidth = w;
            e.style.borderLeftWidth = w;
            e.style.borderRightWidth = w;
        }

        /// <summary>
        /// Pull the game's own UI font so the panel doesn't fall back to Arial and
        /// stand out against every other menu.
        /// </summary>
        private static Font GetUIFont()
        {
            if (_uiFont != null) return _uiFont;
            try
            {
                var uiManager = MonoBehaviourSingleton<UIManager>.Instance;
                var textSettings = (uiManager != null && uiManager.PanelSettings != null)
                    ? uiManager.PanelSettings.textSettings : null;
                if (textSettings != null && textSettings.defaultFontAsset != null)
                    _uiFont = textSettings.defaultFontAsset.sourceFontFile;
            }
            catch { }

            if (_uiFont == null)
            {
                try { _uiFont = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
            }
            return _uiFont;
        }

        private static void ForceUIFont(UITK.VisualElement ve)
        {
            var f = GetUIFont();
            if (f != null) ve.style.unityFont = new UITK.StyleFont(f);
        }

        // ------------------------------------------------------------------
        // Keybind capture
        // ------------------------------------------------------------------

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
                _captureButton.text = "...";
                _captureButton.style.backgroundColor = new UITK.StyleColor(CaptureBg);
            }

            if (_captureLabel != null)
                _captureLabel.text = "Press a key, or a modifier plus a key.";
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
            ResetCaptureButton();
        }

        private void CancelCapture()
        {
            if (!_isCapturing) return;
            _isCapturing = false;
            if (_captureOverlay != null) _captureOverlay.style.display = UITK.DisplayStyle.None;
            ResetCaptureButton();
        }

        private void ResetCaptureButton()
        {
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

        /// <summary>
        /// Every KeyCode that can appear in a chord, in ascending order, resolved once.
        ///
        /// SnapshotCurrentChord used to walk `Enum.GetValues(typeof(KeyCode))` directly, and it
        /// is called on every frame of a keybind capture. That allocates a fresh ~430-entry
        /// array each time, and `foreach` over the non-generic Array enumerator BOXES every
        /// element - so holding a key down for a second cost tens of thousands of short-lived
        /// objects. The allowed set is a pure function of two static predicates and never
        /// changes, so it is built on first use and kept.
        /// </summary>
        private static KeyCode[] _capturableKeys;

        private static KeyCode[] CapturableKeys()
        {
            if (_capturableKeys != null) return _capturableKeys;

            var found = new List<KeyCode>();
            foreach (KeyCode k in Enum.GetValues(typeof(KeyCode)))
            {
                if (!MaxPracticeKeybindManager.IsAllowedKey(k)) continue;
                if (MaxPracticeKeybindManager.IsModifierKey(k)) continue;
                found.Add(k);
            }
            found.Sort();
            _capturableKeys = found.ToArray();
            return _capturableKeys;
        }

        // Refilled per call rather than reallocated. Only the ToArray below escapes.
        private readonly List<KeyCode> _chordScratch = new List<KeyCode>();

        private MaxPracticeKeybindManager.KeyChord SnapshotCurrentChord()
        {
            var kb = Keyboard.current;
            bool ctrl = (kb?.leftCtrlKey?.isPressed ?? false) || (kb?.rightCtrlKey?.isPressed ?? false);
            bool shift = (kb?.leftShiftKey?.isPressed ?? false) || (kb?.rightShiftKey?.isPressed ?? false);
            bool alt = (kb?.leftAltKey?.isPressed ?? false) || (kb?.rightAltKey?.isPressed ?? false);

            var keys = _chordScratch;
            keys.Clear();

            // CapturableKeys is already sorted ascending and this appends in order, so the
            // explicit re-sort the old code did here is redundant.
            KeyCode[] candidates = CapturableKeys();
            for (int i = 0; i < candidates.Length; i++)
            {
                if (IsKeyPressed(candidates[i])) keys.Add(candidates[i]);
            }

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
            if (kb == null) return;
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

        // ------------------------------------------------------------------
        // Chat
        // ------------------------------------------------------------------

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

        /// <summary>
        /// Drop the game's chat input if it is focused, and give the keyboard back to the
        /// panel. StopInput is UIChat's own close path - the same one its Esc and submit
        /// handlers use - so it tears down the text field and the message-expiry tweens the
        /// way the game expects, rather than us hiding a still-focused field.
        /// </summary>
        private static void CloseGameChat()
        {
            try
            {
                var chat = MonoBehaviourSingleton<UIManager>.Instance?.Chat;
                if (chat != null && chat.IsFocused) chat.StopInput();
            }
            catch { }
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
    }
}
