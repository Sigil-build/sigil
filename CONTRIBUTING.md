# Contributing to Sigil

Thanks for considering a contribution! Sigil is in pre-MVP — the surface area
changes weekly. Before opening a non-trivial PR, please open a discussion or
issue so we can align on direction.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), **exactly `10.0.303`** — `global.json` pins it with `"rollForward": "disable"`, so a rolled-forward SDK fails the locked restore with NU1004.
- Git 2.40+
- Optional: `gitleaks` for the local pre-commit hook

## Build & test

Use the same commands CI does. A bare `dotnet restore` skips `--locked-mode`,
so lock-file drift goes unnoticed; a Debug build skips the trim/AOT analyzer,
which is the only place `IL2026` / `IL3050` are errors:

```bash
dotnet restore Sigil.slnx --locked-mode
dotnet build Sigil.slnx -c Release --no-restore
dotnet test Sigil.slnx -c Release
dotnet format Sigil.slnx --verify-no-changes
```

To exercise the Native AOT publish (Windows only; `release.yml` publishes both
`win-x64` and `win-arm64`):

```bash
dotnet publish src/SigilBuild.Cli -c Release -r win-x64 -p:PublishAot=true
```

## Install git hooks (recommended)

Set up the local pre-commit hooks so secrets are caught before they reach the remote:

```bash
# macOS / Linux
./scripts/install-hooks.sh

# Windows (PowerShell)
./scripts/install-hooks.ps1
```

This points `core.hooksPath` at `.githooks/`, which currently runs `gitleaks protect`
on every commit. Install gitleaks first: <https://github.com/gitleaks/gitleaks#installing>.
The hook is a no-op if gitleaks is not on `PATH` — you'll get a friendly skip message.

## Coding conventions

- File-scoped namespaces, nullable reference types on, `TreatWarningsAsErrors=true`.
- Tests use xUnit + FluentAssertions. AAA layout (Arrange / Act / Assert).
- Native AOT is mandatory — **no reflection-heavy patterns** (no `Activator.CreateInstance`,
  no untyped `JsonSerializer.Deserialize`, no runtime expression trees).
  Use source generators when you need codegen.
- Commits follow [Conventional Commits](https://www.conventionalcommits.org/).
  Examples: `feat: add zip packager`, `fix: handle empty manifest`, `chore: bump xunit`.

## AI agents & assisted contributions

AI-assisted PRs are welcome. If you (or your agent) contribute with Claude Code,
Codex, Cursor, Copilot or similar, point the tool at **[AGENTS.md](AGENTS.md)** —
it is the canonical machine-readable guide (Native AOT rules, Windows-only test
caveats, size budgets, schema/docs lockstep). Claude Code users additionally get
project skills in `.claude/skills/` and advisory hooks in `.claude/settings.json`
(they warn, never block).

Two honesty rules for agent PRs:

- Verify with **Release** builds (`dotnet build Sigil.slnx -c Release`) — the
  AOT/trim analyzer does not run in Debug.
- If your environment could not run the Windows-only tests, say so in the PR
  description instead of implying a fully green suite. CI is the arbiter.

The `pr-guards` workflow enforces conventional-commit PR titles, `dotnet format`,
and schema/docs lockstep on every PR.

## The CI gates you will actually hit

Beyond build and test, these run on every PR and are worth knowing before you
are surprised by one:

- **Coverage floors** (`ci.yml`). Project-wide union ≥ **77 %**, plus four hard
  per-assembly floors: `SigilBuild.Core` ≥ 69 %, `SigilBuild.Signing` ≥ 68 %,
  `SigilBuild.Wrapper.Core` ≥ 79 %, `SigilBuild.Packaging` ≥ 72 %. They are a
  ratchet — set at the current measured value rounded down, raised as coverage
  rises, never lowered. Drop `SigilBuild.Core` to 68 % and CI fails.
- **Size gates.** `sigil.exe` ≤ 15 MB, and the installer host's full footprint
  ≤ 45 MB (`scripts/publish-installer-runtime.ps1`). Tripping one is a design
  conversation, not a number to bump.
- **Docs drift** (`docs.yml`). `cli-reference.md` and `manifest-reference.md`
  are regenerated and CI fails if `docs/` is then dirty. Never hand-edit those
  two pages; change the schema `description` or the CLI `Description` string
  and regenerate.
- **Secret scan** (`secret-scan.yml`) — gitleaks over the whole diff.
- **VM install/uninstall matrix** (`wrapper-vm-tests.yml`) — the real
  Windows-installer behaviour, which no unit test covers.

## PR checklist

- [ ] Tests added / updated
- [ ] `dotnet build Sigil.slnx -c Release` succeeds with no warnings
- [ ] `dotnet test Sigil.slnx -c Release` is green (say so explicitly if you could not run the Windows-only tests)
- [ ] `dotnet format Sigil.slnx --verify-no-changes` is clean — this is a required status check
- [ ] Conventional-commit PR title
- [ ] No secrets committed (`gitleaks detect` clean)
- [ ] If you touched `docs/architecture/`, you've updated the relevant ADR

## Code of Conduct

This project follows the [Contributor Covenant 2.1](CODE_OF_CONDUCT.md).
