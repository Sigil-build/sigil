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

```powershell
$env:SIGIL_PFX_PASSWORD = "..."
sigil pack sigil.yaml
sigil sign sigil.yaml --artifact ./dist/MyApp-1.0.0-x64-Setup.exe
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
|`<App.Name>-<version>-<arch>-Setup.exe` (EXE wrapper)|yes|Authenticode via the configured provider. Sign it **after** packing — see [Running it](#running-it).|
|`<App.Name>-<version>-<arch>-WebSetup.exe` (only with `--payload web`)|yes|A **second** signable artefact: the tiny stub that downloads the full package. Sign it too, with the same `sigil sign --artifact` invocation pointed at it. An unsigned stub is the file your users actually download and run first.|
|`uninstall.exe`|inherited|Not separately signed and not embedded anywhere. It is a byte-for-byte runtime copy of the already-signed `Setup.exe`, made at install time, so it carries that same signature.|
|MSIX bundles|yes|Package signature via the provider.|
|ZIP artefacts|no|No widely-understood embedded signature for ZIP; archives are unsigned in the MVP.|

Artefact names are not configurable: the exe uses the sanitized **`app.name`**, while `zip` and `msix` use **`app.id`**. See [Packaging formats](packaging-formats.md#architectures).

## Timestamping

RFC 3161 timestamping is enabled by default for both providers and lands a counter-signature on every Authenticode signature. For the local provider, override the TSA via `local.timestampUrl`. Sigil retries with a short fallback chain before failing the sign step.

## Running it

**`sigil pack` does not sign.** Packing and signing are two commands, and the order is fixed: pack stamps the payload, brand and blob into the installer through the Win32 resource-update APIs, and **any resource edit invalidates a prior Authenticode signature**. So signing must be last.

```bash
sigil pack sigil.yaml
sigil sign sigil.yaml --artifact ./dist/MyApp-1.0.0-x64-Setup.exe
```

`--artifact <file>` is **required** — `sigil sign sigil.yaml` on its own fails immediately. The manifest supplies the provider and its configuration; `--artifact` says which file to sign. Run it once per artefact: each architecture's `Setup.exe`, and the `WebSetup.exe` stub as well when you packed with `--payload web`.

The installer's own verified "Signed by {publisher}" trust line only appears when the finished `Setup.exe` verifies at install time — which is another way of saying: sign the file you ship, after it is finished.

## See also

- [Manifest reference - sign](../manifest-reference.md#sign)
- [Packaging formats](packaging-formats.md)
