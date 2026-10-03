<#
.SYNOPSIS
    Pretends to be the game's Head Tracking plugin, to test HeadTracking.exe without starting SPT.

.DESCRIPTION
    Listens where the plugin listens (127.0.0.1:4243) and prints what HeadTracking.exe sends:
    poses per second, the app's tracking state, and the head offset and zoom the game would apply.
    Sends status back like the plugin does (10 a second, "in raid"), so the app shows
    "In raid, applying". -Recenter sends one recenter command after 3 seconds, as the in-game key would.

    Uses the link protocol code inside HeadTracking.exe itself, so it decodes exactly what the plugin
    would. The game must not be running (it holds port 4243).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\fake-game.ps1 -Seconds 30
#>
param(
    [int] $Seconds = 20,
    [int] $ModPort = 4243,
    [int] $AppPort = 4244,
    [string] $Exe,
    [switch] $Recenter
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Exe) {
    foreach ($candidate in @((Join-Path $root 'src\HeadTracking.App\bin\Release\HeadTracking.exe'), 'H:\SPT4.1.X\HeadTracking.exe')) {
        if (Test-Path $candidate) { $Exe = $candidate; break }
    }
}
if (-not $Exe -or -not (Test-Path $Exe)) { throw 'HeadTracking.exe not found; pass -Exe.' }
[void][System.Reflection.Assembly]::LoadFrom($Exe)

$listener = New-Object System.Net.Sockets.UdpClient (New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Loopback), $ModPort)
$listener.Client.ReceiveTimeout = 100
$sender = New-Object System.Net.Sockets.UdpClient
$app = New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Loopback), $AppPort
Write-Host "Fake game listening on 127.0.0.1:$ModPort, status to $AppPort. Ctrl+C stops." -ForegroundColor Green

$clock = [System.Diagnostics.Stopwatch]::StartNew()
$poses = 0; $settings = 0; $lastPose = $null; $lastSettings = $null
$nextPrint = 0.5; $nextStatus = 0.0; $sequence = 0; $recentered = $false
$from = New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Any), 0

function Send-Bytes([byte[]] $bytes) { [void]$sender.Send($bytes, $bytes.Length, $app) }
Send-Bytes ([HeadTracking.Shared.LinkProtocol]::Encode([HeadTracking.Shared.LinkCommand]::Hello))

try {
    while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
        try {
            $bytes = $listener.Receive([ref]$from)
            $type = [HeadTracking.Shared.LinkMessageType]::Pose
            if ([HeadTracking.Shared.LinkProtocol]::TryReadHeader($bytes, $bytes.Length, [ref]$type)) {
                if ($type -eq [HeadTracking.Shared.LinkMessageType]::Pose) {
                    $pose = New-Object HeadTracking.Shared.PoseMessage
                    if ([HeadTracking.Shared.LinkProtocol]::TryDecode($bytes, $bytes.Length, [ref]$pose)) { $poses++; $lastPose = $pose }
                }
                elseif ($type -eq [HeadTracking.Shared.LinkMessageType]::GameSettings) {
                    $gs = $null
                    if ([HeadTracking.Shared.LinkProtocol]::TryDecode($bytes, $bytes.Length, [ref]$gs)) {
                        $settings++
                        if ($null -eq $lastSettings -or $lastSettings.Revision -ne $gs.Revision) {
                            Write-Host ("  settings rev {0}: pause aiming {1}, zoom {2} (max {3} deg), recenter key {4}, toggle key {5}" -f `
                                    $gs.Revision, $gs.PauseWhileAiming, $gs.ZoomEnabled, $gs.ZoomMaxFovReduction, $gs.RecenterKey, $gs.ToggleKey) -ForegroundColor DarkGray
                        }
                        $lastSettings = $gs
                    }
                }
            }
        }
        catch [System.Net.Sockets.SocketException] { }

        $t = $clock.Elapsed.TotalSeconds
        if ($t -ge $nextStatus) {
            $nextStatus = $t + 0.1
            $sequence++
            $status = New-Object HeadTracking.Shared.StatusMessage
            $status.Sequence = $sequence; $status.InRaid = $true; $status.HasSettings = ($null -ne $lastSettings); $status.ModVersion = 'fake-game'
            if ($null -ne $lastPose) { $status.AppliedYaw = $lastPose.Yaw; $status.AppliedPitch = $lastPose.Pitch; $status.Applying = $lastPose.Enabled }
            Send-Bytes ([HeadTracking.Shared.LinkProtocol]::Encode($status))
        }

        if ($Recenter -and -not $recentered -and $t -ge 3) {
            $recentered = $true
            Send-Bytes ([HeadTracking.Shared.LinkProtocol]::Encode([HeadTracking.Shared.LinkCommand]::Recenter))
            Write-Host '  sent Recenter' -ForegroundColor Yellow
        }

        if ($t -ge $nextPrint) {
            $nextPrint = $t + 0.5
            if ($null -eq $lastPose) { Write-Host ("{0,6:0.0}s  no poses yet" -f $t) }
            else {
                Write-Host ("{0,6:0.0}s  {1,4} poses  state {2,-9} enabled {3,-5} yaw {4,6:+0.0;-0.0;0.0} pitch {5,6:+0.0;-0.0;0.0} zoom {6:0.00}" -f `
                        $t, $poses, $lastPose.State, $lastPose.Enabled, $lastPose.Yaw, $lastPose.Pitch, $lastPose.Zoom)
            }
        }
    }
}
finally {
    $listener.Close(); $sender.Close()
    Write-Host ("Received {0} poses and {1} settings messages in {2:0.0} s." -f $poses, $settings, $clock.Elapsed.TotalSeconds) -ForegroundColor Green
}
