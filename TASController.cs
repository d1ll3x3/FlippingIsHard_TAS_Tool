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
        private bool _wasEditModePressed = false;
        private bool _wasRewindPressed = false;
        private float _lastRewindTime = 0f;

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
        
        // Camera override state — used when loading a savestate.
        // EXACT same approach as macros: disable CinemachineBrain, then write
        // Camera.main.transform directly every frame for N frames, then re-enable Brain.
        private bool _cameraOverrideActive = false;
        private int _cameraOverrideFramesLeft = 0;
        private Unity.Cinemachine.CinemachineCamera _overrideCinCam;
        private Unity.Cinemachine.CinemachineInputAxisController _overrideInputAxisCtrl;
        private SavestateSystem.SaveStateData _overrideCameraState; // custom state for edit mode
        private const int CAMERA_OVERRIDE_FRAMES = 5;
        
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
                _overlayRenderer = new OverlayRenderer();
                _overlayRenderer.RefreshKeybinds();
                
                _timeController = new TimeController();
                _savestateSystem = new SavestateSystem();
                _macroSystem = new InputMacroSystem();
                
                _bindMenu = bindMenu;
                _bindMenu.SetMacroSystem(_macroSystem);
                _bindMenu.OnImportPlayMacro = () => {
                    if (_macroSystem.HasRecordedData)
                    {
                        _savestateSystem.LoadState(_gameObjectFinder, _timeController, true);
                        Physics.SyncTransforms();
                        StartPlayback();
                    }
                };
                GameInputPatch.MacroSystem = _macroSystem;
                FishNetReconcilePatch.MacroSystem = _macroSystem;
                
                ApplyDeterministicSettings();
                
                // Hook FishNet TimeManager so we can inject velocity BEFORE the physics step.
                // FishNet runs its physics simulation BEFORE Unity's FixedUpdate, so any
                // velocity we write in FixedUpdate arrives one frame late and gets overridden
                // by FishNet's Reconcile. Hooking OnPrePhysicsSimulation fixes this.
                SubscribeToFishNet();
                
                UnityEngine.SceneManagement.SceneManager.add_sceneLoaded(
                    new System.Action<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.LoadSceneMode>(OnSceneLoaded)
                );
                
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
                
                // Ensure we catch situations where _fishNetTimeManager was destroyed by a scene reload
                if (_isInGame && _fishNetTimeManager != null && _fishNetTimeManager.gameObject == null)
                {
                    TASPlugin.Logger.LogInfo("TAS: TimeManager destroyed (QuickRestart detected in Update).");
                    ResetTrainer();
                    _fishNetTimeManager = null;
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

                // Camera override for savestate load.
                // EXACT same approach as macro playback: Brain is disabled,
                // we write Camera.main.transform directly every frame.
                if (_cameraOverrideActive)
                {
                    SavestateSystem.SaveStateData state = _overrideCameraState ?? _savestateSystem.GetLastLoadedState();
                    if (state != null && Camera.main != null)
                    {
                        Camera.main.transform.position = state.CameraPosition;
                        Camera.main.transform.rotation = state.CameraRotation;
                    }
                    
                    _cameraOverrideFramesLeft--;
                    if (_cameraOverrideFramesLeft <= 0)
                    {
                        FinalizeCameraRestore();
                    }
                }

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
                        
                        // Read current orbital axes (during normal gameplay, they match camera)
                        float camPan = 0f, camTilt = 0f;
                        var movement = playerTransform?.GetComponent<EHS.PlayerMovement>();
                        if (movement != null && movement.camManager != null)
                        {
                            var cinCam = movement.camManager.MainCinemachineCamera;
                            if (cinCam != null)
                            {
                                var orbital = cinCam.GetComponent<Unity.Cinemachine.CinemachineOrbitalFollow>();
                                if (orbital != null)
                                {
                                    camPan = orbital.HorizontalAxis.Value;
                                    camTilt = orbital.VerticalAxis.Value;
                                }
                            }
                        }
                        
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
                            _cachedRb != null ? _cachedRb.angularVelocity : Vector3.zero,
                            camPan, camTilt
                        );
                        _macroSystem.RecordTick(_timeController.CurrentTick, state);
                    }
                }

                if (_macroSystem.IsPlaying)
                {
                    if (_timeController.CurrentTick > _macroSystem.MaxTick)
                    {
                        // Playback ended naturally — save camera snapshot before stopping
                        // so the camera stays at the final position (same restore as savestate).
                        SavestateSystem.SaveStateData endSnap = CaptureCurrentCameraState();
                        StopPlayback();
                        if (endSnap != null)
                            StartCameraRestoreFromSnapshot(endSnap);
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
                        
                        // Inject orbital axes from macro data so they stay synced with camera
                        InjectPlaybackAxes();
                    }
                }
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error in TASController.FixedUpdate: {ex}");
            }
        }

        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            try
            {
                if (scene.name == "Scene_Game_NW-DemoLive")
                {
                    TASPlugin.Logger.LogInfo("TAS: Scene Loaded (QuickRestart), resetting trainer.");
                    ResetTrainer();
                }
            }
            catch (Exception ex) {
                TASPlugin.Logger.LogError($"Error in OnSceneLoaded: {ex}");
            }
        }

        private void ResetTrainer()
        {
            _timeController?.ResetTick();
            _macroSystem?.Clear();
            _savestateSystem?.Clear();
            _gameObjectFinder?.ClearCache();
            _cachedRb = null;
            _fishNetTimeManager = null;
            ApplyDeterministicSettings();
            
            // Force pause on level start/reset so the user has to manually unpause to start the TAS
            if (_timeController != null && !_timeController.IsPaused)
            {
                _timeController.TogglePause();
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
            // Keep writing camera transform during override so FishNet reconcile
            // doesn't have a chance to trigger camera recalculation with stale data.
            if (_cameraOverrideActive)
            {
                SavestateSystem.SaveStateData state = _overrideCameraState ?? _savestateSystem.GetLastLoadedState();
                if (state != null && Camera.main != null)
                {
                    Camera.main.transform.position = state.CameraPosition;
                    Camera.main.transform.rotation = state.CameraRotation;
                }
            }
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
            bool isShiftPressed = UnityEngine.Input.GetKey(UnityEngine.KeyCode.LeftShift) || UnityEngine.Input.GetKey(UnityEngine.KeyCode.RightShift);
            
            if (isTeleportPressed && !_wasTeleportPressed && !isSavePressed && !isShiftPressed)
            {
                if (_savestateSystem.HasSavedState)
                {
                    _savestateSystem.LoadState(_gameObjectFinder, _timeController, false);
                    Physics.SyncTransforms();
                    StartCameraRestore(_gameObjectFinder);
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
                    SavestateSystem.SaveStateData endSnap = CaptureCurrentCameraState();
                    StopPlayback();
                    if (endSnap != null)
                        StartCameraRestoreFromSnapshot(endSnap);
                }
                else if (_macroSystem.HasRecordedData)
                {
                    _savestateSystem.LoadState(_gameObjectFinder, _timeController, true);
                    Physics.SyncTransforms();
                    // Macro playback disables Brain itself via ToggleCinemachine(false)
                    // in StartPlayback(), so no camera override needed here.
                    StartPlayback();
                }
            }

            // ── Edit Macro (F8): cut playback at current tick and start re-recording ──
            bool isEditModePressed = TASConfig.Settings.EditMacro.IsPressed();
            
            if (isEditModePressed && !_wasEditModePressed)
            {
                if (_macroSystem.IsPlaying)
                {
                    // Auto-pause if not already paused
                    if (!_timeController.IsPaused)
                        _timeController.TogglePause();
                    
                    // Simplest possible: just stop playback and start recording.
                    // InjectPlaybackAxes keeps orbital axes synced during playback.
                    StopPlayback();
                    _macroSystem.EnterEditMode(_timeController.CurrentTick);
                    
                    TASPlugin.Logger.LogInfo($"TAS: Edit Mode ON at tick {_timeController.CurrentTick}");
                }
                else if (_macroSystem.IsEditMode)
                {
                    // EXIT Edit Mode: stop recording
                    _macroSystem.ExitEditMode();
                    TASPlugin.Logger.LogInfo("TAS: Edit Mode OFF — macro updated.");
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

            // ── Rewind: go back 1 tick (press) or 10/sec (hold) during paused replay ONLY ──
            bool isRewindPressed = TASConfig.Settings.RewindTick.IsPressed();
            if (isRewindPressed && _timeController.IsPaused && _macroSystem.IsPlaying && !_macroSystem.IsEditMode && _timeController.CurrentTick > 0)
            {
                // Only rewind on press, not hold — or do 10/sec like frame advance
                bool shouldRewind = !_wasRewindPressed; // first press
                if (!shouldRewind)
                {
                    // Hold repeat: 10/sec
                    float now = Time.unscaledTime;
                    if (now - _lastRewindTime >= 0.1f)
                    {
                        shouldRewind = true;
                        _lastRewindTime = now;
                    }
                }
                else
                {
                    _lastRewindTime = Time.unscaledTime;
                }
                
                if (shouldRewind)
                {
                    RewindOneTick();
                }
            }

            _wasTeleportPressed   = isTeleportPressed;
            _wasSavePressed       = isSavePressed;
            _wasRecordPressed     = isRecordPressed;
            _wasPlayPressed       = isPlayPressed;
            _wasMenuPressed       = isMenuPressed;
            _wasPausePressed      = isPausePressed;
            _wasSlowMoPressed     = isSlowMoPressed;
            _wasFrameAdvancePressed = isFrameAdvancePressed;
            _wasEditModePressed = isEditModePressed;
            _wasRewindPressed = isRewindPressed;
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
        
        /// <summary>
        /// EXACT SAME approach as macros: disable CinemachineBrain, then write
        /// Camera.main.transform directly every frame. This bypasses ALL Cinemachine
        /// internals (PanTilt, InputAxisController, damping, etc.) — same as macro playback.
        /// ALSO injects the saved orbital axis values into CinemachineOrbitalFollow
        /// so that when Brain re-enables, it computes the correct rotation.
        /// </summary>
        private void StartCameraRestore(GameObjectFinder finder)
        {
            try
            {
                SavestateSystem.SaveStateData state = _savestateSystem.GetLastLoadedState();
                if (state == null) return;
                
                // ── Step 1: Disable CinemachineBrain (exact same as macros) ──
                ToggleCinemachine(false);
                
                // ── Step 2: Inject saved orbital angles into CinemachineOrbitalFollow ──
                //    This ensures that when Brain re-enables, Cinemachine's internal
                //    state matches the camera transform we're about to write.
                InjectOrbitalAxes(state, finder);
                
                // ── Step 3: Write camera transform DIRECTLY (exact same as macros) ──
                if (Camera.main != null)
                {
                    Camera.main.transform.position = state.CameraPosition;
                    Camera.main.transform.rotation = state.CameraRotation;
                }
                
                // ── Step 4: Also disable InputAxisController so it doesn't
                //    accumulate mouse input while brain is disabled ──
                var playerTransform = finder.FindPlayerTransform();
                if (playerTransform != null)
                {
                    var movement = playerTransform.GetComponent<EHS.PlayerMovement>();
                    if (movement != null && movement.camManager != null)
                    {
                        var cinCam = movement.camManager.MainCinemachineCamera;
                        if (cinCam != null)
                        {
                            _overrideCinCam = cinCam;
                            
                            var axisCtrl = cinCam.GetComponent<Unity.Cinemachine.CinemachineInputAxisController>();
                            if (axisCtrl == null && movement.camManager.axisSettingsSync != null)
                                axisCtrl = movement.camManager.axisSettingsSync.axisController;
                            
                            if (axisCtrl != null)
                            {
                                axisCtrl.enabled = false;
                                _overrideInputAxisCtrl = axisCtrl;
                                TASPlugin.Logger.LogInfo($"[CamRestore] Brain DISABLED, orbital axes injected. Writing camera transform for {CAMERA_OVERRIDE_FRAMES} frames.");
                            }
                            else
                            {
                                TASPlugin.Logger.LogWarning("[CamRestore] Brain DISABLED but InputAxisController not found.");
                            }
                        }
                    }
                }
                
                _cameraOverrideFramesLeft = CAMERA_OVERRIDE_FRAMES;
                _cameraOverrideActive = true;
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error starting camera restore: {ex}");
                ToggleCinemachine(true); // re-enable brain on error
                _cameraOverrideActive = false;
            }
        }
        
        private void FinalizeCameraRestore()
        {
            try
            {
                _cameraOverrideActive = false;
                _overrideCameraState = null;
                
                // Clear damping history so Cinemachine starts fresh from current camera state
                if (_overrideCinCam != null)
                {
                    _overrideCinCam.PreviousStateIsValid = false;
                    
                    // Try ForceCameraPosition (Cinemachine 3.x) via reflection
                    try
                    {
                        var method = _overrideCinCam.GetType().GetMethod("ForceCameraPosition",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        if (method != null && Camera.main != null)
                        {
                            method.Invoke(_overrideCinCam, new object[] { Camera.main.transform.position, Camera.main.transform.rotation });
                            TASPlugin.Logger.LogInfo("[CamRestore] ForceCameraPosition called successfully.");
                        }
                    }
                    catch { }
                }
                
                // Re-enable InputAxisController
                if (_overrideInputAxisCtrl != null)
                    _overrideInputAxisCtrl.enabled = true;
                
                // Re-enable CinemachineBrain LAST (exact same pattern as StopPlayback)
                ToggleCinemachine(true);
                
                TASPlugin.Logger.LogInfo("[CamRestore] Finalized — Brain re-enabled, Cinemachine takes over now.");
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error finalizing camera restore: {ex}");
                if (_overrideInputAxisCtrl != null)
                    _overrideInputAxisCtrl.enabled = true;
                ToggleCinemachine(true);
                _cameraOverrideActive = false;
            }
        }
        
        /// <summary>
        /// Injects saved pan/tilt values into CinemachineOrbitalFollow's
        /// HorizontalAxis and VerticalAxis. This ensures Cinemachine's internal
        /// state matches our saved camera rotation when Brain re-enables.
        /// </summary>
        private void InjectOrbitalAxes(SavestateSystem.SaveStateData state, GameObjectFinder finder)
        {
            try
            {
                var playerTransform = finder.FindPlayerTransform();
                if (playerTransform == null) return;
                
                var movement = playerTransform.GetComponent<EHS.PlayerMovement>();
                if (movement == null || movement.camManager == null) return;
                
                var cinCam = movement.camManager.MainCinemachineCamera;
                if (cinCam == null) return;
                
                var orbital = cinCam.GetComponent<Unity.Cinemachine.CinemachineOrbitalFollow>();
                if (orbital != null)
                {
                    var hAxis = orbital.HorizontalAxis;
                    hAxis.Value = state.CameraPan;
                    orbital.HorizontalAxis = hAxis;
                    
                    var vAxis = orbital.VerticalAxis;
                    vAxis.Value = state.CameraTilt;
                    orbital.VerticalAxis = vAxis;
                    
                    TASPlugin.Logger.LogInfo($"[CamRestore] Injected orbital axes: pan={state.CameraPan} tilt={state.CameraTilt}");
                }
                else
                {
                    // Fallback: try CinemachinePanTilt
                    var panTilt = cinCam.GetComponent<Unity.Cinemachine.CinemachinePanTilt>();
                    if (panTilt != null)
                    {
                        var pAxis = panTilt.PanAxis;
                        pAxis.Value = state.CameraPan;
                        panTilt.PanAxis = pAxis;
                        
                        var tAxis = panTilt.TiltAxis;
                        tAxis.Value = state.CameraTilt;
                        panTilt.TiltAxis = tAxis;
                        
                        TASPlugin.Logger.LogInfo($"[CamRestore] Injected PanTilt axes: pan={state.CameraPan} tilt={state.CameraTilt}");
                    }
                    else
                    {
                        TASPlugin.Logger.LogWarning("[CamRestore] No orbital or PanTilt component found for axis injection!");
                    }
                }
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error injecting orbital axes: {ex}");
            }
        }
        
        /// <summary>
        /// Captures current Camera.main transform + orbital axis values into a SaveStateData.
        /// Used when entering Edit Mode to snapshot the camera before stopping playback.
        /// </summary>
        /// <summary>
        /// Rewinds one tick during paused replay by loading the recorded physics state
        /// from the macro data at (currentTick - 1).
        /// </summary>
        private void RewindOneTick()
        {
            try
            {
                if (_cachedRb == null) _cachedRb = _gameObjectFinder.GetCachedPlayerRigidbody();
                if (_cachedRb == null) return;
                
                ulong targetTick = _timeController.CurrentTick - 1;
                var state = _macroSystem.GetStateAtTick(targetTick);
                if (state == null) return;
                
                // Apply physics state
                _cachedRb.position = state.Value.PlayerPosition;
                _cachedRb.rotation = state.Value.PlayerRotation;
                _cachedRb.linearVelocity = state.Value.PlayerVelocity;
                _cachedRb.angularVelocity = state.Value.PlayerAngularVelocity;
                
                var playerTransform = _gameObjectFinder.FindPlayerTransform();
                if (playerTransform != null)
                {
                    playerTransform.position = state.Value.PlayerPosition;
                    playerTransform.rotation = state.Value.PlayerRotation;
                }
                Physics.SyncTransforms();
                
                // Update tick
                _timeController.SetTick(targetTick);
                
                // If replaying, update macro playback state so next frame-advance picks up correctly
                if (_macroSystem.IsPlaying)
                {
                    _macroSystem.PlaybackTick(targetTick);
                }
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error rewinding tick: {ex}");
            }
        }
        
        /// <summary>
        /// Computes HorizontalAxis and VerticalAxis values for CinemachineOrbitalFollow
        /// from the actual camera world position relative to the Follow target.
        /// Accounts for TargetOffset and ShoulderOffset to ensure the computed axes
        /// reproduce the exact camera position.
        /// <summary>
        /// Injects orbital axes from the current macro playback state into CinemachineOrbitalFollow.
        /// Keeps axes in perfect sync with Camera.main.transform during playback.
        /// </summary>
        private void InjectPlaybackAxes()
        {
            try
            {
                var playerTransform = _gameObjectFinder.FindPlayerTransform();
                if (playerTransform == null) return;
                var movement = playerTransform.GetComponent<EHS.PlayerMovement>();
                if (movement == null || movement.camManager == null) return;
                var cinCam = movement.camManager.MainCinemachineCamera;
                if (cinCam == null) return;
                var orbital = cinCam.GetComponent<Unity.Cinemachine.CinemachineOrbitalFollow>();
                if (orbital == null) return;
                
                var hAxis = orbital.HorizontalAxis;
                hAxis.Value = _macroSystem.GetCurrentCameraPan();
                orbital.HorizontalAxis = hAxis;
                
                var vAxis = orbital.VerticalAxis;
                vAxis.Value = _macroSystem.GetCurrentCameraTilt();
                orbital.VerticalAxis = vAxis;
            }
            catch { }
        }
        
        /// <summary>
        /// Captures current Camera.main.transform + orbital axes into a SaveStateData.
        /// </summary>
        private SavestateSystem.SaveStateData CaptureCurrentCameraState()
        {
            try
            {
                Quaternion camRot = Camera.main != null ? Camera.main.transform.rotation : Quaternion.identity;
                Vector3 camPos = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
                float pan = 0f, tilt = 0f;
                
                var playerTransform = _gameObjectFinder.FindPlayerTransform();
                if (playerTransform != null)
                {
                    var movement = playerTransform.GetComponent<EHS.PlayerMovement>();
                    if (movement != null && movement.camManager != null)
                    {
                        var cinCam = movement.camManager.MainCinemachineCamera;
                        if (cinCam != null)
                        {
                            var orbital = cinCam.GetComponent<Unity.Cinemachine.CinemachineOrbitalFollow>();
                            if (orbital != null)
                            {
                                pan = orbital.HorizontalAxis.Value;
                                tilt = orbital.VerticalAxis.Value;
                            }
                        }
                    }
                }
                return new SavestateSystem.SaveStateData(
                    Vector3.zero, Quaternion.identity, Vector3.zero, Vector3.zero,
                    camRot, camPos, pan, tilt);
            }
            catch { return null; }
        }
        
        /// <summary>
        /// Starts camera override using a snapshot, same as StartCameraRestore but
        /// using the given state instead of the savestate system.
        /// </summary>
        private void StartCameraRestoreFromSnapshot(SavestateSystem.SaveStateData state)
        {
            try
            {
                if (state == null) return;
                ToggleCinemachine(false);
                InjectOrbitalAxes(state, _gameObjectFinder);
                if (Camera.main != null)
                {
                    Camera.main.transform.position = state.CameraPosition;
                    Camera.main.transform.rotation = state.CameraRotation;
                }
                
                // Find cinCam for FinalizeCameraRestore
                var playerTransform = _gameObjectFinder.FindPlayerTransform();
                if (playerTransform != null)
                {
                    var movement = playerTransform.GetComponent<EHS.PlayerMovement>();
                    if (movement != null && movement.camManager != null)
                    {
                        _overrideCinCam = movement.camManager.MainCinemachineCamera;
                        var axisCtrl = _overrideCinCam?.GetComponent<Unity.Cinemachine.CinemachineInputAxisController>();
                        if (axisCtrl == null && movement.camManager.axisSettingsSync != null)
                            axisCtrl = movement.camManager.axisSettingsSync.axisController;
                        if (axisCtrl != null)
                        {
                            axisCtrl.enabled = false;
                            _overrideInputAxisCtrl = axisCtrl;
                        }
                    }
                }
                
                _cameraOverrideFramesLeft = CAMERA_OVERRIDE_FRAMES;
                _cameraOverrideActive = true;
                _overrideCameraState = state;
            }
            catch { }
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
                _timeController.IsSlowMoBoost,
                _macroSystem.IsEditMode
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
