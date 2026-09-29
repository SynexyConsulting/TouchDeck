<#
.SYNOPSIS
  Test, publish and package Touch Deck into out\TouchDeck-<version>.msi.

.DESCRIPTION
  1. Refreshes firmware\ from the firmware repo (..\build\watch.uf2 and ..\src\version.h) when present.
  2. Runs the tests (hardware tests skip when no board is plugged in).
  3. Publishes the app self-contained for win-x64 to out\publish.
  4. Builds the per-user MSI (WiX v5) and copies it to out\.
  -Smoke additionally runs the published exe with --smoke and prints what it detected.

.EXAMPLE
  .\build.ps1
  .\build.ps1 -SkipTests -Smoke
  .\build.ps1 -Version 1.0.1        # e.g. to test an upgrade over 1.0.0
#>
param(
    [switch]$SkipTests,
    [switch]$Smoke,
    [string]$Configuration = "Release",
    # Overrides <Version> in Directory.Build.props (app, assembly and MSI).
    [string]$Version
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out = Join-Path $root "out"
$publish = Join-Path $out "publish"
# Built outside an if-expression: that would unwrap a one-item array into a string,
# and splatting a string passes it one character at a time.
$versionArgs = @()
if ($Version) { $versionArgs += "-p:Version=$Version" }

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Check($what) { if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" } }

# 1. Bundled firmware
$fwRepo = Split-Path $root -Parent
$uf2 = Join-Path $fwRepo "build\watch.uf2"
$versionH = Join-Path $fwRepo "src\version.h"
if ((Test-Path $uf2) -and (Test-Path $versionH)) {
    $fwVersion = (Select-String -Path $versionH -Pattern '#define FW_VERSION\s+"([^"]+)"').Matches[0].Groups[1].Value
    Step "Bundling RP2040 firmware $fwVersion"
    Copy-Item $uf2 (Join-Path $root "firmware\rp2040-169.uf2") -Force
    $manifest = "[`n  { `"board`": `"rp2040-169`", `"version`": `"$fwVersion`", `"file`": `"rp2040-169.uf2`" }`n]`n"
    [IO.File]::WriteAllText((Join-Path $root "firmware\manifest.json"), $manifest)
} else {
    Step "Firmware repo build not found; keeping the committed firmware\"
}

# 2. Tests
if (-not $SkipTests) {
    Step "Testing"
    dotnet test (Join-Path $root "TouchDeck.sln") -c $Configuration --nologo
    Check "tests"
}

# 3. Publish
Step "Publishing self-contained win-x64"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root "src\TouchDeck.App\TouchDeck.App.csproj") -c $Configuration -r win-x64 --self-contained true -o $publish --nologo @versionArgs
Check "publish"

# 4. MSI
Step "Building the installer"
dotnet build (Join-Path $root "installer\TouchDeck.Installer.wixproj") -c $Configuration "-p:PublishDir=$publish\" --nologo @versionArgs
Check "installer build"
$msi = Get-ChildItem (Join-Path $root "installer\bin\x64\$Configuration") -Filter "TouchDeck-*.msi" | Sort-Object LastWriteTime | Select-Object -Last 1
Copy-Item $msi.FullName $out -Force
$size = [math]::Round($msi.Length / 1MB, 1)
Step "Built out\$($msi.Name) ($size MB)"

if ($Smoke) {
    Step "Smoke run of the published app"
    $dir = Join-Path $out "smoke"
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    Start-Process (Join-Path $publish "TouchDeck.exe") -ArgumentList "--smoke", "`"$dir`"" -Wait
    Get-Content (Join-Path $dir "smoke.txt")
}
