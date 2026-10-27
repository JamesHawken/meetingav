<#
  Start-Meeting.ps1 - gets the meeting PC ready in one go:
    1. Opens OBS with the virtual camera on (if it isn't already running), with its
       remote control (WebSocket server) switched on so the Meeting AV page can connect
    2. Opens JW Library, and joins the Zoom meeting whose link is in Meeting AV's Settings > This PC
       (MeetingAV.exe then arranges the windows once they're open)
    3. Opens the Meeting AV control page (obs-remote.html)
    4. Starts the tablet remote (Serve-Remote.ps1) in the background

  Easiest: double-click Start-Meeting.bat
  Options:  -Meeting Midweek|Weekend   force a meeting instead of going by the day
            -NoObs                     don't start OBS
            -NoApps                    don't open JW Library or Zoom
            -AppMode                   skip steps 3-4 (MeetingAV.exe opens the page and serves tablets itself)
#>
param(
  [ValidateSet('Auto', 'Midweek', 'Weekend')] [string]$Meeting = 'Auto',
  [switch]$NoObs,
  [switch]$NoApps,
  [switch]$AppMode
)

# ---- Settings ---------------------------------------------------------------
$WeekendDays = @('Saturday', 'Sunday')   # get ready for the weekend meeting; Monday to Friday = midweek
$ObsExe      = 'C:\Program Files\obs-studio\bin\64bit\obs64.exe'
$ControlPage = Join-Path $PSScriptRoot 'obs-remote.html'
$RemoteScript = Join-Path $PSScriptRoot 'Serve-Remote.ps1'
$RemotePort   = 8080
$ZoomLinkFile = Join-Path $env:LOCALAPPDATA 'MeetingAV\zoom-link.txt'   # set in Meeting AV: Settings > This PC
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
    $restarted = $false
    if ((Get-Process obs64 -ErrorAction SilentlyContinue) -and $ws -and -not (Wait-ObsAnswers 15)) {
      # Running but not answering: stuck asking about Safe Mode after a crash, or hung. Started afresh.
      Get-Process obs64 -ErrorAction SilentlyContinue | Stop-Process -Force
      Start-Sleep -Seconds 2
      $restarted = $true
    }
    if (Get-Process obs64 -ErrorAction SilentlyContinue) { Ok 'already running' }
    elseif (Test-Path $ObsExe) {
      Clear-ObsCrashFlag
      # OBS must be started from its own folder or it can't find its files. It starts minimised
      # to the notification area (by the clock); Meeting AV is where it's controlled from.
      Start-Process -FilePath $ObsExe -WorkingDirectory (Split-Path $ObsExe) `
        -ArgumentList '--startvirtualcam', '--disable-shutdown-check', '--minimize-to-tray'
      Ok $(if ($restarted) { 'restarted (it was stuck)' } else { 'started (minimised)' })
    }
    else { Fail "not found at $ObsExe (edit `$ObsExe at the top of this script)" }
  }

  # 2. JW Library and Zoom
  if (-not $NoApps) {
    Step 'JW Library'
    if (Get-Process JWLibrary -ErrorAction SilentlyContinue) { Ok 'already open' }
    else {
      # A Microsoft Store app (its package is WatchtowerBibleandTractSo...): started by its app ID from
      # the Start menu. Meeting AV takes it to the meeting's media.
      $app = Get-StartApps -ErrorAction SilentlyContinue | Where-Object Name -eq 'JW Library' | Select-Object -First 1
      if ($app) { Start-Process "shell:AppsFolder\$($app.AppID)"; Ok 'opened' }
      else { Fail 'not installed (get it from the Microsoft Store)' }
    }

    Step 'Zoom meeting'
    $link = if (Test-Path $ZoomLinkFile) { (Get-Content $ZoomLinkFile -Raw).Trim() }
    if (-not $link) { Ok 'no link set (Meeting AV: Settings > This PC)' }
    elseif (Test-ZoomMeeting) { Ok 'already in a meeting' }
    else {
      try { Start-Process (ConvertTo-ZoomJoin $link); Ok 'joining' }
      catch { Fail "couldn't open the link - is Zoom installed?" }
    }
  }

  # 3. Camera control page
  if (-not $AppMode) {
    Step 'Meeting AV page'
    if (Test-Path $ControlPage) { Start-Process $ControlPage; Ok } else { Fail "not found next to this script" }

    # 4. Tablet remote
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

# An invite link (https://us02web.zoom.us/j/123456789?pwd=...) or a meeting ID, as a link that
# opens straight in the Zoom app instead of a browser page asking to open it.
function ConvertTo-ZoomJoin([string]$link) {
  $id = $link -replace '[\s-]', ''
  if ($id -match '^\d{9,11}$') { return "zoommtg://zoom.us/join?action=join&confno=$id" }
  if ($link -match '^https?://([^/]*zoom\.us)/[jw]/(\d+)(?:\?(?:.*&)?pwd=([^&#]+))?') {
    $join = "zoommtg://$($Matches[1])/join?action=join&confno=$($Matches[2])"
    if ($Matches[3]) { $join += "&pwd=$($Matches[3])" }
    return $join
  }
  return $link   # something else (a personal link name, zoommtg://): opened as it is
}

# Zoom's meeting window is open (it has the same window class whichever screen it's on). Only a
# visible one counts: after a meeting Zoom keeps hidden ones, which would make it look as if it's
# still in one, and the meeting wouldn't be joined.
function Test-ZoomMeeting {
  if (-not (Get-Process Zoom -ErrorAction SilentlyContinue)) { return $false }
  if (-not ('MeetingAV.Win' -as [type])) {
    Add-Type -Namespace MeetingAV -Name Win -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
'@
  }
  # [NullString]::Value: PowerShell would pass $null as "", which only finds a window with no title
  $h = [IntPtr]::Zero
  while (($h = [MeetingAV.Win]::FindWindowEx([IntPtr]::Zero, $h, 'ConfMultiTabContentWndClass', [NullString]::Value)) -ne [IntPtr]::Zero) {
    if ([MeetingAV.Win]::IsWindowVisible($h)) { return $true }
  }
  return $false
}

# OBS leaves a file in .sentinel while it runs and removes it when it closes properly; one left
# behind (a crash, the PC switched off) makes it ask about Safe Mode on starting, and wait for
# someone to answer. Only called while OBS isn't running.
function Clear-ObsCrashFlag {
  Remove-Item (Join-Path $env:APPDATA 'obs-studio\.sentinel\*') -Force -ErrorAction SilentlyContinue
}

# OBS's remote control answers within $seconds (it's switched on; OBS is running).
function Wait-ObsAnswers([int]$seconds) {
  $port = 4455
  try { $port = (Get-Content (Join-Path $env:APPDATA 'obs-studio\plugin_config\obs-websocket\config.json') -Raw | ConvertFrom-Json).server_port } catch {}
  for ($i = 0; $i -lt $seconds; $i++) { if (Test-Port $port) { return $true }; Start-Sleep -Seconds 1 }
  return $false
}

# Dot-sourcing (". .\Start-Meeting.ps1") loads the functions without running anything.
if ($MyInvocation.InvocationName -ne '.') {
  Start-Meeting
  exit [int]($script:problems -gt 0)
}
