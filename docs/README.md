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
- [**setup.exe reference**](setup-exe-reference.md) — every runtime flag the
  produced installer/uninstaller accepts (`/S`, `/D=`, `/PName=Value`, exit
  codes, …). *Hand-written — see the page for why it cannot be generated.*

## Guides

How-to guides for each shipped feature. Start with the wizard guide if
you're building a Windows installer; start with packaging formats if you're
not sure which output format you need.

- [Installer wizard](guides/installer-wizard.md) — branded wizard host, brand slots, custom screens (`installer.screens[]`), install icon.
- [Parameters](guides/parameters.md) — install-time parameters, types, dynamic dropdowns.
- [Install steps](guides/install-steps.md) — every step type with a worked example.
- [Uninstaller](guides/uninstaller.md) — auto-generated `uninstall.exe` + Add/Remove Programs entry.
- [Upgrades & downgrades](guides/upgrades.md) — version-aware install, `/force-downgrade`, install-dir preservation.
- [Updates](guides/updates.md) — the `updates:` block, signed channel manifests, `/Update` exit codes, and the web installer (`--payload web`).
- [Prerequisites](guides/prerequisites.md) — detect-then-install dependency units (VC++ redist, .NET runtime).
- [Packaging formats](guides/packaging-formats.md) — MSIX vs ZIP vs EXE-wrapper.
- [Signing](guides/signing.md) — local PFX or Azure Trusted Signing.
- [Conditional installs](guides/conditional-installs.md) — `when:` expressions and rollback.
- [Localization](guides/localization.md) — `installer.language`, `/lang`, the `LocalizedText` shape, and known limitations.

## Concepts

- [**Architecture overview**](architecture-overview.md) — what Sigil does,
  how the pack → sign → publish → update pipeline fits together, and the
  short list of locked-in technical choices.

### Architecture decision records

The ADRs in [`architecture/`](architecture/) record why each load-bearing
choice was made. Several are user-facing contracts, not just internal
rationale:

- [ADR-008 — expression policy and the install-step catalog](architecture/adr-008-expression-policy.md): the closed `when:` grammar, the closed function table, and how a new step type is admitted.
- [ADR-009 — update-manifest signature](architecture/adr-009-update-manifest-signature.md): BCL ECDSA P-256, SPKI public key, IEEE-P1363 signature encoding.
- [ADR-010 — delta-update deferral](architecture/adr-010-delta-update-deferral.md): why `deltaTargets` parses but does nothing.
- [ADR-011 — update-manifest freshness](architecture/adr-011-update-manifest-freshness.md): **the required `issuedAt` / `expiresAt` / `sequence` channel-manifest fields**, the validity window, and `installer.require_signed_downloads`. Read this before you publish an update channel.
- [ADR-012 — COM-registration isolation](architecture/adr-012-com-registration-isolation.md): what `com_register` does to the installer process, and the risk it carries.
- [ADR-013 — brand tokens: runtime JSON vs source generation](architecture/adr-013-brand-token-runtime-json-vs-source-gen.md)
- [ADR-014 — the schema-validator monolith](architecture/adr-014-schema-validator-monolith.md)
- [ADR — Avalonia under Native AOT](architecture/adr-avalonia-aot.md)
- [ADR — the MSIX companion question](architecture/adr-msix-companion.md)

## Migrating from another tool

- [From Inno Setup](migration/from-inno.md)

## A note on the auto-generated files

`cli-reference.md`, `manifest-reference.md`, and (when wired) `api/` are
regenerated from the code, schema, and XML doc comments. Do not edit them by
hand — your changes will be overwritten on the next CI run. To update them,
edit the source of truth (the `Description` strings on CLI commands, the
`description` fields in the JSON schema, or the `///` comments on public
types) and re-run `scripts/docs/generate-*.ps1`.

This is **enforced**, not merely asked: the `docs` workflow regenerates both
pages on every PR that touches `src/`, `schemas/` or `docs/`, and fails if
`git status --porcelain docs/` is non-empty afterwards. A hand edit to either
page therefore fails CI even when it is correct.
