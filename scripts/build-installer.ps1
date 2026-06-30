# Build the single-file Windows installer (NSIS) from the published self-contained app.
# Produces artifacts/Deduplicator-Setup-<version>.exe -- the user downloads ONE exe that
# unpacks + installs everything (Program Files, shortcuts, uninstaller). Run publish first.
# ASCII-only on purpose: Windows PowerShell 5.1 reads .ps1 as the system ANSI codepage.
#
#   powershell -ExecutionPolicy Bypass -File scripts/publish-win-x64.ps1
#   powershell -ExecutionPolicy Bypass -File scripts/build-installer.ps1
#
param([string]$Version = "1.0.0")
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$pub  = Join-Path $root 'dist/Deduplicator-win-x64'
$icon = Join-Path $root 'src/Deduplicator.App/Assets/app.ico'
$outDir = Join-Path $root 'artifacts'
$out  = Join-Path $outDir ("Deduplicator-Setup-$Version.exe")

if (-not (Test-Path (Join-Path $pub 'Deduplicator.exe'))) {
    Write-Host "Publish folder missing: $pub" -ForegroundColor Red
    Write-Host "Run scripts/publish-win-x64.ps1 first." -ForegroundColor Yellow
    exit 1
}
New-Item -ItemType Directory -Force $outDir | Out-Null

$mk = @("$env:ProgramFiles\NSIS\makensis.exe", "${env:ProgramFiles(x86)}\NSIS\makensis.exe") |
      Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $mk) { $c = Get-Command makensis -ErrorAction SilentlyContinue; if ($c) { $mk = $c.Source } }
if (-not $mk) {
    Write-Host "makensis not found. Install NSIS from https://nsis.sourceforge.io and retry." -ForegroundColor Red
    exit 1
}

if (Test-Path $out) { Remove-Item $out -Force }
& $mk /DVERSION=$Version /DPUBDIR=$pub /DOUTFILE=$out /DICON=$icon (Join-Path $root 'installer/Deduplicator.nsi')
if (-not (Test-Path $out)) { Write-Host "Installer build FAILED." -ForegroundColor Red; exit 1 }
Write-Host ("Installer: {0} ({1} MB)" -f $out, [math]::Round((Get-Item $out).Length / 1MB, 1)) -ForegroundColor Green
