@echo off
rem Lets a tablet on the hall Wi-Fi open the Meeting Camera page. Close this window to stop it.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Serve-Remote.ps1"
pause
