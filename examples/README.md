# Sigil example manifests

Each subdirectory contains a `sigil.yaml` that demonstrates a different slice
of the v1.0 schema. They are validated against `schemas/sigil-schema.json`
in CI (see `tests/SigilBuild.Schema.Tests`).

| Example | What it shows |
|---|---|
| `minimal/` | The smallest possible valid manifest (just `spec`, `app`, `build`). |
| `msix-local-sign/` | MSIX packaging with a local PFX signing cert. |
| `azure-trusted-signing/` | Multi-arch MSIX signed via Azure Trusted Signing, published to GitHub Releases, with delta updates. |
| `full/` | Every section of the v1.0 schema populated, including the installer-UI branding slots from Sprint 5b. |

These manifests reference paths (`./out`, `./assets/logo.png`, etc.) that do
**not** exist — they are schema/parser fixtures, not runnable builds. The first
end-to-end runnable example lands in Sprint 4 (packaging) and Sprint 5b
(installer UI).
