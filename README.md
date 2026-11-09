# Flipping is Hard — TAS Tool

A **BepInEx 6 (IL2CPP)** mod for *Flipping is Hard* that turns the game into a TAS workbench: bit-perfect record & replay, frame advance, rewind, savestates, an **Edit Mode** with pinned per-frame inputs, and a piano-roll **editor** with timeline surgery.

Works on the full game and the demo (the game's input API is identical across versions — see [Updating after a game patch](#updating-after-a-game-patch)).

---

## Features

| | |
|---|---|
| 🎥 **Recording** | Every physics tick: movement, look, buttons, camera pose and orbit, full rigidbody state |
| ▶ **Bit-perfect replay** | The recorded state is injected before each physics step — the replay never desyncs |
| ⏸ **Frame advance / rewind** | Step 1 tick at a time (hold for 10/s), step back through any recorded frame |
| ✎ **Edit Mode** | Navigate a run frame-by-frame without losing anything; fork it from any frame with held keys or **pinned inputs** |
| 📌 **Input pins** | Toggle W/A/S/D/Jump/Interact (or exact analog values) in the editor; every frame-advance records them — no need to hold keys |
| 🎹 **Piano-roll editor** | One row per tick with inputs, camera, speed and height; click to seek, Shift+click to select ranges |
| 🧩 **Timeline surgery** | Insert / Delete / Copy / full-state Paste / Trim, with 30-step undo |
| 🎯 **Camera aim** | Set Pan/Tilt from a tick onward |
| 💾 **Savestates** | Save/load the player anywhere; inside a recording, loading jumps to that frame |
| 📁 **Macro files** | Save/load `.tas` files from a file list (double-click to load); atomic writes, unsaved-changes guard, automatic backup before the first edit |
| 🐢 **Speed control** | ×0.1 slow motion (hold boost for ×0.3), ×3 fast forward |
| 🔄 **Quick Restart aware** | Pauses at tick 0 once the level has reloaded; the macro is kept |
| 🏁 **Run end** | Recording stops when the run finishes and the editor opens so you can save |
| 🖥 **Compact HUD** | Mode, tick, macro length, savestate; key list on demand (`F1`); live input display while recording |
| ⌨ **Rebindable** | Every action, with conflict detection (`B`) |

---

## Workflow

1. **Record** — Quick Restart (the TAS holds at tick 0), press `F9`, unpause with `F11` (or frame-advance with `.`). `F9` again stops.
2. **Watch** — `F10` replays from the recording start. With *Pause at macro end* on (default), the replay stops on its last frame.
3. **Fix a section** — pause the replay where it goes wrong and press `F8` (Edit Mode), or click the tick in the editor and press **Edit here**.
   - `.` / `,` with no game input step through the recording — **nothing is changed**.
   - Hold a game key (or pin inputs in the editor) and press `.` → **fork**: everything after this frame is dropped and the new frame is recorded live. Keep stepping (or unpause) to record the rest.
   - In the editor, clicking a **J** / **I** cell does this for you: Edit Mode on the frame before, with that frame's inputs pinned and the button toggled. Press `.` to record it.
4. **Save** — editor → FILES → name → **Save** (`Ctrl+S`).

### Why forks instead of editing inputs in place

This game's physics + FishNet prediction + its internal *stuck/slippery* state are not deterministic enough to recompute a run from edited inputs — resimulation drifts within a few ticks. The replay is perfect precisely because it does **not** simulate: it injects the recorded state. So input changes are made the way you'd TAS any non-deterministic game: go to the frame, change the input, and re-record live from there. Timeline surgery (insert/delete/paste/trim) works on recorded states directly and plays back exactly, with a position seam at a splice.

---

## Keys

| Action | Default |
|---|---|
| Record / Stop | `F9` |
| Play / Stop | `F10` |
| Edit Mode (on/off) | `F8` |
| Reset Tick | `F5` |
| Pause / Resume | `F11` |
| Frame Advance (hold = 10/s) | `.` |
| Rewind Tick (hold = 10/s) | `,` |
| Fast Forward ×3 | `F6` |
| Slow Motion ×0.1 | `F12` |
| Slow-Mo Boost ×0.3 (hold) | `E` |
| Save State / Load State | `Shift+R` / `R` |
| TAS Editor | `T` |
| Settings | `B` |
| Key list on the HUD | `F1` |

**Editor shortcuts:** `Ctrl+Z` / `Ctrl+Y` undo/redo · `Ctrl+C` / `Ctrl+V` copy/paste frames · `Del` delete selection · `Ins` insert frames · `↑` `↓` `PgUp` `PgDn` `Home` `End` move (Shift extends the selection) · `Ctrl+S` save.

TAS hotkeys are ignored while the game's own pause/customization menu is open.

---

## Installation

1. Install **BepInEx 6 IL2CPP** (bleeding edge, be.755 or newer) for *Flipping is Hard* and launch the game once.
2. Get `FlippingIsHardTAS.dll` from [Releases](../../releases).
3. Put it in `BepInEx/plugins/FlippingIsHardTAS/`.
4. Launch the game — the HUD appears once you're in a level.

```text
Flipping is Hard/BepInEx/
├── config/
│   └── com.flippingishard.tas.json        (keybinds + options)
└── plugins/
    └── FlippingIsHardTAS/
        ├── FlippingIsHardTAS.dll
        └── Macros/                         (your .tas files)
```

---

## Building from source

Requirements: .NET SDK 6+, the game with BepInEx installed and launched once (so `BepInEx/interop` exists).

```bat
setup-libs.bat      :: copies the interop DLLs into lib\ (prefers the full game; pass a path to override)
build.bat           :: Release build + deploy to the detected game
```

or `dotnet build -c Release` → `bin/Release/net6.0/FlippingIsHardTAS.dll`.

---

## How it works

- **Recording** — each FixedUpdate the mod captures a `TASInputState`: move/look vectors, the exact `rawData` input bytes, button state, camera pose and Cinemachine orbit, and the rigidbody position/rotation/velocity/angular velocity.
- **Replay** — FishNet's `OnPrePhysicsSimulation` injects the recorded rigidbody state before PhysX runs; `OnPreTick` writes the recorded inputs into `PlayerInputHandler.rawData` so the game's button/animation reads match. No IL2CPP method is Harmony-patched (native detours crashed v0.12), so it survives game updates.
- **Pins** — while recording, pinned inputs are written into the same `rawData` field before each tick (with correct press/release edges), so the game consumes them like real input.
- **`.tas` format** — binary `TAS4` (exact input bytes per tick). `TAS3`/`TAS2` files still import. Files are parsed completely before replacing the current macro, and saved via a temp file.

---

## Updating after a game patch

The DLL is version-agnostic, but BepInEx's IL2CPP **interop** (`BepInEx/interop/`) is generated from the game's metadata and its method tokens change on every update.

> ⚠️ After the game updates, delete `BepInEx/interop/` (or reinstall BepInEx) and launch the game once so it regenerates. A stale interop crashes at `PlayerInputHandler.IsHeld` / `WasPressed`.

---
## License

[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue.svg)](https://www.gnu.org/licenses/agpl-3.0)

This project is licensed under the **GNU Affero General Public License v3.0 (AGPL-3.0)**.
See the [LICENSE](LICENSE) file for details.
