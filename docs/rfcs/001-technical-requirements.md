---
rfc: 001
title: Technical Requirements (Sigil MVP)
status: Accepted
author: Tech Lead
created: 2026-04-30
supersedes: —
related: [decisions/D-003, architecture/adr-001-language-choice, architecture/adr-002-aot-vs-jit]
---

# RFC-001: Technical Requirements

## Summary

This RFC freezes the **measurable** technical requirements that every Sigil MVP
component must satisfy. It is the regression baseline for Sprints 2-12. Any
component that violates one of these numbers without an accompanying superseding
RFC blocks the merge.

## Non-functional requirements

### Performance

| Metric | Target | Measured at |
|---|---|---|
| `sigil --version` cold-start (Native AOT, win-x64) | ≤ 200 ms wall-clock on a 2020-era laptop | Sprint 1 baseline; CI perf job from Sprint 4 |
| `sigil pack` for a 100 MB source tree (ZIP, deterministic) | ≤ 5 s on the same hardware | Sprint 4 |
| `sigil sign` round-trip via Azure Trusted Signing | ≤ 8 s p50, ≤ 20 s p99 | Sprint 7 |
| Delta patch generation, 100 MB → 100 MB build | ≤ 30 s | Sprint 8 |

### Binary size

| Artifact | Target |
|---|---|
| `sigil.exe` (Native AOT, win-x64, Release, stripped) | ≤ 15 MB |
| `SigilBuild.UpdateSdk.dll` (NuGet, framework-dependent) | ≤ 500 KB |

The Sprint 1 baseline measurement of `sigil.exe` size goes here once Task 4 has
been run on a Windows host: `<fill in MB once measured>`.

### Reliability & quality gates

- Unit-test coverage: ≥ 80 % line coverage in `SigilBuild.Core`, ≥ 85 % in
  `SigilBuild.Signing` and `SigilBuild.UpdateSdk`. Enforced from Sprint 4
  onward via Coverlet thresholds.
- `main` must stay green. A red build older than 4 hours is "stop the world"
  per `implementation/README.md`.
- All public types and methods have XML doc comments.
- `TreatWarningsAsErrors=true` solution-wide. AOT/trim/single-file analyzers
  enabled — IL2026 / IL3050 are errors.

### Compatibility

- Target framework: `net10.0`. Bumps require a new RFC.
- OSes: Windows 10 1809+, Windows 11, macOS 13+, Ubuntu 22.04+. ARM64 in
  scope for Windows; macOS / Linux ARM64 are post-MVP.
- No reflection-heavy patterns. Use System.Text.Json source generators,
  YamlDotNet source generators, and hand-rolled where necessary.

### Security

- No secrets in the repo. Enforced via `gitleaks` pre-commit hook
  (Task 9) and the `secret-scan` GitHub Actions job (Task 8).
- All NuGet dependencies pinned in `Directory.Packages.props`. Renovate /
  Dependabot (post-MVP) opens upgrade PRs; humans review them.
- TLS-only HTTP. `ServicePointManager` defaults stay; never disable cert
  validation.

## Functional requirements (MVP scope)

Locked by `product-architect-package/decisions.md#D-004`:

- **Platforms:** Windows (MSIX + .zip), x64 + ARM64.
- **Signing:** Local PFX + Azure Trusted Signing.
- **Updates:** zstd dictionary delta patches; Ed25519-signed update manifest;
  client SDK on NuGet.
- **Installer UI:** Branded Windows wizard, 6 screens, Free tier (per D-011).
- **Distribution channels at launch:** WinGet, NuGet (`dotnet tool`), PowerShell
  installer, shell installer, GitHub Releases (per D-010).

Out of scope: macOS / Linux installers, AWS KMS, SignPath, Distribution-as-Code
auto-generation, custom actions, multi-package bundles.

## Acceptance

This RFC is accepted when:

- [ ] Sprint 1 CI is green on all three OS runners
- [ ] AOT publish on Windows produces a `sigil.exe` under 15 MB
- [ ] All four example manifests validate against `schemas/sigil-schema.json`
- [ ] `dotnet test` passes with 0 failures
- [ ] Branch protection on `Sigil-build/sigil:main` requires the CI checks listed in `docs/sprint-01/identifier-reservation.md`
