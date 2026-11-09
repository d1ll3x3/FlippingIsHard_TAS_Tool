using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace FlippingIsHardTAS
{
    public static class TASMacroSerializer
    {
        // TAS4 adds the exact rawData sbytes per tick. TAS3 added the (now retired)
        // greenzone field; TAS2 predates it. All three still import.
        private const string MAGIC_HEADER = "TAS4";
        private const string HEADER_TAS3 = "TAS3";
        private const string HEADER_TAS2 = "TAS2";

        // A row is ~110 bytes; anything above this is a corrupt count, not a real run.
        private const int MAX_ROWS = 10_000_000;

        public static void ExportToFile(InputMacroSystem sys, string filePath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));

            // Write to a temp file and swap it in, so a crash or a full disk mid-save never
            // destroys the previous version of the macro.
            string tmp = filePath + ".tmp";
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(MAGIC_HEADER);
                writer.Write(sys.RNGSeed);
                writer.Write(sys.MaxTick);
                writer.Write(sys.MaxTick);   // legacy greenzone slot — always the full run now
                writer.Write(sys.RecordedInputs.Count);

                foreach (var kvp in sys.RecordedInputs.OrderBy(k => k.Key))
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

                    writer.Write(state.MoveXRaw);
                    writer.Write(state.MoveYRaw);
                    writer.Write(state.LookXRaw);
                    writer.Write(state.LookYRaw);
                }
            }
            File.Move(tmp, filePath, overwrite: true);
        }

        /// <summary>
        /// Reads a .tas file into `sys`. The file is parsed completely before anything is
        /// replaced, so a corrupt or truncated file leaves the current macro untouched.
        /// </summary>
        public static bool ImportFromFile(InputMacroSystem sys, string filePath, out string error)
        {
            error = null;
            if (!File.Exists(filePath)) { error = "file not found"; return false; }

            try
            {
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                using (var reader = new BinaryReader(stream))
                {
                    var header = reader.ReadString();
                    if (header != MAGIC_HEADER && header != HEADER_TAS3 && header != HEADER_TAS2)
                    {
                        error = "not a TAS macro file";
                        return false;
                    }

                    int seed = reader.ReadInt32();
                    ulong maxTick = reader.ReadUInt64();
                    if (header != HEADER_TAS2) reader.ReadUInt64();   // legacy greenzone, ignored
                    int count = reader.ReadInt32();
                    if (count <= 0 || count > MAX_ROWS)
                    {
                        error = count == 0 ? "the macro is empty" : $"corrupt row count ({count})";
                        return false;
                    }

                    var rows = new Dictionary<ulong, TASInputState>(count);
                    ulong highest = 0;
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

                        // Legacy files only stored floats; the constructor already derived the
                        // raw bytes via QuantizeAxis (best effort).
                        if (header == MAGIC_HEADER)
                        {
                            state.MoveXRaw = reader.ReadSByte();
                            state.MoveYRaw = reader.ReadSByte();
                            state.LookXRaw = reader.ReadSByte();
                            state.LookYRaw = reader.ReadSByte();
                        }

                        rows[tick] = state;
                        if (tick > highest) highest = tick;
                    }

                    // Trust the rows over the header: older files could carry a stale MaxTick.
                    if (maxTick != highest)
                        TASPlugin.Logger.LogWarning($"TAS: {Path.GetFileName(filePath)} header says MaxTick {maxTick}, rows end at {highest} — using {highest}.");
                    sys.ReplaceAll(rows, highest, seed);
                    return true;
                }
            }
            catch (EndOfStreamException)
            {
                error = "the file is truncated";
            }
            catch (Exception ex)
            {
                error = ex.Message;
                TASPlugin.Logger.LogError($"TAS: import of {filePath} failed: {ex}");
            }
            return false;
        }

        private static void WriteVector2(BinaryWriter w, Vector2 v)
        {
            w.Write(v.x);
            w.Write(v.y);
        }

        private static Vector2 ReadVector2(BinaryReader r)
            => new Vector2(r.ReadSingle(), r.ReadSingle());

        private static void WriteVector3(BinaryWriter w, Vector3 v)
        {
            w.Write(v.x);
            w.Write(v.y);
            w.Write(v.z);
        }

        private static Vector3 ReadVector3(BinaryReader r)
            => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        private static void WriteQuaternion(BinaryWriter w, Quaternion q)
        {
            w.Write(q.x);
            w.Write(q.y);
            w.Write(q.z);
            w.Write(q.w);
        }

        private static Quaternion ReadQuaternion(BinaryReader r)
            => new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }
}
