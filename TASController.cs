using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using static FlippingIsHardTAS.TASInputState;

namespace FlippingIsHardTAS
{
    /// <summary>
    /// Owns the TAS state machine (idle / recording / Edit Mode / replay), the hotkeys,
    /// and every interaction with the game: rigidbody state injection, rawData writes,
    /// camera and Cinemachine axes, FishNet tick hooks and scene transitions.
    /// </summary>
    public class TASController
    {
        private TimeController _time;
        private SavestateSystem _saves;
        private InputMacroSystem _macro;
        private TASBindMenuRenderer _bindMenu;
        private TASEditorRenderer _editor;
        private OverlayRenderer _overlay;
        private GameObjectFinder _finder;

        public bool enabled { get; private set; }

        // Physics
        private Rigidbody _cachedRb;
        private RigidbodyInterpolation _originalInterpolation;

        // FishNet tick hooks
        private FishNet.Managing.Timing.TimeManager _fishNetTimeManager;
        private Il2CppSystem.Action<float> _prePhysicsDelegate;
        private Il2CppSystem.Action _preTickDelegate;

        // Scene tracking
        private string _lastScene = "";
        private bool _isInGame = false;
        private bool _wasGameEnded = false;

        // Quick restart: the game resets Time.timeScale while it finishes loading, which
        // overrides our pause. Once the new player exists, hold "paused at tick 0" briefly.
        private bool _restartPending = false;
        private float _restartHoldUntil = 0f;
        private const float RESTART_HOLD_SECONDS = 2.5f;

        // Pinned inputs (editor) and what was last written into rawData for them
        private InputPins _pins;
        private InputPins _lastInjectedPins;
        private bool _pinsApplied;

        // Hold-repeat timers for navigation (frame-step over recorded rows / rewind)
        private float _navHoldStart, _navLastFire, _rewHoldStart, _rewLastFire;
        private bool _gameMenuToastShown;

        /// <summary>A rebindable hotkey sampled once per frame (edge + held state).</summary>
        private sealed class Hotkey
        {
            private readonly Func<TASSettings, KeyBind> _bind;
            private bool _was;
            public bool Held, Down;
            public Hotkey(Func<TASSettings, KeyBind> bind) { _bind = bind; }
            public void Sample()
            {
                Held = _bind(TASConfig.Settings)?.IsPressed() ?? false;
                Down = Held && !_was;
                _was = Held;
            }
        }

        private readonly Hotkey _kEditor   = new Hotkey(s => s.OpenEditor);
        private readonly Hotkey _kMenu     = new Hotkey(s => s.OpenBindMenu);
        private readonly Hotkey _kHelp     = new Hotkey(s => s.ToggleHelp);
        private readonly Hotkey _kSave     = new Hotkey(s => s.SavePosition);
        private readonly Hotkey _kLoad     = new Hotkey(s => s.Teleport);
        private readonly Hotkey _kRecord   = new Hotkey(s => s.RecordMacro);
        private readonly Hotkey _kPlay     = new Hotkey(s => s.PlayMacro);
        private readonly Hotkey _kEdit     = new Hotkey(s => s.EditMacro);
        private readonly Hotkey _kPause    = new Hotkey(s => s.Pause);
        private readonly Hotkey _kSlowMo   = new Hotkey(s => s.SlowMo);
        private readonly Hotkey _kBoost    = new Hotkey(s => s.SlowMoBoost);
        private readonly Hotkey _kAdvance  = new Hotkey(s => s.FrameAdvance);
        private readonly Hotkey _kRewind   = new Hotkey(s => s.RewindTick);
        private readonly Hotkey _kReset    = new Hotkey(s => s.ResetTick);
        private readonly Hotkey _kFastFwd  = new Hotkey(s => s.FastForward);
        private Hotkey[] _allHotkeys;

        public bool Initialize(GameObjectFinder finder, TASBindMenuRenderer bindMenu)
        {
            try
            {
                _finder = finder;
                _overlay = new OverlayRenderer();
                _time = new TimeController();
                _saves = new SavestateSystem();
                _macro = new InputMacroSystem();
                _bindMenu = bindMenu;
                // Closing the menu re-enables the input devices unconditionally — if a
                // replay is running they must be locked again immediately.
                _bindMenu.OnMenuClosed = () => _macro.ReapplyDeviceLock();
                _editor = new TASEditorRenderer(this);
                _allHotkeys = new[] { _kEditor, _kMenu, _kHelp, _kSave, _kLoad, _kRecord, _kPlay, _kEdit,
                                      _kPause, _kSlowMo, _kBoost, _kAdvance, _kRewind, _kReset, _kFastFwd };

                ApplyDeterministicSettings();
                SubscribeToFishNet();
                SceneManager.add_sceneLoaded(new Action<Scene, LoadSceneMode>(OnSceneLoaded));

                enabled = true;
                return true;
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error initializing TASController: {ex}");
                return false;
            }
        }

        // ═════════════════════════════════════════════════════════════════
        //  Unity callbacks
        // ═════════════════════════════════════════════════════════════════

        public void Update()
        {
            if (!enabled) return;
            try
            {
                TrackScene();
                if (!_isInGame) return;

                if (FishNetTimeManagerGone())
                    ResetTrainer();   // the networked session was torn down under us
                if (_fishNetTimeManager == null)
                    SubscribeToFishNet();

                HoldPauseAfterRestart();
                // The game can reset Time.timeScale behind our back — keep the pause authoritative.
                _time.EnforcePause();

                HandleHotkeys();
                _editor.HandleShortcuts();
                ReleasePinsIfIdle();
                CheckGameEnd();
                ForcePlaybackCamera();
                UpdateHud();
            }
            catch (Exception ex)
            {
                LogLoopError("Update", ex);
            }
        }

        public void FixedUpdate()
        {
            if (!enabled || !_isInGame) return;
            try
            {
                _time.FixedUpdate();

                if (_cachedRb == null)
                    _cachedRb = _finder.GetCachedPlayerRigidbody();

                if (_macro.IsRecording)
                {
                    // Pins first, so the recorder below captures the pinned inputs.
                    if (_pins.Active) InjectPins(writeEdges: false);
                    RecordCurrentTick();
                }

                if (_macro.IsPlaying)
                    PlaybackStep();
            }
            catch (Exception ex)
            {
                LogLoopError("FixedUpdate", ex);
            }
        }

        public void OnGUI()
        {
            if (!enabled || !_isInGame) return;
            try
            {
                _overlay.OnGUI();
                _editor.Draw();
            }
            catch (Exception ex)
            {
                LogLoopError("OnGUI", ex);
            }
        }

        /// <summary>Per-frame code: log each distinct failure once instead of every frame.</summary>
        private static void LogLoopError(string where, Exception ex)
            => TASPlugin.WarnOnce($"{where}:{ex.GetType().Name}:{ex.Message}", $"Error in TASController.{where}: {ex}");

        // ═════════════════════════════════════════════════════════════════
        //  Scenes, restart, game end
        // ═════════════════════════════════════════════════════════════════

        private bool FishNetTimeManagerGone()
        {
            if (_fishNetTimeManager == null) return false;
            try { return _fishNetTimeManager.gameObject == null; }
            catch (Exception) { return true; }   // the wrapper throws once the native object is collected
        }

        private static bool IsMenuScene(string name)
            => string.IsNullOrEmpty(name) || name.Contains("MainMenu") || name.Contains("Bootstrap")
            || name.Contains("Cinematic") || name.Contains("Loading");

        private void TrackScene()
        {
            string scene = SceneManager.GetActiveScene().name;
            if (scene == _lastScene) return;
            _lastScene = scene;

            bool wasInGame = _isInGame;
            _isInGame = !IsMenuScene(scene);

            if (wasInGame && !_isInGame)
            {
                TASPlugin.Logger.LogInfo("TAS: back to the menu — mod dormant (the macro is kept).");
                LeaveLevel();
                UnsubscribeFromFishNet();
            }
            else if (_isInGame && !wasInGame)
            {
                // The level's own reset (and the hold at tick 0) already ran in OnSceneLoaded.
                TASPlugin.Logger.LogInfo($"TAS: entered {scene}.");
                ApplyDeterministicSettings();
            }
        }

        /// <summary>Stops every mode and forgets level objects. The recorded run is kept.</summary>
        private void LeaveLevel()
        {
            _editor.ForceClose();
            _bindMenu.ForceClose();
            // Forget the old level's objects BEFORE stopping playback, so the cleanup can't
            // touch destroyed components.
            _finder.ClearCache();
            _cachedRb = null;
            if (_macro.IsPlaying) StopPlayback();
            _macro.ResetModes();
            _pins = default;
            _pinsApplied = false;
            _time.ResetTick();
            _restartPending = false;
            _restartHoldUntil = 0f;
            _wasGameEnded = false;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                // "Scene_Game*" is the level on both the demo and the full game; any other
                // non-menu scene counts only when it replaces the level (not additive chunks).
                bool level = scene.name.StartsWith("Scene_Game")
                             || (!IsMenuScene(scene.name) && mode == LoadSceneMode.Single);
                if (level)
                {
                    TASPlugin.Logger.LogInfo($"TAS: {scene.name} loaded (quick restart) — holding at tick 0.");
                    ResetTrainer();
                }
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error in OnSceneLoaded: {ex}");
            }
        }

        private void ResetTrainer()
        {
            LeaveLevel();
            // Drop our hooks: if FishNet's TimeManager survives the reload we'd otherwise be
            // subscribed twice and run every tick hook twice.
            UnsubscribeFromFishNet();
            ApplyDeterministicSettings();
            _time.SetPaused(true);
            // The pause gets overridden while the game finishes loading; it's re-applied
            // (with tick 0) once the new player spawns — see HoldPauseAfterRestart.
            _restartPending = true;
        }

        private void HoldPauseAfterRestart()
        {
            if (_restartPending && _finder.FindPlayerTransform() != null)
            {
                _restartPending = false;
                _restartHoldUntil = Time.unscaledTime + RESTART_HOLD_SECONDS;
            }
            if (_restartHoldUntil <= 0f) return;

            if (Time.unscaledTime < _restartHoldUntil)
            {
                _time.SetPaused(true);
                if (_time.CurrentTick != 0) _time.SetTick(0);
            }
            else
            {
                _restartHoldUntil = 0f;
                TASGui.Toast("Level ready — paused at tick 0", TASGui.Pause);
            }
        }

        private void CheckGameEnd()
        {
            bool ended;
            try { ended = EHS.GameManager.IsGameEnded; }
            catch (Exception ex)
            {
                TASPlugin.WarnOnce("game-ended", $"GameManager.IsGameEnded unavailable: {ex.Message}");
                return;
            }

            if (ended && !_wasGameEnded)
            {
                bool wasRecording = _macro.IsRecording;
                if (_macro.IsPlaying) StopPlayback();
                if (_macro.IsRecording) _macro.StopRecording();

                if (wasRecording)
                {
                    TASGui.Toast($"Run finished — {_macro.RecordedInputs.Count} ticks recorded. Save it in the editor.", TASGui.Ok);
                    if (!_editor.IsVisible) _editor.ToggleVisibility();
                }
            }
            _wasGameEnded = ended;
        }

        /// <summary>True while the game's own pause/customization menu is open.</summary>
        private bool GameMenuOpen()
        {
            try { return EHS.GameManager.IsPauseMenuShown || EHS.GameManager.IsCustomizationScreenShown; }
            catch (Exception ex)
            {
                TASPlugin.WarnOnce("game-menu", $"GameManager menu flags unavailable: {ex.Message}");
                return false;
            }
        }

        // ═════════════════════════════════════════════════════════════════
        //  Hotkeys
        // ═════════════════════════════════════════════════════════════════

        private void HandleHotkeys()
        {
            // Sample every key each frame, even when blocked, so a key held while a menu
            // was open doesn't register as a fresh press once it closes.
            foreach (var k in _allHotkeys) k.Sample();

            if (TASBindMenuRenderer.IsVisibleGlobally || TASGui.IsTyping) return;
            if (GameMenuOpen())
            {
                if (!_gameMenuToastShown && Array.Exists(_allHotkeys, k => k.Down))
                {
                    TASGui.Toast("Game menu open — TAS hotkeys paused", TASGui.TextDim);
                    _gameMenuToastShown = true;
                }
                return;
            }
            _gameMenuToastShown = false;

            if (_kEditor.Down) _editor.ToggleVisibility();
            if (_kMenu.Down) _bindMenu.ToggleVisibility();
            if (_kHelp.Down)
            {
                TASConfig.Settings.ShowKeyHelp = !TASConfig.Settings.ShowKeyHelp;
                TASConfig.Save();
            }

            if (_kSave.Down) SaveManualState();
            if (_kLoad.Down) LoadManualState();
            if (_kRecord.Down) OnRecordKey();
            if (_kPlay.Down) OnPlayKey();
            if (_kEdit.Down) OnEditKey();

            if (_kPause.Down) _time.TogglePause();
            if (_kSlowMo.Down) _time.ToggleSlowMo();
            if (_kFastFwd.Down) _time.ToggleFastForward();
            _time.SetSlowMoBoost(_kBoost.Held);

            if (_kAdvance.Held) StepForward(_kAdvance.Down);

            if (_kRewind.Held)
            {
                if (!_time.IsPaused)
                {
                    if (_kRewind.Down) TASGui.Toast($"Pause first ({TASConfig.Settings.Pause}) to step back", TASGui.TextDim);
                }
                else if (RepeatFire(_kRewind.Down, ref _rewHoldStart, ref _rewLastFire))
                {
                    StepBack();
                }
            }

            if (_kReset.Down) OnResetTickKey();
        }

        /// <summary>Hold-to-repeat: fires on press, then 10/s after the hold delay.</summary>
        private static bool RepeatFire(bool justPressed, ref float holdStart, ref float lastFire)
        {
            float now = Time.unscaledTime;
            if (justPressed) { holdStart = now; lastFire = now; return true; }
            if (now - holdStart < TimeController.HoldDelay) return false;
            if (now - lastFire < TimeController.HoldInterval) return false;
            lastFire = now;
            return true;
        }

        private void OnRecordKey()
        {
            if (_macro.IsEditMode)
            {
                _macro.ExitEditMode();
                TASGui.Toast($"Edit Mode off — {_macro.RecordedInputs.Count} ticks", TASGui.TextDim);
            }
            else if (_macro.IsRecording)
            {
                _macro.StopRecording();
                TASGui.Toast($"Recording stopped — {_macro.RecordedInputs.Count} ticks", TASGui.TextDim);
            }
            else if (_macro.IsPlaying)
            {
                TASGui.Toast($"Stop the replay first, or press {TASConfig.Settings.EditMacro} to edit from here", TASGui.Pause);
            }
            else
            {
                StartRecordingHere();
            }
        }

        private void StartRecordingHere()
        {
            var start = CaptureState();
            if (start == null) { TASGui.Toast("Player not found — can't record", TASGui.Danger); return; }
            _saves.SetMacroStart(start, _time.CurrentTick);
            _macro.StartRecording();
            TASGui.Toast($"● Recording from tick {_time.CurrentTick}", TASGui.Rec);
        }

        private void OnPlayKey()
        {
            if (_macro.IsPlaying)
            {
                StopPlayback();
                TASGui.Toast($"Replay stopped at tick {_time.CurrentTick}", TASGui.TextDim);
            }
            else if (!_macro.HasRecordedData)
            {
                TASGui.Toast($"Nothing to play — record ({TASConfig.Settings.RecordMacro}) or load a macro ({TASConfig.Settings.OpenEditor})", TASGui.TextDim);
            }
            else if (PlayFromStart(unpause: false))
            {
                TASGui.Toast($"▶ Replaying {_macro.RecordedInputs.Count} ticks", TASGui.Play);
            }
        }

        private void OnEditKey()
        {
            if (_macro.IsEditMode)
            {
                _macro.ExitEditMode();
                TASGui.Toast($"Edit Mode off — {_macro.RecordedInputs.Count} ticks", TASGui.TextDim);
            }
            else if (_macro.IsPlaying || _macro.IsRecording)
            {
                if (BeginEditAt(_time.CurrentTick))
                    TASGui.Toast($"✎ Edit Mode at tick {_time.CurrentTick}", TASGui.Edit);
                else
                    TASGui.Toast($"Tick {_time.CurrentTick} has no recorded frame to edit from", TASGui.Pause);
            }
            else if (_macro.HasRecordedData)
            {
                // Idle with a macro: continue it from its last frame.
                if (BeginEditAt(_macro.MaxTick))
                    TASGui.Toast($"✎ Edit Mode at the end of the macro (tick {_macro.MaxTick})", TASGui.Edit);
            }
            else
            {
                TASGui.Toast($"No macro to edit — record one first ({TASConfig.Settings.RecordMacro})", TASGui.TextDim);
            }
        }

        private void OnResetTickKey()
        {
            if (_macro.IsPlaying || _macro.IsEditMode)
            {
                // Rows are keyed by tick: moving the counter would desync them.
                TASGui.Toast("Tick reset isn't available during a replay or in Edit Mode", TASGui.Pause);
            }
            else if (_macro.IsRecording)
            {
                _time.SetTick(0);
                StartRecordingHere();
            }
            else
            {
                _time.SetTick(0);
                TASGui.Toast("Tick reset to 0", TASGui.TextDim);
            }
        }

        // ═════════════════════════════════════════════════════════════════
        //  Frame stepping
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Frame advance (paused only). In Edit Mode over existing rows with no input held,
        /// it navigates the recording non-destructively. With input held or pinned it FORKS:
        /// the rows after the cursor are dropped and the new tick is recorded live.
        /// </summary>
        private void StepForward(bool justPressed)
        {
            if (!_time.IsPaused) return;

            bool overExisting = _macro.IsRecording && _time.CurrentTick < _macro.MaxTick;
            if (overExisting && !_pins.Active && !GameplayInputHeld())
            {
                if (RepeatFire(justPressed, ref _navHoldStart, ref _navLastFire))
                    NavigateTo(_time.CurrentTick + 1);
                return;
            }

            if (overExisting)
            {
                _macro.TruncateAt(_time.CurrentTick + 1);
                TASGui.Toast($"Forked at tick {_time.CurrentTick} — recording new input", TASGui.Edit);
            }
            _time.TickFrameAdvance(justPressed);
        }

        private void StepBack()
        {
            if (!_time.IsPaused || _time.CurrentTick == 0) return;
            ulong target = _time.CurrentTick - 1;

            if (_macro.IsPlaying)
            {
                NavigateTo(target);
                _macro.SeekPlayback(_time.CurrentTick);
            }
            else if (_macro.IsRecording)
            {
                NavigateTo(target);
            }
        }

        /// <summary>
        /// True if the player is physically giving gameplay input: the default game keys,
        /// or anything the game's input handler currently reports (covers rebinds/gamepads).
        /// </summary>
        private bool GameplayInputHeld()
        {
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.S)
                || Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.Space) || Input.GetKey(KeyCode.E))
                return true;

            var handler = _finder.GetInputHandler();
            if (handler == null) return false;
            var raw = handler.rawData;
            return raw.moveInputSBytes.X != 0 || raw.moveInputSBytes.Y != 0
                || (raw.Buttons.HeldMask & (BTN_JUMP | BTN_INTERACT)) != 0;
        }

        /// <summary>
        /// Restores the recorded state at `targetTick` (player, camera, orbit axes) and moves
        /// the tick counter there. Non-destructive: used by seek, rewind and Edit Mode stepping.
        /// </summary>
        private bool NavigateTo(ulong targetTick)
        {
            var state = _macro.GetStateAtTick(targetTick);
            if (state == null) return false;
            if (_cachedRb == null) _cachedRb = _finder.GetCachedPlayerRigidbody();
            if (_cachedRb == null) return false;

            var s = state.Value;
            ApplyBodyState(s.PlayerPosition, s.PlayerRotation, s.PlayerVelocity, s.PlayerAngularVelocity);

            var cam = Camera.main;
            if (cam != null)
            {
                cam.transform.position = s.CameraPosition;
                cam.transform.rotation = Normalized(s.CameraRotation);
            }

            ResetCinemachineDamping();
            SetOrbitalAxes(s.CameraPan, s.CameraTilt);
            ForceCinemachineUpdate();

            _time.SetTick(targetTick);
            return true;
        }

        // ═════════════════════════════════════════════════════════════════
        //  Recording / playback
        // ═════════════════════════════════════════════════════════════════

        private void RecordCurrentTick()
        {
            var player = _finder.FindPlayerTransform();
            var handler = _finder.GetInputHandler();
            if (player == null || handler == null) return;

            var raw = handler.rawData;
            TryGetOrbitalAxes(out float camPan, out float camTilt);
            var cam = Camera.main;

            var state = new TASInputState(
                handler.MoveInput,
                handler.LookInput(),
                cam != null ? cam.transform.rotation : Quaternion.identity,
                cam != null ? cam.transform.position : Vector3.zero,
                (raw.Buttons.HeldMask & BTN_JUMP) != 0,
                (raw.Buttons.HeldMask & BTN_INTERACT) != 0,
                _cachedRb != null ? _cachedRb.position : player.position,
                _cachedRb != null ? _cachedRb.rotation : player.rotation,
                _cachedRb != null ? _cachedRb.linearVelocity : Vector3.zero,
                _cachedRb != null ? _cachedRb.angularVelocity : Vector3.zero,
                camPan, camTilt);

            // The exact sbytes the game consumed this tick (not a re-quantization of the floats).
            state.MoveXRaw = raw.moveInputSBytes.X;
            state.MoveYRaw = raw.moveInputSBytes.Y;
            state.LookXRaw = raw.lookInputSBytes.X;
            state.LookYRaw = raw.lookInputSBytes.Y;

            _macro.RecordTick(_time.CurrentTick, state);
        }

        private void PlaybackStep()
        {
            ulong tick = _time.CurrentTick;
            if (tick > _macro.MaxTick)
            {
                StopPlayback();
                TASGui.Toast("Replay finished", TASGui.TextDim);
                return;
            }

            // Every recorded tick is a full valid state — the replay injects it (no sim).
            _macro.PlaybackTick(tick);
            if (!_macro.CurrentValid) return;

            var cam = Camera.main;
            if (cam != null)
            {
                cam.transform.rotation = Normalized(_macro.Current.CameraRotation);
                cam.transform.position = _macro.Current.CameraPosition;
            }
            SetOrbitalAxes(_macro.Current.CameraPan, _macro.Current.CameraTilt);

            if (tick == _macro.MaxTick && TASConfig.Settings.PauseAtPlaybackEnd && !_time.IsPaused)
            {
                _time.SetPaused(true);
                TASGui.Toast($"End of macro (tick {tick}) — {TASConfig.Settings.EditMacro} to keep editing, {TASConfig.Settings.Pause} to continue live", TASGui.Pause);
            }
        }

        /// <summary>
        /// Keeps the rendered camera on the recorded pose between physics ticks (Cinemachine
        /// would otherwise smooth it away from the recording).
        /// </summary>
        private void ForcePlaybackCamera()
        {
            if (!_macro.IsPlaying || !_macro.CurrentValid || _time.CurrentTick > _macro.MaxTick) return;
            var cam = Camera.main;
            if (cam == null) return;

            if (_time.IsPaused)
            {
                cam.transform.rotation = Normalized(_macro.Current.CameraRotation);
                cam.transform.position = _macro.Current.CameraPosition;
            }
            else
            {
                float t = (Time.time - Time.fixedTime) / Time.fixedDeltaTime;
                var prev = _macro.Previous;
                var cur = _macro.Current;
                cam.transform.rotation = Normalized(Quaternion.SlerpUnclamped(prev.CameraRotation, cur.CameraRotation, t));
                cam.transform.position = Vector3.LerpUnclamped(prev.CameraPosition, cur.CameraPosition, t);
            }
        }

        private void StartPlayback(ulong startTick)
        {
            _cachedRb = _finder.GetCachedPlayerRigidbody();
            if (_cachedRb != null)
            {
                _originalInterpolation = _cachedRb.interpolation;
                _cachedRb.interpolation = RigidbodyInterpolation.Interpolate;
            }

            _macro.StartPlaying();
            _macro.SeekPlayback(startTick);
            ResetCinemachineDamping();

            var first = _macro.GetStateAtTick(startTick);
            if (first.HasValue)
            {
                SetOrbitalAxes(first.Value.CameraPan, first.Value.CameraTilt);
                ForceCinemachineUpdate();
            }
        }

        private void StopPlayback()
        {
            _macro.StopPlaying();
            ClearRawInputData();
            if (_cachedRb != null)
                _cachedRb.interpolation = _originalInterpolation;
            if (_macro.CurrentValid)
                SetOrbitalAxes(_macro.Current.CameraPan, _macro.Current.CameraTilt);
            SetCinemachineBrain(true);
        }

        /// <summary>FishNet: right before PhysX runs — inject the recorded body state.</summary>
        private void OnPrePhysicsSimulation(float delta)
        {
            try
            {
                if (!enabled || !_isInGame || _cachedRb == null) return;
                if (!_macro.IsPlaying || !_macro.CurrentValid) return;
                if (_time.CurrentTick > _macro.MaxTick) return;

                var s = _macro.Current;
                _cachedRb.position        = s.PlayerPosition;
                _cachedRb.rotation        = s.PlayerRotation;
                _cachedRb.linearVelocity  = s.PlayerVelocity;
                _cachedRb.angularVelocity = s.PlayerAngularVelocity;
            }
            catch (Exception ex)
            {
                LogLoopError("OnPrePhysicsSimulation", ex);
            }
        }

        /// <summary>
        /// FishNet: right before the game processes a tick. The game reads input from the
        /// PlayerInputHandler.rawData FIELD (resolved by name — stable across versions, no
        /// method patching). During playback the recorded inputs go there so the game's
        /// button/animation reads match the replay; while recording, the pinned inputs do.
        /// </summary>
        private void OnPreTick()
        {
            try
            {
                if (!enabled || !_isInGame) return;
                if (_macro.IsPlaying)
                {
                    if (!_macro.CurrentValid) return;
                    var cur = _macro.Current;
                    var prev = _macro.Previous;
                    WriteRawInput(cur.MoveXRaw, cur.MoveYRaw, cur.LookXRaw, cur.LookYRaw, true,
                                  cur.Jump, cur.Interact, prev.Jump, prev.Interact, writeEdges: true);
                }
                else if (_macro.IsRecording && _pins.Active)
                {
                    InjectPins(writeEdges: true);
                }
            }
            catch (Exception ex)
            {
                LogLoopError("OnPreTick", ex);
            }
        }

        // ═════════════════════════════════════════════════════════════════
        //  rawData writes (playback inputs and pins)
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Writes the pins into rawData. Edges (pressed/released this tick) are computed
        /// against the previous pinned tick and only written from OnPreTick — the game clears
        /// them after every fixed update, and computing them twice per tick would lose them.
        /// </summary>
        private void InjectPins(bool writeEdges)
        {
            var prev = _lastInjectedPins;
            WriteRawInput(_pins.MoveX, _pins.MoveY, 0, 0, false,
                          _pins.Jump, _pins.Interact, prev.Jump, prev.Interact, writeEdges);
            if (writeEdges) _lastInjectedPins = _pins;
            _pinsApplied = true;
        }

        /// <summary>Clears what the pins wrote once they're released or recording stops.</summary>
        private void ReleasePinsIfIdle()
        {
            if (!_pinsApplied || (_pins.Active && _macro.IsRecording)) return;
            ClearRawInputData();
            _pinsApplied = false;
            _lastInjectedPins = default;
        }

        private void WriteRawInput(sbyte moveX, sbyte moveY, sbyte lookX, sbyte lookY, bool writeLook,
                                   bool jump, bool interact, bool prevJump, bool prevInteract, bool writeEdges)
        {
            var handler = _finder.GetInputHandler();
            if (handler == null) return;

            var data = handler.rawData;
            data.moveInputSBytes = new EHS.Vector2SByte(moveX, moveY);
            if (writeLook) data.lookInputSBytes = new EHS.Vector2SByte(lookX, lookY);

            const int ours = BTN_JUMP | BTN_INTERACT;
            var buttons = data.Buttons;
            int held = buttons.HeldMask & ~ours;
            if (jump) held |= BTN_JUMP;
            if (interact) held |= BTN_INTERACT;
            buttons.HeldMask = held;

            if (writeEdges)
            {
                int pressed = 0, released = 0;
                if (jump && !prevJump) pressed |= BTN_JUMP;
                if (interact && !prevInteract) pressed |= BTN_INTERACT;
                if (!jump && prevJump) released |= BTN_JUMP;
                if (!interact && prevInteract) released |= BTN_INTERACT;

                buttons.PressedThisUpdateMask = (buttons.PressedThisUpdateMask & ~ours) | pressed;
                buttons.PressedThisFixedUpdateMask = (buttons.PressedThisFixedUpdateMask & ~ours) | pressed;
                buttons.ReleasedThisUpdateMask = (buttons.ReleasedThisUpdateMask & ~ours) | released;
                buttons.ReleasedThisFixedUpdateMask = (buttons.ReleasedThisFixedUpdateMask & ~ours) | released;
            }

            data.Buttons = buttons;
            handler.rawData = data;
        }

        /// <summary>
        /// Zeroes the injected inputs in rawData. The input handler is event-driven, so
        /// without this the last injected input (e.g. W held) keeps driving the player until
        /// a real input event arrives.
        /// </summary>
        private void ClearRawInputData()
        {
            try
            {
                var handler = _finder.GetInputHandler();
                if (handler == null) return;

                const int ours = BTN_JUMP | BTN_INTERACT;
                var data = handler.rawData;
                data.moveInputSBytes = new EHS.Vector2SByte(0, 0);
                data.lookInputSBytes = new EHS.Vector2SByte(0, 0);
                var buttons = data.Buttons;
                buttons.HeldMask &= ~ours;
                buttons.PressedThisUpdateMask &= ~ours;
                buttons.PressedThisFixedUpdateMask &= ~ours;
                buttons.ReleasedThisUpdateMask &= ~ours;
                buttons.ReleasedThisFixedUpdateMask &= ~ours;
                data.Buttons = buttons;
                handler.rawData = data;
            }
            catch (Exception ex)
            {
                TASPlugin.WarnOnce("clear-raw", $"Could not clear injected input: {ex.Message}");
            }
        }

        // ═════════════════════════════════════════════════════════════════
        //  Savestates
        // ═════════════════════════════════════════════════════════════════

        private SavestateSystem.SaveStateData CaptureState()
        {
            var player = _finder.FindPlayerTransform();
            if (player == null) return null;
            var rb = _finder.GetCachedPlayerRigidbody();
            var cam = Camera.main;
            TryGetOrbitalAxes(out float pan, out float tilt);

            return new SavestateSystem.SaveStateData(
                rb != null ? rb.position : player.position,
                rb != null ? rb.rotation : player.rotation,
                rb != null ? rb.linearVelocity : Vector3.zero,
                rb != null ? rb.angularVelocity : Vector3.zero,
                cam != null ? cam.transform.rotation : Quaternion.identity,
                cam != null ? cam.transform.position : Vector3.zero,
                pan, tilt);
        }

        private void ApplyState(SavestateSystem.SaveStateData s)
        {
            ApplyBodyState(s.PlayerPosition, s.PlayerRotation, s.PlayerVelocity, s.PlayerAngularVelocity);
            ResetCinemachineDamping();
            SetOrbitalAxes(s.CameraPan, s.CameraTilt);
            ForceCinemachineUpdate();
        }

        private void ApplyBodyState(Vector3 pos, Quaternion rot, Vector3 vel, Vector3 angVel)
        {
            var player = _finder.FindPlayerTransform();
            var rb = _cachedRb != null ? _cachedRb : _finder.GetCachedPlayerRigidbody();
            if (rb != null)
            {
                rb.position = pos;
                rb.rotation = rot;
                rb.linearVelocity = vel;
                rb.angularVelocity = angVel;
            }
            if (player != null)
            {
                player.position = pos;
                player.rotation = rot;
            }
            Physics.SyncTransforms();
        }

        private void SaveManualState()
        {
            var s = CaptureState();
            if (s == null) { TASGui.Toast("Player not found", TASGui.Danger); return; }
            _saves.SetManual(s, _time.CurrentTick);
            TASGui.Toast($"Savestate saved (tick {_time.CurrentTick})", TASGui.Ok);
        }

        /// <summary>
        /// Idle: teleport to the savestate. Recording/Edit Mode: jump to its tick inside the
        /// recording (non-destructive, same as stepping there). Replay: refused — it would
        /// fight the state injection.
        /// </summary>
        private void LoadManualState()
        {
            if (!_saves.HasSavedState) { TASGui.Toast("No savestate yet", TASGui.TextDim); return; }

            if (_macro.IsPlaying)
            {
                TASGui.Toast("Stop the replay before loading a savestate", TASGui.Pause);
            }
            else if (_macro.IsRecording)
            {
                _time.SetPaused(true);
                if (NavigateTo(_saves.ManualTick))
                    TASGui.Toast($"Jumped to savestate tick {_saves.ManualTick} — input forks from here", TASGui.Edit);
                else
                    TASGui.Toast($"Savestate tick {_saves.ManualTick} isn't part of this recording", TASGui.Pause);
            }
            else
            {
                ApplyState(_saves.Manual);
                _time.SetTick(_saves.ManualTick);
                TASGui.Toast($"Savestate loaded (tick {_saves.ManualTick})", TASGui.Ok);
            }
        }

        // ═════════════════════════════════════════════════════════════════
        //  Public API for the TAS Editor
        // ═════════════════════════════════════════════════════════════════

        public InputMacroSystem MacroSystem => _macro;
        public ulong CurrentTick => _time.CurrentTick;
        public bool IsPaused => _time.IsPaused;

        public InputPins Pins
        {
            get => _pins;
            set => _pins = value;
        }

        public void PauseGame() => _time.SetPaused(true);
        public void TogglePause() => _time.TogglePause();
        public void EditorStepForward() => StepForward(justPressed: true);
        public void EditorStepBack() => StepBack();

        /// <summary>
        /// Plays the macro from its start state. The hotkey keeps the current pause state
        /// (so you can frame-step a replay); the editor's Play runs it.
        /// </summary>
        public bool PlayFromStart(bool unpause)
        {
            if (!_macro.HasRecordedData) return false;
            if (_macro.IsRecording) _macro.StopRecording();
            if (_macro.IsPlaying) StopPlayback();

            // The start savestate must sit right before the first row; after editor surgery
            // or an import it may not — rebuild it from the first recorded row then.
            var start = _saves.MacroStart;
            ulong startTick = _saves.MacroStartTick;
            if (start == null || startTick + 1 < _macro.FirstTick || startTick > _macro.MaxTick)
            {
                startTick = _macro.FirstTick;
                start = SavestateSystem.SaveStateData.FromTick(_macro.GetStateAtTick(startTick).Value);
                _saves.SetMacroStart(start, startTick);
            }

            ApplyState(start);
            _time.SetTick(startTick);
            StartPlayback(startTick);
            if (unpause) _time.SetPaused(false);
            return true;
        }

        public void StopPlaybackFromEditor()
        {
            if (_macro.IsPlaying) StopPlayback();
        }

        /// <summary>
        /// Moves to a recorded tick, paused. In Edit Mode/recording this navigates the run
        /// non-destructively; otherwise it enters a paused replay positioned at that tick.
        /// </summary>
        public bool GoToTick(ulong tick)
        {
            if (_macro.GetStateAtTick(tick) == null) return false;
            _time.SetPaused(true);

            if (_macro.IsRecording)
            {
                if (!NavigateTo(tick)) return false;
                if (!_macro.IsEditMode) _macro.EnterEditMode(tick);
                return true;
            }

            if (!_macro.IsPlaying) StartPlayback(tick);
            if (!NavigateTo(tick)) return false;
            _macro.SeekPlayback(tick);
            return true;
        }

        /// <summary>Enters Edit Mode positioned (paused) on `tick`; nothing is dropped yet.</summary>
        public bool BeginEditAt(ulong tick)
        {
            if (_macro.GetStateAtTick(tick) == null) return false;
            _time.SetPaused(true);
            if (_macro.IsPlaying) StopPlayback();
            if (!NavigateTo(tick)) return false;
            if (!_macro.IsEditMode) _macro.EnterEditMode(tick);
            return true;
        }

        /// <summary>Rebuilds the macro start from the first row (after a file import).</summary>
        public void OnMacroReplaced()
        {
            if (!_macro.HasRecordedData) { _saves.SetMacroStart(null, 0); return; }
            ulong first = _macro.FirstTick;
            _saves.SetMacroStart(SavestateSystem.SaveStateData.FromTick(_macro.GetStateAtTick(first).Value), first);
        }

        /// <summary>Re-syncs a paused replay/Edit Mode cursor after the editor changed rows.</summary>
        public void OnMacroEdited()
        {
            if (!_macro.IsPlaying && !_macro.IsRecording) return;
            if (_time.CurrentTick > _macro.MaxTick)
            {
                if (_macro.IsPlaying) StopPlayback();
                return;
            }
            if (!_time.IsPaused) return;
            NavigateTo(_time.CurrentTick);
            if (_macro.IsPlaying) _macro.SeekPlayback(_time.CurrentTick);
        }

        /// <summary>Discards the macro entirely (editor "New").</summary>
        public void DiscardMacro()
        {
            if (_macro.IsPlaying) StopPlayback();
            _macro.Clear();
            _saves.SetMacroStart(null, 0);
            _pins = default;
        }

        // ═════════════════════════════════════════════════════════════════
        //  Camera / Cinemachine
        // ═════════════════════════════════════════════════════════════════

        private static Quaternion Normalized(Quaternion q)
        {
            q.Normalize();
            return q;
        }

        private Unity.Cinemachine.CinemachineCamera GetCinemachineCamera()
        {
            var movement = _finder.GetMovement();
            if (movement == null) return null;
            var camManager = movement.camManager;
            return camManager != null ? camManager.MainCinemachineCamera : null;
        }

        /// <summary>Reads the player's camera orbit (OrbitalFollow, else PanTilt).</summary>
        private bool TryGetOrbitalAxes(out float pan, out float tilt)
        {
            pan = 0f; tilt = 0f;
            try
            {
                var cinCam = GetCinemachineCamera();
                if (cinCam == null) return false;
                var orbital = cinCam.GetComponent<Unity.Cinemachine.CinemachineOrbitalFollow>();
                if (orbital != null)
                {
                    pan = orbital.HorizontalAxis.Value;
                    tilt = orbital.VerticalAxis.Value;
                    return true;
                }
                var panTilt = cinCam.GetComponent<Unity.Cinemachine.CinemachinePanTilt>();
                if (panTilt != null)
                {
                    pan = panTilt.PanAxis.Value;
                    tilt = panTilt.TiltAxis.Value;
                    return true;
                }
            }
            catch (Exception ex)
            {
                TASPlugin.WarnOnce("axes-read", $"Could not read the camera orbit: {ex.Message}");
            }
            return false;
        }

        /// <summary>Writes the player's camera orbit (OrbitalFollow, else PanTilt).</summary>
        private void SetOrbitalAxes(float pan, float tilt)
        {
            if (float.IsNaN(pan) || float.IsInfinity(pan) || float.IsNaN(tilt) || float.IsInfinity(tilt)) return;
            try
            {
                var cinCam = GetCinemachineCamera();
                if (cinCam == null) return;

                var orbital = cinCam.GetComponent<Unity.Cinemachine.CinemachineOrbitalFollow>();
                if (orbital != null)
                {
                    var h = orbital.HorizontalAxis; h.Value = pan; orbital.HorizontalAxis = h;
                    var v = orbital.VerticalAxis;   v.Value = tilt; orbital.VerticalAxis = v;
                    return;
                }
                var panTilt = cinCam.GetComponent<Unity.Cinemachine.CinemachinePanTilt>();
                if (panTilt != null)
                {
                    var p = panTilt.PanAxis;  p.Value = pan;  panTilt.PanAxis = p;
                    var t = panTilt.TiltAxis; t.Value = tilt; panTilt.TiltAxis = t;
                    return;
                }
                TASPlugin.WarnOnce("axes-missing", "Camera has neither CinemachineOrbitalFollow nor CinemachinePanTilt.");
            }
            catch (Exception ex)
            {
                TASPlugin.WarnOnce("axes-write", $"Could not set the camera orbit: {ex.Message}");
            }
        }

        /// <summary>Re-enabling the virtual camera makes Cinemachine re-evaluate it at once.</summary>
        private void ForceCinemachineUpdate()
        {
            try
            {
                var cinCam = GetCinemachineCamera();
                if (cinCam == null) return;
                cinCam.enabled = false;
                cinCam.enabled = true;
            }
            catch (Exception ex)
            {
                TASPlugin.WarnOnce("cm-refresh", $"Could not refresh the Cinemachine camera: {ex.Message}");
            }
        }

        /// <summary>Makes the next camera update snap instead of damping from the old pose.</summary>
        private void ResetCinemachineDamping()
        {
            try
            {
                var cinCam = GetCinemachineCamera();
                if (cinCam != null) cinCam.PreviousStateIsValid = false;
            }
            catch (Exception ex)
            {
                TASPlugin.WarnOnce("cm-damping", $"Could not reset camera damping: {ex.Message}");
            }
        }

        private static void SetCinemachineBrain(bool on)
        {
            try
            {
                var cam = Camera.main;
                if (cam == null) return;
                var brain = cam.GetComponent<Unity.Cinemachine.CinemachineBrain>();
                if (brain != null) brain.enabled = on;
            }
            catch (Exception ex)
            {
                TASPlugin.WarnOnce("cm-brain", $"Could not toggle the Cinemachine brain: {ex.Message}");
            }
        }

        // ═════════════════════════════════════════════════════════════════
        //  HUD
        // ═════════════════════════════════════════════════════════════════

        private void UpdateHud()
        {
            var hud = new HudState
            {
                Recording = _macro.IsRecording,
                Editing = _macro.IsEditMode,
                Playing = _macro.IsPlaying,
                Paused = _time.IsPaused,
                SpeedFactor = _time.SpeedFactor,
                SlowMo = _time.IsSlowMo,
                FastForward = _time.IsFastForward,
                Tick = _time.CurrentTick,
                MaxTick = _macro.MaxTick,
                Rows = _macro.RecordedInputs.Count,
                HasSavestate = _saves.HasSavedState,
                SavestateTick = _saves.ManualTick,
                Pins = _pins,
            };

            var rb = _cachedRb != null ? _cachedRb : _finder.GetCachedPlayerRigidbody();
            if (rb != null)
            {
                hud.Position = rb.position;
                hud.Speed = rb.linearVelocity.magnitude;
            }
            else
            {
                var t = _finder.FindPlayerTransform();
                if (t != null) hud.Position = t.position;
            }

            // Live held input for the input display (what the next frame-advance records).
            try
            {
                var handler = _finder.GetInputHandler();
                if (handler != null)
                {
                    var rd = handler.rawData;
                    hud.LiveMoveX = rd.moveInputSBytes.X;
                    hud.LiveMoveY = rd.moveInputSBytes.Y;
                    hud.LiveJump = (rd.Buttons.HeldMask & BTN_JUMP) != 0;
                    hud.LiveInteract = (rd.Buttons.HeldMask & BTN_INTERACT) != 0;
                }
            }
            catch (Exception ex)
            {
                TASPlugin.WarnOnce("hud-input", $"Could not read live input for the HUD: {ex.Message}");
            }

            _overlay.SetState(hud);
        }

        // ═════════════════════════════════════════════════════════════════
        //  Setup
        // ═════════════════════════════════════════════════════════════════

        private static void ApplyDeterministicSettings()
        {
            // One physics step per rendered frame at most: a frame hitch must not run a
            // burst of ticks the frame-stepper and the recorder can't see individually.
            Time.maximumDeltaTime = Time.fixedDeltaTime;
        }

        private void SubscribeToFishNet()
        {
            try
            {
                var timeManager = FishNet.InstanceFinder.TimeManager;
                if (timeManager == null) return;

                UnsubscribeFromFishNet();
                _fishNetTimeManager = timeManager;
                _prePhysicsDelegate = (Il2CppSystem.Action<float>)((float delta) => OnPrePhysicsSimulation(delta));
                timeManager.OnPrePhysicsSimulation += _prePhysicsDelegate;
                _preTickDelegate = (Il2CppSystem.Action)(() => OnPreTick());
                timeManager.OnPreTick += _preTickDelegate;
            }
            catch (Exception ex)
            {
                TASPlugin.WarnOnce("fishnet-sub", $"Could not hook FishNet's TimeManager (will retry): {ex.Message}");
            }
        }

        private void UnsubscribeFromFishNet()
        {
            try
            {
                if (_fishNetTimeManager != null)
                {
                    if (_prePhysicsDelegate != null) _fishNetTimeManager.OnPrePhysicsSimulation -= _prePhysicsDelegate;
                    if (_preTickDelegate != null) _fishNetTimeManager.OnPreTick -= _preTickDelegate;
                }
            }
            catch (Exception ex)
            {
                // Expected when the TimeManager was already destroyed with its scene.
                TASPlugin.WarnOnce("fishnet-unsub", $"FishNet unsubscribe failed (TimeManager gone?): {ex.Message}");
            }
            finally
            {
                _fishNetTimeManager = null;
                _prePhysicsDelegate = null;
                _preTickDelegate = null;
            }
        }
    }
}
