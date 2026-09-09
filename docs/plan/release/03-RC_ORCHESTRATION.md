# Release-candidate track — orchestration

> ## Status: Stage 4 IN PROGRESS (2026-09-09) · G2 closed · G3 not open
>
> ### Stage 4 — IN PROGRESS (2026-09-09), RC head `b07021e`
>
> **Done in Stage 4 so far:** **V1.4** (the honest-numbers pass) and **V1.1** (the
> register walk plus this docs pass). V1.4 measured RC `102ea3f` locally at **1708
> tests / 1687 passed / 0 failed / 21 skipped**, Release build 0 warnings, format
> clean, and read CI run
> [34362414470](https://github.com/Sigil-build/sigil/actions/runs/34362414470) for
> **1708 / 1686 / 0 / 22**, union coverage **78.12 %**, `sigil.exe` **14.12 MB**,
> installer-host footprint **42.82 MB** — no regression against the audit baseline
> (see `02-READINESS_REPORT.md`). V1.1 walked **all 71 register rows** against that
> run's `.trx` artifacts: 41 test-evidenced, 14 manual, 11 written deferrals, **2
> claim-only** (R22, R38), **0 dropped**, and 16 closed on narrower evidence than the
> record implied. It corrected **R41a** from "closed" to "documented open", filed
> **R69–R73** (with **R74** and **R75** following from the VM run, below), and gave
> every row a per-row `STATUS (V1.1)` line — 49 of the 68
> walked rows had none, which is now itself a row (**R73**).
>
> **Four VM/workflow rows closed since G2:** **R58** ([#39](https://github.com/Sigil-build/sigil/pull/39)),
> **R67** ([#40](https://github.com/Sigil-build/sigil/pull/40)), **R66** and **R68**
> ([#41](https://github.com/Sigil-build/sigil/pull/41)); **R64** stays open, narrowed
> to the coverage itself. **R69/R70** are fixed in
> [#42](https://github.com/Sigil-build/sigil/pull/42), open.
>
> **The first automatic VM run then produced two more rows.** Diagnosing the
> `vm (install matrix)` leg's six failures on `b07021e`
> (`.superpowers/sdd/2026-09-08-g2-release-prep/vm-install-matrix-diagnosis.md`)
> found that **five of the six were fixture bugs #41 missed** — a `file_copy.to`
> given a *file* path where the step contract wants a destination directory, and one
> app id reused across two install roots — fixed on `rc/vm-fix-fixtures-round2` (PR
> number pending), along with an extension of #41's always-on guard to refuse that
> `to:` shape. **The sixth is a product row: R74.** Because
> `InstalledStateResolver.ScopeProbeOrder` probes HKLM only when elevated (lane S1's
> **R2** fix, and correct), an elevated per-user install cannot see its own prior
> per-user install: the plan says "fresh install" and the downgrade block never
> fires, while the reinstall cleanup — which reads the state store, not ARP — still
> tears the prior version down. **Net effect: an elevated per-user install can
> silently downgrade.** Reproduced, not inferred. **R74 does not block G3** — the
> guard it degrades is UX, not a trust boundary, and only in a session where the
> user already holds the privilege. Until it lands, the two elevation-sensitive
> upgrade assertions in that leg are **honest skips** naming R2, which leaves
> per-user upgrade/downgrade behaviour unexercised end to end — more coverage
> **R64** still owes. Owner: lane **S5/S1**.
>
> **R75**, from the P11 round-two lane: `com_register` journaled an undo for a
> registration that never took effect, and since **R15** a failed
> `DllUnregisterServer` means "still registered" — so with `on_failure: continue`
> that stale record reaches `uninstall.json` and makes **every later uninstall
> fail**, leaving the app permanently in Add/Remove Programs. Fixed in
> [#44](https://github.com/Sigil-build/sigil/pull/44) (`3e0ba90`, a tail-only
> `RollbackJournal.RetractLast`), open. It was found because the P11 VM test had
> been asserting the wrong behaviour as correct — the same lesson as **R66** and
> **R64**, one level in: a leg that never runs does not merely fail to catch bugs,
> it canonises them.
>
> **Blocked on the owner — nothing an agent lane can move:**
>
> - **VM matrix green.** The first *automatic* run
>   ([34368896457](https://github.com/Sigil-build/sigil/actions/runs/34368896457) on
>   `b07021e`, fired by #41's new push trigger) was in flight at the time of writing.
>   Until it is green, **R58**'s own end-to-end test has never executed.
> - **The release dry-run** needs the **six Trusted Signing secrets**; `release.yml`
>   refuses before any restore without them, and it has never run at all.
> - **Clean-machine install** of the published artifact (**R7**), which needs the
>   dry-run first.
> - **Private vulnerability reporting** is still `{"enabled":false}` (**R23**) and
>   **both NuGet IDs are still unreserved** (**R41a**) — two G4 owner actions.
> - **Merging the open lane PRs.** The orchestrator cannot merge them.
>
> **Still to run in Stage 4:** **V1.2** (re-attack the integrated RC, not each lane
> at its own tip) and **V1.3**.
>
> ### G1 / Stages 1–3 (historical)
>
> **Stage 1's four lanes are merged and gate G1 is closed** at RC head `c019df2`
> — see the G1 block below for the five attacks and their refusal lines, and
> `00-GAP_REGISTER.md` for the 17 rows closed and the 14 filed. Headline numbers:
> **1538 tests · 1517 passed · 21 skipped · 0 failed**, project-wide coverage
> **78.04%**. The skip count rose from 1 to 21 on purpose — that is the suite
> starting to tell the truth, not a regression.
>
> ### G2 — CLOSED with R58 open (2026-09-09)
>
> **Stages 2 and 3's seven lanes plus hotfix #36 are merged and gate G2 is
> closed** at RC head `3ba97f6` — see the G2 block below for all ten manual
> checks and `00-GAP_REGISTER.md`'s new "Filed at gate G2" section for R58–R65.
> All ten checks ran; eight passed cleanly, one (check 8, private
> vulnerability reporting) is a still-open **owner action**, and one (check
> 6, the live stale-channel-manifest replay) is unit-tested only, with the
> live leg deferred to the VM matrix at G3. Running those checks against a real
> `Setup.exe` also surfaced **R58**, a **RELEASE BLOCKER**: the ARP
> `UninstallString` blocks on its own pid via the files-in-use gate, so the
> shipped uninstall path is dead for anyone who no longer has the original
> `Setup.exe` — the common case. Its fix (`rc/p6-fix-uninstall-self-block`) is
> open as **[PR #39](https://github.com/Sigil-build/sigil/pull/39)** (commit
> `48e864f`) but **not yet merged**, and that PR's own work surfaced two more
> rows, **R64** and **R65**. **G3 must not open** until PR #39 merges and the
> VM matrix runs for real against it.
>
> ### Stage 0 (2026-07-28)
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
| 1 | [`05-STAGE-1-security-core.md`](05-STAGE-1-security-core.md) | S1, S2, S3, T1 | 4 parallel | **G1 ✅** |
| 2 | [`06-STAGE-2-security-depth.md`](06-STAGE-2-security-depth.md) | S4, S5, S6, **S7** | 4 parallel | G2 |
| 3 | [`07-STAGE-3-release-surface.md`](07-STAGE-3-release-surface.md) | REL, SUP, DOC | 3 parallel, **overlaps Stage 2** | G2 |
| 4 | [`08-STAGE-4-verification.md`](08-STAGE-4-verification.md) | V1 | solo | G3, G4 |

Estimated calendar: 1 day + 1 week + (4 days ∥ 4 days) + 2 days ≈ **2.5 weeks**.

---

## Lane → finding map

Every row appears **exactly once**. This table is the audit trail; V1 walks it
row by row. It was 46 rows at the audit and is **60** after Stage 1 — see the
note below the table for what changed and why R48 is deliberately unassigned.

| Lane | Model | Findings | Count |
|---|---|---|---|
| `F0` foundation | haiku-4.5 | R20, R40, R41 | 3 |
| `S1` trusted state | opus-5 | R1, R2, R19 | 3 |
| `S2` path containment | opus-5 | R3, R9, R16, R31, R32 | 5 |
| `S3` staged execution | opus-5 | R4, R5, R10, R11, R12, R17 | 6 |
| `T1` test truth | sonnet-5 | R6, R21, R22 | 3 |
| `S4` network + update | opus-5 | R8, R13, R14, R30, R37, R39, **R45, R46, R47, R49** | 10 |
| `S5` residual engine | **opus-5** | R15, R18, R28, R29, R34, R38, **R48 (fix only), R53, R56, R57** | 10 |
| `S6` step hardening + ADRs | opus-5 | R33, R35, R36, **R50, R52, R54** | 6 |
| `S7` signed anchorage | opus-5 | **R44, R51** | 2 |
| `REL` release scaffolding | sonnet-5 | R7, R23, R23a, R24 | 4 |
| `SUP` supply chain | sonnet-5 | R42 | 1 |
| `DOC` docs truth | sonnet-5 | R25, R26, R26a, R27, R41a, R43, **R55** | 7 |
| `V1` verification | opus-5 | re-verifies all | — |
| | | **Total** | **60** |

Sorted check — R1 R2 R3 R4 R5 R6 R7 R8 R9 R10 R11 R12 R13 R14 R15 R16 R17 R18
R19 R20 R21 R22 R23 R23a R24 R25 R26 R26a R27 R28 R29 R30 R31 R32 R33 R34 R35
R36 R37 R38 R39 R40 R41 R41a R42 R43 R44 R45 R46 R47 R48 R49 R50 R51 R52 R53 R54
R55 R56 R57 = 60, no gaps, no repeats.

> **R48 is split: S5 owns the fix, the human partner owns the worst-case number.**
> The row was originally unassigned on the grounds that its deliverable was a
> measurement no agent could produce. Half of that is no longer true.
>
> A **best-case floor of 335 ms** was measured on 2026-08-11 (online, warm cache,
> embedded-signed target; runs 2 and 3 came back in 6–9 ms). That is the happy
> path, and it is already past the ~100 ms at which a UI reads as unresponsive —
> so **the off-thread fix is justified now** and does not wait on anything.
>
> What still cannot be produced from an agent run is the **worst** case: an
> offline, cold-certificate-cache first run on real hardware. The human partner
> measures that. Two traps are recorded in the row itself, both hit while
> producing the floor — a *catalog*-signed Windows binary measures 0 ms and proves
> nothing, and any run under ~200 ms means the setup was wrong.
>
> Do not let a lane quietly absorb this row and "verify" it with a fast run.
>
> **Amended 2026-08-11:** the table above was 46 rows and three of its lanes are
> re-scoped. S5 moved sonnet-5 → **opus-5** (R15 caps the whole
> "silently-unremovable" class, it consumes S1's `ReplayRefusalCode` contract, and
> its own R18 task already pre-flagged an escalation). **S7 is a new lane**: R44
> and R51 are one design — resolve declared roots and declared registry keys from
> the **signed blob** at replay time — and building it twice was the alternative.

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

- [x] Plant `C:\ProgramData\Sigil\<AppId>\uninstall.json` with a `restore_file`
      record targeting `C:\Windows\System32\`. Run an elevated machine-scope
      install **and** an elevated uninstall. Both refuse. *(R1)* — **PASSED.** The
      plant succeeds as the standard user and the file is owned by them; the
      elevated uninstall answers *"uninstall state for 'SigilG1ProbePD' was found
      but REFUSED, not replayed: … the state directory **and the state file**
      failed the provenance check … Nothing was uninstalled."* Both objects are
      named, which is the point — a hardened directory alone would not have caught
      a pre-created file, because `File.WriteAllText` truncates in place and leaves
      the attacker's owner on it. The refusal is **not** reported as an absence,
      and the planted file is still on disk afterwards: nothing acted on it.
- [x] Plant the same file in `%LocalAppData%\Sigil\<AppId>\`. A machine-scope
      operation must not read it at all. *(R1)* — **PASSED.** With the machine
      directory empty for that app id, the elevated machine uninstall reports
      *"no uninstall state found … (expected at
      `C:\ProgramData\Sigil\…\uninstall.json`)"* — naming the machine path, never
      the profile one — and the user-profile plant is untouched.
- [x] Plant `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\<AppId>`
      with `DisplayVersion=0.0.1` and `UninstallString` pointing at a scratch
      exe. Run an elevated machine-scope install of that AppId. The scratch exe
      must **not** run. *(R2)* — **PASSED, and both gates were checked
      independently.** Gate 1: `ScopeProbeOrder(User, elevated:true) = [Machine]`,
      so `Resolve` returns `Found=False` and never sees the entry — while the
      **same key is demonstrably readable from that same elevated process's hive**,
      so the negative is not vacuous. Gate 2: handed the path directly,
      `ClassifyPriorUninstaller` returns `Untrusted` and
      `PriorUninstallerNeedsTrust(User, elevated:true)` is `true`, so the spawn is
      refused even if the first gate were bypassed. Two independent gates was the
      design; both are armed.
- [x] `Setup.exe /allusers /D=C:\Users\Public\evil` is rejected. *(R3)* — **PASSED
      2026-08-11.** Refusal line: *"The install directory 'C:\Users\Public\evil' is
      outside the machine scope root 'C:\Program Files', 'C:\Program Files (x86)'
      (or reaches it through a directory junction). Refusing to install there —
      {install_dir} feeds SYSTEM-level step targets. Nothing was installed."* The
      wizard-collected path is refused identically, and the positive control
      (`/D=C:\Program Files\SigilG1Probe`) still resolves, so the check is not
      vacuous.
- [x] Pre-create the elevated native-runtime cache with a bogus `libSkiaSharp.dll`
      and a valid `.sigil-runtime-complete` marker. The elevated wizard must not
      load it. *(R4)* — **the path in this line was stale**: the R4 fix moved the
      elevated root to `%ProgramData%\sigil-runtime\<sha>\`, not
      `%LocalAppData%\Sigil\runtime\<sha>\`. Attacked at the new path.
      **PASSED.** The standard user pre-creates the derivable content-keyed
      directory, plants a hostile `libSkiaSharp.dll`, a `version.dll` the archive
      does not contain at all, and a valid marker. The elevated bootstrap
      *repairs rather than refuses* — *"repaired the access control list …",
      "took ownership … for BUILTIN\Administrators"* — then *"the cache directory
      … does not match the embedded archive — discarding it and extracting
      again"*. Afterwards the DLL holds the genuine bytes, the planted
      `version.dll` is gone, and the directory is administrator-only. **The
      repair-don't-refuse design is what stops this being a denial of service**:
      one `mkdir` by any user would otherwise block every elevated install.

Plus one probe the plan did not ask for, added because **S3's entire elevated path
rests on it**: does a directory `CreateHardened` produces actually satisfy
`IsAdminOnlyWritable` under a real elevated token? If it does not, every elevated
install refuses. It fails only on a box with `NoDefaultAdminOwner=1`, where
`%ProgramFiles%`'s inherit-only `CREATOR OWNER:(F)` materialises as a concrete
user ACE. Run through the **real** predicate, not a PowerShell replica of it.

- [x] Elevated ACL probe: `IsAdminOnlyWritable(dir)` and `IsTrustedFile(file)` on
      a fresh `%ProgramFiles%` directory, on a `CreateHardened` one, and — as the
      non-vacuity control — on `%ProgramData%` itself, which must be **rejected**.
      **PASSED on this machine.** A directory an elevated process creates under
      `%ProgramFiles%`, and a file it writes there, are both owned by
      `BUILTIN\Administrators` and satisfy the predicate; `CreateHardened`'s output
      satisfies `IsAdminOnlyWritable` **and** `IsTrusted`; and `%ProgramData%`
      itself is correctly **rejected**, so the predicate is discriminating rather
      than always-true. **This is one machine's answer, not a proof.** A box with
      `NoDefaultAdminOwner=1` makes the elevating admin the owner instead, at which
      point `%ProgramFiles%`'s inherit-only `CREATOR OWNER:(F)` materialises as a
      concrete user ACE and both halves fail. Re-run the probe on any machine where
      elevated installs start refusing, before debugging anything else.

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

**G1 CLOSED 2026-08-11** at RC head `f300b39`. All five attacks and the ACL probe
refused as designed — 16 harness checks, 0 failures.

Three things this gate does **not** prove, recorded so nobody reads more into it:

1. **No real `Setup.exe` was attacked.** The victim half drove the engine entry
   points directly, because this box cannot AOT-publish. The end-to-end
   pack → `Setup.exe` → attack path remains CI/VM territory (**G3**).
2. **The ACL result is one machine's.** See the probe entry above.
3. **The attacks confirm the fixes hold; they do not confirm the fixes are
   complete.** R16's clause 3 is unimplemented and R22's guards are unproven —
   both stated in `00-GAP_REGISTER.md`, and neither is something these five
   attacks would have caught.

**Residue.** The probe leaves `C:\ProgramData\sigil-runtime` owned by
Administrators with a protected DACL — production behaviour, but it means the
standard-user cleanup cannot remove it. That the unprivileged delete *fails* is
itself confirmation the hardening works, and it is register row **R50** in
miniature.

### G2 — after Stages 2 and 3

Merge order: **S4 → hotfix #36 → S5 → S6 → S7 → REL → SUP → DOC → runbook #35**
(hotfix #36 inserted ahead of S5 — see Trap 0 in `10-G2_G3_RUNBOOK.md`). All
merged, RC head `3ba97f6` — shas and rows-closed per PR in
`00-GAP_REGISTER.md`'s "Stage 2/3 outcome" table.

**S7 is fourth deliberately.** It rebases onto S5's `UninstallEngine` outcome
handling and S6's `ScopeLayout` root set, and where S5 and S7 must agree on a
shape in `ReplayAnchor`, **S7 adapts to what S5 landed**. DOC is last because
R55, DOC.2 and the `uninstaller.md` caveat all describe behaviour the earlier
lanes change.

- [x] Copy-paste the silent-install line from the **corrected**
      `docs/guides/parameters.md` into a real `Setup.exe` — it succeeds *(R26)*
      — **PASS**, CI-built `Setup.exe`, exit `0`, payload/registry/ARP all
      landed; see check 1 in `00-GAP_REGISTER.md`. Running its cleanup step
      (the registered `UninstallString`) is what surfaced **R58**.
- [x] `dotnet restore --locked-mode` succeeds from a clean clone *(R23a)* —
      **PASS** on the post-DOC-merge RC.
- [x] `sigil init --template full` produces a manifest that packs *(R30)* —
      **PASS**, `sigil init --template full-config` (the real flag spelling)
      then `sigil pack` exits `0`; negative control on the pre-R30 signing-key
      shape correctly fails `SIG0325`.
- [x] A manifest with `source: { url: "http://…" }` **fails** to pack *(R8)* —
      **PASS**, `SIG0323`.
- [x] A manifest with `updates: { manifestUrl: "http://…" }` **fails** to
      pack *(R14)* — **PASS**, `SIG0324` (doubly enforced by schema `SIG0010`).
- [ ] A replayed stale signed channel manifest is rejected *(R13)* — unit-tested
      by S4's `UpdateFreshnessTests` only; the live `Setup.exe /Update` replay
      against a hosted channel manifest is **deferred to the VM matrix at G3**,
      left unticked here on purpose.
- [x] `THIRD-PARTY-NOTICES.md` names Skia, ANGLE, HarfBuzz, and libsodium
      explicitly *(R23)* — **PASS**.
- [ ] `SECURITY.md` exists and GitHub private vulnerability reporting is on
      *(R23)* — `SECURITY.md` **present**; private vulnerability reporting
      confirmed **OFF** via the API — **owner action still open**, left
      unticked here on purpose.
- [x] `grep -rn "0\.0\.1-alpha" --include='*.cs' --include='*.csproj' --include='*.yml' .`
      returns **nothing** *(R24)* — **PASS** on a clean clone.
- [x] The vulnerability scan ran; its findings are recorded as fixed or
      accepted *(R42)* — **PASS**, "no vulnerable packages" for every project.

**G2 CLOSED 2026-09-09** at RC head `3ba97f6`. All ten checks ran; eight
tick clean, one (check 6's live half) is deferred to G3 by design, and one
(check 8's private-vulnerability-reporting half) is a repo-owner action that
does not block the gate itself. Running the checks against a real `Setup.exe`
surfaced four defects, filed as **R58–R61** in `00-GAP_REGISTER.md`; **R58 is
release-blocking and its fix has not yet merged** — see the status note at the
top of this document. Landing R58's fix ([PR #39](https://github.com/Sigil-build/sigil/pull/39))
surfaced two more rows, **R64** and **R65**. **G3 must not open until R58's
fix (PR #39, `rc/p6-fix-uninstall-self-block`) merges and the VM matrix runs
for real against the fixed uninstall path — R64 is precisely about that
matrix's own coverage gaps, so its fix belongs in the same G3 prerequisite
check as R58.**

### G3 — after Stage 4

- [ ] `wrapper-vm-tests.yml` run **for real**, green, against non-vacuous tests
      *(R58, R64 — ten `SIGIL_VM_*` toggles were declared, four read by tests at
      the RC base; five drove no test at all: `SIGIL_VM_SCOPE`,
      `SIGIL_VM_SCOPE_MATRIX`, `SIGIL_VM_ARP_VALUES`, `SIGIL_VM_CLOSEAPPS`
      (R58's own P6 leg) and `SIGIL_VM_DOUBLE_INSTALL` — each had to either
      drive a real test or be removed before this run could be trusted.
      `SIGIL_VM_UNINSTALL_SURVIVE` is genuinely fixed: PR #39's
      `ArpUninstallStringTests` runs behind the workflow's
      `SIGIL_VM_UNINSTALL_SURVIVE: "1"`, verified not vacuous)*.

      **Resolved via the "or be removed" branch by
      [PR #41](https://github.com/Sigil-build/sigil/pull/41) (R64, R66, R68).**
      All five orphans are **REMOVED**, not wired: wiring any of them meant
      inventing the test it advertised, which is a coverage decision rather
      than a workflow edit. Consequences to read before ticking this box:
      - the `currentuser × allusers` job matrix went with
        `SIGIL_VM_SCOPE`/`SIGIL_VM_SCOPE_MATRIX` — no test read the scope, so
        both legs ran the identical per-user suite twice (in run
        `34361541578` they failed the same 11 tests for the same reasons).
        There is now **one** leg, `vm (install matrix)`; the old check names
        `vm (currentuser)` / `vm (allusers)` no longer exist.
      - **machine-scope (`/allusers`) end-to-end install is UNCOVERED**, as
        are double-install idempotency, real `manifest.App.*` ARP value
        assertions, and the P6 `/closeapps` + setup-mutex legs. The workflow
        header now lists them as gaps instead of advertising them.
        **R64 stays OPEN for exactly those**, rescoped to the coverage itself
        rather than the env vars — so a green run of this box means "every leg
        the matrix claims, ran", not "every scope is covered".
      - PR #41 also fixed why the first real run was worthless at all: the
        fixtures had rotted (**R66**) and the P12 job could never build
        (**R68**). A green run therefore also requires **R67**'s fix
        ([PR #40](https://github.com/Sigil-build/sigil/pull/40), open) for the
        `vm (p11 system steps)` leg.
      Run URL → `______`
- [ ] The VM matrix runs on a schedule or on merge, not only on demand
      *(**met on the merge half** by
      [PR #41](https://github.com/Sigil-build/sigil/pull/41): `wrapper-vm-tests.yml`
      now has a `push` trigger on `main` and `release/**`, so every merge into
      the RC branch runs the matrix, plus `concurrency` cancel-in-progress. The
      weekly `schedule` in the same file is **default-branch-only** — GitHub runs
      `schedule` from the default branch's copy of the workflow — so it gives
      `main` a floor once the file reaches `main` and does **not** cover
      `release/**`. Tick this on the `push` half; do not read the cron as
      release-branch coverage.)*
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
| T1  | `rc/t1-test-truth` | ☑ | [#23](https://github.com/Sigil-build/sigil/pull/23) | ☑ `86c2799` | **G1 ✅** |
| S4  | `rc/s4-network-update` | ☑ | [#28](https://github.com/Sigil-build/sigil/pull/28) | ☑ `3e94b8b` | G2 |
| HOTFIX | `rc/s1-fix-provenance-fixture` | ☑ | [#36](https://github.com/Sigil-build/sigil/pull/36) | ☑ `0c092d1` | G2 |
| S5  | `rc/s5-residual-engine` | ☑ | [#29](https://github.com/Sigil-build/sigil/pull/29) | ☑ `50e5de4` | G2 |
| S6  | `rc/s6-step-hardening` | ☑ | [#30](https://github.com/Sigil-build/sigil/pull/30) | ☑ `3be9187` | G2 |
| S7  | `rc/s7-signed-anchorage` | ☑ | [#31](https://github.com/Sigil-build/sigil/pull/31) | ☑ `2e32c83` | G2 |
| REL | `rc/rel-scaffolding` | ☑ | [#32](https://github.com/Sigil-build/sigil/pull/32) | ☑ `f9d3af5` | G2 |
| SUP | `rc/sup-supply-chain` | ☑ | [#33](https://github.com/Sigil-build/sigil/pull/33) | ☑ `4dc7820` | G2 |
| DOC | `rc/doc-truth` | ☑ | [#34](https://github.com/Sigil-build/sigil/pull/34) | ☑ `50da43c` | G2 |
| RUNBOOK | `rc/doc-g2-runbook` | ☑ | [#35](https://github.com/Sigil-build/sigil/pull/35) | ☑ `3ba97f6` | **G2 ⚠️ (R58 open)** |
| DOC-G2 | `rc/doc-g2-close` | ☑ | [#37](https://github.com/Sigil-build/sigil/pull/37) | ☑ `da792fb` | **G2 ✅** |
| P6-FIX | `rc/p6-fix-uninstall-self-block` | ☑ | [#39](https://github.com/Sigil-build/sigil/pull/39) | ☑ `102ea3f` | G3 (R58 — e2e still unrun) |
| VM-FIX-B | `rc/vm-fix-p11-anchoring` | ☑ | [#40](https://github.com/Sigil-build/sigil/pull/40) | ☑ `c71bd8c` | G3 (R67) |
| VM-FIX-A | `rc/vm-fix-fixtures` | ☑ | [#41](https://github.com/Sigil-build/sigil/pull/41) | ☑ `b07021e` | G3 (R64 ⚠️, R66, R68) |
| V1-FIX | `rc/v1-sbom-and-kiosk` | ☑ | [#42](https://github.com/Sigil-build/sigil/pull/42) | ☐ **open** | G3 (R69, R70) |
| VM-FIX-B2 | `rc/vm-fix-p11-round2` | ☑ | [#44](https://github.com/Sigil-build/sigil/pull/44) | ☐ **open** | G3 (R75) |
| VM-FIX-A2 | `rc/vm-fix-fixtures-round2` | ☑ | ☐ pending | ☐ | G3 (install-matrix fixtures; R74 skips) |
| V1-DOCS | `rc/v1-register-status` | ☑ | [#43](https://github.com/Sigil-build/sigil/pull/43) | ☐ | G3 (V1.1 — R71–R75 filed) |
| V1  | `rc/v1-verification` | ◐ V1.1 + V1.4 done | ☐ | ☐ | G3/G4 |

The hotfix row (`rc/s1-fix-provenance-fixture`, [#36](https://github.com/Sigil-build/sigil/pull/36)
→ `0c092d1`) is listed above in merge order, between S4 and S5, because that is where
it had to land — see `10-G2_G3_RUNBOOK.md` Trap 0.

Two things this table cannot show, so they are said here. **The orchestrator cannot
merge lane PRs**; every ☐ in the "Merged" column is waiting on the repository owner.
And every row's own disposition now lives on its register row as a
`STATUS (V1.1, …)` line — this table is a summary, not the record. That distinction
is **R73**.
