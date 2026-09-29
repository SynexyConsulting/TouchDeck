<#
.SYNOPSIS
  End-to-end in-app update test on this PC, against a loopback feed.

.DESCRIPTION
  Builds and installs version -From, builds -To, serves updates.json and the -To MSI
  from http://127.0.0.1:<port>/, runs the installed app with
  --update-feed ... --smoke-update, and checks that the installed exe becomes -To and
  that the app restarted itself. Leaves -To installed (install the real release over it).
#>
param(
    [string]$From = "1.1.98",
    [string]$To = "1.1.99",
    [int]$Port = 8765
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "out"
$exe = Join-Path $env:LOCALAPPDATA "Programs\Touch Deck\TouchDeck.exe"
$failures = @()
function Expect($ok, $what) {
    if ($ok) { Write-Host "  ok    $what" -ForegroundColor Green }
    else { Write-Host "  FAIL  $what" -ForegroundColor Red; $script:failures += $what }
}

Write-Host "==> Building $From and $To" -ForegroundColor Cyan
& (Join-Path $root "build.ps1") -SkipTests -Version $From | Out-Null
& (Join-Path $root "build.ps1") -SkipTests -Version $To | Out-Null
$fromMsi = Join-Path $out "TouchDeck-$From.msi"
$toMsi = Join-Path $out "TouchDeck-$To.msi"

Write-Host "==> Installing $From" -ForegroundColor Cyan
if (Test-Path $exe) { (Start-Process $exe -ArgumentList "--quit" -PassThru).WaitForExit(15000) | Out-Null }
# Start clean: a newer installed copy would make installing -From a (refused) downgrade.
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\$sid\Products\*\InstallProperties" -ErrorAction SilentlyContinue |
    Where-Object DisplayName -eq "Touch Deck" | ForEach-Object {
        $code = [regex]::Match($_.UninstallString, '\{[0-9A-Fa-f-]{36}\}').Value
        if ($code) { Start-Process msiexec -ArgumentList "/x $code /qn" -Wait }
    }
$p = Start-Process msiexec -ArgumentList "/i `"$fromMsi`" /qn" -Wait -PassThru
Expect ($p.ExitCode -eq 0) "install $From"
Expect ((Get-Item $exe).VersionInfo.ProductVersion -like "$From*") "installed exe is $From"

Write-Host "==> Serving the $To feed on 127.0.0.1:$Port" -ForegroundColor Cyan
$site = Join-Path $out "e2e-site"
if (Test-Path $site) { Remove-Item $site -Recurse -Force }
New-Item -ItemType Directory $site | Out-Null
Copy-Item $toMsi (Join-Path $site "TouchDeck-$To.msi")
$sha = (Get-FileHash $toMsi -Algorithm SHA256).Hash.ToLower()
$size = (Get-Item $toMsi).Length
$feed = "{`"schema`": 1, `"app`": {`"windows`": {`"version`": `"$To`", `"url`": `"http://127.0.0.1:$Port/TouchDeck-$To.msi`", `"sha256`": `"$sha`", `"size`": $size}}, `"firmware`": []}"
[IO.File]::WriteAllText((Join-Path $site "updates.json"), $feed)
$server = Start-Process python -ArgumentList "-m", "http.server", "$Port", "--bind", "127.0.0.1", "--directory", "`"$site`"" -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2

try {
    Write-Host "==> Running the installed $From with --smoke-update" -ForegroundColor Cyan
    $report = Join-Path $out "e2e-report"
    if (Test-Path $report) { Remove-Item $report -Recurse -Force }
    # Wait for this process only: Start-Process -Wait (PS 5.1) also waits for descendants,
    # and the relaunched app is one, so it would never return.
    $old = Start-Process $exe -ArgumentList "--update-feed", "http://127.0.0.1:$Port/updates.json", "--smoke-update", "`"$report`"" -PassThru
    if (-not $old.WaitForExit(120000)) { throw "the app did not quit for the installer within 2 minutes" }
    $facts = @{}
    Get-Content (Join-Path $report "update.txt") | ForEach-Object { $k, $v = $_ -split "=", 2; $facts[$k] = $v }
    Write-Host "        $($facts.app) -> offer $($facts.offer_app) ($($facts.app_text)) feed=$($facts.feed) error=$($facts.error)"
    Expect ($facts.offer_app -eq $To) "the app offered $To"

    Write-Host "==> Waiting for the installer and the restart" -ForegroundColor Cyan
    $deadline = (Get-Date).AddSeconds(120)
    do {
        Start-Sleep -Seconds 2
        $ver = (Get-Item $exe -ErrorAction SilentlyContinue).VersionInfo.ProductVersion
        $running = Get-Process TouchDeck -ErrorAction SilentlyContinue
    } until ((($ver -like "$To*") -and $running) -or (Get-Date) -gt $deadline)
    Expect ($ver -like "$To*") "installed exe is now $To (was $From)"
    Expect ($null -ne $running) "the app restarted itself after the update"
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $arp = @(Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\$sid\Products\*\InstallProperties" -ErrorAction SilentlyContinue |
             Where-Object DisplayName -eq "Touch Deck")
    Expect ($arp.Count -eq 1 -and $arp[0].DisplayVersion -eq $To) "one Apps & Features entry, version $To"
    Expect (-not (Test-Path (Join-Path $env:LOCALAPPDATA "TouchDeck\Updates\TouchDeck-update.msi.part"))) "no partial download left"
}
finally {
    Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    if (Test-Path $exe) { (Start-Process $exe -ArgumentList "--quit" -PassThru).WaitForExit(15000) | Out-Null }
}

if ($failures.Count) { throw "$($failures.Count) check(s) failed" }
Write-Host "In-app update $From -> $To passed." -ForegroundColor Green
