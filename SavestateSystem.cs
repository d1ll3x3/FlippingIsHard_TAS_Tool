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

            public SaveStateData(Vector3 pos, Quaternion rot, Vector3 vel, Vector3 angVel, Quaternion camRot, Vector3 camPos)
            {
                PlayerPosition = pos;
                PlayerRotation = rot;
                PlayerVelocity = vel;
                PlayerAngularVelocity = angVel;
                CameraRotation = camRot;
                CameraPosition = camPos;
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

        public void Clear()
        {
            _hasSavedState = false;
            SavedTick = 0;
            _hasMacroState = false;
            MacroTick = 0;
        }

        public void SaveState(GameObjectFinder finder, ulong currentTick, bool isMacroSlot = false)
        {
            try
            {
                var playerTransform = finder.FindPlayerTransform();
                if (playerTransform != null)
                {
                    var rb = finder.GetCachedPlayerRigidbody();
                    
                    // CRITICAL: Always save from rb.position, not transform.position.
                    // The Rigidbody center of mass can differ from the Transform pivot.
                    // Saving from transform and restoring to rb creates a constant offset desync.
                    Vector3 pos = rb != null ? rb.position : playerTransform.position;
                    Quaternion rot = rb != null ? rb.rotation : playerTransform.rotation;
                    Vector3 vel = rb != null ? rb.linearVelocity : Vector3.zero;
                    Vector3 angVel = rb != null ? rb.angularVelocity : Vector3.zero;

                    Quaternion camRot = Camera.main != null ? Camera.main.transform.rotation : Quaternion.identity;
                    Vector3 camPos = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
                    var state = new SaveStateData(pos, rot, vel, angVel, camRot, camPos);

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
                SaveStateData state = isMacroSlot ? _macroState : _savedState;

                var playerTransform = finder.FindPlayerTransform();
                if (playerTransform != null)
                {
                    var rb = finder.GetCachedPlayerRigidbody();
                    if (rb != null)
                    {
                        // CRITICAL: Only write to rb — do NOT also write to playerTransform.
                        // Two conflicting position writes cause PhysX to place the body incorrectly.
                        // Unity will sync the Transform from the Rigidbody automatically.
                        rb.position = state.PlayerPosition;
                        rb.rotation = state.PlayerRotation;
                        rb.linearVelocity = state.PlayerVelocity;
                        rb.angularVelocity = state.PlayerAngularVelocity;
                    }
                    else
                    {
                        // Fallback if no rigidbody
                        playerTransform.position = state.PlayerPosition;
                        playerTransform.rotation = state.PlayerRotation;
                    }

                    // Reset Camera Position/Rotation
                    var movement = playerTransform.GetComponent<PlayerMovement>();
                    if (movement != null && movement.camManager != null)
                    {
                        var cinCam = movement.camManager.MainCinemachineCamera;
                        if (cinCam != null)
                        {
                            cinCam.ForceCameraPosition(state.CameraPosition, state.CameraRotation);
                        }
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
