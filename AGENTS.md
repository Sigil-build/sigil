# Sigil — Agent Guide

Canonical context file for AI coding agents (Claude Code, Codex, Cursor, Copilot, …).
`CLAUDE.md` imports this file; keep this one as the single source of truth.

Sigil is an open-source .NET 10 / Native AOT CLI for declarative Windows-software distribution:
pack → sign → publish → update, driven by one `sigil.yaml`. **Status: pre-MVP, Windows-first.**
The publish stage and delta-update SDK are not built yet.

## Build & test

```bash
dotnet restore Sigil.slnx
dotnet build Sigil.slnx -c Release      # Release, not Debug — see AOT rules below
dotnet test Sigil.slnx -c Release
dotnet format Sigil.slnx --verify-no-changes   # CI-enforced
```

.NET SDK is pinned EXACTLY by `global.json` (10.0.303, `rollForward: disable`): the locked restore (R23a) pins the SDK-injected `Microsoft.DotNet.ILCompiler` / `Microsoft.NET.ILLink.Tasks` packages, so a rolled-forward SDK fails with NU1004. Bump the SDK and regenerate the lock files together.

**CI is not Windows-only.** Every job that builds, tests or AOT-publishes runs on `windows-latest`, but five jobs across four workflows run on `ubuntu-latest`: `changes` (`ci.yml:45`), `pr-title` and `schema-lockstep` (`pr-guards.yml:22, 40`), `gitleaks` (`secret-scan.yml:21`) and `drift-check` (`docs.yml:25`). Do not write PowerShell into a bash job — check the job's `runs-on` before you touch a workflow step.

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

The Python gate in `ci.yml` enforces **five** floors, not one:

| Floor | Value | Where |
|---|---|---|
| project-wide **union** | **0.77** | `PROJECT_WIDE_FLOOR`, `ci.yml:232` |
| `SigilBuild.Core` | 0.69 | `ASSEMBLY_FLOORS`, `ci.yml:234-237` |
| `SigilBuild.Signing` | 0.68 | same |
| `SigilBuild.Wrapper.Core` | 0.79 | same |
| `SigilBuild.Packaging` | 0.72 | same |

Ignore the `THRESHOLD = 0.65` constant at `ci.yml:207` — it is dead, and its own
comment says so. A PR written against "65 %" will fail CI.

The floors are a **ratchet**: each is the current measured value rounded down,
re-pinned upward when coverage rises, never lowered. `SigilBuild.Installer.BrandGenerator`
and `SigilBuild.Localization.Generator` report but are deliberately unfloored.
Aspirational (not gates): Core ≥ 80 %. There is no SDK project.

New code ships with tests: xUnit + FluentAssertions, AAA (Arrange / Act / Assert) layout.

### 5. Lockstep surfaces — change one, change all

| If you touch… | You must also touch… |
|---|---|
| `schemas/sigil-schema.json` | `docs/manifest-reference.md`, `examples/**` (CI validates all example manifests), `tests/SigilBuild.Schema.Tests/` fixtures. Note: the schema is an `EmbeddedResource` in `SigilBuild.Core`; the step-`type` enum is duplicated in **multiple** places in the schema file — update all of them. |
| Install-step catalog | Full chain: Core model → parser → schema → blob serializer → runtime step → StepFactory → wizard (if UI-visible) → tests → docs. Use the `add-install-step` skill in `.claude/skills/`. |
| Architecture (engine split, packaging pipeline, AOT strategy) | An ADR in `docs/architecture/` (see `adr-avalonia-aot.md` for format). CODEOWNERS routes these to tech leads. |
| Diagnostics | New validation errors get a `SIG0xxx` code in `src/SigilBuild.Core/Diagnostics/DiagnosticCodes.cs` — reuse the existing band ranges (e.g. SIG023x = install_steps). |
| `src/SigilBuild.Wrapper.Core/Cli/CommandLineParser.cs` | `docs/setup-exe-reference.md`. That page is hand-written (the CLI generator cannot reach this parser) and its whole premise is line-accurate citations into this file — they drift silently. Adding or changing a token means updating the flag tables, the token count, and the cited line ranges. |
| A new manifest field | The schema and `docs/manifest-reference.md` are not enough — a field nobody can find is a field nobody uses. Add it to the guide that owns the feature (`docs/guides/*`), too. |

### 6. CI economy — job-level gating only, never a workflow-level filter on a required check

The release ruleset lives on GitHub and is **not verifiable from this repo** — treat the
live branch-protection settings as the authority, not this file. What the repo does
record is `ci.yml:17-24`'s own comment: `build`, `aot publish (win-x64)` and
`dotnet format` are required status checks. The other candidates a maintainer may or
may not have added are `schema / docs lockstep` and `conventional-commit PR title`
(both `pr-guards.yml`) and `gitleaks` (`secret-scan.yml`); `docs drift check` is
deliberately **not** required (see below). If you need the real list, read the
ruleset on GitHub.

The rule that follows applies to any check that is required, whichever those turn out
to be. A workflow-level `paths:` / `paths-ignore:` filter on the *trigger* of a workflow that
produces one of those checks means the check **never reports at all** on a PR that
doesn't touch the filtered paths — GitHub then waits forever for a status that will
never arrive, and the PR is wedged. This already happened once, deliberately, as a
worked example: `docs.yml`'s `pull_request` trigger has a path filter, which is exactly
why `docs drift check` is **not** a required context (see the G0 note in
`docs/plan/release/03-RC_ORCHESTRATION.md`).

The fix used in `ci.yml` is a job-level gate instead: a cheap first job (`changes`,
via `dorny/paths-filter`, pinned by commit SHA) computes whether the diff is docs-only,
and the expensive jobs (`build`, `aot-publish`, the vulnerability scan) carry
`needs: changes` plus an `if:` on its `docs_only`/`code` outputs. A job that is
**skipped** by an `if:` still reports "skipped" for its check, which satisfies a
required-status-check rule — unlike a job whose *workflow* never triggered.
`wrapper-vm-tests.yml` is the one workflow where a workflow-level `paths-ignore:` is
safe, precisely because it produces no required check.

**The gate is fail-closed: a failed `changes` job runs the full build; only a
positive docs-only verdict skips it.** A gate that skips whenever it *cannot prove*
code changed is exactly backwards — a `changes` job that errors, is cancelled, or
whose output isn't the literal string `'true'` must never be treated the same as a
job that positively proved the diff is docs-only. This is not hypothetical: the
first version of this gate used a `predicate-quantifier` value that doesn't exist on
the pinned `dorny/paths-filter` release, so `changes` errored on every run, and the
downstream jobs' `if:` conditions checked only the output value — an errored gate
produced the same empty/falsy output as nothing having run, so `build`,
`aot-publish` and the vulnerability scan silently reported "skipped" (satisfying
their required checks) on every PR, code changes included. The fix checks
`needs.changes.result` explicitly (`!cancelled() && (needs.changes.result !=
'success' || needs.changes.outputs.docs_only != 'true')`), not just the output
value — see `ci.yml` for the full three-way truth table and why `!cancelled()` is
used instead of `always()`.

When adding or editing a workflow: if it contributes a required check (or might
later), gate expensive jobs with `needs`/`if` on a cheap upstream job's output, not
with `on.push.paths` or `on.pull_request.paths` — and make sure that gate fails
closed (runs the expensive job) whenever it cannot positively prove a skip is safe.

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

Decisions live in `docs/architecture/` (ADRs). The historical sprint and feature-parity
plans under `docs/plan/` are being retired; the **live** release record is
`docs/plan/release/`, which stays authoritative until the release ships. Edit those
files only to record outcomes — a gate closing, a register row landing, a measured
result — never to make an old plan agree with new code. A decision that changed needs a
new doc or an ADR amendment, not a rewritten plan.

## Conventions

- File-scoped namespaces, nullable enabled, `TreatWarningsAsErrors=true` (a new
  warning = a broken build; never suppress with pragmas without a comment saying why).
- Do not weaken `.editorconfig` severities or `Directory.Build.props` settings.
- Conventional Commits (`feat:`, `fix:`, `docs:`, `chore:`, …) — PR titles are lint-gated.
- Secrets never land in the repo; gitleaks runs pre-commit and in CI. Test
  fixtures that look like secrets go under the allowlisted paths in `.gitleaks.toml`.

## PR checklist (what CI + reviewers verify)

1. `dotnet build Sigil.slnx -c Release` — zero warnings (restore with `--locked-mode`, as CI does)
2. `dotnet test Sigil.slnx -c Release` — green (state clearly which tests you could not run locally)
3. `dotnet format Sigil.slnx --verify-no-changes` — clean
4. Lockstep surfaces updated (table above)
5. Conventional-commit PR title
6. No secrets (`gitleaks detect`)
