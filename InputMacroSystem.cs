using System.Collections.Generic;
using UnityEngine;

namespace FlippingIsHardTAS
{
    public class InputMacroSystem
    {
        public bool IsRecording { get; private set; }
        public bool IsPlaying { get; private set; }
        public int RNGSeed { get; private set; }
        public ulong MaxTick { get; private set; }
        
        public Dictionary<ulong, TASInputState> RecordedInputs = new Dictionary<ulong, TASInputState>();

        private TASInputState _currentPlaybackState;
        private TASInputState _previousPlaybackState; // To calculate WasPressed

        public void Clear()
        {
            IsRecording = false;
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
            RNGSeed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
            UnityEngine.Random.InitState(RNGSeed);
            TASPlugin.Logger.LogInfo($"TAS: Started Recording Inputs with Seed: {RNGSeed}");
        }

        public void StopRecording()
        {
            IsRecording = false;
            TASPlugin.Logger.LogInfo("TAS: Stopped Recording");
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
        
        public void RecordTick(ulong currentTick, TASInputState state)
        {
            if (!IsRecording) return;
            RecordedInputs[currentTick] = state;
            if (currentTick > MaxTick) MaxTick = currentTick;
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
                return _currentPlaybackState.CameraRotation;
            
            if (Camera.main != null)
                return Camera.main.transform.rotation;
                
            return Quaternion.identity;
        }

        public Quaternion GetInterpolatedCameraRotation(float t)
        {
            if (IsPlaying && HasRecordedData)
                return Quaternion.Slerp(_previousPlaybackState.CameraRotation, _currentPlaybackState.CameraRotation, t);
            
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
                return Vector3.Lerp(_previousPlaybackState.CameraPosition, _currentPlaybackState.CameraPosition, t);
            
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
