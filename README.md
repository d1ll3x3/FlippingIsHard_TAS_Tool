# Flipping is Hard — TAS Tool

A **BepInEx** mod for *Flipping is Hard (Demo)* that adds full TAS (Tool-Assisted Speedrun) functionality: input recording & bit-perfect replay, savestates, live edit mode, a frame-by-frame piano-roll editor, frame advance, slow-motion, rewind, and a real-time overlay HUD.

---

## Features

| Feature | Description |
|---------|-------------|
| 🎥 **Input Recording** | Records every physics tick: movement, look, camera pose, button edges and full rigidbody state |
| ▶ **Deterministic Replay** | Replays a run by injecting the recorded state before each physics tick — bit-perfect, never desyncs |
| 💾 **Savestates** | Save and load player position / velocity at any moment |
| ✂️ **Live Edit Mode** | Drop into a run at any tick (F8, or **Edit here** in the editor) and re-record from there — the reliable way to change a run |
| 🎹 **TAS Editor (piano roll)** | Frame-by-frame editor (`T`): one row per tick, scrub/step bit-perfect, timeline surgery, per-tick input editing, camera aiming, Save/Load |
| 🧩 **Timeline Surgery** | Insert / Delete / Copy / Paste frames in the editor to trim, splice and rearrange a run |
| 🎯 **Camera Aiming** | Edit Pan/Tilt at a tick and the camera holds that orientation from there on (until you change it again) |
| ⏪ **Rewind** | Go back 1 tick during paused replay (`,`) — hold for 10/sec |
| ⏩ **Fast Forward ×3** | Toggle 3× speed during replay to quickly reach edit points |
| ⏸ **Pause / Frame Advance** | Pause time and step exactly 1 physics tick per press; hold for 10 ticks/sec |
| 🐢 **Slow Motion** | Run the game at ×0.1 speed; hold boost for ×0.3 |
| 🔄 **Reset Tick** | Reset the tick counter to 0 (F5) |
| 📁 **Macro Save & Load** | Save your recorded TAS runs to disk (`.tas` files) and load them later from the editor's FILE section |
| 🏁 **Auto-stop on game end** | Recording/playback stops when the run finishes and the editor opens so you can save |
| 🔄 **Quick Restart Hook** | Full compatibility with the game's Quick Restart. Auto-pauses at tick 0 |
| 🖥 **HUD Overlay** | Always-on display of state, tick counter, recorded length, speed, position and all keybinds |
| ⌨ **Configurable Keybinds** | In-game menu (`B`) to remap every action |

---

## Installation

1. Install **BepInEx 6** (IL2CPP) for *Flipping is Hard*
2. Build the project (`dotnet build -c Release`) or grab the latest `.dll` from [Releases](../../releases)
3. Create a folder named `FlippingIsHardTAS` inside your `BepInEx/plugins/` directory.
4. Drop `FlippingIsHardTAS.dll` into the new folder.
5. Launch the game — the HUD appears immediately.

### Folder Structure
```text
Flipping is Hard Demo/BepInEx/
├── config/
│   └── com.flippingishard.tas.json
└── plugins/
    └── FlippingIsHardTAS/
        ├── FlippingIsHardTAS.dll
        └── Macros/
            └── (your saved .tas macro files)
```

---

## Default Keybinds

| Action | Key |
|--------|-----|
| Save Position | `Shift + R` |
| Load Position | `R` |
| Record Macro | `F9` |
| Play Macro | `F10` |
| Edit Macro | `F8` |
| Pause / Resume | `F11` |
| Frame Advance | `.` (hold = 10/s) |
| Rewind Tick | `,` (hold = 10/s) |
| Reset Tick | `F5` |
| Fast Forward ×3 | `F6` |
| Slow Motion (×0.1) | `F12` |
| Slow-Mo Boost (×0.3) | `E` (hold while slow-mo active) |
| Open Settings | `B` |
| Open TAS Editor | `T` |

All binds are remappable in-game via the **Settings menu** (`B`).

---

## Building from Source

**Requirements:** .NET 6, BepInEx 6 IL2CPP, Unity game DLLs referenced in the `.csproj`

```bash
dotnet build -c Release
```

Output: `bin/Release/net6.0/FlippingIsHardTAS.dll`

---

## How It Works

- **Recording**: Each FishNet physics tick the mod captures a `TASInputState` — move vector, look vector, the exact `rawData` input bytes, button edges, camera pose, and the rigidbody position / rotation / velocity / angular velocity.
- **Replay**: On `OnPrePhysicsSimulation`, the full recorded state is injected into the Rigidbody *before* PhysX runs, while `GameInputPatch` feeds the recorded inputs at the exact point the game reads them. Because it injects state rather than re-simulating, the replay is **bit-perfect**.
- **Live Edit Mode** (`F8`, or **Edit here** in the editor): seeks to a tick, then hands control back to you so you drive the player live from that exact state. Everything before the cut is kept; everything after is replaced by your new recording. This is the way to actually change a run.
- **Rewind** / **Frame Advance**: while paused, step the recorded run backward/forward one tick at a time (bit-perfect state seek).

### About the editor and "resimulation"

This game's physics + networked prediction (FishNet) and its internal *stuck/slippery* mechanic are **not deterministic enough to recompute** a run from edited inputs — re-simulation drifts and snowballs. The replay is perfect precisely because it *doesn't* simulate. So the editor is built around the replay, the way you'd TAS any non-deterministic game:

- **Play / scrub / step** are bit-perfect (state injection).
- **Timeline surgery** — Insert, Delete, Copy/Paste of frames — rearranges recorded data and changes what plays back (with a possible position seam at a splice).
- **Camera aiming** — editing Pan/Tilt holds that orientation from the edited tick onward (the replay renders the camera from the orbital axes).
- **Per-cell input edits** (Move/Jump/Interact) are **data-only** — to change the run's physics, re-record live from that tick.

`.tas` files use the `TAS4` format (older `TAS3` / `TAS2` files import fine).

---

## License

MIT — do whatever you want, credits appreciated.
