@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -Launch
if errorlevel 1 (
  pause
  exit /b 1
)
