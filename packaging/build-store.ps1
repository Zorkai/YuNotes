#requires -Version 5.1
<#
.SYNOPSIS
  Builds the UNSIGNED .msixupload for a Microsoft Store submission.

.DESCRIPTION
  Store packages differ from the sideload build (build.ps1) in two ways:
    1. NOT signed  - Microsoft signs Store packages, so signing is OFF here.
       (A self-signed package is rejected by the Store.)
    2. StoreUpload package mode - produces a .msixupload, the file you upload
       in Partner Center, rather than a bare .msix.

  Output: packaging\store-output\...\YuNotes_<version>_x64.msixupload

  Upload that .msixupload in your Partner Center submission (Packages section).

.PARAMETER FrameworkDependent
  By DEFAULT this builds SELF-CONTAINED: the Windows App SDK runtime is bundled
  inside the package so it has NO dependency on non-integrated software. This
  avoids Store policy 10.2.4.1 ("disclose dependencies on non-integrated
  software"), which flags a framework-dependent WinAppSDK package unless the
  dependency is disclosed in the first two lines of the Store description.
  Pass -FrameworkDependent to instead take the runtime as a Store-managed
  framework dependency (smaller download) - but then you MUST add that
  disclosure to the description or the submission is rejected.

.PARAMETER Configuration
  Build configuration (default Release - always use Release for the Store).

.PARAMETER Platform
  Target platform (default x64 - matches the project's single supported arch).

.NOTES
  Close a running YuNotes of the same Configuration first, or the build can't
  overwrite its files. Run the Windows App Certification Kit (WACK) against the
  package before submitting - Visual Studio's packaging wizard can do this, or
  run it standalone from the Windows SDK.
#>
[CmdletBinding()]
param(
    [switch]$FrameworkDependent,
    [string]$Configuration = "Release",
    [string]$Platform      = "x64"
)
$ErrorActionPreference = "Stop"

# Self-contained by default (bundles the WinAppSDK runtime = no non-integrated
# dependency to disclose). -FrameworkDependent opts into the smaller build.
$selfContained = -not $FrameworkDependent.IsPresent

$repo   = Split-Path -Parent $PSScriptRoot
$proj   = Join-Path $repo "src\YuNotes\YuNotes.csproj"
$outDir = Join-Path $PSScriptRoot "store-output"

# Read identity from the manifest purely for logging / sanity.
$manifest  = Join-Path $repo "src\YuNotes\Package.appxmanifest"
[xml]$mx   = Get-Content $manifest -Raw
$version   = $mx.Package.Identity.Version
$pkgName   = $mx.Package.Identity.Name
$publisher = $mx.Package.Identity.Publisher

Write-Host "Store build for $pkgName $version ($Platform)" -ForegroundColor Cyan
Write-Host "  Publisher:      $publisher"
Write-Host "  Self-contained: $selfContained (runtime bundled = $selfContained)"
if (-not $selfContained) {
    Write-Host "  WARNING: framework-dependent build. You MUST disclose the Windows App" -ForegroundColor Yellow
    Write-Host "           Runtime dependency in the first two lines of the Store description" -ForegroundColor Yellow
    Write-Host "           (policy 10.2.4.1), or the submission will be rejected." -ForegroundColor Yellow
}

# Guard against submitting with the leftover sideload placeholder identity.
if ($publisher -eq "CN=YuNotes" -or $pkgName -eq "YuNotes.YuNotes") {
    throw "Package.appxmanifest still has the sideload placeholder identity ($pkgName / $publisher). Set the real Partner Center Name/Publisher before building a Store package."
}

if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Path $outDir | Out-Null

# Build the Store upload package. Signing OFF (Microsoft signs); StoreUpload mode
# emits the .msixupload. WindowsAppSDKSelfContained is passed explicitly so this
# script controls the runtime packaging without editing the csproj.
Write-Host "Building .msixupload ($Configuration | $Platform)..." -ForegroundColor Cyan
& dotnet build $proj `
    -c $Configuration `
    -p:Platform=$Platform `
    -p:GenerateAppxPackageOnBuild=true `
    -p:AppxPackageSigningEnabled=false `
    -p:UapAppxPackageBuildMode=StoreUpload `
    -p:AppxBundle=Never `
    -p:WindowsAppSDKSelfContained=$($selfContained.ToString().ToLower()) `
    -p:AppxPackageDir="$outDir\"

if ($LASTEXITCODE -ne 0) {
    throw "Build failed (exit $LASTEXITCODE). If YuNotes is running, close it and retry."
}

$upload = Get-ChildItem -Path $outDir -Recurse -Filter *.msixupload |
          Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $upload) {
    throw "Build succeeded but no .msixupload was produced under $outDir."
}

Write-Host ""
Write-Host "Store package ready:" -ForegroundColor Green
Write-Host "  $($upload.FullName)"
Write-Host ""
Write-Host "Next: run WACK against it, then upload it in Partner Center"
Write-Host "(your YuNotes submission > Packages)."
