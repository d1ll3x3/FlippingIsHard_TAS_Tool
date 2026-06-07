using System;
using System.IO;
using UnityEngine;

namespace FlippingIsHardTAS
{
    public static class TASMacroSerializer
    {
        private const string MAGIC_HEADER = "TAS2";

        public static void ExportToFile(InputMacroSystem sys, string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                // Write Header
                writer.Write(MAGIC_HEADER);
                
                // Write Metadata
                writer.Write(sys.RNGSeed);
                writer.Write(sys.MaxTick);
                
                // Write Data
                writer.Write(sys.RecordedInputs.Count);
                
                foreach (var kvp in sys.RecordedInputs)
                {
                    writer.Write(kvp.Key);
                    var state = kvp.Value;
                    
                    WriteVector2(writer, state.Move);
                    WriteVector2(writer, state.Look);
                    WriteQuaternion(writer, state.CameraRotation);
                    WriteVector3(writer, state.CameraPosition);
                    
                    writer.Write(state.Jump);
                    writer.Write(state.Interact);
                    
                    WriteVector3(writer, state.PlayerPosition);
                    WriteQuaternion(writer, state.PlayerRotation);
                    WriteVector3(writer, state.PlayerVelocity);
                    WriteVector3(writer, state.PlayerAngularVelocity);
                    
                    writer.Write(state.CameraPan);
                    writer.Write(state.CameraTilt);
                }
            }
        }

        public static bool ImportFromFile(InputMacroSystem sys, string filePath)
        {
            if (!File.Exists(filePath)) return false;

            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            using (var reader = new BinaryReader(stream))
            {
                var header = reader.ReadString();
                if (header != MAGIC_HEADER)
                {
                    TASPlugin.Logger.LogError("Invalid TAS macro file format.");
                    return false;
                }

                int seed = reader.ReadInt32();
                ulong maxTick = reader.ReadUInt64();
                int count = reader.ReadInt32();

                sys.Clear();
                sys.RNGSeed = seed;
                sys.MaxTick = maxTick;

                for (int i = 0; i < count; i++)
                {
                    ulong tick = reader.ReadUInt64();
                    
                    Vector2 move = ReadVector2(reader);
                    Vector2 look = ReadVector2(reader);
                    Quaternion camRot = ReadQuaternion(reader);
                    Vector3 camPos = ReadVector3(reader);
                    
                    bool jump = reader.ReadBoolean();
                    bool interact = reader.ReadBoolean();
                    
                    Vector3 pPos = ReadVector3(reader);
                    Quaternion pRot = ReadQuaternion(reader);
                    Vector3 pVel = ReadVector3(reader);
                    Vector3 pAngVel = ReadVector3(reader);
                    
                    float camPan = reader.ReadSingle();
                    float camTilt = reader.ReadSingle();

                    var state = new TASInputState(move, look, camRot, camPos, jump, interact, pPos, pRot, pVel, pAngVel, camPan, camTilt);
                    sys.RecordedInputs[tick] = state;
                }

                return true;
            }
        }

        private static void WriteVector2(BinaryWriter w, Vector2 v)
        {
            w.Write(v.x);
            w.Write(v.y);
        }

        private static Vector2 ReadVector2(BinaryReader r)
        {
            return new Vector2(r.ReadSingle(), r.ReadSingle());
        }

        private static void WriteVector3(BinaryWriter w, Vector3 v)
        {
            w.Write(v.x);
            w.Write(v.y);
            w.Write(v.z);
        }

        private static Vector3 ReadVector3(BinaryReader r)
        {
            return new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        }

        private static void WriteQuaternion(BinaryWriter w, Quaternion q)
        {
            w.Write(q.x);
            w.Write(q.y);
            w.Write(q.z);
            w.Write(q.w);
        }

        private static Quaternion ReadQuaternion(BinaryReader r)
        {
            return new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        }
    }
}
