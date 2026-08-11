# Sigil

> Open-source CLI framework for declarative desktop-software distribution.
> Pack → Sign → Publish → Update — driven by a single `sigil.yaml`.

[![CI](https://github.com/Sigil-build/sigil/actions/workflows/ci.yml/badge.svg)](https://github.com/Sigil-build/sigil/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

> **Status:** Pre-MVP. Not yet ready for production use.

## Why Sigil?

Modern desktop installer tooling forces a choice between expensive GUI suites
(InstallShield, Advanced Installer — \$400-\$4,500 / year) and free-but-painful
XML / Pascal scripting (WiX, NSIS, Inno Setup). Sigil sits in the middle:
declarative YAML, headless cloud code signing, and a signed full-package
update engine.

> **Delta updates are not shipped.** Earlier planning material described
> zstd dictionary-mode delta patches and a client Update SDK; both are
> explicitly deferred (`docs/architecture/adr-010-delta-update-deferral.md`),
> and no `SigilBuild.UpdateSdk` project exists in `src/` today. `/Update`
> always fetches and runs the complete new-version package.

## What you get

`sigil.yaml` → `sigil pack --format exe` → a branded, self-elevating Windows
wizard (`<App.Name>-<version>-<arch>-Setup.exe`) — or `zip` / `msix` if you
don't need the wizard. The `exe` path ships:

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

There is no published package yet — `SigilBuild` / `SigilBuild.UpdateSdk`
(NuGet), `winget`, and a install script are all pre-MVP roadmap items, not
available today. Build from source:

```bash
git clone https://github.com/Sigil-build/sigil.git
cd sigil
dotnet build Sigil.slnx -c Release
dotnet test Sigil.slnx -c Release
```

See [Getting started](docs/getting-started.md) for a full walkthrough
(`init` → `validate` → `pack`). A signed GitHub Release with prebuilt
binaries will replace this section once the release workflow ships — see
`CHANGELOG.md`'s "Known limitations".

## Credits

- Install Icon by Saki (Alexandre Moore) on <a href="https://icon-icons.com/authors/32-saki-alexandre-moore">Icon-Icons.com</a>

## License

MIT — see [LICENSE](LICENSE).
