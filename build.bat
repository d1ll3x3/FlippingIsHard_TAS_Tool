@echo off
setlocal enabledelayedexpansion
REM Builds the plugin (Release) and deploys it to the game.
REM Optional: build.bat "X:\path\to\Flipping is Hard"   (otherwise auto-detected,
REM preferring the full game over the demo).

set DLL_NAME=FlippingIsHardTAS
set DLL_PATH=bin\Release\net6.0\%DLL_NAME%.dll

set "GAME_PATH=%~1"
if not "%GAME_PATH%"=="" goto found

for %%N in ("Flipping is Hard" "Flipping is Hard Demo") do (
    for %%D in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do (
        for %%R in ("SteamLibrary\steamapps\common" "Program Files\Steam\steamapps\common" "Program Files (x86)\Steam\steamapps\common") do (
            if exist "%%D:\%%~R\%%~N\BepInEx" (
                set "GAME_PATH=%%D:\%%~R\%%~N"
                goto found
            )
        )
    )
)

echo Game with BepInEx not found. Pass the game folder: build.bat "X:\...\Flipping is Hard"
pause
exit /b 1

:found
echo Game: !GAME_PATH!
set "PLUGIN_DIR=!GAME_PATH!\BepInEx\plugins\%DLL_NAME%"

dotnet build -c Release
if %ERRORLEVEL% NEQ 0 (
    echo BUILD FAILED
    pause
    exit /b 1
)

if not exist "!PLUGIN_DIR!" mkdir "!PLUGIN_DIR!"
copy /Y "%DLL_PATH%" "!PLUGIN_DIR!\%DLL_NAME%.dll" >nul
if %ERRORLEVEL% NEQ 0 (
    echo DEPLOY FAILED - is the game running? Close it and retry.
    pause
    exit /b 1
)

echo Deployed to !PLUGIN_DIR!
pause
