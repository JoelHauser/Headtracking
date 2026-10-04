<#
.SYNOPSIS
    Pretends to be OpenTrack: sends scripted head poses to HeadTracking.exe over UDP.

.DESCRIPTION
    For testing in game without a webcam, and for checking each behaviour on cue. In HeadTracking.exe
    choose the OpenTrack source (Tracking source page; port 4242), then run this.

    Sends exactly what OpenTrack's "UDP over network" output sends: 48-byte datagrams of six
    little-endian doubles (x, y, z, yaw, pitch, roll), about 250 a second. Like a real webcam
    tracker, the pose only changes about 30 times a second; in between the same pose is resent.
    "Face lost" is simulated the way OpenTrack does it: the last pose resent bit for bit.

    Angles use OpenTrack's convention as best known: yaw positive = head turned right, pitch
    positive = head tilted up. The mod's log prints a "Direction check" line the first time each
    axis moves; compare it with what this script says it is doing. The app's Overview page shows the
    same head movement live.

    Stop OpenTrack's tracking while this runs, or both will send to the same port.

.PARAMETER Delay
    Seconds to wait before sending, so there is time to click back into the game: the mod pauses
    while the game window is not focused. Default 8.

.PARAMETER Noise
    Per-frame tracker jitter to add, in degrees (one sigma). 0.47 is what a C920 measured on a
    still face; the default 0.03 is near noise-free.

.PARAMETER Mode
    scenario  (default) A walk-through of every behaviour, announced phase by phase:
              centre, look left, right, up, down, face lost (hold then return), recovery,
              recenter prompt, OpenTrack closing.
    sweep     Smooth figure-of-eight head motion for -Seconds.
    hold      A still head at -Yaw / -Pitch, with sensor-like jitter, for -Seconds.
    frozen    -Yaw / -Pitch sent once, then resent unchanged (face lost) for -Seconds.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\send-test-poses.ps1
    powershell -ExecutionPolicy Bypass -File scripts\send-test-poses.ps1 -Mode sweep -Seconds 60
    powershell -ExecutionPolicy Bypass -File scripts\send-test-poses.ps1 -Mode hold -Yaw 10 -Seconds 20
#>
param(
    [ValidateSet('scenario', 'sweep', 'hold', 'frozen')][string] $Mode = 'scenario',
    [double] $Seconds = 30,
    [double] $Yaw = 0,
    [double] $Pitch = 0,
    [int] $Port = 4242,
    [string] $HostName = '127.0.0.1',
    [int] $Delay = 8,
    [double] $Noise = 0.03
)

$ErrorActionPreference = 'Stop'

$udp = New-Object System.Net.Sockets.UdpClient
$endpoint = New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Parse($HostName)), $Port
$clock = [System.Diagnostics.Stopwatch]::StartNew()
$random = New-Object System.Random 7

$script:sent = 0
$script:lastPose = $null
$script:nextCameraFrame = 0.0

function Encode([double[]] $pose) {
    $bytes = New-Object byte[] 48
    for ($i = 0; $i -lt 6; $i++) {
        [System.BitConverter]::GetBytes([double]$pose[$i]).CopyTo($bytes, $i * 8)
    }
    return , $bytes
}

function Send-Bytes([byte[]] $bytes) {
    [void]$udp.Send($bytes, $bytes.Length, $endpoint)
    $script:sent++
}

# Runs one phase. $poseAt maps seconds-into-the-phase to @(yaw, pitch), or $null to resend the
# previous pose unchanged (face lost). -Silent sends nothing at all (OpenTrack closed).
function Run-Phase([string] $label, [double] $duration, [scriptblock] $poseAt, [switch] $Silent) {
    $t0 = $clock.Elapsed.TotalSeconds
    Write-Host ("{0,7:0.0}s  {1}" -f $t0, $label) -ForegroundColor Cyan
    while ($true) {
        $t = $clock.Elapsed.TotalSeconds - $t0
        if ($t -ge $duration) { break }

        if (-not $Silent) {
            $now = $clock.Elapsed.TotalSeconds
            if ($now -ge $script:nextCameraFrame -or $null -eq $script:lastPose) {
                # A new camera frame: about 30 fps, like a webcam.
                $script:nextCameraFrame = $now + (1.0 / 30.0)
                $angles = & $poseAt $t
                if ($null -ne $angles) {
                    # Sensor-like noise, a few hundredths of a degree, so the pose is never
                    # bit-identical while "tracking" (the mod reads identical as "face lost").
                    # Approximately Gaussian: the sum of three uniforms, scaled to sigma = $Noise.
                    $jy = (($random.NextDouble() + $random.NextDouble() + $random.NextDouble()) - 1.5) * 2 * $Noise
                    $jp = (($random.NextDouble() + $random.NextDouble() + $random.NextDouble()) - 1.5) * 2 * $Noise
                    $script:lastPose = Encode @(0.0, 0.0, 50.0, ($angles[0] + $jy), ($angles[1] + $jp), 0.0)
                }
                elseif ($null -eq $script:lastPose) {
                    $script:lastPose = Encode @(0.0, 0.0, 50.0, 0.0, 0.0, 0.0)
                }
            }
            Send-Bytes $script:lastPose
        }

        Start-Sleep -Milliseconds 4
    }
}

function Ease([double] $t, [double] $duration) {
    $x = [Math]::Min(1.0, [Math]::Max(0.0, $t / $duration))
    return $x * $x * (3 - 2 * $x)
}

Write-Host "Sending OpenTrack-format poses to ${HostName}:$Port. Ctrl+C stops." -ForegroundColor Green
for ($i = $Delay; $i -gt 0; $i--) {
    Write-Host "  starting in $i s - click into the game window now" -ForegroundColor Yellow
    Start-Sleep -Seconds 1
}
$clock.Restart()

try {
    switch ($Mode) {
        'sweep' {
            Run-Phase "Sweep: yaw +-15, pitch +-8, for $Seconds s" $Seconds {
                param($t) @((15.0 * [Math]::Sin($t * 1.1)), (8.0 * [Math]::Sin($t * 2.2)))
            }
        }
        'hold' {
            Run-Phase "Hold: yaw $Yaw pitch $Pitch, for $Seconds s" $Seconds { param($t) @($Yaw, $Pitch) }
        }
        'frozen' {
            Run-Phase "One pose: yaw $Yaw pitch $Pitch" 0.1 { param($t) @($Yaw, $Pitch) }
            Run-Phase "Frozen (face lost): same pose resent for $Seconds s" $Seconds { param($t) $null }
        }
        'scenario' {
            Run-Phase "Centre: head straight. Expect: view unchanged." 4 { param($t) @(0.0, 0.0) }
            Run-Phase "Turning head RIGHT to 12 degrees. Expect: view turns RIGHT (~26 in game)." 3 { param($t) @((12.0 * (Ease $t 1.0)), 0.0) }
            Run-Phase "Back to centre." 2 { param($t) @((12.0 * (1 - (Ease $t 1.0))), 0.0) }
            Run-Phase "Turning head LEFT to 12 degrees. Expect: view turns LEFT." 3 { param($t) @((-12.0 * (Ease $t 1.0)), 0.0) }
            Run-Phase "Back to centre." 2 { param($t) @((-12.0 * (1 - (Ease $t 1.0))), 0.0) }
            Run-Phase "Tilting head UP 8 degrees. Expect: view looks UP." 3 { param($t) @(0.0, (8.0 * (Ease $t 1.0))) }
            Run-Phase "Back to centre." 2 { param($t) @(0.0, (8.0 * (1 - (Ease $t 1.0)))) }
            Run-Phase "Tilting head DOWN 6 degrees. Expect: view looks DOWN (game allows 20 down)." 3 { param($t) @(0.0, (-6.0 * (Ease $t 1.0))) }
            Run-Phase "Back to centre." 2 { param($t) @(0.0, (-6.0 * (1 - (Ease $t 1.0)))) }
            Run-Phase "Turning RIGHT 12 and holding it there." 3 { param($t) @((12.0 * (Ease $t 1.0)), 0.0) }
            Run-Phase "FACE LOST (pose frozen). Expect: view holds about 1 s, then eases back to centre." 5 { param($t) $null }
            Run-Phase "FACE FOUND again, head now LEFT 8. Expect: view glides there, no jump." 4 { param($t) @(-8.0, 0.0) }
            Run-Phase "Head held at RIGHT 6. PRESS THE RECENTER KEY (F7) NOW. Expect: view glides to straight ahead." 8 { param($t) @(6.0, 0.0) }
            Run-Phase "Slow sweep (relative to the new centre)." 8 { param($t) @((6.0 + 10.0 * [Math]::Sin($t * 1.2)), (5.0 * [Math]::Sin($t * 2.4))) }
            Run-Phase "Holding RIGHT 14." 2 { param($t) @(14.0, 0.0) }
            Run-Phase "OPENTRACK CLOSED (nothing sent). Expect: view holds briefly, then eases to centre." 4 { param($t) $null } -Silent
            Write-Host "Done. Press the recenter key again to reset the centre, or restart the game." -ForegroundColor Green
        }
    }
}
finally {
    $udp.Close()
    Write-Host ("Sent {0} packets in {1:0.0} s." -f $script:sent, $clock.Elapsed.TotalSeconds) -ForegroundColor Green
}
