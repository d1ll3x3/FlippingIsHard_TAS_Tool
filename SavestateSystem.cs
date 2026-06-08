using System;
using UnityEngine;
using EHS;
using Unity.Cinemachine;

namespace FlippingIsHardTAS
{
    public class SavestateSystem
    {
        public class SaveStateData
        {
            public Vector3 PlayerPosition;
            public Quaternion PlayerRotation;
            public Vector3 PlayerVelocity;
            public Vector3 PlayerAngularVelocity;
            public Quaternion CameraRotation;
            public Vector3 CameraPosition;
            public float CameraPan;
            public float CameraTilt;

            public SaveStateData(Vector3 pos, Quaternion rot, Vector3 vel, Vector3 angVel, Quaternion camRot, Vector3 camPos, float pan = 0f, float tilt = 0f)
            {
                PlayerPosition = pos;
                PlayerRotation = rot;
                PlayerVelocity = vel;
                PlayerAngularVelocity = angVel;
                CameraRotation = camRot;
                CameraPosition = camPos;
                CameraPan = pan;
                CameraTilt = tilt;
            }
        }

        // Manual Slot
        private bool _hasSavedState = false;
        private SaveStateData _savedState;
        public ulong SavedTick { get; private set; } = 0;
        public bool HasSavedState => _hasSavedState;

        // Macro Slot
        private bool _hasMacroState = false;
        private SaveStateData _macroState;
        public ulong MacroTick { get; private set; } = 0;
        public bool HasMacroState => _hasMacroState;
        
        // Last loaded state — used by TASController to retrieve camera data for restore
        private SaveStateData _lastLoadedState;
        public SaveStateData GetLastLoadedState() => _lastLoadedState;

        public void Clear()
        {
            _hasSavedState = false;
            SavedTick = 0;
            _hasMacroState = false;
            MacroTick = 0;
        }
        
        /// <summary>
        /// Directly sets the macro start state (used when importing macros).
        /// </summary>
        public void SetMacroState(SaveStateData state, ulong tick)
        {
            _macroState = state;
            MacroTick = tick;
            _hasMacroState = true;
        }

        public void SaveState(GameObjectFinder finder, ulong currentTick, bool isMacroSlot = false)
        {
            try
            {
                var playerTransform = finder.FindPlayerTransform();
                if (playerTransform != null)
                {
                    var rb = finder.GetCachedPlayerRigidbody();
                    
                    Vector3 pos = rb != null ? rb.position : playerTransform.position;
                    Quaternion rot = rb != null ? rb.rotation : playerTransform.rotation;
                    Vector3 vel = rb != null ? rb.linearVelocity : Vector3.zero;
                    Vector3 angVel = rb != null ? rb.angularVelocity : Vector3.zero;

                    Quaternion camRot = Camera.main != null ? Camera.main.transform.rotation : Quaternion.identity;
                    Vector3 camPos = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
                    
                    float pan = 0f;
                    float tilt = 0f;
                    var movement = playerTransform.GetComponent<PlayerMovement>();
                    if (movement != null && movement.camManager != null)
                    {
                        var cinCam = movement.camManager.MainCinemachineCamera;
                        if (cinCam != null)
                        {
                            var panTilt = cinCam.GetComponent<Unity.Cinemachine.CinemachinePanTilt>();
                            var orbital = cinCam.GetComponent<Unity.Cinemachine.CinemachineOrbitalFollow>();

                            if (panTilt != null)
                            {
                                pan = panTilt.PanAxis.Value;
                                tilt = panTilt.TiltAxis.Value;
                            }
                            else if (orbital != null)
                            {
                                pan = orbital.HorizontalAxis.Value;
                                tilt = orbital.VerticalAxis.Value;
                            }
                        }

                    }
                    
                    var state = new SaveStateData(pos, rot, vel, angVel, camRot, camPos, pan, tilt);

                    if (isMacroSlot)
                    {
                        _macroState = state;
                        MacroTick = currentTick;
                        _hasMacroState = true;
                        TASPlugin.Logger.LogInfo($"Macro Start State saved at tick {currentTick}");
                    }
                    else
                    {
                        _savedState = state;
                        SavedTick = currentTick;
                        _hasSavedState = true;
                        TASPlugin.Logger.LogInfo($"Manual TAS State saved at tick {currentTick}");
                    }
                }
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error saving state: {ex}");
            }
        }

        public void LoadState(GameObjectFinder finder, TimeController timeCtrl = null, bool isMacroSlot = false)
        {
            if (isMacroSlot && !_hasMacroState) return;
            if (!isMacroSlot && !_hasSavedState) return;

            try
            {
                var state = isMacroSlot ? _macroState : _savedState;
                _lastLoadedState = state;
                var playerTransform = finder.FindPlayerTransform();
                if (playerTransform != null)
                {
                    var rb = finder.GetCachedPlayerRigidbody();
                    if (rb != null)
                    {
                        rb.position = state.PlayerPosition;
                        rb.rotation = state.PlayerRotation;
                        rb.linearVelocity = state.PlayerVelocity;
                        rb.angularVelocity = state.PlayerAngularVelocity;
                        
                        playerTransform.position = state.PlayerPosition;
                        playerTransform.rotation = state.PlayerRotation;
                        Physics.SyncTransforms();
                    }
                    else
                    {
                        playerTransform.position = state.PlayerPosition;
                        playerTransform.rotation = state.PlayerRotation;
                    }
                }
                
                ulong targetTick = isMacroSlot ? MacroTick : SavedTick;

                if (timeCtrl != null)
                {
                    timeCtrl.SetTick(targetTick);
                }

                TASPlugin.Logger.LogInfo($"TAS State loaded to tick {targetTick} (MacroSlot: {isMacroSlot})");
            }
            catch (Exception ex)
            {
                TASPlugin.Logger.LogError($"Error loading state: {ex}");
            }
        }
    }
}
