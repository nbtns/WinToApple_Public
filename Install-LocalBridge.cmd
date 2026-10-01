@echo off
setlocal
title LocalBridge Installer

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0windows\Installer\Install.ps1"
if errorlevel 1 (
    echo.
    echo LocalBridge could not be installed. Please keep this window open and share the error shown above.
    pause
    exit /b 1
)

echo.
echo LocalBridge was installed successfully.
echo You can close this window after the LocalBridge settings screen opens.
pause
