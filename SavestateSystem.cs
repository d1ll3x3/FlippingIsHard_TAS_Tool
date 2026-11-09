using UnityEngine;

namespace FlippingIsHardTAS
{
    /// <summary>
    /// Two snapshot slots: the user's manual savestate, and the macro start (where the run
    /// was recorded from, restored before every playback). Capturing and applying the
    /// snapshot to the player/camera lives in TASController, which owns those references.
    /// </summary>
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

            public SaveStateData(Vector3 pos, Quaternion rot, Vector3 vel, Vector3 angVel, Quaternion camRot, Vector3 camPos, float pan, float tilt)
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

            public static SaveStateData FromTick(TASInputState s) => new SaveStateData(
                s.PlayerPosition, s.PlayerRotation, s.PlayerVelocity, s.PlayerAngularVelocity,
                s.CameraRotation, s.CameraPosition, s.CameraPan, s.CameraTilt);
        }

        public SaveStateData Manual { get; private set; }
        public ulong ManualTick { get; private set; }
        public bool HasSavedState => Manual != null;

        public SaveStateData MacroStart { get; private set; }
        public ulong MacroStartTick { get; private set; }
        public bool HasMacroState => MacroStart != null;

        public void SetManual(SaveStateData state, ulong tick)
        {
            Manual = state;
            ManualTick = tick;
        }

        public void SetMacroStart(SaveStateData state, ulong tick)
        {
            MacroStart = state;
            MacroStartTick = tick;
        }

        public void Clear()
        {
            Manual = null;
            ManualTick = 0;
            MacroStart = null;
            MacroStartTick = 0;
        }
    }
}
