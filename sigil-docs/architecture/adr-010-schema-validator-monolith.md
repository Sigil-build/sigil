# ADR-010: Schema Validator as a Single-Class Monolith

**Status:** Accepted (2026-05-12) — backfills a decision already shipped in PR #2.

## Context

Plan _2 (Schema Validation) specified three separate source files inside `SigilBuild.Core/Configuration/`:

- `YamlToJsonConverter.cs` — converts a YAML `YamlNode` tree to a `JsonDocument` while preserving source positions.
- `JsonSchemaDocument.cs` — wraps the parsed schema JSON and provides keyword-level access.
- `SchemaValidator.cs` — orchestrates validation using the two helpers above.

The shipped implementation merges all three into a single `SchemaValidator.cs` file (~326 lines), with `YamlToJsonConverter` and `JsonSchemaDocument` as private nested types or private methods.

## Decision

Keep the merged single-file implementation. Do not split into three files.

## Rationale

**Internal surface exposure.** `YamlToJsonConverter` is non-trivial: it carries per-node source-position metadata (line and column from the YamlDotNet parser) that feeds into schema error messages. Exposing it as a separate `internal` class would require either making it `public` (leaking it into the API) or granting `InternalsVisibleTo` to additional test projects. Neither option adds value when `SchemaValidator` is the only consumer.

**Single responsibility at the component level.** The three notional files would not have separate unit tests — they would be tested only through `SchemaValidator`. Splitting them would create file-level separation without meaningful test-level isolation. The cohesion of the entire validation pipeline (YAML parse → JSON conversion → schema walk → error collection) is higher than the cohesion of any one step in isolation.

**Acceptable file size.** At ~326 lines, `SchemaValidator.cs` is well within the project's informal complexity budget. A large single-responsibility file is preferable to multiple files with artificially narrow scopes.

## Consequences

- `SchemaValidator.cs` is the single entry point and single implementation locus for manifest validation. Reading it end-to-end gives a complete picture of the validation pipeline.
- Source-position tracking in error messages (`line:N col:N`) is an implementation detail of the converter; it does not leak through the public API.

## When to Split

If a second consumer outside `SigilBuild.Core` ever needs YAML-to-JSON conversion with position metadata (for example, a future `sigil lint` formatter), extract `YamlToJsonConverter` into a standalone `internal` class at that point. Do not pre-split in anticipation of a hypothetical consumer.
