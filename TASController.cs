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
        private TASEditorRenderer _editor;
        private bool _wasEditorKeyPressed = false;

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
        private bool _wasResetTickPressed = false;
        private bool _wasFastForwardPressed = false;
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
        private Il2CppSystem.Action _preTickDelegate;
        
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
                _editor = new TASEditorRenderer(this);

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
                        _editor?.ForceClose();
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
                        _wasGameEnded = false;
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

                // Auto-stop recording/playback when game ends
                CheckGameEnd();
                
                if (_macroSystem != null && _macroSystem.IsPlaying && Camera.main != null
                    && _timeController.CurrentTick <= _macroSystem.GreenzoneEnd)
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

                        // Capture the exact sbytes the game consumed this tick — re-injecting
                        // these during resim is bit-identical, unlike re-quantizing the floats.
                        state.MoveXRaw = rawData.moveInputSBytes.X;
                        state.MoveYRaw = rawData.moveInputSBytes.Y;
                        state.LookXRaw = rawData.lookInputSBytes.X;
                        state.LookYRaw = rawData.lookInputSBytes.Y;

                        _macroSystem.RecordTick(_timeController.CurrentTick, state);
                    }
                }

                if (_macroSystem.IsPlaying)
                {
                    ulong tick = _timeController.CurrentTick;

                    if (tick > _macroSystem.MaxTick)
                    {
                        StopPlayback();
                    }
                    else
                    {
                        // Every recorded tick has valid state — the replay injects it (no sim).
                        _macroSystem.PlaybackTick(tick);

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
            _editor?.ForceClose();
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
            _wasGameEnded = false;
            ApplyDeterministicSettings();
            if (_timeController != null && !_timeController.IsPaused)
                _timeController.TogglePause();
        }

        private int _prePhysicsCallCount = 0;

        private void OnPrePhysicsSimulation(float delta)
        {
            try
            {
                _prePhysicsCallCount++;
                if (!enabled || !_isInGame) return;
                if (_cachedRb == null) return;
                if (_macroSystem == null || !_macroSystem.IsPlaying) return;
                // Every recorded tick is valid state (no resim), so this always runs during
                // playback: inject the recorded rigidbody state before PhysX → bit-perfect.
                if (_timeController.CurrentTick > _macroSystem.GreenzoneEnd) return;

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

        /// <summary>
        /// Fires right before FishNet processes the tick. The game reads input from the
        /// PlayerInputHandler.rawData FIELD (resolved by name — stable across game versions,
        /// and no IL2CPP method patching), so during playback we write the recorded inputs
        /// there. The trajectory itself comes from state injection (OnPrePhysicsSimulation);
        /// this keeps the game's button/animation reads in sync with the replay.
        /// </summary>
        private void OnPreTick()
        {
            try
            {
                if (!enabled || !_isInGame) return;
                if (_macroSystem == null) return;

                if (!_macroSystem.IsPlaying) return;
                InjectRawInputData();
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error in OnPreTick: {ex}");
            }
        }

        /// <summary>
        /// Zeroes the injected inputs in rawData. Called when playback stops — the input
        /// handler is event-driven, so without this the last injected input (e.g. W held)
        /// keeps driving the player until a real input event arrives.
        /// </summary>
        private void ClearRawInputData()
        {
            try
            {
                var playerTransform = _gameObjectFinder.FindPlayerTransform();
                var handler = playerTransform?.GetComponent<EHS.PlayerInputHandler>();
                if (handler == null) return;

                var data = handler.rawData;
                data.moveInputSBytes = new EHS.Vector2SByte(0, 0);
                data.lookInputSBytes = new EHS.Vector2SByte(0, 0);

                var buttons = data.Buttons;
                buttons.HeldMask &= ~(4 | 8);
                buttons.PressedThisUpdateMask &= ~(4 | 8);
                buttons.PressedThisFixedUpdateMask &= ~(4 | 8);
                buttons.ReleasedThisUpdateMask &= ~(4 | 8);
                buttons.ReleasedThisFixedUpdateMask &= ~(4 | 8);
                data.Buttons = buttons;

                handler.rawData = data;
            }
            catch { }
        }

        private void InjectRawInputData()
        {
            var playerTransform = _gameObjectFinder.FindPlayerTransform();
            if (playerTransform == null) return;
            var handler = playerTransform.GetComponent<EHS.PlayerInputHandler>();
            if (handler == null) return;

            var data = handler.rawData;

            var (moveXRaw, moveYRaw) = _macroSystem.GetCurrentMoveRaw();
            data.moveInputSBytes = new EHS.Vector2SByte(moveXRaw, moveYRaw);

            var (lookXRaw, lookYRaw) = _macroSystem.GetCurrentLookRaw();
            data.lookInputSBytes = new EHS.Vector2SByte(lookXRaw, lookYRaw);

            var buttons = data.Buttons;

            int held = buttons.HeldMask & ~(4 | 8);
            if (_macroSystem.GetButtonHeld(4)) held |= 4;
            if (_macroSystem.GetButtonHeld(8)) held |= 8;

            int pressed = 0, released = 0;
            if (_macroSystem.GetButtonPressed(4))  pressed  |= 4;
            if (_macroSystem.GetButtonPressed(8))  pressed  |= 8;
            if (_macroSystem.GetButtonReleased(4)) released |= 4;
            if (_macroSystem.GetButtonReleased(8)) released |= 8;

            buttons.HeldMask = held;
            buttons.PressedThisUpdateMask = (buttons.PressedThisUpdateMask & ~(4 | 8)) | pressed;
            buttons.PressedThisFixedUpdateMask = (buttons.PressedThisFixedUpdateMask & ~(4 | 8)) | pressed;
            buttons.ReleasedThisUpdateMask = (buttons.ReleasedThisUpdateMask & ~(4 | 8)) | released;
            buttons.ReleasedThisFixedUpdateMask = (buttons.ReleasedThisFixedUpdateMask & ~(4 | 8)) | released;

            data.Buttons = buttons;
            handler.rawData = data;
        }
        
        public void OnGUI()
        {
            try
            {
                if (!enabled || !_isInGame) return;
                _overlayRenderer.OnGUI();
                _editor?.Draw();
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error in TASController.OnGUI: {ex}");
            }
        }
        
        private bool _wasGameEnded = false;
        private float _lastFwdNavTime = -1f;

        /// <summary>True if any gameplay key (move/jump/interact) is physically held right now.</summary>
        private bool GameplayKeyHeld()
            => UnityEngine.Input.GetKey(KeyCode.W) || UnityEngine.Input.GetKey(KeyCode.A)
            || UnityEngine.Input.GetKey(KeyCode.S) || UnityEngine.Input.GetKey(KeyCode.D)
            || UnityEngine.Input.GetKey(KeyCode.Space) || UnityEngine.Input.GetKey(KeyCode.E);

        private void CheckGameEnd()
        {
            try
            {
                bool isGameEnded = EHS.GameManager.IsGameEnded;
                
                if (isGameEnded && !_wasGameEnded)
                {
                    _wasGameEnded = true;
                    bool wasRecording = _macroSystem.IsRecording;

                    if (_macroSystem.IsPlaying)
                        StopPlayback();
                    if (_macroSystem.IsRecording)
                        _macroSystem.StopRecording();
                    
                    if (wasRecording)
                    {
                        TASPlugin.Logger.LogInfo("TAS: Game ended — recording stopped. Save it from the TAS Editor's FILE section.");
                        if (!_editor.IsVisible) _editor.ToggleVisibility();
                    }
                }
            }
            catch { }
        }
        
        private void HandleHotkeys()
        {
            if (TASBindMenuRenderer.IsVisibleGlobally) return;

            // While typing in an editor text field, swallow ALL hotkeys (including the
            // editor toggle) so e.g. pressing the bound letter doesn't close the window.
            if (TASEditorRenderer.IsTextFieldFocused)
            {
                _wasEditorKeyPressed = TASConfig.Settings.OpenEditor.IsPressed();
                return;
            }

            bool isEditorKeyPressed = TASConfig.Settings.OpenEditor.IsPressed();
            if (isEditorKeyPressed && !_wasEditorKeyPressed)
                _editor.ToggleVisibility();
            _wasEditorKeyPressed = isEditorKeyPressed;

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
                else if (!_macroSystem.IsPlaying) // recording during replay would wipe the macro
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
                else if (_macroSystem.HasRecordedData && !_macroSystem.IsRecording && !_macroSystem.IsEditMode)
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
            {
                bool justPressed = !_wasFrameAdvancePressed;
                bool editing = (_macroSystem.IsEditMode || _macroSystem.IsRecording) && _timeController.IsPaused;
                bool overExisting = editing && _timeController.CurrentTick < _macroSystem.MaxTick;

                if (overExisting && !GameplayKeyHeld())
                {
                    // NAVIGATE forward through recorded frames (non-destructive replay-step),
                    // with hold-repeat. No gameplay key held = scrubbing, nothing is lost.
                    bool step = justPressed;
                    float now = Time.unscaledTime;
                    if (!step) { if (now - _lastFwdNavTime >= 0.1f) { step = true; _lastFwdNavTime = now; } }
                    else _lastFwdNavTime = now;
                    if (step) RewindToTick(_timeController.CurrentTick + 1);
                }
                else
                {
                    // RECORD: you're holding an input over existing data (fork — drop the stale
                    // continuation once), or you're at the front, or it's a replay step.
                    if (overExisting && justPressed)
                        _macroSystem.TruncateAt(_timeController.CurrentTick + 1);
                    _timeController.TickFrameAdvance(justPressed: justPressed);
                }
            }

            bool isRewindPressed = TASConfig.Settings.RewindTick.IsPressed();
            bool canRewind = _timeController.IsPaused && !_macroSystem.IsEditMode && _timeController.CurrentTick > 0;
            bool canRewindRec = _timeController.IsPaused && (_macroSystem.IsEditMode || _macroSystem.IsRecording) && _timeController.CurrentTick > 0;
            
            if (isRewindPressed && (canRewind && _macroSystem.IsPlaying || canRewindRec))
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
                {
                    if (_macroSystem.IsPlaying)
                        RewindOneTick();
                    else
                        RewindRecording(_timeController.CurrentTick - 1);
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
            
            // Reset Tick (F5)
            bool isResetTickPressed = TASConfig.Settings.ResetTick.IsPressed();
            if (isResetTickPressed && !_wasResetTickPressed)
            {
                _timeController.SetTick(0);
                if (_macroSystem.IsRecording)
                    _macroSystem.RecordedInputs.Clear();
            }
            _wasResetTickPressed = isResetTickPressed;
            
            // Fast Forward (F6) — ×3 speed
            bool isFastForwardPressed = TASConfig.Settings.FastForward.IsPressed();
            if (isFastForwardPressed && !_wasFastForwardPressed)
                _timeController.ToggleFastForward();
            _wasFastForwardPressed = isFastForwardPressed;
        }
        
        // ===== Public API for the TAS Editor (piano roll) =====

        public InputMacroSystem MacroSystem => _macroSystem;
        public ulong EditorCurrentTick => _timeController != null ? _timeController.CurrentTick : 0;
        public bool EditorIsPaused => _timeController != null && _timeController.IsPaused;

        public void EditorPauseGame()
        {
            if (_timeController != null && !_timeController.IsPaused)
                _timeController.TogglePause();
        }

        public void EditorTogglePause()
        {
            _timeController?.TogglePause();
        }

        /// <summary>
        /// Steps one tick forward (editor button). Only while paused. In Edit Mode, navigates
        /// non-destructively through recorded frames (replay-step) when over existing data;
        /// records a live tick only at the front. (Live editing is done by holding a gameplay
        /// key with the frame-advance hotkey.)
        /// </summary>
        public void EditorStepForward()
        {
            if (_timeController == null || !_timeController.IsPaused) return;
            if ((_macroSystem.IsEditMode || _macroSystem.IsRecording)
                && _timeController.CurrentTick < _macroSystem.MaxTick)
            {
                RewindToTick(_timeController.CurrentTick + 1);   // navigate, non-destructive
                return;
            }
            _timeController.TickFrameAdvance(justPressed: true);
        }

        /// <summary>Steps one tick back (editor button). Mirrors the rewind hotkey guards.</summary>
        public void EditorStepBack()
        {
            if (_timeController == null || !_timeController.IsPaused) return;
            if (_timeController.CurrentTick == 0 || _macroSystem == null) return;

            if (_macroSystem.IsPlaying)
                RewindOneTick();
            else if (_macroSystem.IsEditMode || _macroSystem.IsRecording)
                RewindRecording(_timeController.CurrentTick - 1);
        }

        /// <summary>
        /// Called by the editor after importing a macro file: rebuilds the macro-start
        /// savestate from the macro's own first recorded tick, so playback starts from
        /// the real recording origin instead of wherever the player happened to stand.
        /// </summary>
        public void EditorMacroImported()
        {
            if (_macroSystem == null || !_macroSystem.HasRecordedData) return;

            ulong first = ulong.MaxValue;
            foreach (var k in _macroSystem.RecordedInputs.Keys)
                if (k < first) first = k;

            var st = _macroSystem.GetStateAtTick(first);
            if (st == null) return;
            var s = st.Value;

            _savestateSystem.SetMacroState(new SavestateSystem.SaveStateData(
                s.PlayerPosition, s.PlayerRotation, s.PlayerVelocity, s.PlayerAngularVelocity,
                s.CameraRotation, s.CameraPosition, s.CameraPan, s.CameraTilt), first);
        }

        /// <summary>
        /// Seeks (paused) to a tick inside the greenzone, entering paused playback so
        /// frame advance continues the macro from there. Returns false if the tick has
        /// no valid state (beyond the greenzone) — play through it to resimulate first.
        /// </summary>
        public bool SeekToTick(ulong tick)
        {
            if (_macroSystem == null || !_macroSystem.HasRecordedData) return false;
            if (tick > _macroSystem.GreenzoneEnd || _macroSystem.GetStateAtTick(tick) == null) return false;
            if (_macroSystem.IsRecording || _macroSystem.IsEditMode) return false;

            EditorPauseGame();

            if (!_macroSystem.IsPlaying)
                StartPlaybackWithAxes(tick);

            RewindToTick(tick);
            _macroSystem.PlaybackTick(tick);
            _macroSystem.PlaybackTick(tick);
            return true;
        }

        /// <summary>
        /// Editor "Edit here": positions the player at `tick`, switches from replay to live
        /// Edit Mode (records from tick+1, discarding the macro after `tick` — same as the F8
        /// hotkey), and runs the game so the player drives live. The editor closes its window
        /// afterwards so the cursor locks and mouse-look works.
        /// </summary>
        public bool EditorEnterEditModeAt(ulong tick)
        {
            if (_macroSystem == null || !_macroSystem.HasRecordedData) return false;
            if (_macroSystem.IsRecording || _macroSystem.IsEditMode) return false;
            if (tick > _macroSystem.MaxTick || _macroSystem.GetStateAtTick(tick) == null) return false;

            // Get into a paused replay positioned exactly at `tick` (same as clicking the
            // tick number), so the state below is identical to the F8 hotkey path.
            if (!SeekToTick(tick)) return false;

            // Exactly what the F8 hotkey does: enter live Edit Mode at the current tick,
            // leaving the game PAUSED (the user unpauses to drive).
            if (!_timeController.IsPaused) _timeController.TogglePause();
            ulong cutTick = _timeController.CurrentTick;
            StopPlayback();
            _macroSystem.EnterEditMode(cutTick);
            TASPlugin.Logger.LogInfo($"TAS: Edit Mode ON at tick {cutTick} (from editor, = F8).");
            return true;
        }

        /// <summary>
        /// Starts greenzone playback from the macro start (same as the Play hotkey) — the
        /// bit-perfect replay (state injection per tick). This is what the editor's Play does.
        /// </summary>
        public bool EditorPlayFromStart()
        {
            if (_macroSystem == null || !_macroSystem.HasRecordedData) return false;
            if (_macroSystem.IsPlaying || _macroSystem.IsRecording || _macroSystem.IsEditMode) return false;

            _savestateSystem.LoadState(_gameObjectFinder, _timeController, true);
            Physics.SyncTransforms();
            StartPlaybackWithAxes(_savestateSystem.MacroTick);

            // The editor pauses the game when it opens — playback must actually run.
            // Without this the player just floats at the loaded position (timeScale 0).
            if (_timeController.IsPaused)
                _timeController.TogglePause();
            return true;
        }

        public void EditorStopPlayback()
        {
            if (_macroSystem != null && _macroSystem.IsPlaying)
                StopPlayback();
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
            ClearRawInputData();
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
            RewindToTick(_timeController.CurrentTick - 1);
            if (_macroSystem.IsPlaying)
            {
                _macroSystem.PlaybackTick(_timeController.CurrentTick);
                _macroSystem.PlaybackTick(_timeController.CurrentTick);
            }
        }
        
        /// <summary>
        /// Rewinds during recording/Edit Mode — NON-destructive: just moves the cursor back
        /// and restores state. The forward ticks stay; they're only dropped when you actually
        /// re-record over them (fork-on-modify in FixedUpdate). So you can step back freely
        /// without losing anything until you change an input.
        /// </summary>
        private void RewindRecording(ulong targetTick)
        {
            RewindToTick(targetTick);
        }
        
        /// <summary>
        /// Common rewind logic: restore player, camera, orbital axes at target tick.
        /// </summary>
        private void RewindToTick(ulong targetTick)
        {
            if (_cachedRb == null) _cachedRb = _gameObjectFinder.GetCachedPlayerRigidbody();
            if (_cachedRb == null) return;
            try
            {
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
                _timeController.IsFastForward,
                _macroSystem.IsEditMode,
                _macroSystem.GreenzoneEnd,
                (ulong)_macroSystem.RecordedInputs.Count
            );

            // Live held input for the frame-by-frame input display (what gets recorded
            // on the next frame-advance tick). Read the same rawData the recorder reads.
            try
            {
                var handler = _gameObjectFinder.FindPlayerTransform()?.GetComponent<EHS.PlayerInputHandler>();
                if (handler != null)
                {
                    var rd = handler.rawData;
                    _overlayRenderer.UpdateLiveInput(
                        rd.moveInputSBytes.X, rd.moveInputSBytes.Y,
                        (rd.Buttons.HeldMask & 4) != 0, (rd.Buttons.HeldMask & 8) != 0,
                        rd.lookInputSBytes.X, rd.lookInputSBytes.Y);
                }
            }
            catch { }
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
                        try { _fishNetTimeManager.OnPreTick -= _preTickDelegate; } catch { }
                    }

                    _fishNetTimeManager = timeManager;
                    _prePhysicsDelegate = (Il2CppSystem.Action<float>)((float delta) => OnPrePhysicsSimulation(delta));
                    timeManager.OnPrePhysicsSimulation += _prePhysicsDelegate;
                    _postTickDelegate = (Il2CppSystem.Action)(() => OnPostTick());
                    timeManager.OnPostTick += _postTickDelegate;
                    _preTickDelegate = (Il2CppSystem.Action)(() => OnPreTick());
                    timeManager.OnPreTick += _preTickDelegate;
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
                    if (_preTickDelegate != null)
                        try { _fishNetTimeManager.OnPreTick -= _preTickDelegate; } catch { }
                }
            }
            catch { }
            finally
            {
                _fishNetTimeManager = null;
                _prePhysicsDelegate = null;
                _postTickDelegate = null;
                _preTickDelegate = null;
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
