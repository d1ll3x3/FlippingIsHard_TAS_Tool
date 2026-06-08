@echo off
setlocal enabledelayedexpansion

set DLL_NAME=FlippingIsHardTAS
set DLL_PATH=bin\Debug\net6.0\%DLL_NAME%.dll
set PLUGIN_FOLDER=FlippingIsHardTAS

echo Searching for game installation...

set GAME_PATH=
for %%D in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do (
    if exist "%%D:\SteamLibrary\steamapps\common\Flipping is Hard Demo" (
        set GAME_PATH=%%D:\SteamLibrary\steamapps\common\Flipping is Hard Demo
        goto found
    )
)

echo Game not found in any SteamLibrary.
pause
exit /b 1

:found
echo Found: !GAME_PATH!

set GAME_PLUGINS=!GAME_PATH!\BepInEx\plugins\%PLUGIN_FOLDER%

echo Building %DLL_NAME%...
dotnet build -c Debug

if %ERRORLEVEL% NEQ 0 (
    echo BUILD FAILED
    pause
    exit /b 1
)

echo.
echo Deploying to game...
if not exist "!GAME_PLUGINS!" mkdir "!GAME_PLUGINS!"
copy /Y "%DLL_PATH%" "!GAME_PLUGINS!\%DLL_NAME%.dll"

echo.
echo Done! Plugin deployed to:
echo !GAME_PLUGINS!
pause
