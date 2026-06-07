# Flipping is Hard — TAS Tool

A **BepInEx** mod for *Flipping is Hard (Demo)* that adds full TAS (Tool-Assisted Speedrun) functionality: input recording & playback, savestates, frame advance, slow-motion, and a real-time overlay HUD.

---

## Features

| Feature | Description |
|---------|-------------|
| 🎥 **Input Recording** | Records every physics tick of movement, look, camera and physics state |
| ▶ **Deterministic Playback** | Replays inputs with full state injection before each physics tick — no desync |
| 💾 **Savestates** | Save and load player position / velocity at any moment |
| 📁 **Macro Export & Import** | Save your recorded TAS runs to disk (`.tas` files) and load them later. Includes an integrated custom naming text field |
| 🔄 **Quick Restart Hook** | Full compatibility with the game's Quick Restart. The TAS resets and auto-pauses at tick 0 instantly |
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
Your installation should look exactly like this:
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
| Pause / Resume | `F11` |
| Frame Advance | `.` (hold = 10/s) |
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
- **Playback**: On `OnPrePhysicsSimulation`, the full recorded state (position, rotation, velocity) is injected into the Rigidbody *before* PhysX simulates — giving deterministic results and letting `RigidbodyInterpolation.Interpolate` produce smooth visuals between 50 Hz physics ticks and high-refresh rendering.
- **Offline mode**: FishNet's multiplayer stack is kept active but forced into offline/host mode; the LAN discovery service is suppressed.

---

## License

MIT — do whatever you want, credits appreciated.