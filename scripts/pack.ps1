<#
.SYNOPSIS
    Builds Head Tracking (app + plugin), runs the tests, smoke-tests the packaged layout, zips it,
    and optionally installs it.

.DESCRIPTION
    Run this through PowerShell, not Bash: Bash mangles 'H:\SPT4.1.X' into 'H:SPT4.1.X'.

    The zip is unpacked over the SPT root:
        HeadTracking.exe                               the app, the only file in the SPT root
        HeadTrackingApp\                               README, notices, lib\ (libraries, ONNX Runtime), models\
        BepInEx\plugins\HeadTracking\                  the game half
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
# The SPT root gets HeadTracking.exe and nothing else beside it; the rest is in two folders:
#   HeadTrackingApp\  README.md, THIRD-PARTY-NOTICES.md, lib\ (libraries), models\
#   BepInEx\plugins\HeadTracking\HeadTracking.Plugin.dll
$dataDir = Join-Path $staging 'HeadTrackingApp'
$libDir = Join-Path $dataDir 'lib'
$pluginDir = Join-Path $staging 'BepInEx\plugins\HeadTracking'
New-Item -ItemType Directory -Force -Path (Join-Path $dataDir 'models'), $libDir, $pluginDir | Out-Null

Copy-Item (Join-Path $appBin 'HeadTracking.exe') $staging
Get-ChildItem $appBin -Filter *.dll | Copy-Item -Destination $libDir
foreach ($m in $models) { Copy-Item (Join-Path $ModelsPath $m) (Join-Path $dataDir 'models') }
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $dataDir
Copy-Item (Join-Path $root 'README.md') (Join-Path $dataDir 'README.md')
Copy-Item $pluginDll $pluginDir

$required = @('HeadTracking.exe', 'HeadTrackingApp\README.md', 'HeadTrackingApp\lib\onnxruntime.dll', 'HeadTrackingApp\lib\Microsoft.ML.OnnxRuntime.dll',
    'HeadTrackingApp\lib\FlashCap.dll', 'HeadTrackingApp\lib\FlashCap.Core.dll', 'HeadTrackingApp\lib\System.Memory.dll',
    'HeadTrackingApp\models\head-localizer.onnx', 'BepInEx\plugins\HeadTracking\HeadTracking.Plugin.dll')
foreach ($r in $required) { if (-not (Test-Path (Join-Path $staging $r))) { throw "Staged layout is missing $r" } }
$rootFiles = @(Get-ChildItem $staging -File | ForEach-Object Name)
if ($rootFiles.Count -ne 1 -or $rootFiles[0] -ne 'HeadTracking.exe') { throw "Only HeadTracking.exe belongs in the SPT root; staged: $($rootFiles -join ', ')" }
if (Get-ChildItem $dataDir -Filter *.dll -File) { throw 'Libraries belong in HeadTrackingApp\lib, not HeadTrackingApp.' }

# ---- smoke test the staged layout ----------------------------------------------------
# Runs the real exe from the real layout (libraries found in HeadTrackingApp\lib by the exe's own
# resolver, no .exe.config) on a still face image. Proves the package loads, not just that it builds.
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

    Copy-Item (Join-Path $staging 'HeadTracking.exe') $SPTPath -Force
    $target = Join-Path $SPTPath 'HeadTrackingApp'
    foreach ($sub in 'lib', 'models') {
        New-Item -ItemType Directory -Force -Path (Join-Path $target $sub) | Out-Null
        Get-ChildItem (Join-Path $dataDir $sub) -File | Copy-Item -Destination (Join-Path $target $sub) -Force
    }
    Get-ChildItem $dataDir -File | Copy-Item -Destination $target -Force
    $targetPlugins = Join-Path $SPTPath 'BepInEx\plugins\HeadTracking'
    New-Item -ItemType Directory -Force -Path $targetPlugins | Out-Null
    Copy-Item (Join-Path $pluginDir 'HeadTracking.Plugin.dll') $targetPlugins -Force

    # The 0.4.0 and earlier layout: a config and README beside the exe, libraries loose in
    # HeadTrackingApp\, the plugin loose in BepInEx\plugins (a second copy would load twice).
    $legacy = @((Join-Path $SPTPath 'HeadTracking.exe.config'), (Join-Path $SPTPath 'HeadTracking-README.md'),
        (Join-Path $SPTPath 'BepInEx\plugins\HeadTracking.Plugin.dll'))
    $legacy += Get-ChildItem $target -Filter *.dll -File | Where-Object { Test-Path (Join-Path $target "lib\$($_.Name)") } | ForEach-Object FullName
    foreach ($f in $legacy) {
        if (Test-Path $f) { Remove-Item $f -Force; Write-Host "Removed old-layout file $f" -ForegroundColor Yellow }
    }
    Write-Host "Installed to $SPTPath (HeadTracking.exe, HeadTrackingApp\, BepInEx\plugins\HeadTracking\)" -ForegroundColor Green
}
