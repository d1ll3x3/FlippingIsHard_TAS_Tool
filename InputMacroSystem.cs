using System.Collections.Generic;
using UnityEngine;

namespace FlippingIsHardTAS
{
    public class InputMacroSystem
    {
        public bool IsRecording { get; private set; }
        public bool IsPlaying { get; private set; }
        public bool IsEditMode { get; private set; }
        public int RNGSeed { get; set; }
        public ulong MaxTick { get; set; }

        /// <summary>
        /// Last tick whose recorded physics state is still valid ("greenzone", like TAS Studio).
        /// Up to this tick, playback injects the recorded physics state for perfect determinism.
        /// Beyond it (after an input edit in the editor), playback injects ONLY inputs and lets
        /// PhysX resimulate, re-capturing the state as it goes to extend the greenzone again.
        /// </summary>
        public ulong GreenzoneEnd { get; set; }
        
        public Dictionary<ulong, TASInputState> RecordedInputs = new Dictionary<ulong, TASInputState>();

        public void ExportMacro(string path)
        {
            TASMacroSerializer.ExportToFile(this, path);
        }

        public bool ImportMacro(string path)
        {
            return TASMacroSerializer.ImportFromFile(this, path);
        }

        private TASInputState _currentPlaybackState;
        private TASInputState _previousPlaybackState;

        public void Clear()
        {
            IsRecording = false;
            IsEditMode = false;
            GreenzoneEnd = 0;
            RecordedInputs.Clear();
            _currentPlaybackState = default;
            _previousPlaybackState = default;
            EnableInputSystemDevices();
        }

        public void StartRecording()
        {
            IsRecording = true;
            IsPlaying = false;
            RecordedInputs.Clear();
            MaxTick = 0;
            GreenzoneEnd = 0;
            RNGSeed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
            UnityEngine.Random.InitState(RNGSeed);
            TASPlugin.Logger.LogInfo($"TAS: Started Recording Inputs with Seed: {RNGSeed}");
        }

        public void StopRecording()
        {
            IsRecording = false;
            TASPlugin.Logger.LogInfo("TAS: Stopped Recording");
        }
        
        /// <summary>
        /// Enters Edit Mode at a tick — NON-destructive: all recorded data is kept. Inside Edit
        /// Mode you frame-step the recording non-destructively (forward replays, rewind rewinds);
        /// the forward ticks are dropped only when you actually input a change (handled by the
        /// controller's frame-step logic). Lets you scrub to the edit point without losing work.
        /// </summary>
        public void EnterEditMode(ulong currentTick)
        {
            IsPlaying = false;
            IsEditMode = true;
            IsRecording = true;
            EnableInputSystemDevices();
            TASPlugin.Logger.LogInfo($"TAS: Entered Edit Mode at tick {currentTick} — kept all {RecordedInputs.Count} ticks (MaxTick={MaxTick}); navigate freely, edits fork from where you change input.");
        }

        /// <summary>
        /// Exits Edit Mode: stops recording user inputs. The macro now contains
        /// the original prefix + the newly recorded suffix.
        /// </summary>
        public void ExitEditMode()
        {
            IsRecording = false;
            IsEditMode = false;
            TASPlugin.Logger.LogInfo($"TAS: Exited Edit Mode. Macro has {RecordedInputs.Count} total ticks (MaxTick={MaxTick}).");
        }
        
        public void StartPlaying()
        {
            IsPlaying = true;
            _currentPlaybackState = default;
            _previousPlaybackState = default;
            UnityEngine.Random.InitState(RNGSeed);
            TASPlugin.Logger.LogInfo($"TAS: Started Playing Inputs with Seed: {RNGSeed}");
            DisableInputSystemDevices();
        }

        public void StopPlaying()
        {
            IsPlaying = false;
            TASPlugin.Logger.LogInfo("TAS: Stopped Playing");
            EnableInputSystemDevices();
        }
        
        /// <summary>
        /// Re-applies the playback device lock. The bind menu enables the input devices
        /// unconditionally when it closes; if a replay is running they must stay disabled
        /// or real keyboard/mouse input bleeds into the playback.
        /// </summary>
        public void ReapplyDeviceLock()
        {
            if (IsPlaying) DisableInputSystemDevices();
        }

        private void DisableInputSystemDevices()
        {
            try
            {
                if (UnityEngine.InputSystem.Keyboard.current != null)
                    UnityEngine.InputSystem.InputSystem.DisableDevice(UnityEngine.InputSystem.Keyboard.current);
                if (UnityEngine.InputSystem.Mouse.current != null)
                    UnityEngine.InputSystem.InputSystem.DisableDevice(UnityEngine.InputSystem.Mouse.current);
            }
            catch { }
        }

        private void EnableInputSystemDevices()
        {
            try
            {
                if (UnityEngine.InputSystem.Keyboard.current != null)
                    UnityEngine.InputSystem.InputSystem.EnableDevice(UnityEngine.InputSystem.Keyboard.current);
                if (UnityEngine.InputSystem.Mouse.current != null)
                    UnityEngine.InputSystem.InputSystem.EnableDevice(UnityEngine.InputSystem.Mouse.current);
            }
            catch { }
        }
        
        public bool HasRecordedData => RecordedInputs.Count > 0;
        
        /// <summary>
        /// Truncates all recorded data from this tick onward. Every remaining tick keeps its
        /// valid recorded state (no resim), so the greenzone simply follows MaxTick.
        /// </summary>
        public void TruncateAt(ulong tick)
        {
            var keysToRemove = new List<ulong>();
            foreach (var kvp in RecordedInputs)
            {
                if (kvp.Key >= tick)
                    keysToRemove.Add(kvp.Key);
            }
            foreach (var key in keysToRemove)
                RecordedInputs.Remove(key);
            if (tick > 0) MaxTick = tick - 1;
            GreenzoneEnd = MaxTick;
        }

        /// <summary>
        /// Inserts a new frame at the given tick, shifting every later entry up by one.
        /// The new row duplicates the previous tick's recorded state (replay shows a small
        /// position seam at the insertion point — expected without resim).
        /// </summary>
        public void InsertTickAt(ulong tick)
        {
            var shifted = new Dictionary<ulong, TASInputState>(RecordedInputs.Count + 1);
            foreach (var kvp in RecordedInputs)
                shifted[kvp.Key >= tick ? kvp.Key + 1 : kvp.Key] = kvp.Value;

            TASInputState template;
            if (!RecordedInputs.TryGetValue(tick > 0 ? tick - 1 : tick, out template))
                RecordedInputs.TryGetValue(tick, out template);
            shifted[tick] = template;

            RecordedInputs = shifted;
            MaxTick++;
            GreenzoneEnd = MaxTick;
        }

        /// <summary>
        /// Deletes the frame at the given tick, shifting every later entry down by one.
        /// </summary>
        public void DeleteTickAt(ulong tick)
        {
            var shifted = new Dictionary<ulong, TASInputState>(RecordedInputs.Count);
            foreach (var kvp in RecordedInputs)
            {
                if (kvp.Key == tick) continue;
                shifted[kvp.Key > tick ? kvp.Key - 1 : kvp.Key] = kvp.Value;
            }

            RecordedInputs = shifted;
            if (MaxTick > 0) MaxTick--;
            GreenzoneEnd = MaxTick;
        }

        public void RecordTick(ulong currentTick, TASInputState state)
        {
            if (!IsRecording) return;
            RecordedInputs[currentTick] = state;
            if (currentTick > MaxTick) MaxTick = currentTick;
            // Real recording always captures real physics state — greenzone extends with it
            if (currentTick > GreenzoneEnd) GreenzoneEnd = currentTick;
        }

        /// <summary>
        /// Replaces the input portion of the state at a tick (editor use). Data-only: with no
        /// resimulation, this edits the stored .tas but does NOT change the replayed trajectory
        /// (the recorded physics state is what plays back). To actually change a run, re-record
        /// from this tick. The recorded physics state stays valid, so greenzone is untouched.
        /// </summary>
        public void SetInputAt(ulong tick, Vector2 move, bool jump, bool interact, float camPan, float camTilt)
        {
            if (!RecordedInputs.TryGetValue(tick, out var state)) return;

            bool changed = state.Move != move || state.Jump != jump || state.Interact != interact ||
                           state.CameraPan != camPan || state.CameraTilt != camTilt;
            if (!changed) return;

            state.Move = move;
            state.Jump = jump;
            state.Interact = interact;
            state.CameraPan = camPan;
            state.CameraTilt = camTilt;
            state.MoveXRaw = TASInputState.QuantizeAxis(move.x);
            state.MoveYRaw = TASInputState.QuantizeAxis(move.y);
            RecordedInputs[tick] = state;
        }

        /// <summary>
        /// Sets the camera orbit (pan/tilt) on EVERY tick from fromTick to the end. The replay
        /// renders the camera from these orbital axes, so the edited orientation holds from
        /// fromTick onward until a later edit (at a higher tick) overrides it. Lets the user
        /// "aim the camera and have it stay there."
        /// </summary>
        public void SetCameraFrom(ulong fromTick, float pan, float tilt)
        {
            foreach (var key in new List<ulong>(RecordedInputs.Keys))
            {
                if (key < fromTick) continue;
                var st = RecordedInputs[key];
                st.CameraPan = pan;
                st.CameraTilt = tilt;
                RecordedInputs[key] = st;
            }
        }

        /// <summary>
        /// Overwrites the FULL recorded state (inputs + physics + camera) at a tick. Used by
        /// timeline surgery (paste): the replay then reproduces the pasted segment's real
        /// trajectory, with a possible position seam at the boundary.
        /// </summary>
        public void SetFullStateAt(ulong tick, TASInputState state)
        {
            if (tick > MaxTick) return;
            RecordedInputs[tick] = state;
            if (tick > GreenzoneEnd) GreenzoneEnd = tick;
        }

        public void PlaybackTick(ulong currentTick)
        {
            if (!IsPlaying) return;

            _previousPlaybackState = _currentPlaybackState;

            if (RecordedInputs.TryGetValue(currentTick, out var state))
            {
                _currentPlaybackState = state;
            }
            else
            {
                _currentPlaybackState = default;
            }
        }

        public TASInputState? GetStateAtTick(ulong tick)
        {
            if (RecordedInputs.TryGetValue(tick, out var state))
                return state;
            return null;
        }

        public Vector2 GetCurrentMoveInput()
        {
            return _currentPlaybackState.Move;
        }

        public Vector2 GetCurrentLookInput()
        {
            return _currentPlaybackState.Look;
        }

        /// <summary>Exact rawData sbytes captured at record time (bit-identical injection).</summary>
        public (sbyte x, sbyte y) GetCurrentMoveRaw()
        {
            return (_currentPlaybackState.MoveXRaw, _currentPlaybackState.MoveYRaw);
        }

        public (sbyte x, sbyte y) GetCurrentLookRaw()
        {
            return (_currentPlaybackState.LookXRaw, _currentPlaybackState.LookYRaw);
        }

        public Quaternion GetCurrentCameraRotation()
        {
            if (IsPlaying && HasRecordedData)
            {
                var q = _currentPlaybackState.CameraRotation;
                q.Normalize();
                return q;
            }
            
            if (Camera.main != null)
                return Camera.main.transform.rotation;
                
            return Quaternion.identity;
        }

        public Quaternion GetInterpolatedCameraRotation(float t)
        {
            if (IsPlaying && HasRecordedData)
            {
                var q = Quaternion.SlerpUnclamped(_previousPlaybackState.CameraRotation, _currentPlaybackState.CameraRotation, t);
                q.Normalize();
                return q;
            }
            
            return GetCurrentCameraRotation();
        }

        public Vector3 GetCurrentCameraPosition()
        {
            if (IsPlaying && HasRecordedData)
                return _currentPlaybackState.CameraPosition;
            
            if (Camera.main != null)
                return Camera.main.transform.position;
                
            return Vector3.zero;
        }

        public Vector3 GetInterpolatedCameraPosition(float t)
        {
            if (IsPlaying && HasRecordedData)
                return Vector3.LerpUnclamped(_previousPlaybackState.CameraPosition, _currentPlaybackState.CameraPosition, t);
            
            return GetCurrentCameraPosition();
        }

        public bool GetButtonHeld(int btn)
        {
            if (btn == 4) return _currentPlaybackState.Jump;
            if (btn == 8) return _currentPlaybackState.Interact;
            return false;
        }

        public bool GetButtonPressed(int btn)
        {
            if (btn == 4) return _currentPlaybackState.Jump && !_previousPlaybackState.Jump;
            if (btn == 8) return _currentPlaybackState.Interact && !_previousPlaybackState.Interact;
            return false;
        }

        public bool GetButtonReleased(int btn)
        {
            if (btn == 4) return !_currentPlaybackState.Jump && _previousPlaybackState.Jump;
            if (btn == 8) return !_currentPlaybackState.Interact && _previousPlaybackState.Interact;
            return false;
        }

        /// <summary>
        /// Returns the exact world-space linear velocity of the player recorded during this tick.
        /// Used to override physics after input injection, ensuring deterministic replay
        /// regardless of camera orientation drift.
        /// </summary>
        public Vector3 GetCurrentPlayerVelocity()
        {
            return _currentPlaybackState.PlayerVelocity;
        }

        public Vector3 GetCurrentPlayerAngularVelocity()
        {
            return _currentPlaybackState.PlayerAngularVelocity;
        }
        
        public float GetCurrentCameraPan()
        {
            return _currentPlaybackState.CameraPan;
        }
        
        public float GetCurrentCameraTilt()
        {
            return _currentPlaybackState.CameraTilt;
        }

        /// <summary>
        /// Returns the exact world-space position recorded for this tick.
        /// Injected in OnPostTick (after FishNet Reconcile) to override any server correction.
        /// </summary>
        public Vector3 GetCurrentPlayerPosition()
        {
            return _currentPlaybackState.PlayerPosition;
        }

        /// <summary>
        /// Returns the exact world-space rotation recorded for this tick.
        /// Injected in OnPostTick (after FishNet Reconcile) to override any server correction.
        /// </summary>
        public Quaternion GetCurrentPlayerRotation()
        {
            return _currentPlaybackState.PlayerRotation;
        }
    }
}
