---
name: schema-change
description: Safely modify sigil.yaml's JSON schema (schemas/sigil-schema.json) and keep docs, examples, fixtures, and the typed manifest graph in lockstep. Use for any manifest format change, new manifest field, or schema edit.
---

# Change the sigil.yaml schema

`schemas/sigil-schema.json` is the public contract of the whole tool. CI fails
PRs that change it without touching its lockstep surfaces.

## Rules of the file

- Draft 2020-12, `additionalProperties: false` at every object level **except**
  the step shape, which is intentionally loose — per-step validation lives in
  `src/SigilBuild.Core/Configuration/ManifestParser.cs`, not the schema.
- Repeated enums (notably the step-`type` list) appear in **multiple places**.
  Grep for one known member (e.g. `"file_copy"`) and update every occurrence.
- User-visible strings use the `LocalizedText` definition (plain string or
  `{en: …, uk: …}` map; `en` required — SIG0290).
- The schema ships inside the binary: it's an `EmbeddedResource` in
  `SigilBuild.Core` (`Configuration/EmbeddedSchemas.cs`). Rebuild = updated.

## Lockstep checklist (do all of these)

1. `schemas/sigil-schema.json` — the change itself
2. Typed graph — matching record(s) under `src/SigilBuild.Core/Manifest/` and
   parsing in `Configuration/ManifestParser.cs`; new validation failures get
   `SIG0xxx` constants in `Diagnostics/DiagnosticCodes.cs` (stay in the band:
   SIG02xx parameters, SIG023x steps, SIG0290+ localization)
3. `docs/manifest-reference.md` — field tables
4. `examples/**/sigil.yaml` — CI runs `sigil validate` on every example; new
   required fields break ALL examples, so default-or-optional is strongly preferred
5. `tests/SigilBuild.Schema.Tests/Fixtures/` — positive + negative fixture
6. If the field reaches the installer at runtime: the wrapper blob
   (`SigilBuild.Wrapper.Core/Json/Serializable*.cs` + `WrapperBlobJsonContext`)
   and the pack-time writer in `SigilBuild.Packaging/ExeWrapper/`

## Compatibility

Pre-MVP, so breaking changes are allowed but must be deliberate: call the break
out in the PR description and update the migration docs
(`docs/migration/from-nsis.md`, `from-wix.md`) if they show affected syntax.
Never rename a shipped field silently.

## Verify

```bash
dotnet build Sigil.slnx -c Release
dotnet test Sigil.slnx -c Release --filter FullyQualifiedName~Schema
dotnet test Sigil.slnx -c Release   # then the full suite
```
