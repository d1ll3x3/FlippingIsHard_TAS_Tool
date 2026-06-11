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
                _editor = new TASEditorRenderer(this);
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

                        bool record = true;
                        if (_robotActive)
                        {
                            ulong t = _timeController.CurrentTick;
                            if (t > _robotEndTick)
                            {
                                // Don't grow the macro past its original end (+1 tick drift per resim)
                                record = false;
                            }
                            else if (_robotScript.TryGetValue(t, out var scripted))
                            {
                                // Preserve the user's exact input timeline: rows keep the
                                // scripted values; only physics/camera state comes from the
                                // live simulation. (The robot's OS keys lag ~2 ticks, so the
                                // raw capture would shift and mangle the edited inputs.)
                                state.Move = scripted.Move;
                                state.Look = scripted.Look;
                                state.Jump = scripted.Jump;
                                state.Interact = scripted.Interact;
                                state.CameraPan = scripted.CameraPan;
                                state.CameraTilt = scripted.CameraTilt;
                            }
                        }

                        if (record)
                            _macroSystem.RecordTick(_timeController.CurrentTick, state);
                    }
                }

                if (_robotActive)
                {
                    UpdateRobotResim();
                }

                if (_macroSystem.IsPlaying)
                {
                    ulong tick = _timeController.CurrentTick;

                    if (tick > _macroSystem.MaxTick)
                    {
                        StopPlayback();
                    }
                    else if (tick > _macroSystem.GreenzoneEnd)
                    {
                        // End of valid recorded state. Playback cannot simulate past this
                        // point (the game freezes the player while IsPlaying) — pause on the
                        // last valid tick; the robot resim handles everything beyond it.
                        if (!_timeController.IsPaused) _timeController.TogglePause();
                        RewindToTick(_macroSystem.GreenzoneEnd);
                        _macroSystem.PlaybackTick(_timeController.CurrentTick);
                        TASPlugin.Logger.LogInfo($"TAS: Playback paused at greenzone end (tick {_macroSystem.GreenzoneEnd}). Use Resim to re-simulate the rest.");
                    }
                    else
                    {
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
            StopRobotResim(completed: false);
            ReleaseOSKeys();
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
                // Beyond the greenzone the recorded physics state is stale (inputs were edited):
                // inject only inputs (via GameInputPatch) and let PhysX resimulate.
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
        /// Fires right before FishNet processes the tick (replicates/movement logic).
        /// The game's native movement code reads PlayerInputHandler.rawData directly
        /// (a field — Harmony can't intercept that), so during playback we overwrite
        /// the field with the recorded inputs. Without this, input-only resimulation
        /// gets zero input (devices are disabled during playback).
        /// </summary>
        private void OnPreTick()
        {
            try
            {
                if (!enabled || !_isInGame) return;
                if (_macroSystem == null) return;

                // Robot resim: refresh the injected inputs right before the game
                // processes the tick, in case anything reset rawData since FixedUpdate.
                if (_robotActive)
                {
                    if (_robotScript.TryGetValue(_timeController.CurrentTick, out var st))
                        InjectRawDataFromScript(st, _robotPrevState);
                    return;
                }

                if (!_macroSystem.IsPlaying) return;
                InjectRawInputData();
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error in OnPreTick: {ex}");
            }
        }

        // ===== OS-level input playback (resim) =====
        // The game's FishNet prediction pipeline reads input through native (inlined)
        // code that no Harmony hook or memory write can reach. The only injection point
        // the game can't tell apart from a human is the OS keyboard itself: during
        // resimulation we literally press the keys with SendInput.
        private bool _osW, _osA, _osS, _osD, _osSpace, _osE;
        private bool _osKeysActive = false;

        private int _keyReassertCounter = 0;

        private void DriveOSKeysFromState(TASInputState st)
        {
            SetOSKey(KeyCode.W, ref _osW, st.Move.y > 0.3f);
            SetOSKey(KeyCode.S, ref _osS, st.Move.y < -0.3f);
            SetOSKey(KeyCode.A, ref _osA, st.Move.x < -0.3f);
            SetOSKey(KeyCode.D, ref _osD, st.Move.x > 0.3f);
            SetOSKey(KeyCode.Space, ref _osSpace, st.Jump);
            SetOSKey(KeyCode.E, ref _osE, st.Interact);
            _osKeysActive = true;

            // Re-assert held keys every ~10 ticks: SendInput only emits an event on
            // press/release, so if the game resets its input state mid-run (landing,
            // checkpoint, respawn) a long-held key would silently vanish for it.
            // A repeated key-down on an already-held key is harmless.
            if (++_keyReassertCounter >= 10)
            {
                _keyReassertCounter = 0;
                if (_osW) OSInput.SetKeyPressed(KeyCode.W, true);
                if (_osA) OSInput.SetKeyPressed(KeyCode.A, true);
                if (_osS) OSInput.SetKeyPressed(KeyCode.S, true);
                if (_osD) OSInput.SetKeyPressed(KeyCode.D, true);
                if (_osSpace) OSInput.SetKeyPressed(KeyCode.Space, true);
                if (_osE) OSInput.SetKeyPressed(KeyCode.E, true);
            }
        }

        /// <summary>
        /// Starts the robot resimulation: seeks to the cut tick (greenzone end, or the
        /// macro start for a full resim), copies the edited inputs into a script, enters
        /// the proven Edit-Mode recording path, and replays the script via the OS keyboard.
        /// </summary>
        public bool StartRobotResim(bool fromStart)
        {
            if (_macroSystem == null || !_macroSystem.HasRecordedData) return false;
            if (_robotActive || _macroSystem.IsRecording || _macroSystem.IsEditMode) return false;

            ulong cutTick;
            if (fromStart)
            {
                cutTick = ulong.MaxValue;
                foreach (var k in _macroSystem.RecordedInputs.Keys)
                    if (k < cutTick) cutTick = k;
                if (cutTick == ulong.MaxValue) return false;
            }
            else
            {
                cutTick = _macroSystem.GreenzoneEnd;
            }

            if (_macroSystem.GetStateAtTick(cutTick) == null)
            {
                TASPlugin.Logger.LogError($"TAS: Robot resim aborted — no recorded state at cut tick {cutTick}.");
                return false;
            }

            // Copy the script BEFORE EnterEditMode truncates everything past the cut
            _robotScript.Clear();
            _robotEndTick = _macroSystem.MaxTick;
            foreach (var kvp in _macroSystem.RecordedInputs)
                if (kvp.Key > cutTick) _robotScript[kvp.Key] = kvp.Value;

            if (_robotScript.Count == 0)
            {
                TASPlugin.Logger.LogInfo("TAS: Nothing to resimulate (greenzone already covers the whole macro).");
                return false;
            }

            if (_macroSystem.IsPlaying) StopPlayback();

            // Restore the player to the exact recorded state at the cut tick.
            // Uses the macro's own data (not the savestate slot) — also fixes the
            // bogus (0,0,0) teleport seen when the macro slot state was stale.
            _cachedRb = _gameObjectFinder.GetCachedPlayerRigidbody();
            RewindToTick(cutTick);

            _robotCutTick = cutTick;
            _robotStuckTicks = 0;
            _hasUndoableResim = false;
            _robotPrevState = _macroSystem.GetStateAtTick(cutTick) ?? default;

            // No truncation: the robot overwrites rows in place as it advances, so the
            // user's input timeline never disappears (and survives an abort).
            _macroSystem.BeginRobotRecording();
            DisableMouseDevice(); // keyboard must stay live for SendInput; mouse must not pollute look
            ApplyRobotSpeed();
            _resimDiagCount = 0;
            _robotActive = true;

            if (_timeController.IsPaused) _timeController.TogglePause();

            TASPlugin.Logger.LogInfo($"TAS: ROBOT RESIM started at tick {cutTick} → {_robotEndTick} ({_robotScript.Count} ticks, speed {RobotSpeed}x). Hands off the keyboard!");
            return true;
        }

        /// <summary>Runs every FixedUpdate while the robot is active.</summary>
        private void UpdateRobotResim()
        {
            // Edit mode got terminated externally (scene change, user F8, etc.)
            if (!_macroSystem.IsEditMode)
            {
                StopRobotResim(completed: false);
                return;
            }

            ulong tick = _timeController.CurrentTick;
            if (tick > _robotEndTick)
            {
                StopRobotResim(completed: true);
                return;
            }

            if (_robotScript.TryGetValue(tick, out var st))
            {
                // Primary input channel: write the script inputs straight into the
                // game's rawData. SendInput keys never reach this game's InputSystem
                // (diagnostics showed gameMove=(0,0) with keys held), but rawData
                // writes demonstrably drive the player outside playback mode — the
                // old "leftover input keeps moving the player after stop" proved it.
                InjectRawDataFromScript(st, _robotPrevState);
                DriveOSKeysFromState(st);
                InjectAxesFromState(st); // keep camera pan/tilt on the recorded path
                LogRobotDiagnostics(tick);

                // Stuck detection: script wants movement but the player hasn't moved for
                // a full second → something froze the player. Abort losslessly instead of
                // baking a dead run into the greenzone.
                bool wantsMove = st.Move.sqrMagnitude > 0.09f;
                bool stalled = _cachedRb != null && _cachedRb.linearVelocity.sqrMagnitude < 0.0004f;
                if (wantsMove && stalled) _robotStuckTicks++;
                else _robotStuckTicks = 0;

                if (_robotStuckTicks >= 60)
                {
                    TASPlugin.Logger.LogError("TAS: ROBOT RESIM stuck — no movement for 60 ticks with input held. Restoring macro.");
                    StopRobotResim(completed: false);
                }

                _robotPrevState = st;
            }
        }

        /// <summary>
        /// Writes the script inputs directly into PlayerInputHandler.rawData — the
        /// input source the game's movement pipeline actually consumes.
        /// </summary>
        private void InjectRawDataFromScript(TASInputState st, TASInputState prev)
        {
            try
            {
                var playerTransform = _gameObjectFinder.FindPlayerTransform();
                var handler = playerTransform?.GetComponent<EHS.PlayerInputHandler>();
                if (handler == null) return;

                var data = handler.rawData;

                data.moveInputSBytes = new EHS.Vector2SByte(
                    (sbyte)Mathf.RoundToInt(Mathf.Clamp(st.Move.x, -1f, 1f) * 127f),
                    (sbyte)Mathf.RoundToInt(Mathf.Clamp(st.Move.y, -1f, 1f) * 127f));
                data.lookInputSBytes = new EHS.Vector2SByte(
                    (sbyte)Mathf.RoundToInt(Mathf.Clamp(st.Look.x, -1f, 1f) * 127f),
                    (sbyte)Mathf.RoundToInt(Mathf.Clamp(st.Look.y, -1f, 1f) * 127f));

                var buttons = data.Buttons;

                int held = buttons.HeldMask & ~(4 | 8);
                if (st.Jump) held |= 4;
                if (st.Interact) held |= 8;

                int pressed = 0, released = 0;
                if (st.Jump && !prev.Jump) pressed |= 4;
                if (st.Interact && !prev.Interact) pressed |= 8;
                if (!st.Jump && prev.Jump) released |= 4;
                if (!st.Interact && prev.Interact) released |= 8;

                buttons.HeldMask = held;
                buttons.PressedThisUpdateMask = (buttons.PressedThisUpdateMask & ~(4 | 8)) | pressed;
                buttons.PressedThisFixedUpdateMask = (buttons.PressedThisFixedUpdateMask & ~(4 | 8)) | pressed;
                buttons.ReleasedThisUpdateMask = (buttons.ReleasedThisUpdateMask & ~(4 | 8)) | released;
                buttons.ReleasedThisFixedUpdateMask = (buttons.ReleasedThisFixedUpdateMask & ~(4 | 8)) | released;

                data.Buttons = buttons;
                handler.rawData = data;
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error injecting robot rawData: {ex}");
            }
        }

        public void StopRobotResim(bool completed)
        {
            if (!_robotActive) return;
            _robotActive = false;
            ReleaseOSKeys();
            ClearRawInputData(); // don't leave the last injected input driving the player
            EnableMouseDevice();
            RestoreRobotSpeed();

            if (_macroSystem.IsEditMode)
                _macroSystem.ExitEditMode();

            if (completed)
            {
                // Keep the script so the user can "Undo resim" if the result is bad
                _hasUndoableResim = true;
                TASPlugin.Logger.LogInfo($"TAS: ROBOT RESIM completed — greenzone now ends at {_macroSystem.GreenzoneEnd}.");
            }
            else
            {
                // Lossless abort: put the macro back exactly as it was before the resim
                RestoreMacroFromScript();
                _robotScript.Clear();
                _hasUndoableResim = false;
                TASPlugin.Logger.LogInfo($"TAS: ROBOT RESIM aborted — macro restored (greenzone back at {_macroSystem.GreenzoneEnd}).");
            }

            // Pause so the user can inspect the result in the editor
            if (!_timeController.IsPaused) _timeController.TogglePause();
        }

        /// <summary>Reverts the macro to its exact pre-resim contents (rows + greenzone).</summary>
        private void RestoreMacroFromScript()
        {
            foreach (var kvp in _robotScript)
                _macroSystem.RecordedInputs[kvp.Key] = kvp.Value;
            if (_macroSystem.GreenzoneEnd > _robotCutTick)
                _macroSystem.GreenzoneEnd = _robotCutTick;
        }

        /// <summary>Undoes the last completed robot resim (editor button).</summary>
        public bool UndoLastResim()
        {
            if (_robotActive || !_hasUndoableResim || _robotScript.Count == 0) return false;
            RestoreMacroFromScript();
            _robotScript.Clear();
            _hasUndoableResim = false;
            TASPlugin.Logger.LogInfo($"TAS: Last resim undone — macro restored (greenzone back at {_macroSystem.GreenzoneEnd}).");
            return true;
        }

        /// <summary>Called by the editor when the user edits inputs — the undo snapshot no longer matches.</summary>
        public void InvalidateResimUndo()
        {
            if (_robotActive) return;
            _hasUndoableResim = false;
            _robotScript.Clear();
        }

        private void ApplyRobotSpeed()
        {
            if (RobotSpeed >= 0.99f) return;
            if (!_timeController.IsSlowMo) _timeController.ToggleSlowMo();      // ×0.1
            _timeController.SetSlowMoBoost(RobotSpeed > 0.2f);                  // ×0.3 if boosted
        }

        private void RestoreRobotSpeed()
        {
            if (_timeController.IsSlowMo) _timeController.ToggleSlowMo();
        }

        private void DisableMouseDevice()
        {
            try
            {
                if (UnityEngine.InputSystem.Mouse.current != null)
                    UnityEngine.InputSystem.InputSystem.DisableDevice(UnityEngine.InputSystem.Mouse.current);
            }
            catch { }
        }

        private void EnableMouseDevice()
        {
            try
            {
                if (UnityEngine.InputSystem.Mouse.current != null)
                    UnityEngine.InputSystem.InputSystem.EnableDevice(UnityEngine.InputSystem.Mouse.current);
            }
            catch { }
        }

        private void SetOSKey(KeyCode key, ref bool state, bool desired)
        {
            if (state == desired) return;
            OSInput.SetKeyPressed(key, desired);
            state = desired;
        }

        private void ReleaseOSKeys()
        {
            if (!_osKeysActive) return;
            SetOSKey(KeyCode.W, ref _osW, false);
            SetOSKey(KeyCode.A, ref _osA, false);
            SetOSKey(KeyCode.S, ref _osS, false);
            SetOSKey(KeyCode.D, ref _osD, false);
            SetOSKey(KeyCode.Space, ref _osSpace, false);
            SetOSKey(KeyCode.E, ref _osE, false);
            _osKeysActive = false;
        }

        private int _resimDiagCount = 0;

        /// <summary>
        /// Logs robot resim health (pressed keys, position, velocity, game flags) —
        /// first ticks and then once per second.
        /// </summary>
        private void LogRobotDiagnostics(ulong tick)
        {
            _resimDiagCount++;
            if (_resimDiagCount > 5 && _resimDiagCount % 60 != 0) return;

            try
            {
                string keys = $"{(_osW ? "W" : "")}{(_osA ? "A" : "")}{(_osS ? "S" : "")}{(_osD ? "D" : "")}{(_osSpace ? "_" : "")}{(_osE ? "E" : "")}";
                string rbStr = _cachedRb != null
                    ? $"pos={_cachedRb.position} vel={_cachedRb.linearVelocity} kin={_cachedRb.isKinematic}"
                    : "rb=null";
                bool gamePaused = false, gameEnded = false, summoned = false;
                try
                {
                    gamePaused = EHS.GameManager.IsGamePaused;
                    gameEnded = EHS.GameManager.IsGameEnded;
                    summoned = EHS.GameManager.IsBeingSummoned;
                }
                catch { }

                // GameInputPatch doesn't intercept while the robot runs (IsPlaying=false),
                // so this is the input the game ACTUALLY sees. keys=[W] with gameMove=(0,0)
                // means the held key got lost; gameMove=(0,1) with no motion means the game
                // itself is freezing the player (summon/checkpoint/etc).
                string gameMove = "?";
                try
                {
                    var handler = _gameObjectFinder.FindPlayerTransform()?.GetComponent<EHS.PlayerInputHandler>();
                    if (handler != null) gameMove = handler.MoveInput.ToString();
                }
                catch { }

                TASPlugin.Logger.LogInfo($"TAS ROBOT t={tick} keys=[{keys}] gameMove={gameMove} {rbStr} gamePaused={gamePaused} gameEnded={gameEnded} summoned={summoned}");
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Robot diag error: {ex}");
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

            Vector2 move = _macroSystem.GetCurrentMoveInput();
            data.moveInputSBytes = new EHS.Vector2SByte(
                (sbyte)Mathf.RoundToInt(Mathf.Clamp(move.x, -1f, 1f) * 127f),
                (sbyte)Mathf.RoundToInt(Mathf.Clamp(move.y, -1f, 1f) * 127f));

            Vector2 look = _macroSystem.GetCurrentLookInput();
            data.lookInputSBytes = new EHS.Vector2SByte(
                (sbyte)Mathf.RoundToInt(Mathf.Clamp(look.x, -1f, 1f) * 127f),
                (sbyte)Mathf.RoundToInt(Mathf.Clamp(look.y, -1f, 1f) * 127f));

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

        // ===== Robot resim state =====
        // Resimulation = automated Edit Mode: playback freezes the player in this game
        // (native FishNet pipeline), but recording mode (F8) gives control back. The
        // robot replays the edited inputs through the OS keyboard while the proven
        // recording path re-captures the real state, regenerating the greenzone.
        private bool _robotActive = false;
        private ulong _robotEndTick = 0;
        private ulong _robotCutTick = 0;
        private int _robotStuckTicks = 0;
        private bool _hasUndoableResim = false;
        // Previous script state — needed for clean pressed/released button edges
        private TASInputState _robotPrevState;
        private readonly System.Collections.Generic.Dictionary<ulong, TASInputState> _robotScript
            = new System.Collections.Generic.Dictionary<ulong, TASInputState>();
        public bool IsRobotActive => _robotActive;
        public bool HasUndoableResim => _hasUndoableResim && !_robotActive;
        public float RobotSpeed { get; set; } = 1f; // 1, 0.3 or 0.1 — applied at robot start
        
        private void CheckGameEnd()
        {
            try
            {
                bool isGameEnded = EHS.GameManager.IsGameEnded;
                
                if (isGameEnded && !_wasGameEnded)
                {
                    _wasGameEnded = true;
                    bool wasRecording = _macroSystem.IsRecording;

                    if (_robotActive)
                        StopRobotResim(completed: true); // run finished during resim — result is valid
                    if (_macroSystem.IsPlaying)
                        StopPlayback();
                    if (_macroSystem.IsRecording)
                        _macroSystem.StopRecording();
                    
                    if (wasRecording)
                    {
                        TASPlugin.Logger.LogInfo("TAS: Game ended — recording stopped.");
                        _bindMenu.ToggleVisibility(); // open menu for export
                    }
                }
            }
            catch { }
        }
        
        private void HandleHotkeys()
        {
            if (TASBindMenuRenderer.IsVisibleGlobally) return;

            // While the robot is replaying, the OS keyboard belongs to it. Only allow
            // aborting via the EditMacro key (F8) — everything else is blocked so the
            // robot's synthetic keypresses can't trigger TAS hotkeys.
            if (_robotActive)
            {
                bool isAbortPressed = TASConfig.Settings.EditMacro.IsPressed();
                if (isAbortPressed && !_wasEditModePressed)
                    StopRobotResim(completed: false);
                _wasEditModePressed = isAbortPressed;
                return;
            }

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
                _timeController.TickFrameAdvance(justPressed: !_wasFrameAdvancePressed);

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
        /// Starts greenzone playback from the macro start (same as the Play hotkey).
        /// Playback auto-pauses at the greenzone end; resimulation past it is the
        /// robot's job (StartRobotResim).
        /// </summary>
        public bool EditorPlayFromStart()
        {
            if (_macroSystem == null || !_macroSystem.HasRecordedData) return false;
            if (_macroSystem.IsPlaying || _macroSystem.IsRecording || _macroSystem.IsEditMode || _robotActive) return false;

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
            ReleaseOSKeys();
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
        /// Rewinds during recording/Edit Mode: restores state + sets cut point for deferred truncation.
        /// </summary>
        private void RewindRecording(ulong targetTick)
        {
            RewindToTick(targetTick);
            // Immediately truncate data from targetTick+1 onward (same as EnterEditMode)
            _macroSystem.TruncateAt(targetTick + 1);
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
                StopRobotResim(completed: false);
                ReleaseOSKeys();
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
