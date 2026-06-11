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
        private bool _isFastForward = false;
        private ulong _currentTick = 0;
        private bool _showOverlay = true;

        // Coordinate caching
        private Vector3? _cachedCoords = null;
        private string _cachedHeightStr = "HEIGHT: 0.0 M";
        private string _cachedCoordsStr = "XYZ: 0.0, 0.0, 0.0";
        private float _lastSpeed = -1f;
        private string _cachedSpeedStr = "SPEED: 0.0 M/S";

        // Base layout constants (scaled by OverlayScale at draw time)
        private const float BASE_CTRL_W = 420f;
        private const float BASE_COORD_W = 240f;
        private const float BASE_COORD_H = 92f;
        private const float BASE_PAD = 20f;
        private const float BASE_LINE_H = 22f;
        private const float BASE_SECTION_H = 20f;

        // Colors
        private readonly Color _bgColor     = new Color(0.06f, 0.06f, 0.06f, 0.88f);
        private readonly Color _borderColor = new Color(0.35f, 0.6f,  1.0f,  1.0f);
        private readonly Color _headerColor = new Color(0.0f,  0.85f, 1.0f,  1.0f);
        private readonly Color _savedColor  = new Color(0.2f,  1.0f,  0.4f,  1.0f);
        private readonly Color _recColor    = new Color(1.0f,  0.25f, 0.25f, 1.0f);
        private readonly Color _playColor   = new Color(0.0f,  1.0f,  1.0f,  1.0f);
        private readonly Color _pauseColor  = new Color(1.0f,  0.75f, 0.2f,  1.0f);
        private readonly Color _dimColor    = new Color(0.65f, 0.65f, 0.65f, 1.0f);
        private readonly Color _keyColor    = new Color(1.0f,  0.85f, 0.4f,  1.0f);
        private readonly Color _sectionColor= new Color(0.5f,  0.75f, 1.0f,  1.0f);

        // Styles (recreated when scale changes)
        private GUIStyle _styleHeader;
        private GUIStyle _styleText;
        private GUIStyle _styleKey;
        private GUIStyle _styleSection;
        private float _cachedScale = -1f;

        private ulong _greenzoneEnd = 0;
        private ulong _recordedCount = 0;   // number of valid recorded ticks (no-data excluded)

        public void UpdateData(Vector3 pos, float speed, bool hasSaved, bool isRecording,
                               bool isPlaying, bool isPaused, ulong currentTick,
                               bool isSlowMo = false, bool isSlowMoBoost = false, bool isFastForward = false, bool isEditMode = false,
                               ulong greenzoneEnd = 0, ulong recordedCount = 0)
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
            _isFastForward    = isFastForward;
            _currentTick      = currentTick;
            _greenzoneEnd     = greenzoneEnd;
            _recordedCount    = recordedCount;
            _showOverlay      = Application.isFocused;
        }

        public void RefreshKeybinds() { }

        private float Scale => Mathf.Clamp(TASConfig.Settings.OverlayScale, 0.25f, 2.0f);

        public void OnGUI()
        {
            if (!_showOverlay) return;
            EnsureStyles();
            DrawControls();
            DrawCoords();
        }

        private void EnsureStyles()
        {
            float scale = Scale;
            if (Mathf.Approximately(_cachedScale, scale)) return;
            _cachedScale = scale;

            int headerSize = Mathf.RoundToInt(22 * scale);
            int textSize   = Mathf.RoundToInt(18 * scale);
            int keySize    = Mathf.RoundToInt(17 * scale);
            int sectionSize= Mathf.RoundToInt(13 * scale);

            _styleHeader = new GUIStyle
            {
                fontSize = headerSize,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft
            };
            _styleHeader.normal.textColor = _headerColor;

            _styleText = new GUIStyle
            {
                fontSize = textSize,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft
            };
            _styleText.normal.textColor = Color.white;

            _styleKey = new GUIStyle
            {
                fontSize = keySize,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft
            };
            _styleKey.normal.textColor = _keyColor;

            _styleSection = new GUIStyle
            {
                fontSize = sectionSize,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft
            };
            _styleSection.normal.textColor = _sectionColor;
        }

        private void DrawControls()
        {
            var s = TASConfig.Settings;
            float sc = Scale;
            float lineH = BASE_LINE_H * sc;
            float sectionH = BASE_SECTION_H * sc;
            float ctrlW = BASE_CTRL_W * sc;
            float pad = BASE_PAD;
            float totalH = 35f * sc  // header + tick + state
                         + lineH * 2             // Recorded + Reset Tick (under the tick counter)
                         + sectionH + lineH * 2  // savestate section
                         + sectionH + lineH * 3  // macro section (Record, Play, Edit)
                         + sectionH + lineH * 5  // playback section (Pause, FrameAdv, Rewind, FastFwd, SlowMo)
                         + sectionH + lineH * 2   // ui section
                         + 14f * sc;              // padding

            float x = pad;
            float y = Screen.height - totalH - pad;

            DrawBox(x, y, ctrlW, totalH);

            float cx = x + 12f * sc;
            float cy = y + 10f * sc;

            // ── Header ──
            _styleHeader.normal.textColor = _headerColor;
            GUI.Label(new Rect(cx, cy, ctrlW - 20f * sc, 26f * sc), "  Flipping is Hard TAS", _styleHeader);
            cy += 30f * sc;

            // Tick + State
            _styleText.normal.textColor = _savedColor;
            GUI.Label(new Rect(cx, cy, 190f * sc, lineH), $"  TICK: {_currentTick}", _styleText);

            string stateStr;
            Color stateColor;
            if (_isEditMode)
            {
                stateStr = "✎ EDIT (REC)"; stateColor = new Color(1f, 0.5f, 0f, 1f);
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
            else if (_isPlaying && _isFastForward)
            {
                stateStr = "⏩ REPLAY ×3"; stateColor = new Color(0.2f, 0.8f, 1f, 1f);
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
            GUI.Label(new Rect(cx + 180f * sc, cy, ctrlW - 200f * sc, 20f * sc), stateStr, _styleText);
            cy += lineH;

            // Recorded + Reset Tick: right under the tick counter (per user request)
            if (_recordedCount > 0)
                DrawKeySingle(cx, ref cy, $"Recorded: {_recordedCount} ticks", _savedColor, ctrlW, lineH);
            else
                DrawKeySingle(cx, ref cy, "Recorded: —", _dimColor, ctrlW, lineH);
            DrawKeySingle(cx, ref cy, $"[{s.ResetTick}] Reset Tick", _dimColor, ctrlW, lineH);

            // ── Savestate ──
            DrawSectionLabel(cx, ref cy, "SAVESTATE", ctrlW, lineH);
            DrawKeyRow(cx, ref cy, $"[{s.SavePosition}] Save", $"[{s.Teleport}] Load",
                _hasSavedPosition ? _savedColor : _dimColor, ctrlW, lineH);

            // ── Macro ──
            DrawSectionLabel(cx, ref cy, "MACRO", ctrlW, lineH);
            DrawKeySingle(cx, ref cy, $"[{s.RecordMacro}] Record",
                _isRecording ? _recColor : (_isPlaying ? new Color(0.6f, 0.2f, 0.2f) : _dimColor), ctrlW, lineH);
            DrawKeySingle(cx, ref cy, $"[{s.PlayMacro}] Play / Stop",
                _isEditMode || _isRecording ? new Color(0.6f, 0.2f, 0.2f) : (_isPlaying ? _playColor : _dimColor), ctrlW, lineH);
            DrawKeySingle(cx, ref cy, $"[{s.EditMacro}] Edit Macro",
                _isEditMode ? new Color(1f, 0.5f, 0f) : _dimColor, ctrlW, lineH);

            // ── Playback Controls ──
            DrawSectionLabel(cx, ref cy, "PLAYBACK CONTROLS", ctrlW, lineH);
            DrawKeySingle(cx, ref cy, $"[{s.Pause}] Pause / Resume",
                _isPaused ? _pauseColor : _dimColor, ctrlW, lineH);
            DrawKeySingle(cx, ref cy, $"[{s.FrameAdvance}] Frame Advance  (hold = 10/s)",
                _isPaused ? _keyColor : _dimColor, ctrlW, lineH);
            DrawKeySingle(cx, ref cy, $"[{s.RewindTick}] Rewind Tick  (hold = 10/s)",
                _isEditMode ? new Color(0.6f, 0.2f, 0.2f) : (_isPaused ? _keyColor : _dimColor), ctrlW, lineH);
            DrawKeySingle(cx, ref cy, $"[{s.FastForward}] Fast Forward ×3",
                _isFastForward ? new Color(0.2f, 0.8f, 1f) : _dimColor, ctrlW, lineH);
            DrawKeySingle(cx, ref cy, $"[{s.SlowMo}] Slow Motion (×0.1)",
                _isSlowMo ? _playColor : _dimColor, ctrlW, lineH);
            if (_isSlowMo)
            {
                Color boostCol = _isSlowMoBoost ? new Color(1f, 0.55f, 0.1f) : _dimColor;
                DrawKeySingle(cx, ref cy, $"  └ [{s.SlowMoBoost}] Boost (×0.3)", boostCol, ctrlW, lineH);
            }

            // ── UI ──
            DrawSectionLabel(cx, ref cy, "INTERFACE", ctrlW, lineH);
            DrawKeySingle(cx, ref cy, $"[{s.OpenBindMenu}] Open Settings", _dimColor, ctrlW, lineH);
            DrawKeySingle(cx, ref cy, $"[{s.OpenEditor}] Open TAS Editor", _dimColor, ctrlW, lineH);
        }

        private void DrawSectionLabel(float x, ref float y, string title, float ctrlW, float lineH)
        {
            GUI.color = _sectionColor;
            GUI.Label(new Rect(x + 2f, y, ctrlW - 20f, 18f * Scale), $"— {title} —", _styleSection);
            GUI.color = Color.white;
            y += sectionSpacing;
        }

        private float sectionSpacing => 19f * Scale;

        private void DrawKeyRow(float x, ref float y, string left, string right, Color col, float ctrlW, float lineH)
        {
            _styleKey.normal.textColor = col;
            float halfW = (ctrlW - 20f) / 2f;
            GUI.Label(new Rect(x + 4f, y, halfW, 20f * Scale), left, _styleKey);
            GUI.Label(new Rect(x + 4f + halfW, y, halfW, 20f * Scale), right, _styleKey);
            GUI.color = Color.white;
            y += lineH;
        }

        private void DrawKeySingle(float x, ref float y, string text, Color col, float ctrlW, float lineH)
        {
            _styleKey.normal.textColor = col;
            GUI.Label(new Rect(x + 4f, y, ctrlW - 20f, 20f * Scale), text, _styleKey);
            GUI.color = Color.white;
            y += lineH;
        }

        private void DrawCoords()
        {
            float sc = Scale;
            float coordW = BASE_COORD_W * sc;
            float coordH = BASE_COORD_H * sc;
            float pad = BASE_PAD;
            float x = Screen.width - coordW - pad;
            float y = pad;

            DrawBox(x, y, coordW, coordH);

            float cx = x + 12f * sc;
            float cy = y + 10f * sc;
            float labelH = 24f * sc;

            _styleText.normal.textColor = Color.white;
            GUI.Label(new Rect(cx, cy, coordW - 24f * sc, labelH), _cachedSpeedStr, _styleText);
            cy += labelH;
            GUI.Label(new Rect(cx, cy, coordW - 24f * sc, labelH), _cachedHeightStr, _styleText);
            cy += labelH;
            GUI.Label(new Rect(cx, cy, coordW - 24f * sc, labelH), _cachedCoordsStr, _styleText);
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
