#requires -Version 5.1
<#
.SYNOPSIS
  Builds a signed YuNotes .msix for local sideloading.

.DESCRIPTION
  1. Creates a self-signed code-signing certificate (CN=YuNotes) on first run and
     exports it to YuNotes.pfx / YuNotes.cer beside this script. The certificate
     subject MUST equal the Package.appxmanifest <Identity Publisher>, or Windows
     rejects the package.
  2. Builds the project with MSIX packaging enabled and signs the package.

  Output: packaging\output\<...>\YuNotes_<ver>_x64.msix

  NOTE: close the running YuNotes first if you are building the same
  Configuration it is running from, or the build can't overwrite its files.
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Platform      = "x64"
)
$ErrorActionPreference = "Stop"

$repo   = Split-Path -Parent $PSScriptRoot
$proj   = Join-Path $repo "src\YuNotes\YuNotes.csproj"
$pfx    = Join-Path $PSScriptRoot "YuNotes.pfx"
$cer    = Join-Path $PSScriptRoot "YuNotes.cer"
$outDir = Join-Path $PSScriptRoot "output"
$pfxPassword = "YuNotes"   # dev cert only — not a secret

# ── 1. Ensure the signing certificate exists in the user's store ────────────
# The MSIX signing tool imports the cert from the store BY THUMBPRINT (the same
# thing Visual Studio does). Passing a password-protected .pfx directly to the
# build fails with APPX0105 ("key file may be password protected"), so the store
# is the reliable path. We also export a .cer (for install-time trust) and a .pfx
# (portable backup / CI).
$storeCert = Get-ChildItem "Cert:\CurrentUser\My" |
    Where-Object { $_.Subject -eq "CN=YuNotes" } | Select-Object -First 1

if (-not $storeCert) {
    if (Test-Path $pfx) {
        Write-Host "Importing existing YuNotes.pfx into CurrentUser\My..." -ForegroundColor Cyan
        $sec = ConvertTo-SecureString -String $pfxPassword -Force -AsPlainText
        $storeCert = Import-PfxCertificate -FilePath $pfx `
            -CertStoreLocation "Cert:\CurrentUser\My" -Password $sec
    } else {
        Write-Host "Creating self-signed dev certificate (CN=YuNotes)..." -ForegroundColor Cyan
        $storeCert = New-SelfSignedCertificate `
            -Type CodeSigningCert `
            -Subject "CN=YuNotes" `
            -KeyUsage DigitalSignature `
            -KeyExportPolicy Exportable `
            -FriendlyName "YuNotes Dev Cert" `
            -CertStoreLocation "Cert:\CurrentUser\My" `
            -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
        $sec = ConvertTo-SecureString -String $pfxPassword -Force -AsPlainText
        Export-PfxCertificate -Cert $storeCert -FilePath $pfx -Password $sec | Out-Null
        Write-Host "  wrote $pfx"
    }
}
if (-not (Test-Path $cer)) {
    Export-Certificate -Cert $storeCert -FilePath $cer | Out-Null
    Write-Host "  wrote $cer"
}
$thumb = $storeCert.Thumbprint
Write-Host "Signing with cert thumbprint $thumb"

# ── 2. Build + sign the MSIX ─────────────────────────────────────────────────
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

Write-Host "Building signed MSIX ($Configuration | $Platform)..." -ForegroundColor Cyan
& dotnet build $proj `
    -c $Configuration `
    -p:Platform=$Platform `
    -p:GenerateAppxPackageOnBuild=true `
    -p:AppxPackageSigningEnabled=true `
    -p:AppxBundle=Never `
    -p:PackageCertificateThumbprint=$thumb `
    -p:AppxPackageDir="$outDir\"

if ($LASTEXITCODE -ne 0) {
    throw "Build failed (exit $LASTEXITCODE). If YuNotes is running, close it and retry."
}

$msix = Get-ChildItem -Path $outDir -Recurse -Filter *.msix |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $msix) { throw "Build succeeded but no .msix was produced under $outDir." }

Write-Host ""
Write-Host "MSIX ready:" -ForegroundColor Green
Write-Host "  $($msix.FullName)"
Write-Host "Install it with:  packaging\install.ps1   (run elevated)"
