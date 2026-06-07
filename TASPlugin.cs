using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using HarmonyLib;
using UnityEngine;
using System;

namespace FlippingIsHardTAS
{
    [BepInPlugin("com.flippingishard.tas", "Flipping is Hard TAS", "1.0.0")]
    public class TASPlugin : BasePlugin
    {
        internal static ManualLogSource Logger { get; private set; }
        public override void Load()
        {
            Logger = Log;
            
            TASConfig.Load();

            Logger.LogInfo("Flipping is Hard TAS plugin loaded!");
            Logger.LogInfo("Controls: Shift+R (Save), R (Teleport), F (Fly Mode)");

            try
            {
                var harmony = new Harmony("com.flippingishard.tas.patches");

                // Apply the standard input patches (these are stable and always valid)
                harmony.PatchAll(typeof(GameInputPatch));
                Logger.LogInfo("GameInputPatch applied.");

                // Register our custom MonoBehaviour with IL2CPP before using AddComponent
                ClassInjector.RegisterTypeInIl2Cpp<TASBehaviour>();

                var go = new GameObject("FlippingIsHardTAS");
                GameObject.DontDestroyOnLoad(go);
                go.AddComponent<TASBehaviour>();

                Logger.LogInfo("Trainer behaviour attached successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error loading trainer: {ex}");
            }
        }
    }

    /// <summary>
    /// Main MonoBehaviour for the trainer.
    /// Must have the IntPtr constructor for IL2CPP interop.
    /// </summary>
    public class TASBehaviour : MonoBehaviour
    {
        // Required by IL2CPP interop
        public TASBehaviour(IntPtr ptr) : base(ptr) { }

        private TASController _controller;
        private TASBindMenuRenderer _bindMenuRenderer;
        private GameObjectFinder _finder;
        private bool _initialized = false;
        private float _timer = 0f;
        private float _nextAttempt = 2f;   // first attempt after 2s
        private const float RETRY_INTERVAL = 3f;

        void Awake()
        {
            _finder = new GameObjectFinder();
            TASPlugin.Logger.LogInfo("TASBehaviour awake, waiting for game scene...");
        }

        void Update()
        {
            if (_initialized)
            {
                _controller?.Update();
                return;
            }

            _timer += Time.deltaTime;
            if (_timer >= _nextAttempt)
            {
                _timer = 0f;
                _nextAttempt = RETRY_INTERVAL;
                TryInitialize();
            }
        }

        void FixedUpdate()
        {
            if (_initialized)
            {
                _controller?.FixedUpdate();
            }
        }

        public void OnGUI()
        {
            _controller?.OnGUI();
            _bindMenuRenderer?.Draw();
        }

        private void TryInitialize()
        {
            try
            {
                var player = _finder.FindPlayer();
                var camera = _finder.FindCamera();

                if (player != null && camera != null)
                {
                    TASPlugin.Logger.LogInfo($"Found player: {player.name}");
                    try
                    {
                        var gameObjectFinder = new GameObjectFinder();
                        _bindMenuRenderer = new TASBindMenuRenderer(gameObjectFinder);
                        _controller = new TASController();
                        _controller.Initialize(gameObjectFinder, _bindMenuRenderer);
                        _controller.enabled = true;
                        _initialized = true;
                    }
                    catch (Exception ex)
                    {
                        TASPlugin.Logger.LogError($"Error in TryInitialize inner block: {ex}");
                    }
                    TASPlugin.Logger.LogInfo("Trainer initialized successfully!");
                }
                else
                {
                    if (player == null) TASPlugin.Logger.LogInfo("Player not found yet, retrying...");
                    if (camera == null) TASPlugin.Logger.LogInfo("Camera not found yet, retrying...");
                }
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error during initialization: {ex}");
            }
        }
    }
}

