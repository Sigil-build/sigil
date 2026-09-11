# Sigil example manifests

Each subdirectory contains a `sigil.yaml` that demonstrates a different slice
of the v1.0 schema. They are validated against `schemas/sigil-schema.json`
in CI (see `tests/SigilBuild.Schema.Tests`).

| Example | What it shows |
|---|---|
| `minimal/` | The smallest possible valid manifest (just `spec`, `app`, `build`). |
| `msix-local-sign/` | MSIX packaging with a local PFX signing cert. |
| `azure-trusted-signing/` | Multi-arch MSIX signed via Azure Trusted Signing, published to GitHub Releases, with a signed full-package update channel. |
| `full/` | Every section of the v1.0 schema populated, including the installer-UI branding slots. |
| `exe-wrapper/hello-desktop-app/` | **Runnable.** The smallest complete EXE-wrapper installer: a payload, an install-time parameter, and install steps. |
| `exe-wrapper/multi-edition/` | **Runnable.** `when:`-gated steps driven by an `edition` enum parameter. |

> The `azure-trusted-signing/` manifest sets `updates.deltaTargets: 3`. That
> field is parsed and schema-validated but **functionally inert** — delta
> updates are deferred ([ADR-010](../docs/architecture/adr-010-delta-update-deferral.md)).
> What the example actually demonstrates is a signed **full-package** update
> channel.

The four manifests in the first group reference paths (`./out`,
`./assets/logo.png`, etc.) that do **not** exist — they are schema/parser
fixtures, not runnable builds. The two under `exe-wrapper/` are the runnable
ones: each ships its own `payload/` directory and produces a real `Setup.exe`
on a Windows pack host.
