<#
.SYNOPSIS
    Builds the bundled Python tools (view_steps.exe + compute_component_volume.exe, one shared
    bundle) using PyInstaller.
    Run this once on any developer machine that has Python + pyvista + build123d installed.
    The output (tools/dist/view_steps/) is then picked up by publish.ps1.

.NOTES
    Build-machine prerequisites:
      pip install pyvista build123d      (already required for development)
    PyInstaller is installed automatically by this script.

    The resulting bundle is ~400-600 MB and contains a full Python runtime,
    VTK, pyvista, and build123d/OCP — end users need nothing installed.
#>

$ErrorActionPreference = "Stop"

$toolsDir = $PSScriptRoot   # this script lives in tools/
$specFile  = Join-Path $toolsDir "view_steps.spec"

if (-not (Test-Path $specFile)) {
    Write-Error "Spec file not found: $specFile"
}

Push-Location $toolsDir
try {
    # ── Install / upgrade PyInstaller ────────────────────────────────────────
    Write-Host ""
    Write-Host "=== Step 1: ensure PyInstaller is installed ===" -ForegroundColor Cyan
    python -m pip install "pyinstaller>=6.0" --quiet
    if ($LASTEXITCODE -ne 0) { Write-Error "pip install pyinstaller failed" }

    # ── Clean previous build artefacts ───────────────────────────────────────
    Write-Host ""
    Write-Host "=== Step 2: clean previous build ===" -ForegroundColor Cyan
    foreach ($dir in @("build", "dist")) {
        if (Test-Path $dir) {
            Write-Host "  Removing $dir/ ..."
            Remove-Item $dir -Recurse -Force
        }
    }

    # ── Run PyInstaller ───────────────────────────────────────────────────────
    Write-Host ""
    Write-Host "=== Step 3: PyInstaller build (this takes several minutes) ===" -ForegroundColor Cyan
    python -m PyInstaller view_steps.spec -y
    if ($LASTEXITCODE -ne 0) { Write-Error "PyInstaller failed (exit $LASTEXITCODE)" }

    # ── Verify output ─────────────────────────────────────────────────────────
    # The one bundle carries BOTH tools (they share the Python/OCP runtime): the 3D viewer and
    # the real-volume computer the assembly-comparison feature depends on for accurate volumes.
    foreach ($exeName in @("view_steps.exe", "compute_component_volume.exe")) {
        $exePath = Join-Path $toolsDir "dist\view_steps\$exeName"
        if (-not (Test-Path $exePath)) {
            Write-Error "Build succeeded but expected exe not found: $exePath"
        }
    }

    $bundleDir = Join-Path $toolsDir "dist\view_steps"
    $sizeMB = [math]::Round(
        (Get-ChildItem $bundleDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 0)

    Write-Host ""
    Write-Host "=== Done ===" -ForegroundColor Green
    Write-Host "  Bundle : $bundleDir"
    Write-Host "  Size   : ~$sizeMB MB"
    Write-Host ""
    Write-Host "Next step: run publish.ps1 to produce the distributable ZIP." -ForegroundColor DarkGray
    Write-Host "(Or copy dist\view_steps\ to <app-output>\viewer\ manually for testing.)" -ForegroundColor DarkGray
    Write-Host ""
}
finally {
    Pop-Location
}
