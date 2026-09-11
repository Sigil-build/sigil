# Sigil

> Open-source CLI framework for declarative desktop-software distribution.
> Pack → Sign → Publish → Update — driven by a single `sigil.yaml`.

[![CI](https://github.com/Sigil-build/sigil/actions/workflows/ci.yml/badge.svg)](https://github.com/Sigil-build/sigil/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

> **Status:** Pre-MVP. Not yet ready for production use.

## Why Sigil?

Sigil is a manifest-first tool for shipping Windows software: a readable
`sigil.yaml` you can review in a pull request, headless code signing that
fits CI, and a signed full-package update engine. The whole pipeline lives
in version control next to the app it ships.

> **Delta updates are not shipped.** Earlier planning material described
> zstd dictionary-mode delta patches and a client Update SDK; both are
> explicitly deferred (`docs/architecture/adr-010-delta-update-deferral.md`),
> and no `SigilBuild.UpdateSdk` project exists in `src/` today. `/Update`
> always fetches and runs the complete new-version package.

> **Breaking change for update publishers.** The channel manifest `/Update`
> fetches now **requires** three more fields — `issuedAt`, `expiresAt` and
> `sequence` — inside the signed byte range, and the client enforces a validity
> window, a 30-day maximum age and a monotonic anti-replay high-water mark
> (ADR-011). A manifest built to the older five-field shape is rejected as
> malformed by every shipped installer. See
> [Updates](docs/guides/updates.md#the-channel-manifest-contract).

## What you get

`sigil.yaml` with `package: { formats: [exe] }` → `sigil pack` → a branded,
self-elevating Windows wizard (`<App.Name>-<version>-<arch>-Setup.exe`) — or
`zip` / `msix` if you don't need the wizard. (The output format is chosen in
the manifest; `sigil pack` has no `--format` flag.) The `exe` path ships:

- a branded install wizard with wizard chrome themed from two manifest
  colors, driven entirely by `sigil.yaml`'s `installer:` / `parameters:`
  blocks (see [Installer wizard](docs/guides/installer-wizard.md));
- a closed catalog of install steps (`file_copy`, registry, shortcuts,
  services, scheduled tasks, COM registration, firewall rules, and more —
  see [Install steps](docs/guides/install-steps.md)), each one journaled for
  automatic rollback on a failed or cancelled install;
- an auto-generated `uninstall.exe` and Add/Remove Programs entry, with
  anchored journal replay so an untrusted uninstall state cannot be used to
  drive an elevated process anywhere it shouldn't go (see
  [Uninstaller](docs/guides/uninstaller.md));
- silent install/uninstall/update via a documented flag set — see the
  [setup.exe reference](docs/setup-exe-reference.md);
- a signed, full-package update engine (`/Update`) — see
  [Updates](docs/guides/updates.md).

**Status:** pre-MVP. The `publish` stage (hosting + release-channel
distribution) is not built yet.

## Install

There is no published package yet — NuGet, `winget`, and an install script are
all pre-MVP roadmap items, not available today. Build from source:

```bash
git clone https://github.com/Sigil-build/sigil.git
cd sigil
dotnet build Sigil.slnx -c Release
dotnet test Sigil.slnx -c Release
```

See [Getting started](docs/getting-started.md) for a full walkthrough
(`init` → `validate` → `pack`). A signed GitHub Release with prebuilt binaries
will replace this section on the first tagged release. The release workflow
itself already ships: `release.yml` triggers on a `v*` tag, runs the full VM
matrix, AOT-publishes win-x64 (win-arm64 best-effort), signs with Azure Trusted Signing,
emits a CycloneDX SBOM and `SHA256SUMS`, and publishes a prerelease. What is
missing is a pushed tag, not the automation.

## Credits

- Install Icon by Saki (Alexandre Moore) on <a href="https://icon-icons.com/authors/32-saki-alexandre-moore">Icon-Icons.com</a>

## License

MIT — see [LICENSE](LICENSE).
