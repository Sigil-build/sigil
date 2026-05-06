#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Creates a self-signed code-signing certificate, signs an MSIX with it,
    and installs the cert as a trusted root so Add-AppxPackage accepts the package.

.DESCRIPTION
    Run this once per machine before running install-msix.ps1 (without -AllowUnsigned).
    The certificate is stored in the CurrentUser\My store and also copied to
    LocalMachine\Root (trusted root) so Windows accepts packages signed by it.

    The Subject must match the Publisher attribute in AppxManifest.xml exactly.

.PARAMETER Subject
    Certificate subject - must match the 'publisher' field in sigil.yaml exactly.
    Default: "CN=Example Inc., O=Example Inc., C=US"

.PARAMETER MsixPath
    Path to the .msix file to sign. If omitted, only the cert is created/trusted.

.PARAMETER PfxPath
    Where to save the exported PFX.
    Default: tests\SigilBuild.Packaging.IntegrationTests\test-codesign.pfx

.EXAMPLE
    # Create cert + sign the MSIX in one step
    .\setup-test-cert.ps1 -MsixPath dist\msix-smoke\com.example.LocalSignedApp-1.2.3-x64.msix
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Subject  = 'CN=Example Inc., O=Example Inc., C=US',
    [string]$MsixPath = '',
    [string]$PfxPath  = "$PSScriptRoot\test-codesign.pfx"
)

$ErrorActionPreference = 'Stop'

# 1. Create self-signed cert
Write-Host "Creating self-signed certificate: $Subject"
$cert = New-SelfSignedCertificate `
    -Subject $Subject `
    -Type CodeSigningCert `
    -CertStoreLocation Cert:\CurrentUser\My `
    -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddYears(3)

Write-Host "  Thumbprint: $($cert.Thumbprint)"

# 2. Trust it as a root CA (required for Add-AppxPackage)
Write-Host 'Installing cert into LocalMachine\Root (requires elevation)...'
$store = New-Object System.Security.Cryptography.X509Certificates.X509Store('Root', 'LocalMachine')
$store.Open('ReadWrite')
$store.Add($cert)
$store.Close()
Write-Host '  Trusted.'

# 3. Export PFX (for use with sigil sign in Phase 3)
$pfxPassword = ConvertTo-SecureString 'SigilTest1!' -AsPlainText -Force
Export-PfxCertificate -Cert $cert -FilePath $PfxPath -Password $pfxPassword | Out-Null
Write-Host "  PFX exported to: $PfxPath  (password: SigilTest1!)"

# 4. Sign the MSIX (if provided)
if ($MsixPath -and (Test-Path $MsixPath)) {
    # Locate signtool.exe from the Windows SDK
    $sdkBins = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:ProgramFiles\Windows Kits\10\bin"
    )
    $signtool = $null
    foreach ($root in $sdkBins) {
        if (Test-Path $root) {
            $signtool = Get-ChildItem "$root\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
                Sort-Object { [Version]($_.Directory.Parent.Name) } -Descending |
                Select-Object -First 1 -ExpandProperty FullName
            if ($signtool) { break }
        }
    }
    if (-not $signtool) { throw 'signtool.exe not found - install the Windows 10/11 SDK.' }

    Write-Host "Signing $MsixPath with signtool..."
    & $signtool sign /fd SHA256 /a /sha1 $cert.Thumbprint $MsixPath
    if ($LASTEXITCODE -ne 0) { throw "signtool exited $LASTEXITCODE" }
    Write-Host '  Signed.'

    Write-Host ''
    Write-Host 'Done. Now run:'
    Write-Host "  .\install-msix.ps1 -MsixPath '$MsixPath' -ExpectedAppId com.example.LocalSignedApp"
} else {
    Write-Host ''
    Write-Host 'Cert ready. To sign your MSIX, run:'
    Write-Host '  .\setup-test-cert.ps1 -MsixPath path\to\your.msix'
    Write-Host 'Or use signtool directly:'
    Write-Host "  signtool sign /fd SHA256 /sha1 $($cert.Thumbprint) path\to\your.msix"
}
