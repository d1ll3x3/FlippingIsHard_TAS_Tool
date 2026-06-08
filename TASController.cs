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

        // Cached physics references
        private Rigidbody _cachedRb;
        private RigidbodyInterpolation _originalInterpolation;
        
        public bool enabled { get; set; }
        
        // FishNet TimeManager reference
        private FishNet.Managing.Timing.TimeManager _fishNetTimeManager;
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
                        if (!_savestateSystem.HasMacroState)
                            _savestateSystem.SaveState(_gameObjectFinder, _timeController.CurrentTick, true);
                        _savestateSystem.LoadState(_gameObjectFinder, _timeController, true);
                        Physics.SyncTransforms();
                        StartPlaybackWithAxes(_savestateSystem.MacroTick);
                        _bindMenu.RequestClose();
                    }
                };
                GameInputPatch.MacroSystem = _macroSystem;
                FishNetReconcilePatch.MacroSystem = _macroSystem;
                
                ApplyDeterministicSettings();
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
                        TASPlugin.Logger.LogInfo("TAS: Exited to menu, making mod dormant.");
                        _timeController?.ResetTick();
                        _macroSystem?.Clear();
                        _savestateSystem?.Clear();
                        _gameObjectFinder?.ClearCache();
                        _cachedRb = null;
                        if (_timeController != null)
                        {
                            if (_timeController.IsPaused) _timeController.TogglePause();
                            if (_timeController.IsSlowMo) _timeController.ToggleSlowMo();
                        }
                        UnsubscribeFromFishNet();
                    }
                    else if (_isInGame && !wasInGame)
                    {
                        TASPlugin.Logger.LogInfo("TAS: Entered game, waking up mod.");
                        _timeController?.ResetTick();
                        _macroSystem?.Clear();
                        _savestateSystem?.Clear();
                        _gameObjectFinder?.ClearCache();
                        _cachedRb = null;
                        ApplyDeterministicSettings();
                    }
                }
                
                if (_isInGame && _fishNetTimeManager != null && _fishNetTimeManager.gameObject == null)
                {
                    ResetTrainer();
                    _fishNetTimeManager = null;
                }

                if (!_isInGame) return;

                if (_fishNetTimeManager == null)
                {
                    try
                    {
                        if (FishNet.InstanceFinder.TimeManager != null)
                            SubscribeToFishNet();
                    }
                    catch { }
                }

                HandleHotkeys();

                if (_macroSystem != null && _macroSystem.IsPlaying && Camera.main != null)
                {
                    if (_timeController.IsPaused)
                    {
                        Quaternion rot = _macroSystem.GetCurrentCameraRotation();
                        rot.Normalize();
                        Camera.main.transform.rotation = rot;
                        Camera.main.transform.position = _macroSystem.GetCurrentCameraPosition();
                    }
                    else
                    {
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

                if (_cachedRb == null)
                    _cachedRb = _gameObjectFinder.GetCachedPlayerRigidbody();

                if (_macroSystem.IsRecording)
                {
                    var playerTransform = _gameObjectFinder.FindPlayerTransform();
                    var handler = playerTransform?.GetComponent<EHS.PlayerInputHandler>();
                    if (handler != null)
                    {
                        var rawData = handler.rawData;
                        
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
                        _macroSystem.PlaybackTick(_timeController.CurrentTick);

                        if (Camera.main != null)
                        {
                            Quaternion rot = _macroSystem.GetCurrentCameraRotation();
                            rot.Normalize();
                            Camera.main.transform.rotation = rot;
                            Camera.main.transform.position = _macroSystem.GetCurrentCameraPosition();
                        }
                        
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
            if (_timeController != null && !_timeController.IsPaused)
                _timeController.TogglePause();
        }

        private void OnPrePhysicsSimulation(float delta)
        {
            try
            {
                if (!enabled || !_isInGame) return;
                if (_cachedRb == null) return;
                if (_macroSystem == null || !_macroSystem.IsPlaying) return;

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

        private void OnPostTick() { }
        
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

            if (isMenuPressed && !_wasMenuPressed)
                _bindMenu.ToggleVisibility();

            if (isSavePressed && !_wasSavePressed)
            {
                var player = _gameObjectFinder.FindPlayerTransform();
                if (player != null)
                    _savestateSystem.SaveState(_gameObjectFinder, _timeController.CurrentTick, false);
            }

            bool isShiftPressed = UnityEngine.Input.GetKey(UnityEngine.KeyCode.LeftShift) || UnityEngine.Input.GetKey(UnityEngine.KeyCode.RightShift);
            
            if (isTeleportPressed && !_wasTeleportPressed && !isSavePressed && !isShiftPressed)
            {
                if (_savestateSystem.HasSavedState)
                {
                    _savestateSystem.LoadState(_gameObjectFinder, _timeController, false);
                    Physics.SyncTransforms();
                    ResetCinemachineDamping();
                    var state = _savestateSystem.GetLastLoadedState();
                    if (state != null)
                    {
                        InjectOrbitalAxes(state, _gameObjectFinder);
                        ForceCinemachineUpdate();
                    }
                }
            }

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
                    StartPlaybackWithAxes(_savestateSystem.MacroTick);
                }
            }

            bool isEditModePressed = TASConfig.Settings.EditMacro.IsPressed();
            
            if (isEditModePressed && !_wasEditModePressed)
            {
                if (_macroSystem.IsPlaying)
                {
                    if (!_timeController.IsPaused)
                        _timeController.TogglePause();
                    
                    ulong cutTick = _timeController.CurrentTick;
                    StopPlayback();
                    _macroSystem.EnterEditMode(cutTick);
                    
                    TASPlugin.Logger.LogInfo($"TAS: Edit Mode ON at tick {cutTick}");
                }
                else if (_macroSystem.IsEditMode)
                {
                    _macroSystem.ExitEditMode();
                    TASPlugin.Logger.LogInfo("TAS: Edit Mode OFF — macro updated.");
                }
            }

            bool isPausePressed       = TASConfig.Settings.Pause.IsPressed();
            bool isSlowMoPressed      = TASConfig.Settings.SlowMo.IsPressed();
            bool isFrameAdvancePressed = TASConfig.Settings.FrameAdvance.IsPressed();

            if (isPausePressed && !_wasPausePressed)
                _timeController.TogglePause();

            if (isSlowMoPressed && !_wasSlowMoPressed)
                _timeController.ToggleSlowMo();

            if (_timeController.IsSlowMo)
                _timeController.SetSlowMoBoost(TASConfig.Settings.SlowMoBoost.IsPressed());

            if (isFrameAdvancePressed)
                _timeController.TickFrameAdvance(justPressed: !_wasFrameAdvancePressed);

            bool isRewindPressed = TASConfig.Settings.RewindTick.IsPressed();
            if (isRewindPressed && _timeController.IsPaused && _macroSystem.IsPlaying && !_macroSystem.IsEditMode && _timeController.CurrentTick > 0)
            {
                bool shouldRewind = !_wasRewindPressed;
                if (!shouldRewind)
                {
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
                    RewindOneTick();
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
        
        private void StartPlaybackWithAxes(ulong startTick)
        {
            _cachedRb = _gameObjectFinder.GetCachedPlayerRigidbody();
            if (_cachedRb != null)
            {
                _originalInterpolation = _cachedRb.interpolation;
                _cachedRb.interpolation = RigidbodyInterpolation.Interpolate;
            }
            
            _macroSystem.StartPlaying();
            _macroSystem.PlaybackTick(startTick);
            ResetCinemachineDamping();
            
            var firstTickState = _macroSystem.GetStateAtTick(startTick);
            if (firstTickState.HasValue)
            {
                InjectAxesFromState(firstTickState.Value);
                ForceCinemachineUpdate();
            }
            
            TASPlugin.Logger.LogInfo("TAS: Playback started (axes-only mode)");
        }

        private void StopPlayback()
        {
            _macroSystem.StopPlaying();
            if (_cachedRb != null)
                _cachedRb.interpolation = _originalInterpolation;
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
                        TASPlugin.Logger.LogError("[InjectAxes] No orbital or PanTilt component found!");
                    }
                }
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error injecting orbital axes: {ex}");
            }
        }
        
        private void RewindOneTick()
        {
            try
            {
                if (_cachedRb == null) _cachedRb = _gameObjectFinder.GetCachedPlayerRigidbody();
                if (_cachedRb == null) return;
                
                ulong targetTick = _timeController.CurrentTick - 1;
                var state = _macroSystem.GetStateAtTick(targetTick);
                if (state == null) return;
                
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
                
                if (Camera.main != null)
                {
                    Camera.main.transform.position = state.Value.CameraPosition;
                    Quaternion camRot = state.Value.CameraRotation;
                    camRot.Normalize();
                    Camera.main.transform.rotation = camRot;
                }
                
                ResetCinemachineDamping();
                InjectAxesFromState(state.Value);
                ForceCinemachineUpdate();
                
                _timeController.SetTick(targetTick);
                
                if (_macroSystem.IsPlaying)
                {
                    _macroSystem.PlaybackTick(targetTick);
                    _macroSystem.PlaybackTick(targetTick);
                }
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error rewinding tick: {ex}");
            }
        }
        
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
                
                float pan = _macroSystem.GetCurrentCameraPan();
                if (!float.IsNaN(pan) && !float.IsInfinity(pan))
                {
                    var hAxis = orbital.HorizontalAxis;
                    hAxis.Value = pan;
                    orbital.HorizontalAxis = hAxis;
                }
                
                float tilt = _macroSystem.GetCurrentCameraTilt();
                if (!float.IsNaN(tilt) && !float.IsInfinity(tilt))
                {
                    var vAxis = orbital.VerticalAxis;
                    vAxis.Value = tilt;
                    orbital.VerticalAxis = vAxis;
                }
            }
            catch { }
        }
        
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
                if (!float.IsNaN(pan) && !float.IsInfinity(pan))
                {
                    var hAxis = orbital.HorizontalAxis;
                    hAxis.Value = pan;
                    orbital.HorizontalAxis = hAxis;
                }
                
                float tilt = state.CameraTilt;
                if (!float.IsNaN(tilt) && !float.IsInfinity(tilt))
                {
                    var vAxis = orbital.VerticalAxis;
                    vAxis.Value = tilt;
                    orbital.VerticalAxis = vAxis;
                }
            }
            catch { }
        }
        
        private void ForceCinemachineUpdate()
        {
            try
            {
                if (Camera.main == null) return;
                var brain = Camera.main.GetComponent<Unity.Cinemachine.CinemachineBrain>();
                if (brain == null) return;
                
                var playerTransform = _gameObjectFinder.FindPlayerTransform();
                if (playerTransform == null) return;
                var movement = playerTransform.GetComponent<EHS.PlayerMovement>();
                if (movement?.camManager?.MainCinemachineCamera == null) return;
                
                var cinCam = movement.camManager.MainCinemachineCamera;
                cinCam.enabled = false;
                cinCam.enabled = true;
            }
            catch { }
        }
        
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
                Time.maximumDeltaTime = Time.fixedDeltaTime;
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error applying deterministic settings: {ex}");
            }
        }
        
        private void SubscribeToFishNet()
        {
            try
            {
                var timeManager = FishNet.InstanceFinder.TimeManager;
                if (timeManager != null)
                {
                    if (_fishNetTimeManager != null && _prePhysicsDelegate != null)
                    {
                        try { _fishNetTimeManager.OnPrePhysicsSimulation -= _prePhysicsDelegate; } catch { }
                        try { _fishNetTimeManager.OnPostTick -= _postTickDelegate; } catch { }
                    }

                    _fishNetTimeManager = timeManager;
                    _prePhysicsDelegate = (Il2CppSystem.Action<float>)((float delta) => OnPrePhysicsSimulation(delta));
                    timeManager.OnPrePhysicsSimulation += _prePhysicsDelegate;
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
