# Getting started

This guide walks you through your first `sigil.yaml` manifest, from a fresh
checkout to a validated, packed artifact.

> **Pre-MVP status.** Sigil is not yet on `winget` / `dotnet tool install`.
> Today the only way to run it is to build from source. Public installers
> ship at MVP launch — see [the README](../README.md).

## 1. Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (10.0.100 or newer)
- Git 2.40+
- Windows 10 1809+, Windows 11, macOS 13+, or Ubuntu 22.04+

The Native AOT publish targets Windows x64 / ARM64 only. macOS / Linux
support the JIT build for development; AOT for those platforms is
post-MVP.

## 2. Build the CLI

```bash
git clone https://github.com/Sigil-build/sigil.git
cd sigil
dotnet build src/SigilBuild.Cli
```

A debug `sigil` is now invokable via:

```bash
dotnet run --project src/SigilBuild.Cli -- --help
```

For convenience, the rest of this guide writes `sigil <command>` — substitute
the longer form above, or do an AOT publish:

```bash
dotnet publish src/SigilBuild.Cli -c Release -r win-x64 -p:PublishAot=true \
  -o publish/win-x64
```

The resulting `publish/win-x64/sigil.exe` is **not** single-file: the CLI
project sets no `PublishSingleFile` (`src/SigilBuild.Cli/SigilBuild.Cli.csproj`)
and links against `SigilBuild.Packaging` (SkiaSharp, for logo resizing) and
`SigilBuild.Signing` (NSec.Cryptography, for ZIP manifest signing), so
`publish/win-x64/` also holds `libSkiaSharp.dll` and `libsodium.dll`
(`Directory.Packages.props:28,45-47`). CI enforces `sigil.exe` itself at
**≤ 15 MB** (`.github/workflows/ci.yml:241`); the last locally measured build
was 13.98 MB (`docs/plan/release/02-READINESS_REPORT.md:105`) — not the ~1 MB
this guide used to claim.

## 3. Generate a manifest

`sigil init` scaffolds a `sigil.yaml` for you:

```bash
sigil init \
  --non-interactive \
  --template minimal \
  --app-id com.example.HelloSigil \
  --app-name "Hello Sigil" \
  --version 0.1.0 \
  --publisher "Example, Inc."
```

You'll get a file like:

```yaml
spec: v1.0

app:
  id: com.example.HelloSigil
  name: Hello Sigil
  version: 0.1.0
  publisher: Example, Inc.

build:
  source: ./out
```

Other templates (`--template`):

| Template | Adds |
|---|---|
| `minimal` | Just `spec`, `app`, `build`. |
| `msix-local-sign` | MSIX packaging + local PFX signing block. |
| `azure-signing` | Azure Trusted Signing block + GitHub Releases publish + delta updates. |
| `full-config` | Every v1.0 section, including the branded installer-UI slots. |

Each template is documented end-to-end in [`examples/`](../examples/).

## 4. Validate it

```bash
sigil validate sigil.yaml
```

Output:

```
sigil.yaml is valid (schema v1.0).
```

For machine-readable output (use this in CI):

```bash
sigil validate sigil.yaml --format json
```

The validator runs the full JSON Schema (see
[manifest reference](manifest-reference.md)) plus the typed install-step
deserializer, so semantic errors (unknown step `type`, missing `id`,
unparseable `version`) are caught here too.

## 5. Pack it

> All three package formats — ZIP, MSIX, and EXE-wrapper — are implemented
> and shipping (`SigilBuild.Packaging.Zip.ZipPackager`,
> `.Msix.MsixPackager`, `.ExeWrapper.ExeWrapperPackager`). What is **not**
> built yet is the `publish` stage and delta updates — see
> [ADR-010](architecture/adr-010-delta-update-deferral.md).

```bash
sigil pack sigil.yaml --out ./dist
```

For `package.formats: [zip]`, output lands as a flat file directly under
`--out`: `./dist/<app.id>-<app.version>-<arch>.zip`
(`src/SigilBuild.Packaging/Zip/ZipPackager.cs:24-25`) — not in a
per-build subdirectory.

## 6. Build the branded wizard (EXE-wrapper format)

`sigil pack` with `package.format: exe` produces a single self-extracting
`<App.Name>-<version>-<arch>-Setup.exe` that opens a branded Windows wizard on
double-click (`src/SigilBuild.Packaging/ExeWrapper/ExeWrapperPackager.cs:134`
— e.g. `HelloSigil-0.1.0-x64-Setup.exe`, not the generic `setup.exe` this
guide used to show). The wizard flow is built dynamically from your
`parameters:` block — there's no per-page XAML to write. The Choose Install
Location screen itself is **not** part of that dynamic flow: it is always
rendered, second after Welcome, whether or not you declare any parameters at
all (`InstallerViewModel.cs:1041-1045`) — see
[Installer wizard](guides/installer-wizard.md#screen-flow).

Extend the minimal manifest with the wizard knobs you'll most often touch:

```yaml
installer:
  icon: ./brand/installer.ico   # optional; the bundled default ships otherwise
  brand:
    logo: ./brand/logo.svg
    colors:
      primary: "#1F6FEB"
      accent:  "#7C3AED"

parameters:
  server_url:
    type: string
    install_time: true
    description: "Server URL"
    screen: "Server Settings"     # Groups onto a 'Server Settings' page.

  enable_telemetry:
    type: bool
    install_time: true
    default: false
    description: "Send anonymous usage telemetry"
    screen: "Privacy"             # Renders as a CheckBox on a 'Privacy' page.

install_steps:
  - id: copy-app
    type: file_copy
    from: payload/**
    to: "{install_dir}"           # the resolved destination — do NOT declare
                                   # a parameter named `install_dir` to mean this.

uninstall:
  - id: stop-service
    type: run_program
    program: sc.exe
    args: ["stop", "HelloSigilService"]
    wait: true
    on_failure: continue
```

The wizard flow is now:
**Welcome → Install Location (with disk-space card) → Server Settings → Privacy → Installing → Finish**
(License would insert after Install Location if this manifest declared one).

Per-parameter widget choice is automatic: `type: enum` with a `values:` list renders a ComboBox; `type: enum` with a `source: { url, items_path, value_property, label_property }` block renders a ComboBox populated by an HTTPS fetch at page-attach; `type: bool` renders a CheckBox; everything else renders a TextBox. See the [manifest reference](manifest-reference.md) for every field.

When the manifest declares an `uninstall:` block, the packager produces a sibling `uninstall.exe` inside the Setup.exe and the wrapper drops it to `<install_dir>\uninstall.exe` on install success — plus a Control Panel "Add/Remove Programs" entry pointing at it.

## 7. Next steps

- Browse the [CLI reference](cli-reference.md) for every subcommand and option.
- Browse the [manifest reference](manifest-reference.md) for every key in
  `sigil.yaml`.
- Read the [architecture overview](architecture-overview.md) to understand
  how packing, signing, publishing, and updates fit together.
- Migrating from another installer? See
  [from WiX](migration/from-wix.md) or [from NSIS](migration/from-nsis.md).
