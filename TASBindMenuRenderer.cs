using System;
using System.Collections.Generic;
using UnityEngine;

namespace FlippingIsHardTAS
{
    /// <summary>
    /// Settings window: rebinding (driven by TASConfig.Actions) and HUD options. Modal —
    /// while it's open the game's input, the player movement and the editor are blocked.
    /// Changes are made on a copy and only applied by Save.
    /// </summary>
    public class TASBindMenuRenderer
    {
        private readonly GameObjectFinder _finder;
        private readonly GuiWindow _win = new GuiWindow("TAS SETTINGS", new Rect(0, 0, 720, 520));

        // Scripts we disabled while open, re-enabled on close
        private readonly List<MonoBehaviour> _disabledScripts = new List<MonoBehaviour>();
        private Vector3 _menuCameraPos;
        private Quaternion _menuCameraRot;

        private TASSettings _temp;
        private BindAction _listening;
        private bool _wasCloseKeyHeld;
        private string _status = "";
        private Color _statusColor = TASGui.TextDim;

        private static KeyCode[] _allKeyCodes;
        private static readonly KeyCode[] GameKeys = { KeyCode.W, KeyCode.A, KeyCode.S, KeyCode.D, KeyCode.Space };

        public static bool IsVisibleGlobally { get; private set; }
        public Action OnMenuClosed;

        private const float ROW_H = 30f, COL_W = 336f;

        public TASBindMenuRenderer(GameObjectFinder finder)
        {
            _finder = finder;
        }

        public void ToggleVisibility()
        {
            if (IsVisibleGlobally) CloseMenu();
            else OpenMenu();
        }

        public void ForceClose()
        {
            if (IsVisibleGlobally) CloseMenu();
        }

        private void OpenMenu()
        {
            var cam = Camera.main;
            if (cam != null)
            {
                _menuCameraPos = cam.transform.position;
                _menuCameraRot = cam.transform.rotation;
            }

            IsVisibleGlobally = true;
            TASGui.ModalLayer = 1;
            TASGui.ActiveField = null;
            DisableGameScripts();
            if (!GameEnded()) SetGameDevices(false);

            _temp = TASConfig.Settings.Clone();
            _listening = null;
            _status = "Click a key to rebind it. Esc cancels a capture.";
            _statusColor = TASGui.TextDim;
            // The key that opened the menu is still held — require a fresh press to close.
            _wasCloseKeyHeld = true;
            _win.CenterOnScreen();
        }

        private void CloseMenu()
        {
            IsVisibleGlobally = false;
            TASGui.ModalLayer = 0;
            _listening = null;

            EnableGameScripts();
            if (!GameEnded()) SetGameDevices(true);

            // Re-lock the cursor only in gameplay (a player exists). At the main menu / end
            // screen it must stay free or nothing can be clicked.
            bool inGameplay = !GameEnded() && _finder.FindPlayer() != null;
            if (inGameplay)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }

            OnMenuClosed?.Invoke();
        }

        public void Draw()
        {
            if (!IsVisibleGlobally) return;

            // Pin the camera while the menu is open (Cinemachine is off; nothing else may move it).
            var cam = Camera.main;
            if (cam != null) cam.transform.SetPositionAndRotation(_menuCameraPos, _menuCameraRot);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            if (TASGui.IsRepaint)
            {
                bool wasListening = _listening != null;
                if (wasListening) PollKeyCapture();

                // Esc / the menu key closes (= Cancel), but not while capturing a key.
                bool closeHeld = Input.GetKey(KeyCode.Escape) || TASConfig.Settings.OpenBindMenu.IsPressed();
                bool close = !wasListening && closeHeld && !_wasCloseKeyHeld;
                _wasCloseKeyHeld = closeHeld;
                if (close) { CloseMenu(); return; }
            }

            // Dim the game behind the modal.
            TASGui.Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0f, 0f, 0f, 0.22f));

            if (_win.DrawChrome($"v{MyPluginInfo.PLUGIN_VERSION}")) { CloseMenu(); return; }

            float top = GuiWindow.TitleH + 12f;
            float yl = top, yr = top;
            float xl = 16f, xr = 16f + COL_W + 20f;

            // Left column: MACRO + TIME; right column: SAVESTATE + INTERFACE + options.
            string section = null;
            foreach (var a in TASConfig.Actions)
            {
                bool left = a.Section == "MACRO" || a.Section == "TIME";
                ref float y = ref (left ? ref yl : ref yr);
                float x = left ? xl : xr;
                if (a.Section != section)
                {
                    section = a.Section;
                    if (y > top) y += 6f;
                    TASGui.SectionTitle(_win.R(x, y, COL_W, 18), a.Section);
                    y += 24f;
                }
                DrawBindRow(a, x, y);
                y += ROW_H;
            }

            yr += 6f;
            TASGui.SectionTitle(_win.R(xr, yr, COL_W, 18), "OPTIONS");
            yr += 24f;
            DrawOptions(xr, ref yr);

            // Footer
            float fy = _win.Rect.height - 76f;
            TASGui.Label(_win.R(16, fy, _win.Rect.width - 32, 22), _status, _statusColor, 12);
            fy += 30f;
            if (TASGui.Button(_win.R(16, fy, 130, 30), "Reset defaults"))
            {
                // Only the copy — Save commits it, Cancel discards it.
                _temp = new TASSettings { ShowKeyHelp = _temp.ShowKeyHelp };
                SetStatus("Defaults restored — Save to apply.", TASGui.Pause);
            }
            if (TASGui.Button(_win.R(_win.Rect.width - 276, fy, 120, 30), "Cancel"))
            {
                CloseMenu();
                return;
            }
            if (TASGui.Button(_win.R(_win.Rect.width - 148, fy, 132, 30), "Save", true, true, TASGui.Ok))
            {
                TASConfig.Settings = _temp;
                TASConfig.Save();
                TASGui.Toast("Settings saved", TASGui.Ok);
                CloseMenu();
                return;
            }

            _win.End();
        }

        private void DrawBindRow(BindAction a, float x, float y)
        {
            var bind = a.Get(_temp);
            bool listening = _listening == a;

            TASGui.Label(_win.R(x, y, 150, 26), a.Label, TASGui.Text, 12);

            string text = listening ? "Press a key…" : bind.ToString();
            if (TASGui.Button(_win.R(x + 150, y, 150, 26), text, true, listening, TASGui.Ok))
            {
                _listening = listening ? null : a;
                if (_listening != null) SetStatus($"Press the new key for “{a.Label}” (Shift/Ctrl/Alt combos allowed, Esc cancels).", TASGui.Ok);
            }
            if (TASGui.Button(_win.R(x + 304, y, 28, 26), "×", bind.IsBound, accent: TASGui.Danger))
            {
                a.Set(_temp, new KeyBind());
                SetStatus($"“{a.Label}” unbound.", TASGui.TextDim);
            }
        }

        private void DrawOptions(float x, ref float y)
        {
            TASGui.Label(_win.R(x, y, 150, 26), "HUD scale", TASGui.Text, 12);
            float scl = _temp.OverlayScale;
            if (TASGui.Button(_win.R(x + 150, y, 34, 26), "−")) scl -= 0.1f;
            TASGui.Label(_win.R(x + 186, y, 78, 26), $"{scl:0.0}×", TASGui.Text, 13, true, TextAnchor.MiddleCenter);
            if (TASGui.Button(_win.R(x + 266, y, 34, 26), "+")) scl += 0.1f;
            _temp.OverlayScale = Mathf.Round(Mathf.Clamp(scl, 0.5f, 2f) * 10f) / 10f;
            y += ROW_H;

            TASGui.Label(_win.R(x, y, 150, 26), "Pause at macro end", TASGui.Text, 12);
            _temp.PauseAtPlaybackEnd = TASGui.Toggle(_win.R(x + 150, y, 150, 26),
                _temp.PauseAtPlaybackEnd ? "On" : "Off", _temp.PauseAtPlaybackEnd, TASGui.Ok);
            y += ROW_H;

            TASGui.Label(_win.R(x, y, 150, 26), "Key list on HUD", TASGui.Text, 12);
            _temp.ShowKeyHelp = TASGui.Toggle(_win.R(x + 150, y, 150, 26),
                _temp.ShowKeyHelp ? "Shown" : "Hidden", _temp.ShowKeyHelp, TASGui.Ok);
            y += ROW_H;
        }

        /// <summary>
        /// Key capture by polling legacy Input — the IMGUI KeyDown/MouseDown events don't fire
        /// reliably in this game's IL2CPP build, so the prompt could hang forever with them.
        /// </summary>
        private void PollKeyCapture()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                _listening = null;
                SetStatus("Capture cancelled.", TASGui.TextDim);
                return;
            }

            if (_allKeyCodes == null)
                _allKeyCodes = (KeyCode[])Enum.GetValues(typeof(KeyCode));

            foreach (var key in _allKeyCodes)
            {
                if (key == KeyCode.None || key == KeyCode.Escape) continue;
                if (key == KeyCode.Mouse0 || key == KeyCode.Mouse1) continue;   // used to click the UI
                if (KeyBind.IsModifierKey(key)) continue;                          // modifiers only combine

                bool down;
                try { down = Input.GetKeyDown(key); }
                catch (Exception) { continue; }   // joystick codes the legacy backend rejects
                if (!down) continue;

                KeyCode mod = KeyCode.None;
                if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) mod = KeyCode.LeftShift;
                else if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) mod = KeyCode.LeftControl;
                else if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) mod = KeyCode.LeftAlt;

                Assign(_listening, new KeyBind(key, mod));
                _listening = null;
                return;
            }
        }

        private void Assign(BindAction action, KeyBind bind)
        {
            // The same combo on another action would fire both — take it away from there.
            string taken = null;
            foreach (var other in TASConfig.Actions)
            {
                if (other == action || !other.Get(_temp).SameAs(bind)) continue;
                other.Set(_temp, new KeyBind());
                taken = other.Label;
            }
            action.Set(_temp, bind);

            if (taken != null)
                SetStatus($"{bind} → “{action.Label}” (removed from “{taken}”).", TASGui.Pause);
            else if (bind.Modifier == KeyCode.None && Array.IndexOf(GameKeys, bind.MainKey) >= 0)
                SetStatus($"{bind} → “{action.Label}”. Careful: {bind} is also a game key.", TASGui.Pause);
            else
                SetStatus($"{bind} → “{action.Label}”.", TASGui.Ok);
        }

        private void SetStatus(string msg, Color c)
        {
            _status = msg;
            _statusColor = c;
        }

        // ── Game blocking while open ─────────────────────────────────────
        private static bool GameEnded()
        {
            try { return EHS.GameManager.IsGameEnded; }
            catch (Exception ex)
            {
                TASPlugin.WarnOnce("menu-game-ended", $"GameManager.IsGameEnded unavailable: {ex.Message}");
                return false;
            }
        }

        private static void SetGameDevices(bool enabled)
        {
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                var mouse = UnityEngine.InputSystem.Mouse.current;
                if (enabled)
                {
                    if (kb != null) UnityEngine.InputSystem.InputSystem.EnableDevice(kb);
                    if (mouse != null) UnityEngine.InputSystem.InputSystem.EnableDevice(mouse);
                }
                else
                {
                    if (kb != null) UnityEngine.InputSystem.InputSystem.DisableDevice(kb);
                    if (mouse != null) UnityEngine.InputSystem.InputSystem.DisableDevice(mouse);
                }
            }
            catch (Exception ex)
            {
                TASPlugin.WarnOnce("menu-devices", $"Could not toggle input devices for the settings menu: {ex.Message}");
            }
        }

        /// <summary>
        /// Stops the camera and the player from reacting while the menu is open: Cinemachine's
        /// brain and input axis controller, and the player's movement component.
        /// </summary>
        private void DisableGameScripts()
        {
            _disabledScripts.Clear();
            try
            {
                var cam = Camera.main;
                if (cam != null)
                {
                    var brain = cam.GetComponent<Unity.Cinemachine.CinemachineBrain>();
                    if (brain != null && brain.enabled)
                    {
                        brain.enabled = false;
                        _disabledScripts.Add(brain);
                    }
                    var axisCtrl = cam.GetComponent<Unity.Cinemachine.CinemachineInputAxisController>();
                    if (axisCtrl != null && axisCtrl.enabled)
                    {
                        axisCtrl.enabled = false;
                        _disabledScripts.Add(axisCtrl);
                    }
                }

                var movement = _finder.GetMovement();
                if (movement != null && movement.enabled)
                {
                    movement.enabled = false;
                    _disabledScripts.Add(movement);
                }
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogWarning($"TAS: could not pause game scripts for the settings menu: {ex.Message}");
            }
        }

        private void EnableGameScripts()
        {
            foreach (var comp in _disabledScripts)
            {
                try
                {
                    if (comp != null) comp.enabled = true;
                }
                catch (Exception ex)
                {
                    TASPlugin.Logger.LogWarning($"TAS: could not re-enable {comp?.GetType().Name}: {ex.Message}");
                }
            }
            _disabledScripts.Clear();
        }
    }
}
