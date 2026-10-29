@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0register_vcam.ps1" -Action install
exit /b %errorlevel%
