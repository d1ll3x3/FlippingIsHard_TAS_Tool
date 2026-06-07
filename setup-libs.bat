@echo off
REM Setup script to copy BepInEx and Unity DLLs to lib folder
REM Automatically searches for the game in common Steam library locations

echo Setting up BepInEx TAS Tool build environment...
echo.

REM Create lib folder
if not exist "lib" mkdir lib

REM Try to find the game in common Steam library locations
set "GAME_PATH="

REM Check common Steam library paths
for %%D in (C: D: E: F: G: H: I: J: K: L: M: N: O: P: Q: R: S: T: U: V: W: X: Y: Z:) do (
    if exist "%%D\SteamLibrary\steamapps\common\Flipping is Hard Demo\BepInEx" (
        set "GAME_PATH=%%D\SteamLibrary\steamapps\common\Flipping is Hard Demo"
        goto found
    )
    if exist "%%D\Program Files\Steam\steamapps\common\Flipping is Hard Demo\BepInEx" (
        set "GAME_PATH=%%D\Program Files\Steam\steamapps\common\Flipping is Hard Demo"
        goto found
    )
    if exist "%%D\Program Files (x86)\Steam\steamapps\common\Flipping is Hard Demo\BepInEx" (
        set "GAME_PATH=%%D\Program Files (x86)\Steam\steamapps\common\Flipping is Hard Demo"
        goto found
    )
)

:found
if not exist "%GAME_PATH%" (
    echo ERROR: Could not find game installation automatically.
    echo.
    echo Please make sure:
    echo   1. BepInEx IL2CPP is installed in your game folder
    echo   2. The game is in a standard Steam library location
    echo   3. You have run the game at least once to generate interop DLLs
    echo.
    echo If you have a custom Steam library path, edit setup-libs.ps1 and run that instead.
    echo.
    pause
    exit /b 1
)

echo Found game at: "%GAME_PATH%"
echo.

set INTEROP_PATH=%GAME_PATH%\BepInEx\interop
if not exist "%INTEROP_PATH%" (
    echo ERROR: BepInEx\interop folder not found.
    echo You must launch the game at least once after installing BepInEx so it generates interop assemblies.
    pause
    exit /b 1
)

echo Copying DLLs...

set DLLS=Assembly-CSharp.dll EHS.Core.Components.dll FishNet.Runtime.dll Il2Cppmscorlib.dll Il2CppSystem.dll Unity.Cinemachine.dll Unity.InputSystem.dll UnityEngine.CoreModule.dll UnityEngine.IMGUIModule.dll UnityEngine.InputLegacyModule.dll UnityEngine.PhysicsModule.dll UnityEngine.TextRenderingModule.dll

for %%f in (%DLLS%) do (
    if exist "%INTEROP_PATH%\%%f" (
        copy "%INTEROP_PATH%\%%f" "lib\" >nul
        echo   - %%f copied
    ) else (
        echo   - ERROR: %%f not found
    )
)

echo.
echo Setup complete! You can now run "dotnet build" or use build.bat.
echo.
pause
