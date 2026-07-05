# Flipping is Hard — TAS Tool

A **BepInEx** mod for *Flipping is Hard (Demo)* that adds full TAS (Tool-Assisted Speedrun) functionality: input recording & bit-perfect replay, savestates, live edit mode, a frame-by-frame piano-roll editor, frame advance, slow-motion, rewind, and a real-time overlay HUD.

---

## Features

| Feature | Description |
|---------|-------------|
| 🎥 **Input Recording** | Records every physics tick: movement, look, camera pose, button edges and full rigidbody state |
| ▶ **Deterministic Replay** | Replays a run by injecting the recorded state before each physics tick — bit-perfect, never desyncs |
| 💾 **Savestates** | Save and load player position / velocity at any moment |
| ✂️ **Edit Mode** | Drop into a run at any tick (`F8`) and navigate frame-by-frame non-destructively; fork the run from any frame by holding a game key while advancing, or by editing a cell in the editor |
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

## Edit Mode workflow

This is where you actually edit a run. Enter with `F8` (or **Edit here** in the editor) — the recording is preserved.

| Action | What happens |
|--------|-------------|
| `.` (advance, no game key held) | Step forward through recorded frames — **non-destructive**, nothing is changed |
| `,` (rewind) | Step back — **non-destructive** |
| Hold `W/A/S/D` / `Space` / `E` + press `.` | **Fork**: discards everything after the current frame and starts recording your live input from here |
| Edit a cell in the editor table (Move / Jump / Interact) | **Fork**: same as above — recording continues from that tick |

> In plain **Replay** (Play + Pause), advance/rewind navigate the recording without forking. Holding game keys there does nothing.

---

## Installation

1. Install **BepInEx 6** (IL2CPP) for *Flipping is Hard*
2. Grab the latest `FlippingIsHardTAS.dll` from [Releases](../../releases)
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
| Edit Mode | `F8` |
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
- **Edit Mode** (`F8`): preserves the full recording and lets you navigate frame-by-frame. Advance without game input = non-destructive replay step. Advance while holding a game key = fork: live recording replaces everything from that frame onward.
- **Rewind** / **Frame Advance**: while paused, step the recorded run backward/forward one tick at a time (bit-perfect state seek).

### About the editor and "resimulation"

This game's physics + networked prediction (FishNet) and its internal *stuck/slippery* mechanic are **not deterministic enough to recompute** a run from edited inputs — re-simulation drifts and snowballs. The replay is perfect precisely because it *doesn't* simulate. So the editor is built around the replay, the way you'd TAS any non-deterministic game:

- **Play / scrub / step** are bit-perfect (state injection).
- **Timeline surgery** — Insert, Delete, Copy/Paste of frames — rearranges recorded data and changes what plays back (with a possible position seam at a splice).
- **Camera aiming** — editing Pan/Tilt holds that orientation from the edited tick onward (the replay renders the camera from the orbital axes).
- **Per-cell input edits** (Move/Jump/Interact) fork the run — live recording takes over from that tick.

`.tas` files use the `TAS4` format (older `TAS3` / `TAS2` files import fine).

---

## Updating after a game patch

The mod's `.dll` is **version-agnostic** — the game's input API (`PlayerInputHandler`,
`PlayerInputData`, `PlayerButtons`, `Vector2SByte`) has identical signatures across versions,
so the same build runs on the demo and newer releases.

What is **not** version-agnostic is BepInEx's IL2CPP **interop** (`BepInEx/interop/`): it's
generated from the game's metadata and the internal method tokens shift every game update.

> ⚠️ **After the game updates, let BepInEx regenerate the interop.** Delete the
> `BepInEx/interop/` folder (or reinstall BepInEx) and launch the game once. If you keep a
> stale interop from a previous version, the mod will **crash at `PlayerInputHandler.IsHeld` /
> `WasPressed`** because the old method tokens now point at the wrong native methods.

---
## License

[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue.svg)](https://www.gnu.org/licenses/agpl-3.0)

This project is licensed under the **GNU Affero General Public License v3.0 (AGPL-3.0)**.
See the [LICENSE](LICENSE) file for details.
