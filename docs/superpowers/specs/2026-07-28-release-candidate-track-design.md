# Design — release-candidate track for Sigil v0.1.0-alpha

Date: 2026-07-28 · Author: brainstorming session · Status: **approved, not yet executed**

Closes the gap between `main` @ `1be494c` and a defensible first public release.
Input is the audit in [`docs/plan/release/00-GAP_REGISTER.md`](../../plan/release/00-GAP_REGISTER.md)
— 46 findings, 7 of them release blockers, five of which are local
privilege-escalation paths in software that elevates to admin.

This document is the **design**. The executable plan (orchestrator + five stage
documents) is produced from it in the planning step that follows; see
[§8 Deliverables](#8-deliverables).

---

## 1. Problem

The audit established three things:

1. **Five local privilege-escalation paths**, four exploitable against a
   correctly authored manifest — R1 (elevated replay of user-writable install
   state, on the *install* path as well as uninstall), R2 (elevated spawn of an
   exe path read from HKCU), R3 (`/D=` sets `install_dir` anywhere, and
   privileged steps resolve targets from it), R4 (elevated process trusts a
   per-user native DLL cache), R5 (predictable-path verify→exec TOCTOU in the
   web stub).
2. **The suite that should have caught them reports green while proving almost
   nothing.** Measured: 1097 tests, 1096 passed, 1 skipped — but ~24 of those
   "passes" are early `return` statements. Every real install, uninstall, scope,
   upgrade, and `Setup.exe`-stamping proof is vacuous, and the workflow that
   runs them properly is `workflow_dispatch` only.
3. **The release surface does not exist.** No release workflow, no tag, no
   signed artifact; the one CI artifact uploads `sigil.exe` without the two
   native DLLs it needs to run. Docs teach a silent-install command the parser
   rejects.

Compounding all three: `dotnet format --verify-no-changes` fails on `main`
(28 files), PR-title lint and schema-lockstep are documented as CI-enforced but
were never installed — the workflow implementing all three
(`_agent-setup/github-workflows/pr-guards.yml`) exists in the repo and was never
copied into `.github/workflows/`.

Nothing here is architectural. All 46 are missing checks, missing files, or
missing enforcement in otherwise sound code — which is why the estimate is weeks
and not months.

## 2. Decisions taken

| Decision | Choice | Rationale |
|---|---|---|
| **Scope** | All 46 rows | Chosen over "blockers + should-fix". Post-v1 items are hardening rather than holes, but the RC clears them too. IDs run R1–R43 plus the three suffixed rows R23a, R26a, R41a — 46 in total. |
| **Integration** | PR per lane into a release-candidate branch | Matches how PRs #12–#15 already ran; gives `pr-guards` a per-lane gate; leaves a reviewable record for security fixes, where review matters most. |
| **Stale branches** | Archive tag, then delete — **done** | They were squash-merge leftovers, not lost work — `task/p13-verification` was byte-identical to `main`, the rest showed main *ahead*. 15 `archive/*` tags pushed to origin, then the branches deleted. The remote is now `main` + the RC only. |
| **Staging** | By dependency and risk — 5 stages, 12 lanes | Chosen over by-subsystem (continuous conflicts on `InstallSession.cs`, `ci.yml`, the schema) and over two mega-stages (a 25-row stage is unreviewable, and it flattens the distinction between an escalation fix and a docs rename). |
| **R43 (stale plan docs)** | Forward-pointer banner, not a rewrite | `AGENTS.md` declares `docs/plan/*` read-only history. A banner corrects the reader without falsifying the record. |

**Explicitly rejected:** a bare `0.1.0` tag (claims confidence nobody has
earned; the manifest schema will move once real users push on it), and a
private-tag-only release (the remote, org, and CI badges already exist, so the
marginal secrecy is small — the half worth keeping is the discipline of not
announcing).

## 3. Branch model

- **RC branch** `release/v0.1.0-alpha`, cut from `main` @ `1be494c`.
- **Lane branches** `rc/<lane>-<slug>` (e.g. `rc/s1-trusted-state`), cut from
  the RC, never from `main`.
- Each lane opens a **PR into the RC**. `pr-guards`, installed in Stage 0, gates
  every one.
- **One PR merges RC → main** after Stage 4 passes, then the tag.
- The stale remotes got `archive/<name>` lightweight tags pushed first, then the
  branches were deleted. **Completed 2026-07-28.** Note for the record: the
  audit's "15 stale branches" figure was wrong — 7 had already been deleted on
  GitHub and only stale local remote-tracking refs made them look live. The true
  count was 8. Always `git fetch --prune` before reading `git branch -r`.

Two accepted consequences, named so they are not surprises:

- `main` sits behind the RC for ~2–3 weeks. An urgent `main` fix must be
  cherry-picked onto the RC.
- The final RC → main merge is large. That is the price of having a single
  coherent "is the RC ready" state to evaluate.

## 4. Stages and lanes

12 lanes. Every one of the 46 rows appears in **exactly one** lane; the
orchestrator carries the map that proves none was dropped.

### Stage 0 — Foundation · solo, ~1 day

Runs with **nothing else in flight**: the `dotnet format` backlog rewrites 28
files across `src/` and `tests/`, so any concurrent lane eats a conflict. This
is a structural reason for the stage boundary, not a scheduling preference.

| Lane | Model | Rows |
|---|---|---|
| `F0` format backlog; install `pr-guards.yml` + `.claude/`; drop `_agent-setup/`; `.gitignore` | haiku-4.5 | R20, R40, R41 |

### Stage 1 — Security core · 4 lanes parallel, ~1 week

| Lane | Model | Rows |
|---|---|---|
| `S1` trusted state — state provenance + ACL, drop opposite-scope fallback, anchor journal replay, HKLM-only probe, verify prior uninstaller, hostile-JSON fails closed | opus-5 | R1, R2, R19 |
| `S2` path containment — reject out-of-root `/D=`, one `PathContainment` helper, anchor privileged step targets, step destinations, `/TR` quoting, INI CRLF | opus-5 | R3, R9, R16, R31, R32 |
| `S3` staged execution — admin-only native cache with per-file verification, randomized admin-only staging, held handles, Authenticode before launch, download size caps, revocation checking | opus-5 | R4, R5, R10, R11, R12, R17 |
| `T1` test truth — `Assert.Skip` everywhere, stage the runtime in the `build` job, per-assembly coverage floors + assembly allowlist, pre-flight guards on all VM jobs | sonnet-5 | R6, R21, R22 |

### Stage 2 — Security depth · 3 lanes, ~4 days

| Lane | Model | Rows |
|---|---|---|
| `S4` network + update — https on `source.url` and `manifestUrl`, signed manifest freshness (+ADR), template signing key, version-floor edge, verify-before-parse | opus-5 | R8, R13, R14, R30, R37, R39 |
| `S5` residual engine — uninstall failure reporting + state retention, secret command-line channel, `.sigil-bak` lifecycle (+ADR), de-elevation fallback, mutex fail-open, RM session key | sonnet-5 | R15, R18, R28, R29, R34, R38 |
| `S6` step hardening + ADRs — explicit XXE posture, `json_edit` value typing, `com_register` in-process trust decision (+ADR) | opus-5 | R33, R35, R36 |

### Stage 3 — Release surface · 3 lanes, **parallel to Stage 2**, ~4 days

Shares no files with Stage 2, which is what compresses the calendar to ~2.5
weeks.

| Lane | Model | Rows |
|---|---|---|
| `REL` scaffolding — `release.yml` on `v*`, SECURITY/CHANGELOG/THIRD-PARTY-NOTICES, version single source, lockfiles + `NuGet.config`, fix the artifact to ship its sibling DLLs | sonnet-5 | R7, R23, R23a, R24 |
| `SUP` supply chain — dependabot, `--vulnerable` CI step, SBOM, preview-dependency call | sonnet-5 | R42 |
| `DOC` docs truth — README rewrite, the rejected silent-install syntax, `setup-exe-reference.md`, output filenames, architecture-overview corrections, ADR renumber + CODEOWNERS, plan-doc banners | sonnet-5 | R25, R26, R26a, R27, R41a, R43 |

### Stage 4 — Verification · 1 lane, ~2 days

| Lane | Model | Rows |
|---|---|---|
| `V1` adversarial re-verify of every row, VM matrix run for real, release dry-run, go/no-go | opus-5 | all 46 |

### File ownership rules

Three rules keep the parallelism honest:

- `ci.yml` belongs to `T1` in Stage 1; `REL` rebases onto it in Stage 3.
- `schemas/sigil-schema.json` and `docs/manifest-reference.md` belong to `S4`
  alone (lockstep surface — the whole chain moves in one commit, and the
  step-`type` enum appears in multiple places in the schema). `DOC` must not
  touch them.
- `InstallSession.cs` is `S1`'s exclusively. `S3` reports rather than edits if
  the update temp path needs to move.

## 5. Gates

Each gate is a merge point on the RC with a check that can actually be run.

- **G0** (after Stage 0) — `dotnet format --verify-no-changes` exits 0 on the
  RC; `git ls-files .claude` non-empty; `_agent-setup/` gone. **Proof of gate:**
  open a throwaway PR titled `broken title` and watch `pr-guards` fail it. A
  gate nobody has seen fail is not a gate.
- **G1** (after Stage 1) — merge S1 → S2 → S3 → T1. Four attacks run by hand as
  a standard user, each of which must be refused with a log line: plant
  `uninstall.json` in `%ProgramData%` and in `%LocalAppData%`; plant an HKCU ARP
  entry with an `UninstallString`; pass `/D=C:\Users\Public\evil`; pre-create
  the native-runtime cache with a valid completion marker. Plus `dotnet test`
  now reports a **non-zero skip count**, recorded as the honest size of the
  untested surface.
- **G2** (after Stages 2+3) — merge S4 → S5 → S6 → REL → SUP → DOC. Copy-paste
  the silent-install line from the corrected docs into a real `Setup.exe` and
  watch it succeed; `dotnet restore --locked-mode` from a clean clone;
  `sigil init --template full` produces a manifest that packs.
- **G3** (after Stage 4) — VM matrix run for real with its run URL recorded; a
  release dry-run producing a signed artifact that **runs on a clean machine**
  (the sibling-DLL trap from R7); `02-READINESS_REPORT.md`'s Definition of Done
  fully ticked or explicitly deferred.
- **G4** — RC → main PR, then tag `v0.1.0-alpha`.

## 6. Failure handling

- A red lane holds only its **dependents**. S1 failing holds S5; it does not
  hold REL or DOC.
- Nothing is fixed forward on the RC. The owning lane branch is reopened with
  the failure attached.
- A lane that discovers a gap **not** in the register **stops and files a new
  row** rather than widening its own scope. The orchestrator triages it into a
  stage.
- Lanes never merge their own PRs; the orchestrator does, in the stated order.

## 7. Verification obligations

**The rule enforced hardest:** every security fix lands with a negative test,
and the orchestrator verifies it by checking out the parent commit and
confirming **the test actually fails there**. A negative test that passes before
the fix is not a test. This track exists because ~24 tests were passing while
asserting nothing; accepting a new test on trust would repeat precisely the
failure being fixed.

Per-lane definition of done — the standing triple plus lane-specific checks:

```
dotnet build Sigil.slnx -c Release        # clean
dotnet test Sigil.slnx -c Release         # green, skip count reported
dotnet format Sigil.slnx --verify-no-changes   # clean (enforced in CI after G0)
```

**Honesty requirement.** Native AOT publish **fails on this machine**
(`vswhere.exe` unresolvable, MSVC linker `MSB3073` / exit 123), so anything
needing a real `Setup.exe` is CI-only. A lane must say which checks it could not
run rather than implying a green suite — the same standard `AGENTS.md` §2
already sets.

## 8. Deliverables

Produced by the planning step that follows this design, in `docs/plan/release/`:

| File | State | Role |
|---|---|---|
| `00-GAP_REGISTER.md` | exists | Findings source of truth. Amended only when a lane files a new row. |
| `01-FIX_PLAN.md` | exists | Gets a **superseded** banner pointing at `03`. Kept as the audit record. |
| `02-READINESS_REPORT.md` | exists | Decision doc. Stage 4 ticks its Definition of Done. |
| `03-RC_ORCHESTRATION.md` | new | Branch policy, stage index, gates, lane→row map, progress table. Holds **no** lane detail, so it stays readable as stage docs churn. |
| `04-STAGE-0-foundation.md` | new | Lane F0: scope, prompt, gate G0. |
| `05-STAGE-1-security-core.md` | new | Lanes S1–S3, T1. |
| `06-STAGE-2-security-depth.md` | new | Lanes S4–S6. |
| `07-STAGE-3-release-surface.md` | new | Lanes REL, SUP, DOC. |
| `08-STAGE-4-verification.md` | new | Lane V1 and gates G3/G4. |

Repository actions at track start — **both complete as of 2026-07-28**:

1. ✅ Pushed `archive/<name>` tags for the stale remote branches, then deleted
   them. `git ls-remote --heads origin` now returns `main` and
   `release/v0.1.0-alpha` only.
2. ✅ Cut and pushed `release/v0.1.0-alpha` from `main` @ `1be494c`.

## 9. Success criteria

The track is done when:

- All 46 register rows are demonstrated fixed, or explicitly deferred with a
  one-line justification recorded in `02-READINESS_REPORT.md`. No row is
  silently dropped.
- The four hand-run privilege-escalation attacks from G1 are refused.
- `dotnet test` reports a non-zero, honest skip count, and no test soft-skips by
  returning early.
- `wrapper-vm-tests.yml` has run green against non-vacuous tests, with its run
  URL recorded, and runs on a schedule or on merge rather than only on demand.
- A tagged release produces a signed, checksummed artifact that runs on a clean
  machine.
- `v0.1.0-alpha` is tagged and published **unannounced**, with the known-limitations
  section from `02-READINESS_REPORT.md` as its release notes.

## 10. Out of scope

- Delta updates and the update SDK — deferred by
  `docs/architecture/adr-010-delta-update-deferral.md`; the README claim gets
  removed rather than the feature built (R25).
- Cross-platform packaging. Windows-only is a stated non-goal for v1.
- Raising the aspirational per-assembly coverage targets (Core 80 %, Signing
  85 %). `T1` sets enforced floors at **current measured values rounded down** —
  a ratchet, not a cliff. Reaching the targets is post-v1 work.
- The launch announcement. Held until a `v0.2.0` with at least one external
  user's successful install and a scheduled green VM matrix.
