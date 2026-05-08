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

## 6. Next steps

- Browse the [CLI reference](cli-reference.md) for every subcommand and option.
- Browse the [manifest reference](manifest-reference.md) for every key in
  `sigil.yaml`.
- Read the [architecture overview](architecture-overview.md) to understand
  how packing, signing, publishing, and updates fit together.
- Migrating from another installer? See
  [from WiX](migration/from-wix.md) or [from NSIS](migration/from-nsis.md).
