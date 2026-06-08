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

        public override string ToString()
        {
            if (MainKey == KeyCode.None) return "Unbound";
            
            string modStr = Modifier == KeyCode.None ? "" : $"{Modifier} + ";
            return $"{modStr}{MainKey}";
        }

        public KeyBind Clone()
        {
            return new KeyBind(MainKey, Modifier);
        }

        public bool IsPressed()
        {
            if (MainKey == KeyCode.None) return false;

            bool isMainHeld = Input.GetKey(MainKey);
            bool isModHeld  = IsModifierHeld(Modifier);

            // If main key is itself a modifier key, skip the anti-conflict check
            // so e.g. SlowMoBoost = LeftShift works without a modifier requirement.
            if (IsModifierKey(MainKey))
                return isMainHeld;

            bool modValid = (Modifier == KeyCode.None) ? !IsAnyModifierHeld() : isModHeld;
            return isMainHeld && modValid;
        }

        private static bool IsModifierKey(KeyCode k)
        {
            return k == KeyCode.LeftShift  || k == KeyCode.RightShift  ||
                   k == KeyCode.LeftControl|| k == KeyCode.RightControl ||
                   k == KeyCode.LeftAlt   || k == KeyCode.RightAlt;
        }

        private bool IsModifierHeld(KeyCode modifier)
        {
            if (modifier == KeyCode.None) return false;
            if (modifier == KeyCode.LeftShift || modifier == KeyCode.RightShift)
                return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (modifier == KeyCode.LeftControl || modifier == KeyCode.RightControl)
                return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (modifier == KeyCode.LeftAlt || modifier == KeyCode.RightAlt)
                return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            
            return Input.GetKey(modifier);
        }

        private bool IsAnyModifierHeld()
        {
            return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ||
                   Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) ||
                   Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        }
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
    }

    public static class TASConfig
    {
        private static string ConfigFilePath => Path.Combine(Paths.ConfigPath, "com.flippingishard.tas.json");
        
        public static TASSettings Settings { get; set; } = new TASSettings();

        public static void Load()
        {
            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    string json = File.ReadAllText(ConfigFilePath);
                    var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
                    Settings = JsonSerializer.Deserialize<TASSettings>(json, options) ?? new TASSettings();
                    TASPlugin.Logger.LogInfo("Config loaded successfully.");
                }
                else
                {
                    Save();
                }
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error loading config: {ex.Message}. Using defaults.");
                Settings = new TASSettings();
            }
        }

        public static void Save()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
                string json = JsonSerializer.Serialize(Settings, options);
                File.WriteAllText(ConfigFilePath, json);
                TASPlugin.Logger.LogInfo("Config saved successfully.");
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error saving config: {ex.Message}");
            }
        }

        public static void ResetToDefaults()
        {
            Settings = new TASSettings();
        }
    }
}
