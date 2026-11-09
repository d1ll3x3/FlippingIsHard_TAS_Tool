using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BepInEx;
using UnityEngine;

namespace FlippingIsHardTAS
{
    public class KeyBind
    {
        public KeyCode Modifier { get; set; } = KeyCode.None;
        public KeyCode MainKey { get; set; } = KeyCode.None;

        public KeyBind() { }

        public KeyBind(KeyCode mainKey, KeyCode modifier = KeyCode.None)
        {
            MainKey = mainKey;
            Modifier = modifier;
        }

        [JsonIgnore]
        public bool IsBound => MainKey != KeyCode.None;

        public override string ToString()
        {
            if (MainKey == KeyCode.None) return "—";
            string mod = Modifier == KeyCode.None ? "" : KeyName(Modifier) + "+";
            return mod + KeyName(MainKey);
        }

        public bool SameAs(KeyBind other)
            => other != null && other.MainKey == MainKey && other.Modifier == Modifier;

        /// <summary>Short, human-readable key name for the HUD and menus.</summary>
        public static string KeyName(KeyCode k)
        {
            switch (k)
            {
                case KeyCode.Period: return ".";
                case KeyCode.Comma: return ",";
                case KeyCode.Slash: return "/";
                case KeyCode.Backslash: return "\\";
                case KeyCode.Semicolon: return ";";
                case KeyCode.Quote: return "'";
                case KeyCode.Minus: return "-";
                case KeyCode.Equals: return "=";
                case KeyCode.LeftBracket: return "[";
                case KeyCode.RightBracket: return "]";
                case KeyCode.BackQuote: return "`";
                case KeyCode.LeftShift: case KeyCode.RightShift: return "Shift";
                case KeyCode.LeftControl: case KeyCode.RightControl: return "Ctrl";
                case KeyCode.LeftAlt: case KeyCode.RightAlt: return "Alt";
                case KeyCode.Return: return "Enter";
                case KeyCode.Escape: return "Esc";
                case KeyCode.PageUp: return "PgUp";
                case KeyCode.PageDown: return "PgDn";
                case KeyCode.Mouse2: return "Mouse3";
                case KeyCode.Mouse3: return "Mouse4";
                case KeyCode.Mouse4: return "Mouse5";
            }
            string s = k.ToString();
            if (s.StartsWith("Alpha")) return s.Substring(5);
            if (s.StartsWith("Keypad")) return "Num" + s.Substring(6);
            return s;
        }

        public bool IsPressed()
        {
            if (MainKey == KeyCode.None) return false;

            bool isMainHeld = Input.GetKey(MainKey);

            // A modifier used as the main key (e.g. SlowMoBoost = LeftShift) must not be
            // vetoed by the "no other modifier held" rule below.
            if (IsModifierKey(MainKey))
                return isMainHeld;

            // Unmodified binds don't fire while a modifier is held, so "R" and "Shift+R"
            // can coexist on different actions.
            bool modValid = Modifier == KeyCode.None ? !IsAnyModifierHeld() : IsModifierHeld(Modifier);
            return isMainHeld && modValid;
        }

        public static bool IsModifierKey(KeyCode k)
            => k == KeyCode.LeftShift || k == KeyCode.RightShift
            || k == KeyCode.LeftControl || k == KeyCode.RightControl
            || k == KeyCode.LeftAlt || k == KeyCode.RightAlt;

        private static bool IsModifierHeld(KeyCode modifier)
        {
            if (modifier == KeyCode.LeftShift || modifier == KeyCode.RightShift)
                return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (modifier == KeyCode.LeftControl || modifier == KeyCode.RightControl)
                return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (modifier == KeyCode.LeftAlt || modifier == KeyCode.RightAlt)
                return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            return Input.GetKey(modifier);
        }

        public static bool IsAnyModifierHeld()
            => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)
            || Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)
            || Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
    }

    public class TASSettings
    {
        public KeyBind SavePosition   { get; set; } = new KeyBind(KeyCode.R, KeyCode.LeftShift);
        public KeyBind Teleport       { get; set; } = new KeyBind(KeyCode.R);
        public KeyBind RecordMacro    { get; set; } = new KeyBind(KeyCode.F9);
        public KeyBind PlayMacro      { get; set; } = new KeyBind(KeyCode.F10);
        public KeyBind EditMacro      { get; set; } = new KeyBind(KeyCode.F8);
        public KeyBind RewindTick     { get; set; } = new KeyBind(KeyCode.Comma);
        public KeyBind OpenBindMenu   { get; set; } = new KeyBind(KeyCode.B);
        public KeyBind Pause          { get; set; } = new KeyBind(KeyCode.F11);
        public KeyBind SlowMo         { get; set; } = new KeyBind(KeyCode.F12);
        public KeyBind SlowMoBoost    { get; set; } = new KeyBind(KeyCode.E);
        public KeyBind FrameAdvance   { get; set; } = new KeyBind(KeyCode.Period);
        public KeyBind ResetTick      { get; set; } = new KeyBind(KeyCode.F5);
        public KeyBind FastForward    { get; set; } = new KeyBind(KeyCode.F6);
        public KeyBind OpenEditor     { get; set; } = new KeyBind(KeyCode.T);
        public KeyBind ToggleHelp     { get; set; } = new KeyBind(KeyCode.F1);
        public float OverlayScale     { get; set; } = 1.0f;
        public bool ShowKeyHelp       { get; set; } = false;
        public bool PauseAtPlaybackEnd { get; set; } = true;

        public TASSettings Clone()
            => JsonSerializer.Deserialize<TASSettings>(JsonSerializer.Serialize(this, TASConfig.JsonOptions), TASConfig.JsonOptions);
    }

    /// <summary>
    /// One rebindable action. The bind menu, the HUD key list and duplicate detection all
    /// iterate this table, so adding an action is a one-line change here.
    /// </summary>
    public sealed class BindAction
    {
        public readonly string Section;
        public readonly string Label;
        public readonly Func<TASSettings, KeyBind> Get;
        public readonly Action<TASSettings, KeyBind> Set;

        public BindAction(string section, string label, Func<TASSettings, KeyBind> get, Action<TASSettings, KeyBind> set)
        {
            Section = section; Label = label; Get = get; Set = set;
        }
    }

    public static class TASConfig
    {
        private static string ConfigFilePath => Path.Combine(Paths.ConfigPath, "com.flippingishard.tas.json");

        internal static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        public static TASSettings Settings { get; set; } = new TASSettings();

        public static readonly BindAction[] Actions =
        {
            new BindAction("MACRO",     "Record / Stop",       s => s.RecordMacro,  (s, b) => s.RecordMacro = b),
            new BindAction("MACRO",     "Play / Stop",         s => s.PlayMacro,    (s, b) => s.PlayMacro = b),
            new BindAction("MACRO",     "Edit Mode",           s => s.EditMacro,    (s, b) => s.EditMacro = b),
            new BindAction("MACRO",     "Reset Tick",          s => s.ResetTick,    (s, b) => s.ResetTick = b),
            new BindAction("TIME",      "Pause / Resume",      s => s.Pause,        (s, b) => s.Pause = b),
            new BindAction("TIME",      "Frame Advance",       s => s.FrameAdvance, (s, b) => s.FrameAdvance = b),
            new BindAction("TIME",      "Rewind Tick",         s => s.RewindTick,   (s, b) => s.RewindTick = b),
            new BindAction("TIME",      "Fast Forward ×3",     s => s.FastForward,  (s, b) => s.FastForward = b),
            new BindAction("TIME",      "Slow Motion ×0.1",    s => s.SlowMo,       (s, b) => s.SlowMo = b),
            new BindAction("TIME",      "Slow-Mo Boost ×0.3",  s => s.SlowMoBoost,  (s, b) => s.SlowMoBoost = b),
            new BindAction("SAVESTATE", "Save State",          s => s.SavePosition, (s, b) => s.SavePosition = b),
            new BindAction("SAVESTATE", "Load State",          s => s.Teleport,     (s, b) => s.Teleport = b),
            new BindAction("INTERFACE", "TAS Editor",          s => s.OpenEditor,   (s, b) => s.OpenEditor = b),
            new BindAction("INTERFACE", "Settings",            s => s.OpenBindMenu, (s, b) => s.OpenBindMenu = b),
            new BindAction("INTERFACE", "Key List (HUD)",      s => s.ToggleHelp,   (s, b) => s.ToggleHelp = b),
        };

        public static void Load()
        {
            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    string json = File.ReadAllText(ConfigFilePath);
                    // Properties missing from an older config keep their initializer defaults.
                    Settings = JsonSerializer.Deserialize<TASSettings>(json, JsonOptions) ?? new TASSettings();
                    foreach (var a in Actions)
                        if (a.Get(Settings) == null) a.Set(Settings, new KeyBind());
                    TASPlugin.Logger.LogInfo("Config loaded.");
                }
                else
                {
                    Save();
                }
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error loading config ({ConfigFilePath}): {ex.Message}. Using defaults.");
                Settings = new TASSettings();
            }
        }

        public static void Save()
        {
            try
            {
                string json = JsonSerializer.Serialize(Settings, JsonOptions);
                // Write-then-rename so a crash mid-write can't leave a truncated config.
                string tmp = ConfigFilePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, ConfigFilePath, overwrite: true);
                TASPlugin.Logger.LogInfo("Config saved.");
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error saving config ({ConfigFilePath}): {ex.Message}");
            }
        }
    }
}
