# Produce CLEAN distributable archives (audit P2.1): a source zip without bin/obj/dist,
# and a separate release zip with only the published app. ASCII-only for Windows PowerShell 5.1.
#
#   powershell -ExecutionPolicy Bypass -File scripts/package-source.ps1
#
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force $outDir | Out-Null

$srcZip = Join-Path $outDir 'projact-source.zip'
$relZip = Join-Path $outDir 'projact-release-win-x64.zip'
foreach ($z in @($srcZip, $relZip)) { if (Test-Path $z) { Remove-Item $z -Force } }

# --- Source archive: code only, no build artifacts ---
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ('projact-src-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $staging | Out-Null
try {
    foreach ($d in @('src', 'tests', 'docs', 'scripts', 'tools')) {
        $s = Join-Path $root $d
        if (Test-Path $s) { Copy-Item $s (Join-Path $staging $d) -Recurse }
    }
    Get-ChildItem $root -File |
        Where-Object { $_.Extension -in '.slnx', '.sln' -or $_.Name -eq '.gitignore' -or $_.Name -eq 'README.md' } |
        ForEach-Object { Copy-Item $_.FullName $staging }

    # Strip build/scratch dirs that may have been copied in: bin, obj, dist, .codex_manual, artifacts.
    Get-ChildItem $staging -Recurse -Directory -Force |
        Where-Object { $_.Name -in @('bin', 'obj', 'dist', '.codex_manual', 'artifacts') } |
        Sort-Object FullName -Descending |
        ForEach-Object { Remove-Item $_.FullName -Recurse -Force }

    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $srcZip
}
finally {
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
}

# --- Release archive: only the published self-contained app ---
$dist = Join-Path $root 'dist/Deduplicator-win-x64'
if (Test-Path $dist) {
    Compress-Archive -Path $dist -DestinationPath $relZip
    Write-Host ("Release: {0}" -f $relZip) -ForegroundColor Green
} else {
    Write-Host "Release skipped: run scripts/publish-win-x64.ps1 first to create dist/." -ForegroundColor Yellow
}

# --- Verify the source zip is clean ---
Add-Type -AssemblyName System.IO.Compression.FileSystem
# NB: Windows PowerShell 5.1 Compress-Archive writes entry FullName with BACKSLASH separators, so the
# verify regex must accept BOTH separators ([\\/]) or it would never match and silently pass a dirty zip.
$zip = [System.IO.Compression.ZipFile]::OpenRead($srcZip)
$dirty = $zip.Entries | Where-Object { $_.FullName -match '(^|[\\/])(bin|obj|dist|\.codex_manual|artifacts)[\\/]' }
$count = $zip.Entries.Count
$zip.Dispose()
if ($dirty) {
    Write-Host "FAIL: source zip still contains bin/obj/dist/.codex_manual/artifacts entries:" -ForegroundColor Red
    $dirty | ForEach-Object { Write-Host ("  " + $_.FullName) -ForegroundColor Red }
    exit 1
}
Write-Host ("Source:  {0} ({1} entries, no bin/obj/dist/.codex_manual/artifacts)" -f $srcZip, $count) -ForegroundColor Green
