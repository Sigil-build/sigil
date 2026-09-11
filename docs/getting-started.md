# Getting started

This guide walks you through your first `sigil.yaml` manifest, from a fresh
checkout to a validated, packed artifact.

> **Pre-MVP status.** Sigil is not yet on `winget` / `dotnet tool install`.
> Today the only way to run it is to build from source. Public installers
> ship at MVP launch — see [the README](../README.md).

## 1. Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), **exactly `10.0.303`**. `global.json` pins it with `"rollForward": "disable"` and `"allowPrerelease": false`, so any other 10.0.x SDK fails outright — and under a `--locked-mode` restore it fails with NU1004, because the lock files pin the SDK-injected ILCompiler / ILLink packages too.
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
(`Directory.Packages.props:28` for NSec, `:51-53` for the SkiaSharp pins). CI
enforces `sigil.exe` itself at **≤ 15 MB** (`.github/workflows/ci.yml:379`, and
again per-architecture in `release.yml`) — a few MB, not the ~1 MB this guide
used to claim.

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
| `azure-signing` | `app`, `build`, `package` (msix, x64 + arm64) and an Azure Trusted Signing `sign` block. No `publish:` and no `updates:`. |
| `full-config` | `app`, `build`, `package`, `sign`, `publish`, `updates` and `installer.brand`. |

None of the templates is a tour of the whole schema: **no** template ships
`parameters:`, `install_steps:`, `pre_install:`/`post_install:`, `uninstall:`,
or the rest of the `installer:` block (`options`, `screens`, `vars`, `hooks`,
`prerequisites`, `app_mutex`, `scope`, `license`, `require_signed_downloads`).
For those, start from a guide or from [`examples/`](../examples/) — the two
manifests under `examples/exe-wrapper/` are the ones that produce a real
installer.

## 4. Validate it

```bash
sigil validate sigil.yaml
```

Output:

```text
OK: sigil.yaml
```

An invalid manifest prints its `SIG0xxx` diagnostics to **stderr** (one per
line, with a `file:line:col` prefix) and exits **1**, so `sigil validate` works
as a CI gate as-is.

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

There is no `--format` flag: **the output format is chosen in the manifest**,
via `package.formats:` (an array, default `["zip"]`). `sigil pack`'s only
options are `--out`, `--payload` and `--package-url`.

The `exe` format is produced **only on a Windows pack host** — stamping the
payload into the installer runtime uses `BeginUpdateResourceW`, which has no
cross-platform equivalent. On Linux or macOS `sigil pack` emits **SIG0270** and
skips exe; the other requested formats still pack, but the run exits non-zero
so the unmet request is not silent.

For `package.formats: [zip]`, output lands as a flat file directly under
`--out`: `./dist/<app.id>-<app.version>-<arch>.zip`
(`src/SigilBuild.Packaging/Zip/ZipPackager.cs:24-25`) — not in a
per-build subdirectory.

## 6. Build the branded wizard (EXE-wrapper format)

`sigil pack` with `package.formats: [exe]` produces a single self-extracting
`<App.Name>-<version>-<arch>-Setup.exe` that opens a branded Windows wizard on
double-click — e.g. `HelloSigil-0.1.0-x64-Setup.exe`, not the generic
`setup.exe` this guide used to show. The wizard's custom pages are built from
your `installer.screens[]` list — there's no per-page XAML to write. The Choose
Install Location screen is **not** one of those: it is always rendered, second
after Welcome, whether or not you declare any parameters at all — see
[Installer wizard](guides/installer-wizard.md#screen-flow).

Extend the minimal manifest with the wizard knobs you'll most often touch:

```yaml
installer:
  icon: ./brand/installer.ico   # optional; the bundled default ships otherwise
  brand:
    logo: ./brand/logo.svg
    primaryColor: "#1F6FEB"
    accentColor:  "#7C3AED"
  screens:                        # custom wizard pages come from here, and only here
    - id: server
      title: "Server Settings"
      fields: [server_url]
    - id: privacy
      title: "Privacy"
      fields: [enable_telemetry]

parameters:
  server_url:
    type: string
    install_time: true
    description: "Server URL"

  enable_telemetry:
    type: bool
    install_time: true
    default: false
    description: "Send anonymous usage telemetry"

install_steps:
  - id: copy-app
    type: file_copy
    from: payload://**            # the `payload://` scheme is what rebases onto the
                                   # extracted payload; a bare `payload/**` resolves
                                   # against the working directory and fails.
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
(License would insert after Install Location if this manifest declared
`installer.license`, and the built-in Options page after that if it declared
`installer.options`).

Note the two parameters carry no `screen:` field. A parameter-level `screen:` is
accepted by the schema but produces no wizard page — pages come from
`installer.screens[]`, and a parameter that no screen names is never shown. See
[Installer wizard](guides/installer-wizard.md#custom-screens-installerscreens).

Per-parameter widget choice is automatic: an `enum` with four or fewer `values:`
renders a radio group and a larger one a dropdown; an `enum` with a
`source: { url, items_path, value_property, label_property }` block renders a
dropdown populated by an HTTPS fetch at page-attach; `bool` renders a checkbox;
`path` a path input, `secret` a masked input, `int` a number input, `string` a
text input. A screen field's `{ param, widget }` form overrides the default. See
[Installer wizard](guides/installer-wizard.md#per-parameter-widget-selection).

**On every successful `exe` install** — with or without an `uninstall:` block —
the wrapper copies its own running image to `<install_dir>\uninstall.exe` and
writes a Control Panel "Add/Remove Programs" entry pointing at it. It is a
runtime copy, not something the packager builds and embeds, and there is no way
to suppress either. The `uninstall:` block is for tear-down the rollback journal
cannot infer; it is not what causes an uninstaller to exist. See
[Uninstaller](guides/uninstaller.md).

## 7. Next steps

- Browse the [CLI reference](cli-reference.md) for every subcommand and option.
- Browse the [manifest reference](manifest-reference.md) for every key in
  `sigil.yaml`.
- Read the [architecture overview](architecture-overview.md) to understand
  how packing, signing, publishing, and updates fit together.
- Migrating from another installer? See
  [from Inno Setup](migration/from-inno.md).
