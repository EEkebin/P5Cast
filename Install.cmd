@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1"
if errorlevel 1 (
  pause
  exit /b 1
)
start "" "%LOCALAPPDATA%\Programs\P5Cast\P5Cast.exe"
