@echo off
rem Builds MeetingCamera.exe with the C# compiler that comes with Windows (nothing to install).
rem Run it again after changing obs-remote.html, Start-Meeting.ps1 or the lib folder to build the new versions in.
setlocal enabledelayedexpansion
pushd "%~dp0"
set RES=
for %%f in (webview2\*) do set RES=!RES! /resource:webview2\%%~nxf,webview2/%%~nxf
for %%f in (lib\*) do set RES=!RES! /resource:lib\%%~nxf,lib/%%~nxf
for %%f in (lib\model\*) do set RES=!RES! /resource:lib\model\%%~nxf,lib/model/%%~nxf
set NET=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
"%NET%\csc.exe" /nologo /optimize /target:winexe /win32icon:MeetingCamera.ico /platform:x64 /out:MeetingCamera.exe ^
  /r:"%NET%\WPF\UIAutomationClient.dll" /r:"%NET%\WPF\UIAutomationTypes.dll" /r:"%NET%\WPF\WindowsBase.dll" ^
  /r:webview2\Microsoft.Web.WebView2.Core.dll /r:webview2\Microsoft.Web.WebView2.WinForms.dll ^
  /resource:obs-remote.html,obs-remote.html /resource:timer.html,timer.html /resource:Start-Meeting.ps1,Start-Meeting.ps1 ^
  /resource:MeetingCamera.ico,MeetingCamera.ico !RES! MeetingCamera.cs
if errorlevel 1 (echo Build failed. & pause) else (echo Built MeetingCamera.exe)
