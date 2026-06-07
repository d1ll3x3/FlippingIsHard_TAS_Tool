using UnityEngine;

namespace FlippingIsHardTAS
{
    public class OverlayRenderer
    {
        // Overlay data
        private Vector3 _currentPosition = Vector3.zero;
        private bool _hasSavedPosition = false;
        private bool _isRecording = false;
        private bool _isPlaying = false;
        private bool _isPaused = false;
        private bool _isSlowMo = false;
        private bool _isSlowMoBoost = false;
        private bool _isEditMode = false;
        private ulong _currentTick = 0;
        private bool _showOverlay = true;

        // Coordinate caching
        private Vector3? _cachedCoords = null;
        private string _cachedHeightStr = "HEIGHT: 0.0 M";
        private string _cachedCoordsStr = "XYZ: 0.0, 0.0, 0.0";
        private float _lastSpeed = -1f;
        private string _cachedSpeedStr = "SPEED: 0.0 M/S";

        // Layout constants
        private const int CTRL_W = 420;
        private const int COORD_W = 240;
        private const int COORD_H = 92;
        private const int PAD = 20;

        // Colors
        private readonly Color _bgColor    = new Color(0.06f, 0.06f, 0.06f, 0.88f);
        private readonly Color _borderColor = new Color(0.35f, 0.6f,  1.0f,  1.0f);
        private readonly Color _headerColor = new Color(0.0f,  0.85f, 1.0f,  1.0f);
        private readonly Color _savedColor  = new Color(0.2f,  1.0f,  0.4f,  1.0f);
        private readonly Color _recColor    = new Color(1.0f,  0.25f, 0.25f, 1.0f);
        private readonly Color _playColor   = new Color(0.0f,  1.0f,  1.0f,  1.0f);
        private readonly Color _pauseColor  = new Color(1.0f,  0.75f, 0.2f,  1.0f);
        private readonly Color _dimColor    = new Color(0.65f, 0.65f, 0.65f, 1.0f);
        private readonly Color _keyColor    = new Color(1.0f,  0.85f, 0.4f,  1.0f);
        private readonly Color _sectionColor= new Color(0.5f,  0.75f, 1.0f,  1.0f);

        // Styles
        private GUIStyle _styleHeader;
        private GUIStyle _styleText;
        private GUIStyle _styleKey;
        private GUIStyle _styleSection;
        private bool _stylesReady = false;

        public void UpdateData(Vector3 pos, float speed, bool hasSaved, bool isRecording,
                               bool isPlaying, bool isPaused, ulong currentTick,
                               bool isSlowMo = false, bool isSlowMoBoost = false, bool isEditMode = false)
        {
            if (_cachedCoords == null || Vector3.Distance(_currentPosition, pos) > 0.05f)
            {
                _currentPosition = pos;
                _cachedHeightStr = $"HEIGHT: {_currentPosition.y:F1} M";
                _cachedCoordsStr = $"XYZ: {_currentPosition.x:F1}, {_currentPosition.y:F1}, {_currentPosition.z:F1}";
                _cachedCoords = pos;
            }

            if (Mathf.Abs(_lastSpeed - speed) > 0.1f)
            {
                _lastSpeed = speed;
                _cachedSpeedStr = $"SPEED: {speed:F1} M/S";
            }

            _hasSavedPosition = hasSaved;
            _isRecording      = isRecording;
            _isPlaying        = isPlaying;
            _isPaused         = isPaused;
            _isSlowMo         = isSlowMo;
            _isSlowMoBoost    = isSlowMoBoost;
            _isEditMode       = isEditMode;
            _currentTick      = currentTick;
            _showOverlay      = Application.isFocused;
        }

        public void RefreshKeybinds() { }

        public void OnGUI()
        {
            if (!_showOverlay) return;
            EnsureStyles();
            DrawControls();
            DrawCoords();
        }

        private void EnsureStyles()
        {
            if (_stylesReady) return;

            _styleHeader = new GUIStyle
            {
                fontSize = 22,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft
            };
            _styleHeader.normal.textColor = _headerColor;

            _styleText = new GUIStyle
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft
            };
            _styleText.normal.textColor = Color.white;

            _styleKey = new GUIStyle
            {
                fontSize = 17,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft
            };
            _styleKey.normal.textColor = _keyColor;

            _styleSection = new GUIStyle
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft
            };
            _styleSection.normal.textColor = _sectionColor;

            _stylesReady = true;
        }

        private void DrawControls()
        {
            var s = TASConfig.Settings;

            // Compute dynamic height
            float lineH = 22f;
            float sectionH = 20f;
            float totalH = 35  // header + tick + state
                         + sectionH + lineH * 2  // savestate section
                         + sectionH + lineH * 3  // macro section (Record, Play, Edit)
                         + sectionH + lineH * 4  // playback section (Pause, FrameAdv, Rewind, SlowMo)
                         + sectionH + lineH       // ui section
                         + 14;                    // padding

            float x = PAD;
            float y = Screen.height - totalH - PAD;

            DrawBox(x, y, CTRL_W, totalH);

            float cx = x + 12;
            float cy = y + 10;

            // ── Header ───────────────────────────────────────────────
            _styleHeader.normal.textColor = _headerColor;
            GUI.Label(new Rect(cx, cy, CTRL_W - 20, 26), "  Flipping is Hard TAS", _styleHeader);
            cy += 30; // extra space below title

            // Tick + State on same row
            _styleText.normal.textColor = _savedColor;
            GUI.Label(new Rect(cx, cy, 190, 22), $"  TICK: {_currentTick}", _styleText);

            string stateStr;
            Color stateColor;
            if (_isEditMode)
            {
                stateStr = "✎ EDIT (REC)"; stateColor = new Color(1f, 0.5f, 0f, 1f); // orange
            }
            else if (_isRecording)
            {
                stateStr = "● REC"; stateColor = _recColor;
            }
            else if (_isPlaying && _isPaused)
            {
                stateStr = "⏸ PAUSED (REPLAY)"; stateColor = _pauseColor;
            }
            else if (_isPlaying && _isSlowMoBoost)
            {
                stateStr = "▶ REPLAY ×0.3"; stateColor = _playColor;
            }
            else if (_isPlaying && _isSlowMo)
            {
                stateStr = "▶ REPLAY ×0.1"; stateColor = _playColor;
            }
            else if (_isPlaying)
            {
                stateStr = "▶ REPLAY"; stateColor = _playColor;
            }
            else if (_isPaused)
            {
                stateStr = "⏸ PAUSED"; stateColor = _pauseColor;
            }
            else if (_isSlowMoBoost)
            {
                stateStr = "▶ SLOW ×0.3"; stateColor = _playColor;
            }
            else if (_isSlowMo)
            {
                stateStr = "▶ SLOW ×0.1"; stateColor = _playColor;
            }
            else
            {
                stateStr = "■ IDLE"; stateColor = _dimColor;
            }

            _styleText.normal.textColor = stateColor;
            GUI.Label(new Rect(cx + 180, cy, CTRL_W - 200, 20), stateStr, _styleText);
            cy += 22;

            // ── Savestate ─────────────────────────────────────────────
            DrawSectionLabel(cx, ref cy, "SAVESTATE");
            DrawKeyRow(cx, ref cy,
                $"[{s.SavePosition}] Save",
                $"[{s.Teleport}] Load",
                _hasSavedPosition ? _savedColor : _dimColor);

            // ── Macro ─────────────────────────────────────────────────
            DrawSectionLabel(cx, ref cy, "MACRO");
            DrawKeyRow(cx, ref cy,
                $"[{s.RecordMacro}] Record",
                $"[{s.PlayMacro}] Play / Stop",
                _isRecording ? _recColor : (_isPlaying ? _playColor : Color.white));
            DrawKeySingle(cx, ref cy, $"[{s.EditMacro}] Edit Macro", _isEditMode ? new Color(1f, 0.5f, 0f) : _dimColor);

            // ── Playback Controls ────────────────────────────────────────
            DrawSectionLabel(cx, ref cy, "PLAYBACK CONTROLS");
            DrawKeySingle(cx, ref cy, $"[{s.Pause}] Pause / Resume", _isPaused ? _pauseColor : _dimColor);
            DrawKeySingle(cx, ref cy, $"[{s.FrameAdvance}] Frame Advance  (hold = 10/s)", _isPaused ? _keyColor : _dimColor);
            DrawKeySingle(cx, ref cy, $"[{s.RewindTick}] Rewind Tick  (hold = 10/s)", _isPaused ? _keyColor : _dimColor);

            // SlowMo row + optional boost hint below it (shown only when slowmo active)
            DrawKeySingle(cx, ref cy, $"[{s.SlowMo}] Slow Motion (×0.1)", _isSlowMo ? _playColor : _dimColor);
            if (_isSlowMo)
            {
                Color boostCol = _isSlowMoBoost ? new Color(1f, 0.55f, 0.1f) : _dimColor;
                DrawKeySingle(cx, ref cy, $"  └ [{s.SlowMoBoost}] Boost (×0.3)", boostCol);
            }

            // ── UI ────────────────────────────────────────────────────
            DrawSectionLabel(cx, ref cy, "INTERFACE");
            DrawKeySingle(cx, ref cy, $"[{s.OpenBindMenu}] Open Settings", _dimColor);
        }

        private void DrawSectionLabel(float x, ref float y, string title)
        {
            GUI.color = _sectionColor;
            GUI.Label(new Rect(x + 2, y, CTRL_W - 20, 18), $"— {title} —", _styleSection);
            GUI.color = Color.white;
            y += 19;
        }

        private void DrawKeyRow(float x, ref float y, string left, string right, Color col)
        {
            _styleKey.normal.textColor = col;
            GUI.Label(new Rect(x + 4, y, (CTRL_W - 20) / 2, 20), left, _styleKey);
            GUI.Label(new Rect(x + 4 + (CTRL_W - 20) / 2, y, (CTRL_W - 20) / 2, 20), right, _styleKey);
            GUI.color = Color.white;
            y += 22;
        }

        private void DrawKeySingle(float x, ref float y, string text, Color col)
        {
            _styleKey.normal.textColor = col;
            GUI.Label(new Rect(x + 4, y, CTRL_W - 20, 20), text, _styleKey);
            GUI.color = Color.white;
            y += 22;
        }

        private void DrawCoords()
        {
            float x = Screen.width - COORD_W - PAD;
            float y = PAD;

            DrawBox(x, y, COORD_W, COORD_H);

            float cx = x + 12;
            float cy = y + 10;

            _styleText.normal.textColor = Color.white;
            GUI.Label(new Rect(cx, cy, COORD_W - 24, 22), _cachedSpeedStr, _styleText);
            cy += 24;
            GUI.Label(new Rect(cx, cy, COORD_W - 24, 22), _cachedHeightStr, _styleText);
            cy += 24;
            GUI.Label(new Rect(cx, cy, COORD_W - 24, 22), _cachedCoordsStr, _styleText);
        }

        private void DrawBox(float x, float y, float w, float h)
        {
            Color orig = GUI.color;

            GUI.color = _bgColor;
            GUI.Box(new Rect(x, y, w, h), GUIContent.none);

            float b = 2f;
            GUI.color = _borderColor;
            GUI.Box(new Rect(x,         y,         w, b), GUIContent.none);
            GUI.Box(new Rect(x,         y + h - b, w, b), GUIContent.none);
            GUI.Box(new Rect(x,         y,         b, h), GUIContent.none);
            GUI.Box(new Rect(x + w - b, y,         b, h), GUIContent.none);

            GUI.color = orig;
        }
    }
}
