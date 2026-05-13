# RFC-001: Technical Requirements — Sprint 1 Baseline

**Status:** Accepted (2026-04-30). Backfilled retrospectively 2026-05-12.

## Context

Sigil's MVP is deliberately narrow: Windows-only desktop-software distribution (MSIX + .zip), x64 and ARM64, local PFX signing plus Azure Trusted Signing. The target audience is .NET developers who want a modern, CI/CD-native replacement for WiX and NSIS. Three hard non-negotiables shaped every technology decision made before Sprint 1:

1. The CLI must ship as a single self-contained binary (`sigil.exe`) — no runtime installation required on a developer machine.
2. Cold-start must stay under 200 ms to feel instant in a terminal.
3. The binary must stay under 15 MB so it is trivially distributable through `winget`, `dotnet tool`, and direct download.

These constraints ruled out JIT-compiled deployment and made Native AOT mandatory from the outset.

## Key Decisions

### Language and runtime — C# / .NET 10 LTS Native AOT

ADR-001 and ADR-002 (and D-003) lock in .NET 10 LTS with Native AOT publication. The primary drivers were ecosystem fit for the target audience (most Sigil users are already .NET developers), the richness of `Microsoft.Extensions.*`, and the fact that Native AOT can meet the cold-start and binary-size targets without sacrificing developer productivity. Rust and Go were evaluated but rejected: Go's ecosystem alignment with Windows packaging is weak, and Rust would have required re-implementing YAML parsing, MSIX tooling integration, and NuGet publishing from scratch.

Native AOT prohibits reflection-heavy patterns. The project mandates source generators throughout: `System.Text.Json` source-gen contexts for all serialization, YamlDotNet AOT-compatible source generators for manifest parsing, and hand-rolled schema validation (see below) instead of the originally-planned NJsonSchema (D-012).

### Configuration format — YAML via YamlDotNet

ADR-003 chose YAML over TOML and HCL. YAML is already the de-facto format for CI/CD pipelines (GitHub Actions, Azure DevOps) that Sigil integrates with, reducing context-switching for the developer. YamlDotNet was selected over alternatives because it supports AOT-compatible source generators starting in 16.x.

The `sigil.yaml` manifest is validated against `schemas/sigil-schema.json` via a hand-rolled validator in `SigilBuild.Core` rather than NJsonSchema. This was a plan deviation documented in D-012 and ADR-007: NJsonSchema 11.x introduces `IL3053`/`IL2104` AOT warnings that cannot be suppressed without violating the AOT mandate. The hand-rolled validator supports all keywords used by the schema (`type`, `required`, `additionalProperties`, `properties`, `pattern`, `minLength/maxLength`, `minimum/maximum`, `enum`, `const`, `items/minItems/uniqueItems`, `format=uri`, `if/then`, `allOf`) and additionally provides YAML source-position tracking (`line:N col:N`) in error messages.

### Business model — Open Core, 2-tier honor system

ADR-005, D-002, and D-008 establish the commercial model. The CLI, packaging, local signing, Update SDK, and zstd delta engine are MIT-licensed and free for everyone including companies under $3M ARR. A Business tier ($499/month) targets companies with $3M–$50M ARR and is enforced via honor-system self-attestation. An Enterprise tier exists for regulated industries and companies above $50M ARR. This radically simple model was chosen over a 5-tier structure because it maximizes bottom-up adoption without requiring a sales motion.

### Two-surface UX — CLI for developers, branded Windows wizard for end-users

ADR-006 and D-011 separate two distinct UI surfaces. The Sigil-CLI itself is and will remain CLI-only. The installer wizard that end-users see when installing a customer's product is a separate runtime host (Avalonia 11, shipped inside the MSIX package). This wizard is in MVP scope for Windows, with macOS and Linux using OS-default installer chrome until post-MVP.

## Non-Functional Targets

| Metric | Target | Enforcement |
|---|---|---|
| Cold-start (AOT, win-x64) | ≤ 200 ms | CI benchmark gate |
| Single-file binary size | ≤ 15 MB | CI artifact size check |
| Core test coverage | ≥ 80 % | Coverlet + CI quality gate |
| Signing & Update SDK coverage | ≥ 85 % | Coverlet + CI quality gate |
| CI green on `main` | Always | Break > 4 h is stop-the-world |

## Sprint 1 Baseline Measurement

A baseline AOT publish measurement was planned for Sprint 1. Run:

```
dotnet publish src/SigilBuild.Cli -c Release -r win-x64 -p:PublishAot=true -o publish/baseline-rfc001
```

The CI pipeline enforces the 15 MB cap as an artifact size gate; the exact baseline measurement is recorded in CI run logs for Sprint 1. If the CI gate passes, the binary is compliant.
