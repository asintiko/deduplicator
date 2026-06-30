# Clean Windows publish + verify the NEW engine actually made it into the output.
# Solves the audit P0 "sources are new but the setup is stale". Run from the repo root.
# ASCII-only on purpose: Windows PowerShell 5.1 reads .ps1 as the system ANSI codepage,
# so non-ASCII comments can corrupt parsing depending on file encoding.
#
#   powershell -ExecutionPolicy Bypass -File scripts/publish-win-x64.ps1
#
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root 'dist/Deduplicator-win-x64'

Write-Host '== clean ==' -ForegroundColor Cyan
dotnet clean "$root/Deduplicator.slnx" -c Release | Out-Null
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

Write-Host '== build + test ==' -ForegroundColor Cyan
dotnet build "$root/Deduplicator.slnx" -c Release
dotnet test  "$root/Deduplicator.slnx" -c Release --no-build

Write-Host '== publish (self-contained win-x64) ==' -ForegroundColor Cyan
dotnet publish "$root/src/Deduplicator.App/Deduplicator.App.csproj" `
    -c Release -r win-x64 --self-contained true -o $out

# --- Verify the publish folder contains the NEW engine, not a stale one ---
$fail = $false
function Check([bool]$ok, [string]$msg) {
    if ($ok) { Write-Host "OK   $msg" -ForegroundColor Green }
    else     { Write-Host "FAIL $msg" -ForegroundColor Red; $script:fail = $true }
}

Check (Test-Path (Join-Path $out 'FastDedup.Engine.dll'))  'FastDedup.Engine.dll present'
Check (Test-Path (Join-Path $out 'Deduplicator.Core.dll')) 'Deduplicator.Core.dll present'

$deps = Join-Path $out 'Deduplicator.deps.json'
Check ((Test-Path $deps) -and (Select-String -Path $deps -Pattern 'FastDedup.Engine' -Quiet)) `
    'deps.json references FastDedup.Engine'

# New engine symbols must be in FastDedup.Engine.dll (rough proof fresh code was built).
$engine = Join-Path $out 'FastDedup.Engine.dll'
foreach ($sym in 'Win32FindMetadataEnumerator','BackendSelector','UsnEnumerator') {
    Check (Select-String -Path $engine -Pattern $sym -Quiet) "FastDedup.Engine.dll contains '$sym'"
}

# New Core symbols too.
$core = Join-Path $out 'Deduplicator.Core.dll'
foreach ($sym in 'ExactDuplicateEngineV2','ProgressCoalescer','VolumeParallelExecutor') {
    Check (Select-String -Path $core -Pattern $sym -Quiet) "Deduplicator.Core.dll contains '$sym'"
}

if ($fail) { Write-Host "`nPUBLISH FAILED VERIFICATION - do not distribute this dist." -ForegroundColor Red; exit 1 }
Write-Host "`nDone: $out" -ForegroundColor Green
