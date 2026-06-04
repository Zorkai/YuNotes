# YuNotes - build and self-sign
#
# Usage from PowerShell:
#   .\build-signed.ps1                        # Debug build, signs the exe
#   .\build-signed.ps1 -Configuration Release # Release build
#   .\build-signed.ps1 -Run                   # also launch after signing
#   .\build-signed.ps1 -TrustCert             # one-time: install the cert into
#                                             #   LocalMachine\Root (needs admin)
#
# What it does:
#   1. Ensures a self-signed code-signing certificate "CN=YuNotes Dev" exists in
#      CurrentUser\My (creates one if missing).
#   2. Runs dotnet build for the project.
#   3. Signs the produced YuNotes.exe with that certificate.
#   4. Optionally launches the signed exe.
#
# Notes:
#   - Step 1 and Step 3 do NOT need administrator privileges.
#   - -TrustCert needs an elevated PowerShell. Without it, Windows will see a
#     valid signature but from an untrusted publisher; that is still less noisy
#     than no signature at all.

[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Debug',
    [switch]$Run,
    [switch]$TrustCert
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

$project   = Join-Path $scriptRoot 'src\YuNotes\YuNotes.csproj'
$exePath   = Join-Path $scriptRoot ("src\YuNotes\bin\x64\{0}\net8.0-windows10.0.19041.0\YuNotes.exe" -f $Configuration)
$certSubj  = 'CN=YuNotes Dev'

# Only keep the window open when running interactively (e.g. double-clicked or
# launched from a fresh shell). When the script is hosted by build-signed.bat,
# that wrapper already pauses, so skip the prompt here to avoid two pauses.
$KeepWindowOpen = $Host.Name -eq 'ConsoleHost' -and -not $env:PROMPT

try {

# 1. Ensure cert exists
$cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $certSubj -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending | Select-Object -First 1

if (-not $cert) {
    Write-Host "Creating self-signed code-signing cert ($certSubj)..." -ForegroundColor Cyan
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $certSubj `
        -KeyAlgorithm RSA -KeyLength 2048 `
        -KeyUsage DigitalSignature `
        -CertStoreLocation Cert:\CurrentUser\My `
        -NotAfter (Get-Date).AddYears(5)
    Write-Host "  Thumbprint: $($cert.Thumbprint)"
}
else {
    Write-Host "Using existing cert. Thumbprint: $($cert.Thumbprint)" -ForegroundColor DarkGray
}

# 2. Optional one-time trust install (needs admin)
if ($TrustCert) {
    $isAdmin = ([Security.Principal.WindowsPrincipal] `
        [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        throw "-TrustCert requires this PowerShell to be running as Administrator."
    }
    $cerFile = Join-Path $env:TEMP 'YuNotesDev.cer'
    Export-Certificate -Cert $cert -FilePath $cerFile -Force | Out-Null
    Import-Certificate -FilePath $cerFile -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
    Write-Host "Installed cert into LocalMachine\Root. Windows will now trust the signature." -ForegroundColor Green
}

# 3. Build
Write-Host ""
Write-Host "Building $Configuration x64..." -ForegroundColor Cyan
dotnet build $project -c $Configuration -p:Platform=x64 | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }
if (-not (Test-Path $exePath)) { throw "Build succeeded but exe not found at: $exePath" }

# 4. Sign
Write-Host ""
Write-Host "Signing $exePath" -ForegroundColor Cyan
$sigResult = Set-AuthenticodeSignature `
    -FilePath $exePath `
    -Certificate $cert `
    -TimestampServer 'http://timestamp.digicert.com' `
    -HashAlgorithm SHA256
$final = Get-AuthenticodeSignature $exePath
Write-Host ("  Status:     {0}" -f $final.Status)
Write-Host ("  Signer:     {0}" -f $final.SignerCertificate.Subject)
Write-Host ("  Thumbprint: {0}" -f $final.SignerCertificate.Thumbprint)
if ($final.Status -ne 'Valid' -and $final.Status -ne 'UnknownError') {
    throw ("Signing failed: {0} - {1}" -f $sigResult.Status, $sigResult.StatusMessage)
}

# 5. Optionally launch
if ($Run) {
    Write-Host ""
    Write-Host "Launching..." -ForegroundColor Cyan
    Start-Process -FilePath $exePath
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
}
catch {
    Write-Host ""
    Write-Host "BUILD FAILED" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    if ($_.ScriptStackTrace) { Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray }
    if ($KeepWindowOpen) { Read-Host "Press Enter to close" | Out-Null }
    exit 1
}
if ($KeepWindowOpen) { Read-Host "Press Enter to close" | Out-Null }
