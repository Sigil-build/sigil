# Release-candidate track — orchestration

> ## Status: Stage 0 COMPLETE (2026-07-28) · Stage 1 ready to open
>
> **Stage 0 (lane F0) merged as [PR #16](https://github.com/Sigil-build/sigil/pull/16)
> → `c82f5eb`.** Gate **G0 passed**, including its proof-of-failure ceremony:
> throwaway PR #17, titled `broken title`, was *observed failing* the
> `conventional-commit PR title` job, then closed. Register rows **R20, R40, R41**
> closed. Full CI green on the RC: `build`, `dotnet format`,
> `aot publish (win-x64)`, `docs drift check`, `gitleaks`,
> `schema / docs lockstep`, `conventional-commit PR title`. Tests unchanged
> throughout at **1097 total / 1096 passed / 1 skipped / 0 failed**.
>
> ### ✅ The RC is gated (confirmed 2026-07-28)
>
> Ruleset `19919273` ("release") is **`enforcement: active`**.
> `gh api repos/Sigil-build/sigil/rules/branches/release%2Fv0.1.0-alpha` reports
> four rule types in force — `deletion`, `non_fast_forward`, `pull_request`,
> `required_status_checks` — with six required contexts: `build`,
> `aot publish (win-x64)`, `dotnet format`, `schema / docs lockstep`,
> `conventional-commit PR title`, `gitleaks`.
>
> `docs drift check` is deliberately **not** required: `docs.yml:8-13`
> path-filters its `pull_request` trigger, so on a PR touching none of those
> paths it never reports, and a required-check rule on a never-reporting check
> wedges the PR permanently.
>
> Note the ruleset sets `strict_required_status_checks_policy: true`, so once
> active each merge into the RC invalidates the other open lane PRs and they must
> be rebased before merging. That is the correct trade for this track — it
> guarantees each lane's checks ran against the integrated tree — but it makes
> the G1 merge order (S1 → S2 → S3 → T1) a serial rebase chain, not a free-for-all.
> **CORRECTED 2026-08-11 — this paragraph previously said the opposite.** It read
> "`bypass_actors` is empty and `current_user_can_bypass` is `never`". Measured on
> the live ruleset: `current_user_can_bypass: always`, with **two** bypass actors
> (`OrganizationAdmin`, and `RepositoryRole` id 5). So direct pushes to
> `release/**` do **not** stop working for the repository owner. Stage 2's
> orchestration reasons from this sentence, which is why it is corrected rather
> than quietly deleted — but note that "can bypass" is not "should": every Stage 1
> lane went through a PR, and the merge order held because of it.

### CI evidence captured at G0 (authoritative — resolves audit UNVERIFIED items)

| Metric | Audit (local) | CI |
|---|---|---|
| `SigilBuild.Core` coverage | 63.89% | **63.89%** |
| `SigilBuild.Signing` coverage | 68.79% | **68.79%** |
| Project-wide union | 75.17% | **74.74%** |
| `sigil.exe` size | 13.98 MB | **13.98 MB** (≤ 15 MB gate) |
| AOT publish | fails on dev box | **succeeds in CI** |

**R21 confirmed:** the coverage denominator contains only six assemblies —
`SigilBuild.Cli`, `SigilBuild.Wrapper` and `SigilBuild.Installer.Host`
contribute zero lines. T1 fixes this.

### CI evidence captured at G1 (supersedes the G0 table above)

RC head `86c2799`, CI run 31510096635 — green, including `aot publish (win-x64)`.

| Metric | G0 | G1 |
|---|---|---|
| Tests | 1097 · 1 skipped | **1538 · 1517 passed · 21 skipped · 0 failed** |
| `SigilBuild.Core` coverage | 63.89% | **69.51%** |
| `SigilBuild.Packaging` coverage | 72.00% | **86.51%** |
| `SigilBuild.Signing` coverage | 68.79% | **68.79%** |
| `SigilBuild.Wrapper.Core` coverage | 77.64% | **79.47%** |
| Project-wide union | 74.74% | **78.04%** |

Two things this table does not say. **The three zero-line assemblies still report
zero** — T1 made the absence loud (`::warning::`) rather than fixing it, because
hard-failing would have turned the required `build` check red at the merge and
blocked every Stage 2/3 lane behind it. And **`SigilBuild.Packaging` jumps 14
points on CI purely because CI stages the win-x64 runtime**, which unskips nine
tests the dev box cannot run — the floors are pinned to the *local* measurement
precisely so they can never fail an otherwise-green CI run.

> **For agentic workers:** REQUIRED SUB-SKILL: use `superpowers:subagent-driven-development`
> (recommended) or `superpowers:executing-plans` to implement the stage documents
> task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close all findings in
[`00-GAP_REGISTER.md`](00-GAP_REGISTER.md) on the branch
`release/v0.1.0-alpha`, ending in a signed, tagged `v0.1.0-alpha` that a
stranger can download and run.

> The audit opened with **46**. Stage 1 closed 17 and filed **14 more** (R44–R57),
> all of them found while fixing something else. Expect the register to keep
> growing as lanes land: a stage that adds no rows either got lucky or was not
> looking. Cite the register for the current count rather than repeating a number
> here.

**Architecture:** Five sequential stages of parallel, file-disjoint lanes. Each
lane is a branch cut from the RC that opens a PR into the RC; the orchestrator
merges at gates. Stage 0 runs alone because its formatting pass rewrites 28
files. Stages 2 and 3 overlap because they share no files. `main` is untouched
until one final RC→main PR.

**Tech Stack:** .NET 10, Native AOT, xUnit + FluentAssertions, Avalonia 12,
GitHub Actions on `windows-latest`, PowerShell for build scripting.

Design of record:
[`../../superpowers/specs/2026-07-28-release-candidate-track-design.md`](../../superpowers/specs/2026-07-28-release-candidate-track-design.md).
Findings and evidence:
[`00-GAP_REGISTER.md`](00-GAP_REGISTER.md). Decision context:
[`02-READINESS_REPORT.md`](02-READINESS_REPORT.md).
[`01-FIX_PLAN.md`](01-FIX_PLAN.md) is **superseded by this document** and kept
only as the audit record.

This document holds **no lane detail** — it is an index, the gates, and the
proof that no finding was dropped. Lane work lives in the stage documents.

---

## Global Constraints

Every task in every stage document implicitly includes these. Copied verbatim
from `AGENTS.md` and the audit.

- **Native AOT is mandatory.** No `Activator.CreateInstance`, `Type.GetType`,
  `Assembly.Load*`, `DynamicMethod`, `MakeGenericType`/`MakeGenericMethod` on
  unconstrained types, expression trees + `.Compile()`, or reflection-based
  `JsonSerializer`. Use the source-generated contexts
  (`Json/WrapperBlobJsonContext.cs`). `SerializableInstallStep` uses a
  hand-rolled discriminator **on purpose** — do not convert it to
  `JsonDerivedType`.
- **`TreatWarningsAsErrors=true`.** A new warning is a broken build. Never
  suppress with a pragma without a comment saying why. The trim/AOT analyzer
  (`IL2026`, `IL3050` = errors) runs **only on Release** — a green Debug build
  proves nothing.
- **Verify with `-c Release`, always.**
- **File-scoped namespaces, nullable enabled.** Do not weaken `.editorconfig`
  severities or `Directory.Build.props` settings.
- **Size budgets:** `sigil.exe` ≤ 15 MB, installer host footprint ≤ 45 MB.
  Tripping a gate is a design conversation, not a number to bump.
- **Lockstep surfaces.** Touching `schemas/sigil-schema.json` means also
  touching `docs/manifest-reference.md`, `examples/**`, and
  `tests/SigilBuild.Schema.Tests/` fixtures — and the step-`type` enum appears
  in **multiple** places in the schema file. New validation errors get a
  `SIG0xxx` code in `Diagnostics/DiagnosticCodes.cs`, reusing the existing
  bands. Highest currently used: **SIG0322**.
- **Conventional Commits.** PR titles are lint-gated once Stage 0 lands.
- **Tests:** xUnit + FluentAssertions, AAA layout. Note the repo's unusual house
  style — some files put `using` directives *inside* the file-scoped namespace
  (`tests/SigilBuild.Wrapper.Tests/Engine/UninstallEngineTests.cs`) and some
  outside (`.../Steps/ConfigEditorTests.cs`). Match the file you are editing.
- **Native AOT publish does not work on the dev machine** (`vswhere.exe`
  unresolvable, MSVC linker `MSB3073`/exit 123). Anything needing a real
  `Setup.exe` is CI-only. **Say which checks you could not run.** Never imply a
  green suite you did not observe.

### The rule enforced hardest

Every security fix lands with a **negative test**, and the orchestrator verifies
it by checking out the parent commit and confirming **the test actually fails
there**:

```bash
git stash && git checkout HEAD~1 -- src/ && dotnet test --filter "FullyQualifiedName~YourNewTest" -c Release
# expect FAIL, then restore
git checkout HEAD -- src/ && git stash pop
```

A negative test that passes before the fix is not a test. This track exists
because ~24 tests were passing while asserting nothing; accepting a new test on
trust would repeat precisely the failure being fixed.

---

## Branch policy

| | |
|---|---|
| RC branch | `release/v0.1.0-alpha`, cut from `main` @ `1be494c` |
| Lane branches | `rc/<lane>-<slug>`, cut **from the RC**, never from `main` |
| Lane completion | PR into the RC; `pr-guards` gates it; **a human merges** — see below |
| Track completion | one PR RC → `main`, then tag `v0.1.0-alpha` |
| Urgent `main` fixes | cherry-pick onto the RC; do not merge `main` into the RC mid-stage |

**The orchestrator cannot merge lane PRs — discovered at S1a, 2026-08-11.** The
ruleset sets `required_approving_review_count: 1` and GitHub forbids approving
your own PR, so **every** lane PR needs a human hand-merge (or the count dropped
to 0). `gh pr merge --admin` is refused by the sandbox classifier and **must not
be worked around**. Budget for a human round-trip at each link of the Stage 2 and
Stage 3 merge chains, not just at the gate.

**Completed 2026-07-28:** stale remote branches archived as `archive/*` tags
(pushed to origin) and deleted. `git ls-remote --heads origin` now returns
`main` and `release/v0.1.0-alpha` only. The `archive/*` tags may be deleted once
the RC merges.

---

## Stage index

| Stage | Document | Lanes | Runs | Gate |
|---|---|---|---|---|
| 0 | [`04-STAGE-0-foundation.md`](04-STAGE-0-foundation.md) | F0 | **solo** | G0 |
| 1 | [`05-STAGE-1-security-core.md`](05-STAGE-1-security-core.md) | S1, S2, S3, T1 | 4 parallel | G1 |
| 2 | [`06-STAGE-2-security-depth.md`](06-STAGE-2-security-depth.md) | S4, S5, S6 | 3 parallel | G2 |
| 3 | [`07-STAGE-3-release-surface.md`](07-STAGE-3-release-surface.md) | REL, SUP, DOC | 3 parallel, **overlaps Stage 2** | G2 |
| 4 | [`08-STAGE-4-verification.md`](08-STAGE-4-verification.md) | V1 | solo | G3, G4 |

Estimated calendar: 1 day + 1 week + (4 days ∥ 4 days) + 2 days ≈ **2.5 weeks**.

---

## Lane → finding map

Every one of the 46 rows appears **exactly once**. This table is the audit
trail; V1 walks it row by row.

| Lane | Model | Findings | Count |
|---|---|---|---|
| `F0` foundation | haiku-4.5 | R20, R40, R41 | 3 |
| `S1` trusted state | opus-5 | R1, R2, R19 | 3 |
| `S2` path containment | opus-5 | R3, R9, R16, R31, R32 | 5 |
| `S3` staged execution | opus-5 | R4, R5, R10, R11, R12, R17 | 6 |
| `T1` test truth | sonnet-5 | R6, R21, R22 | 3 |
| `S4` network + update | opus-5 | R8, R13, R14, R30, R37, R39 | 6 |
| `S5` residual engine | sonnet-5 | R15, R18, R28, R29, R34, R38 | 6 |
| `S6` step hardening + ADRs | opus-5 | R33, R35, R36 | 3 |
| `REL` release scaffolding | sonnet-5 | R7, R23, R23a, R24 | 4 |
| `SUP` supply chain | sonnet-5 | R42 | 1 |
| `DOC` docs truth | sonnet-5 | R25, R26, R26a, R27, R41a, R43 | 6 |
| `V1` verification | opus-5 | re-verifies all | — |
| | | **Total** | **46** |

Sorted check — R1 R2 R3 R4 R5 R6 R7 R8 R9 R10 R11 R12 R13 R14 R15 R16 R17 R18
R19 R20 R21 R22 R23 R23a R24 R25 R26 R26a R27 R28 R29 R30 R31 R32 R33 R34 R35
R36 R37 R38 R39 R40 R41 R41a R42 R43 = 46, no gaps, no repeats.

---

## File ownership (cross-lane conflicts)

Only files touched by more than one lane are listed. Everything else belongs to
whichever lane's task names it.

| File | Owner | Rule |
|---|---|---|
| `.github/workflows/ci.yml` | `T1` (Stage 1) | `REL` and `SUP` rebase onto T1's version in Stage 3 and append only |
| `src/.../Engine/InstallSession.cs` | `S1` | `S3` **reports** rather than edits if the update temp path must move |
| `src/.../Engine/RollbackJournal.cs` | `S1` (Stage 1) | `S5` extends it in Stage 2, after S1 merges |
| `src/.../Update/UpdateRunner.cs` | `S3` (Stage 1) | `S4` extends it in Stage 2, after S3 merges |
| `schemas/sigil-schema.json`, `docs/manifest-reference.md`, `examples/**` | `S4` | lockstep surface — one commit moves all of it. `DOC` must not touch these. `S6` routes its `json_edit` schema change **through S4** |
| `docs/guides/install-steps.md` | `S2` | `DOC` must not touch it |
| `Directory.Build.props` | `REL` | version SoT + lock files |

---

## Gates

A gate is a merge point with a check that can actually be run. **Do not open the
next stage until the gate's manual checks have been performed by hand, not
assumed.**

### G0 — after Stage 0

- [ ] `dotnet format Sigil.slnx --verify-no-changes` exits **0** on the RC
- [ ] `git ls-files .claude` is non-empty
- [ ] `_agent-setup/` no longer exists
- [ ] `git check-ignore -v .superpowers` exits **0**
- [ ] **Proof of gate:** open a throwaway PR titled `broken title` against the
      RC and **watch `pr-guards` fail it**, then close the PR. A gate nobody has
      seen fail is not a gate.
- [ ] **Branch protection — the checks must GATE, not merely report.** Added
      after Stage 0's final review found that
      `gh api repos/Sigil-build/sigil/branches/release%2Fv0.1.0-alpha/protection`
      returns **404 "Branch not protected"** and `main` has no required status
      checks. Without this, a lane PR can go fully red and still be merged, and
      Stage 0's whole purpose is only half met.

      Require on `release/v0.1.0-alpha`: `build`, `aot publish (win-x64)`,
      `conventional-commit PR title`, `schema / docs lockstep`, `dotnet format`,
      `gitleaks`.

      > **Do NOT require `docs drift check`.** `.github/workflows/docs.yml:8-13`
      > puts a `paths:` filter on its `pull_request` trigger, so on a PR touching
      > none of those paths the check never reports at all — and a required-check
      > rule on a check that never reports wedges the PR permanently. `ci.yml`,
      > `secret-scan.yml` and `pr-guards.yml` carry no path filters and are safe
      > to require.
- [ ] `dotnet build -c Release` and `dotnet test -c Release` totals unchanged
      from before Stage 0 (record both)

### G1 — after Stage 1

Merge order: **S1 → S2 → S3 → T1**. All four merged: `5b65712` (#20, after
`31ae3a3` / #19), `4505b24` (#21), `72d6437` (#22), `86c2799` (#23).

**Five** attacks (the heading said four and listed five), run **by hand as a
standard (non-admin) user**. Each must be refused with a log line naming the
reason — not crash, not silently proceed.

**How these are executed on this machine.** The dev box cannot Native-AOT-publish
(`vswhere` / MSVC linker, `MSB3073` exit 123), so there is no real `Setup.exe` to
attack. Each artifact is therefore planted **by hand as the standard user** — which
is the half of the attack that must succeed — and the victim half drives
`InstallSession` / `UninstallEngine` / `InstalledStateResolver` /
`NativeRuntimeBootstrap` directly from a small elevated harness. The harness
borrows the `SigilBuild.Wrapper.Tests` assembly name to reach `internal` members,
but is deliberately **not** the test project: that project installs a process-wide
`NeverStageElevatedForTesting` floor via `[ModuleInitializer]`, and running these
attacks under it would exercise a path production never takes.

- [ ] Plant `C:\ProgramData\Sigil\<AppId>\uninstall.json` with a `restore_file`
      record targeting `C:\Windows\System32\`. Run an elevated machine-scope
      install **and** an elevated uninstall. Both refuse. *(R1)*
- [ ] Plant the same file in `%LocalAppData%\Sigil\<AppId>\`. A machine-scope
      operation must not read it at all. *(R1)*
- [ ] Plant `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\<AppId>`
      with `DisplayVersion=0.0.1` and `UninstallString` pointing at a scratch
      exe. Run an elevated machine-scope install of that AppId. The scratch exe
      must **not** run. *(R2)*
- [x] `Setup.exe /allusers /D=C:\Users\Public\evil` is rejected. *(R3)* — **PASSED
      2026-08-11.** Refusal line: *"The install directory 'C:\Users\Public\evil' is
      outside the machine scope root 'C:\Program Files', 'C:\Program Files (x86)'
      (or reaches it through a directory junction). Refusing to install there —
      {install_dir} feeds SYSTEM-level step targets. Nothing was installed."* The
      wizard-collected path is refused identically, and the positive control
      (`/D=C:\Program Files\SigilG1Probe`) still resolves, so the check is not
      vacuous.
- [ ] Pre-create the elevated native-runtime cache with a bogus `libSkiaSharp.dll`
      and a valid `.sigil-runtime-complete` marker. The elevated wizard must not
      load it. *(R4)* — **the path in this line was stale**: the R4 fix moved the
      elevated root to `%ProgramData%\sigil-runtime\<sha>\`, not
      `%LocalAppData%\Sigil\runtime\<sha>\`. Attack the new one.

Plus one probe the plan did not ask for, added because **S3's entire elevated path
rests on it**: does a directory `CreateHardened` produces actually satisfy
`IsAdminOnlyWritable` under a real elevated token? If it does not, every elevated
install refuses. It fails only on a box with `NoDefaultAdminOwner=1`, where
`%ProgramFiles%`'s inherit-only `CREATOR OWNER:(F)` materialises as a concrete
user ACE. Run through the **real** predicate, not a PowerShell replica of it.

- [ ] Elevated ACL probe: `IsAdminOnlyWritable(dir)` and `IsTrustedFile(file)` on
      a fresh `%ProgramFiles%` directory, on a `CreateHardened` one, and — as the
      non-vacuity control — on `%ProgramData%` itself, which must be **rejected**.

Then:

- [x] `dotnet test -c Release` reports a **non-zero skip count**. Record the
      exact number here → **21** (CI run 31510096635 on `86c2799`; **27** locally).
      That number is the honest size of the untested surface, against the **1** this
      stage started with. Take the CI figure as authoritative: the local run differs
      legitimately by `+1` (an unelevated-only fact that skips on the elevated
      runner) and `−7` (CI stages the runtime, so seven packaging tests actually
      run).
- [x] No test soft-skips by returning early (`grep -rn "// soft-skip" tests/`
      and `grep -rn '"SKIP:' tests/` both return nothing)
- [x] CI green including the new per-assembly coverage floors
- [x] For each security PR: the negative test was confirmed failing on the
      parent commit (orchestrator ran it, did not take the lane's word) — R1's
      directory attack at `8ad077d` and file attack at `5d3fd98`; R12's four
      `StagedExecutionTests` at `31ae3a3`; R3's 6-of-8 and R31/R32's 12-of-15 at
      the S2 parent, in each case with the passing remainder being the positive
      controls.

### G2 — after Stages 2 and 3

Merge order: **S4 → S5 → S6 → REL → SUP → DOC**.

- [ ] Copy-paste the silent-install line from the **corrected**
      `docs/guides/parameters.md` into a real `Setup.exe` — it succeeds *(R26)*
- [ ] `dotnet restore --locked-mode` succeeds from a clean clone *(R23a)*
- [ ] `sigil init --template full` produces a manifest that packs *(R30)*
- [ ] A manifest with `source: { url: "http://…" }` **fails** to pack *(R8)*
- [ ] A manifest with `updates: { manifestUrl: "http://…" }` **fails** to pack *(R14)*
- [ ] A replayed stale signed channel manifest is rejected *(R13)*
- [ ] `THIRD-PARTY-NOTICES.md` names Skia, ANGLE, HarfBuzz, and libsodium
      explicitly *(R23)*
- [ ] `SECURITY.md` exists and GitHub private vulnerability reporting is on *(R23)*
- [ ] `grep -rn "0\.0\.1-alpha" --include='*.cs' --include='*.csproj' --include='*.yml' .`
      returns **nothing** *(R24)*
- [ ] The vulnerability scan ran; its findings are recorded as fixed or accepted *(R42)*

### G3 — after Stage 4

- [ ] `wrapper-vm-tests.yml` run **for real**, green, against non-vacuous tests.
      Run URL → `______`
- [ ] The VM matrix runs on a schedule or on merge, not only on demand
- [ ] Release dry-run: a throwaway prerelease tag produces signed, checksummed
      artifacts with the notices attached
- [ ] The downloaded artifact **runs on a clean machine** — verified by
      downloading and executing it, not by reading the workflow *(R7's
      sibling-DLL trap)*
- [ ] Every one of the 46 rows demonstrated fixed **or** explicitly deferred
      with a one-line justification in `02-READINESS_REPORT.md`
- [ ] `02-READINESS_REPORT.md` Definition of Done fully ticked

### G4 — release

- [ ] RC → `main` PR opened, reviewed, merged
- [ ] Tag `v0.1.0-alpha`
- [ ] Release notes = the known-limitations draft from `02-READINESS_REPORT.md`
- [ ] **Not announced.** Hold the launch post for a `v0.2.0` with at least one
      external user's successful install and a scheduled green VM matrix.
- [ ] NuGet IDs `SigilBuild` and `SigilBuild.UpdateSdk` reserved *(R41a)* —
      orchestrator chore, do before the repo gets attention
- [ ] `archive/*` tags deleted

---

## Failure handling

- A red lane holds only its **dependents**. S1 failing holds S5; it does not
  hold REL or DOC.
- **Nothing is fixed forward on the RC.** Reopen the owning lane branch with the
  failure attached.
- A lane that finds a gap **not** in the register **stops and files a new row**
  in `00-GAP_REGISTER.md` rather than widening its own scope. The orchestrator
  triages it into a stage.
- Lanes never merge their own PRs.

---

## Progress

| Lane | Branch | Started | PR | Merged | Gate |
|------|--------|:---:|:---:|:---:|------|
| F0  | `rc/f0-foundation` | ☑ | [#16](https://github.com/Sigil-build/sigil/pull/16) | ☑ `c82f5eb` | **G0 ✅** |
| S1a | `rc/s1-trusted-state` | ☑ | [#19](https://github.com/Sigil-build/sigil/pull/19) | ☑ `31ae3a3` | G1 |
| S1  | `rc/s1-trusted-state` | ☑ | [#20](https://github.com/Sigil-build/sigil/pull/20) | ☑ `5b65712` | G1 |
| S2  | `rc/s2-path-containment` | ☑ | [#21](https://github.com/Sigil-build/sigil/pull/21) | ☑ `4505b24` | G1 |
| S3  | `rc/s3-staged-execution` | ☑ | [#22](https://github.com/Sigil-build/sigil/pull/22) | ☑ `72d6437` | G1 |
| T1  | `rc/t1-test-truth` | ☑ | [#23](https://github.com/Sigil-build/sigil/pull/23) | ☑ `86c2799` | G1 |
| S4  | `rc/s4-network-update` | ☐ | ☐ | ☐ | G2 |
| S5  | `rc/s5-residual-engine` | ☐ | ☐ | ☐ | G2 |
| S6  | `rc/s6-step-hardening` | ☐ | ☐ | ☐ | G2 |
| REL | `rc/rel-scaffolding` | ☐ | ☐ | ☐ | G2 |
| SUP | `rc/sup-supply-chain` | ☐ | ☐ | ☐ | G2 |
| DOC | `rc/doc-truth` | ☐ | ☐ | ☐ | G2 |
| V1  | `rc/v1-verification` | ☐ | ☐ | ☐ | G3/G4 |
