#requires -Version 5.1
<#
.SYNOPSIS
  Uninstalls the sideloaded YuNotes package for the current user.

.DESCRIPTION
  Equivalent to right-clicking YuNotes in the Start menu > Uninstall, or removing
  it from Settings > Apps. Does not require elevation.
#>
$ErrorActionPreference = "Stop"

# Match any installed YuNotes package by name so this keeps working across
# identity changes (the pre-Store placeholder "YuNotes.YuNotes" and the current
# Store identity "YunusOztrk.YuNotes" both contain "YuNotes"). Hard-coding a
# single Name broke uninstall when the manifest identity changed.
$pkgs = Get-AppxPackage | Where-Object { $_.Name -like "*YuNotes*" }

if ($pkgs) {
    foreach ($pkg in $pkgs) {
        Write-Host "Removing $($pkg.PackageFullName)..." -ForegroundColor Cyan
        Remove-AppxPackage -Package $pkg.PackageFullName
    }
    Write-Host "Uninstalled." -ForegroundColor Green
} else {
    Write-Host "YuNotes is not installed for the current user."
}
