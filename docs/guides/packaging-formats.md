# Packaging formats

`package.formats:` controls what artefacts Sigil produces. The MVP supports three formats; you can request any subset.

```yaml
package:
  formats: [exe]
  architectures: [x64, arm64]
```

Each `format x architecture` pair produces one artefact - e.g. `formats: [msix, exe]` x `architectures: [x64, arm64]` yields four files.

## Formats

### `msix`

Windows-canonical app package. Sandboxed, Microsoft Store-distributable, supports differential install via App Installer.

- No driver install, no service install, no arbitrary registry writes outside the package container.
- No `parameters:` or `install_steps:` - MSIX is declarative-only at install time, the package model has no scripting surface to plug into.
- Optional WACK (Windows App Certification Kit) via `package.msix.runWack: true`.

Best for: sandbox-friendly apps you also want in the Store.

### `zip`

Deterministic archive of your `build.source` directory. No installer chrome, no Windows-only assumptions, cross-platform-readable.

- No signing (the package itself can't carry a signature meaningfully).
- No install logic - the user extracts and runs.

Best for: portable apps, CI artefacts, per-user "extract anywhere" distributions.

### `exe`

Self-extracting `setup.exe` with Sigil's branded wizard host (D-013 / D-014). Full install-step engine, declarative parameters, auto-generated `uninstaller.exe`, ARP integration.

- All MUST-tier step types (`file_copy`, `registry_*`, `shortcut_create`, `env_set`, `run_program`, `service_install`, ...).
- `parameters:` block surfaces in the wizard or via `/Name=Value` on silent install.
- Signing: `setup.exe` and the embedded `uninstaller.exe` are both signed.

Best for: "real" Windows installers. The intended NSIS / WiX replacement.

## Decision matrix

|Use case|`exe`|`msix`|`zip`|
|---|---|---|---|
|Install needs to write `HKLM` or `HKCU`|x|-|-|
|Install needs to register a Windows service|x|-|-|
|Install needs Start Menu / Desktop shortcuts|x|x|-|
|Branded installer wizard|x|-|-|
|Silent install (`/S` + parameter overrides)|x|x*|-|
|Microsoft Store distribution|-|x|-|
|Sandboxed runtime|-|x|-|
|Portable / no-install distribution|-|-|x|

*MSIX installs are silent by default via App Installer; there is no Sigil-controlled silent surface.

## Architectures

`package.architectures:` accepts `x64`, `arm64`, or both (default `[x64]`). Each combination of format x architecture produces one artefact, e.g.:

```yaml
package:
  formats:       [exe, zip]
  architectures: [x64, arm64]
```

yields `setup-x64.exe`, `setup-arm64.exe`, `app-x64.zip`, `app-arm64.zip`.

## Migrating from WiX or NSIS

If you're replacing WiX, you want `exe`. If you're replacing NSIS, you want `exe`. The migration guides cover the command-by-command mapping:

- [Migrating from WiX](../migration/from-wix.md)
- [Migrating from NSIS](../migration/from-nsis.md)

## See also

- [Manifest reference - package](../manifest-reference.md#package)
- [Installer wizard](installer-wizard.md)
- [Signing](signing.md)
