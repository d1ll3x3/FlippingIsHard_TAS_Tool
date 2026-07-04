using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using System;

namespace FlippingIsHardTAS
{
    [BepInPlugin("com.flippingishard.tas", "Flipping is Hard TAS", "2.0.4")]
    public class TASPlugin : BasePlugin
    {
        internal static ManualLogSource Logger { get; private set; }
        public override void Load()
        {
            Logger = Log;
            
            TASConfig.Load();

            Logger.LogInfo("Flipping is Hard TAS plugin loaded!");
            Logger.LogInfo("Controls: B (Settings), T (TAS Editor), F9 (Record), F10 (Play) — all rebindable in-game.");

            // NOTE: we intentionally do NOT Harmony-patch any IL2CPP game method. The old
            // GameInputPatch hooked PlayerInputHandler.IsHeld/WasPressed/MoveInput/LookInput,
            // and the native detour for those crashed the game on save-load in v0.12. The
            // replay doesn't need them: the trajectory comes from per-tick STATE injection
            // (OnPrePhysicsSimulation) and inputs from rawData FIELD writes (resolved by
            // name — stable across versions). No method patching = works on all versions.

            try
            {
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

