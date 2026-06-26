<#
.SYNOPSIS
    Publishes SolidWorks Part Matcher as a single-file self-contained Windows x64 release
    and zips the output for distribution.

.PARAMETER Version
    Semantic version string embedded in the output folder and zip name.
    Defaults to the version declared in the App project (1.0.0).

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -Version 1.1.0

.NOTES
    Build machine requirements:
      - SolidWorks 2024 installed at the default path (for interop DLLs at build time)
      - .NET 8 SDK

    Target machine requirements:
      - Windows 10/11 x64
      - SolidWorks 2024, licensed
      - No .NET runtime needed (bundled in the output)
#>
param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

$dotnet  = "C:\Program Files\dotnet\dotnet.exe"
$project = "$PSScriptRoot\src\SolidWorksPartMatcher.App\SolidWorksPartMatcher.App.csproj"
$outDir  = "$PSScriptRoot\publish\SolidWorksPartMatcher-v$Version"
$zipPath = "$PSScriptRoot\publish\SolidWorksPartMatcher-v$Version.zip"

if (-not (Test-Path $dotnet)) {
    Write-Error ".NET SDK not found at '$dotnet'. Adjust the path or add dotnet to PATH."
}

Write-Host ""
Write-Host "=== SolidWorks Part Matcher -- publish v$Version ===" -ForegroundColor Cyan
Write-Host "Output folder : $outDir"
Write-Host "Zip           : $zipPath"
Write-Host ""

# Clean previous output so stale files don't sneak in.
if (Test-Path $outDir) {
    Write-Host "Removing previous publish output..." -ForegroundColor DarkGray
    Remove-Item $outDir -Recurse -Force
}

Write-Host "Running dotnet publish (single-file)..." -ForegroundColor Yellow

& $dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:Version=$Version `
    -p:AssemblyVersion="$Version.0" `
    -p:FileVersion="$Version.0" `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false `
    --output $outDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed (exit $LASTEXITCODE)."
}

# Verify the main exe exists.
$exe = Join-Path $outDir "SolidWorksPartMatcher.App.exe"
if (-not (Test-Path $exe)) {
    Write-Error "Expected exe not found: $exe"
}

# Remove debug symbol files — end users don't need them.
Get-ChildItem $outDir -Filter "*.pdb" | Remove-Item -Force

# Zip the output folder.
Write-Host ""
Write-Host "Creating zip..." -ForegroundColor Yellow
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path "$outDir\*" -DestinationPath $zipPath

$fileCount = (Get-ChildItem $outDir -File).Count
$zipBytes  = (Get-Item $zipPath).Length
$sizeMB    = [math]::Round($zipBytes / 1048576, 1)

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  Publish folder : $outDir ($fileCount file(s))"
Write-Host "  Zip ($sizeMB MB)   : $zipPath"
Write-Host ""
Write-Host "Distribute the ZIP. Recipients unzip and run SolidWorksPartMatcher.App.exe."
Write-Host "Requires: Windows 10/11 x64 + SolidWorks 2024 (licensed)."
Write-Host ""
