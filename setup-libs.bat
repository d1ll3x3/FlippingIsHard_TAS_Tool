@echo off
REM Copies the game's BepInEx interop DLLs into lib\ — see setup-libs.ps1.
REM Optional: setup-libs.bat "X:\path\to\Flipping is Hard"
if "%~1"=="" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-libs.ps1"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-libs.ps1" -GamePath "%~1"
)
pause
