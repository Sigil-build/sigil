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

The resulting `publish/win-x64/sigil.exe` is a single-file ~1 MB binary.

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

> **Sprint-4-onwards.** `sigil pack` is wired to the CLI today and validates
> manifests, but the packaging back-end is incomplete — only the ZIP path is
> functional in the current alpha. The MSIX path lands in Sprint 4. Treat the
> command below as a smoke test for now.

```bash
sigil pack sigil.yaml --out ./dist
```

Output goes under `./dist/<app-id>-<version>/`.

## 6. Build the branded wizard (EXE-wrapper format)

`sigil pack` with `package.format: exe` produces a single self-extracting
`setup.exe` that opens a branded Windows wizard on double-click. The wizard
flow is built dynamically from your `parameters:` block — there's no per-page
XAML to write.

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
  install_dir:
    type: path
    install_time: true
    default: "C:\\Program Files\\Hello Sigil"
    description: "Install location"
    # No screen: field — install_dir always renders on the dedicated
    # Install Location page (with disk-space readout).

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

uninstall:
  - id: stop-service
    type: run_program
    program: sc.exe
    args: ["stop", "HelloSigilService"]
    wait: true
    on_failure: continue
```

The wizard flow is now:
**Welcome → License → Install Location (with disk-space card) → Server Settings → Privacy → Installing → Finish**.

Per-parameter widget choice is automatic: `type: enum` with a `values:` list renders a ComboBox; `type: enum` with a `source: { url, items_path, value_property, label_property }` block renders a ComboBox populated by an HTTPS fetch at page-attach; `type: bool` renders a CheckBox; everything else renders a TextBox. See the [manifest reference](manifest-reference.md) for every field.

When the manifest declares an `uninstall:` block, the packager produces a sibling `uninstaller.exe` inside `setup.exe` and the wrapper drops it to `<install_dir>\uninstaller.exe` on install success — plus a Control Panel "Add/Remove Programs" entry pointing at it.

## 7. Next steps

- Browse the [CLI reference](cli-reference.md) for every subcommand and option.
- Browse the [manifest reference](manifest-reference.md) for every key in
  `sigil.yaml`.
- Read the [architecture overview](architecture-overview.md) to understand
  how packing, signing, publishing, and updates fit together.
- Migrating from another installer? See
  [from WiX](migration/from-wix.md) or [from NSIS](migration/from-nsis.md).
