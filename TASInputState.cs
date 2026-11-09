using UnityEngine;

namespace FlippingIsHardTAS
{
    /// <summary>
    /// Inputs pinned from the editor while recording / in Edit Mode. When any pin is set,
    /// the pins fully define move/jump/interact for each recorded tick (written into the
    /// game's rawData), so frame-by-frame input needs no held keys. Mouse look stays live.
    /// </summary>
    public struct InputPins
    {
        public sbyte MoveX, MoveY;
        public bool Jump, Interact;

        public bool Active => MoveX != 0 || MoveY != 0 || Jump || Interact;

        public static InputPins FromState(TASInputState s) => new InputPins
        {
            MoveX = s.MoveXRaw, MoveY = s.MoveYRaw, Jump = s.Jump, Interact = s.Interact,
        };
    }

    public struct TASInputState
    {
        // PlayerButtons.ButtonType bits (EHS) for the two buttons the TAS records.
        public const int BTN_JUMP = 4;
        public const int BTN_INTERACT = 8;

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

        // Exact rawData sbytes the game consumed this tick (re-injected verbatim on replay,
        // avoiding the float→sbyte round-trip quantization error).
        public sbyte MoveXRaw;
        public sbyte MoveYRaw;
        public sbyte LookXRaw;
        public sbyte LookYRaw;

        // Single quantization formula for edited inputs and legacy (pre-TAS4) files,
        // which only stored the float values.
        public static sbyte QuantizeAxis(float v)
            => (sbyte)Mathf.RoundToInt(Mathf.Clamp(v, -1f, 1f) * 127f);

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
            // Raw bytes default to the quantized floats; the recorder overwrites them
            // with the exact rawData values right after construction.
            MoveXRaw = QuantizeAxis(move.x);
            MoveYRaw = QuantizeAxis(move.y);
            LookXRaw = QuantizeAxis(look.x);
            LookYRaw = QuantizeAxis(look.y);
        }
    }
}
