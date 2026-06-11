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

        private bool _isVisible = false;
        private Rect _windowRect = new Rect(Screen.width / 2 - 260, Screen.height / 2 - 325, 520, 650);

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

            if (Event.current.type == EventType.Repaint)
                _clickHandledThisFrame = false;

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            InitStyles();

            // Key capture
            if (_listeningAction != null)
            {
                Event e = Event.current;

                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    _listeningAction = null;
                    if (e.type != EventType.Repaint && e.type != EventType.Layout) e.Use();
                }
                else if (e.type == EventType.KeyDown || e.type == EventType.MouseDown)
                {
                    KeyCode key = KeyCode.None;
                    if (e.type == EventType.KeyDown)
                        key = e.keyCode;
                    else if (e.type == EventType.MouseDown)
                    {
                        if      (e.button == 0) key = KeyCode.Mouse0;
                        else if (e.button == 1) key = KeyCode.Mouse1;
                        else if (e.button == 2) key = KeyCode.Mouse2;
                        else if (e.button == 3) key = KeyCode.Mouse3;
                        else if (e.button == 4) key = KeyCode.Mouse4;
                    }

                    if (key != KeyCode.None && key != KeyCode.Mouse0 && key != KeyCode.Mouse1 && key != KeyCode.Escape)
                    {
                        if (key != KeyCode.LeftShift  && key != KeyCode.RightShift  &&
                            key != KeyCode.LeftControl && key != KeyCode.RightControl &&
                            key != KeyCode.LeftAlt     && key != KeyCode.RightAlt)
                        {
                            KeyCode mod = KeyCode.None;
                            if (e.shift   || Input.GetKey(KeyCode.LeftShift)   || Input.GetKey(KeyCode.RightShift))   mod = KeyCode.LeftShift;
                            else if (e.control || Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) mod = KeyCode.LeftControl;
                            else if (e.alt    || Input.GetKey(KeyCode.LeftAlt)     || Input.GetKey(KeyCode.RightAlt))     mod = KeyCode.LeftAlt;

                            AssignKey(_listeningAction, key, mod);
                            _listeningAction = null;
                            e.Use();
                        }
                    }
                }
            }

            GUI.backgroundColor = new Color(0.15f, 0.15f, 0.15f, 1f);
            _windowRect = GUI.Window(8494, _windowRect, _windowDelegate, "TAS KEYBINDS");
            GUI.backgroundColor = _defaultBgColor;
        }

        private void AssignKey(string action, KeyCode main, KeyCode mod)
        {
            if (_tempSettings == null) return;
            switch (action)
            {
                case "Save":         _tempSettings.SavePosition = new KeyBind(main, mod); break;
                case "Teleport":     _tempSettings.Teleport     = new KeyBind(main, mod); break;
                case "Record":       _tempSettings.RecordMacro  = new KeyBind(main, mod); break;
                case "Play":         _tempSettings.PlayMacro    = new KeyBind(main, mod); break;
                case "EditMacro":    _tempSettings.EditMacro    = new KeyBind(main, mod); break;
                case "RewindTick":   _tempSettings.RewindTick   = new KeyBind(main, mod); break;
                case "Menu":         _tempSettings.OpenBindMenu = new KeyBind(main, mod); break;
                case "Pause":        _tempSettings.Pause        = new KeyBind(main, mod); break;
                case "SlowMo":       _tempSettings.SlowMo       = new KeyBind(main, mod); break;
                case "SlowMoBoost":  _tempSettings.SlowMoBoost  = new KeyBind(main, mod); break;
                case "FrameAdvance": _tempSettings.FrameAdvance = new KeyBind(main, mod); break;
                case "ResetTick":    _tempSettings.ResetTick    = new KeyBind(main, mod); break;
                case "FastForward":  _tempSettings.FastForward  = new KeyBind(main, mod); break;
                case "OpenEditor":   _tempSettings.OpenEditor   = new KeyBind(main, mod); break;
            }
        }

        private void WindowFunction(int id)
        {
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
                TASConfig.ResetToDefaults();
                _tempSettings = CloneSettings(TASConfig.Settings);
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

            GUI.DragWindow(new Rect(0, 0, _windowRect.width, 35));
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

            // Only re-lock cursor if game is still running (not at end screen)
            bool gameEnded = false;
            try { gameEnded = EHS.GameManager.IsGameEnded; } catch { }
            if (!gameEnded)
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

        // Overload without extra dummy parameter (leftover from refactor)
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

        // Overload with extra dummy string (compiler doesn't mind, kept for clarity)
        private void DrawBindRow(float x, ref float y, string label, string actionId, string _, KeyBind currentBind)
            => DrawBindRow(x, ref y, label, actionId, currentBind);
    }
}
