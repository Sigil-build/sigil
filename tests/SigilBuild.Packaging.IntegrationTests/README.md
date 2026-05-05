# MSIX install integration test

Cannot run on the GitHub-hosted Windows runner because `Add-AppxPackage` requires
either a trusted signing chain or Developer Mode, neither of which is the
default. Run this on a Windows 11 VM (or self-hosted runner) with developer
mode enabled.

## Prereqs

- Developer mode on (`Settings → Privacy → For developers`)
- A test code-signing cert installed in `Cert:\LocalMachine\Root` (run `tests/setup-test-cert.ps1` once)

## Run

```powershell
dotnet run --project src/SigilBuild.Cli -- pack examples/msix-local-sign/sigil.yaml --out dist
./tests/SigilBuild.Packaging.IntegrationTests/install-msix.ps1 `
  -MsixPath dist\com.example.LocalSignedApp-1.2.3-x64.msix `
  -ExpectedAppId com.example.LocalSignedApp
```

## Why not in CI?

Sprint-6 acceptance gate (WBS 2.10) says "MSIX installs in a Windows 11 VM".
We satisfy that manually for the MVP launch. A self-hosted runner that runs this
nightly is post-MVP (M9–M12).
