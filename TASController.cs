using System;
using UnityEngine;
using EHS;
using FishNet.Managing;

namespace FlippingIsHardTAS
{
    public class TASController
    {
        // TAS Components
        private TimeController _timeController;
        private SavestateSystem _savestateSystem;
        private InputMacroSystem _macroSystem;
        private TASBindMenuRenderer _bindMenu;

        private bool _wasTeleportPressed = false;
        private bool _wasSavePressed = false;
        private bool _wasRecordPressed = false;
        private bool _wasPlayPressed = false;
        private bool _wasMenuPressed = false;
        private bool _wasPausePressed = false;
        private bool _wasSlowMoPressed = false;
        private bool _wasFrameAdvancePressed = false;

        // Component references
        private GameObjectFinder _gameObjectFinder;
        private OverlayRenderer _overlayRenderer;

        // Scene tracking
        private string _lastScene = "";

        // Current state for overlay
        private Vector3 _currentPosition = Vector3.zero;

        // Cached physics references — updated when recording/playback starts and on scene change.
        // Avoids repeated GameObject lookups every physics tick (50Hz).
        private Rigidbody _cachedRb;
        private RigidbodyInterpolation _originalInterpolation;
        
        public bool enabled { get; set; }
        
        // FishNet TimeManager reference (hooked in Initialize, unhooked on cleanup)
        private FishNet.Managing.Timing.TimeManager _fishNetTimeManager;
        // IL2CPP delegate wrappers (stored to keep alive and enable clean unsubscription)
        private Il2CppSystem.Action<float> _prePhysicsDelegate;
        private Il2CppSystem.Action _postTickDelegate;
        
        public void Initialize(GameObjectFinder gameObjectFinder, TASBindMenuRenderer bindMenu)
        {
            try
            {
                TASConfig.Load();
                
                _gameObjectFinder = gameObjectFinder;
                _bindMenu = bindMenu;
                _overlayRenderer = new OverlayRenderer();
                _overlayRenderer.RefreshKeybinds();
                
                _timeController = new TimeController();
                _savestateSystem = new SavestateSystem();
                _macroSystem = new InputMacroSystem();
                
                GameInputPatch.MacroSystem = _macroSystem;
                FishNetReconcilePatch.MacroSystem = _macroSystem;
                
                ApplyDeterministicSettings();
                
                // Hook FishNet TimeManager so we can inject velocity BEFORE the physics step.
                // FishNet runs its physics simulation BEFORE Unity's FixedUpdate, so any
                // velocity we write in FixedUpdate arrives one frame late and gets overridden
                // by FishNet's Reconcile. Hooking OnPrePhysicsSimulation fixes this.
                SubscribeToFishNet();
                
                TASPlugin.Logger.LogInfo("TASController initialized successfully");
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error initializing TASController: {ex}");
                enabled = false;
            }
        }
        
        private bool _isInGame = false;

        public void Update()
        {
            try
            {
                if (!enabled) return;
                
                string currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                if (currentScene != _lastScene)
                {
                    _lastScene = currentScene;
                    bool wasInGame = _isInGame;
                    _isInGame = !(currentScene == "Scene_MainMenu" || currentScene == "Scene_Bootstrapper" || currentScene == "Scene_Cinematic_Intro");
                    
                    if (!_isInGame && wasInGame)
                    {
                        // Player exited to menu
                        TASPlugin.Logger.LogInfo("TAS: Exited to menu, making mod dormant.");
                        _timeController?.ResetTick();
                        _macroSystem?.Clear();
                        _savestateSystem?.Clear();
                        _gameObjectFinder?.ClearCache();
                        _cachedRb = null;
                        
                        // Disable slowmo/pause if they were active
                        if (_timeController != null)
                        {
                            if (_timeController.IsPaused) _timeController.TogglePause();
                            if (_timeController.IsSlowMo) _timeController.ToggleSlowMo();
                        }
                        
                        // Clean up hooks because the network session is ending
                        UnsubscribeFromFishNet();
                    }
                    else if (_isInGame && !wasInGame)
                    {
                        // Player entered game
                        TASPlugin.Logger.LogInfo("TAS: Entered game, waking up mod.");
                        _timeController?.ResetTick();
                        _macroSystem?.Clear();
                        _savestateSystem?.Clear();
                        _gameObjectFinder?.ClearCache();
                        _cachedRb = null;
                        
                        // Re-apply deterministic settings because Unity/Game might have reset them on scene load!
                        // This prevents lag spikes (like starting OBS) from causing physics catch-up desyncs.
                        ApplyDeterministicSettings();
                    }
                }

                // If not in an active game session, do nothing else
                if (!_isInGame) return;

                // FishNet TimeManager takes a few frames to initialize after entering a match.
                // We poll every frame until it exists and we successfully subscribe.
                if (_fishNetTimeManager == null)
                {
                    try
                    {
                        if (FishNet.InstanceFinder.TimeManager != null)
                        {
                            SubscribeToFishNet();
                        }
                    }
                    catch { } // Suppress errors if InstanceFinder is not ready
                }

                HandleHotkeys();

                if (_macroSystem != null && _macroSystem.IsPlaying && Camera.main != null)
                {
                    float t = (Time.time - Time.fixedTime) / Time.fixedDeltaTime;
                    Camera.main.transform.rotation = _macroSystem.GetInterpolatedCameraRotation(t);
                    Camera.main.transform.position = _macroSystem.GetInterpolatedCameraPosition(t);
                }
                
                if (Time.frameCount % 5 == 0)
                {
                    UpdateCurrentPosition();
                    UpdateOverlay();
                }
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error in TASController.Update: {ex}");
            }
        }


        public void FixedUpdate()
        {
            try
            {
                if (!enabled || !_isInGame) return;

                _timeController?.FixedUpdate();

                if (_timeController == null) return;

                // Refresh cache lazily — cheap null check every tick
                if (_cachedRb == null)
                    _cachedRb = _gameObjectFinder.GetCachedPlayerRigidbody();

                if (_macroSystem.IsRecording)
                {
                    var playerTransform = _gameObjectFinder.FindPlayerTransform();
                    var handler = playerTransform?.GetComponent<EHS.PlayerInputHandler>();
                    if (handler != null)
                    {
                        var rawData = handler.rawData;
                        var state = new TASInputState(
                            handler.MoveInput,
                            handler.LookInput(),
                            Camera.main != null ? Camera.main.transform.rotation : Quaternion.identity,
                            Camera.main != null ? Camera.main.transform.position : Vector3.zero,
                            (rawData.Buttons.HeldMask & 4) != 0,
                            (rawData.Buttons.HeldMask & 8) != 0,
                            _cachedRb != null ? _cachedRb.position : playerTransform.position,
                            _cachedRb != null ? _cachedRb.rotation : playerTransform.rotation,
                            _cachedRb != null ? _cachedRb.linearVelocity : Vector3.zero,
                            _cachedRb != null ? _cachedRb.angularVelocity : Vector3.zero
                        );
                        _macroSystem.RecordTick(_timeController.CurrentTick, state);
                    }
                }

                if (_macroSystem.IsPlaying)
                {
                    if (_timeController.CurrentTick > _macroSystem.MaxTick)
                    {
                        StopPlayback();
                    }
                    else
                    {
                        // Advance the playback state for this tick.
                        // Velocity injection happens in OnPrePhysicsSimulation (before physics).
                        // Position/rotation correction happens in OnPostTick (after FishNet Reconcile).
                        // Nothing extra needed here — keeping FixedUpdate lean.
                        _macroSystem.PlaybackTick(_timeController.CurrentTick);

                        if (Camera.main != null)
                        {
                            Camera.main.transform.rotation = _macroSystem.GetCurrentCameraRotation();
                            Camera.main.transform.position = _macroSystem.GetCurrentCameraPosition();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error in TASController.FixedUpdate: {ex}");
            }
        }

        // Called by FishNet TimeManager BEFORE PhysX simulates the current tick.
        private void OnPrePhysicsSimulation(float delta)
        {
            try
            {
                if (!enabled || !_isInGame) return;
                if (_cachedRb == null) return;
                if (_macroSystem == null || !_macroSystem.IsPlaying) return;

                // Full state injection before physics.
                // This is the exact method that worked perfectly before the menu bug caused desyncs.
                _cachedRb.position        = _macroSystem.GetCurrentPlayerPosition();
                _cachedRb.rotation        = _macroSystem.GetCurrentPlayerRotation();
                _cachedRb.linearVelocity  = _macroSystem.GetCurrentPlayerVelocity();
                _cachedRb.angularVelocity = _macroSystem.GetCurrentPlayerAngularVelocity();
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error in OnPrePhysicsSimulation: {ex}");
            }
        }

        private void OnPostTick()
        {
            // Empty
        }

        
        public void OnGUI()
        {
            try
            {
                if (!enabled || !_isInGame) return;
                _overlayRenderer.OnGUI();
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error in TASController.OnGUI: {ex}");
            }
        }
        
        private void HandleHotkeys()
        {
            if (TASBindMenuRenderer.IsVisibleGlobally) return;

            bool isTeleportPressed = TASConfig.Settings.Teleport.IsPressed();
            bool isSavePressed = TASConfig.Settings.SavePosition.IsPressed();
            bool isRecordPressed = TASConfig.Settings.RecordMacro.IsPressed();
            bool isPlayPressed = TASConfig.Settings.PlayMacro.IsPressed();
            bool isMenuPressed = TASConfig.Settings.OpenBindMenu.IsPressed();

            // Menu
            if (isMenuPressed && !_wasMenuPressed)
            {
                _bindMenu.ToggleVisibility();
            }

            // Save Position (Manual)
            if (isSavePressed && !_wasSavePressed)
            {
                var player = _gameObjectFinder.FindPlayerTransform();
                if (player != null)
                {
                    _savestateSystem.SaveState(_gameObjectFinder, _timeController.CurrentTick, false);
                }
            }

            // Load Position (Manual)
            if (isTeleportPressed && !_wasTeleportPressed && !isSavePressed)
            {
                if (_savestateSystem.HasSavedState)
                {
                    _savestateSystem.LoadState(_gameObjectFinder, _timeController, false);
                    Physics.SyncTransforms();
                }
            }

            // Record Macro
            if (isRecordPressed && !_wasRecordPressed)
            {
                if (_macroSystem.IsRecording)
                {
                    _macroSystem.StopRecording();
                }
                else
                {
                    var player = _gameObjectFinder.FindPlayerTransform();
                    if (player != null)
                    {
                        _savestateSystem.SaveState(_gameObjectFinder, _timeController.CurrentTick, true);
                        _macroSystem.StartRecording();
                    }
                }
            }

            // Play Macro
            if (isPlayPressed && !_wasPlayPressed)
            {
                if (_macroSystem.IsPlaying)
                {
                    StopPlayback();
                }
                else if (_macroSystem.HasRecordedData)
                {
                    _savestateSystem.LoadState(_gameObjectFinder, _timeController, true);
                    Physics.SyncTransforms();
                    StartPlayback();
                }
            }

            // ── TAS Playback Controls ──────────────────────────────────────
            bool isPausePressed       = TASConfig.Settings.Pause.IsPressed();
            bool isSlowMoPressed      = TASConfig.Settings.SlowMo.IsPressed();
            bool isFrameAdvancePressed = TASConfig.Settings.FrameAdvance.IsPressed();

            // Pause / Unpause
            if (isPausePressed && !_wasPausePressed)
                _timeController.TogglePause();

            // Toggle Slow-Motion
            if (isSlowMoPressed && !_wasSlowMoPressed)
                _timeController.ToggleSlowMo();

            // SlowMo Boost — held key makes slow-mo run 3× faster (0.3× instead of 0.1×)
            if (_timeController.IsSlowMo)
                _timeController.SetSlowMoBoost(TASConfig.Settings.SlowMoBoost.IsPressed());

            // Frame-Advance: 1 tick on press, 10/sec when held
            if (isFrameAdvancePressed)
                _timeController.TickFrameAdvance(justPressed: !_wasFrameAdvancePressed);

            _wasTeleportPressed   = isTeleportPressed;
            _wasSavePressed       = isSavePressed;
            _wasRecordPressed     = isRecordPressed;
            _wasPlayPressed       = isPlayPressed;
            _wasMenuPressed       = isMenuPressed;
            _wasPausePressed      = isPausePressed;
            _wasSlowMoPressed     = isSlowMoPressed;
            _wasFrameAdvancePressed = isFrameAdvancePressed;
        }
        
        private void StartPlayback()
        {
            _cachedRb = _gameObjectFinder.GetCachedPlayerRigidbody();
            if (_cachedRb != null)
            {
                // Enable interpolation so Unity smoothly renders the rigidbody's position
                // between physics ticks (50Hz physics → 60–144Hz rendering).
                _originalInterpolation = _cachedRb.interpolation;
                _cachedRb.interpolation = RigidbodyInterpolation.Interpolate;
            }
            _macroSystem.StartPlaying();
            ToggleCinemachine(false);
            TASPlugin.Logger.LogInfo("TAS: Playback started");
        }

        private void StopPlayback()
        {
            _macroSystem.StopPlaying();
            if (_cachedRb != null)
            {
                _cachedRb.interpolation = _originalInterpolation;
            }
            ToggleCinemachine(true);
            TASPlugin.Logger.LogInfo("TAS: Playback stopped");
        }

        private void ToggleCinemachine(bool state)
        {
            try
            {
                if (Camera.main != null)
                {
                    var brain = Camera.main.GetComponent<Unity.Cinemachine.CinemachineBrain>();
                    if (brain != null)
                        brain.enabled = state;
                }
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error toggling Cinemachine: {ex}");
            }
        }
        
        private void UpdateCurrentPosition()
        {
            if (_cachedRb != null)
                _currentPosition = _cachedRb.position;
            else
            {
                var t = _gameObjectFinder.FindPlayerTransform();
                if (t != null) _currentPosition = t.position;
            }
        }

        private void UpdateOverlay()
        {
            float speed = _cachedRb != null ? _cachedRb.linearVelocity.magnitude : 0f;

            _overlayRenderer.UpdateData(
                _currentPosition,
                speed,
                _savestateSystem.HasSavedState,
                _macroSystem.IsRecording,
                _macroSystem.IsPlaying,
                _timeController.IsPaused,
                _timeController.CurrentTick,
                _timeController.IsSlowMo,
                _timeController.IsSlowMoBoost
            );
        }
        
        private void ApplyDeterministicSettings()
        {
            try
            {
                // Lock physics accumulator to prevent frame drop variations
                Time.maximumDeltaTime = Time.fixedDeltaTime;
                TASPlugin.Logger.LogInfo($"Set maximumDeltaTime to {Time.maximumDeltaTime}");
                
                // Note: We reverted the InputSystem updateMode override because forcing it
                // to ProcessEventsInFixedUpdate breaks mouse sensitivity accumulation and UI events (like ESC).
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error applying deterministic settings: {ex}");
            }
        }
        
        /// <summary>
        /// Subscribes to FishNet's TimeManager physics events.
        /// FishNet (when using PhysicsMode.TimeManager) runs physicsScene.Simulate() inside its
        /// own tick loop BEFORE Unity's FixedUpdate. Hooking OnPrePhysicsSimulation ensures our
        /// velocity injection arrives before the physics step, not after.
        /// </summary>
        private void SubscribeToFishNet()
        {
            try
            {
                var timeManager = FishNet.InstanceFinder.TimeManager;
                if (timeManager != null)
                {
                    // Unsubscribe from old one if it still exists
                    if (_fishNetTimeManager != null && _prePhysicsDelegate != null)
                    {
                        try { _fishNetTimeManager.OnPrePhysicsSimulation -= _prePhysicsDelegate; } catch { }
                        try { _fishNetTimeManager.OnPostTick -= _postTickDelegate; } catch { }
                    }

                    _fishNetTimeManager = timeManager;

                    // Hook BEFORE physics: inject velocity so PhysX simulates with correct momentum
                    _prePhysicsDelegate = (Il2CppSystem.Action<float>)((float delta) => OnPrePhysicsSimulation(delta));
                    timeManager.OnPrePhysicsSimulation += _prePhysicsDelegate;

                    // Hook AFTER full tick (after Reconcile): fallback to correct position if it drifted
                    _postTickDelegate = (Il2CppSystem.Action)(() => OnPostTick());
                    timeManager.OnPostTick += _postTickDelegate;

                    TASPlugin.Logger.LogInfo("Subscribed to FishNet TimeManager pre/post hooks");
                }
                else
                {
                    TASPlugin.Logger.LogWarning("FishNet TimeManager not found — velocity injection will use FixedUpdate fallback");
                }
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogWarning($"Could not subscribe to FishNet TimeManager (game may not be in network session yet): {ex.Message}");
            }
        }
        
        private void UnsubscribeFromFishNet()
        {
            try
            {
                if (_fishNetTimeManager != null)
                {
                    if (_prePhysicsDelegate != null)
                        try { _fishNetTimeManager.OnPrePhysicsSimulation -= _prePhysicsDelegate; } catch { }
                    
                    if (_postTickDelegate != null)
                        try { _fishNetTimeManager.OnPostTick -= _postTickDelegate; } catch { }
                        
                    TASPlugin.Logger.LogInfo("Unsubscribed from FishNet TimeManager");
                }
            }
            catch { }
            finally
            {
                _fishNetTimeManager = null;
                _prePhysicsDelegate = null;
                _postTickDelegate = null;
            }
        }

        public void Destroy()
        {
            try
            {
                UnsubscribeFromFishNet();
                ToggleCinemachine(true);
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error in TASController.Destroy: {ex}");
            }
        }
    }
}
