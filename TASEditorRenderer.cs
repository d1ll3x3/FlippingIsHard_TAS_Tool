using System;
using UnityEngine;

namespace FlippingIsHardTAS
{
    /// <summary>
    /// Frame-by-frame macro editor ("piano roll"), in the spirit of TAS Studio / libTAS.
    /// Works on the bit-perfect replay (state injection — no resimulation): Play/scrub/step
    /// reproduce the recorded states exactly. Timeline surgery (insert/delete/paste/trim)
    /// changes what plays back; per-cell input edits are data-only — to change a run's
    /// physics, re-record live from a chosen tick.
    /// </summary>
    public class TASEditorRenderer
    {
        public static bool IsVisibleGlobally = false;
        public static bool IsTextFieldFocused = false;

        private readonly TASController _controller;
        private bool _isVisible = false;
        private Rect _windowRect = new Rect(60, 40, 680, 740);
        private GUI.WindowFunction _windowDelegate;

        // Virtual scrolling
        private const int VISIBLE_ROWS = 16;
        private const float ROW_H = 26f;
        private long _topTick = 0;
        private bool _followPlayback = true;

        // Selection / inline editing
        private long _selectedTick = -1;
        private string _editMoveX = "0", _editMoveY = "0", _editPan = "0", _editTilt = "0";
        // IMGUI interactive controls (GUI.Button/Toggle/TextField) don't work in this
        // game's IL2CPP build — visuals render but events never fire. Like the bind menu,
        // everything is drawn with GUI.Box and hit-tested manually against legacy Input.
        private string _activeField = null;
        private bool _clickHandledThisFrame = false;

        // Manual window dragging — GUI.DragWindow relies on the broken IMGUI event pipeline
        private bool _dragging = false;
        private Vector2 _dragOffset;

        // Range tool
        private string _rangeFrom = "0", _rangeTo = "0";

        private string _statusMsg = "";
        private float _statusTimer = 0f;

        // One-time safety net per editor session: resim overwrites recorded physics
        // state, so a broken resim would silently destroy the macro without this.
        private bool _backupDone = false;

        // ===== Undo/redo (full-macro snapshots; TASInputState is a struct so a
        // dictionary copy is a deep copy) =====
        private class MacroSnapshot
        {
            public System.Collections.Generic.Dictionary<ulong, TASInputState> Rows;
            public ulong MaxTick;
            public ulong GreenzoneEnd;
        }
        private readonly System.Collections.Generic.List<MacroSnapshot> _undoStack
            = new System.Collections.Generic.List<MacroSnapshot>();
        private readonly System.Collections.Generic.List<MacroSnapshot> _redoStack
            = new System.Collections.Generic.List<MacroSnapshot>();
        private const int UNDO_CAP = 20;

        // Range clipboard: (offset from range start, state)
        private readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<ulong, TASInputState>> _clipboard
            = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<ulong, TASInputState>>();

        // Macro file name for the Save/Load section
        private string _fileName = "macro1";

        private MacroSnapshot TakeSnapshot(InputMacroSystem macro) => new MacroSnapshot
        {
            Rows = new System.Collections.Generic.Dictionary<ulong, TASInputState>(macro.RecordedInputs),
            MaxTick = macro.MaxTick,
            GreenzoneEnd = macro.GreenzoneEnd,
        };

        private void ApplySnapshot(InputMacroSystem macro, MacroSnapshot snap)
        {
            macro.RecordedInputs = new System.Collections.Generic.Dictionary<ulong, TASInputState>(snap.Rows);
            macro.MaxTick = snap.MaxTick;
            macro.GreenzoneEnd = snap.GreenzoneEnd;
        }

        /// <summary>Call BEFORE any mutation of the macro (edits, insert/delete, paste, resim).</summary>
        private void PushUndo(InputMacroSystem macro)
        {
            _undoStack.Add(TakeSnapshot(macro));
            if (_undoStack.Count > UNDO_CAP) _undoStack.RemoveAt(0);
            _redoStack.Clear();
        }

        private void PopUndoDiscard()
        {
            if (_undoStack.Count > 0) _undoStack.RemoveAt(_undoStack.Count - 1);
        }

        private void DoUndo(InputMacroSystem macro)
        {
            if (_undoStack.Count == 0) { SetStatus("Nothing to undo."); return; }
            _redoStack.Add(TakeSnapshot(macro));
            var snap = _undoStack[_undoStack.Count - 1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            ApplySnapshot(macro, snap);
            SetStatus($"Undo — greenzone at {macro.GreenzoneEnd}, {macro.RecordedInputs.Count} ticks.");
        }

        private void DoRedo(InputMacroSystem macro)
        {
            if (_redoStack.Count == 0) { SetStatus("Nothing to redo."); return; }
            _undoStack.Add(TakeSnapshot(macro));
            var snap = _redoStack[_redoStack.Count - 1];
            _redoStack.RemoveAt(_redoStack.Count - 1);
            ApplySnapshot(macro, snap);
            SetStatus($"Redo — greenzone at {macro.GreenzoneEnd}, {macro.RecordedInputs.Count} ticks.");
        }

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
            _backupDone = false;
        }

        // No resim anymore — timeline editing is always allowed. Kept as a single
        // gate point in case we want to block edits during live recording later.
        private bool EditingLocked() => false;

        private void BackupMacroOnce(InputMacroSystem macro)
        {
            if (_backupDone || macro == null || !macro.HasRecordedData) return;
            try
            {
                string path = System.IO.Path.Combine(BepInEx.Paths.PluginPath,
                    "FlippingIsHardTAS", "Macros", "editor_backup.tas");
                macro.ExportMacro(path);
                _backupDone = true;
                TASPlugin.Logger.LogInfo("TAS: Macro backed up to Macros/editor_backup.tas");
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Macro backup failed: {ex}");
            }
        }

        private void Close()
        {
            _isVisible = false;
            IsVisibleGlobally = false;
            IsTextFieldFocused = false;
            _activeField = null;
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

            // Keep the cursor free (to use the editor) — the game re-locks it every frame.
            // EXCEPT during live recording/Edit Mode (unpaused): then the cursor must stay
            // locked so the mouse drives the camera. During replay the cursor stays free
            // (the camera is replayed, not mouse-driven), so you can keep editing.
            var macroSys = _controller.MacroSystem;
            bool liveDriving = macroSys != null
                               && (macroSys.IsRecording || macroSys.IsEditMode)
                               && !_controller.EditorIsPaused;
            if (!liveDriving)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }

            InitStyles();

            if (_followPlayback && macroSys != null
                && (macroSys.IsPlaying || macroSys.IsRecording || macroSys.IsEditMode))
                CenterOn(_controller.EditorCurrentTick);

            if (Event.current.type == EventType.Repaint)
                _clickHandledThisFrame = false;

            HandleWindowDrag();

            _windowRect = GUI.Window(51237, _windowRect, _windowDelegate, "TAS EDITOR");

            IsTextFieldFocused = _activeField != null;
        }

        private void HandleWindowDrag()
        {
            if (Event.current.type != EventType.Repaint) return;

            Vector2 m = Input.mousePosition;
            m.y = Screen.height - m.y;

            if (Input.GetMouseButtonDown(0) && !_dragging)
            {
                // Title bar = top strip of the window, minus the X button corner
                var titleRect = new Rect(_windowRect.x, _windowRect.y, _windowRect.width - 44, 24);
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

        /// <summary>
        /// Button that works under IL2CPP: GUI.Box visual + manual hit-test against
        /// legacy Input (same approach as TASBindMenuRenderer.CustomButton).
        /// Rect is in window-local coordinates.
        /// </summary>
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

        /// <summary>
        /// Minimal text field that works under IL2CPP (GUI.TextField is stripped here).
        /// Click to focus, type to edit, Enter/Escape to unfocus.
        /// </summary>
        private string SimpleTextField(Rect r, string id, string value)
        {
            bool active = _activeField == id;
            GUI.color = active ? new Color(0.8f, 1f, 0.8f) : Color.white;
            GUI.Box(r, active ? value + "_" : value);
            GUI.color = Color.white;

            var e = Event.current;
            if (Input.GetMouseButtonDown(0) && e.type == EventType.Repaint)
            {
                Rect abs = new Rect(_windowRect.x + r.x, _windowRect.y + r.y, r.width, r.height);
                Vector2 m = Input.mousePosition;
                m.y = Screen.height - m.y;
                if (abs.Contains(m) && !_clickHandledThisFrame)
                {
                    _activeField = id;
                    _clickHandledThisFrame = true;
                }
                else if (active && !abs.Contains(m))
                    _activeField = null;
            }

            if (active && e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Backspace)
                {
                    if (value.Length > 0) value = value.Substring(0, value.Length - 1);
                    e.Use();
                }
                else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.Escape)
                {
                    _activeField = null;
                    e.Use();
                }
                else if (e.character != 0 && e.character >= 32)
                {
                    value += e.character;
                    e.Use();
                }
            }
            return value;
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
            if (CustomButton(new Rect(_windowRect.width - 36, 4, 28, 20), "X"))
            {
                Close();
                GUI.DragWindow();
                return;
            }
            GUI.backgroundColor = Color.white;

            float x = 12, y = 28;

            if (macro == null || !macro.HasRecordedData)
            {
                GUI.Label(new Rect(x, y, 600, 22), "No macro loaded. Record one (F9) or load it from the FILE section below.");
                GUI.DragWindow();
                return;
            }

            // ── Toolbar ──
            ulong curTick = _controller.EditorCurrentTick;
            GUI.Label(new Rect(x, y, 420, 20),
                $"Ticks: {macro.MaxTick}   Current: {curTick}" +
                (macro.IsPlaying ? "   [PLAYING]" : ""));

            GUI.backgroundColor = _followPlayback ? new Color(0.2f, 0.9f, 0.4f) : Color.white;
            if (CustomButton(new Rect(x + 430, y, 110, 20), _followPlayback ? "Follow: ON" : "Follow: OFF"))
                _followPlayback = !_followPlayback;
            GUI.backgroundColor = Color.white;
            y += 24;

            // ── Toolbar row 1: playback (bit-perfect replay) ──
            if (CustomButton(new Rect(x, y, 60, 22), macro.IsPlaying ? "Stop" : "Play"))
            {
                if (macro.IsPlaying) _controller.EditorStopPlayback();
                else
                {
                    BackupMacroOnce(macro);
                    if (!_controller.EditorPlayFromStart()) SetStatus("Could not start playback.");
                }
            }
            if (CustomButton(new Rect(x + 66, y, 70, 22), _controller.EditorIsPaused ? "Resume" : "Pause"))
                _controller.EditorTogglePause();
            if (CustomButton(new Rect(x + 142, y, 36, 22), "<"))
                _controller.EditorStepBack();
            if (CustomButton(new Rect(x + 184, y, 36, 22), ">"))
                _controller.EditorStepForward();
            if (CustomButton(new Rect(x + 226, y, 95, 22), "Go to current"))
                CenterOn(curTick);
            // Enter live Edit Mode at the selected tick (or current) and close the editor so
            // the cursor locks and the mouse drives the camera in-game.
            GUI.backgroundColor = new Color(1f, 0.6f, 0.2f);
            if (CustomButton(new Rect(x + 331, y, 90, 22), "Edit here"))
            {
                ulong at = _selectedTick >= 0 ? (ulong)_selectedTick : curTick;
                BackupMacroOnce(macro);
                if (_controller.EditorEnterEditModeAt(at))
                    ForceClose();
                else
                    SetStatus("Can't enter edit mode here (busy or no data at tick).");
            }
            GUI.backgroundColor = Color.white;
            y += 28;

            // ── Toolbar row 2: timeline editing ──
            if (CustomButton(new Rect(x, y, 60, 22), "Undo"))
                DoUndo(macro);
            if (CustomButton(new Rect(x + 66, y, 60, 22), "Redo"))
                DoRedo(macro);
            if (CustomButton(new Rect(x + 132, y, 70, 22), "Insert"))
                InsertAtSelection(macro);
            if (CustomButton(new Rect(x + 208, y, 70, 22), "Delete"))
                DeleteAtSelection(macro);
            if (CustomButton(new Rect(x + 284, y, 60, 22), "Copy"))
                CopyRange(macro);
            if (CustomButton(new Rect(x + 350, y, 95, 22), "Paste @ sel"))
                PasteAtSelection(macro);
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

            // ── Macro file save/load ──
            DrawFileSection(macro, x, ref y);

            if (_statusTimer > 0f)
            {
                _statusTimer -= Time.unscaledDeltaTime;
                GUI.Label(new Rect(x, _windowRect.height - 24, 620, 20), _statusMsg);
            }

            GUI.DragWindow();
        }

        private void HandleScroll()
        {
            // Legacy Input — IMGUI ScrollWheel events never fire in this game.
            // Only react once per frame (Repaint) and only with the mouse over the window.
            if (Event.current.type != EventType.Repaint) return;

            float scroll = Input.mouseScrollDelta.y;
            if (scroll == 0f) return;

            Vector2 m = Input.mousePosition;
            m.y = Screen.height - m.y;
            if (!_windowRect.Contains(m)) return;

            _topTick += scroll < 0 ? 3 : -3;
            ClampScroll();
            _followPlayback = false;
        }

        private void DrawRow(InputMacroSystem macro, ulong tick, ulong curTick, float x, float y)
        {
            var stateOpt = macro.GetStateAtTick(tick);

            // Row background: current tick highlighted red; recorded rows tinted green.
            Color bg;
            if (tick == curTick)        bg = new Color(0.85f, 0.25f, 0.25f, 0.45f);
            else if (stateOpt != null)  bg = new Color(0.15f, 0.5f, 0.2f, 0.30f);
            else                        bg = new Color(0.4f, 0.4f, 0.4f, 0.20f);
            if ((long)tick == _selectedTick) bg.a += 0.25f;

            float totalW = 0f;
            foreach (var w in ColW) totalW += w;
            GUI.color = bg;
            GUI.DrawTexture(new Rect(x, y, totalW, ROW_H - 2), Texture2D.whiteTexture);
            GUI.color = Color.white;

            float cx = x;

            // Tick column: click = select + seek (if inside greenzone)
            if (CustomButton(new Rect(cx + 2, y, ColW[0] - 4, ROW_H - 3), tick.ToString()))
            {
                if (EditingLocked()) return;
                _selectedTick = (long)tick;
                LoadSelectionIntoFields();
                _followPlayback = false;
                // Every recorded tick has a valid state (no resim), so any tick is seekable.
                if (!_controller.SeekToTick(tick))
                    SetStatus($"Could not seek to tick {tick}.");
            }
            cx += ColW[0];

            if (stateOpt == null)
            {
                GUI.Label(new Rect(cx, y, 200, ROW_H), "— no data —", _styleCell);
                return;
            }
            var s = stateOpt.Value;

            GUI.Label(new Rect(cx, y, ColW[1], ROW_H), s.Move.x.ToString("F2"), _styleCell);
            cx += ColW[1];
            GUI.Label(new Rect(cx, y, ColW[2], ROW_H), s.Move.y.ToString("F2"), _styleCell);
            cx += ColW[2];

            // Jump / Interact: one click toggles the button on that frame
            GUI.backgroundColor = s.Jump ? new Color(0.2f, 0.9f, 0.4f) : Color.white;
            if (CustomButton(new Rect(cx + 8, y, ColW[3] - 16, ROW_H - 3), s.Jump ? "J" : "·"))
            {
                if (!EditingLocked())
                {
                    BackupMacroOnce(macro);
                    PushUndo(macro);
                    macro.SetInputAt(tick, s.Move, !s.Jump, s.Interact, s.CameraPan, s.CameraTilt);
                    if (_selectedTick == (long)tick) LoadSelectionIntoFields();
                }
            }
            cx += ColW[3];
            GUI.backgroundColor = s.Interact ? new Color(0.2f, 0.9f, 0.4f) : Color.white;
            if (CustomButton(new Rect(cx + 8, y, ColW[4] - 16, ROW_H - 3), s.Interact ? "I" : "·"))
            {
                if (!EditingLocked())
                {
                    BackupMacroOnce(macro);
                    PushUndo(macro);
                    macro.SetInputAt(tick, s.Move, s.Jump, !s.Interact, s.CameraPan, s.CameraTilt);
                    if (_selectedTick == (long)tick) LoadSelectionIntoFields();
                }
            }
            GUI.backgroundColor = Color.white;
            cx += ColW[4];

            GUI.Label(new Rect(cx, y, ColW[5], ROW_H), s.CameraPan.ToString("F1"), _styleCell);
            cx += ColW[5];
            GUI.Label(new Rect(cx, y, ColW[6], ROW_H), s.CameraTilt.ToString("F1"), _styleCell);
        }

        private void DrawEditPanel(InputMacroSystem macro, float x, ref float y)
        {
            GUI.Label(new Rect(x, y, 200, 20), $"EDIT TICK {(_selectedTick >= 0 ? _selectedTick.ToString() : "—")}", _styleHeader);
            y += 22;

            GUI.Label(new Rect(x, y, 55, 20), "MoveX:");
            _editMoveX = SimpleTextField(new Rect(x + 55, y, 60, 20), "mx", _editMoveX);
            GUI.Label(new Rect(x + 125, y, 55, 20), "MoveY:");
            _editMoveY = SimpleTextField(new Rect(x + 180, y, 60, 20), "my", _editMoveY);
            GUI.Label(new Rect(x + 250, y, 40, 20), "Pan:");
            _editPan = SimpleTextField(new Rect(x + 290, y, 60, 20), "pan", _editPan);
            GUI.Label(new Rect(x + 360, y, 40, 20), "Tilt:");
            _editTilt = SimpleTextField(new Rect(x + 400, y, 60, 20), "tilt", _editTilt);

            if (CustomButton(new Rect(x + 475, y, 80, 22), "Apply"))
                ApplyEditFields(macro);
            y += 28;
        }

        private void DrawRangeTool(InputMacroSystem macro, float x, ref float y)
        {
            GUI.Label(new Rect(x, y, 200, 20), "RANGE", _styleHeader);
            y += 22;

            GUI.Label(new Rect(x, y, 45, 20), "From:");
            _rangeFrom = SimpleTextField(new Rect(x + 45, y, 65, 20), "rfrom", _rangeFrom);
            GUI.Label(new Rect(x + 120, y, 45, 20), "To:");
            _rangeTo = SimpleTextField(new Rect(x + 165, y, 65, 20), "rto", _rangeTo);

            if (CustomButton(new Rect(x + 245, y, 90, 22), "Jump ON"))
                ApplyRange(macro, jump: true);
            if (CustomButton(new Rect(x + 340, y, 90, 22), "Jump OFF"))
                ApplyRange(macro, jump: false);
            if (CustomButton(new Rect(x + 435, y, 75, 22), "Int ON"))
                ApplyRange(macro, interact: true);
            if (CustomButton(new Rect(x + 515, y, 75, 22), "Int OFF"))
                ApplyRange(macro, interact: false);
            y += 28;
        }

        private void ApplyRange(InputMacroSystem macro, bool? jump = null, bool? interact = null)
        {
            if (!ulong.TryParse(_rangeFrom, out ulong from) || !ulong.TryParse(_rangeTo, out ulong to) || to < from)
            {
                SetStatus("Invalid range.");
                return;
            }
            if (EditingLocked()) return;
            BackupMacroOnce(macro);
            PushUndo(macro);
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
            SetStatus($"Applied J/I to {applied} ticks ({from}–{to}). Data only — re-record to change the run.");
        }

        private void InsertAtSelection(InputMacroSystem macro)
        {
            if (EditingLocked()) return;
            if (_selectedTick < 0) { SetStatus("Select a tick first (click its number)."); return; }
            BackupMacroOnce(macro);
            PushUndo(macro);
            macro.InsertTickAt((ulong)_selectedTick);
            SetStatus($"Frame inserted at {_selectedTick} — later frames shifted +1 (MaxTick {macro.MaxTick}).");
        }

        private void DeleteAtSelection(InputMacroSystem macro)
        {
            if (EditingLocked()) return;
            if (_selectedTick < 0) { SetStatus("Select a tick first (click its number)."); return; }
            BackupMacroOnce(macro);
            PushUndo(macro);
            macro.DeleteTickAt((ulong)_selectedTick);
            if ((ulong)_selectedTick > macro.MaxTick) _selectedTick = (long)macro.MaxTick;
            LoadSelectionIntoFields();
            SetStatus($"Frame {_selectedTick} deleted — later frames shifted -1 (MaxTick {macro.MaxTick}).");
        }

        private void CopyRange(InputMacroSystem macro)
        {
            if (!ulong.TryParse(_rangeFrom, out ulong from) || !ulong.TryParse(_rangeTo, out ulong to) || to < from)
            {
                SetStatus("Invalid range (set From/To in the RANGE section).");
                return;
            }
            _clipboard.Clear();
            for (ulong t = from; t <= to && t <= macro.MaxTick; t++)
            {
                var st = macro.GetStateAtTick(t);
                if (st != null)
                    _clipboard.Add(new System.Collections.Generic.KeyValuePair<ulong, TASInputState>(t - from, st.Value));
            }
            SetStatus($"Copied {_clipboard.Count} frames ({from}–{to}).");
        }

        private void PasteAtSelection(InputMacroSystem macro)
        {
            if (EditingLocked()) return;
            if (_clipboard.Count == 0) { SetStatus("Clipboard empty — use Copy first."); return; }
            // Fall back to the current tick if nothing is selected (same as "Edit here").
            ulong at = _selectedTick >= 0 ? (ulong)_selectedTick : _controller.EditorCurrentTick;

            BackupMacroOnce(macro);
            PushUndo(macro);
            int pasted = 0;
            foreach (var kvp in _clipboard)
            {
                ulong target = at + kvp.Key;
                if (target > macro.MaxTick) break;
                // Paste all input fields (Move X/Y, Jump, Interact, Pan, Tilt) onto the
                // destination tick, leaving its physics state intact (no teleport).
                var src = kvp.Value;
                macro.SetInputAt(target, src.Move, src.Jump, src.Interact, src.CameraPan, src.CameraTilt);
                pasted++;
            }
            SetStatus($"Pasted {pasted} frames at {at} (MoveX/Y, Jump, Int, Pan, Tilt).");
        }

        private void DrawFileSection(InputMacroSystem macro, float x, ref float y)
        {
            GUI.Label(new Rect(x, y, 200, 20), "FILE", _styleHeader);
            y += 22;

            GUI.Label(new Rect(x, y, 45, 20), "Name:");
            _fileName = SimpleTextField(new Rect(x + 45, y, 160, 20), "fname", _fileName);

            string path = System.IO.Path.Combine(BepInEx.Paths.PluginPath,
                "FlippingIsHardTAS", "Macros", _fileName + ".tas");

            if (CustomButton(new Rect(x + 215, y, 70, 22), "Save"))
            {
                if (macro.HasRecordedData)
                {
                    try { macro.ExportMacro(path); SetStatus($"Saved to Macros/{_fileName}.tas"); }
                    catch (Exception ex) { SetStatus("Save failed!"); TASPlugin.Logger.LogError(ex.ToString()); }
                }
                else SetStatus("No macro to save.");
            }
            if (CustomButton(new Rect(x + 291, y, 70, 22), "Load"))
            {
                if (!EditingLocked())
                {
                    _controller.EditorStopPlayback();
                    PushUndo(macro);
                    if (macro.ImportMacro(path))
                    {
                        _controller.EditorMacroImported();
                        _selectedTick = -1;
                        _topTick = 0;
                        ClampScroll();
                        SetStatus($"Loaded Macros/{_fileName}.tas — {macro.RecordedInputs.Count} ticks.");
                    }
                    else
                    {
                        PopUndoDiscard();
                        SetStatus($"Could not load Macros/{_fileName}.tas");
                    }
                }
            }
            y += 28;
        }

        private void LoadSelectionIntoFields()
        {
            var macro = _controller.MacroSystem;
            if (macro == null || _selectedTick < 0) return;
            var st = macro.GetStateAtTick((ulong)_selectedTick);
            if (st == null) return;
            // Invariant culture: on Spanish Windows ToString gives "12,34" which the
            // invariant parser in TryParseFloat rejects — Apply failed without edits.
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            _editMoveX = st.Value.Move.x.ToString("F2", inv);
            _editMoveY = st.Value.Move.y.ToString("F2", inv);
            _editPan = st.Value.CameraPan.ToString("F2", inv);
            _editTilt = st.Value.CameraTilt.ToString("F2", inv);
        }

        private void ApplyEditFields(InputMacroSystem macro)
        {
            if (EditingLocked()) return;
            if (_selectedTick < 0) { SetStatus("Select a tick first (click its number)."); return; }
            var st = macro.GetStateAtTick((ulong)_selectedTick);
            if (st == null) { SetStatus("Selected tick has no data."); return; }

            if (!TryParseFloat(_editMoveX, out float mx) || !TryParseFloat(_editMoveY, out float my) ||
                !TryParseFloat(_editPan, out float pan) || !TryParseFloat(_editTilt, out float tilt))
            {
                SetStatus("Invalid numeric value.");
                return;
            }

            mx = Mathf.Clamp(mx, -1f, 1f);
            my = Mathf.Clamp(my, -1f, 1f);

            var s = st.Value;
            BackupMacroOnce(macro);
            PushUndo(macro);

            // Move/Jump/Interact: data-only edit on the selected tick.
            macro.SetInputAt((ulong)_selectedTick, new Vector2(mx, my), s.Jump, s.Interact, s.CameraPan, s.CameraTilt);

            // Camera: aim it and have it STAY — propagate the new pan/tilt from this tick to
            // the end (a later edit at a higher tick overrides downstream). The replay renders
            // the camera from these orbital axes, so the view holds.
            bool camChanged = !Mathf.Approximately(pan, s.CameraPan) || !Mathf.Approximately(tilt, s.CameraTilt);
            if (camChanged)
            {
                macro.SetCameraFrom((ulong)_selectedTick, pan, tilt);
                SetStatus($"Camera set to {pan:F1}/{tilt:F1} from tick {_selectedTick} onward.");
            }
            else
            {
                SetStatus($"Tick {_selectedTick} inputs edited (data only — re-record from here to change the run).");
            }
        }

        private static bool TryParseFloat(string str, out float value)
        {
            // Accept both "12.5" and "12,5" (comma decimal separator on Spanish locales)
            str = (str ?? "").Trim().Replace(',', '.');
            if (!float.TryParse(str, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out value))
                return false;
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private void SetStatus(string msg)
        {
            _statusMsg = msg;
            _statusTimer = 5f;
        }
    }
}
