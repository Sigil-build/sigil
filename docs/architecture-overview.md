# Architecture overview

A short, public-friendly tour of how Sigil is put together. For deep
references, see the [CLI reference](cli-reference.md) and the
[manifest reference](manifest-reference.md).

## High-level concept

Sigil is a CLI framework that orchestrates four stages of desktop-software
distribution: **pack → sign → publish → update**. All four stages are wired
together by a single declarative YAML manifest.

```text
┌──────────────┐
│  sigil.yaml  │ ← single source of truth
└───────┬──────┘
        │
   ┌────▼────┐    ┌──────────┐    ┌───────────┐
   │  sigil  │ →  │  sigil   │ →  │  publish  │
   │  pack   │    │   sign   │    │(not built)│
   └─────────┘    └──────────┘    └───────────┘
        │
        │ produces
        ▼
   ┌───────────────────────────────┐
   │  <App>-<ver>-<arch>-Setup.exe │
   │    …/Update  ← run by the     │
   │      installed app itself     │
   └───────────────────────────────┘
```

**The CLI has exactly four commands** — `validate`, `init`, `pack`, `sign`.
There is no `sigil update` subcommand and no `SigilBuild.UpdateSdk` project:
updating is `Setup.exe /Update`, a mode of the installer you already shipped,
run by the installed application (or a scheduled task). See
[Updates](guides/updates.md).

Pack today ships working ZIP, MSIX, and **EXE-wrapper** paths. The
EXE-wrapper produces a single self-extracting
`<App.Name>-<version>-<arch>-Setup.exe` (and, with `--payload web`, a tiny
`…-WebSetup.exe` stub beside it) carrying a branded Windows wizard, custom
parameter pages declared as `installer.screens[]`, a dedicated install-location
screen with disk-space readout, and an automatic `uninstall.exe` plus
Add/Remove Programs entry. Signing and the full-package update engine both ship
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
  System.Text.Json, hand-rolled where needed). `PublishAot` is set
  **per project**, not in `Directory.Build.props`: `SigilBuild.Cli` and
  `SigilBuild.Wrapper` always, and `SigilBuild.Installer.Host` behind the
  opt-in `SigilAotPublish` property. Those three are the binaries that ship;
  the trim/AOT *analyzer*, however, runs on every Release build.
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
- **Update trust is time-bounded, not just signed**
  ([ADR-011](architecture/adr-011-update-manifest-freshness.md)). A signature
  says *who* minted a document, never *when*, so the channel manifest carries
  three **required** fields inside the signed byte range — `issuedAt`,
  `expiresAt` and `sequence` — and the client enforces a ±5-minute skew
  tolerance, a 30-day maximum age independent of `expiresAt`, and a persisted
  monotonic `sequence` high-water mark against replay. Separately,
  `installer.require_signed_downloads` makes the Authenticode policy for a
  downloaded binary **declared** rather than inferred from whether the
  publisher configured signing for their own output. All shipped; see
  [Updates](guides/updates.md).
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
| Compression | ZstdSharp.Port — pure-managed C# zstd port, "nothing to bundle" (`Directory.Packages.props:44`) |
| Crypto — ZIP manifest signing | NSec.Cryptography / Ed25519 (`SigilBuild.Signing/Local/ZipManifestSigner.cs`) |
| Crypto — update-manifest signing | BCL `ECDsa`, P-256 — no native crypto dependency ([ADR-009](architecture/adr-009-update-manifest-signature.md)) |
| HTTP | HttpClient + Polly |
| MSIX | `MakeAppx.exe` + a custom `AppxManifest` builder |
| CI | GitHub Actions |

Two different signature schemes for two different jobs, on purpose — ADR-009
records the update-manifest rationale.

## Runtime targets

CI enforces **two** size gates and **five** coverage floors. The remaining rows
are **targets**, not gates:

| Metric | Target | CI-enforced? |
|---|---|---|
| `sigil --version` cold-start (Native AOT, win-x64) | ≤ 200 ms | No |
| `sigil.exe` (AOT-published, Release, stripped) | ≤ 15 MB | **Yes** — `ci.yml:379`, and again per-architecture at `release.yml:95` (win-x64) and `release.yml:107` (win-arm64) |
| Installer-host full footprint (win-x64) | ≤ 45 MB | **Yes** — `scripts/publish-installer-runtime.ps1:190-192`, invoked with `-SizeGateMb 45` from `ci.yml:170`, `ci.yml:441`, `release.yml:126` and `wrapper-vm-tests.yml:133, 230`. The gate a contributor is most likely to trip. |
| `sigil pack` for a 100 MB source tree | ≤ 5 s | No |
| `sigil sign` round-trip via Azure Trusted Signing | ≤ 8 s p50, ≤ 20 s p99 | No |
| Delta patch generation, 100 MB → 100 MB build | ≤ 30 s | No — metric for a deferred feature, see [ADR-010](architecture/adr-010-delta-update-deferral.md) |
| Test coverage, project-wide union | ≥ **77 %** | **Yes** — `ci.yml:232` |
| Test coverage, per assembly | `SigilBuild.Core` ≥ 69 %, `SigilBuild.Signing` ≥ 68 %, `SigilBuild.Wrapper.Core` ≥ 79 %, `SigilBuild.Packaging` ≥ 72 % | **Yes** — `ci.yml:235-238` |

The coverage floors are a **ratchet**: each is the current measured value
rounded down, re-pinned upward as coverage rises, never lowered. `ci.yml` also
carries a `THRESHOLD = 0.65` constant, which is dead — its own comment says
`PROJECT_WIDE_FLOOR` is the gate. Aspirational targets (Core ≥ 80 %) are not
gates, and there is no SDK project to hold to one.

## Where to go next

- Build and run the CLI: [getting started](getting-started.md).
- Every subcommand: [CLI reference](cli-reference.md).
- Every key in `sigil.yaml`: [manifest reference](manifest-reference.md).
- The decisions themselves: [architecture decision records](architecture/).
- Already on another installer? [migration guides](migration/).
