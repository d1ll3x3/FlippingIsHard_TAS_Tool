# Release v1.2.1 - Camera Sync Improvements

## 🎯 Major Fixes

### Camera System Overhaul
- **Fixed camera desync when starting playback (F10)**: Camera now correctly snaps to the first recorded tick of the macro instead of the savestate values
- **Fixed camera desync when loading savestates (R)**: Camera axes are now injected with forced Cinemachine update for instant positioning
- **Fixed menu (B) camera jump bug**: CinemachineBrain is now properly disabled while menu is open to prevent position overrides

### Edit Mode (F8) Improvements
- **Preserved cut-point tick**: Edit Mode now keeps the tick where you cut (instead of deleting it) to maintain perfect camera continuity
- Edit Mode camera behavior is now the reference implementation - all other actions (F10, R, Import) follow the same approach

### Technical Changes
- Removed manual Camera.main.transform writing during playback (was causing flickering)
- CinemachineBrain stays active during replay - only orbital axes are injected (proper TAS methodology)
- Added `ForceCinemachineUpdate()` to eliminate 1-frame camera lag on state transitions
- Fixed macro start tick detection to match savestate tick exactly

## 📁 Files Changed
- `TASController.cs`: Complete camera handling rewrite (~200 lines of camera override code removed)
- `InputMacroSystem.cs`: Edit Mode now preserves cut tick for continuity
- `TASBindMenuRenderer.cs`: Menu now disables CinemachineBrain to prevent camera movement

## 📝 Documentation Added
- `SOLUTION.md`: Explains the proper TAS approach (Option 1: Brain active, axes-only)
- `DIAGNOSIS.md`: Details why manual camera writing causes flickering

## ⚠️ Known Issues
- Camera may have slight jitter during playback due to Cinemachine's internal damping (not accessible in this version)
- First tick of macro must be recorded at the same tick as savestate creation for perfect sync

## 🔧 Installation
Same as before - drop `FlippingIsHardTAS.dll` into `BepInEx/plugins/FlippingIsHardTAS/`

---

**Full Changelog**: https://github.com/d1ll3x3/FlippingIsHard_TAS_Tool/compare/v1.2.0...v1.2.1
