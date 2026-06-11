using System;
using UnityEngine;

namespace FlippingIsHardTAS
{
    /// <summary>
    /// Frame-by-frame macro editor ("piano roll"), in the spirit of TAS Studio / libTAS.
    /// Shows one row per physics tick with the recorded inputs; cells can be edited.
    /// Rows inside the greenzone (valid physics state) are tinted green; rows past it
    /// are stale and need a playback pass (resimulation) to become valid again.
    /// </summary>
    public class TASEditorRenderer
    {
        public static bool IsVisibleGlobally = false;
        public static bool IsTextFieldFocused = false;

        private readonly TASController _controller;
        private bool _isVisible = false;
        private Rect _windowRect = new Rect(60, 60, 660, 580);
        private GUI.WindowFunction _windowDelegate;

        // Virtual scrolling
        private const int VISIBLE_ROWS = 18;
        private const float ROW_H = 22f;
        private long _topTick = 0;
        private bool _followPlayback = true;

        // Selection / inline editing
        private long _selectedTick = -1;
        private string _editMoveX = "0", _editMoveY = "0", _editPan = "0", _editTilt = "0";

        // Range tool
        private string _rangeFrom = "0", _rangeTo = "0";

        private string _statusMsg = "";
        private float _statusTimer = 0f;

        // Styles
        private GUIStyle _styleCell, _styleCellOn, _styleHeader;
        private bool _stylesReady = false;

        private CursorLockMode _prevLock;
        private bool _prevCursorVisible;

        public TASEditorRenderer(TASController controller)
        {
            _controller = controller;
            _windowDelegate = new Action<int>(WindowFunction);
        }

        public bool IsVisible => _isVisible;

        public void ForceClose()
        {
            if (_isVisible) Close();
        }

        public void ToggleVisibility()
        {
            if (_isVisible) Close();
            else Open();
        }

        private void Open()
        {
            _isVisible = true;
            IsVisibleGlobally = true;
            _controller.EditorPauseGame();

            _prevLock = Cursor.lockState;
            _prevCursorVisible = Cursor.visible;

            var macro = _controller.MacroSystem;
            if (macro != null && macro.HasRecordedData)
            {
                _selectedTick = (long)_controller.EditorCurrentTick;
                CenterOn(_controller.EditorCurrentTick);
                LoadSelectionIntoFields();
            }
        }

        private void Close()
        {
            _isVisible = false;
            IsVisibleGlobally = false;
            IsTextFieldFocused = false;
            Cursor.lockState = _prevLock;
            Cursor.visible = _prevCursorVisible;
        }

        private void CenterOn(ulong tick)
        {
            _topTick = (long)tick - VISIBLE_ROWS / 2;
            ClampScroll();
        }

        private void ClampScroll()
        {
            var macro = _controller.MacroSystem;
            long max = macro != null ? (long)macro.MaxTick : 0;
            if (_topTick > max - 1) _topTick = max - 1;
            if (_topTick < 0) _topTick = 0;
        }

        public void Draw()
        {
            if (!_isVisible) return;

            // Keep the cursor usable every frame — the game re-locks it
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            InitStyles();

            if (_followPlayback && _controller.MacroSystem != null && _controller.MacroSystem.IsPlaying)
                CenterOn(_controller.EditorCurrentTick);

            _windowRect = GUI.Window(51237, _windowRect, _windowDelegate, "TAS EDITOR");

            IsTextFieldFocused = !string.IsNullOrEmpty(GUI.GetNameOfFocusedControl()) &&
                                 GUI.GetNameOfFocusedControl().StartsWith("tased_");
        }

        private void InitStyles()
        {
            if (_stylesReady) return;
            _styleCell = new GUIStyle { fontSize = 12, alignment = TextAnchor.MiddleCenter };
            _styleCell.normal.textColor = Color.white;
            _styleCellOn = new GUIStyle { fontSize = 12, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _styleCellOn.normal.textColor = new Color(0.3f, 1f, 0.5f);
            _styleHeader = new GUIStyle { fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _styleHeader.normal.textColor = new Color(0.5f, 0.75f, 1f);
            _stylesReady = true;
        }

        // Column layout: Tick | MoveX | MoveY | Jump | Int | Pan | Tilt
        private static readonly float[] ColW = { 80f, 80f, 80f, 60f, 60f, 90f, 90f };
        private static readonly string[] ColName = { "TICK", "MOVE X", "MOVE Y", "JUMP", "INT", "PAN", "TILT" };

        private void WindowFunction(int id)
        {
            var macro = _controller.MacroSystem;

            GUI.backgroundColor = new Color(0.7f, 0.2f, 0.2f, 1f);
            if (GUI.Button(new Rect(_windowRect.width - 36, 4, 28, 20), "X"))
            {
                Close();
                GUI.DragWindow();
                return;
            }
            GUI.backgroundColor = Color.white;

            float x = 12, y = 28;

            if (macro == null || !macro.HasRecordedData)
            {
                GUI.Label(new Rect(x, y, 600, 22), "No hay macro cargado. Graba (F9) o importa uno desde el menú (B).");
                GUI.DragWindow();
                return;
            }

            // ── Toolbar ──
            ulong curTick = _controller.EditorCurrentTick;
            GUI.Label(new Rect(x, y, 420, 20),
                $"Ticks: {macro.MaxTick}   Greenzone: {macro.GreenzoneEnd}   Actual: {curTick}" +
                (macro.IsPlaying ? "   [PLAYING]" : ""));

            _followPlayback = GUI.Toggle(new Rect(x + 430, y, 90, 20), _followPlayback, " Seguir");
            y += 24;

            if (GUI.Button(new Rect(x, y, 70, 22), macro.IsPlaying ? "Stop" : "Play"))
            {
                if (macro.IsPlaying) _controller.EditorStopPlayback();
                else if (!_controller.EditorPlayFromStart()) SetStatus("No se pudo iniciar el playback.");
            }
            if (GUI.Button(new Rect(x + 76, y, 130, 22), "Resim completo"))
            {
                if (_controller.EditorPlayFromStart(inputOnly: true))
                    SetStatus("Resimulando todo el macro solo con inputs…");
                else
                    SetStatus("No se pudo iniciar la resimulación.");
            }
            if (GUI.Button(new Rect(x + 212, y, 110, 22), "Ir a actual"))
                CenterOn(curTick);
            y += 30;

            // ── Header row ──
            float cx = x;
            for (int c = 0; c < ColName.Length; c++)
            {
                GUI.Label(new Rect(cx, y, ColW[c], 18), ColName[c], _styleHeader);
                cx += ColW[c];
            }
            y += 20;

            // ── Rows (virtual scroll) ──
            HandleScroll();
            float rowsTop = y;
            for (int i = 0; i < VISIBLE_ROWS; i++)
            {
                long tick = _topTick + i;
                if (tick < 0 || (ulong)tick > macro.MaxTick) break;
                DrawRow(macro, (ulong)tick, curTick, x, y);
                y += ROW_H;
            }
            y = rowsTop + VISIBLE_ROWS * ROW_H + 6;

            // ── Edit panel for the selected tick ──
            DrawEditPanel(macro, x, ref y);

            // ── Range tool ──
            DrawRangeTool(macro, x, ref y);

            if (_statusTimer > 0f)
            {
                _statusTimer -= Time.unscaledDeltaTime;
                GUI.Label(new Rect(x, _windowRect.height - 24, 620, 20), _statusMsg);
            }

            GUI.DragWindow();
        }

        private void HandleScroll()
        {
            var e = Event.current;
            if (e.type == EventType.ScrollWheel)
            {
                _topTick += e.delta.y > 0 ? 3 : -3;
                ClampScroll();
                _followPlayback = false;
                e.Use();
            }
        }

        private void DrawRow(InputMacroSystem macro, ulong tick, ulong curTick, float x, float y)
        {
            var stateOpt = macro.GetStateAtTick(tick);

            // Row background: current tick > greenzone > stale
            Color bg;
            if (tick == curTick)            bg = new Color(0.85f, 0.25f, 0.25f, 0.45f);
            else if (tick <= macro.GreenzoneEnd) bg = new Color(0.15f, 0.5f, 0.2f, 0.30f);
            else                            bg = new Color(0.4f, 0.4f, 0.4f, 0.20f);
            if ((long)tick == _selectedTick) bg.a += 0.25f;

            float totalW = 0f;
            foreach (var w in ColW) totalW += w;
            GUI.color = bg;
            GUI.DrawTexture(new Rect(x, y, totalW, ROW_H - 2), Texture2D.whiteTexture);
            GUI.color = Color.white;

            float cx = x;

            // Tick column: click = select + seek (if inside greenzone)
            if (GUI.Button(new Rect(cx + 2, y, ColW[0] - 4, ROW_H - 3), tick.ToString()))
            {
                _selectedTick = (long)tick;
                LoadSelectionIntoFields();
                _followPlayback = false;
                if (tick <= macro.GreenzoneEnd)
                {
                    if (!_controller.SeekToTick(tick))
                        SetStatus($"No se pudo hacer seek al tick {tick}.");
                }
                else
                    SetStatus("Tick fuera de la greenzone — usa Play/Resim para regenerar el estado.");
            }
            cx += ColW[0];

            if (stateOpt == null)
            {
                GUI.Label(new Rect(cx, y, 200, ROW_H), "— sin datos —", _styleCell);
                return;
            }
            var s = stateOpt.Value;

            GUI.Label(new Rect(cx, y, ColW[1], ROW_H), s.Move.x.ToString("F2"), _styleCell);
            cx += ColW[1];
            GUI.Label(new Rect(cx, y, ColW[2], ROW_H), s.Move.y.ToString("F2"), _styleCell);
            cx += ColW[2];

            // Jump / Interact: one click toggles the button on that frame
            GUI.backgroundColor = s.Jump ? new Color(0.2f, 0.9f, 0.4f) : Color.white;
            if (GUI.Button(new Rect(cx + 8, y, ColW[3] - 16, ROW_H - 3), s.Jump ? "J" : "·"))
            {
                macro.SetInputAt(tick, s.Move, !s.Jump, s.Interact, s.CameraPan, s.CameraTilt);
                if (_selectedTick == (long)tick) LoadSelectionIntoFields();
            }
            cx += ColW[3];
            GUI.backgroundColor = s.Interact ? new Color(0.2f, 0.9f, 0.4f) : Color.white;
            if (GUI.Button(new Rect(cx + 8, y, ColW[4] - 16, ROW_H - 3), s.Interact ? "I" : "·"))
            {
                macro.SetInputAt(tick, s.Move, s.Jump, !s.Interact, s.CameraPan, s.CameraTilt);
                if (_selectedTick == (long)tick) LoadSelectionIntoFields();
            }
            GUI.backgroundColor = Color.white;
            cx += ColW[4];

            GUI.Label(new Rect(cx, y, ColW[5], ROW_H), s.CameraPan.ToString("F1"), _styleCell);
            cx += ColW[5];
            GUI.Label(new Rect(cx, y, ColW[6], ROW_H), s.CameraTilt.ToString("F1"), _styleCell);
        }

        private void DrawEditPanel(InputMacroSystem macro, float x, ref float y)
        {
            GUI.Label(new Rect(x, y, 200, 20), $"EDITAR TICK {(_selectedTick >= 0 ? _selectedTick.ToString() : "—")}", _styleHeader);
            y += 22;

            GUI.Label(new Rect(x, y, 55, 20), "MoveX:");
            GUI.SetNextControlName("tased_mx");
            _editMoveX = GUI.TextField(new Rect(x + 55, y, 60, 20), _editMoveX);
            GUI.Label(new Rect(x + 125, y, 55, 20), "MoveY:");
            GUI.SetNextControlName("tased_my");
            _editMoveY = GUI.TextField(new Rect(x + 180, y, 60, 20), _editMoveY);
            GUI.Label(new Rect(x + 250, y, 40, 20), "Pan:");
            GUI.SetNextControlName("tased_pan");
            _editPan = GUI.TextField(new Rect(x + 290, y, 60, 20), _editPan);
            GUI.Label(new Rect(x + 360, y, 40, 20), "Tilt:");
            GUI.SetNextControlName("tased_tilt");
            _editTilt = GUI.TextField(new Rect(x + 400, y, 60, 20), _editTilt);

            if (GUI.Button(new Rect(x + 475, y, 80, 22), "Aplicar"))
                ApplyEditFields(macro);
            y += 28;
        }

        private void DrawRangeTool(InputMacroSystem macro, float x, ref float y)
        {
            GUI.Label(new Rect(x, y, 200, 20), "RANGO", _styleHeader);
            y += 22;

            GUI.Label(new Rect(x, y, 45, 20), "Desde:");
            GUI.SetNextControlName("tased_rf");
            _rangeFrom = GUI.TextField(new Rect(x + 45, y, 65, 20), _rangeFrom);
            GUI.Label(new Rect(x + 120, y, 45, 20), "Hasta:");
            GUI.SetNextControlName("tased_rt");
            _rangeTo = GUI.TextField(new Rect(x + 165, y, 65, 20), _rangeTo);

            if (GUI.Button(new Rect(x + 245, y, 90, 22), "Jump ON"))
                ApplyRange(macro, jump: true);
            if (GUI.Button(new Rect(x + 340, y, 90, 22), "Jump OFF"))
                ApplyRange(macro, jump: false);
            if (GUI.Button(new Rect(x + 435, y, 75, 22), "Int ON"))
                ApplyRange(macro, interact: true);
            if (GUI.Button(new Rect(x + 515, y, 75, 22), "Int OFF"))
                ApplyRange(macro, interact: false);
            y += 28;
        }

        private void ApplyRange(InputMacroSystem macro, bool? jump = null, bool? interact = null)
        {
            if (!ulong.TryParse(_rangeFrom, out ulong from) || !ulong.TryParse(_rangeTo, out ulong to) || to < from)
            {
                SetStatus("Rango inválido.");
                return;
            }
            int applied = 0;
            for (ulong t = from; t <= to && t <= macro.MaxTick; t++)
            {
                var st = macro.GetStateAtTick(t);
                if (st == null) continue;
                var s = st.Value;
                macro.SetInputAt(t, s.Move,
                                 jump ?? s.Jump,
                                 interact ?? s.Interact,
                                 s.CameraPan, s.CameraTilt);
                applied++;
            }
            SetStatus($"Aplicado a {applied} ticks ({from}–{to}). Greenzone cortada en {macro.GreenzoneEnd}.");
        }

        private void LoadSelectionIntoFields()
        {
            var macro = _controller.MacroSystem;
            if (macro == null || _selectedTick < 0) return;
            var st = macro.GetStateAtTick((ulong)_selectedTick);
            if (st == null) return;
            _editMoveX = st.Value.Move.x.ToString("F3");
            _editMoveY = st.Value.Move.y.ToString("F3");
            _editPan = st.Value.CameraPan.ToString("F2");
            _editTilt = st.Value.CameraTilt.ToString("F2");
        }

        private void ApplyEditFields(InputMacroSystem macro)
        {
            if (_selectedTick < 0) { SetStatus("Selecciona un tick primero (clic en su número)."); return; }
            var st = macro.GetStateAtTick((ulong)_selectedTick);
            if (st == null) { SetStatus("El tick seleccionado no tiene datos."); return; }

            if (!TryParseFloat(_editMoveX, out float mx) || !TryParseFloat(_editMoveY, out float my) ||
                !TryParseFloat(_editPan, out float pan) || !TryParseFloat(_editTilt, out float tilt))
            {
                SetStatus("Valor numérico inválido.");
                return;
            }

            mx = Mathf.Clamp(mx, -1f, 1f);
            my = Mathf.Clamp(my, -1f, 1f);

            var s = st.Value;
            macro.SetInputAt((ulong)_selectedTick, new Vector2(mx, my), s.Jump, s.Interact, pan, tilt);
            SetStatus($"Tick {_selectedTick} editado. Greenzone cortada en {macro.GreenzoneEnd}.");
        }

        private static bool TryParseFloat(string str, out float value)
        {
            return float.TryParse(str, System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        private void SetStatus(string msg)
        {
            _statusMsg = msg;
            _statusTimer = 5f;
        }
    }
}
