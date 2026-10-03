<#
.SYNOPSIS
    Builds Head Tracking (app + plugin), runs the tests, smoke-tests the packaged layout, zips it,
    and optionally installs it.

.DESCRIPTION
    Run this through PowerShell, not Bash: Bash mangles 'H:\SPT4.1.X' into 'H:SPT4.1.X'.

    The zip is unpacked over the SPT root:
        HeadTracking.exe, HeadTracking.exe.config     the app (two files in the SPT root)
        HeadTrackingApp\                               its libraries, ONNX Runtime, the face models
        BepInEx\plugins\HeadTracking.Plugin.dll        the game half
    Entries are written by hand with forward slashes: PowerShell 5.1's Compress-Archive writes
    backslashes, which some extractors turn into file names instead of folders.

.PARAMETER SPTPath
    The SPT install root to compile the plugin against (and install into, with -Install).

.PARAMETER ModelsPath
    Where OpenTrack's neuralnet models are (OpenTrack 2026.1.0 ships them in modules\models).

.PARAMETER Install
    Also install into SPTPath. Refuses while EFT or HeadTracking.exe run. Moves a 0.1.0 plugin
    (BepInEx\plugins\HeadTracking.dll, same plugin GUID) to H:\SPTMods\plugin-backups first.
    Never touches HeadTrackingApp\settings.json or logs.

.EXAMPLE
    scripts\pack.ps1 -SPTPath H:\SPT4.1.X
    scripts\pack.ps1 -SPTPath H:\SPT4.1.X -Install
#>
param(
    [Parameter(Mandatory = $true)][string] $SPTPath,
    [string] $ModelsPath = 'H:\opentrack\modules\models',
    [switch] $Install
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
$pluginProject = Join-Path $root 'src\HeadTracking.Plugin\HeadTracking.Plugin.csproj'
$appProject = Join-Path $root 'src\HeadTracking.App\HeadTracking.App.csproj'
$tests = Join-Path $root 'tests\HeadTracking.Tests\HeadTracking.Tests.csproj'
$models = @('head-localizer.onnx', 'head-pose-0.4-small-f32.onnx', 'head-pose-0.4-small-int8.onnx', 'head-pose-0.4-big-int8.onnx')

if (-not (Test-Path (Join-Path $SPTPath 'BepInEx\core\BepInEx.dll'))) {
    throw "SPTPath '$SPTPath' is not an SPT install root: no BepInEx\core\BepInEx.dll."
}
foreach ($m in $models) {
    if (-not (Test-Path (Join-Path $ModelsPath $m))) { throw "Model $m not found in $ModelsPath. Install OpenTrack 2026.1.0 or pass -ModelsPath." }
}

# ---- version agreement -----------------------------------------------------------
# Four places, one product version: both csproj files, the plugin's constant, the app's constant.
function CsprojVersion($p) { ([xml](Get-Content $p)).Project.PropertyGroup.Version | Where-Object { $_ } }
function SourceVersion($p, $pattern) { (Select-String -Path $p -Pattern $pattern).Matches[0].Groups[1].Value }
$versions = [ordered]@{
    'plugin csproj'    = CsprojVersion $pluginProject
    'PluginVersion'    = SourceVersion (Join-Path $root 'src\HeadTracking.Plugin\HeadTrackingPlugin.cs') 'PluginVersion\s*=\s*"([^"]+)"'
    'app csproj'       = CsprojVersion $appProject
    'AppInfo.Version'  = SourceVersion (Join-Path $root 'src\HeadTracking.App\Engine\AppPaths.cs') 'Version\s*=\s*"([^"]+)"'
}
if (($versions.Values | Select-Object -Unique).Count -ne 1) {
    throw "Version mismatch: $(($versions.GetEnumerator() | ForEach-Object { "$($_.Key) $($_.Value)" }) -join ', ')"
}
$version = $versions['plugin csproj']
Write-Host "Version $version" -ForegroundColor Cyan

# ---- build and test --------------------------------------------------------------
dotnet build $pluginProject -c Release "-p:SPTPath=$SPTPath" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
dotnet build $appProject -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'App build failed.' }
dotnet test $tests --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

$pluginDll = Join-Path $root 'src\HeadTracking.Plugin\bin\Release\HeadTracking.Plugin.dll'
$appBin = Join-Path $root 'src\HeadTracking.App\bin\Release'

# ---- stage -------------------------------------------------------------------------
$dist = Join-Path $root 'dist'
$staging = Join-Path $dist 'staging'
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
$dataDir = Join-Path $staging 'HeadTrackingApp'
New-Item -ItemType Directory -Force -Path (Join-Path $dataDir 'models'), (Join-Path $staging 'BepInEx\plugins') | Out-Null

Copy-Item (Join-Path $appBin 'HeadTracking.exe'), (Join-Path $appBin 'HeadTracking.exe.config') $staging
Get-ChildItem $appBin -Filter *.dll | Copy-Item -Destination $dataDir
foreach ($m in $models) { Copy-Item (Join-Path $ModelsPath $m) (Join-Path $dataDir 'models') }
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $dataDir
Copy-Item $pluginDll (Join-Path $staging 'BepInEx\plugins')
Copy-Item (Join-Path $root 'README.md') (Join-Path $staging 'HeadTracking-README.md')

$required = @('HeadTracking.exe', 'HeadTracking.exe.config', 'HeadTrackingApp\onnxruntime.dll', 'HeadTrackingApp\Microsoft.ML.OnnxRuntime.dll',
    'HeadTrackingApp\FlashCap.dll', 'HeadTrackingApp\FlashCap.Core.dll', 'HeadTrackingApp\System.Memory.dll',
    'HeadTrackingApp\models\head-localizer.onnx', 'BepInEx\plugins\HeadTracking.Plugin.dll')
foreach ($r in $required) { if (-not (Test-Path (Join-Path $staging $r))) { throw "Staged layout is missing $r" } }
if (-not (Select-String -Path (Join-Path $staging 'HeadTracking.exe.config') -Pattern 'privatePath="HeadTrackingApp"' -Quiet)) {
    throw 'HeadTracking.exe.config lost its probing path; the exe would not find its libraries.'
}

# ---- smoke test the staged layout ----------------------------------------------------
# Runs the real exe from the real layout (libraries via the probing path, ONNX Runtime from
# HeadTrackingApp\) on a still face image. Proves the package loads, not just that it builds.
$face = Join-Path $SPTPath 'SPT_Runtime\SPT_Data\images\trader\avatar\59b91cab86f77469aa5343ca.png'
if (Test-Path $face) {
    $out = Join-Path $dist 'smoke-test.txt'
    $p = Start-Process (Join-Path $staging 'HeadTracking.exe') -ArgumentList '--test-image', "`"$face`"", '--out', "`"$out`"" -PassThru -Wait
    if ($p.ExitCode -ne 0) { throw "Smoke test failed (exit $($p.ExitCode)): $(Get-Content $out -ErrorAction SilentlyContinue | Out-String)" }
    Write-Host "Smoke test: $((Get-Content $out | Select-String 'original').Line)" -ForegroundColor Green
    Remove-Item (Join-Path $dataDir 'logs') -Recurse -Force -ErrorAction SilentlyContinue
}
else {
    Write-Warning "No test face image at $face; skipping the smoke test."
}

# ---- zip -----------------------------------------------------------------------------
$zip = Join-Path $dist "HeadTracking-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem $staging -Recurse -File | ForEach-Object {
        $entry = $_.FullName.Substring($staging.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally {
    $archive.Dispose()
}
Write-Host ("Packed {0} ({1:0.0} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Green

# ---- install ---------------------------------------------------------------------------
if ($Install) {
    if (Get-Process EscapeFromTarkov -ErrorAction SilentlyContinue) { throw 'EFT is running and holds the plugin open. Close the game first.' }
    if (Get-Process HeadTracking -ErrorAction SilentlyContinue) { throw 'HeadTracking.exe is running. Close it first.' }

    $old = Join-Path $SPTPath 'BepInEx\plugins\HeadTracking.dll'
    if (Test-Path $old) {
        $backup = Join-Path 'H:\SPTMods\plugin-backups' ((Get-Date -Format 'yyyy-MM-dd') + '-headtracking-0.1.0')
        New-Item -ItemType Directory -Force -Path $backup | Out-Null
        Move-Item $old $backup -Force
        Write-Host "Moved the 0.1.0 plugin (same GUID) to $backup" -ForegroundColor Yellow
    }

    Copy-Item (Join-Path $staging 'HeadTracking.exe'), (Join-Path $staging 'HeadTracking.exe.config') $SPTPath -Force
    $target = Join-Path $SPTPath 'HeadTrackingApp'
    New-Item -ItemType Directory -Force -Path (Join-Path $target 'models') | Out-Null
    Get-ChildItem $dataDir -File | Copy-Item -Destination $target -Force
    Get-ChildItem (Join-Path $dataDir 'models') -File | Copy-Item -Destination (Join-Path $target 'models') -Force
    Copy-Item (Join-Path $staging 'BepInEx\plugins\HeadTracking.Plugin.dll') (Join-Path $SPTPath 'BepInEx\plugins') -Force
    Copy-Item (Join-Path $staging 'HeadTracking-README.md') $SPTPath -Force
    Write-Host "Installed to $SPTPath (HeadTracking.exe, HeadTrackingApp\, BepInEx\plugins\HeadTracking.Plugin.dll)" -ForegroundColor Green
}
