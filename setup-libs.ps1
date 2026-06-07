# Setup script to find and copy BepInEx and Unity DLLs
# Works across different Steam library locations

Write-Host "Setting up BepInEx TAS Tool build environment..." -ForegroundColor Cyan
Write-Host ""

# Create lib folder
if (-not (Test-Path "lib")) {
    New-Item -ItemType Directory -Path "lib" | Out-Null
}

# Find game installation
$gamePath = $null
$searchPaths = @(
    "I:\SteamLibrary\steamapps\common\Flipping is Hard Demo",
    "C:\SteamLibrary\steamapps\common\Flipping is Hard Demo",
    "D:\SteamLibrary\steamapps\common\Flipping is Hard Demo",
    "E:\SteamLibrary\steamapps\common\Flipping is Hard Demo",
    "C:\Program Files\Steam\steamapps\common\Flipping is Hard Demo",
    "C:\Program Files (x86)\Steam\steamapps\common\Flipping is Hard Demo",
    "D:\Program Files\Steam\steamapps\common\Flipping is Hard Demo",
    "D:\Program Files (x86)\Steam\steamapps\common\Flipping is Hard Demo"
)

foreach ($path in $searchPaths) {
    if (Test-Path "$path\BepInEx") {
        $gamePath = $path
        break
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
