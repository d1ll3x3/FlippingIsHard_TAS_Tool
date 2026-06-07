# Flipping is Hard — TAS Tool

A **BepInEx** mod for *Flipping is Hard (Demo)* that adds full TAS (Tool-Assisted Speedrun) functionality: input recording & playback, savestates, edit mode, frame advance, slow-motion, rewind, and a real-time overlay HUD.

---

## Features

| Feature | Description |
|---------|-------------|
| 🎥 **Input Recording** | Records every physics tick of movement, look, camera and physics state |
| ▶ **Deterministic Playback** | Replays inputs with full state injection before each physics tick — no desync |
| 💾 **Savestates** | Save and load player position / velocity at any moment |
| ✂️ **Edit Mode** | Cut a replay at any tick (F8) and re-record from there — keeps everything before the cut |
| ⏪ **Rewind** | Go back 1 tick during paused replay (`,`) — hold for 10/sec |
| 📁 **Macro Export & Import** | Save your recorded TAS runs to disk (`.tas` files) and load them later |
| 🔄 **Quick Restart Hook** | Full compatibility with the game's Quick Restart. Auto-pauses at tick 0 |
| ⏸ **Pause / Frame Advance** | Pause time and step exactly 1 physics tick per press; hold for 10 ticks/sec |
| 🐢 **Slow Motion** | Run game at ×0.1 speed; hold boost key for ×0.3 |
| 🖥 **HUD Overlay** | Always-on display of state, tick counter, speed, position and all keybinds |
| ⌨ **Configurable Keybinds** | In-game menu to remap every single action |

---

## Installation

1. Install **BepInEx 6** (IL2CPP) for *Flipping is Hard*
2. Build the project (`dotnet build`) or grab the latest `.dll` from [Releases](../../releases)
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
            └── (your exported .tas macro files)
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
| Slow Motion (×0.1) | `F12` |
| Slow-Mo Boost (×0.3) | `E` (hold while slow-mo active) |
| Open Settings | `B` |

All binds are remappable in-game via the **Settings menu** (`B`).

---

## Building from Source

**Requirements:** .NET 6, BepInEx 6 IL2CPP, Unity game DLLs referenced in the `.csproj`

```bash
dotnet build
```

Output: `bin/Debug/net6.0/FlippingIsHardTAS.dll`

---

## How It Works

- **Recording**: Each FishNet physics tick, the mod captures `TASInputState` (move vector, look vector, all button states, camera pose, rigidbody velocity + angular velocity + position + rotation).
- **Playback**: On `OnPrePhysicsSimulation`, the full recorded state (position, rotation, velocity) is injected into the Rigidbody *before* PhysX simulates — giving deterministic results.
- **Edit Mode**: During playback, press F8 to stop at the current tick. All macro data before that tick is preserved; everything after is replaced by your new inputs. Export the edited run when done.
- **Rewind**: While paused during replay, press `,` to step back 1 tick — loads the recorded physics state from the macro data at that tick.

---

## License

MIT — do whatever you want, credits appreciated.
