[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$MsixPath,
    [Parameter(Mandatory)] [string]$ExpectedAppId,
    # Use when the package is unsigned (requires Windows 11 + Developer Mode).
    # For signed packages, omit this flag and run setup-test-cert.ps1 first.
    [switch]$AllowUnsigned
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $MsixPath)) { throw "MSIX not found: $MsixPath" }

Write-Host "Installing $MsixPath ..."
if ($AllowUnsigned) {
    Add-AppxPackage -Path $MsixPath -ForceApplicationShutdown -AllowUnsigned
} else {
    # Signed package path: the signing cert must already be trusted.
    # Run setup-test-cert.ps1 once before calling this script without -AllowUnsigned.
    Add-AppxPackage -Path $MsixPath -ForceApplicationShutdown
}

$installed = Get-AppxPackage -Name $ExpectedAppId
if (-not $installed) { throw "Package '$ExpectedAppId' is not installed after Add-AppxPackage" }

Write-Host "Installed: $($installed.Name) $($installed.Version)"

# Tear down
Remove-AppxPackage -Package $installed.PackageFullName
Write-Host "Removed."
