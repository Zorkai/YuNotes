#requires -Version 5.1
<#
.SYNOPSIS
  Builds a signed .msix and bundles a ready-to-ship release ZIP for GitHub.

.DESCRIPTION
  Produces packaging\release\YuNotes-<version>-<platform>.zip containing:
    - YuNotes_<version>_<platform>.msix   (the app)
    - YuNotes.cer                         (dev cert to trust)
    - Install.bat / Install.ps1           (trust cert + install, self-elevating)
    - Uninstall.bat / Uninstall.ps1
    - README.txt                          (end-user instructions)

  The bundled scripts are STANDALONE — they only reference files sitting next to
  them, so they work from wherever the user extracts the zip.

  Drag the resulting .zip onto a GitHub release. Users download, extract, and
  double-click Install.bat.

.PARAMETER SkipBuild
  Reuse the existing .msix in packaging\output instead of rebuilding.
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [string]$Configuration = "Release",
    [string]$Platform      = "x64"
)
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$repo = Split-Path -Parent $here

# ── 1. Build (unless reusing) ────────────────────────────────────────────────
if (-not $SkipBuild) {
    & (Join-Path $here "build.ps1") -Configuration $Configuration -Platform $Platform
    if ($LASTEXITCODE -ne 0) { throw "build.ps1 failed (exit $LASTEXITCODE)." }
}

# ── 2. Locate the freshly built artifacts ────────────────────────────────────
$msix = Get-ChildItem (Join-Path $here "output") -Recurse -Filter *.msix -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $msix) { throw "No .msix under packaging\output. Run without -SkipBuild first." }
$cer = Join-Path $here "YuNotes.cer"
if (-not (Test-Path $cer)) { throw "Missing $cer. Run packaging\build.ps1 first." }

# ── 3. Read identity from the manifest (version + package name) ──────────────
[xml]$manifest = Get-Content (Join-Path $repo "src\YuNotes\Package.appxmanifest")
$version = $manifest.Package.Identity.Version
$pkgName = $manifest.Package.Identity.Name
Write-Host "Packaging release for $pkgName $version ($Platform)" -ForegroundColor Cyan

# ── 4. Stage the release folder ──────────────────────────────────────────────
$stageName  = "YuNotes-$version-$Platform"
$releaseDir = Join-Path $here "release"
$stage      = Join-Path $releaseDir $stageName
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

Copy-Item $msix.FullName (Join-Path $stage $msix.Name)
Copy-Item $cer           (Join-Path $stage "YuNotes.cer")

# ── 5. Emit the standalone end-user scripts ──────────────────────────────────
$installPs1 = @'
#requires -Version 5.1
# Trusts the bundled certificate and installs YuNotes. Run via Install.bat
# (which elevates). Only touches files next to this script.
$ErrorActionPreference = "Stop"
$admin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw "Please run Install.bat (it requests administrator rights)." }

$cer  = Join-Path $PSScriptRoot "YuNotes.cer"
$msix = Get-ChildItem $PSScriptRoot -Filter *.msix | Select-Object -First 1
if (-not (Test-Path $cer)) { throw "YuNotes.cer is missing next to this script." }
if (-not $msix)            { throw "No .msix found next to this script." }

Write-Host "Trusting certificate..." -ForegroundColor Cyan
Import-Certificate -FilePath $cer -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null
Write-Host "Installing $($msix.Name)..." -ForegroundColor Cyan
Add-AppxPackage -Path $msix.FullName
Write-Host "Installed. Launch YuNotes from the Start menu." -ForegroundColor Green
'@
Set-Content -Path (Join-Path $stage "Install.ps1") -Value $installPs1 -Encoding UTF8

$installBat = @'
@echo off
setlocal
cd /d "%~dp0"
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator privileges...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
echo ============================================
echo   Installing YuNotes
echo ============================================
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1"
echo.
echo Done. You can close this window.
pause
'@
Set-Content -Path (Join-Path $stage "Install.bat") -Value $installBat -Encoding ASCII

$uninstallPs1 = (@'
#requires -Version 5.1
$ErrorActionPreference = "Stop"
$pkg = Get-AppxPackage -Name "__PKGNAME__"
if ($pkg) {
    Remove-AppxPackage -Package $pkg.PackageFullName
    Write-Host "YuNotes uninstalled." -ForegroundColor Green
} else {
    Write-Host "YuNotes is not installed."
}
'@).Replace("__PKGNAME__", $pkgName)
Set-Content -Path (Join-Path $stage "Uninstall.ps1") -Value $uninstallPs1 -Encoding UTF8

$uninstallBat = @'
@echo off
setlocal
cd /d "%~dp0"
echo Uninstalling YuNotes...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall.ps1"
echo.
echo Done. You can close this window.
pause
'@
Set-Content -Path (Join-Path $stage "Uninstall.bat") -Value $uninstallBat -Encoding ASCII

$readme = @"
YuNotes $version (x64)
======================

TO INSTALL
  1. Double-click  Install.bat
  2. Click "Yes" on the "Do you want to allow this app to make changes?" prompt.
  It trusts the app's certificate, then installs YuNotes. When it finishes,
  YuNotes is in your Start menu.

  (The certificate step is needed because this build is signed with the
  developer's own certificate rather than one bought from a certificate
  authority. It's a one-time trust for this app only.)

TO UNINSTALL
  Double-click  Uninstall.bat
  ...or use Windows Settings > Apps > YuNotes > Uninstall.

REQUIREMENTS
  64-bit Windows 10 version 1809 (build 17763) or newer.

FILES
  YuNotes_${version}_x64.msix   the app package
  YuNotes.cer                   the signing certificate to trust
  Install.bat / Uninstall.bat   run these
"@
Set-Content -Path (Join-Path $stage "README.txt") -Value $readme -Encoding UTF8

# ── 6. Zip the staged folder ─────────────────────────────────────────────────
$zip = Join-Path $releaseDir "$stageName.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip

Write-Host ""
Write-Host "Release ZIP ready:" -ForegroundColor Green
Write-Host "  $zip"
Write-Host "Drag it onto a GitHub release. Users extract and run Install.bat."
