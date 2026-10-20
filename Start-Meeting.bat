@echo off
rem Double-click to get OBS, JW Library and the Meeting Camera page ready.
rem Add Midweek or Weekend after the name to override today's meeting, e.g. Start-Meeting.bat Weekend
if "%~1"=="" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-Meeting.ps1"
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-Meeting.ps1" -Meeting %1
)
if errorlevel 1 (pause) else (timeout /t 5)
