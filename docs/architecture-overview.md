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

Pack today ships a working ZIP path; MSIX, signing, publishing, and the
delta-update SDK arrive across the MVP timeline.

## Locked-in technical decisions

These are the load-bearing choices the codebase is built around. None of them
will change without a superseding architecture decision record:

- **Language: .NET 10 LTS, Native AOT.** Cold-start under 200 ms, single-file
  binary under 15 MB. Reflection-heavy patterns are forbidden — source
  generators are used instead (YamlDotNet, System.Text.Json, hand-rolled
  where needed).
- **Manifest format: YAML.** Strict-mode parsing, exhaustive JSON Schema
  validation, plus a typed install-step deserializer that catches semantic
  errors the schema can't express.
- **Schema validator: hand-rolled.** Off-the-shelf .NET schema validators
  rely on reflection paths that conflict with AOT trim warnings, so Sigil
  ships its own draft-07-compatible validator tuned for the manifest's
  shape.
- **Delta updates: zstd dictionary mode.** Trained against an app's prior
  release as the dictionary, then signed with Ed25519. The client SDK
  verifies the signature before applying the patch.
- **Two-surface UX: CLI for developers, branded Windows wizard for end
  users.** The CLI is the primary product; the wizard is a thin host that
  consumes the same manifest.
- **Open Core, two-tier honor system.** The CLI in this repo is MIT-licensed.
  A separate, closed-source SaaS half handles cloud signing orchestration,
  team accounts, and signing history; it is **not** co-located with the OSS
  components and has no required runtime dependency on the CLI.

## Component layout

```
src/
├── SigilBuild.Cli/                  # Console entry point — the `sigil` binary.
├── SigilBuild.Core/                 # Manifest parsing, schema validation, diagnostics, versioning.
├── SigilBuild.Packaging/            # MSIX + ZIP packagers, deterministic by design.
├── SigilBuild.Wrapper/              # The branded Windows installer wizard's runtime engine.
├── SigilBuild.Installer.Host/       # AOT-published wizard host that the wrapper drives.
└── SigilBuild.Installer.BrandGenerator/   # Compile-time branding asset pipeline.
```

The packagers, signers, and publishers live behind small interfaces in
`SigilBuild.Core` so a third-party signing provider or a different package
format is a focused implementation, not a fork.

## Tech stack at a glance

| Layer | Choice |
|---|---|
| Language | C# 14 / .NET 10 LTS, Native AOT |
| YAML | YamlDotNet (with source generators for AOT) |
| JSON Schema | Hand-rolled draft-07 validator |
| Compression | ZstdNet + native fallback (zstd 1.5+ dictionary mode) |
| Crypto (Ed25519) | NSec.Cryptography |
| HTTP | HttpClient + Polly |
| MSIX | `MakeAppx.exe` + a custom `AppxManifest` builder |
| CI | GitHub Actions |

## Runtime targets

These numbers are quality bars enforced by CI, not aspirations:

| Metric | Target |
|---|---|
| `sigil --version` cold-start (Native AOT, win-x64) | ≤ 200 ms |
| `sigil.exe` (AOT-published, Release, stripped) | ≤ 15 MB |
| `sigil pack` for a 100 MB source tree | ≤ 5 s |
| `sigil sign` round-trip via Azure Trusted Signing | ≤ 8 s p50, ≤ 20 s p99 |
| Delta patch generation, 100 MB → 100 MB build | ≤ 30 s |
| Test coverage in `SigilBuild.Core` | ≥ 80 % line coverage |
| Test coverage in signing + update SDK | ≥ 85 % line coverage |

A red `main` build older than four hours is "stop the world" — the project's
quality model assumes `main` is always shippable.

## Where to go next

- Build and run the CLI: [getting started](getting-started.md).
- Every subcommand: [CLI reference](cli-reference.md).
- Every key in `sigil.yaml`: [manifest reference](manifest-reference.md).
- Already on WiX or NSIS? [migration guides](migration/).
