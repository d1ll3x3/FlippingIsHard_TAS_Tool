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
        
        // Orbital damping backup (for instant camera transitions)
        private float _originalHDamping = -1f;
        private float _originalVDamping = -1f;
        private bool _shouldRestoreDamping = false;
        
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
                        // For imported macros, ensure macro state exists (save current pos if not set)
                        if (!_savestateSystem.HasMacroState)
                            _savestateSystem.SaveState(_gameObjectFinder, _timeController.CurrentTick, true);
                        _savestateSystem.LoadState(_gameObjectFinder, _timeController, true);
                        Physics.SyncTransforms();
                        
                        // Get the STARTING tick from the savestate
                        ulong startTick = _savestateSystem.MacroTick;
                        
                        // Start playback system
                        _macroSystem.StartPlaying();
                        
                        // Load the FIRST RECORDED TICK from the macro
                        _macroSystem.PlaybackTick(startTick);
                        
                        // Disable damping for instant camera snap
                        DisableOrbitalDamping();
                        
                        // Inject camera axes from the first recorded tick (NOT from savestate)
                        var firstTickState = _macroSystem.GetStateAtTick(startTick);
                        if (firstTickState.HasValue)
                        {
                            TASPlugin.Logger.LogInfo($"[Import] Auto-playing from tick {startTick}");
                            InjectAxesFromState(firstTickState.Value);
                            ForceCinemachineUpdate();
                        }
                        else
                        {
                            TASPlugin.Logger.LogWarning($"[Import] No macro data at start tick {startTick}!");
                        }
                        
                        // Restore damping after 1 frame
                        _shouldRestoreDamping = true;
                        
                        // Enable Rigidbody interpolation
                        _cachedRb = _gameObjectFinder.GetCachedPlayerRigidbody();
                        if (_cachedRb != null)
                        {
                            _originalInterpolation = _cachedRb.interpolation;
                            _cachedRb.interpolation = RigidbodyInterpolation.Interpolate;
                        }
                        
                        TASPlugin.Logger.LogInfo("TAS: Playback started (Brain remains active, axes-only mode)");
                        _bindMenu.RequestClose();
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
                
                // Deferred damping restore (after 1 frame)
                if (_shouldRestoreDamping)
                {
                    RestoreOrbitalDamping();
                    _shouldRestoreDamping = false;
                }
                
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
                    // Quick restart handled silently
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

                // Camera override during playback: Brain is DISABLED, we write Camera.main.transform directly
                if (_macroSystem != null && _macroSystem.IsPlaying && Camera.main != null)
                {
                    // When PAUSED, always show the exact camera state of the current tick (no interpolation)
                    if (_timeController.IsPaused)
                    {
                        Quaternion rot = _macroSystem.GetCurrentCameraRotation();
                        rot.Normalize();
                        Vector3 pos = _macroSystem.GetCurrentCameraPosition();
                        
                        Camera.main.transform.rotation = rot;
                        Camera.main.transform.position = pos;
                    }
                    else
                    {
                        // When PLAYING, interpolate between ticks for smooth 60fps camera
                        float t = (Time.time - Time.fixedTime) / Time.fixedDeltaTime;
                        Quaternion rot = _macroSystem.GetInterpolatedCameraRotation(t);
                        rot.Normalize();
                        Camera.main.transform.rotation = rot;
                        Camera.main.transform.position = _macroSystem.GetInterpolatedCameraPosition(t);
                    }
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
                        StopPlayback();
                    }
                    else
                    {
                        // Advance the playback state for this tick.
                        // Velocity injection happens in OnPrePhysicsSimulation (before physics).
                        _macroSystem.PlaybackTick(_timeController.CurrentTick);

                        if (Camera.main != null)
                        {
                            Quaternion rot = _macroSystem.GetCurrentCameraRotation();
                            rot.Normalize();
                            Camera.main.transform.rotation = rot;
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
            // Don't clear macro data OR savestate on quick restart — keep everything for replay
            if (_macroSystem != null)
            {
                if (_macroSystem.IsPlaying) _macroSystem.StopPlaying();
                if (_macroSystem.IsRecording) _macroSystem.StopRecording();
                if (_macroSystem.IsEditMode) _macroSystem.ExitEditMode();
            }
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
            // OPCIÓN 1: No camera override needed, Brain handles everything
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
                    
                    // Disable damping for instant camera snap
                    DisableOrbitalDamping();
                    
                    // Inject axes and force immediate Cinemachine update
                    var state = _savestateSystem.GetLastLoadedState();
                    if (state != null)
                    {
                        InjectOrbitalAxes(state, _gameObjectFinder);
                        ForceCinemachineUpdate();
                    }
                    
                    // Restore damping after 1 frame
                    _shouldRestoreDamping = true;
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
                    // Load physical state (position/velocity) from savestate
                    _savestateSystem.LoadState(_gameObjectFinder, _timeController, true);
                    Physics.SyncTransforms();
                    
                    // Get the STARTING tick from the savestate
                    ulong startTick = _savestateSystem.MacroTick;
                    
                    // Start playback system
                    _macroSystem.StartPlaying();
                    
                    // Load the FIRST RECORDED TICK from the macro
                    _macroSystem.PlaybackTick(startTick);
                    
                    // Disable damping for instant camera snap
                    DisableOrbitalDamping();
                    
                    // Inject camera axes from the first recorded tick (NOT from savestate)
                    var firstTickState = _macroSystem.GetStateAtTick(startTick);
                    if (firstTickState.HasValue)
                    {
                        InjectAxesFromState(firstTickState.Value);
                        ForceCinemachineUpdate();
                    }
                    else
                    {
                        TASPlugin.Logger.LogWarning($"[F10] No macro data at start tick {startTick}!");
                    }
                    
                    // Restore damping after 1 frame
                    _shouldRestoreDamping = true;
                    
                    // Enable Rigidbody interpolation
                    _cachedRb = _gameObjectFinder.GetCachedPlayerRigidbody();
                    if (_cachedRb != null)
                    {
                        _originalInterpolation = _cachedRb.interpolation;
                        _cachedRb.interpolation = RigidbodyInterpolation.Interpolate;
                    }
                    
                    TASPlugin.Logger.LogInfo("TAS: Playback started (Brain remains active, axes-only mode)");
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
                    
                    ulong cutTick = _timeController.CurrentTick;
                    
                    // Stop playback and enter edit mode (preserves cutTick, deletes after)
                    StopPlayback();
                    _macroSystem.EnterEditMode(cutTick);
                    
                    TASPlugin.Logger.LogInfo($"TAS: Edit Mode ON at tick {cutTick}");
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
            // Sync orbital axes from last macro tick before re-enabling Brain
            InjectPlaybackAxes();
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
        /// Injects saved pan/tilt values into CinemachineOrbitalFollow's
        /// HorizontalAxis and VerticalAxis. This ensures Cinemachine's internal
        /// state matches our saved camera rotation.
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
                    }
                    else
                    {
                        TASPlugin.Logger.LogError($"[InjectAxes] No orbital or PanTilt component found!");
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
                
                // Also restore camera to the rewinded tick's position
                if (Camera.main != null)
                {
                    Camera.main.transform.position = state.Value.CameraPosition;
                    Quaternion camRot = state.Value.CameraRotation;
                    camRot.Normalize();
                    Camera.main.transform.rotation = camRot;
                }
                
                // Reset Cinemachine damping so camera snaps instantly to rewound position
                ResetCinemachineDamping();
                
                // Restore orbital axes to match
                InjectAxesFromState(state.Value);
                ForceCinemachineUpdate();
                
                // Update tick
                _timeController.SetTick(targetTick);
                
                // If replaying, update macro playback state so next frame-advance picks up correctly
                if (_macroSystem.IsPlaying)
                {
                    _macroSystem.PlaybackTick(targetTick);
                    // Second call: makes _previousPlaybackState == _currentPlaybackState
                    // so interpolation in Update() gives exactly the target tick
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
                float pan = _macroSystem.GetCurrentCameraPan();
                if (!float.IsNaN(pan) && !float.IsInfinity(pan))
                {
                    hAxis.Value = pan;
                    orbital.HorizontalAxis = hAxis;
                }
                
                var vAxis = orbital.VerticalAxis;
                float tilt = _macroSystem.GetCurrentCameraTilt();
                if (!float.IsNaN(tilt) && !float.IsInfinity(tilt))
                {
                    vAxis.Value = tilt;
                    orbital.VerticalAxis = vAxis;
                }
            }
            catch { }
        }
        
        /// <summary>
        /// Injects CameraPan/CameraTilt from a TASInputState into CinemachineOrbitalFollow.
        /// </summary>
        private void InjectAxesFromState(TASInputState state)
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
                
                float pan = state.CameraPan;
                float tilt = state.CameraTilt;
                if (!float.IsNaN(pan) && !float.IsInfinity(pan))
                {
                    var hAxis = orbital.HorizontalAxis;
                    hAxis.Value = pan;
                    orbital.HorizontalAxis = hAxis;
                }
                if (!float.IsNaN(tilt) && !float.IsInfinity(tilt))
                {
                    var vAxis = orbital.VerticalAxis;
                    vAxis.Value = tilt;
                    orbital.VerticalAxis = vAxis;
                }
            }
            catch { }
        }
        
        /// <summary>
        /// Forces CinemachineBrain to update immediately after axis injection,
        /// preventing 1-frame camera lag/flicker when loading savestates or starting playback.
        /// </summary>
        private void ForceCinemachineUpdate()
        {
            try
            {
                if (Camera.main == null) return;
                var brain = Camera.main.GetComponent<Unity.Cinemachine.CinemachineBrain>();
                if (brain == null) return;
                
                // Force brain to recalculate camera position immediately
                var playerTransform = _gameObjectFinder.FindPlayerTransform();
                if (playerTransform == null) return;
                var movement = playerTransform.GetComponent<EHS.PlayerMovement>();
                if (movement?.camManager?.MainCinemachineCamera == null) return;
                
                // Trigger a manual update by disabling and re-enabling
                // (ManualUpdate() doesn't exist in all Cinemachine versions)
                var cinCam = movement.camManager.MainCinemachineCamera;
                cinCam.enabled = false;
                cinCam.enabled = true;
            }
            catch { }
        }
        
        /// <summary>
        /// Temporarily disables orbital axis damping for instant camera response.
        /// Call before injecting axes, then set _shouldRestoreDamping flag after.
        /// </summary>
        private void DisableOrbitalDamping()
        {
            try
            {
                var playerTransform = _gameObjectFinder.FindPlayerTransform();
                if (playerTransform == null) return;
                var movement = playerTransform.GetComponent<EHS.PlayerMovement>();
                if (movement?.camManager?.MainCinemachineCamera == null) return;
                var orbital = movement.camManager.MainCinemachineCamera.GetComponent<Unity.Cinemachine.CinemachineOrbitalFollow>();
                if (orbital == null) return;
                
                // Backup original damping values (only once)
                if (_originalHDamping < 0f)
                {
                    var hAxis = orbital.HorizontalAxis;
                    var vAxis = orbital.VerticalAxis;
                    _originalHDamping = hAxis.Center;
                    _originalVDamping = vAxis.Center;
                }
                
                // Cinemachine Orbital doesn't expose damping directly - we can't disable it
                // Instead, we'll use ForceCinemachineUpdate() to force immediate recalculation
            }
            catch { }
        }
        
        /// <summary>
        /// Restores original orbital axis damping values.
        /// </summary>
        private void RestoreOrbitalDamping()
        {
            try
            {
                // Nothing to restore since we can't modify damping
                _originalHDamping = -1f;
                _originalVDamping = -1f;
            }
            catch { }
        }
        
        /// <summary>
        /// Resets Cinemachine damping so the next axis injection snaps instantly.
        /// </summary>
        private void ResetCinemachineDamping()
        {
            try
            {
                var playerTransform = _gameObjectFinder.FindPlayerTransform();
                if (playerTransform == null) return;
                var movement = playerTransform.GetComponent<EHS.PlayerMovement>();
                if (movement?.camManager?.MainCinemachineCamera == null) return;
                movement.camManager.MainCinemachineCamera.PreviousStateIsValid = false;
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
                }
            }
            catch { }
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
