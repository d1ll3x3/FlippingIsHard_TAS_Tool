using UnityEngine;

namespace FlippingIsHardTAS
{
    public struct TASInputState
    {
        public Vector2 Move;
        public Vector2 Look;
        public Quaternion CameraRotation;
        public Vector3 CameraPosition;
        public bool Jump;
        public bool Interact;
        
        // Full physics state — recorded every tick and re-injected on replay
        // to guarantee determinism regardless of FishNet reconciliation
        public Vector3 PlayerPosition;
        public Quaternion PlayerRotation;
        public Vector3 PlayerVelocity;
        public Vector3 PlayerAngularVelocity;
        
        // Orbital camera axes — saved per tick so playback keeps them in sync with Camera.main.transform
        public float CameraPan;
        public float CameraTilt;

        public TASInputState(Vector2 move, Vector2 look, Quaternion camRot, Vector3 camPos,
                             bool jump, bool interact,
                             Vector3 playerPos, Quaternion playerRot,
                             Vector3 playerVel, Vector3 playerAngVel,
                             float camPan = 0f, float camTilt = 0f)
        {
            Move = move;
            Look = look;
            CameraRotation = camRot;
            CameraPosition = camPos;
            Jump = jump;
            Interact = interact;
            PlayerPosition = playerPos;
            PlayerRotation = playerRot;
            PlayerVelocity = playerVel;
            PlayerAngularVelocity = playerAngVel;
            CameraPan = camPan;
            CameraTilt = camTilt;
        }
    }
}
