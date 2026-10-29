@echo off
setlocal
if not exist "%~dp0..\LensioStudio.exe" (
    call "%~dp0LensioStudio\build.bat"
    if errorlevel 1 exit /b 1
)
start "" "%~dp0..\LensioStudio.exe"
