using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace FlippingIsHardTAS
{
    [BepInPlugin("com.flippingishard.tas", "Flipping is Hard TAS", MyPluginInfo.PLUGIN_VERSION)]
    public class TASPlugin : BasePlugin
    {
        internal static ManualLogSource Logger { get; private set; }

        private static readonly HashSet<string> _warned = new HashSet<string>();

        /// <summary>
        /// Logs a warning once per key. For failures inside per-frame code paths, where
        /// logging every occurrence would flood the log.
        /// </summary>
        internal static void WarnOnce(string key, string message)
        {
            if (_warned.Add(key)) Logger.LogWarning(message);
        }

        public override void Load()
        {
            Logger = Log;
            TASConfig.Load();

            // NOTE: we intentionally do NOT Harmony-patch any IL2CPP game method. The old
            // GameInputPatch hooked PlayerInputHandler.IsHeld/WasPressed/MoveInput/LookInput,
            // and the native detour for those crashed the game on save-load in v0.12. The
            // replay doesn't need them: the trajectory comes from per-tick STATE injection
            // (OnPrePhysicsSimulation) and inputs from rawData FIELD writes (resolved by
            // name — stable across versions). No method patching = works on all versions.
            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<TASBehaviour>();
                var go = new GameObject("FlippingIsHardTAS");
                GameObject.DontDestroyOnLoad(go);
                go.AddComponent<TASBehaviour>();
                Logger.LogInfo($"Flipping is Hard TAS {MyPluginInfo.PLUGIN_VERSION} loaded — waiting for a level.");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to attach the TAS behaviour: {ex}");
            }
        }
    }

    /// <summary>
    /// Host MonoBehaviour: forwards Unity callbacks to the controller once a player exists.
    /// Must have the IntPtr constructor for IL2CPP interop.
    /// </summary>
    public class TASBehaviour : MonoBehaviour
    {
        public TASBehaviour(IntPtr ptr) : base(ptr) { }

        private TASController _controller;
        private TASBindMenuRenderer _bindMenu;
        private GameObjectFinder _finder;
        private float _nextAttempt = 2f;
        private const float RETRY_INTERVAL = 3f;

        void Awake()
        {
            _finder = new GameObjectFinder();
        }

        void Update()
        {
            if (_controller != null)
            {
                _controller.Update();
                return;
            }

            if (Time.unscaledTime >= _nextAttempt)
            {
                _nextAttempt = Time.unscaledTime + RETRY_INTERVAL;
                TryInitialize();
            }
        }

        void FixedUpdate()
        {
            _controller?.FixedUpdate();
        }

        public void OnGUI()
        {
            if (_controller == null) return;
            _controller.OnGUI();
            // Drawn last so the modal settings window sits on top of the editor.
            TASGui.CurrentLayer = 1;
            _bindMenu.Draw();
            TASGui.CurrentLayer = 0;
            TASGui.DrawToasts();
        }

        private void TryInitialize()
        {
            // The controller waits for a real level: the player and the camera must exist.
            if (_finder.FindPlayer() == null || _finder.FindCamera() == null) return;

            try
            {
                var bindMenu = new TASBindMenuRenderer(_finder);
                var controller = new TASController();
                if (!controller.Initialize(_finder, bindMenu)) return;
                _bindMenu = bindMenu;
                _controller = controller;
                TASPlugin.Logger.LogInfo($"TAS ready. [{TASConfig.Settings.OpenEditor}] editor · [{TASConfig.Settings.OpenBindMenu}] settings · [{TASConfig.Settings.ToggleHelp}] key list.");
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"TAS initialization failed (will retry): {ex}");
            }
        }
    }
}
