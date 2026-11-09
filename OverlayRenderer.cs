using UnityEngine;

namespace FlippingIsHardTAS
{
    /// <summary>Everything the HUD shows, filled by the controller once per frame.</summary>
    public struct HudState
    {
        public bool Recording, Editing, Playing, Paused, SlowMo, FastForward;
        public float SpeedFactor;
        public ulong Tick, MaxTick;
        public int Rows;
        public bool HasSavestate;
        public ulong SavestateTick;
        public InputPins Pins;
        public Vector3 Position;
        public float Speed;
        public sbyte LiveMoveX, LiveMoveY;
        public bool LiveJump, LiveInteract;
    }

    /// <summary>
    /// In-game HUD: a compact status panel (bottom-left), an optional key list above it,
    /// a position/speed readout (top-right) and the input display while recording.
    /// Non-interactive, so it scales by multiplying sizes (HUD Scale setting).
    /// </summary>
    public class OverlayRenderer
    {
        private HudState _s;
        private bool _show = true;

        // Strings rebuilt only when their value changes (OnGUI runs several times a frame).
        private ulong _tickShown = ulong.MaxValue, _maxShown = ulong.MaxValue;
        private string _tickStr = "", _maxStr = "";
        private Vector3 _posShown = new Vector3(float.NaN, 0, 0);
        private float _speedShown = -1f;
        private string _speedStr = "", _heightStr = "", _xyzStr = "";

        private const float PAD = 16f;

        public void SetState(HudState s)
        {
            _s = s;
            _show = Application.isFocused;

            if (s.Tick != _tickShown) { _tickShown = s.Tick; _tickStr = s.Tick.ToString(); }
            if (s.MaxTick != _maxShown) { _maxShown = s.MaxTick; _maxStr = s.MaxTick.ToString(); }
            if (float.IsNaN(_posShown.x) || (s.Position - _posShown).sqrMagnitude > 0.0025f)
            {
                _posShown = s.Position;
                _heightStr = $"{s.Position.y:F1} m";
                _xyzStr = $"{s.Position.x:F1}, {s.Position.y:F1}, {s.Position.z:F1}";
            }
            if (Mathf.Abs(_speedShown - s.Speed) > 0.05f)
            {
                _speedShown = s.Speed;
                _speedStr = $"{s.Speed:F1} m/s";
            }
        }

        private static float Scale => Mathf.Clamp(TASConfig.Settings.OverlayScale, 0.5f, 2.0f);

        private static int Font(float size, float k) => Mathf.Max(8, Mathf.RoundToInt(size * k));

        public void OnGUI()
        {
            if (!_show) return;
            float k = Scale;
            float statusTop = DrawStatus(k);
            if (TASConfig.Settings.ShowKeyHelp) DrawKeyHelp(k, statusTop);
            DrawReadout(k);
            if (_s.Recording) DrawInputDisplay(k);
        }

        // ── Status panel ────────────────────────────────────────────────
        private void ModeBadge(out string text, out Color color)
        {
            if (_s.Editing)        { text = "EDIT";   color = TASGui.Edit; }
            else if (_s.Recording) { text = "REC";    color = TASGui.Rec; }
            else if (_s.Playing)   { text = "REPLAY"; color = TASGui.Play; }
            else                   { text = "IDLE";   color = TASGui.TextMuted; }
        }

        /// <summary>Draws the bottom-left status panel; returns its top edge.</summary>
        private float DrawStatus(float k)
        {
            float w = 300f * k, h = 84f * k;
            float x = PAD, y = Screen.height - h - PAD;
            var r = new Rect(x, y, w, h);
            TASGui.Panel(r);

            ModeBadge(out string mode, out Color modeCol);
            TASGui.Fill(new Rect(x, y, 2f, h), TASGui.Tint(modeCol, 0.8f));

            float cx = x + 12f * k, cy = y + 9f * k;
            float bh = 20f * k;
            TASGui.Badge(new Rect(cx, cy + 3f * k, 64f * k, bh), mode, modeCol, Font(11, k));
            float bx = cx + 70f * k;
            if (_s.Paused)
            {
                TASGui.Badge(new Rect(bx, cy + 3f * k, 58f * k, bh), "PAUSED", TASGui.Pause, Font(11, k));
                bx += 64f * k;
            }
            if (!Mathf.Approximately(_s.SpeedFactor, 1f))
            {
                string sp = _s.FastForward ? "×3" : _s.SpeedFactor < 0.2f ? "×0.1" : "×0.3";
                TASGui.Badge(new Rect(bx, cy + 3f * k, 40f * k, bh), sp, TASGui.Play, Font(11, k));
            }

            // Tick counter, right-aligned on the first row
            TASGui.Label(new Rect(x + w - 150f * k, cy, 138f * k, 26f * k), _tickStr, TASGui.Text,
                         Font(19, k), false, TextAnchor.MiddleRight);
            cy += 30f * k;

            string runInfo = _s.Rows > 0 ? $"Macro  {_s.Rows} ticks · end {_maxStr}" : "No macro";
            TASGui.Label(new Rect(cx, cy, w * 0.62f, 18f * k), runInfo, _s.Rows > 0 ? TASGui.TextDim : TASGui.TextMuted, Font(12, k));
            string saveInfo = _s.HasSavestate ? $"Save @ {_s.SavestateTick}" : "No save";
            TASGui.Label(new Rect(x + w * 0.6f, cy, w * 0.4f - 12f * k, 18f * k), saveInfo,
                         _s.HasSavestate ? TASGui.Ok : TASGui.TextMuted, Font(12, k), false, TextAnchor.MiddleRight);
            cy += 20f * k;

            var st = TASConfig.Settings;
            TASGui.Label(new Rect(cx, cy, w - 24f * k, 16f * k),
                         $"[{st.ToggleHelp}] keys   [{st.OpenEditor}] editor   [{st.OpenBindMenu}] settings",
                         TASGui.TextMuted, Font(12, k));
            return y;
        }

        // ── Key list ────────────────────────────────────────────────────
        private void DrawKeyHelp(float k, float statusTop)
        {
            var actions = TASConfig.Actions;
            int sections = 0;
            string last = null;
            foreach (var a in actions) if (a.Section != last) { sections++; last = a.Section; }

            float lineH = 18f * k, secH = 22f * k;
            float w = 300f * k;
            float h = 10f * k + sections * secH + actions.Length * lineH + 6f * k;
            float x = PAD, y = statusTop - h - 8f * k;
            TASGui.Panel(new Rect(x, y, w, h));

            float cx = x + 12f * k, cy = y + 8f * k;
            last = null;
            var s = TASConfig.Settings;
            foreach (var a in actions)
            {
                if (a.Section != last)
                {
                    last = a.Section;
                    TASGui.Label(new Rect(cx, cy + 4f * k, w - 24f * k, 16f * k), a.Section, TASGui.TextMuted, Font(11, k), true);
                    cy += secH;
                }
                var bind = a.Get(s);
                TASGui.Label(new Rect(cx, cy, w * 0.6f, lineH), a.Label, TASGui.TextDim, Font(12, k));
                TASGui.Label(new Rect(x + w * 0.55f, cy, w * 0.45f - 12f * k, lineH), bind.ToString(),
                             bind.IsBound ? TASGui.Text : TASGui.TextMuted, Font(12, k), false, TextAnchor.MiddleRight);
                cy += lineH;
            }
        }

        // ── Position / speed readout ────────────────────────────────────
        private void DrawReadout(float k)
        {
            float w = 220f * k, lineH = 20f * k, h = 3 * lineH + 14f * k;
            float x = Screen.width - w - PAD, y = PAD;
            TASGui.Panel(new Rect(x, y, w, h));

            float cx = x + 12f * k, cy = y + 7f * k, vw = w - 24f * k;
            ReadoutLine(cx, cy, vw, lineH, "SPEED", _speedStr, k); cy += lineH;
            ReadoutLine(cx, cy, vw, lineH, "HEIGHT", _heightStr, k); cy += lineH;
            ReadoutLine(cx, cy, vw, lineH, "XYZ", _xyzStr, k);
        }

        private static void ReadoutLine(float x, float y, float w, float h, string label, string value, float k)
        {
            TASGui.Label(new Rect(x, y, w, h), label, TASGui.TextMuted, Font(12, k), true);
            TASGui.Label(new Rect(x, y, w, h), value, TASGui.Text, Font(13, k), false, TextAnchor.MiddleRight);
        }

        // ── Input display (recording / Edit Mode) ───────────────────────
        private void DrawInputDisplay(float k)
        {
            var pins = _s.Pins;
            bool pinned = pins.Active;
            // What the next recorded tick will contain: the pins when set, else live input.
            int mx = pinned ? pins.MoveX : _s.LiveMoveX;
            int my = pinned ? pins.MoveY : _s.LiveMoveY;
            bool jump = pinned ? pins.Jump : _s.LiveJump;
            bool inter = pinned ? pins.Interact : _s.LiveInteract;

            float box = 30f * k, gap = 4f * k, pad = 10f * k, titleH = 18f * k;
            float wideW = box * 1.9f;
            float gridW = box * 3 + gap * 2;
            float w = pad * 2 + gridW + gap * 3 + wideW;
            float h = pad + titleH + 6f * k + box * 2 + gap + pad;
            float x = (Screen.width - w) / 2f;
            float hintH = _s.Paused ? 22f * k : 0f;
            float y = Screen.height - h - hintH - PAD;

            TASGui.Panel(new Rect(x, y, w, h));
            float cx = x + pad, cy = y + pad;
            TASGui.Label(new Rect(cx, cy, w - pad * 2, titleH), "NEXT INPUT", TASGui.TextMuted, Font(11, k), true);
            if (pinned)
                TASGui.Badge(new Rect(x + w - pad - 58f * k, cy, 58f * k, titleH), "PINNED", TASGui.Edit, Font(11, k));
            cy += titleH + 6f * k;

            Color on = pinned ? TASGui.Edit : TASGui.Ok;
            KeyBox(new Rect(cx + box + gap, cy, box, box), "W", my > 16, on, k);
            float row2 = cy + box + gap;
            KeyBox(new Rect(cx, row2, box, box), "A", mx < -16, on, k);
            KeyBox(new Rect(cx + box + gap, row2, box, box), "S", my < -16, on, k);
            KeyBox(new Rect(cx + (box + gap) * 2, row2, box, box), "D", mx > 16, on, k);

            float rx = cx + gridW + gap * 3;
            KeyBox(new Rect(rx, cy, wideW, box), "JUMP", jump, on, k);
            KeyBox(new Rect(rx, row2, wideW, box), "INT", inter, on, k);

            if (_s.Paused)
            {
                var st = TASConfig.Settings;
                TASGui.Label(new Rect(x - 120f * k, y + h + 4f * k, w + 240f * k, 18f * k),
                             $"Hold keys or pin inputs in the editor   [{st.FrameAdvance}] +1 tick   [{st.RewindTick}] back",
                             TASGui.TextDim, Font(12, k), false, TextAnchor.MiddleCenter);
            }
        }

        private static void KeyBox(Rect r, string label, bool active, Color onColor, float k)
        {
            TASGui.Fill(r, new Color(0.14f, 0.147f, 0.167f, 0.92f));
            if (active)
            {
                TASGui.Fill(r, TASGui.Tint(onColor, 0.24f));
                TASGui.Fill(new Rect(r.x, r.yMax - 2f, r.width, 2f), TASGui.Tint(onColor, 0.85f));
            }
            TASGui.Outline(r, TASGui.Border);
            TASGui.Label(r, label, active ? onColor : TASGui.TextMuted, Font(12, k), active, TextAnchor.MiddleCenter);
        }
    }
}
