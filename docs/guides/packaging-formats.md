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

A self-extracting `<App.Name>-<version>-<arch>-Setup.exe` with Sigil's branded wizard host. Full install-step engine, declarative parameters, automatic `uninstall.exe`, ARP integration.

> **Producing the `exe` format requires a Windows pack host.** Stamping the payload into the installer runtime uses the Win32 resource-update APIs (`BeginUpdateResourceW`), which have no cross-platform equivalent. On Linux or macOS `sigil pack` emits **SIG0270** and **skips** the exe format; the other requested formats still pack, but the run exits non-zero to flag the unmet request. (Do not confuse this with **SIG0120**, "EXE-wrapper packaging requires the AOT-published `SigilBuild.Wrapper` runtime" — a different failure, on Windows, when the staged installer runtime is missing.)

- The full closed catalog of 18 install-step types — see [Install steps](install-steps.md).
- `parameters:` surfaces in the wizard or via `/PName=Value` on silent install (`/S /D=<dir> /PName=Value`; see [setup.exe reference](../setup-exe-reference.md)).
- Signing: `sigil sign --artifact` signs the finished `Setup.exe` after packing. `uninstall.exe` is a runtime copy of it and inherits the signature. See [Signing](signing.md).
- **Payload delivery is a packaging choice too.** The default, `sigil pack --payload embedded`, puts the whole payload inside `Setup.exe`. `sigil pack --payload web --package-url <https URL>` instead produces a **second, tiny artefact** — `<App.Name>-<version>-<arch>-WebSetup.exe` — that downloads the full package from that URL at install time, verifies its SHA-256, and runs it. Both artefacts need signing. See [Updates](updates.md).

Best for: full Windows installers with a wizard, install steps, and an uninstaller.

## Decision matrix

|Use case|`exe`|`msix`|`zip`|
|---|---|---|---|
|Install needs to write `HKLM` or `HKCU`|x|-|-|
|Install needs to register a Windows service|x|-|-|
|Install needs Start Menu / Desktop shortcuts|x|x|-|
|Branded installer wizard|x|-|-|
|Silent install (`/S` + parameter overrides)|x|n/a*|-|
|Microsoft Store distribution|-|x|-|
|Sandboxed runtime|-|x|-|
|Portable / no-install distribution|-|-|x|

*MSIX installs are silent by default via App Installer — that is Windows doing it, not Sigil. There is no Sigil-controlled silent surface for `msix`, and no `parameters:` or `install_steps:` to override, so this row is "not applicable" rather than "supported".

## Architectures

`package.architectures:` accepts `x64`, `arm64`, or both (default `[x64]`). Each combination of format x architecture produces one artefact, e.g.:

```yaml
package:
  formats:       [exe, zip]
  architectures: [x64, arm64]
```

yields four files. Artefact names are fixed by the packager, not configurable:

|Format|File name|
|---|---|
|`exe`|`<sanitized app.name>-<version>-<arch>-Setup.exe` (and `…-WebSetup.exe` with `--payload web`)|
|`zip`|`<app.id>-<version>-<arch>.zip`|
|`msix`|`<app.id>-<version>-<arch>.msix`|

Note the asymmetry: the **exe** name is built from `app.name` (sanitized against path traversal and illegal filename characters), while **zip** and **msix** use `app.id`. So an app with `name: My App` and `id: com.example.myapp` produces `My App-1.0.0-x64-Setup.exe` alongside `com.example.myapp-1.0.0-x64.zip`.

## Migrating from another installer

Coming from another installer toolchain, `exe` is the format you want:

- [Migrating from Inno Setup](../migration/from-inno.md)

## See also

- [Manifest reference - package](../manifest-reference.md#package)
- [Installer wizard](installer-wizard.md)
- [Signing](signing.md)
