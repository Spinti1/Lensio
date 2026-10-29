@echo off
setlocal
title Build LensioCam
pushd "%~dp0android"
call gradlew.bat assembleDebug
if errorlevel 1 (
    popd
    exit /b 1
)
copy /Y "app\build\outputs\apk\debug\app-debug.apk" "%~dp0..\..\LensioCam.apk" >nul
if errorlevel 1 (
    popd
    exit /b 1
)
popd
echo [SUCCESS] Built "%~dp0..\..\LensioCam.apk"
exit /b 0
