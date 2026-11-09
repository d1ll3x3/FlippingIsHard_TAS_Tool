# Copies the game's BepInEx interop DLLs into lib\ so the project can build.
#
# Usage:
#   .\setup-libs.ps1                                   # auto-detect the installed game
#   .\setup-libs.ps1 -GamePath "X:\...\Flipping is Hard"
#
# The interop DLLs are build-time references only. Build against the version you play:
# the full game ("Flipping is Hard") is preferred over the demo when both are installed.
# After a game update, BepInEx must regenerate its interop (see README) and you should
# re-run this script.
param(
    [string]$GamePath
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# NOTE: PowerShell variable names are case-insensitive - never reuse "$gamePath" here,
# it IS the -GamePath parameter.
$found = $null
if ($GamePath) {
    if (-not (Test-Path (Join-Path $GamePath "BepInEx\interop"))) {
        Write-Host "ERROR: no BepInEx\interop under '$GamePath'." -ForegroundColor Red
        Write-Host "Install BepInEx 6 IL2CPP there and launch the game once."
        exit 1
    }
    $found = $GamePath
}
else {
    $names = @("Flipping is Hard", "Flipping is Hard Demo")
    $roots = @("SteamLibrary\steamapps\common",
               "Program Files\Steam\steamapps\common",
               "Program Files (x86)\Steam\steamapps\common")
    $drives = Get-PSDrive -PSProvider FileSystem | ForEach-Object { $_.Root }
    :search foreach ($n in $names) {
        foreach ($d in $drives) {
            foreach ($r in $roots) {
                $candidate = Join-Path $d (Join-Path $r $n)
                if (Test-Path (Join-Path $candidate "BepInEx\interop")) { $found = $candidate; break search }
            }
        }
    }
}

if (-not $found) {
    Write-Host "ERROR: could not find the game with BepInEx interop generated." -ForegroundColor Red
    Write-Host "Run the game once with BepInEx installed, or pass -GamePath."
    exit 1
}

Write-Host "Using game at: $found" -ForegroundColor Green
New-Item -ItemType Directory -Force -Path "lib" | Out-Null

$dlls = @(
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

$interop = Join-Path $found "BepInEx\interop"
$missing = 0
foreach ($dll in $dlls) {
    $src = Join-Path $interop $dll
    if (Test-Path $src) {
        Copy-Item $src "lib\" -Force
        Write-Host "  OK  $dll"
    } else {
        Write-Host "  MISSING  $dll" -ForegroundColor Red
        $missing++
    }
}

if ($missing -gt 0) {
    Write-Host "Done with $missing missing DLL(s) - the build will fail." -ForegroundColor Yellow
    exit 1
}
Write-Host "Done. Build with build.bat or 'dotnet build -c Release'." -ForegroundColor Green
