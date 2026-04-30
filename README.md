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
declarative YAML, headless cloud code signing, and zstd dictionary-mode delta
updates with a built-in client SDK.

## Install (after MVP launch)

```bash
# Windows
winget install Sigil-build.sigil

# macOS / Linux
curl -sSL https://sigil.build/install.sh | sh

# .NET developers, any platform
dotnet tool install -g SigilBuild
```

Today (pre-MVP) you can build from source — see `CONTRIBUTING.md`.

## Build from source

```bash
git clone https://github.com/Sigil-build/sigil.git
cd sigil
dotnet build
dotnet test
```

## License

MIT — see [LICENSE](LICENSE).
