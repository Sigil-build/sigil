# Signing

`sign:` selects a code-signing provider. The MVP ships two: `local` (PFX file on disk) and `azure-trusted-signing` (cloud). Choose `none` (the default) to skip signing.

```yaml
sign:
  provider: local
```

## Local PFX

For indie devs holding an EV or OV PFX, possibly on a build machine, where the file is on disk:

```yaml
sign:
  provider: local
  local:
    pfx: ./certs/codesign.pfx
    passwordEnv: SIGIL_PFX_PASSWORD
    timestampUrl: http://timestamp.digicert.com
```

|Field|Required|Default|Notes|
|---|---|---|---|
|`pfx`|yes|-|Path to the `.pfx` file (relative to the manifest).|
|`passwordEnv`|-|-|Environment variable holding the PFX password. Read at sign time.|
|`timestampUrl`|-|`http://timestamp.digicert.com`|RFC 3161 timestamp authority.|

Drop the password into the environment, not the manifest:

```bash
$env:SIGIL_PFX_PASSWORD = "..."
sigil pack sigil.yaml
```

## Azure Trusted Signing

The cloud-native path. No USB token, no PFX file on disk, CI/CD-friendly (D-008):

```yaml
sign:
  provider: azure-trusted-signing
  azureTrustedSigning:
    endpoint:           https://eus.codesigning.azure.net/
    accountName:        my-signing-account
    certificateProfile: my-profile
```

|Field|Required|Default|Notes|
|---|---|---|---|
|`endpoint`|yes|-|Region-specific Trusted Signing endpoint.|
|`accountName`|yes|-|Trusted Signing account name in your Azure subscription.|
|`certificateProfile`|yes|-|Certificate profile inside the account.|
|`tenantIdEnv`|-|`AZURE_TENANT_ID`|Env var holding the Azure tenant ID.|
|`clientIdEnv`|-|`AZURE_CLIENT_ID`|Env var holding the service-principal client ID.|
|`clientSecretEnv`|-|`AZURE_CLIENT_SECRET`|Env var holding the client secret.|

Auth uses the standard Azure SDK environment-variable conventions. In GitHub Actions or Azure DevOps, populate the three vars from a service-principal secret store.

## What gets signed

|Artefact|Signed?|How|
|---|---|---|
|`setup.exe` (EXE wrapper)|yes|Authenticode via the configured provider.|
|`uninstaller.exe` (embedded inside `setup.exe`)|yes|Same provider, signed before being embedded.|
|MSIX bundles|yes|Package signature via the provider.|
|ZIP artefacts|no|No widely-understood embedded signature for ZIP; archives are unsigned in the MVP.|

## Timestamping

RFC 3161 timestamping is enabled by default for both providers and lands a counter-signature on every Authenticode signature. For the local provider, override the TSA via `local.timestampUrl`. Sigil retries with a short fallback chain before failing the sign step.

## Running it

`sigil pack` invokes the signer automatically when a `sign:` block is present. To sign without packing (e.g. to re-sign an existing artefact):

```bash
sigil sign sigil.yaml
```

## See also

- [Manifest reference - sign](../manifest-reference.md#sign)
- [Packaging formats](packaging-formats.md)
- ADR-005 (monetization) - cloud signing as the wedge between OSS and Business tiers.
