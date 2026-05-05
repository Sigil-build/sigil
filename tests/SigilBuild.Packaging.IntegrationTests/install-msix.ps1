[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$MsixPath,
    [Parameter(Mandatory)] [string]$ExpectedAppId
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $MsixPath)) { throw "MSIX not found: $MsixPath" }

# A self-signed test cert must be trusted on the VM; this script assumes that step
# is already done (Windows blocks Add-AppxPackage of unsigned packages).
Write-Host "Installing $MsixPath ..."
Add-AppxPackage -Path $MsixPath -ForceApplicationShutdown

$installed = Get-AppxPackage -Name $ExpectedAppId
if (-not $installed) { throw "Package '$ExpectedAppId' is not installed after Add-AppxPackage" }

Write-Host "Installed: $($installed.Name) $($installed.Version)"

# Tear down
Remove-AppxPackage -Package $installed.PackageFullName
Write-Host "Removed."
