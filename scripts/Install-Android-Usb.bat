@echo off
setlocal
title Install LensioCam
set "ADB_PATH=%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe"
if not exist "%ADB_PATH%" set "ADB_PATH=adb"
set "APK_PATH=%~dp0..\..\LensioCam.apk"
if not exist "%APK_PATH%" set "APK_PATH=%~dp0..\LensioCam.apk"
if not exist "%APK_PATH%" set "APK_PATH=%~dp0..\LensioCam\android\app\build\outputs\apk\debug\app-debug.apk"
if not exist "%APK_PATH%" (
    echo [ERROR] Build LensioCam first with LensioCam\build_android.bat.
    exit /b 1
)
set "PHYSICAL_DEVICE="
for /f "tokens=1,2,*" %%A in ('"%ADB_PATH%" devices -l') do (
    if "%%B"=="device" (
        echo %%C | findstr /C:"usb:" >nul
        if not errorlevel 1 set "PHYSICAL_DEVICE=%%A"
    )
)
if not defined PHYSICAL_DEVICE (
    echo [ERROR] Connect and authorize a physical USB phone first.
    exit /b 1
)
"%ADB_PATH%" -s "%PHYSICAL_DEVICE%" install -r "%APK_PATH%"
if errorlevel 1 exit /b 1
"%ADB_PATH%" -s "%PHYSICAL_DEVICE%" reverse tcp:8556 tcp:8556
if errorlevel 1 exit /b 1
"%ADB_PATH%" -s "%PHYSICAL_DEVICE%" forward tcp:8080 tcp:8080
if errorlevel 1 exit /b 1
"%ADB_PATH%" -s "%PHYSICAL_DEVICE%" shell am start -n com.LensioCam/.MainActivity
if errorlevel 1 exit /b 1
echo [SUCCESS] LensioCam installed and started.
