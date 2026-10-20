<#
  Serve-Remote.ps1 - lets a tablet or phone on the hall Wi-Fi open the Meeting Camera page.
  Serves obs-remote.html at http://<this PC>:8080/  (Start-Meeting.ps1 starts it in the background).
  The first time, Windows asks whether to allow it on the network: tick "Private networks".
  Options: -Port 8080   -LocalOnly (only this PC can connect; for testing)
#>
param([int]$Port = 8080, [switch]$LocalOnly)

$page = Join-Path $PSScriptRoot 'obs-remote.html'
$address = if ($LocalOnly) { [Net.IPAddress]::Loopback } else { [Net.IPAddress]::Any }
$listener = New-Object Net.Sockets.TcpListener($address, $Port)
$listener.Start()
Write-Host "Meeting Camera tablet remote on port $Port - close this window to stop it."

# Addresses a tablet can reach: adapters that are up and have a router (skips WSL/VPN-only adapters).
function Get-LanIPs {
  Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' } |
    ForEach-Object { $_.IPv4Address.IPAddress }
}

function Send($stream, [int]$code, [string]$type, [byte[]]$body) {
  $reason = @{ 200 = 'OK'; 404 = 'Not Found' }[$code]
  $head = "HTTP/1.1 $code $reason`r`nContent-Type: $type`r`nContent-Length: $($body.Length)`r`n" +
          "Cache-Control: no-store`r`nAccess-Control-Allow-Origin: *`r`nConnection: close`r`n`r`n"
  $bytes = [Text.Encoding]::ASCII.GetBytes($head)
  $stream.Write($bytes, 0, $bytes.Length)
  $stream.Write($body, 0, $body.Length)
}

while ($true) {
  $client = $listener.AcceptTcpClient()
  try {
    $stream = $client.GetStream()
    $stream.ReadTimeout = 3000
    $reader = New-Object IO.StreamReader($stream, [Text.Encoding]::ASCII, $false, 4096, $true)
    $request = $reader.ReadLine()
    while ($reader.ReadLine()) {}   # skip the request headers
    $path = (($request -split ' ')[1] -split '\?')[0]
    if ($path -eq '/info') {
      $json = @{ ips = @(Get-LanIPs); port = $Port } | ConvertTo-Json -Compress
      Send $stream 200 'application/json' ([Text.Encoding]::UTF8.GetBytes($json))
    }
    elseif ($path -eq '/' -or $path -eq '/index.html') {
      Send $stream 200 'text/html; charset=utf-8' ([IO.File]::ReadAllBytes($page))
    }
    elseif ($path -match '^/lib(/[\w.-]+)+$' -and $path -notmatch '\.\.' -and
            (Test-Path ($file = Join-Path $PSScriptRoot $path.Substring(1)) -PathType Leaf)) {
      # People detector and QR code library, so nothing comes from the internet.
      $type = @{ '.js' = 'text/javascript'; '.json' = 'application/json'; '.wasm' = 'application/wasm' }[[IO.Path]::GetExtension($path)]
      Send $stream 200 $(if ($type) { $type } else { 'application/octet-stream' }) ([IO.File]::ReadAllBytes($file))
    }
    else {
      Send $stream 404 'text/plain' ([Text.Encoding]::ASCII.GetBytes('Not found'))
    }
  }
  catch {}
  finally { $client.Close() }
}
