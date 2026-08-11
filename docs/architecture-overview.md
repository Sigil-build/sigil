# Architecture overview

A short, public-friendly tour of how Sigil is put together. For deep
references, see the [CLI reference](cli-reference.md) and the
[manifest reference](manifest-reference.md).

## High-level concept

Sigil is a CLI framework that orchestrates four stages of desktop-software
distribution: **pack → sign → publish → update**. All four stages are wired
together by a single declarative YAML manifest.

```
┌──────────────┐
│  sigil.yaml  │ ← single source of truth
└───────┬──────┘
        │
   ┌────▼────┐    ┌──────────┐    ┌───────────┐    ┌───────────┐
   │  sigil  │ →  │  sigil   │ →  │   sigil   │ →  │   sigil   │
   │  pack   │    │   sign   │    │  publish  │    │  update   │
   └─────────┘    └──────────┘    └───────────┘    └───────────┘
                                                        │
                                                        ▼
                                              Update SDK on user device
```

Pack today ships working ZIP, MSIX, and **EXE-wrapper** paths. The
EXE-wrapper produces a single self-extracting `setup.exe` with a branded
Windows wizard, NSIS-style screen-grouped parameters, a dedicated install
location screen with disk-space readout, and an auto-generated
`uninstall.exe` plus Add/Remove Programs entry. Signing and the
full-package update engine (see [Updates](guides/updates.md)) both ship
today too; `publish` and delta updates are the remaining pieces of the MVP
timeline.

## Locked-in technical decisions

These are the load-bearing choices the codebase is built around. None of them
will change without a superseding architecture decision record:

- **Language: .NET 10 LTS, Native AOT.** Cold-start under 200 ms, `sigil.exe`
  itself under 15 MB (CI-gated) — but not single-file: `sigil.exe` ships
  beside `libSkiaSharp.dll` and `libsodium.dll`, the native halves of
  `SigilBuild.Packaging` and `SigilBuild.Signing`. Reflection-heavy patterns
  are forbidden — source generators are used instead (YamlDotNet,
  System.Text.Json, hand-rolled where needed).
- **Manifest format: YAML.** Strict-mode parsing, exhaustive JSON Schema
  validation, plus a typed install-step deserializer that catches semantic
  errors the schema can't express.
- **Schema validator: hand-rolled.** Off-the-shelf .NET schema validators
  rely on reflection paths that conflict with AOT trim warnings, so Sigil
  ships its own draft-07-compatible validator tuned for the manifest's
  shape.
- **Updates: signed full-package first, delta deferred.** `/Update` fetches
  a channel manifest signed with ECDSA P-256 (BCL-only, no native crypto
  dependency — see [ADR-009](architecture/adr-009-update-manifest-signature.md))
  and, when a newer version is available, downloads and runs the complete
  new package. Delta patches (zstd dictionary mode, trained against an
  app's prior release) are intentionally deferred — see
  [ADR-010](architecture/adr-010-delta-update-deferral.md).
- **Two-surface UX: CLI for developers, branded Windows wizard for end
  users.** The CLI is the primary product; the wizard is a thin host that
  consumes the same manifest.
- **Open Core, two-tier honor system.** The CLI in this repo is MIT-licensed.
  A separate, closed-source SaaS half handles cloud signing orchestration,
  team accounts, and signing history; it is **not** co-located with the OSS
  components and has no required runtime dependency on the CLI.

## Component layout

All nine shipping assemblies under `src/` (`Sigil.slnx`):

```
src/
├── SigilBuild.Cli/                        # Console entry point — the `sigil` binary (validate, init, pack, sign).
├── SigilBuild.Core/                       # Manifest parsing, schema validation, diagnostics, versioning.
├── SigilBuild.Packaging/                  # Zip/, Msix/, ExeWrapper/ (builds the installer blob), Installer/ backends.
├── SigilBuild.Signing/                    # Authenticode: Local/ (signtool), Azure/ (Trusted Signing), audit log.
├── SigilBuild.Wrapper.Core/               # Shared install engine — Engine/ (InstallEngine, RollbackJournal,
│                                           # StepFactory), Steps/ (the step catalog), Expressions/ (when-clauses).
├── SigilBuild.Wrapper/                    # Console-only wrapper host (the `/silent` entry point).
├── SigilBuild.Installer.Host/             # Avalonia wizard UI (Views/Screens, ViewModels), engine-driven.
├── SigilBuild.Installer.BrandGenerator/   # Derives a light+dark palette from two manifest colors at pack time.
└── SigilBuild.Localization.Generator/     # netstandard2.0 source generator for the wizard's string catalog.
```

The wizard's install engine moved out of `SigilBuild.Wrapper` and into the
shared `SigilBuild.Wrapper.Core` during the T1–T18 installer track — both
`SigilBuild.Wrapper` (headless `/S`) and `SigilBuild.Installer.Host` (the
GUI) now drive the same `InstallEngine`. The packagers, signers, and
publishers live behind small interfaces in `SigilBuild.Core` so a
third-party signing provider or a different package format is a focused
implementation, not a fork.

## Tech stack at a glance

| Layer | Choice |
|---|---|
| Language | C# 14 / .NET 10 LTS, Native AOT |
| YAML | YamlDotNet (with source generators for AOT) |
| JSON Schema | Hand-rolled draft-07 validator |
| Compression | ZstdSharp.Port — pure-managed C# zstd port, "nothing to bundle" (`Directory.Packages.props:38-43`) |
| Crypto — ZIP manifest signing | NSec.Cryptography / Ed25519 (`SigilBuild.Signing/Local/ZipManifestSigner.cs`) |
| Crypto — update-manifest signing | BCL `ECDsa`, P-256 — no native crypto dependency ([ADR-009](architecture/adr-009-update-manifest-signature.md)) |
| HTTP | HttpClient + Polly |
| MSIX | `MakeAppx.exe` + a custom `AppxManifest` builder |
| CI | GitHub Actions |

Two different signature schemes for two different jobs, on purpose — ADR-009
records the update-manifest rationale.

## Runtime targets

Of the rows below, CI actually enforces **one** size number — `sigil.exe` ≤
15 MB, `.github/workflows/ci.yml:241` — plus the project-wide test-coverage
floor. The rest are **targets**, not gates:

| Metric | Target | CI-enforced? |
|---|---|---|
| `sigil --version` cold-start (Native AOT, win-x64) | ≤ 200 ms | No |
| `sigil.exe` (AOT-published, Release, stripped) | ≤ 15 MB | **Yes** — `ci.yml:241` |
| `sigil pack` for a 100 MB source tree | ≤ 5 s | No |
| `sigil sign` round-trip via Azure Trusted Signing | ≤ 8 s p50, ≤ 20 s p99 | No |
| Delta patch generation, 100 MB → 100 MB build | ≤ 30 s | No — metric for a deferred feature, see [ADR-010](architecture/adr-010-delta-update-deferral.md) |
| Test coverage, project-wide union | ≥ 65 % (aspirational: Core ≥ 80 %, Signing/SDK ≥ 85 %) | **Yes** — `ci.yml`'s Python coverage gate |

A red `main` build older than four hours is "stop the world" — the project's
quality model assumes `main` is always shippable.

## Where to go next

- Build and run the CLI: [getting started](getting-started.md).
- Every subcommand: [CLI reference](cli-reference.md).
- Every key in `sigil.yaml`: [manifest reference](manifest-reference.md).
- Already on WiX or NSIS? [migration guides](migration/).
