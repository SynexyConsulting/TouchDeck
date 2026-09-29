<#
.SYNOPSIS
  End-to-end installer check: silent install, verify, run the installed app, uninstall, verify.

.DESCRIPTION
  Installs out\TouchDeck-<version>.msi per-user (no admin), checks the files, Start-menu
  shortcut and Apps & Features entry, runs the installed exe with --smoke (board detection and
  firmware version, when a board is plugged in), then uninstalls and checks everything is gone.
  -KeepInstalled skips the uninstall. Logs go to out\install.log and out\uninstall.log.
#>
param([switch]$KeepInstalled)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "out"
$msi = Get-ChildItem $out -Filter "TouchDeck-*.msi" | Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $msi) { throw "No MSI in out\ - run build.ps1 first." }

$dir = Join-Path $env:LOCALAPPDATA "Programs\Touch Deck"
$lnk = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Touch Deck.lnk"
$failures = @()
function Expect($ok, $what) {
    if ($ok) { Write-Host "  ok    $what" -ForegroundColor Green }
    else { Write-Host "  FAIL  $what" -ForegroundColor Red; $script:failures += $what }
}
# Per-user MSIs register under Installer\UserData\<SID>, not HKCU\...\Uninstall.
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
function Arp { Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\$sid\Products\*\InstallProperties" -ErrorAction SilentlyContinue |
               Where-Object DisplayName -eq "Touch Deck" }

Write-Host "==> Installing $($msi.Name)" -ForegroundColor Cyan
$p = Start-Process msiexec -ArgumentList "/i `"$($msi.FullName)`" /qn /l*v `"$out\install.log`"" -Wait -PassThru
Expect ($p.ExitCode -eq 0) "msiexec /i exit code 0 (was $($p.ExitCode))"
Expect (Test-Path "$dir\TouchDeck.exe") "TouchDeck.exe in $dir"
Expect (Test-Path "$dir\firmware\manifest.json") "bundled firmware manifest"
Expect (Test-Path "$dir\firmware\rp2040-169.uf2") "bundled RP2040 firmware"
Expect (Test-Path "$dir\licenses\OFL-Barlow.txt") "font licenses"
Expect (Test-Path $lnk) "Start-menu shortcut"
$arp = @(Arp)
Expect ($arp.Count -eq 1) "exactly one Apps & Features entry (found $($arp.Count))"
if ($arp.Count) { Write-Host "        $($arp[0].DisplayName) $($arp[0].DisplayVersion)" }

Write-Host "==> Running the installed app (--smoke)" -ForegroundColor Cyan
$smoke = Join-Path $out "install-smoke"
if (Test-Path $smoke) { Remove-Item $smoke -Recurse -Force }
Start-Process "$dir\TouchDeck.exe" -ArgumentList "--smoke", "`"$smoke`"" -Wait
Expect (Test-Path "$smoke\smoke.txt") "app started and reported"
if (Test-Path "$smoke\smoke.txt") {
    $facts = @{}
    Get-Content "$smoke\smoke.txt" | ForEach-Object { $k, $v = $_ -split "=", 2; $facts[$k] = $v }
    Write-Host "        status=$($facts.status) board=$($facts.board) port=$($facts.port) firmware=$($facts.firmware)"
    Expect ($facts.exe -like "$dir*") "ran from the install folder"
    Expect ($facts.bundled -match "rp2040-169 \d+\.\d+\.\d+") "bundled firmware visible to the app"
    if ($facts.status -eq "Connected") { Expect ($facts.firmware -match "^\d+\.\d+\.\d+$") "board firmware version read" }
    else { Write-Host "        (no board connected: detection not checked)" }
}

if (-not $KeepInstalled) {
    Write-Host "==> Uninstalling" -ForegroundColor Cyan
    $p = Start-Process msiexec -ArgumentList "/x `"$($msi.FullName)`" /qn /l*v `"$out\uninstall.log`"" -Wait -PassThru
    Expect ($p.ExitCode -eq 0) "msiexec /x exit code 0 (was $($p.ExitCode))"
    Expect (-not (Test-Path "$dir\TouchDeck.exe")) "files removed"
    Expect (-not (Test-Path $dir)) "install folder removed"
    Expect (-not (Test-Path $lnk)) "shortcut removed"
    Expect ($null -eq (Arp)) "Apps & Features entry removed"
}

if ($failures.Count) { throw "$($failures.Count) check(s) failed" }
Write-Host "All installer checks passed." -ForegroundColor Green
