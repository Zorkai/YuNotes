#requires -Version 5.1
<#
.SYNOPSIS
  Publishes YuNotes as a single, standalone, self-contained .exe (no MSIX,
  no installer, no folder — one file that contains everything).

.DESCRIPTION
  Produces one YuNotes.exe that bundles the .NET 8 runtime, the Windows App SDK
  runtime, all managed/native dependencies and resources.pri. It is unpackaged
  (WindowsPackageType=None), so it just runs — copy the exe anywhere and
  double-click it. No certificate, trust step or install is required.

  This is independent of the MSIX/Store build (build.ps1 / build-store.ps1);
  the project still builds a packaged app by default. Uses the publish profile
  src\YuNotes\Properties\PublishProfiles\win-x64-exe.pubxml.

  Output: packaging\exe-output\YuNotes.exe

  NOTE: On first launch a single-file exe self-extracts its native components to
  a per-user temp folder, so the very first start is slightly slower. Close any
  running YuNotes before republishing so its files can be overwritten.
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime       = "win-x64"
)
$ErrorActionPreference = "Stop"

$repo   = Split-Path -Parent $PSScriptRoot
$proj   = Join-Path $repo "src\YuNotes\YuNotes.csproj"
$outDir = Join-Path $PSScriptRoot "exe-output"

if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Path $outDir | Out-Null

Write-Host "Publishing standalone single-file exe ($Configuration | $Runtime)..." -ForegroundColor Cyan
& dotnet publish $proj `
    -c $Configuration `
    -r $Runtime `
    -p:PublishProfile=win-x64-exe `
    -p:PublishDir="$outDir\"

if ($LASTEXITCODE -ne 0) {
    throw "Publish failed (exit $LASTEXITCODE). If YuNotes is running, close it and retry."
}

$exe = Join-Path $outDir "YuNotes.exe"
if (-not (Test-Path $exe)) { throw "Publish succeeded but $exe was not produced." }

$sizeMB = [math]::Round((Get-Item $exe).Length / 1MB, 1)

# Report anything left beside the exe. A clean single-file build should contain
# only YuNotes.exe (plus optionally the .pdb debug symbols, which are harmless).
$extras = Get-ChildItem $outDir -Recurse -File |
    Where-Object { $_.Name -ne "YuNotes.exe" -and $_.Extension -ne ".pdb" }

Write-Host ""
Write-Host "Standalone exe ready:" -ForegroundColor Green
Write-Host "  $exe  ($sizeMB MB)"
if ($extras) {
    Write-Host ""
    Write-Host "NOTE: these files were emitted next to the exe (not folded in):" -ForegroundColor Yellow
    $extras | ForEach-Object { Write-Host "  $($_.FullName.Substring($outDir.Length + 1))" }
} else {
    Write-Host "  Everything is packed into the single exe. Copy it anywhere and run it." -ForegroundColor Green
}
