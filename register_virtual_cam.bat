@echo off
setlocal
title Register LensioCam
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0LensioCam\driver\register_vcam.ps1" -Action install
if errorlevel 1 (
    echo [ERROR] Virtual camera registration failed.
    exit /b 1
)
echo [SUCCESS] Select LensioCam in Discord or your browser.
exit /b 0
