# Sigil documentation

User-facing docs for the `sigil` CLI and its manifest format. For contributor
docs (build, test, branch policy), see [`../CONTRIBUTING.md`](../CONTRIBUTING.md).

## Start here

- [**Getting started**](getting-started.md) — install Sigil and run your first
  `init` → `validate` → `pack` cycle in under five minutes.

## Reference

- [**CLI reference**](cli-reference.md) — every `sigil` subcommand, its options,
  and an example. *Auto-generated from the live command tree.*
- [**Manifest reference**](manifest-reference.md) — every key in `sigil.yaml`,
  with type, default, and description. *Auto-generated from
  [`schemas/sigil-schema.json`](../schemas/sigil-schema.json).*

## Concepts

- [**Architecture overview**](architecture-overview.md) — what Sigil does,
  how the pack → sign → publish → update pipeline fits together, and the
  short list of locked-in technical choices.

## Migrating from another tool

- [From WiX](migration/from-wix.md)
- [From NSIS](migration/from-nsis.md)

## A note on the auto-generated files

`cli-reference.md`, `manifest-reference.md`, and (when wired) `api/` are
regenerated from the code, schema, and XML doc comments. Do not edit them by
hand — your changes will be overwritten on the next CI run. To update them,
edit the source of truth (the `Description` strings on CLI commands, the
`description` fields in the JSON schema, or the `///` comments on public
types) and re-run `scripts/docs/generate-*.ps1`.
