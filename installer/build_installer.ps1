<#
.SYNOPSIS
    Builds the Windows installer (a single Setup .exe) from an existing publish folder.

.DESCRIPTION
    Run publish.ps1 first — this script packages what publish.ps1 produced, it does
    not build the app itself.

    The result is one file, publish\Tytle3DModelComparator-Setup-v<version>.exe, which
    installs the app plus its bundled viewer\ folder and creates a Start-menu shortcut.
    Recipients never see the bundled Python/OpenCASCADE runtime.

.PARAMETER Version
    Version to package. Must match a folder produced by publish.ps1.

.EXAMPLE
    .\installer\build_installer.ps1 -Version 1.1.0

.NOTES
    Requires Inno Setup 6.3 or later:  winget install --id JRSoftware.InnoSetup -e

    6.3.0 is the minimum because the .iss uses the x64compatible architecture
    identifier, which older 6.x releases reject.
#>
param(
    [string]$Version = "1.2.1"
)

# Continue, not Stop: ISCC writes progress to stderr, which under "Stop" PowerShell
# wraps as a terminating NativeCommandError even on a successful run. We gate on
# $LASTEXITCODE and throw explicitly instead.
$ErrorActionPreference = "Continue"

$repoRoot  = Split-Path $PSScriptRoot -Parent
$sourceDir = Join-Path $repoRoot "publish\Tytle3DModelComparator-v$Version"
$issFile   = Join-Path $PSScriptRoot "Tytle3DModelComparator.iss"
$outFile   = Join-Path $repoRoot "publish\Tytle3DModelComparator-Setup-v$Version.exe"

# ── Locate the Inno Setup compiler ────────────────────────────────────────────
$isccCandidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6.3+ not found. Install it with:  winget install --id JRSoftware.InnoSetup -e"
}

# The .iss requires Inno Setup 6.3.0+ (it uses the x64compatible architecture identifier).
# We deliberately do not check the version here: Inno Setup's binaries all report a file
# version of 0.0.0.0, so there is nothing reliable to read. The .iss carries an #error guard
# instead, which the preprocessor evaluates against its own version and reports immediately.

if (-not (Test-Path $sourceDir)) {
    throw "Publish folder not found: $sourceDir`nRun  .\publish.ps1 -Version $Version  first."
}

$exe = Join-Path $sourceDir "Tytle3DModelComparator.exe"
if (-not (Test-Path $exe)) { throw "Expected app exe not found: $exe" }

Write-Host ""
Write-Host "=== Tytle 3D Model Comparator -- installer v$Version ===" -ForegroundColor Cyan
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
