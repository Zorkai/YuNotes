#requires -Version 5.1
<#
.SYNOPSIS
  Uninstalls the sideloaded YuNotes package for the current user.

.DESCRIPTION
  Equivalent to right-clicking YuNotes in the Start menu > Uninstall, or removing
  it from Settings > Apps. Does not require elevation.
#>
$ErrorActionPreference = "Stop"

$pkg = Get-AppxPackage -Name "YuNotes.YuNotes"
if ($pkg) {
    Write-Host "Removing $($pkg.PackageFullName)..." -ForegroundColor Cyan
    Remove-AppxPackage -Package $pkg.PackageFullName
    Write-Host "Uninstalled." -ForegroundColor Green
} else {
    Write-Host "YuNotes is not installed for the current user."
}
