# MSIX install integration test

Cannot run on the GitHub-hosted Windows runner because `Add-AppxPackage` requires
either a trusted signing chain or Developer Mode, neither of which is the
default. Run this on a Windows 11 machine or VM.

## Path A — Unsigned install (quickest, for local dev)

Requires: **Developer Mode** on (`Settings → System → For developers → Developer Mode`).

```powershell
# Build the MSIX
dotnet run --project src/SigilBuild.Cli -- pack examples/msix-local-sign/sigil.yaml --out dist

# Install without signing (-AllowUnsigned requires Developer Mode)
.\tests\SigilBuild.Packaging.IntegrationTests\install-msix.ps1 `
  -MsixPath dist\com.example.LocalSignedApp-1.2.3-x64.msix `
  -ExpectedAppId com.example.LocalSignedApp `
  -AllowUnsigned
```

## Path B — Self-signed cert (closer to production, one-time setup)

Requires: **Elevated PowerShell** (Run as Administrator) + Windows 10/11 SDK.

```powershell
# One-time: create a test cert, trust it as root, sign the MSIX
.\tests\SigilBuild.Packaging.IntegrationTests\setup-test-cert.ps1 `
  -MsixPath dist\com.example.LocalSignedApp-1.2.3-x64.msix

# Install (cert is now trusted, no -AllowUnsigned needed)
.\tests\SigilBuild.Packaging.IntegrationTests\install-msix.ps1 `
  -MsixPath dist\com.example.LocalSignedApp-1.2.3-x64.msix `
  -ExpectedAppId com.example.LocalSignedApp
```

The `setup-test-cert.ps1` script:
- Creates `CN=Example Inc., O=Example Inc., C=US` as a self-signed code-signing cert
- Installs it in `Cert:\LocalMachine\Root` (trusted root)
- Signs the `.msix` with `signtool.exe` from the Windows SDK
- Exports a PFX to `tests/SigilBuild.Packaging.IntegrationTests/test-codesign.pfx` (password: `SigilTest1!`)

> **Note:** The cert Subject must match the `publisher` field in `sigil.yaml` exactly.
> For the example: `CN=Example Inc., O=Example Inc., C=US`.

## ARM64 variant

Same steps — use `com.example.LocalSignedApp-1.2.3-arm64.msix` instead.
Ensure you run on an ARM64 machine (Snapdragon X / Surface Pro X).

## Why not in CI?

Sprint-6 acceptance gate (WBS 2.10): "MSIX installs in a Windows 11 VM".
We satisfy that manually for the MVP launch. A self-hosted runner is post-MVP (M9–M12).
