#requires -Version 5.1
<#
.SYNOPSIS
  Trusts the YuNotes dev certificate and installs the signed .msix.

.DESCRIPTION
  Windows only accepts a sideloaded package if the signing certificate is trusted
  on the machine. This imports YuNotes.cer into LocalMachine\TrustedPeople — the
  store Windows checks for app-package signatures — then installs the newest
  .msix from packaging\output.

  Must be run ELEVATED (the LocalMachine cert store requires admin).
  After install, YuNotes appears in the Start menu and in Settings > Apps.
#>
$ErrorActionPreference = "Stop"

# Require elevation for the LocalMachine certificate import.
$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw "Run this script from an elevated PowerShell (Run as administrator)."
}

$cer    = Join-Path $PSScriptRoot "YuNotes.cer"
$outDir = Join-Path $PSScriptRoot "output"

if (-not (Test-Path $cer)) { throw "Missing $cer. Run packaging\build.ps1 first." }
$msix = Get-ChildItem -Path $outDir -Recurse -Filter *.msix -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $msix) { throw "No .msix found under $outDir. Run packaging\build.ps1 first." }

Write-Host "Trusting dev certificate (LocalMachine\TrustedPeople)..." -ForegroundColor Cyan
Import-Certificate -FilePath $cer -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null

Write-Host "Installing $($msix.Name)..." -ForegroundColor Cyan
Add-AppxPackage -Path $msix.FullName

Write-Host "Installed. Launch YuNotes from the Start menu." -ForegroundColor Green
