<#
  Start-Meeting.ps1 - gets the meeting PC ready in one go:
    1. Opens OBS with the virtual camera on (if it isn't already running), with its
       remote control (WebSocket server) switched on so the Meeting AV page can connect
    2. Opens the Meeting AV control page (obs-remote.html)
    3. Starts the tablet remote (Serve-Remote.ps1) in the background
  JW Library and Zoom are opened by hand.

  Easiest: double-click Start-Meeting.bat
  Options:  -Meeting Midweek|Weekend   force a meeting instead of going by the day
            -NoObs                     don't start OBS
            -AppMode                   skip steps 2-3 (MeetingAV.exe opens the page and serves tablets itself)
#>
param(
  [ValidateSet('Auto', 'Midweek', 'Weekend')] [string]$Meeting = 'Auto',
  [switch]$NoObs,
  [switch]$AppMode
)

# ---- Settings ---------------------------------------------------------------
$WeekendDays = @('Saturday', 'Sunday')   # days with the weekend meeting; any other day = midweek
$ObsExe      = 'C:\Program Files\obs-studio\bin\64bit\obs64.exe'
$ControlPage = Join-Path $PSScriptRoot 'obs-remote.html'
$RemoteScript = Join-Path $PSScriptRoot 'Serve-Remote.ps1'
$RemotePort   = 8080
# -----------------------------------------------------------------------------

function Step($text) { Write-Host ("  {0,-34}" -f $text) -NoNewline }
function Ok($text = 'done') { Write-Host "[ok] $text" -ForegroundColor Green }
function Fail($text) { Write-Host "[!!] $text" -ForegroundColor Yellow; $script:problems++ }

function Test-Port([int]$port) {
  try { $c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', $port); $c.Close(); return $true }
  catch { return $false }
}

function Start-Meeting {
  $script:problems = 0
  if ($Meeting -eq 'Auto') {
    $Meeting = if ($WeekendDays -contains (Get-Date).DayOfWeek.ToString()) { 'Weekend' } else { 'Midweek' }
  }
  Write-Host "`n  Getting ready for the $Meeting meeting`n" -ForegroundColor Cyan

  # 1. OBS
  Step 'OBS remote control'
  $ws = Enable-ObsWebSocket
  if ($ws) { Ok $ws } else { Fail 'off - close OBS and run this again, or in OBS: Tools > WebSocket Server Settings > Enable' }
  if (-not $NoObs) {
    Step 'OBS + virtual camera'
    if (Get-Process obs64 -ErrorAction SilentlyContinue) { Ok 'already running' }
    elseif (Test-Path $ObsExe) {
      # OBS must be started from its own folder or it can't find its files. It starts minimised
      # to the notification area (by the clock); Meeting AV is where it's controlled from.
      Start-Process -FilePath $ObsExe -WorkingDirectory (Split-Path $ObsExe) `
        -ArgumentList '--startvirtualcam', '--disable-shutdown-check', '--minimize-to-tray'
      Ok 'started (minimised)'
    }
    else { Fail "not found at $ObsExe (edit `$ObsExe at the top of this script)" }
  }

  # 2. Camera control page
  if (-not $AppMode) {
    Step 'Meeting AV page'
    if (Test-Path $ControlPage) { Start-Process $ControlPage; Ok } else { Fail "not found next to this script" }

    # 3. Tablet remote
    Step 'Tablet remote'
    if (Test-Port $RemotePort) { Ok 'already running' }
    elseif (Test-Path $RemoteScript) {
      Start-Process powershell.exe -WindowStyle Hidden -ArgumentList `
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$RemoteScript`"", '-Port', $RemotePort
      $ip = Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' } |
            Select-Object -First 1 | ForEach-Object { $_.IPv4Address.IPAddress }
      Ok $(if ($ip) { "http://${ip}:$RemotePort/" } else { 'started' })
    }
    else { Fail 'Serve-Remote.ps1 not found next to this script' }
  }

  if ($script:problems) { Write-Host "`n  Finished with $($script:problems) problem(s) - see [!!] above.`n" -ForegroundColor Yellow }
  else { Write-Host "`n  All set.`n" -ForegroundColor Green }
}

# The Meeting AV page talks to OBS through its WebSocket server. OBS only reads the setting
# when it starts, so it can be switched on here only while OBS is closed. Returns what was done,
# or $null if it's off and OBS is running.
function Enable-ObsWebSocket {
  $file = Join-Path $env:APPDATA 'obs-studio\plugin_config\obs-websocket\config.json'
  $cfg = $null
  if (Test-Path $file) { try { $cfg = Get-Content $file -Raw | ConvertFrom-Json } catch {} }
  if ($cfg -and $cfg.server_enabled) { return 'on' }
  if (Get-Process obs64 -ErrorAction SilentlyContinue) { return $null }
  $password = -join ((48..57) + (65..90) + (97..122) | Get-Random -Count 16 | ForEach-Object { [char]$_ })
  if (-not $cfg) {
    $cfg = [pscustomobject]@{ alerts_enabled = $false; auth_required = $true; first_load = $false
                              server_enabled = $true; server_password = $password; server_port = 4455 }
  } else {
    $cfg.server_enabled = $true
    if ($cfg.auth_required -and -not $cfg.server_password) { $cfg.server_password = $password }
  }
  New-Item -ItemType Directory -Force -Path (Split-Path $file) | Out-Null
  [IO.File]::WriteAllText($file, ($cfg | ConvertTo-Json))   # UTF-8 without a BOM, like OBS writes it
  return 'switched on'
}

# Dot-sourcing (". .\Start-Meeting.ps1") loads the functions without running anything.
if ($MyInvocation.InvocationName -ne '.') {
  Start-Meeting
  exit [int]($script:problems -gt 0)
}
