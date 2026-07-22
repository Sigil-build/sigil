# Sigil — Agent Guide

Canonical context file for AI coding agents (Claude Code, Codex, Cursor, Copilot, …).
`CLAUDE.md` imports this file; keep this one as the single source of truth.

Sigil is an open-source .NET 10 / Native AOT CLI that replaces NSIS / Inno Setup / WiX:
pack → sign → publish → update, driven by one `sigil.yaml`. **Status: pre-MVP, Windows-first.**
The publish stage and delta-update SDK are not built yet.

## Build & test

```bash
dotnet restore Sigil.slnx
dotnet build Sigil.slnx -c Release      # Release, not Debug — see AOT rules below
dotnet test Sigil.slnx -c Release
dotnet format Sigil.slnx --verify-no-changes   # CI-enforced
```

.NET SDK is pinned by `global.json` (10.0.100+). CI runs on `windows-latest` only.

## Hard rules (CI will reject violations)

### 1. Native AOT is mandatory — and Debug builds lie

Everything ships AOT-compiled. The trim/AOT analyzer (`IL2026`, `IL3050` = errors)
only runs on **Release** builds (`EnableTrimAnalyzer` is Release-conditioned in
`Directory.Build.props`). A green Debug build proves nothing. Always verify with
`-c Release` before claiming success.

Banned patterns (they fail AOT publish):

- `Activator.CreateInstance`, `Type.GetType`, `Assembly.Load*`, `DynamicMethod`
- `MakeGenericType` / `MakeGenericMethod` on unconstrained types
- Expression trees + `.Compile()`
- Reflection-based `JsonSerializer.(De)Serialize` — use the source-generated
  contexts instead (e.g. `src/SigilBuild.Wrapper.Core/Json/WrapperBlobJsonContext.cs`).
  `SerializableInstallStep` uses a **hand-rolled discriminator on purpose** —
  do not "simplify" it to `JsonDerivedType` polymorphism.

Need codegen? Use a source generator (`SigilBuild.Localization.Generator` is the model).

### 2. Windows-only tests — know what your sandbox can actually verify

If you are running on Linux/macOS (most agent sandboxes), you can edit code and
reason about it, but signtool, MSIX/MakeAppx, registry steps, COM shortcuts, and
the installer-host runtime **only build/run on Windows**. Do not claim tests pass
if you could not run them — say so explicitly and let CI (`ci.yml`,
`wrapper-vm-tests.yml`) be the arbiter. Integration-only wrappers carry
`[ExcludeFromCodeCoverage]`.

### 3. Size budgets (CI-gated, do not raise casually)

- `sigil.exe` (CLI, win-x64 AOT): **≤ 15 MB**
- Installer host full footprint: **≤ 45 MB** (gate in `scripts/publish-installer-runtime.ps1`,
  re-pinned 40→45 in P9; ~3 MB headroom left)

If your change trips a gate, that is a design conversation, not a number to bump.

### 4. Coverage gate

CI enforces ≥ 65 % project-wide **union** coverage (see the Python gate in `ci.yml`).
Aspirational targets: Core ≥ 80 %, Signing/SDK ≥ 85 %. New code ships with tests:
xUnit + FluentAssertions, AAA (Arrange / Act / Assert) layout.

### 5. Lockstep surfaces — change one, change all

| If you touch… | You must also touch… |
|---|---|
| `schemas/sigil-schema.json` | `docs/manifest-reference.md`, `examples/**` (CI validates all example manifests), `tests/SigilBuild.Schema.Tests/` fixtures. Note: the schema is an `EmbeddedResource` in `SigilBuild.Core`; the step-`type` enum is duplicated in **multiple** places in the schema file — update all of them. |
| Install-step catalog | Full chain: Core model → parser → schema → blob serializer → runtime step → StepFactory → wizard (if UI-visible) → tests → docs. Use the `add-install-step` skill in `.claude/skills/`. |
| Architecture (engine split, packaging pipeline, AOT strategy) | An ADR in `docs/architecture/` (see `adr-avalonia-aot.md` for format). CODEOWNERS routes these to tech leads. |
| Diagnostics | New validation errors get a `SIG0xxx` code in `src/SigilBuild.Core/Diagnostics/DiagnosticCodes.cs` — reuse the existing band ranges (e.g. SIG023x = install_steps). |

## Repo map

| Project | Role |
|---|---|
| `SigilBuild.Cli` | `sigil` entry point (System.CommandLine); commands: validate, init, pack, sign |
| `SigilBuild.Core` | Manifest typed graph (`Manifest/`), YAML parsing + schema validation (`Configuration/`), `SIG0xxx` diagnostics |
| `SigilBuild.Packaging` | Pack backends: `Zip/`, `Msix/`, `ExeWrapper/` (builds the installer blob), `Installer/` |
| `SigilBuild.Signing` | Authenticode: `Local/` (signtool), `Azure/` (Trusted Signing), audit log |
| `SigilBuild.Wrapper.Core` | Shared install engine: `Engine/` (InstallEngine, RollbackJournal, StepFactory), `Steps/` (the step catalog), `Expressions/` (when-clauses), `Json/` (AOT-safe blob serialization) |
| `SigilBuild.Wrapper` | Console-only wrapper host (`/silent` path) |
| `SigilBuild.Installer.Host` | Avalonia wizard UI (Views/Screens, ViewModels), engine-driven |
| `SigilBuild.Installer.BrandGenerator` | Derives light+dark palette from two manifest colors at pack time |
| `SigilBuild.Localization.Generator` | netstandard2.0 source generator (analyzer-only reference — beware `PublishAot` property leaks; see the comment in `Directory.Build.props`) |

Decisions live in `docs/architecture/` (ADRs) and `docs/plan/` (historical specs —
**read-only context, do not edit** to match new code; write a new doc or ADR instead).

## Conventions

- File-scoped namespaces, nullable enabled, `TreatWarningsAsErrors=true` (a new
  warning = a broken build; never suppress with pragmas without a comment saying why).
- Do not weaken `.editorconfig` severities or `Directory.Build.props` settings.
- Conventional Commits (`feat:`, `fix:`, `docs:`, `chore:`, …) — PR titles are lint-gated.
- Secrets never land in the repo; gitleaks runs pre-commit and in CI. Test
  fixtures that look like secrets go under the allowlisted paths in `.gitleaks.toml`.

## PR checklist (what CI + reviewers verify)

1. `dotnet build Sigil.slnx -c Release` — zero warnings
2. `dotnet test Sigil.slnx -c Release` — green (state clearly which tests you could not run locally)
3. `dotnet format Sigil.slnx --verify-no-changes` — clean
4. Lockstep surfaces updated (table above)
5. Conventional-commit PR title
6. No secrets (`gitleaks detect`)
