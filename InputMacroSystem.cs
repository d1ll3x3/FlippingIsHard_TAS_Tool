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
        /// Enters Edit Mode: stops playback, preserves all macro data UP TO AND INCLUDING the given tick,
        /// deletes everything AFTER this tick, and starts recording the user's real inputs from the next tick.
        /// This allows editing a macro from a specific point without losing earlier work or causing camera jumps.
        /// </summary>
        public void EnterEditMode(ulong currentTick)
        {
            // Stop playback and re-enable real input devices
            IsPlaying = false;
            IsEditMode = true;
            EnableInputSystemDevices();
            
            // Delete all recorded inputs AFTER currentTick (keep currentTick itself for continuity)
            var keysToRemove = new List<ulong>();
            foreach (var kvp in RecordedInputs)
            {
                if (kvp.Key > currentTick) // Changed from >= to >
                    keysToRemove.Add(kvp.Key);
            }
            foreach (var key in keysToRemove)
                RecordedInputs.Remove(key);
            
            // Update MaxTick to currentTick (the last preserved tick)
            MaxTick = currentTick;
            if (GreenzoneEnd > currentTick) GreenzoneEnd = currentTick;
            
            // Start recording from the NEXT tick (don't clear data!)
            IsRecording = true;
            TASPlugin.Logger.LogInfo($"TAS: Entered Edit Mode at tick {currentTick} — kept {RecordedInputs.Count} previous ticks (including cut point), now recording from tick {currentTick + 1}.");
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
        /// Re-enables input devices during resimulation. The game's input pipeline may
        /// need live devices to process ticks; recorded inputs are still injected into
        /// rawData every tick, overriding whatever the real devices produce.
        /// </summary>
        public void EnableDevicesForResim()
        {
            EnableInputSystemDevices();
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
        /// Truncates all recorded data from this tick onward.
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
            if (tick > 0 && GreenzoneEnd >= tick) GreenzoneEnd = tick - 1;
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
        /// Replaces ONLY the input portion of the state at a tick (editor use).
        /// The recorded physics state at the tick itself stays valid (it's the pre-tick state),
        /// but every state AFTER it becomes stale, so the greenzone is cut back to this tick.
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
            RecordedInputs[tick] = state;

            if (GreenzoneEnd > tick) GreenzoneEnd = tick;
        }

        /// <summary>
        /// During resimulation (playback beyond the greenzone), overwrites the stale physics/camera
        /// state at this tick with the freshly simulated one, extending the greenzone.
        /// Inputs at the tick are preserved untouched.
        /// </summary>
        public void ResimCaptureTick(ulong tick, Vector3 playerPos, Quaternion playerRot,
                                     Vector3 playerVel, Vector3 playerAngVel,
                                     Vector3 camPos, Quaternion camRot)
        {
            if (!RecordedInputs.TryGetValue(tick, out var state)) return;

            state.PlayerPosition = playerPos;
            state.PlayerRotation = playerRot;
            state.PlayerVelocity = playerVel;
            state.PlayerAngularVelocity = playerAngVel;
            state.CameraPosition = camPos;
            state.CameraRotation = camRot;
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
