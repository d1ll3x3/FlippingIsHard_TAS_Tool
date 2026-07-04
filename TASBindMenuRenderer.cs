using System;
using System.Collections.Generic;
using UnityEngine;
using BepInEx;

namespace FlippingIsHardTAS
{
    public class TASBindMenuRenderer
    {
        private GameObjectFinder _gameObjectFinder;
        
        // Only store camera-related components to restore later
        private List<MonoBehaviour> _disabledCameraScripts = new List<MonoBehaviour>();
        
        // Camera state backup for menu (fixes coordinate bug)
        private Vector3 _menuCameraPos;
        private Quaternion _menuCameraRot;

        private const int WINDOW_ID = 8494;

        private bool _isVisible = false;
        private Rect _windowRect = new Rect(Screen.width / 2 - 260, Screen.height / 2 - 325, 520, 650);

        // Manual window dragging — GUI.DragWindow relies on the broken IMGUI event pipeline
        private bool _dragging = false;
        private Vector2 _dragOffset;
        private bool _wasCloseKeyHeld = false;

        // All rebindable action ids — used to clear duplicate binds on assign
        private static readonly string[] AllActions =
        {
            "Save", "Teleport", "Record", "Play", "EditMacro", "RewindTick", "Menu",
            "Pause", "SlowMo", "SlowMoBoost", "FrameAdvance", "ResetTick", "FastForward", "OpenEditor"
        };

        private static KeyCode[] _allKeyCodes;

        private GUIStyle _titleStyle;
        private GUIStyle _sectionStyle;
        private bool _stylesReady = false;

        private Color _defaultBgColor;

        private TASSettings _tempSettings;

        private string _listeningAction = null;
        private bool _clickHandledThisFrame = false;
        private GUI.WindowFunction _windowDelegate;

        public static bool IsVisibleGlobally = false;
        public bool IsVisible => _isVisible;
        public Action OnMenuClosed;

        public TASBindMenuRenderer(GameObjectFinder gameObjectFinder)
        {
            _gameObjectFinder = gameObjectFinder;
            _windowDelegate = new Action<int>(WindowFunction);
        }

        public void ToggleVisibility()
        {
            if (_isVisible)
            {
                CloseMenu();
            }
            else
            {
                // Backup camera BEFORE disabling scripts
                if (Camera.main != null)
                {
                    _menuCameraPos = Camera.main.transform.position;
                    _menuCameraRot = Camera.main.transform.rotation;
                    
                    // CRITICAL: Disable CinemachineBrain to prevent it from overriding camera
                    var brain = Camera.main.GetComponent<Unity.Cinemachine.CinemachineBrain>();
                    if (brain != null)
                        brain.enabled = false;
                }
                
                _isVisible = true;
                IsVisibleGlobally = true;

                DisableGameScripts();

                // Block game input while menu is open (unless game is ended)
                bool gameEnded = false;
                try { gameEnded = EHS.GameManager.IsGameEnded; } catch { }
                if (!gameEnded)
                {
                    try {
                        if (UnityEngine.InputSystem.Keyboard.current != null)
                            UnityEngine.InputSystem.InputSystem.DisableDevice(UnityEngine.InputSystem.Keyboard.current);
                        if (UnityEngine.InputSystem.Mouse.current != null)
                            UnityEngine.InputSystem.InputSystem.DisableDevice(UnityEngine.InputSystem.Mouse.current);
                    } catch { }
                }

                // Clone all settings into temp copy
                _tempSettings = CloneSettings(TASConfig.Settings);
                _listeningAction = null;
                _dragging = false;
                // The key that opened the menu is still held — require a fresh press to close
                _wasCloseKeyHeld = true;

                // Keep the window on-screen if the resolution changed since construction
                _windowRect.x = Mathf.Clamp(_windowRect.x, 0, Mathf.Max(0, Screen.width - _windowRect.width));
                _windowRect.y = Mathf.Clamp(_windowRect.y, 0, Mathf.Max(0, Screen.height - _windowRect.height));
            }
        }

        private TASSettings CloneSettings(TASSettings src) => new TASSettings
        {
            SavePosition  = src.SavePosition.Clone(),
            Teleport      = src.Teleport.Clone(),
            RecordMacro   = src.RecordMacro.Clone(),
            PlayMacro     = src.PlayMacro.Clone(),
            EditMacro     = src.EditMacro.Clone(),
            RewindTick    = src.RewindTick.Clone(),
            OpenBindMenu  = src.OpenBindMenu.Clone(),
            Pause         = src.Pause.Clone(),
            SlowMo        = src.SlowMo.Clone(),
            SlowMoBoost   = src.SlowMoBoost.Clone(),
            FrameAdvance  = src.FrameAdvance.Clone(),
            ResetTick     = src.ResetTick.Clone(),
            FastForward   = src.FastForward.Clone(),
            OpenEditor    = src.OpenEditor.Clone(),
            OverlayScale  = src.OverlayScale,
        };

        private void InitStyles()
        {
            if (_stylesReady) return;

            _titleStyle = new GUIStyle();
            _titleStyle.normal.textColor = new Color(0.5f, 0.8f, 1f);
            _titleStyle.fontSize = 20;
            _titleStyle.fontStyle = FontStyle.Bold;
            _titleStyle.alignment = TextAnchor.MiddleCenter;

            _sectionStyle = new GUIStyle();
            _sectionStyle.normal.textColor = new Color(0.6f, 0.6f, 0.6f);
            _sectionStyle.fontSize = 13;
            _sectionStyle.fontStyle = FontStyle.Bold;
            _sectionStyle.alignment = TextAnchor.MiddleLeft;

            _defaultBgColor = GUI.backgroundColor;
            _stylesReady = true;
        }

        public void Draw()
        {
            if (!_isVisible) return;
            
            // Force camera position while menu is open (fixes coordinate jump bug)
            if (Camera.main != null)
            {
                Camera.main.transform.SetPositionAndRotation(_menuCameraPos, _menuCameraRot);
            }

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            InitStyles();

            // All input is read from legacy Input (the IMGUI event pipeline is broken in
            // this game), and only once per frame — on the Repaint pass.
            if (Event.current.type == EventType.Repaint)
            {
                _clickHandledThisFrame = false;

                bool wasListening = _listeningAction != null;
                if (wasListening)
                    PollKeyCapture();

                // Close with Esc or the menu key itself (edge-detected; not while capturing)
                bool closeHeld = Input.GetKey(KeyCode.Escape) || TASConfig.Settings.OpenBindMenu.IsPressed();
                if (!wasListening && closeHeld && !_wasCloseKeyHeld)
                {
                    _wasCloseKeyHeld = closeHeld;
                    CloseMenu();
                    return;
                }
                _wasCloseKeyHeld = closeHeld;

                HandleWindowDrag();
            }

            GUI.backgroundColor = new Color(0.15f, 0.15f, 0.15f, 1f);
            _windowRect = GUI.Window(WINDOW_ID, _windowRect, _windowDelegate, "TAS KEYBINDS");
            GUI.backgroundColor = _defaultBgColor;
        }

        /// <summary>
        /// Key capture via legacy Input polling. The old capture read IMGUI KeyDown/MouseDown
        /// events, which don't fire reliably in this game's IL2CPP build — the "Press any
        /// key..." prompt could hang forever. Polling uses the same input path as every
        /// other hotkey in the mod, which is known to work.
        /// </summary>
        private void PollKeyCapture()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                _listeningAction = null;
                return;
            }

            if (_allKeyCodes == null)
                _allKeyCodes = (KeyCode[])Enum.GetValues(typeof(KeyCode));

            foreach (var key in _allKeyCodes)
            {
                if (key == KeyCode.None || key == KeyCode.Escape) continue;
                if (key == KeyCode.Mouse0 || key == KeyCode.Mouse1) continue; // reserved for clicking the UI
                if (IsModifierKey(key)) continue; // modifiers only combine, never bind alone

                bool down;
                try { down = Input.GetKeyDown(key); } catch { continue; }
                if (!down) continue;

                KeyCode mod = KeyCode.None;
                if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) mod = KeyCode.LeftShift;
                else if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) mod = KeyCode.LeftControl;
                else if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) mod = KeyCode.LeftAlt;

                AssignKey(_listeningAction, key, mod);
                _listeningAction = null;
                return;
            }
        }

        private static bool IsModifierKey(KeyCode k)
            => k == KeyCode.LeftShift || k == KeyCode.RightShift
            || k == KeyCode.LeftControl || k == KeyCode.RightControl
            || k == KeyCode.LeftAlt || k == KeyCode.RightAlt;

        private void HandleWindowDrag()
        {
            Vector2 m = Input.mousePosition;
            m.y = Screen.height - m.y;

            if (Input.GetMouseButtonDown(0) && !_dragging)
            {
                // Title bar = top strip of the window, minus the X button corner
                var titleRect = new Rect(_windowRect.x, _windowRect.y, _windowRect.width - 44, 30);
                if (titleRect.Contains(m))
                {
                    _dragging = true;
                    _dragOffset = m - new Vector2(_windowRect.x, _windowRect.y);
                }
            }

            if (_dragging)
            {
                if (!Input.GetMouseButton(0))
                    _dragging = false;
                else
                {
                    _windowRect.x = Mathf.Clamp(m.x - _dragOffset.x, -_windowRect.width + 60, Screen.width - 60);
                    _windowRect.y = Mathf.Clamp(m.y - _dragOffset.y, 0, Screen.height - 40);
                }
            }
        }

        private void AssignKey(string action, KeyCode main, KeyCode mod)
        {
            if (_tempSettings == null) return;

            // Same key+modifier on another action → unbind it there, otherwise both
            // actions would fire on one press.
            foreach (var other in AllActions)
            {
                if (other == action) continue;
                var b = GetBind(other);
                if (b != null && b.MainKey == main && b.Modifier == mod)
                    SetBind(other, new KeyBind(KeyCode.None));
            }

            SetBind(action, new KeyBind(main, mod));
        }

        private KeyBind GetBind(string action)
        {
            switch (action)
            {
                case "Save":         return _tempSettings.SavePosition;
                case "Teleport":     return _tempSettings.Teleport;
                case "Record":       return _tempSettings.RecordMacro;
                case "Play":         return _tempSettings.PlayMacro;
                case "EditMacro":    return _tempSettings.EditMacro;
                case "RewindTick":   return _tempSettings.RewindTick;
                case "Menu":         return _tempSettings.OpenBindMenu;
                case "Pause":        return _tempSettings.Pause;
                case "SlowMo":       return _tempSettings.SlowMo;
                case "SlowMoBoost":  return _tempSettings.SlowMoBoost;
                case "FrameAdvance": return _tempSettings.FrameAdvance;
                case "ResetTick":    return _tempSettings.ResetTick;
                case "FastForward":  return _tempSettings.FastForward;
                case "OpenEditor":   return _tempSettings.OpenEditor;
                default:             return null;
            }
        }

        private void SetBind(string action, KeyBind bind)
        {
            switch (action)
            {
                case "Save":         _tempSettings.SavePosition = bind; break;
                case "Teleport":     _tempSettings.Teleport     = bind; break;
                case "Record":       _tempSettings.RecordMacro  = bind; break;
                case "Play":         _tempSettings.PlayMacro    = bind; break;
                case "EditMacro":    _tempSettings.EditMacro    = bind; break;
                case "RewindTick":   _tempSettings.RewindTick   = bind; break;
                case "Menu":         _tempSettings.OpenBindMenu = bind; break;
                case "Pause":        _tempSettings.Pause        = bind; break;
                case "SlowMo":       _tempSettings.SlowMo       = bind; break;
                case "SlowMoBoost":  _tempSettings.SlowMoBoost  = bind; break;
                case "FrameAdvance": _tempSettings.FrameAdvance = bind; break;
                case "ResetTick":    _tempSettings.ResetTick    = bind; break;
                case "FastForward":  _tempSettings.FastForward  = bind; break;
                case "OpenEditor":   _tempSettings.OpenEditor   = bind; break;
            }
        }

        private void WindowFunction(int id)
        {
            // Both this menu and the editor hit-test the mouse manually, so make sure
            // this window renders on top when both are open.
            GUI.BringWindowToFront(WINDOW_ID);

            // Close button
            GUI.backgroundColor = new Color(0.7f, 0.2f, 0.2f, 1f);
            if (CustomButton(new Rect(_windowRect.width - 38, 5, 30, 22), "X"))
                CloseMenu();
            GUI.backgroundColor = _defaultBgColor;

            float cx = 20;
            float cy = 38;

            // ── Section: Position & State ─────────────────────────────
            DrawSectionLabel(cx, ref cy, "POSITION & SAVESTATE");
            DrawBindRow(cx, ref cy, "Save Position",  "Save",    _tempSettings.SavePosition);
            DrawBindRow(cx, ref cy, "Load Position",  "Teleport",_tempSettings.Teleport);

            // ── Section: Recording ───────────────────────────────────
            DrawSectionLabel(cx, ref cy, "MACRO");
            DrawBindRow(cx, ref cy, "Record Macro",   "Record",  _tempSettings.RecordMacro);
            DrawBindRow(cx, ref cy, "Play Macro",     "Play",    _tempSettings.PlayMacro);
            DrawBindRow(cx, ref cy, "Reset Tick",           "ResetTick",    _tempSettings.ResetTick);
            DrawBindRow(cx, ref cy, "Fast Forward ×3",     "FastForward",  _tempSettings.FastForward);
            DrawBindRow(cx, ref cy, "Edit Macro",     "EditMacro", _tempSettings.EditMacro);

            // ── Section: Playback Controls ────────────────────────────────
            DrawSectionLabel(cx, ref cy, "PLAYBACK CONTROLS");
            DrawBindRow(cx, ref cy, "Pause / Resume",       "Pause",        _tempSettings.Pause);
            DrawBindRow(cx, ref cy, "Slow Motion (×0.1)",   "SlowMo",       _tempSettings.SlowMo);
            DrawBindRow(cx, ref cy, "  └ Boost (hold=×0.3)","SlowMoBoost",  _tempSettings.SlowMoBoost);
            DrawBindRow(cx, ref cy, "Frame Advance",        "FrameAdvance", _tempSettings.FrameAdvance);
            DrawBindRow(cx, ref cy, "Rewind Tick",          "RewindTick",   _tempSettings.RewindTick);

            // ── Section: UI ──────────────────────────────────────────
            DrawSectionLabel(cx, ref cy, "INTERFACE");
            DrawBindRow(cx, ref cy, "Open Settings",  "Menu", _tempSettings.OpenBindMenu);
            DrawBindRow(cx, ref cy, "Open TAS Editor", "OpenEditor", _tempSettings.OpenEditor);
            
            // Overlay scale: use +/- buttons instead of slider for compatibility
            GUI.color = Color.white;
            GUI.Label(new Rect(cx, cy, 100, 20), "HUD Scale:");
            float scl = _tempSettings.OverlayScale;
            GUI.backgroundColor = new Color(0.4f, 0.4f, 0.4f, 1f);
            if (CustomButton(new Rect(cx + 100, cy, 30, 20), "-"))
                scl = Mathf.Max(0.25f, scl - 0.05f);
            GUI.Label(new Rect(cx + 135, cy, 50, 20), $"{scl:F2}x");
            if (CustomButton(new Rect(cx + 175, cy, 30, 20), "+"))
                scl = Mathf.Min(2.0f, scl + 0.05f);
            _tempSettings.OverlayScale = Mathf.Round(scl * 100f) / 100f;
            GUI.backgroundColor = _defaultBgColor;
            cy += 26;
            GUI.color = Color.white;

            // Bottom buttons
            float by = _windowRect.height - 45;
            GUI.backgroundColor = new Color(0.3f, 0.3f, 0.3f, 1f);
            if (CustomButton(new Rect(cx, by, 130, 30), "Reset Defaults"))
            {
                // Reset only the temp copy — SAVE commits it, Cancel discards it.
                // (Resetting TASConfig.Settings here wiped the live binds even on Cancel.)
                _tempSettings = new TASSettings();
            }
            GUI.backgroundColor = new Color(0.3f, 0.3f, 0.3f, 1f);
            if (CustomButton(new Rect(cx + 150, by, 130, 30), "Cancel"))
                CloseMenu();

            GUI.backgroundColor = new Color(0.1f, 0.5f, 0.2f, 1f);
            if (CustomButton(new Rect(cx + 300, by, 140, 30), "SAVE"))
            {
                TASConfig.Settings = _tempSettings;
                TASConfig.Save();
                CloseMenu();
            }
            GUI.backgroundColor = _defaultBgColor;

            // Dragging is handled manually in HandleWindowDrag — GUI.DragWindow relies
            // on the broken IMGUI event pipeline and never worked here.
        }

        private void DrawSectionLabel(float x, ref float y, string title)
        {
            y += 4;
            GUI.color = new Color(0.6f, 0.85f, 1f, 1f);
            GUI.Label(new Rect(x, y, _windowRect.width - 40, 18), title, _sectionStyle);
            GUI.color = Color.white;
            y += 20;
        }

        private bool CustomButton(Rect rect, string text)
        {
            GUI.Box(rect, text);
            if (_clickHandledThisFrame) return false;

            if (Input.GetMouseButtonDown(0))
            {
                Vector2 rawMouse = Input.mousePosition;
                rawMouse.y = Screen.height - rawMouse.y;

                Rect absRect = new Rect(_windowRect.x + rect.x, _windowRect.y + rect.y, rect.width, rect.height);

                if (absRect.Contains(rawMouse))
                {
                    if (Event.current.type == EventType.Repaint)
                    {
                        _clickHandledThisFrame = true;
                        return true;
                    }
                }
            }
            return false;
        }

        private void CloseMenu()
        {
            _isVisible = false;
            IsVisibleGlobally = false;
            
            // Re-enable Cinemachine
            if (Camera.main != null)
            {
                var brain = Camera.main.GetComponent<Unity.Cinemachine.CinemachineBrain>();
                if (brain != null)
                    brain.enabled = true;
                
                var axisCtrl = Camera.main.GetComponent<Unity.Cinemachine.CinemachineInputAxisController>();
                if (axisCtrl != null)
                    axisCtrl.enabled = true;
            }

            EnableGameScripts();

            // Re-enable input devices (unless game is ended)
            bool gameEnded2 = false;
            try { gameEnded2 = EHS.GameManager.IsGameEnded; } catch { }
            if (!gameEnded2)
            {
                try {
                    if (UnityEngine.InputSystem.Keyboard.current != null)
                        UnityEngine.InputSystem.InputSystem.EnableDevice(UnityEngine.InputSystem.Keyboard.current);
                    if (UnityEngine.InputSystem.Mouse.current != null)
                        UnityEngine.InputSystem.InputSystem.EnableDevice(UnityEngine.InputSystem.Mouse.current);
                } catch { }
            }

            // Only re-lock the cursor if we're actually IN GAMEPLAY (a player exists). At the
            // main menu / end screen there's no player and the cursor must stay free, or you
            // can't click anything ("lost cursor in the menu" bug).
            bool inGameplay = false;
            try { inGameplay = !EHS.GameManager.IsGameEnded && _gameObjectFinder.FindPlayer() != null; } catch { }
            if (inGameplay)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            
            OnMenuClosed?.Invoke();
        }
        
        private void DisableGameScripts()
        {
            _disabledCameraScripts.Clear();
            
            // Only disable cinematic camera controllers (minimal intervention)
            if (Camera.main != null)
            {
                var brain = Camera.main.GetComponent<Unity.Cinemachine.CinemachineBrain>();
                if (brain != null && brain.enabled)
                {
                    brain.enabled = false;
                    // already handled in ToggleVisibility
                }
                
                var axisCtrl = Camera.main.GetComponent<Unity.Cinemachine.CinemachineInputAxisController>();
                if (axisCtrl != null && axisCtrl.enabled)
                {
                    axisCtrl.enabled = false;
                    _disabledCameraScripts.Add(axisCtrl);
                }
            }
            
            // Also disable input related objects that could interfere with menu navigation
            var player = _gameObjectFinder.FindPlayerTransform();
            if (player != null)
            {
                var inputHandler = player.GetComponent<EHS.PlayerInputHandler>();
                if (inputHandler != null)
                {
                    var movement = player.GetComponent<EHS.PlayerMovement>();
                    if (movement != null && movement.enabled)
                    {
                        movement.enabled = false;
                        _disabledCameraScripts.Add(movement);
                    }
                }
            }
        }

        private void EnableGameScripts()
        {
            foreach (var comp in _disabledCameraScripts)
            {
                if (comp != null) comp.enabled = true;
            }
            _disabledCameraScripts.Clear();
        }

        private void DrawBindRow(float x, ref float y, string label, string actionId, KeyBind currentBind)
        {
            GUI.color = Color.white;
            GUI.Label(new Rect(x, y, 200, 25), label);

            string btnText = _listeningAction == actionId
                ? "[ Press any key... (Esc = cancel) ]"
                : $"[ {currentBind} ]";

            if (_listeningAction == actionId)
                GUI.color = new Color(0.3f, 1f, 0.5f);

            if (CustomButton(new Rect(x + 200, y, 280, 25), btnText))
                _listeningAction = actionId;

            GUI.color = Color.white;
            y += 32;
        }
    }
}
