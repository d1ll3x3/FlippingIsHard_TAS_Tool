# Setup script to find and copy BepInEx and Unity DLLs
# Works across different Steam library locations and game versions.
#
# Usage:
#   .\setup-libs.ps1                       # auto-detect the installed game
#   .\setup-libs.ps1 -GamePath "X:\...\Flipping is Hard"   # build against a specific install
#
# NOTE: the copied interop DLLs are only build-time references. The game's input API is
# identical across versions, so the resulting .dll works on every version — provided each
# player's BepInEx has REGENERATED its interop for their game version (see README:
# "Updating after a game patch"). A stale interop crashes at PlayerInputHandler.IsHeld.
param(
    [string]$GamePath
)

Write-Host "Setting up BepInEx TAS Tool build environment..." -ForegroundColor Cyan
Write-Host ""

# Create lib folder
if (-not (Test-Path "lib")) {
    New-Item -ItemType Directory -Path "lib" | Out-Null
}

# Find game installation. The product folder is "Flipping is Hard Demo" on the demo and
# "Flipping is Hard" once the demo tag is dropped — search both, plus any "- copia" variants.
$gamePath = $null
if ($GamePath) {
    if (Test-Path "$GamePath\BepInEx") { $gamePath = $GamePath }
    else { Write-Host "ERROR: no BepInEx under -GamePath '$GamePath'" -ForegroundColor Red; exit 1 }
}
else {
    $drives = @("C","D","E","F","G","H","I","J","K")
    $names  = @("Flipping is Hard Demo", "Flipping is Hard")
    $roots  = @("SteamLibrary\steamapps\common",
                "Program Files\Steam\steamapps\common",
                "Program Files (x86)\Steam\steamapps\common")
    $candidates = @()
    foreach ($d in $drives) { foreach ($r in $roots) { foreach ($n in $names) {
        $candidates += "$($d):\$r\$n"
    } } }

    foreach ($path in $candidates) {
        if (Test-Path "$path\BepInEx\interop") { $gamePath = $path; break }
    }
}

if (-not $gamePath) {
    Write-Host "ERROR: Could not find game installation automatically." -ForegroundColor Red
    Write-Host ""
    Write-Host "Please make sure:" -ForegroundColor Yellow
    Write-Host "  1. BepInEx IL2CPP is installed in your game folder"
    Write-Host "  2. The game is in a standard Steam library location"
    Write-Host "  3. You have run the game at least once so BepInEx generates the interop DLLs"
    Write-Host ""
    Write-Host "If your game is elsewhere, edit this script to add your path."
    exit 1
}

Write-Host "Found game at: $gamePath" -ForegroundColor Green
Write-Host ""

Write-Host "Copying Game and Unity Interop DLLs..." -ForegroundColor Cyan

$dllsToCopy = @(
    "Assembly-CSharp.dll",
    "EHS.Core.Components.dll",
    "FishNet.Runtime.dll",
    "Il2Cppmscorlib.dll",
    "Il2CppSystem.dll",
    "Unity.Cinemachine.dll",
    "Unity.InputSystem.dll",
    "UnityEngine.CoreModule.dll",
    "UnityEngine.IMGUIModule.dll",
    "UnityEngine.InputLegacyModule.dll",
    "UnityEngine.PhysicsModule.dll",
    "UnityEngine.TextRenderingModule.dll"
)

$interopPath = "$gamePath\BepInEx\interop"
if (-not (Test-Path $interopPath)) {
    Write-Host "ERROR: BepInEx\interop folder not found." -ForegroundColor Red
    Write-Host "You must launch the game at least once after installing BepInEx so it can generate the interop assemblies."
    exit 1
}

$success = $true
foreach ($dll in $dllsToCopy) {
    $sourcePath = "$interopPath\$dll"
    if (Test-Path $sourcePath) {
        Copy-Item $sourcePath "lib\" -Force
        Write-Host "  OK: $dll copied" -ForegroundColor Green
    } else {
        Write-Host "  ERROR: $dll not found in interop folder" -ForegroundColor Red
        $success = $false
    }
}

Write-Host ""
if ($success) {
    Write-Host "Setup complete! You can now build the project." -ForegroundColor Green
} else {
    Write-Host "Setup finished with errors. Some DLLs are missing." -ForegroundColor Yellow
}
