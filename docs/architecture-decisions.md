# Architecture Decision Records

Architecture Decision Records (ADRs) for Sigil live in the [product-architect-package](https://github.com/Sigil-build/product-architect-package) Obsidian vault under `architecture/adr-NNN-*.md`. This file is a stub pointer kept inside the source repo for traceability with the code it describes.

## Sprint 2-3 (declarative config)

- **ADR-007 — Schema Validator Implementation** (`product-architect-package/architecture/adr-007-schema-validator-implementation.md`)
  - **Status:** Accepted, 2026-04-30
  - **Decision:** Replace NJsonSchema with a hand-rolled validator in `src/SigilBuild.Core/Configuration/SchemaValidator.cs`.
  - **Why:** NJsonSchema 11.x and its transitive deps (Newtonsoft.Json, Namotion.Reflection) emit `IL2104`/`IL3053` warnings under Native AOT, which violates ADR-001 / ADR-002 with `TreatWarningsAsErrors=true`. Custom validator is ~250 LOC, AOT-clean, enforces `const`, and threads YAML source positions into schema diagnostics.
  - **Supersedes:** WBS row 1.2 ("(NJsonSchema)").

## Pre-MVP ADRs (Sprint 1 & earlier)

- ADR-001 — language choice (.NET 10 Native AOT)
- ADR-002 — AOT vs JIT trade-offs
- ADR-003 — config format (YAML over TOML/HCL)
- ADR-004 — delta algorithm (zstd dictionary mode)
- ADR-005 — monetization model (Open Core)
- ADR-006 — installer UI surface (CLI + branded wizard)

See the product-architect-package vault for full text.
