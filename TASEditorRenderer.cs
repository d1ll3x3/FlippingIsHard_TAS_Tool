using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace FlippingIsHardTAS
{
    /// <summary>
    /// Frame-by-frame macro editor ("piano roll"). Works on the bit-perfect replay (state
    /// injection — no resimulation): Play / seek / step reproduce the recorded states
    /// exactly, and timeline surgery (insert / delete / paste / trim) changes what plays
    /// back. To change the INPUTS of a stretch you fork it: go to a frame, pin the inputs
    /// you want and frame-advance — the run is re-recorded live from there.
    /// </summary>
    public class TASEditorRenderer
    {
        private readonly TASController _controller;
        private readonly GuiWindow _win = new GuiWindow("TAS EDITOR", new Rect(60, 40, 724, 760));
        private bool _visible;
        private bool _placed;

        // ── Table / selection ────────────────────────────────────────────
        private const float ROW_H = 22f;
        private int _visibleRows = 18;
        private long _topTick;
        private bool _follow = true;
        private long _anchor = -1, _cursor = -1;   // selection = [min, max] of the two
        private bool _draggingThumb;
        private float _thumbGrabOffset;

        private long SelFrom => Math.Min(_anchor, _cursor);
        private long SelTo => Math.Max(_anchor, _cursor);
        private bool HasSelection => _cursor >= 0 && _anchor >= 0;

        // Column layout
        private static readonly float[] ColW = { 70f, 74f, 52f, 52f, 46f, 46f, 74f, 74f, 80f, 80f };
        private static readonly string[] ColName = { "TICK", "KEYS", "X", "Y", "JUMP", "INT", "PAN", "TILT", "SPEED", "HEIGHT" };
        private const float TABLE_X = 12f, SCROLL_W = 12f;

        // ── Panels ───────────────────────────────────────────────────────
        private string _pinX = "0", _pinY = "0";
        private string _aimPan = "0", _aimTilt = "0";
        private string _fileName = "macro1";
        private string _currentFile;   // file the macro was last loaded from / saved to
        private string[] _files = Array.Empty<string>();
        private int _fileScroll;
        private string _lastClickedFile;
        private float _lastFileClickTime;

        private string _status = "";
        private Color _statusColor = TASGui.TextDim;
        private float _statusTime;

        // Two-click confirmation for destructive actions
        private string _confirmKey;
        private float _confirmUntil;

        // ── Undo / redo (full-run snapshots; TASInputState is a struct → deep copy) ──
        private sealed class Snapshot
        {
            public Dictionary<ulong, TASInputState> Rows;
            public ulong MaxTick;
            public int Seed;
        }
        private readonly List<Snapshot> _undo = new List<Snapshot>();
        private readonly List<Snapshot> _redo = new List<Snapshot>();
        private const int UNDO_CAP = 30;

        private readonly List<TASInputState> _clipboard = new List<TASInputState>();

        // Dirty tracking against the last save/load
        private int _savedVersion = -1;
        private bool _backupDone;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public TASEditorRenderer(TASController controller)
        {
            _controller = controller;
        }

        private InputMacroSystem Macro => _controller.MacroSystem;
        public bool IsVisible => _visible;

        private static string MacroDir => Path.Combine(BepInEx.Paths.PluginPath, "FlippingIsHardTAS", "Macros");

        private bool IsDirty => Macro.HasRecordedData && Macro.Version != _savedVersion;

        // ═════════════════════════════════════════════════════════════════
        //  Open / close
        // ═════════════════════════════════════════════════════════════════

        public void ToggleVisibility()
        {
            if (_visible) Close();
            else Open();
        }

        public void ForceClose()
        {
            if (_visible) Close();
        }

        private void Open()
        {
            _visible = true;
            _backupDone = false;
            _controller.PauseGame();
            RefreshFiles();

            if (!_placed)
            {
                _placed = true;
                _win.Rect.x = Mathf.Max(20f, Screen.width - _win.Rect.width - 260f);
                _win.Rect.y = 60f;
            }
            _win.ClampToScreen();

            if (Macro.HasRecordedData)
            {
                ulong cur = Clamp(_controller.CurrentTick);
                _anchor = _cursor = (long)cur;
                CenterOn(cur);
                LoadAimFields();
            }
        }

        private void Close()
        {
            _visible = false;
            TASGui.ActiveField = null;
            _draggingThumb = false;
            // Hand the cursor back FREE: in gameplay the game re-locks it every frame for
            // mouse-look; at the main menu it must stay free so you can click.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        // ═════════════════════════════════════════════════════════════════
        //  Draw
        // ═════════════════════════════════════════════════════════════════

        public void Draw()
        {
            if (!_visible) return;

            // Keep the cursor free to use the editor — except while recording live
            // (unpaused), where the mouse must keep driving the camera.
            if (!(Macro.IsRecording && !_controller.IsPaused))
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }

            // Fit the table to the screen height.
            _visibleRows = Mathf.Clamp(Mathf.FloorToInt((Screen.height - 440f) / ROW_H), 6, 24);
            _win.Rect.height = 380f + _visibleRows * ROW_H;

            if (_follow && (Macro.IsPlaying || Macro.IsRecording) && !_draggingThumb)
                CenterOn(_controller.CurrentTick);

            if (_win.DrawChrome(Subtitle()))
            {
                Close();
                return;
            }

            float y = GuiWindow.TitleH + 10f;
            DrawTransport(ref y);
            DrawEditBar(ref y);
            DrawTable(ref y);
            DrawPinsAndAim(ref y);
            DrawFiles(ref y);
            DrawStatusBar();

            _win.End();
        }

        private string Subtitle()
        {
            string mode = Macro.IsEditMode ? "EDIT MODE" : Macro.IsRecording ? "RECORDING" : Macro.IsPlaying ? "REPLAY" : "IDLE";
            if (!Macro.HasRecordedData) return mode;
            string file = _currentFile != null ? _currentFile + ".tas" : "unsaved";
            return $"{file}{(IsDirty ? " •" : "")}   ·   {mode}";
        }

        private Rect R(float x, float y, float w, float h) => _win.R(x, y, w, h);

        // ── Transport ────────────────────────────────────────────────────
        private void DrawTransport(ref float y)
        {
            bool has = Macro.HasRecordedData;
            float x = 12f;

            if (TASGui.Button(R(x, y, 76, 26), Macro.IsPlaying ? "■ Stop" : "▶ Play", has, Macro.IsPlaying, TASGui.Play))
            {
                if (Macro.IsPlaying) _controller.StopPlaybackFromEditor();
                else if (_controller.PlayFromStart(unpause: true)) SetStatus("Replaying from the start.", TASGui.Play);
            }
            x += 82;
            if (TASGui.Button(R(x, y, 84, 26), _controller.IsPaused ? "Resume" : "Pause", true, _controller.IsPaused, TASGui.Pause))
                _controller.TogglePause();
            x += 90;
            if (TASGui.Button(R(x, y, 36, 26), "<", has)) _controller.EditorStepBack();
            x += 40;
            if (TASGui.Button(R(x, y, 36, 26), ">", has || Macro.IsRecording)) _controller.EditorStepForward();
            x += 48;

            string tick = has ? $"Tick  {_controller.CurrentTick}  /  {Macro.MaxTick}" : $"Tick  {_controller.CurrentTick}";
            TASGui.Label(R(x, y, 260, 26), tick, TASGui.Text, 14, true);

            _follow = TASGui.Toggle(R(_win.Rect.width - 104, y, 92, 26), "Follow", _follow, TASGui.Ok);
            y += 34;
        }

        // ── Edit bar ─────────────────────────────────────────────────────
        private void DrawEditBar(ref float y)
        {
            bool has = Macro.HasRecordedData;
            bool sel = has && HasSelection;
            float x = 12f;

            if (TASGui.Button(R(x, y, 56, 24), "Undo", _undo.Count > 0)) DoUndo();
            x += 60;
            if (TASGui.Button(R(x, y, 56, 24), "Redo", _redo.Count > 0)) DoRedo();
            x += 68;
            if (TASGui.Button(R(x, y, 60, 24), "Insert", sel)) InsertFrames();
            x += 64;
            if (TASGui.Button(R(x, y, 60, 24), "Delete", sel)) DeleteFrames();
            x += 72;
            if (TASGui.Button(R(x, y, 52, 24), "Copy", sel)) CopyFrames();
            x += 56;
            if (TASGui.Button(R(x, y, 52, 24), "Paste", sel && _clipboard.Count > 0)) PasteFrames();
            x += 64;
            if (TASGui.Button(R(x, y, 82, 24), "Edit here", sel, accent: TASGui.Edit)) EditHere();
            x += 86;
            if (TASGui.Button(R(x, y, 82, 24), "Trim after", sel)) TrimAfter();
            x += 94;
            if (TASGui.Button(R(x, y, 50, 24), "New", has, accent: TASGui.Danger)) NewMacro();
            y += 32;
        }

        // ── Table ────────────────────────────────────────────────────────
        private float TableWidth()
        {
            float w = 0f;
            foreach (var c in ColW) w += c;
            return w;
        }

        private void DrawTable(ref float y)
        {
            float tw = TableWidth();
            float cx = TABLE_X;
            TASGui.Fill(R(TABLE_X, y, tw + SCROLL_W + 2, 20), TASGui.BgRaised);
            for (int c = 0; c < ColName.Length; c++)
            {
                TASGui.Label(R(cx, y, ColW[c], 20), ColName[c], TASGui.TextMuted, 11, true, TextAnchor.MiddleCenter);
                cx += ColW[c];
            }
            y += 22;

            float top = y;
            float bodyH = _visibleRows * ROW_H;
            var body = R(TABLE_X, top, tw, bodyH);
            TASGui.Fill(body, TASGui.BgInset);

            if (!Macro.HasRecordedData)
            {
                TASGui.Label(body, $"No macro. Record one ({TASConfig.Settings.RecordMacro}) or load a file below.",
                             TASGui.TextMuted, 13, false, TextAnchor.MiddleCenter);
            }
            else
            {
                HandleWheel(body);
                ClampScroll();
                ulong cur = _controller.CurrentTick;
                for (int i = 0; i < _visibleRows; i++)
                {
                    long tick = _topTick + i;
                    if (tick > (long)Macro.MaxTick) break;
                    DrawRow((ulong)tick, cur, top + i * ROW_H, i);
                }
                DrawScrollbar(R(TABLE_X + tw + 2, top, SCROLL_W, bodyH));
            }
            y = top + bodyH + 10;
        }

        private void DrawRow(ulong tick, ulong curTick, float y, int index)
        {
            float tw = TableWidth();
            var rowRect = R(TABLE_X, y, tw, ROW_H);

            bool selected = HasSelection && (long)tick >= SelFrom && (long)tick <= SelTo;
            bool current = tick == curTick;
            // Quiet rows: zebra striping, a soft tint for the selection, and the playhead
            // marked by a tint plus a thin bar instead of a solid block.
            if (index % 2 == 0) TASGui.Fill(rowRect, new Color(1f, 1f, 1f, 0.018f));
            if (selected) TASGui.Fill(rowRect, TASGui.Tint(TASGui.Accent, 0.09f));
            if (current) TASGui.Fill(rowRect, TASGui.Tint(TASGui.Accent, 0.16f));
            if (current || (long)tick == _cursor)
                TASGui.Fill(new Rect(rowRect.x, rowRect.y, 2, rowRect.height), TASGui.Tint(TASGui.Accent, current ? 0.9f : 0.4f));
            if (TASGui.Hover(rowRect)) TASGui.Fill(rowRect, new Color(1f, 1f, 1f, 0.03f));

            float cx = TABLE_X;

            // Tick: click = select + go there; Shift+click = extend the selection.
            var tickRect = R(cx + 3, y + 2, ColW[0] - 6, ROW_H - 4);
            bool tickHover = TASGui.Hover(tickRect);
            TASGui.Label(tickRect, tick.ToString(), tickHover ? TASGui.Accent : current ? TASGui.Text : TASGui.TextDim,
                         12, current, TextAnchor.MiddleCenter);
            if (TASGui.Clicked(tickRect)) OnTickClicked(tick);
            cx += ColW[0];

            var st = Macro.GetStateAtTick(tick);
            if (st == null)
            {
                TASGui.Label(R(cx, y, 200, ROW_H), "— no data —", TASGui.TextMuted, 12, false, TextAnchor.MiddleCenter);
                return;
            }
            var s = st.Value;

            Color zero = TASGui.TextMuted;
            string keys = KeysGlyph(s.MoveXRaw, s.MoveYRaw);
            TASGui.Label(R(cx, y, ColW[1], ROW_H), keys, keys == "·" ? zero : TASGui.Text, 12, false, TextAnchor.MiddleCenter);
            cx += ColW[1];
            TASGui.Label(R(cx, y, ColW[2], ROW_H), s.MoveXRaw.ToString(), s.MoveXRaw != 0 ? TASGui.TextDim : zero, 12, false, TextAnchor.MiddleCenter);
            cx += ColW[2];
            TASGui.Label(R(cx, y, ColW[3], ROW_H), s.MoveYRaw.ToString(), s.MoveYRaw != 0 ? TASGui.TextDim : zero, 12, false, TextAnchor.MiddleCenter);
            cx += ColW[3];

            // Jump / Interact cells: click = fork here with that button toggled.
            if (ButtonCell(R(cx + 6, y + 2, ColW[4] - 12, ROW_H - 4), "J", s.Jump)) ForkToggling(tick, toggleJump: true);
            cx += ColW[4];
            if (ButtonCell(R(cx + 6, y + 2, ColW[5] - 12, ROW_H - 4), "I", s.Interact)) ForkToggling(tick, toggleJump: false);
            cx += ColW[5];

            TASGui.Label(R(cx, y, ColW[6], ROW_H), s.CameraPan.ToString("F1", Inv), TASGui.TextDim, 12, false, TextAnchor.MiddleCenter);
            cx += ColW[6];
            TASGui.Label(R(cx, y, ColW[7], ROW_H), s.CameraTilt.ToString("F2", Inv), TASGui.TextDim, 12, false, TextAnchor.MiddleCenter);
            cx += ColW[7];
            TASGui.Label(R(cx, y, ColW[8], ROW_H), s.PlayerVelocity.magnitude.ToString("F2", Inv), TASGui.TextDim, 12, false, TextAnchor.MiddleCenter);
            cx += ColW[8];
            TASGui.Label(R(cx, y, ColW[9], ROW_H), s.PlayerPosition.y.ToString("F2", Inv), TASGui.TextDim, 12, false, TextAnchor.MiddleCenter);
        }

        private static bool ButtonCell(Rect r, string label, bool on)
        {
            bool hover = TASGui.Hover(r);
            // A small tinted pill when pressed, not a solid block: long held-button runs
            // otherwise turn the table into a wall of colour.
            var pill = new Rect(r.center.x - 11, r.y + 2, 22, r.height - 4);
            if (on) TASGui.Fill(pill, TASGui.Tint(TASGui.Ok, hover ? 0.30f : 0.18f));
            else if (hover) TASGui.Fill(pill, new Color(1f, 1f, 1f, 0.06f));
            TASGui.Label(pill, on ? label : "·", on ? TASGui.Ok : TASGui.TextMuted,
                         11, on, TextAnchor.MiddleCenter);
            return TASGui.Clicked(r);
        }

        private static string KeysGlyph(sbyte x, sbyte y)
        {
            string s = (y > 16 ? "W" : "") + (x < -16 ? "A" : "") + (y < -16 ? "S" : "") + (x > 16 ? "D" : "");
            return s.Length == 0 ? "·" : s;
        }

        private void HandleWheel(Rect body)
        {
            if (!TASGui.IsRepaint || !TASGui.Interactive) return;
            float wheel = Input.mouseScrollDelta.y;
            if (wheel == 0f || !body.Contains(TASGui.Mouse)) return;
            _topTick += wheel < 0 ? 3 : -3;
            _follow = false;
        }

        private void DrawScrollbar(Rect track)
        {
            TASGui.Fill(track, TASGui.BgRaised);
            long first = (long)Macro.FirstTick, total = (long)Macro.MaxTick - first + 1;
            if (total <= _visibleRows) return;

            float thumbH = Mathf.Max(24f, track.height * _visibleRows / total);
            long maxTop = MaxTop();
            float t = maxTop > first ? (float)(_topTick - first) / (maxTop - first) : 0f;
            var thumb = new Rect(track.x + 2, track.y + t * (track.height - thumbH), track.width - 4, thumbH);

            if (TASGui.IsRepaint && TASGui.Interactive)
            {
                Vector2 m = TASGui.Mouse;
                if (!_draggingThumb && Input.GetMouseButtonDown(0) && track.Contains(m))
                {
                    TASGui.Clicked(track);   // consume
                    _draggingThumb = true;
                    _thumbGrabOffset = thumb.Contains(m) ? m.y - thumb.y : thumbH / 2f;
                    _follow = false;
                }
                if (_draggingThumb)
                {
                    if (!Input.GetMouseButton(0)) _draggingThumb = false;
                    else
                    {
                        float f = Mathf.Clamp01((m.y - _thumbGrabOffset - track.y) / (track.height - thumbH));
                        _topTick = first + (long)Mathf.Round(f * (maxTop - first));
                    }
                }
            }

            bool hot = _draggingThumb || TASGui.Hover(thumb);
            TASGui.Fill(thumb, hot ? TASGui.Tint(TASGui.Accent, 0.6f) : new Color(1f, 1f, 1f, 0.12f));
        }

        // ── Pins + camera aim ────────────────────────────────────────────
        private void DrawPinsAndAim(ref float y)
        {
            float x = 12f;
            float leftW = 420f;
            TASGui.SectionTitle(R(x, y, leftW, 18), "INPUT PINS — recorded on every frame-advance in Edit Mode");
            float rx = x + leftW + 16f, rightW = _win.Rect.width - rx - 12f;
            TASGui.SectionTitle(R(rx, y, rightW, 18), "CAMERA AIM");
            y += 24;

            var pins = _controller.Pins;
            var p = pins;
            float bx = x;
            p.MoveY = PinToggle(R(bx, y, 34, 26), "W", p.MoveY > 0) ? (sbyte)(p.MoveY > 0 ? 0 : 127) : p.MoveY; bx += 38;
            p.MoveX = PinToggle(R(bx, y, 34, 26), "A", p.MoveX < 0) ? (sbyte)(p.MoveX < 0 ? 0 : -127) : p.MoveX; bx += 38;
            p.MoveY = PinToggle(R(bx, y, 34, 26), "S", p.MoveY < 0) ? (sbyte)(p.MoveY < 0 ? 0 : -127) : p.MoveY; bx += 38;
            p.MoveX = PinToggle(R(bx, y, 34, 26), "D", p.MoveX > 0) ? (sbyte)(p.MoveX > 0 ? 0 : 127) : p.MoveX; bx += 46;
            if (PinToggle(R(bx, y, 62, 26), "JUMP", p.Jump)) p.Jump = !p.Jump; bx += 66;
            if (PinToggle(R(bx, y, 50, 26), "INT", p.Interact)) p.Interact = !p.Interact; bx += 58;
            if (TASGui.Button(R(bx, y, 56, 26), "Clear", p.Active)) p = default; bx += 60;
            if (TASGui.Button(R(bx, y, 60, 26), "From row", HasSelection && Macro.GetStateAtTick((ulong)_cursor) != null))
                p = InputPins.FromState(Macro.GetStateAtTick((ulong)_cursor).Value);

            // Camera aim fields (same row)
            TASGui.Label(R(rx, y, 32, 26), "Pan", TASGui.TextDim, 12);
            _aimPan = TASGui.TextField(R(rx + 32, y + 1, 70, 24), "aim-pan", _aimPan, 12);
            TASGui.Label(R(rx + 110, y, 32, 26), "Tilt", TASGui.TextDim, 12);
            _aimTilt = TASGui.TextField(R(rx + 142, y + 1, 70, 24), "aim-tilt", _aimTilt, 12);
            y += 32;

            // Analog move (-1..1), for gamepad-like partial inputs
            TASGui.Label(R(x, y, 70, 24), "Analog X", TASGui.TextDim, 12);
            _pinX = TASGui.TextField(R(x + 66, y, 60, 24), "pin-x", _pinX, 8);
            TASGui.Label(R(x + 136, y, 20, 24), "Y", TASGui.TextDim, 12);
            _pinY = TASGui.TextField(R(x + 152, y, 60, 24), "pin-y", _pinY, 8);
            if (TASGui.Button(R(x + 220, y, 50, 24), "Set"))
            {
                if (TryParseFloat(_pinX, out float ax) && TryParseFloat(_pinY, out float ay))
                {
                    p.MoveX = TASInputState.QuantizeAxis(ax);
                    p.MoveY = TASInputState.QuantizeAxis(ay);
                }
                else SetStatus("Analog values must be numbers between -1 and 1.", TASGui.Danger);
            }
            string pinHint = !Macro.IsRecording ? "Applies once you're in Edit Mode"
                           : p.Active ? $"Next [{TASConfig.Settings.FrameAdvance}] records this" : "Nothing pinned — live keys";
            TASGui.Label(R(x + 280, y, leftW - 280, 24), pinHint, p.Active ? TASGui.Edit : TASGui.TextMuted, 12);

            if (TASGui.Button(R(rx, y, 140, 24), "Aim from cursor", HasSelection)) ApplyAim();
            if (TASGui.Button(R(rx + 146, y, 66, 24), "Reload", HasSelection)) LoadAimFields();
            y += 34;

            if (!SamePins(p, pins))
            {
                _controller.Pins = p;
                _pinX = (p.MoveX / 127f).ToString("0.##", Inv);
                _pinY = (p.MoveY / 127f).ToString("0.##", Inv);
            }
        }

        private static bool PinToggle(Rect r, string label, bool on)
            => TASGui.Button(r, label, true, on, TASGui.Edit, 12);

        private static bool SamePins(InputPins a, InputPins b)
            => a.MoveX == b.MoveX && a.MoveY == b.MoveY && a.Jump == b.Jump && a.Interact == b.Interact;

        // ── Files ────────────────────────────────────────────────────────
        private void DrawFiles(ref float y)
        {
            float x = 12f, w = _win.Rect.width - 24f;
            TASGui.SectionTitle(R(x, y, w, 18), "FILES  ·  BepInEx/plugins/FlippingIsHardTAS/Macros");
            y += 24;

            // List (left)
            const int listRows = 4;
            float listW = 300f, itemH = 22f;
            var list = R(x, y, listW, listRows * itemH);
            TASGui.Fill(list, TASGui.BgInset);
            if (_files.Length == 0)
                TASGui.Label(list, "No saved macros yet", TASGui.TextMuted, 12, false, TextAnchor.MiddleCenter);

            if (TASGui.IsRepaint && TASGui.Interactive && list.Contains(TASGui.Mouse))
            {
                float wheel = Input.mouseScrollDelta.y;
                if (wheel != 0f) _fileScroll += wheel < 0 ? 1 : -1;
            }
            _fileScroll = Mathf.Clamp(_fileScroll, 0, Mathf.Max(0, _files.Length - listRows));

            for (int i = 0; i < listRows && _fileScroll + i < _files.Length; i++)
            {
                string name = _files[_fileScroll + i];
                var item = new Rect(list.x, list.y + i * itemH, list.width, itemH);
                bool sel = name == _fileName;
                if (sel) TASGui.Fill(item, TASGui.Tint(TASGui.Accent, 0.14f));
                else if (TASGui.Hover(item)) TASGui.Fill(item, new Color(1f, 1f, 1f, 0.05f));
                TASGui.Label(new Rect(item.x + 8, item.y, item.width - 16, item.height), name + ".tas", sel ? TASGui.Text : TASGui.TextDim, 12);
                if (TASGui.Clicked(item))
                {
                    // Click selects; double-click loads.
                    bool dbl = _lastClickedFile == name && Time.unscaledTime - _lastFileClickTime < 0.4f;
                    _fileName = name;
                    _lastClickedFile = name;
                    _lastFileClickTime = Time.unscaledTime;
                    if (dbl) LoadFile();
                }
            }

            // Name + actions (right)
            float rx = x + listW + 12f;
            TASGui.Label(R(rx, y, 44, 24), "Name", TASGui.TextDim, 12);
            _fileName = TASGui.TextField(R(rx + 44, y, 200, 24), "fname", _fileName, 40);
            if (TASGui.Button(R(rx + 252, y, 70, 24), "Save", Macro.HasRecordedData, accent: TASGui.Ok)) SaveFile();
            y += 30;
            if (TASGui.Button(R(rx + 44, y, 96, 24), "Load", _fileName.Length > 0)) LoadFile();
            if (TASGui.Button(R(rx + 146, y, 96, 24), "Refresh")) RefreshFiles();
            y += 30;
            TASGui.Label(R(rx, y, w - listW - 12f, 24),
                         "Ctrl+Z/Y · Ctrl+C/V · Del/Ins · ↑↓ PgUp/PgDn · Ctrl+S",
                         TASGui.TextMuted, 12);
            y = list.yMax + 8f;
        }

        private void DrawStatusBar()
        {
            var bar = R(0, _win.Rect.height - 26, _win.Rect.width, 26);
            TASGui.Fill(bar, TASGui.BgRaised);
            if (string.IsNullOrEmpty(_status)) return;
            // Recent messages are bright, older ones fade to dim.
            Color c = Time.unscaledTime - _statusTime < 6f ? _statusColor : TASGui.TextMuted;
            TASGui.Label(new Rect(bar.x + 12, bar.y, bar.width - 24, bar.height), _status, c, 12);
        }

        // ═════════════════════════════════════════════════════════════════
        //  Keyboard shortcuts (called from the controller's Update)
        // ═════════════════════════════════════════════════════════════════

        public void HandleShortcuts()
        {
            if (!_visible || TASGui.IsTyping || TASBindMenuRenderer.IsVisibleGlobally) return;

            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

            if (ctrl)
            {
                if (Input.GetKeyDown(KeyCode.Z)) { if (shift) DoRedo(); else DoUndo(); }
                else if (Input.GetKeyDown(KeyCode.Y)) DoRedo();
                else if (Input.GetKeyDown(KeyCode.C)) CopyFrames();
                else if (Input.GetKeyDown(KeyCode.V)) PasteFrames();
                else if (Input.GetKeyDown(KeyCode.S)) SaveFile();
                return;
            }

            if (!Macro.HasRecordedData) return;
            if (Input.GetKeyDown(KeyCode.Delete)) DeleteFrames();
            else if (Input.GetKeyDown(KeyCode.Insert)) InsertFrames();
            else if (Input.GetKeyDown(KeyCode.UpArrow)) MoveCursor(-1, shift);
            else if (Input.GetKeyDown(KeyCode.DownArrow)) MoveCursor(1, shift);
            else if (Input.GetKeyDown(KeyCode.PageUp)) MoveCursor(-_visibleRows, shift);
            else if (Input.GetKeyDown(KeyCode.PageDown)) MoveCursor(_visibleRows, shift);
            else if (Input.GetKeyDown(KeyCode.Home)) MoveCursor(long.MinValue / 2, shift);
            else if (Input.GetKeyDown(KeyCode.End)) MoveCursor(long.MaxValue / 2, shift);
        }

        private void MoveCursor(long delta, bool extend)
        {
            long first = (long)Macro.FirstTick, last = (long)Macro.MaxTick;
            long from = _cursor >= 0 ? _cursor : (long)_controller.CurrentTick;
            long to = Math.Max(first, Math.Min(last, from + delta));
            _cursor = to;
            if (!extend || _anchor < 0) _anchor = to;
            _follow = false;
            EnsureVisible(to);
            if (!extend) GoTo((ulong)to);
        }

        // ═════════════════════════════════════════════════════════════════
        //  Actions
        // ═════════════════════════════════════════════════════════════════

        private void OnTickClicked(ulong tick)
        {
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            _follow = false;
            if (shift && _anchor >= 0)
            {
                _cursor = (long)tick;
                SetStatus($"Selected {SelTo - SelFrom + 1} frames ({SelFrom}–{SelTo}).", TASGui.TextDim);
                return;
            }
            _anchor = _cursor = (long)tick;
            GoTo(tick);
        }

        private void GoTo(ulong tick)
        {
            LoadAimFields();
            if (!_controller.GoToTick(tick))
                SetStatus($"Can't go to tick {tick}.", TASGui.Danger);
        }

        /// <summary>
        /// J/I cell: changing a button at tick N means re-recording N from N-1's state. Enter
        /// Edit Mode on N-1 with that frame's inputs pinned (the button toggled); the next
        /// frame-advance forks the run and records N live.
        /// </summary>
        private void ForkToggling(ulong tick, bool toggleJump)
        {
            var st = Macro.GetStateAtTick(tick);
            if (st == null) return;
            if (tick == 0 || tick <= Macro.FirstTick || Macro.GetStateAtTick(tick - 1) == null)
            {
                SetStatus("The first frame has no previous state to fork from.", TASGui.Pause);
                return;
            }
            if (!_controller.BeginEditAt(tick - 1))
            {
                SetStatus($"Can't enter Edit Mode at tick {tick - 1}.", TASGui.Danger);
                return;
            }

            var pins = InputPins.FromState(st.Value);
            if (toggleJump) pins.Jump = !pins.Jump;
            else pins.Interact = !pins.Interact;
            _controller.Pins = pins;
            _anchor = _cursor = (long)tick - 1;
            _follow = true;
            SetStatus($"Edit Mode at {tick - 1}, inputs of {tick} pinned with {(toggleJump ? "JUMP" : "INT")} {(toggleJump ? (pins.Jump ? "on" : "off") : (pins.Interact ? "on" : "off"))}. " +
                      $"Press [{TASConfig.Settings.FrameAdvance}] to record it (drops the frames after).", TASGui.Edit);
        }

        private void EditHere()
        {
            if (!HasSelection) return;
            if (_controller.BeginEditAt((ulong)_cursor))
                SetStatus($"Edit Mode at {_cursor}. Frame-advance with keys held or inputs pinned to fork from here.", TASGui.Edit);
            else
                SetStatus($"Can't enter Edit Mode at {_cursor}.", TASGui.Danger);
        }

        private void TrimAfter()
        {
            if (!HasSelection) return;
            ulong at = (ulong)_cursor;
            if (at >= Macro.MaxTick) { SetStatus("Nothing after the cursor.", TASGui.TextDim); return; }
            BeginMutation();
            ulong dropped = Macro.MaxTick - at;
            Macro.TruncateAt(at + 1);
            AfterMutation();
            SetStatus($"Trimmed {dropped} frames after {at}.", TASGui.Ok);
        }

        private void NewMacro()
        {
            if (IsDirty && !Confirm("new", "Unsaved changes — click New again to discard the macro (Undo can still bring it back).")) return;
            BeginMutation();
            _controller.DiscardMacro();
            AfterMutation();
            _anchor = _cursor = -1;
            _currentFile = null;
            _savedVersion = Macro.Version;
            SetStatus("Macro cleared.", TASGui.TextDim);
        }

        private void InsertFrames()
        {
            if (!HasSelection) return;
            int count = (int)Math.Min(SelTo - SelFrom + 1, 100000);
            BeginMutation();
            Macro.InsertTicksAt((ulong)SelFrom, count);
            AfterMutation();
            SetStatus($"Inserted {count} frame(s) at {SelFrom} (copies of the frame before) — later frames +{count}.", TASGui.Ok);
        }

        private void DeleteFrames()
        {
            if (!HasSelection) return;
            long from = SelFrom, to = SelTo;
            BeginMutation();
            Macro.DeleteRange((ulong)from, (ulong)to);
            AfterMutation();
            long n = to - from + 1;
            _anchor = _cursor = Math.Min(from, (long)Macro.MaxTick);
            SetStatus($"Deleted {n} frame(s) {from}–{to} — later frames −{n}.", TASGui.Ok);
        }

        private void CopyFrames()
        {
            if (!HasSelection) return;
            _clipboard.Clear();
            for (long t = SelFrom; t <= SelTo; t++)
            {
                var st = Macro.GetStateAtTick((ulong)t);
                if (st != null) _clipboard.Add(st.Value);
            }
            SetStatus($"Copied {_clipboard.Count} frame(s) {SelFrom}–{SelTo}.", TASGui.TextDim);
        }

        /// <summary>Full-state paste: the pasted segment plays back exactly (seam at the edges).</summary>
        private void PasteFrames()
        {
            if (_clipboard.Count == 0) { SetStatus("Clipboard is empty — Copy first.", TASGui.TextDim); return; }
            if (!HasSelection) return;
            ulong at = (ulong)_cursor;
            BeginMutation();
            Macro.PasteStates(at, _clipboard);
            AfterMutation();
            SetStatus($"Pasted {_clipboard.Count} frame(s) over {at}–{at + (ulong)_clipboard.Count - 1}.", TASGui.Ok);
        }

        private void ApplyAim()
        {
            if (!HasSelection) return;
            if (!TryParseFloat(_aimPan, out float pan) || !TryParseFloat(_aimTilt, out float tilt))
            {
                SetStatus("Pan and Tilt must be numbers.", TASGui.Danger);
                return;
            }
            BeginMutation();
            Macro.SetCameraFrom((ulong)_cursor, pan, tilt);
            AfterMutation();
            SetStatus($"Camera aimed at {pan:F1} / {tilt:F2} from tick {_cursor} onward.", TASGui.Ok);
        }

        private void LoadAimFields()
        {
            if (!HasSelection) return;
            var st = Macro.GetStateAtTick((ulong)_cursor);
            if (st == null) return;
            // Invariant culture: Spanish Windows would format "12,34", which reads back wrong.
            _aimPan = st.Value.CameraPan.ToString("F2", Inv);
            _aimTilt = st.Value.CameraTilt.ToString("F3", Inv);
        }

        // ── Files ────────────────────────────────────────────────────────
        private string PathFor(string name) => Path.Combine(MacroDir, name + ".tas");

        private static bool ValidFileName(string name)
            => !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

        private void RefreshFiles()
        {
            try
            {
                if (!Directory.Exists(MacroDir)) { _files = Array.Empty<string>(); return; }
                var infos = new DirectoryInfo(MacroDir).GetFiles("*.tas");
                Array.Sort(infos, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                _files = Array.ConvertAll(infos, f => Path.GetFileNameWithoutExtension(f.Name));
            }
            catch (Exception ex)
            {
                _files = Array.Empty<string>();
                SetStatus($"Can't list macros: {ex.Message}", TASGui.Danger);
            }
        }

        private void SaveFile()
        {
            if (!Macro.HasRecordedData) { SetStatus("No macro to save.", TASGui.TextDim); return; }
            string name = _fileName.Trim();
            if (!ValidFileName(name)) { SetStatus("Invalid file name.", TASGui.Danger); return; }
            string path = PathFor(name);
            // Re-saving the file you're working on is silent; clobbering another one asks first.
            if (File.Exists(path) && name != _currentFile
                && !Confirm("save:" + name, $"{name}.tas already exists — click Save again to overwrite it.")) return;

            try
            {
                Macro.ExportMacro(path);
                _fileName = name;
                _currentFile = name;
                _savedVersion = Macro.Version;
                RefreshFiles();
                SetStatus($"Saved {name}.tas — {Macro.RecordedInputs.Count} ticks.", TASGui.Ok);
                TASGui.Toast($"Saved {name}.tas", TASGui.Ok);
            }
            catch (Exception ex)
            {
                SetStatus($"Save failed: {ex.Message}", TASGui.Danger);
                TASPlugin.Logger.LogError($"TAS: saving {path} failed: {ex}");
            }
        }

        private void LoadFile()
        {
            string name = _fileName.Trim();
            if (!ValidFileName(name)) { SetStatus("Invalid file name.", TASGui.Danger); return; }
            if (IsDirty && !Confirm("load:" + name, "Unsaved changes — click Load again to replace them (Undo can bring them back).")) return;

            _controller.StopPlaybackFromEditor();
            var before = TakeSnapshot();
            if (!Macro.ImportMacro(PathFor(name), out string error))
            {
                SetStatus($"Can't load {name}.tas: {error}.", TASGui.Danger);
                return;
            }
            PushUndo(before);
            _controller.OnMacroReplaced();
            _currentFile = name;
            _savedVersion = Macro.Version;
            _anchor = _cursor = (long)Macro.FirstTick;
            _topTick = (long)Macro.FirstTick;
            LoadAimFields();
            SetStatus($"Loaded {name}.tas — {Macro.RecordedInputs.Count} ticks ({Macro.FirstTick}–{Macro.MaxTick}).", TASGui.Ok);
        }

        // ═════════════════════════════════════════════════════════════════
        //  Undo / redo
        // ═════════════════════════════════════════════════════════════════

        private Snapshot TakeSnapshot() => new Snapshot
        {
            Rows = new Dictionary<ulong, TASInputState>(Macro.RecordedInputs),
            MaxTick = Macro.MaxTick,
            Seed = Macro.RNGSeed,
        };

        private void Restore(Snapshot s)
            => Macro.ReplaceAll(new Dictionary<ulong, TASInputState>(s.Rows), s.MaxTick, s.Seed);

        private void PushUndo(Snapshot s)
        {
            _undo.Add(s);
            if (_undo.Count > UNDO_CAP) _undo.RemoveAt(0);
            _redo.Clear();
        }

        /// <summary>Call before changing the run: one-time file backup + undo snapshot.</summary>
        private void BeginMutation()
        {
            if (!_backupDone && Macro.HasRecordedData)
            {
                try
                {
                    Macro.ExportMacro(PathFor("editor_backup"));
                    _backupDone = true;
                }
                catch (Exception ex)
                {
                    TASPlugin.Logger.LogError($"TAS: editor backup failed: {ex}");
                }
            }
            PushUndo(TakeSnapshot());
        }

        private void AfterMutation()
        {
            _controller.OnMacroEdited();
            ClampSelection();
        }

        private void DoUndo()
        {
            if (_undo.Count == 0) { SetStatus("Nothing to undo.", TASGui.TextDim); return; }
            _redo.Add(TakeSnapshot());
            var s = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            Restore(s);
            AfterMutation();
            SetStatus($"Undo — {Macro.RecordedInputs.Count} ticks.", TASGui.TextDim);
        }

        private void DoRedo()
        {
            if (_redo.Count == 0) { SetStatus("Nothing to redo.", TASGui.TextDim); return; }
            _undo.Add(TakeSnapshot());
            var s = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            Restore(s);
            AfterMutation();
            SetStatus($"Redo — {Macro.RecordedInputs.Count} ticks.", TASGui.TextDim);
        }

        // ═════════════════════════════════════════════════════════════════
        //  Helpers
        // ═════════════════════════════════════════════════════════════════

        private bool Confirm(string key, string prompt)
        {
            if (_confirmKey == key && Time.unscaledTime < _confirmUntil)
            {
                _confirmKey = null;
                return true;
            }
            _confirmKey = key;
            _confirmUntil = Time.unscaledTime + 4f;
            SetStatus(prompt, TASGui.Pause);
            return false;
        }

        private ulong Clamp(ulong tick)
        {
            if (!Macro.HasRecordedData) return 0;
            if (tick < Macro.FirstTick) return Macro.FirstTick;
            return tick > Macro.MaxTick ? Macro.MaxTick : tick;
        }

        private void ClampSelection()
        {
            if (!Macro.HasRecordedData) { _anchor = _cursor = -1; return; }
            long first = (long)Macro.FirstTick, last = (long)Macro.MaxTick;
            if (_anchor >= 0) _anchor = Math.Max(first, Math.Min(last, _anchor));
            if (_cursor >= 0) _cursor = Math.Max(first, Math.Min(last, _cursor));
        }

        private long MaxTop() => Math.Max((long)Macro.FirstTick, (long)Macro.MaxTick - _visibleRows + 1);

        private void ClampScroll()
        {
            long first = (long)Macro.FirstTick;
            _topTick = Math.Max(first, Math.Min(MaxTop(), _topTick));
        }

        private void CenterOn(ulong tick)
        {
            _topTick = (long)tick - _visibleRows / 2;
            if (Macro.HasRecordedData) ClampScroll();
        }

        private void EnsureVisible(long tick)
        {
            if (tick < _topTick) _topTick = tick;
            else if (tick >= _topTick + _visibleRows) _topTick = tick - _visibleRows + 1;
            ClampScroll();
        }

        private static bool TryParseFloat(string str, out float value)
        {
            // Accept "12.5" and "12,5" (comma decimal separator on Spanish locales).
            str = (str ?? "").Trim().Replace(',', '.');
            return float.TryParse(str, NumberStyles.Float, Inv, out value)
                   && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private void SetStatus(string msg, Color color)
        {
            _status = msg;
            _statusColor = color;
            _statusTime = Time.unscaledTime;
        }
    }
}
