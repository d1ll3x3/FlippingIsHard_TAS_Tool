using System.Collections.Generic;
using UnityEngine;

namespace FlippingIsHardTAS
{
    /// <summary>
    /// Shared immediate-mode UI toolkit for the HUD, the editor and the settings window.
    ///
    /// IMGUI's interactive controls (GUI.Button/Toggle/TextField/Window dragging) are broken
    /// in this game's IL2CPP build: they render but never receive events. So everything is
    /// drawn with GUI.DrawTexture + GUI.Label, and the mouse is read from legacy Input and
    /// hit-tested manually — once per frame, on the Repaint pass. All rects are in screen
    /// coordinates (top-left origin).
    /// </summary>
    internal static class TASGui
    {
        // ── Palette ──────────────────────────────────────────────────────
        // Muted, low-saturation tones: the UI sits on top of a bright game for hours, so
        // colour is reserved for state and used as tints rather than solid blocks.
        // Even the "muted" grey must stay comfortably readable on the darkest panel.
        public static readonly Color Bg          = new Color(0.085f, 0.090f, 0.105f, 0.94f);
        public static readonly Color BgRaised    = new Color(0.115f, 0.120f, 0.138f, 0.96f);
        public static readonly Color BgInset     = new Color(0.065f, 0.069f, 0.080f, 0.95f);
        public static readonly Color Border      = new Color(1f, 1f, 1f, 0.07f);
        public static readonly Color Accent      = new Color(0.60f, 0.74f, 0.96f, 1f);
        public static readonly Color Text        = new Color(0.93f, 0.94f, 0.95f, 1f);
        public static readonly Color TextDim     = new Color(0.79f, 0.80f, 0.83f, 1f);
        public static readonly Color TextMuted   = new Color(0.65f, 0.67f, 0.71f, 1f);
        public static readonly Color Rec         = new Color(0.96f, 0.58f, 0.58f, 1f);
        public static readonly Color Play        = new Color(0.60f, 0.85f, 0.93f, 1f);
        public static readonly Color Pause       = new Color(0.95f, 0.83f, 0.57f, 1f);
        public static readonly Color Edit        = new Color(0.97f, 0.73f, 0.52f, 1f);
        public static readonly Color Ok          = new Color(0.63f, 0.89f, 0.71f, 1f);
        public static readonly Color Danger      = new Color(0.94f, 0.55f, 0.55f, 1f);
        private static readonly Color BtnBg       = new Color(0.150f, 0.157f, 0.178f, 1f);
        private static readonly Color BtnHover    = new Color(0.190f, 0.198f, 0.224f, 1f);
        private static readonly Color BtnPressed  = new Color(0.225f, 0.235f, 0.265f, 1f);
        private static readonly Color BtnDisabled = new Color(0.120f, 0.126f, 0.142f, 1f);

        /// <summary>The colour at a given opacity, for tinted fills.</summary>
        public static Color Tint(Color c, float alpha) => new Color(c.r, c.g, c.b, alpha);

        // ── Input routing ────────────────────────────────────────────────
        /// <summary>Layer being drawn (0 = HUD/editor, 1 = settings).</summary>
        public static int CurrentLayer = 0;
        /// <summary>Lowest layer that may take input — raised while a modal window is open.</summary>
        public static int ModalLayer = 0;

        private static int _clickConsumedFrame = -1;

        public static bool IsRepaint => Event.current != null && Event.current.type == EventType.Repaint;
        public static bool Interactive => CurrentLayer >= ModalLayer;

        public static Vector2 Mouse
        {
            get
            {
                Vector3 m = Input.mousePosition;
                return new Vector2(m.x, Screen.height - m.y);
            }
        }

        public static bool Hover(Rect r) => Interactive && r.Contains(Mouse);

        /// <summary>True once, on the frame the left button goes down inside r.</summary>
        public static bool Clicked(Rect r)
        {
            if (!IsRepaint || !Interactive || _clickConsumedFrame == Time.frameCount) return false;
            if (!Input.GetMouseButtonDown(0) || !r.Contains(Mouse)) return false;
            _clickConsumedFrame = Time.frameCount;
            return true;
        }

        /// <summary>Swallows this frame's click (e.g. a click on a window's empty area).</summary>
        public static void ConsumeClickIn(Rect r)
        {
            if (IsRepaint && Interactive && Input.GetMouseButtonDown(0) && r.Contains(Mouse))
                _clickConsumedFrame = Time.frameCount;
        }

        // ── Drawing primitives ───────────────────────────────────────────
        public static void Fill(Rect r, Color c)
        {
            Color o = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = o;
        }

        public static void Outline(Rect r, Color c, float t = 1f)
        {
            Fill(new Rect(r.x, r.y, r.width, t), c);
            Fill(new Rect(r.x, r.yMax - t, r.width, t), c);
            Fill(new Rect(r.x, r.y, t, r.height), c);
            Fill(new Rect(r.xMax - t, r.y, t, r.height), c);
        }

        public static void Panel(Rect r)
        {
            Fill(r, Bg);
            Outline(r, Border);
        }

        private static readonly Dictionary<int, GUIStyle> _styles = new Dictionary<int, GUIStyle>();

        public static GUIStyle Style(int size, bool bold, TextAnchor anchor)
        {
            int key = size * 1000 + (bold ? 100 : 0) + (int)anchor;
            if (!_styles.TryGetValue(key, out var st))
            {
                st = new GUIStyle
                {
                    fontSize = size,
                    fontStyle = bold ? FontStyle.Bold : FontStyle.Normal,
                    alignment = anchor,
                    clipping = TextClipping.Clip,
                    wordWrap = false,
                };
                _styles[key] = st;
            }
            return st;
        }

        public static void Label(Rect r, string text, Color color, int size = 13, bool bold = false,
                                 TextAnchor anchor = TextAnchor.MiddleLeft)
        {
            var st = Style(size, bold, anchor);
            st.normal.textColor = color;
            GUI.Label(r, text, st);
        }

        /// <summary>Small tinted tag with coloured text (state badges, "PINNED", etc).</summary>
        public static void Badge(Rect r, string text, Color color, int size = 12)
        {
            Fill(r, Tint(color, 0.16f));
            Fill(new Rect(r.x, r.y, 2, r.height), Tint(color, 0.75f));
            Label(r, text, color, size, true, TextAnchor.MiddleCenter);
        }

        public static void SectionTitle(Rect r, string text)
        {
            Label(r, text, TextMuted, 11, true);
            Fill(new Rect(r.x, r.yMax - 1, r.width, 1), Border);
        }

        // ── Widgets ──────────────────────────────────────────────────────
        /// <param name="on">Draws the button latched/highlighted (toggles, active modes).</param>
        /// <param name="accent">Colour for the "on" fill and the hover underline.</param>
        public static bool Button(Rect r, string label, bool enabled = true, bool on = false,
                                  Color? accent = null, int size = 12)
        {
            Color acc = accent ?? Accent;
            bool hover = enabled && Hover(r);
            bool held = hover && Input.GetMouseButton(0);

            Color bg = !enabled ? BtnDisabled
                     : held ? BtnPressed
                     : hover ? BtnHover
                     : BtnBg;
            Fill(r, bg);
            if (on && enabled)
            {
                Fill(r, Tint(acc, hover ? 0.24f : 0.18f));
                Fill(new Rect(r.x, r.yMax - 2, r.width, 2), Tint(acc, 0.8f));
            }
            Outline(r, Border);

            Color fg = !enabled ? Tint(TextMuted, 0.75f) : on ? acc : hover ? Text : TextDim;
            Label(r, label, fg, size, on, TextAnchor.MiddleCenter);

            return enabled && Clicked(r);
        }

        public static bool Toggle(Rect r, string label, bool value, Color? accent = null, int size = 12)
            => Button(r, label, true, value, accent, size) ? !value : value;

        /// <summary>Id of the focused text field; hotkeys are suspended while one is focused.</summary>
        public static string ActiveField;
        public static bool IsTyping => ActiveField != null;

        /// <summary>Click to focus, type, Enter/Esc (or click elsewhere) to unfocus.</summary>
        public static string TextField(Rect r, string id, string value, int maxLength = 48)
        {
            bool active = ActiveField == id;
            bool hover = Hover(r);
            Fill(r, BgInset);
            Outline(r, active ? Tint(Accent, 0.7f) : hover ? new Color(1f, 1f, 1f, 0.14f) : Border);

            bool caret = active && (Time.unscaledTime % 1f) < 0.55f;
            Label(new Rect(r.x + 6, r.y, r.width - 10, r.height), caret ? value + "|" : value, Text, 12);

            if (!Interactive) return value;

            if (IsRepaint && Input.GetMouseButtonDown(0))
            {
                if (r.Contains(Mouse) && _clickConsumedFrame != Time.frameCount)
                {
                    ActiveField = id;
                    _clickConsumedFrame = Time.frameCount;
                }
                else if (active && !r.Contains(Mouse))
                {
                    ActiveField = null;
                }
            }

            // Key input arrives through IMGUI KeyDown events (these do fire, unlike clicks).
            var e = Event.current;
            if (active && e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Backspace)
                {
                    if (value.Length > 0) value = value.Substring(0, value.Length - 1);
                    e.Use();
                }
                else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter || e.keyCode == KeyCode.Escape)
                {
                    ActiveField = null;
                    e.Use();
                }
                else if (e.character >= 32 && value.Length < maxLength)
                {
                    value += e.character;
                    e.Use();
                }
            }
            return value;
        }

        // ── Toasts ───────────────────────────────────────────────────────
        private struct ToastEntry { public string Text; public Color Color; public float Until; }
        private static readonly List<ToastEntry> _toasts = new List<ToastEntry>();
        private const float TOAST_SECONDS = 2.6f;

        /// <summary>Short on-screen notification (top centre). Also written to the log.</summary>
        public static void Toast(string text, Color? color = null)
        {
            _toasts.Add(new ToastEntry { Text = text, Color = color ?? Text, Until = Time.unscaledTime + TOAST_SECONDS });
            if (_toasts.Count > 4) _toasts.RemoveAt(0);
            TASPlugin.Logger.LogInfo($"TAS: {text}");
        }

        public static void ClearToasts() => _toasts.Clear();

        public static void DrawToasts()
        {
            if (_toasts.Count == 0) return;
            float now = Time.unscaledTime;
            _toasts.RemoveAll(t => t.Until <= now);

            const float w = 460f, h = 30f;
            float y = 18f;
            foreach (var t in _toasts)
            {
                float alpha = Mathf.Clamp01((t.Until - now) / 0.35f);
                var r = new Rect((Screen.width - w) / 2f, y, w, h);
                Fill(r, new Color(Bg.r, Bg.g, Bg.b, Bg.a * alpha));
                Fill(new Rect(r.x, r.y, 2, r.height), Tint(t.Color, 0.8f * alpha));
                Label(new Rect(r.x + 12, r.y, r.width - 18, r.height), t.Text,
                      Tint(t.Color, alpha), 13, false, TextAnchor.MiddleCenter);
                y += h + 6f;
            }
        }
    }

    /// <summary>
    /// A draggable window drawn with TASGui. Content rects are given in window-local
    /// coordinates through R(); the window keeps itself on screen.
    /// </summary>
    internal sealed class GuiWindow
    {
        public Rect Rect;
        public readonly string Title;
        public const float TitleH = 30f;

        private bool _dragging;
        private Vector2 _dragOffset;

        public GuiWindow(string title, Rect rect)
        {
            Title = title;
            Rect = rect;
        }

        public Rect R(float x, float y, float w, float h) => new Rect(Rect.x + x, Rect.y + y, w, h);

        public void CenterOnScreen()
        {
            Rect.x = Mathf.Round((Screen.width - Rect.width) / 2f);
            Rect.y = Mathf.Round((Screen.height - Rect.height) / 2f);
            ClampToScreen();
        }

        public void ClampToScreen()
        {
            Rect.x = Mathf.Clamp(Rect.x, 60 - Rect.width, Screen.width - 60);
            Rect.y = Mathf.Clamp(Rect.y, 0, Mathf.Max(0, Screen.height - TitleH));
        }

        /// <summary>
        /// Draws the window frame and title bar and handles dragging by the title bar.
        /// Returns true when the close button was clicked.
        /// </summary>
        public bool DrawChrome(string subtitle = null)
        {
            if (TASGui.IsRepaint) HandleDrag();

            TASGui.Fill(new Rect(Rect.x + 3, Rect.y + 4, Rect.width, Rect.height), new Color(0f, 0f, 0f, 0.22f));
            TASGui.Panel(Rect);

            var bar = new Rect(Rect.x, Rect.y, Rect.width, TitleH);
            TASGui.Fill(bar, TASGui.BgRaised);
            TASGui.Fill(new Rect(Rect.x, Rect.y + TitleH - 1, Rect.width, 1), TASGui.Border);
            TASGui.Label(new Rect(Rect.x + 12, Rect.y + 1, Rect.width * 0.5f, TitleH), Title, TASGui.TextDim, 12, true);
            if (!string.IsNullOrEmpty(subtitle))
                TASGui.Label(new Rect(Rect.x + Rect.width * 0.35f, Rect.y + 1, Rect.width * 0.65f - 44, TitleH),
                             subtitle, TASGui.TextDim, 12, false, TextAnchor.MiddleRight);

            return TASGui.Button(new Rect(Rect.xMax - 32, Rect.y + 5, 24, 20), "×", accent: TASGui.Danger, size: 14);
        }

        /// <summary>
        /// Call after drawing the content: a click on empty window space is swallowed so it
        /// can't reach anything drawn later in the frame.
        /// </summary>
        public void End() => TASGui.ConsumeClickIn(Rect);

        private void HandleDrag()
        {
            if (!TASGui.Interactive) { _dragging = false; return; }
            Vector2 m = TASGui.Mouse;

            if (!_dragging && Input.GetMouseButtonDown(0))
            {
                var handle = new Rect(Rect.x, Rect.y, Rect.width - 40, TitleH);
                if (handle.Contains(m))
                {
                    _dragging = true;
                    _dragOffset = m - new Vector2(Rect.x, Rect.y);
                }
            }

            if (_dragging)
            {
                if (!Input.GetMouseButton(0)) _dragging = false;
                else
                {
                    Rect.x = m.x - _dragOffset.x;
                    Rect.y = m.y - _dragOffset.y;
                    ClampToScreen();
                }
            }
        }
    }
}
