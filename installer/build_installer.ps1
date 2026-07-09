<#
.SYNOPSIS
    Builds the Windows installer (a single Setup .exe) from an existing publish folder.

.DESCRIPTION
    Run publish.ps1 first — this script packages what publish.ps1 produced, it does
    not build the app itself.

    The result is one file, publish\SolidWorksPartMatcher-Setup-v<version>.exe, which
    installs the app plus its bundled viewer\ folder and creates a Start-menu shortcut.
    Recipients never see the bundled Python/OpenCASCADE runtime.

.PARAMETER Version
    Version to package. Must match a folder produced by publish.ps1.

.EXAMPLE
    .\installer\build_installer.ps1 -Version 1.1.0

.NOTES
    Requires Inno Setup 6:  winget install --id JRSoftware.InnoSetup -e
#>
param(
    [string]$Version = "1.1.0"
)

# Continue, not Stop: ISCC writes progress to stderr, which under "Stop" PowerShell
# wraps as a terminating NativeCommandError even on a successful run. We gate on
# $LASTEXITCODE and throw explicitly instead.
$ErrorActionPreference = "Continue"

$repoRoot  = Split-Path $PSScriptRoot -Parent
$sourceDir = Join-Path $repoRoot "publish\SolidWorksPartMatcher-v$Version"
$issFile   = Join-Path $PSScriptRoot "SolidWorksPartMatcher.iss"
$outFile   = Join-Path $repoRoot "publish\SolidWorksPartMatcher-Setup-v$Version.exe"

# ── Locate the Inno Setup compiler ────────────────────────────────────────────
$isccCandidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 not found. Install it with:  winget install --id JRSoftware.InnoSetup -e"
}

if (-not (Test-Path $sourceDir)) {
    throw "Publish folder not found: $sourceDir`nRun  .\publish.ps1 -Version $Version  first."
}

$exe = Join-Path $sourceDir "SolidWorksPartMatcher.App.exe"
if (-not (Test-Path $exe)) { throw "Expected app exe not found: $exe" }

Write-Host ""
Write-Host "=== SolidWorks Part Matcher -- installer v$Version ===" -ForegroundColor Cyan
Write-Host "Source : $sourceDir"
Write-Host "Output : $outFile"
Write-Host "ISCC   : $iscc"
Write-Host ""

# ── Un-hide the viewer folder before packaging ────────────────────────────────
# publish.ps1 marks viewer\ hidden so it stays out of sight in the zip layout. The
# installer puts it under Program Files where the user never looks, so the flag is
# unnecessary — and clearing it guarantees every file is picked up by the [Files]
# wildcard regardless of how the compiler treats hidden sources.
$viewer = Join-Path $sourceDir "viewer"
if (Test-Path $viewer) {
    $vi = Get-Item $viewer -Force
    if ($vi.Attributes -band [IO.FileAttributes]::Hidden) {
        $vi.Attributes = $vi.Attributes -band (-bnot [IO.FileAttributes]::Hidden)
        Write-Host "Cleared hidden flag on viewer\ for packaging." -ForegroundColor DarkGray
    }
}

# ── Compile ───────────────────────────────────────────────────────────────────
Write-Host "Compiling (LZMA2/max over ~900 MB - this takes several minutes)..." -ForegroundColor Yellow

& $iscc "/DAppVersion=$Version" "/DSourceDir=$sourceDir" $issFile
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit $LASTEXITCODE)." }

if (-not (Test-Path $outFile)) { throw "Compiler reported success but $outFile is missing." }

$sizeMB = [math]::Round((Get-Item $outFile).Length / 1MB, 1)

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  Installer ($sizeMB MB): $outFile"
Write-Host ""
Write-Host "Distribute that single file. It installs per-user by default (no admin"
Write-Host "prompt); pass /ALLUSERS for a machine-wide install."
Write-Host ""
