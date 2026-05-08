# Cloud signing integration tests

These run against real Azure Trusted Signing and a real PFX. They are **not**
part of `dotnet test` in CI — they require credentials.

## Local PFX

Prereqs: Windows + Windows SDK + a self-signed code-signing PFX at `./certs/codesign.pfx`.

```powershell
$env:SIGIL_PFX_PASSWORD="password"
dotnet run --project src/SigilBuild.Cli -- sign examples/msix-local-sign/sigil.yaml `
  --artifact dist/com.example.LocalSignedApp-1.2.3-x64.msix
```

Expected: the MSIX is signed in place; `signtool verify /pa` shows a valid chain.

## Azure Trusted Signing

Prereqs: Trusted Signing account, certificate profile, and a Service Principal
with `Trusted Signing Certificate Profile Signer` role.

```bash
export AZURE_TENANT_ID=...
export AZURE_CLIENT_ID=...
export AZURE_CLIENT_SECRET=...
dotnet run --project src/SigilBuild.Cli -- sign examples/azure-trusted-signing/sigil.yaml \
  --artifact dist/com.example.AzureSignedApp-2.0.0-x64.msix
```

Expected: a `.sig` is produced (MSIX embedding lands in delta-updates plan task 6).
