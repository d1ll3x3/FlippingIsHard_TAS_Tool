using System;
using System.Collections.Generic;
using UnityEngine;

namespace FlippingIsHardTAS
{
    /// <summary>
    /// The recorded run: one full state (inputs + rigidbody + camera) per physics tick.
    /// Playback injects these states verbatim (bit-perfect, no resimulation), so every
    /// stored tick is always valid and "the run" is simply the rows up to MaxTick.
    /// </summary>
    public class InputMacroSystem
    {
        public bool IsRecording { get; private set; }
        public bool IsPlaying { get; private set; }
        public bool IsEditMode { get; private set; }
        public int RNGSeed { get; set; }
        public ulong MaxTick { get; private set; }

        public Dictionary<ulong, TASInputState> RecordedInputs { get; private set; } = new Dictionary<ulong, TASInputState>();

        /// <summary>Bumped on every mutation — lets the UI cache derived values (FirstTick).</summary>
        public int Version { get; private set; }

        private ulong _firstTickCache;
        private int _firstTickVersion = -1;

        /// <summary>Lowest recorded tick (recordings start wherever the tick counter was).</summary>
        public ulong FirstTick
        {
            get
            {
                if (_firstTickVersion != Version)
                {
                    _firstTickCache = 0;
                    bool any = false;
                    foreach (var k in RecordedInputs.Keys)
                        if (!any || k < _firstTickCache) { _firstTickCache = k; any = true; }
                    _firstTickVersion = Version;
                }
                return _firstTickCache;
            }
        }

        public bool HasRecordedData => RecordedInputs.Count > 0;

        /// <summary>State injected on the current playback tick, and the one before it (for button edges).</summary>
        public TASInputState Current { get; private set; }
        public TASInputState Previous { get; private set; }
        /// <summary>False when the playback tick has no row — nothing must be injected then.</summary>
        public bool CurrentValid { get; private set; }

        private void Touch() => Version++;

        // ── Lifecycle ─────────────────────────────────────────────────────

        /// <summary>Drops the run and every mode flag. Used when leaving the level.</summary>
        public void Clear()
        {
            IsRecording = false;
            IsEditMode = false;
            IsPlaying = false;
            MaxTick = 0;
            RecordedInputs.Clear();
            Current = default;
            Previous = default;
            CurrentValid = false;
            Touch();
            SetGameInputDevices(true);
        }

        /// <summary>Leaves every mode but keeps the run (level reload / back to menu).</summary>
        public void ResetModes()
        {
            IsRecording = false;
            IsEditMode = false;
            IsPlaying = false;
            CurrentValid = false;
            SetGameInputDevices(true);
        }

        /// <summary>Replaces the whole run (file import, undo/redo).</summary>
        public void ReplaceAll(Dictionary<ulong, TASInputState> rows, ulong maxTick, int seed)
        {
            RecordedInputs = rows;
            MaxTick = maxTick;
            RNGSeed = seed;
            Touch();
        }

        public void StartRecording()
        {
            IsRecording = true;
            IsPlaying = false;
            IsEditMode = false;
            RecordedInputs.Clear();
            MaxTick = 0;
            Touch();
            RNGSeed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
            UnityEngine.Random.InitState(RNGSeed);
            TASPlugin.Logger.LogInfo($"TAS: recording started (seed {RNGSeed}).");
        }

        public void StopRecording()
        {
            IsRecording = false;
            IsEditMode = false;
            TASPlugin.Logger.LogInfo($"TAS: recording stopped — {RecordedInputs.Count} ticks.");
        }

        /// <summary>
        /// Enters Edit Mode — NON-destructive: all recorded data is kept. Frame-stepping over
        /// existing rows navigates them; the rows after the cursor are dropped only when you
        /// actually input something (fork), handled by the controller.
        /// </summary>
        public void EnterEditMode(ulong currentTick)
        {
            IsPlaying = false;
            IsEditMode = true;
            IsRecording = true;
            SetGameInputDevices(true);
            TASPlugin.Logger.LogInfo($"TAS: Edit Mode at tick {currentTick} ({RecordedInputs.Count} ticks kept, MaxTick {MaxTick}).");
        }

        public void ExitEditMode()
        {
            IsRecording = false;
            IsEditMode = false;
            TASPlugin.Logger.LogInfo($"TAS: Edit Mode off — {RecordedInputs.Count} ticks (MaxTick {MaxTick}).");
        }

        public void StartPlaying()
        {
            IsPlaying = true;
            Current = default;
            Previous = default;
            CurrentValid = false;
            UnityEngine.Random.InitState(RNGSeed);
            // Real keyboard/mouse must not reach the game during the replay.
            SetGameInputDevices(false);
            TASPlugin.Logger.LogInfo($"TAS: playback started (seed {RNGSeed}).");
        }

        public void StopPlaying()
        {
            IsPlaying = false;
            SetGameInputDevices(true);
            TASPlugin.Logger.LogInfo("TAS: playback stopped.");
        }

        /// <summary>
        /// Re-applies the playback device lock. The bind menu re-enables the devices when it
        /// closes; during a replay they must stay disabled or real input bleeds into it.
        /// </summary>
        public void ReapplyDeviceLock()
        {
            if (IsPlaying) SetGameInputDevices(false);
        }

        private static void SetGameInputDevices(bool enabled)
        {
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                var mouse = UnityEngine.InputSystem.Mouse.current;
                if (enabled)
                {
                    if (kb != null) UnityEngine.InputSystem.InputSystem.EnableDevice(kb);
                    if (mouse != null) UnityEngine.InputSystem.InputSystem.EnableDevice(mouse);
                }
                else
                {
                    if (kb != null) UnityEngine.InputSystem.InputSystem.DisableDevice(kb);
                    if (mouse != null) UnityEngine.InputSystem.InputSystem.DisableDevice(mouse);
                }
            }
            catch (Exception ex)
            {
                TASPlugin.WarnOnce("devices", $"Could not {(enabled ? "enable" : "disable")} input devices: {ex.Message}");
            }
        }

        // ── Recording / playback ──────────────────────────────────────────

        public void RecordTick(ulong currentTick, TASInputState state)
        {
            if (!IsRecording) return;
            RecordedInputs[currentTick] = state;
            if (currentTick > MaxTick) MaxTick = currentTick;
            Touch();
        }

        public void PlaybackTick(ulong currentTick)
        {
            if (!IsPlaying) return;
            Previous = Current;
            CurrentValid = RecordedInputs.TryGetValue(currentTick, out var state);
            Current = CurrentValid ? state : default;
        }

        /// <summary>Points both Current and Previous at a tick (seek/rewind: no button edge).</summary>
        public void SeekPlayback(ulong tick)
        {
            CurrentValid = RecordedInputs.TryGetValue(tick, out var st);
            Current = st;
            Previous = st;
        }

        public TASInputState? GetStateAtTick(ulong tick)
            => RecordedInputs.TryGetValue(tick, out var state) ? state : (TASInputState?)null;

        // ── Timeline surgery (editor) ─────────────────────────────────────

        /// <summary>Drops every row from this tick onward.</summary>
        public void TruncateAt(ulong tick)
        {
            var keys = new List<ulong>();
            foreach (var k in RecordedInputs.Keys)
                if (k >= tick) keys.Add(k);
            foreach (var k in keys)
                RecordedInputs.Remove(k);
            MaxTick = tick > 0 && RecordedInputs.Count > 0 ? Math.Min(MaxTick, tick - 1) : 0;
            Touch();
        }

        /// <summary>
        /// Inserts `count` frames at `tick`, shifting later rows up. The new rows duplicate the
        /// previous tick's state (replay shows a short freeze there — expected without resim).
        /// </summary>
        public void InsertTicksAt(ulong tick, int count)
        {
            if (count <= 0) return;
            if (!RecordedInputs.TryGetValue(tick > 0 ? tick - 1 : tick, out var template))
                RecordedInputs.TryGetValue(tick, out template);

            var shifted = new Dictionary<ulong, TASInputState>(RecordedInputs.Count + count);
            foreach (var kvp in RecordedInputs)
                shifted[kvp.Key >= tick ? kvp.Key + (ulong)count : kvp.Key] = kvp.Value;
            for (int i = 0; i < count; i++)
                shifted[tick + (ulong)i] = template;

            RecordedInputs = shifted;
            MaxTick += (ulong)count;
            Touch();
        }

        /// <summary>Deletes the frames from..to (inclusive), shifting later rows down.</summary>
        public void DeleteRange(ulong from, ulong to)
        {
            if (to < from) return;
            ulong n = to - from + 1;
            var shifted = new Dictionary<ulong, TASInputState>(RecordedInputs.Count);
            foreach (var kvp in RecordedInputs)
            {
                if (kvp.Key >= from && kvp.Key <= to) continue;
                shifted[kvp.Key > to ? kvp.Key - n : kvp.Key] = kvp.Value;
            }
            RecordedInputs = shifted;
            ulong removed = Math.Min(n, MaxTick >= from ? MaxTick - from + 1 : 0);
            MaxTick = MaxTick >= removed ? MaxTick - removed : 0;
            Touch();
        }

        /// <summary>
        /// Overwrites rows starting at `at` with full recorded states (inputs + physics +
        /// camera), extending the run if they go past its end. Because the replay injects
        /// state, the pasted segment plays back exactly — with a seam at its boundaries.
        /// </summary>
        public void PasteStates(ulong at, IList<TASInputState> states)
        {
            for (int i = 0; i < states.Count; i++)
            {
                ulong t = at + (ulong)i;
                RecordedInputs[t] = states[i];
                if (t > MaxTick) MaxTick = t;
            }
            Touch();
        }

        /// <summary>
        /// Sets the camera orbit (pan/tilt) on every tick from fromTick to the end. The replay
        /// drives the orbital axes from these values, so the edited aim holds until a later edit.
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
            Touch();
        }

        // ── File I/O ──────────────────────────────────────────────────────

        public void ExportMacro(string path) => TASMacroSerializer.ExportToFile(this, path);

        public bool ImportMacro(string path, out string error) => TASMacroSerializer.ImportFromFile(this, path, out error);
    }
}
