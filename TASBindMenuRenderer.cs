using System;
using System.Collections.Generic;
using UnityEngine;
using BepInEx;

namespace FlippingIsHardTAS
{
    public class TASBindMenuRenderer
    {
        private GameObjectFinder _gameObjectFinder;
        private List<MonoBehaviour> _disabledScripts = new List<MonoBehaviour>();

        private bool _isVisible = false;
        // Taller window to fit all 8 binds and the new Macro section
        private Rect _windowRect = new Rect(Screen.width / 2 - 260, Screen.height / 2 - 290, 520, 580);

        private GUIStyle _titleStyle;
        private GUIStyle _sectionStyle;
        private bool _stylesReady = false;

        private Color _defaultBgColor;

        private TASSettings _tempSettings;
        private InputMacroSystem _macroSystem;
        private string _macroFileName = "default";
        private bool _isEditingFileName = false;
        private string _macroStatusMsg = "";
        private float _macroStatusTimer = 0f;

        private string _listeningAction = null;
        private bool _clickHandledThisFrame = false;
        private GUI.WindowFunction _windowDelegate;

        public static bool IsVisibleGlobally = false;
        public bool IsVisible => _isVisible;
        public Action OnMenuClosed;
        public Action OnImportPlayMacro;

        public TASBindMenuRenderer(GameObjectFinder gameObjectFinder)
        {
            _gameObjectFinder = gameObjectFinder;
            _windowDelegate = new Action<int>(WindowFunction);
        }

        public void SetMacroSystem(InputMacroSystem sys)
        {
            _macroSystem = sys;
        }

        public void ToggleVisibility()
        {
            if (_isVisible)
            {
                CloseMenu();
            }
            else
            {
                _isVisible = true;
                IsVisibleGlobally = true;

                DisableGameScripts();

                try {
                    if (UnityEngine.InputSystem.Keyboard.current != null)
                        UnityEngine.InputSystem.InputSystem.DisableDevice(UnityEngine.InputSystem.Keyboard.current);
                    if (UnityEngine.InputSystem.Mouse.current != null)
                        UnityEngine.InputSystem.InputSystem.DisableDevice(UnityEngine.InputSystem.Mouse.current);
                } catch { }

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
            OpenBindMenu  = src.OpenBindMenu.Clone(),
            Pause         = src.Pause.Clone(),
            SlowMo        = src.SlowMo.Clone(),
            SlowMoBoost   = src.SlowMoBoost.Clone(),
            FrameAdvance  = src.FrameAdvance.Clone(),
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

            if (_macroStatusTimer > 0)
            {
                _macroStatusTimer -= Time.deltaTime;
                if (_macroStatusTimer <= 0) _macroStatusMsg = "";
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
                case "Menu":         _tempSettings.OpenBindMenu = new KeyBind(main, mod); break;
                case "Pause":        _tempSettings.Pause        = new KeyBind(main, mod); break;
                case "SlowMo":       _tempSettings.SlowMo       = new KeyBind(main, mod); break;
                case "SlowMoBoost":  _tempSettings.SlowMoBoost  = new KeyBind(main, mod); break;
                case "FrameAdvance": _tempSettings.FrameAdvance = new KeyBind(main, mod); break;
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
            DrawBindRow(cx, ref cy, "Edit Macro",     "EditMacro", _tempSettings.EditMacro);

            // ── Section: Playback Controls ────────────────────────────────
            DrawSectionLabel(cx, ref cy, "PLAYBACK CONTROLS");
            DrawBindRow(cx, ref cy, "Pause / Resume",       "Pause",        _tempSettings.Pause);
            DrawBindRow(cx, ref cy, "Slow Motion (×0.1)",   "SlowMo",       _tempSettings.SlowMo);
            DrawBindRow(cx, ref cy, "  └ Boost (hold=×0.3)","SlowMoBoost",  _tempSettings.SlowMoBoost);
            DrawBindRow(cx, ref cy, "Frame Advance",        "FrameAdvance", _tempSettings.FrameAdvance);

            // ── Section: UI ──────────────────────────────────────────
            DrawSectionLabel(cx, ref cy, "INTERFACE");
            DrawBindRow(cx, ref cy, "Open Settings",  "Menu", _tempSettings.OpenBindMenu);

            // ── Section: Macros ──────────────────────────────────────
            DrawSectionLabel(cx, ref cy, "MACRO FILES");
            
            GUI.color = Color.white;
            GUI.Label(new Rect(cx, cy, 80, 20), "Filename:");
            
            // Custom Text Field Implementation
            Rect txtRect = new Rect(cx + 80, cy, 140, 20);
            GUI.color = _isEditingFileName ? new Color(0.8f, 1f, 0.8f) : Color.white;
            GUI.Box(txtRect, _macroFileName);
            GUI.color = Color.white;
            
            // Handle clicking to focus
            Event currentEvent = Event.current;
            if (currentEvent.type == EventType.MouseDown)
            {
                Rect absTxtRect = new Rect(_windowRect.x + txtRect.x, _windowRect.y + txtRect.y, txtRect.width, txtRect.height);
                Vector2 rawMouse = Input.mousePosition;
                rawMouse.y = Screen.height - rawMouse.y;
                _isEditingFileName = absTxtRect.Contains(rawMouse);
            }
            
            // Handle typing
            if (_isEditingFileName && currentEvent.type == EventType.KeyDown)
            {
                if (currentEvent.keyCode == KeyCode.Backspace)
                {
                    if (_macroFileName.Length > 0)
                        _macroFileName = _macroFileName.Substring(0, _macroFileName.Length - 1);
                    currentEvent.Use();
                }
                else if (currentEvent.keyCode == KeyCode.Return || currentEvent.keyCode == KeyCode.Escape)
                {
                    _isEditingFileName = false;
                    currentEvent.Use();
                }
                else if (currentEvent.character != 0)
                {
                    char c = currentEvent.character;
                    if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
                    {
                        if (_macroFileName.Length < 25)
                            _macroFileName += c;
                    }
                    currentEvent.Use();
                }
            }
            
            string path = System.IO.Path.Combine(Paths.PluginPath, "FlippingIsHardTAS", "Macros", $"{_macroFileName}.tas");
            
            GUI.backgroundColor = new Color(0.2f, 0.4f, 0.6f, 1f);
            if (CustomButton(new Rect(cx + 220, cy, 80, 20), "Export"))
            {
                if (_macroSystem != null)
                {
                    try {
                        _macroSystem.ExportMacro(path);
                        _macroStatusMsg = "Exported OK";
                    } catch (Exception e) {
                        _macroStatusMsg = "Export Error!";
                        TASPlugin.Logger.LogError(e.ToString());
                    }
                    _macroStatusTimer = 3f;
                }
            }
            
            GUI.backgroundColor = new Color(0.6f, 0.4f, 0.2f, 1f);
            if (CustomButton(new Rect(cx + 310, cy, 80, 20), "Import"))
            {
                if (_macroSystem != null)
                {
                    if (_macroSystem.ImportMacro(path))
                    {
                        _macroStatusMsg = "Imported OK";
                        OnImportPlayMacro?.Invoke();
                    }
                    else
                    {
                        _macroStatusMsg = "Not Found!";
                    }
                    _macroStatusTimer = 3f;
                }
            }
            GUI.backgroundColor = _defaultBgColor;
            
            if (!string.IsNullOrEmpty(_macroStatusMsg))
            {
                GUI.color = _macroStatusMsg.Contains("OK") ? Color.green : Color.red;
                GUI.Label(new Rect(cx + 400, cy, 80, 20), _macroStatusMsg);
                GUI.color = Color.white;
            }
            cy += 25;

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

            EnableGameScripts();

            try {
                if (UnityEngine.InputSystem.Keyboard.current != null)
                    UnityEngine.InputSystem.InputSystem.EnableDevice(UnityEngine.InputSystem.Keyboard.current);
                if (UnityEngine.InputSystem.Mouse.current != null)
                    UnityEngine.InputSystem.InputSystem.EnableDevice(UnityEngine.InputSystem.Mouse.current);
            } catch { }

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            OnMenuClosed?.Invoke();
        }

        private void DisableGameScripts()
        {
            _disabledScripts.Clear();

            var allMonos = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            foreach (var comp in allMonos)
            {
                if (comp == null || !comp.enabled) continue;

                string ns = comp.GetType().Namespace ?? "";
                string name = comp.GetType().Name.ToLower();

                if (ns.StartsWith("UnityEngine.InputSystem") ||
                    name.Contains("input") ||
                    name.Contains("pause") ||
                    name.Contains("camera") ||
                    name.Contains("look"))
                {
                    if (ns != "FlippingIsHardTAS")
                    {
                        comp.enabled = false;
                        _disabledScripts.Add(comp);
                    }
                }
            }

            var player = _gameObjectFinder.FindPlayerTransform();
            if (player != null)
            {
                foreach (var comp in player.GetComponents<MonoBehaviour>())
                {
                    if (comp != null && comp.enabled && !(comp.GetType().Namespace ?? "").StartsWith("UnityEngine"))
                    {
                        if ((comp.GetType().Namespace ?? "") != "FlippingIsHardTAS")
                        {
                            comp.enabled = false;
                            if (!_disabledScripts.Contains(comp))
                                _disabledScripts.Add(comp);
                        }
                    }
                }
            }

            var camera = _gameObjectFinder.FindCameraTransform();
            if (camera != null)
            {
                foreach (var comp in camera.GetComponents<MonoBehaviour>())
                {
                    if (comp != null && comp.enabled && !(comp.GetType().Namespace ?? "").StartsWith("UnityEngine"))
                    {
                        if ((comp.GetType().Namespace ?? "") != "FlippingIsHardTAS")
                        {
                            comp.enabled = false;
                            if (!_disabledScripts.Contains(comp))
                                _disabledScripts.Add(comp);
                        }
                    }
                }
            }
        }

        private void EnableGameScripts()
        {
            foreach (var comp in _disabledScripts)
            {
                if (comp != null) comp.enabled = true;
            }
            _disabledScripts.Clear();
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
