# Release gap register — first public release

Audit date: **2026-07-28**. Tree audited: `main` @ `1be494c`, clean working tree
(only `docs/plan/RELEASE_AUDIT_PROMPT.md` untracked).

This file is the single source of truth for what stands between `main` and a
first public release. It supersedes the prior review's notes: every prior
finding is re-verified here against current line numbers, and prior claims that
turned out **narrower than stated** are marked as such.

Scope of this audit: the full `src/` tree (300 C# files), `.github/workflows/`,
`docs/`, and the release surface. The P0–P13 feature-parity track — which had
never had a security review — was the main effort.

## Measured facts (this machine, Windows 11, .NET SDK 10.0.302)

| Check | Command | Result |
|---|---|---|
| Release build | `dotnet build Sigil.slnx -c Release` | **0 warnings, 0 errors**, 41.5 s |
| Test suite | `dotnet test Sigil.slnx -c Release --no-build` | **1097 total: 1096 passed, 1 skipped, 0 failed** |
| Format gate | `dotnet format Sigil.slnx --verify-no-changes` | **FAILS — exit 2, 28 of 465 files need formatting** |
| AOT publish | `dotnet publish src/SigilBuild.Cli -c Release -r win-x64 -p:PublishAot=true` | **FAILS on this box** — `vswhere.exe` not found / MSVC linker unresolvable (`MSB3073`, exit 123) |
| VM matrix | `wrapper-vm-tests.yml` | **NOT RUN** — `workflow_dispatch` only; not run here (needs admin + a disposable VM) |

Two numbers to hold onto: the suite reports **1096 passes with a single skip**,
and that single skip is the only honest one — see **R6**. The plan docs'
"527 tests green" (`ORCHESTRATION_PLAN.md:6`) is stale by half.

## Stage 1 outcome (2026-08-11) — these numbers supersede the table above

Stage 1 closed 17 rows across four lanes, merged in the order S1 → S2 → S3 → T1.
RC head `86c2799`. **Measured on CI, not locally** — the local box cannot
AOT-publish, so it skips several packaging tests CI runs.

| Check | Audit (2026-07-28) | RC `86c2799` (CI) |
|---|---|---|
| Tests | 1097 total · 1096 passed · **1 skipped** | **1538 total · 1517 passed · 21 skipped · 0 failed** |
| Format gate | fails, 28 files | passes (Stage 0) |
| `SigilBuild.Core` coverage | 63.89% | **69.51%** (floor 69%) |
| `SigilBuild.Packaging` coverage | 72.00% | **86.51%** (floor 72%) |
| `SigilBuild.Signing` coverage | 68.79% | **68.79%** (floor 68%) |
| `SigilBuild.Wrapper.Core` coverage | 77.64% | **79.47%** (floor 79%) |
| Project-wide union | 74.74% | **78.04%** (floor 77%) |

**The skip count is the point, not a regression.** 1 → 21 is not the suite getting
worse; it is the suite starting to tell the truth. Roughly 28 tests were reporting
`Passed` while asserting nothing. 21 is the honest size of the untested surface,
and every one of them names its missing precondition.

Coverage floors were re-pinned upward on the integrated tree at the T1 merge, at
the measured value **rounded down** — a ratchet, not the aspirational targets in
`AGENTS.md`. Nothing was lowered.

| Row | Lane | Merge |
|---|---|---|
| R1, R2, R19 | S1 | `31ae3a3` (#19), `5b65712` (#20) |
| R3, R9, R16, R31, R32 | S2 | `4505b24` (#21) |
| R4, R5, R10, R11, R12, R17 | S3 | `72d6437` (#22) |
| R6, R21, R22 | T1 | `86c2799` (#23) |

Three of those are **not** simply "fixed": **R9** is closed but the row's own text
is wrong (see its status note), **R16** is partial (clause 3 unimplemented), and
**R22**'s guards cannot be proven until the VM matrix runs at G3.

Stage 1 also produced **fourteen new rows** — R44, filed mid-stage, and R45–R57 in
the "Filed during Stage 1" section below. Eleven **over-refusals** were caught and
fixed before merge: cases where a security fix refused legitimate behaviour, the
worst being that **both shipped exe-wrapper examples aborted at their first
`file_copy`** while CI stayed green, because the example gate is schema-only.

## Stage 2/3 outcome (2026-09-09) — these numbers supersede the table above

Stages 2 and 3 closed 40 rows across seven lanes plus one hotfix, merged in the
order **S4 → hotfix #36 → S5 → S6 → S7 → REL → SUP → DOC → runbook #35**. RC
head `3ba97f6`. **Measured on CI** — the RC push at `3ba97f6` reported success
on every required check (`build`, `aot publish (win-x64)`, `dotnet format`,
`schema / docs lockstep`, `conventional-commit PR title`, `gitleaks`). The
local seven-lane pre-flight run on 2026-09-08 (before any of these PRs
actually merged) reported **1694 total · 1674 passed · 20 skipped · 0
failed** — evidence that the merge chain was structurally sound (zero git
conflicts end to end), not a substitute for each PR's own CI numbers, which
this pass does not re-tabulate.

| PR | Lane | Merge | Rows closed |
|---|---|---|---|
| [#28](https://github.com/Sigil-build/sigil/pull/28) | S4 | `3e94b8b` | R8, R13, R14, R30, R37, R39, R45, R46, R47, R49 |
| [#36](https://github.com/Sigil-build/sigil/pull/36) | hotfix | `0c092d1` | none — fixes the R1 test-fixture regression that put the RC red at `3e94b8b` (`CreateHardened` hardens missing ancestors; see `10-G2_G3_RUNBOOK.md` Trap 0) |
| [#29](https://github.com/Sigil-build/sigil/pull/29) | S5 | `50e5de4` | R15, R18, R28, R29, R34, R38, R48 (fix only), R53, R56, R57 |
| [#30](https://github.com/Sigil-build/sigil/pull/30) | S6 | `3be9187` | R33, R35, R36, R50, R52, R54 |
| [#31](https://github.com/Sigil-build/sigil/pull/31) | S7 | `2e32c83` | R44, R51 |
| [#32](https://github.com/Sigil-build/sigil/pull/32) | REL | `f9d3af5` | R7, R23, R23a, R24 |
| [#33](https://github.com/Sigil-build/sigil/pull/33) | SUP | `4dc7820` | R42 |
| [#34](https://github.com/Sigil-build/sigil/pull/34) | DOC | `50da43c` | R25, R26, R26a, R27, R41a †, R43, R55 |
| [#35](https://github.com/Sigil-build/sigil/pull/35) | runbook | `3ba97f6` | none — docs-only, the G2/G3 human runbook |

† **R41a is listed here but was not actually closed.** #34 documented it; the two
NuGet IDs remain unreserved, which is an owner action on the G4 checklist. Corrected
by the V1.1 walk — see R41a's own status line. This is exactly the failure mode a
merge table has and a per-row status line does not, which is why **R73** now exists.

### G2 check results (2026-09-09)

Full command-level evidence for checks 1, 3, 4 and 5:
`.superpowers/sdd/2026-09-08-g2-release-prep/g2-checks-report.md` (gitignored,
not part of this PR).

1. **PASS** — the corrected `docs/guides/parameters.md` silent-install line
   installs cleanly against a real, CI-built `Setup.exe` *(R26)*.
2. **PASS** — `dotnet restore Sigil.slnx --locked-mode` succeeds from a clean
   clone of the post-DOC-merge RC *(R23a)*.
3. **PASS** — `sigil init --template full-config` produces a manifest that
   packs, exit `0` *(R30)*.
4. **PASS** — `parameters.<name>.source.url: http://…` fails to pack —
   `SIG0323` *(R8)*.
5. **PASS** — `updates.manifestUrl: http://…` fails to pack — `SIG0324` (and,
   doubly, schema `SIG0010`) *(R14)*.
6. **Unit-tested, not live.** S4's `UpdateFreshnessTests` cover the stale/replayed
   channel-manifest rejection in-process; a live `Setup.exe /Update` replay
   against a hosted, signed channel manifest is deferred to the VM matrix at G3
   *(R13)*.
7. **PASS** — `THIRD-PARTY-NOTICES.md` names Skia, ANGLE, HarfBuzz, and
   libsodium explicitly *(R23)*.
8. **File present; repo setting still open.** `SECURITY.md` exists on the RC.
   GitHub private vulnerability reporting is confirmed **OFF**
   (`gh api repos/Sigil-build/sigil/private-vulnerability-reporting` →
   `{"enabled":false}`) — this half is a **repo-owner action**, not something
   any lane PR can flip *(R23)*.
9. **PASS** — `grep -rn "0\.0\.1-alpha" --include='*.cs' --include='*.csproj' --include='*.yml' .`
   returns nothing on a clean clone *(R24)*.
10. **PASS** — the vulnerability-scan CI step ran (confirmed, not merely
    assumed from a green build) and reported "no vulnerable packages" for
    every project *(R42)*.

Four defects were found while running checks 1, 3, 4 and 5. **R58** (below) is
release-blocking; **R59** is fixed in this same PR; **R60** and **R61** are
filed for a later stage. R58's fix opened as **[PR #39](https://github.com/Sigil-build/sigil/pull/39)**
(commit `48e864f`, not yet merged) and its own work surfaced **R64** and
**R65**. The first real `wrapper-vm-tests.yml` run — the one R64 had just
shown would be worth less than it looked — then surfaced **R66–R68**. See the
"Filed at gate G2" section below for all nine rows.

## V1.1 register walk (2026-09-09) — how to read the per-row STATUS lines

Task **V1.1** (Stage 4, lane V1) walked **all 71 rows** of this register against the
tree at `102ea3f` and against the authoritative CI run
**[34362414470](https://github.com/Sigil-build/sigil/actions/runs/34362414470)**
(`ci`, push, `102ea3f`, conclusion `success`). Per-test outcomes were read out of that
run's `test-results` artifact — 12 `.trx` files — **not out of PR bodies**. Full
report: `.superpowers/sdd/2026-09-08-g2-release-prep/v1-1-register-walk.md`
(gitignored, not part of this PR).

**Every row now carries a `> **STATUS (V1.1, 2026-09-09):**` line.** Until this pass,
**49 of the 68 walked rows had no per-row status at all** — 40 of them rows closed by
Stages 2 and 3, whose closure existed only as a cell in the merge table above. That
gap is itself filed, as **R73**. Where a row already carried an older `STATUS —`
note, the V1.1 line sits **above** it: the V1.1 line is the current disposition, the
older note is the history.

How to read one:

- **Evidence T** — a named test that exists in the tree **and** reported `Passed` on
  run `34362414470`. The count in parentheses is that test class's `Passed` count on
  that run.
- **Evidence M** — no test: a command re-run during the walk with its output, or a
  G1/G2 ceremony result.
- **Evidence D** — an explicit **written** deferral (a register note, a code
  `<remarks>`, or an ADR). Naming *where* the justification is written is part of the
  status; a decision that exists only in someone's head is not closed.
- **Evidence C — claim-only.** The closure rests on nothing beyond a PR body. Two
  rows are in this state: **R22** and **R38**.
- **"Narrower than recorded"** flags the 16 rows the walk found closed on less
  evidence than the record implies. None is a fabrication; each is a place where
  "closed" was doing more work than the evidence. That set is the part of this
  register a gate should read first: R1, R7, R13, R16, R17/R46, R19, R21, R22, R23,
  R23a, R28/R56, R38, R41a, R42, R53, R58.

Two scope limits on the walk itself, stated so nobody reads more into these lines
than they say. **No `Setup.exe` was built or attacked** — the walk box cannot
Native-AOT-publish (no MSVC C++ workload), so every "T" is unit/CI-level plus the
G1/G2 ceremonies already recorded by others. And the register's **Stage-1
negative-test claims are historical**: reverting `src/` to a Stage-1 parent now
yields a compile error naming the missing security API rather than an observed
assertion failure, because the tests those lanes shipped assert against APIs the
fixes introduced. That is still genuine fail-on-parent evidence, but it is not
re-auditable the way the original per-lane ceremony was. Where a **red assertion**
was obtainable — R8/R14/R30/R45, R31, R33 — the status line says so.

Rows merged **after** `102ea3f` (**R64**, **R66**, **R67**, **R68**) cannot cite run
`34362414470`; their lines name their own PR's CI instead. The first *automatic*
`wrapper-vm-tests.yml` run —
[34368896457](https://github.com/Sigil-build/sigil/actions/runs/34368896457) on
`b07021e`, fired by the push trigger [#41](https://github.com/Sigil-build/sigil/pull/41)
added — was still in flight when this pass was written, so **no row claims a VM
verdict**.

---

## Severity rubric

- **RELEASE BLOCKER** — ship this and you have a CVE or a broken promise.
- **SHOULD-FIX** — fix in the release, not after.
- **POST-v1** — real, but a documented limitation is honest.
- **NOTE** — informational.

Effort: **S** ≤ 1 day · **M** 1–3 days · **L** ≈ 1 week.

---

# RELEASE BLOCKERS

### R1 — Elevated replay of unauthenticated, user-writable install state
**Component:** Wrapper.Core / Engine · **Effort: L**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane S1,
> [#19](https://github.com/Sigil-build/sigil/pull/19) `31ae3a3` (primitives) and
> [#20](https://github.com/Sigil-build/sigil/pull/20) `5b65712` (main body); the
> plant fixture was later made host- and order-independent by
> [#36](https://github.com/Sigil-build/sigil/pull/36) `0c092d1`. **Evidence T + M:**
> `StateProvenanceTests` (21), `ReplayAnchoringTests` (115), `HostileStateJsonTests`
> (10), `UninstallAnchorSelectionTests` (6), `ScopeInstallSessionTests` (9) — all
> `Passed` on run `34362414470`; plus G1 hand-attacks 1–2 with their quoted refusal
> lines (`03-RC_ORCHESTRATION.md`). **Narrower than recorded:** the "forcing the
> anchoring predicate to `Allow` fails 51 tests" claim in the note below is a
> mutation experiment the V1.1 walk did **not** reproduce. What it could
> re-establish is weaker but still sound: `StateDirectorySecurity.cs` and
> `ReplayAnchor.cs` did not exist at the S1 parent (`8ad077d`) at all, so the
> provenance primitive and its replay anchor are demonstrably this fix's.

> **STATUS — FIXED in Stage 1** (lane S1; primitives `31ae3a3` / PR #19, main body
> `5b65712` / PR #20). Both negative tests were confirmed failing at the parent
> commit by the orchestrator, not accepted on the lane's word: the directory
> attack fails at `8ad077d`, the file attack at `5d3fd98`. Forcing the anchoring
> predicate to `Allow` fails 51 tests. See **R44** for the one composite this fix
> does not cover.

This is the prior review's B1. It is **confirmed, and materially worse than
described** — the blast radius extends to the install path, not just uninstall.

Evidence, all current:

| Clause | Location |
|---|---|
| Machine state dir created with a bare `Directory.CreateDirectory`, no DACL | `src/SigilBuild.Wrapper.Core/Engine/UninstallStateStore.cs:70-71` |
| No ACL hardening exists anywhere in `src/` | `grep -r "DirectorySecurity\|SetAccessControl\|FileSystemAccessRule" src/` → **zero hits** |
| Machine state root is `%ProgramData%` | `src/SigilBuild.Wrapper.Core/Engine/ScopeLayout.cs:72-74`, `UninstallStateStore.cs:42` |
| `TryLoad` falls back to the **opposite scope** | `UninstallStateStore.cs:136-140` |
| Authoritative scope is read from **inside the found file** | `UninstallStateStore.cs:174` |
| Replayed by elevated **uninstall** | `Engine/UninstallEngine.cs:42` |
| Replayed by elevated **install** (new) | `Engine/InstallSession.cs:621` (`ExistingInstallDetected` → `TryLoad`) → `:918-922` (`PerformReinstallCleanupAsync` → `UninstallEngine.RunAsync`) |

Live confirmation, not just code reading: `icacls C:\ProgramData` grants
`BUILTIN\Users:(CI)(WD,AD,WEA,WA)` and `CREATOR OWNER:(OI)(CI)(IO)(F)`. On this
machine `C:\ProgramData\Sigil` already exists **owned by the standard user with
full control**. `File.WriteAllText` truncates in place and preserves the
existing owner and DACL, so an attacker who pre-creates the file keeps control
of it after the elevated installer writes to it.

The replayed records are not path-limited in any way — no anchoring to
`install_dir`, no registry-subtree limit:

| Record | Location | Elevated primitive |
|---|---|---|
| `UnregisterCom` | `Engine/RollbackJournal.cs:602` | `LoadLibrary` + call an export from an **attacker-chosen DLL path** — arbitrary code as admin |
| `RestoreFile` | `RollbackJournal.cs:156-164` | arbitrary file overwrite / delete |
| `RestoreDeletedFile` / `RestoreDeletedDirectory` | `:388-398`, `:412-423` | arbitrary file / tree write from an attacker-chosen stash |
| `RestoreRegistryValue` / `RestoreRegistryKey` | `:238-266`, `:359-373` | arbitrary **HKLM** write (`RegistryHelper.ParseHive` accepts `"HKLM"`, `RegistryHelper.cs:22`) |
| `RestoreEnv` (`scope: machine`) | `:299-325` | machine `PATH` hijack |
| `RemoveService` | `:510-511` | stop/delete any service |
| `RemoveUninstaller` | `:494` | arbitrary (or reboot-scheduled) delete |

**Why it matters:** a local unprivileged user plants `uninstall.json`, then
waits. The next time an administrator runs the publisher's signed
`Setup.exe` — a *plain install*, not just an uninstall — the elevated process
loads and executes the planted records. `unregister_com` alone is a one-JSON-record
arbitrary-code-execution-as-admin primitive. There is no integrity protection of
any kind: no signature, no HMAC, no ownership check (`grep -r "HMAC" src/SigilBuild.Wrapper.Core` → zero hits).

**Fix:** (a) create the machine state directory with an explicit DACL — SYSTEM +
Administrators full, Users read, inheritance disabled — and refuse to load state
whose directory/file is not owned by SYSTEM or Administrators; (b) delete the
opposite-scope fallback and derive scope from the directory the file was found
in, not from a field inside it; (c) anchor replay — reject any record whose
target path is not under the recorded `install_dir` or a scope-root allowlist,
and whose registry key is not under the app's own subtree; (d) re-derive
`unregister_com`'s DLL path from `install_dir` rather than persisting a
free-form path.

---

### R2 — Elevated installer spawns an executable path taken from HKCU
**Component:** Wrapper.Core / upgrade · **Effort: M**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane S1,
> [#20](https://github.com/Sigil-build/sigil/pull/20) `5b65712`. **Evidence T + M:**
> `InstalledStateResolverTests` (7), `PriorUninstallerTrustTests` (20) — `Passed` on
> run `34362414470`; G1 attack 3, with both gates probed independently rather than
> as one composite. Negative-test re-check: reverting `InstalledStateResolver.cs` to
> the S1 parent fails to compile against `ScopeProbeOrder` — the asymmetric probe
> order this fix introduced did not exist there.

> **STATUS — FIXED in Stage 1** (lane S1, `5b65712` / PR #20). The probe order is
> now asymmetric by design — see `InstalledStateResolver.ScopeProbeOrder`, and do
> not "restore the symmetry". This fix is what broke a **pre-existing** test on CI
> that planted an HKCU entry and asserted it was read: CI runs elevated, so the
> elevated arm was reached there and nowhere locally. Fixed test-only in `728c957`.

`InstalledStateResolver.Resolve` probes the **user** hive as a fallback even
when the tentative scope is machine:

```
src/SigilBuild.Wrapper.Core/Engine/InstalledStateResolver.cs:38-40
    var order = tentativeScope == InstallScope.Machine
        ? new[] { InstallScope.Machine, InstallScope.User }
        : new[] { InstallScope.User, InstallScope.Machine };
```

`UninstallString` is read from that key (`:70`), parsed to an exe path, and then
spawned by the already-elevated install session:

```
src/SigilBuild.Wrapper.Core/Engine/InstallSession.cs:937   var exe = _plan.PriorUninstallExe;
src/SigilBuild.Wrapper.Core/Engine/InstallSession.cs:962   psi.FileName = exe;
src/SigilBuild.Wrapper.Core/Engine/InstallSession.cs:970   using var proc = System.Diagnostics.Process.Start(psi);
```

Elevation happens first (`src/SigilBuild.Installer.Host/Program.cs:71-77`), so
this runs at high integrity. There is no signature check, no path validation,
and no admin-writable-directory requirement — only `File.Exists` (`:945`).

**Why it matters:** an unprivileged user writes
`HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\<AppId>` with a
`DisplayVersion` low enough to classify as an upgrade (`UpgradePlanner.cs:69`)
and `UninstallString` pointing at their own binary. The next admin-approved run
of the publisher's legitimate installer executes it as admin. Found
independently by two audit lanes.

**Fix:** when the effective scope is machine (or the process is elevated), probe
HKLM only. Additionally require `PriorUninstallExe` to be Authenticode-verified
or to live under an admin-only directory before spawning.

---

### R3 — `/D=` is unvalidated and privileged step targets are unanchored → SYSTEM binary in a user-writable directory
**Component:** Wrapper.Core / steps + install-dir resolution · **Effort: M**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane S2,
> [#21](https://github.com/Sigil-build/sigil/pull/21) `4505b24`. **Evidence T + M:**
> `InstallDirContainmentTests` (24), `InstallDirContractTests` (8),
> `InstallDirResolverTests` (10), `PrivilegedStepContainmentTests` (16) — `Passed`
> on run `34362414470`; G1 attack 4 with its literal refusal line. Negative-test
> re-check: `InstallDirResolver.GrandfatheredPriorDir` and `ScopeDefault` are absent
> at the parent (`4505b24~1`), so the containment surface is this fix's.

> **STATUS — FIXED in Stage 1** (lane S2, `4505b24` / PR #21). 6 of the 8 negative
> tests fail at the parent, and the 2 that pass are exactly the positive controls.
> Both `%ProgramFiles%` roots are accepted; a recovered prior install dir is
> **grandfathered and logged** so non-default prior installs stay upgradable.
> Reducing the grandfather clause to "a prior install exists" re-opens the hole —
> that reduction was run and reproduced the escalation.

`InstallDirResolver.Resolve` accepts the `/D=` command-line override and only
canonicalizes it — there is **no containment check against the scope root**:

```
src/SigilBuild.Wrapper.Core/Engine/InstallDirResolver.cs:66-69
    var template = FirstNonBlank(collected, cliOverride, priorInstallDir, manifestInstallDir) ?? DefaultTemplate;
    var resolved = SubstituteDirTokens(template, scopeRoot, appName, appId);
    return Canonicalize(resolved);          // :102 → Path.GetFullPath only
```

`{install_dir}` then substitutes into step fields (`Engine/StepContext.cs:419`),
and `ResolvePath` guards **only** the `payload://` scheme — every other resolved
string is returned verbatim:

```
src/SigilBuild.Wrapper.Core/Engine/StepContext.cs:505
    if (!resolved.StartsWith(PayloadScheme, StringComparison.Ordinal)) { return resolved; }
```

The privileged steps consume that unvalidated path directly:

- `Steps/ScheduledTaskCreateStep.cs:68` → `/TR` with **`/RU SYSTEM`** hardcoded at `:127-128`
- `Steps/ServiceInstallStep.cs:49-50` → `sc create binPath=` (checks `File.Exists` at `:62`, not location)
- `Steps/Win32/ComRegisterStep.cs:51` → `LoadLibrary` in the elevated process
- `Steps/FirewallRuleStep.cs:67-69` → `program=`

**This is reachable from the documented example manifest.**
`docs/guides/install-steps.md:223` shows exactly:

```yaml
- type: scheduled_task_create
  program: "${parameters.install_dir}\\heartbeat.exe"
```

So: `Setup.exe /allusers /D=C:\Users\Public\evil` → an administrator approves the
UAC prompt for a legitimately signed installer → payload lands in a
user-writable directory → a **SYSTEM scheduled task** (or auto-start service)
is created pointing at a binary any user can replace. A publisher who follows
the documentation correctly still produces a vulnerable installer; that is what
makes this a blocker rather than a footgun.

`/D=` is a first-class documented flag (`Cli/CommandLineParser.cs:427-435`,
`:372`) and is forwarded verbatim to the elevated child (`Engine/Elevation.cs`
via `Installer.Host/Program.cs:76`).

**Fix:** reject a resolved `install_dir` that is not under
`ScopeLayout.For(scope).InstallRoot` (for machine scope, under an admin-only
root); and for the four machine-scope steps, require the resolved target to be
contained in `install_dir` and to sit in a directory not writable by
non-administrators.

---

### R4 — Elevated process loads native DLLs from a per-user cache gated only by a marker file
**Component:** Wrapper.Core / native bootstrap · **Effort: M**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane S3,
> [#22](https://github.com/Sigil-build/sigil/pull/22) `72d6437`. **Evidence T + M:**
> `NativeRuntimeCacheTrustTests` (9), `NativeRuntimeReclaimTests` (10) — `Passed` on
> run `34362414470`; G1 attack 5 re-run at the **new** `%ProgramData%\sigil-runtime`
> path, not the old per-user one. Negative-test re-check:
> `NativeRuntimeBootstrap.PrepareCacheDirectory` is absent at the parent (6 call
> sites fail to compile).

> **STATUS — FIXED in Stage 1** (lane S3, `72d6437` / PR #22). The marker is now a
> fast path consulted only *alongside* an ACL check and a full content comparison,
> never instead of them. Note the elevated cache root moved to
> `%ProgramData%\sigil-runtime` — **not** `%ProgramData%\Sigil`, deliberately, so a
> squatted directory can be repaired rather than turning R1's plant into a denial
> of service. The G1 gate text below still names the old `%LocalAppData%` path.

```
src/SigilBuild.Wrapper.Core/Engine/NativeRuntimeBootstrap.cs:85-86
    var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    return Path.Combine(baseDir, "Sigil", "runtime", hash);

src/SigilBuild.Wrapper.Core/Engine/NativeRuntimeBootstrap.cs:98-101
    var marker = Path.Combine(targetDir, CompletionMarkerName);
    if (File.Exists(marker)) { return; }        // extraction skipped wholesale

src/SigilBuild.Wrapper.Core/Engine/NativeRuntimeBootstrap.cs:171
    var cookie = AddDllDirectory(directory);
```

`Installer.Host/Program.cs:136` calls `EnsureNativeDependenciesLoadable()`
**after** the elevation branch at `:71-77` — i.e. inside the elevated process.

**Why it matters:** an attacker pre-creates the content-keyed directory with a
malicious `libSkiaSharp.dll` and touches the completion marker. The elevated
wizard skips extraction entirely, then registers the attacker-controlled
directory on the process DLL search path; Skia/ANGLE/HarfBuzz `DllImport`s
resolve to the planted binary. The SHA-256 directory name is not a defence — the
archive is readable straight out of the setup exe, so the hash is derivable.
Even the incremental path only checks file *length* (`:139-144`), not content.

This affects the GUI install path (the headless `/silent` path returns before
this point, per the comment at `Program.cs:79-86`) — i.e. the default
double-click experience.

**Fix:** for elevated runs, extract to an admin-only directory (`%ProgramData%`
with a hardened DACL, or `%WINDIR%\Temp`), and verify each extracted file's hash
against the embedded archive before `AddDllDirectory` rather than trusting a
marker file.

---

### R5 — Web-installer stub verifies, then executes, a predictably-named `%TEMP%` file
**Component:** Packaging / ExeWrapper · **Effort: M**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane S3,
> [#22](https://github.com/Sigil-build/sigil/pull/22) `72d6437`. **Evidence T:**
> `WebInstallerStubEndToEndTests` (2), `SecureStagingTests` (20),
> `StagingDirTokenTests` (8) — all `Passed` on run `34362414470`.

> **STATUS — FIXED in Stage 1** (lane S3, `72d6437` / PR #22). Confirmed failing at
> the parent: the swapped binary really was launched, and exited 0.

The `--payload web` stub's blob is two independent steps — an `http_download`
followed by a `run_program` of the same path:

```
src/SigilBuild.Packaging/ExeWrapper/ExeWrapperPackager.cs:230
    var downloadDest = "{temp_dir}/" + fullPackageFileName;   // <App>-<ver>-<arch>-Setup.exe — no GUID
src/SigilBuild.Packaging/ExeWrapper/ExeWrapperPackager.cs:234-248   // HttpDownload step, then RunProgram step
src/SigilBuild.Wrapper.Core/Engine/StepContext.cs:433-437           // {temp_dir} → Path.GetTempPath()
```

The download handle is closed before the hash is compared and is never re-opened
or re-checked (`Engine/SigilDownloader.cs:126-148`). The stub runs
`requireAdministrator`.

**Why it matters:** the filename is a pack-time constant derived from the public
artifact name, so it can be both **pre-planted** and **swapped after
verification**. Any medium-integrity process running as the same user (the
normal split-token-admin case) converts a user foothold into elevated code
execution. The SHA-256 check protects the download, not the execution.
`HttpDownloadStep.cs:62-69` will back up and overwrite a pre-existing file at
that path rather than refusing it, and `FileMode.Create`
(`SigilDownloader.cs:126`) follows a planted hardlink or reparse point.

**Fix:** stage into a freshly created, randomly named, admin-only directory, and
re-verify the hash immediately before `run_program` — or hold the verified file
open with sharing that denies write/delete across the launch.

---

### R6 — VM-gated and runtime-gated tests soft-skip by returning early, so they report **Passed**
**Component:** tests / CI · **Effort: S** (the fix; the consequence is large)

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane T1,
> [#23](https://github.com/Sigil-build/sigil/pull/23) `86c2799`. **Evidence T + M:**
> 22 skips on run `34362414470`, **every one carrying a reason string** (full
> inventory, classified, in the walk report's Step 4: 16 VM-only, 5 other, 1
> elevated-only); `grep -rn "// soft-skip" tests/` and `grep -rn '"SKIP:' tests/`
> return only doc-comments describing the old pattern, no live soft-skips.
> **Narrower than recorded:** one skip's *stated* precondition was still wrong —
> `KioskSetupFactAttribute.SetupPath` pointed one directory outside the repo, so its
> test could not execute on any machine and its message told the reader to do
> something that would not help. That is this row's own failure class surviving
> inside its fix. Filed as **R69**, fixed by
> [#42](https://github.com/Sigil-build/sigil/pull/42).

> **STATUS — FIXED in Stage 1** (lane T1, `86c2799` / PR #23). Every gate is now an
> attribute that sets xunit's `Skip`, naming the missing precondition. The measured
> consequence: the suite went from **1 skip** to **21 on CI / 27 locally**. Note
> `Assert.SkipUnless` — which the stage document prescribed — **does not exist** in
> xunit 2.9.2; custom `FactAttribute` subclasses replace it.

The uniform construct is an early `return`, not `Assert.Skip`:

```
tests/SigilBuild.Wrapper.IntegrationTests/UpgradeInstallTests.cs:39-45
tests/SigilBuild.Wrapper.IntegrationTests/MultiEditionInstallTests.cs:54-59
tests/SigilBuild.Wrapper.IntegrationTests/WixClassInstallUninstallTests.cs:61-66
tests/SigilBuild.Wrapper.IntegrationTests/PrerequisiteInstallTests.cs:35-41
tests/SigilBuild.Wrapper.IntegrationTests/ScheduledTaskCreateInstallTests.cs:58-63
tests/SigilBuild.Wrapper.IntegrationTests/FirewallRuleInstallTests.cs:61-66
tests/SigilBuild.Wrapper.IntegrationTests/ComRegisterInstallTests.cs:76-79
tests/SigilBuild.Wrapper.IntegrationTests/LocalizationEndToEndTests.cs:87-92
```

The codebase says so itself: `UpgradeInstallTests.cs:22` — *"Soft-skips
(**returns Passed**) unless Windows + `SIGIL_VM_TESTS=1` …"*. The gate is
`TestEnvironment.IsEnabled` / `IsRuntimeAvailable`
(`tests/SigilBuild.Wrapper.IntegrationTests/TestEnvironment.cs:20`, `:33-47`).

The same pattern gates the **packaging** tests on a staged AOT runtime, using
`Console.WriteLine("SKIP: …"); return;` — which never reaches the trx summary:

```
tests/SigilBuild.Packaging.Tests/ExeWrapper/ExeWrapperPackagerTests.cs:70-79, :164
tests/SigilBuild.Packaging.Tests/ExeWrapper/ExeWrapperWebInstallerPackTests.cs:56-62, :139, :192
```

**Why it matters:** in the run I measured, **1096 tests passed and exactly 1 was
reported as skipped**. Among those "passes" are
`Upgrade_replaces_older_version_preserving_install_dir_and_single_arp_row`,
`Silent_downgrade_is_blocked_with_exit_code_3`,
`WixClass_install_then_uninstall_yields_empty_diff`, and
`PackAsync_produces_arch_tagged_Setup_exe_with_sigil_resources_per_architecture`.
None of them asserted anything. Roughly **14 VM-gated facts plus ~10
runtime-gated packaging facts** are vacuous, and nothing in the reported totals
distinguishes "verified" from "never ran". A green suite is not evidence.

Compounding it: `ci.yml:41-42` runs `dotnet test` in the `build` job, while
`scripts/publish-installer-runtime.ps1` only runs in the *later, separate*
`aot-publish` job (`ci.yml:111,114,181-188`) — so on every push the AOT host is
never staged and the whole pack→`Setup.exe` stamping path soft-skips.

Only one VM job guards against a vacuous green
(`wrapper-vm-tests.yml:231-236`, the p11 job, whose comment names the exact
risk: *"refusing to pass vacuously"*). The scope matrix (`:35-96`) and the p12
job (`:125-171`) have no such guard.

**Fix:** convert every early return to xUnit v3 `Assert.Skip(reason)` (or a
`[VmFact]` attribute with a computed `Skip`) so the totals show them as skipped;
stage the runtime in the `build` job before `dotnet test`; add the p11-style
pre-flight assertion to the other two VM jobs.

---

### R7 — No release pipeline, no artifact publication, no signed output; README promises install channels that do not exist
**Component:** CI / repo · **Effort: M**

> **STATUS (V1.1, 2026-09-09):** CLOSED IN STRUCTURE, **claim-only for the execution
> half** — lane REL, [#32](https://github.com/Sigil-build/sigil/pull/32) `f9d3af5`.
> **Evidence M:** `.github/workflows/release.yml` re-read during the walk — the
> `publish` job carries `needs: vm-tests` (calling `wrapper-vm-tests.yml`), a
> "require signing secrets" refusal **before** any restore, Azure Trusted Signing
> `@v2.0.0`, and a `SHA256SUMS` step. **Evidence C:** the workflow has **never
> run** — it is tag-triggered on a non-default branch, so its structure was read,
> not exercised. The DoD clause "the published artifact **runs on a clean
> machine**" therefore remains an unticked G3 item, and the release dry-run is
> blocked on the six Trusted Signing secrets. The SBOM step this workflow should
> also carry was orphaned between two merged lanes — see **R70** and
> [#42](https://github.com/Sigil-build/sigil/pull/42).

- `git ls-files .github/workflows` → exactly four: `ci.yml`, `docs.yml`,
  `secret-scan.yml`, `wrapper-vm-tests.yml`. **No tag trigger anywhere, no
  `release.yml`.**
- `ci.yml:220-224` uploads **unsigned** AOT artifacts as CI job artifacts —
  which require a login, expire, and are not a distribution channel.
- **The one artifact that exists is broken by construction.** `ci.yml:224`
  uploads `path: publish/win-x64/sigil.exe` — the single file. But the AOT
  output is not single-file: `publish/win-x64/` also contains
  `libSkiaSharp.dll` (11.09 MB) and `libsodium.dll` (0.33 MB), which
  `sigil.exe` needs for logo resizing and ZIP manifest signing. A user who
  downloads the artifact gets a binary that throws `DllNotFoundException` on
  those paths.
- No workflow Authenticode-signs `sigil.exe` or any produced `Setup.exe`.
- `git ls-remote --tags origin` → only `exe-installer-v1`. No release tag.
  (`pre-merge-backup-p9` is local-only.)
- `README.md:19-30` advertises `winget install Sigil-build.sigil`,
  `curl -sSL https://sigil.build/install.sh | sh`, and
  `dotnet tool install -g SigilBuild`. None of these exist. The `curl` and
  `dotnet tool` lines are offered for **macOS / Linux**, for a Windows-only
  product.
- `docs/sprint-01/identifier-reservation.md` still carries the NuGet ID as an
  unresolved placeholder.

**Why it matters:** you cannot ship a release without a way for a user to obtain
the artifact, and an unsigned installer-builder distributed by an unknown
publisher will be flagged by SmartScreen and rightly distrusted. The README
currently misrepresents the product's availability and platform support.

**Fix:** add a tag-triggered `release.yml` that runs the VM matrix as a required
gate, Authenticode-signs the CLI, publishes checksummed artifacts to a GitHub
Release, and generates the notices file; correct the README install section to
"build from source" until a channel actually exists.

---

# SHOULD-FIX

### R8 — Parameter `source.url` accepts `http://` end to end; its values feed elevated install steps
**Component:** Core / parser + Installer.Host · **Effort: S**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane S4,
> [#28](https://github.com/Sigil-build/sigil/pull/28) `3e94b8b`. **Evidence T + M:**
> `NetworkTrustParseTests.Parameter_source_url_must_be_https` (3 cases) `Passed` on
> run `34362414470`; G2 check 4 → `SIG0323` against a real CI-built `sigil.exe`.
> Negative-test re-check — one of the walk's three **red assertions**: reverting
> `Configuration/ManifestParser.cs` to `3e94b8b~1` fails 14 of 23
> `NetworkTrustParseTests` (9 pass as positive controls), covering this row plus
> **R14**, **R30** and **R45** in one experiment.

`schemas/sigil-schema.json:51` (and again at `:624`) declares
`"url": { "type": "string" }` with no scheme constraint;
`Configuration/ManifestParser.cs:1092-1107` checks presence only; and
`Installer.Host/Services/HttpOptionsLoader.cs:47` GETs it verbatim. This is the
**only** HTTP consumer with no scheme validation — `http_download`
(`HttpDownloadStep.cs:37-40`, SIG0235/0236) and the channel manifest's
`packageUrl` (`ChannelManifestParser.cs:82-85`) both enforce HTTPS at pack *and*
run time.

The fetched values become parameter values, which are substituted into step
fields (paths, registry coordinates, arguments) executed elevated. Graded
SHOULD-FIX rather than blocker only because it requires the publisher to write
an `http://` URL — but nothing warns them, and the fix is trivial.

**Fix:** reject non-`https://` `source.url` in `ManifestParser` (mirroring
SIG0235) and re-check the substituted URL in `HttpOptionsLoader.LoadAsync`.

---

### R9 — `/P<name>=` values flow unvalidated into privileged step fields
**Component:** Wrapper.Core / steps · **Effort: M**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane S2,
> [#21](https://github.com/Sigil-build/sigil/pull/21) `4505b24`. **Evidence T:**
> `PrivilegedTargetGuardTests` (8), `SecretHygieneTests` (6),
> `PrivilegedStepContainmentTests` (16) — `Passed` on run `34362414470`. The row's
> own text is wrong about `payload://`, and the Stage-1 note below says so in
> writing; the walk confirms the **note**, not the row body. Read them in that
> order.

> **STATUS — CLOSED in Stage 1, but NOT "fixed as written"** (lane S2, `4505b24` /
> PR #21). Privileged targets now require containment **and**
> admin-only-writability. **The row's own text is wrong on one point and the
> implementation deliberately departs from it:** this row names `payload://` as a
> safe source for a privileged target. The implementation **refuses** it, because
> the payload extracts into the invoking user's own `%TEMP%` — replaceable between
> extraction and use, and deleted at end of run — so a scheduled task or service
> pointing into it targets a path that no longer exists. Anyone reconciling this
> row against the code should reconcile toward the code.

`Cli/CommandLineParser.cs:590-594` accepts `/PName=Value`; the values are
forwarded verbatim to the elevated child and expand via `ctx.Resolve*` into
`scheduled_task_create.program` (`/RU SYSTEM`), `service_install.binary_path`,
`com_register.path`, and `firewall_rule.program`. Same missing containment as
**R3**, reached through a different input. Listed separately because the fix is
the same helper but a different set of call sites, and because R3's `/D=` route
is exploitable against the documented example while this one needs the manifest
to declare a parameter in a privileged field.

**Fix:** resolve privileged step targets only from `payload://` or a contained
`{install_dir}`; reject substituted values that escape it.

---

### R10 — No size cap on any download; the channel manifest is fully buffered before its signature is checked
**Component:** Wrapper.Core / net · **Effort: S**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane S3,
> [#22](https://github.com/Sigil-build/sigil/pull/22) `72d6437`. **Evidence T:**
> `DownloadSizeCeilingTests` (7) `Passed` on run `34362414470`. Negative-test
> re-check: `SigilDownloader.DefaultMaxBytes` is absent at the parent at **three**
> call sites — `PrerequisiteRunner.cs:314`, `Steps/HttpDownloadStep.cs:91`,
> `Update/UpdateSeams.cs:185` — which is also the evidence that the cap is enforced
> on every download path, not one.

> **STATUS — FIXED in Stage 1** (lane S3, `72d6437` / PR #22). `maxBytes` rejects up
> front on `Content-Length` **and** aborts mid-stream on a server that lies about
> it; the pre-authentication manifest buffer is capped.

```
src/SigilBuild.Wrapper.Core/Engine/SigilDownloader.cs:118   var total = resp.Content.Headers.ContentLength;  // progress only
src/SigilBuild.Wrapper.Core/Engine/SigilDownloader.cs:133   while ((n = await src.ReadAsync(...)) > 0)       // no cap
src/SigilBuild.Wrapper.Core/Update/UpdateSeams.cs:78        var bytes = await resp.Content.ReadAsByteArrayAsync(...)
```

`Content-Length` is read but never enforced as a ceiling. `FetchAsync` buffers
the entire channel manifest into memory **before**
`ChannelManifestVerifier.Verify` runs (`UpdateRunner.cs:116`), so memory
exhaustion is a *pre-authentication* attack surface. Timeouts do not bound a
slow-drip large body.

**Fix:** add a `maxBytes` ceiling to `DownloadVerifiedAsync` (reject up front
when `ContentLength` exceeds it, abort mid-stream otherwise); cap the manifest
fetch at a few hundred KB.

---

### R11 — Nothing downloaded is Authenticode-verified before elevated execution
**Component:** Wrapper.Core · **Effort: S**

> **STATUS (V1.1, 2026-09-09):** CLOSED with two written limitations — lane S3,
> [#22](https://github.com/Sigil-build/sigil/pull/22) `72d6437`. **Evidence T + D:**
> `AuthenticodeLaunchGateTests` (2), `DownloadedBinaryTrustTests` (35),
> `LaunchGateOrderingTests` (3) — `Passed` on run `34362414470`. The two residuals
> were **filed as rows rather than claimed done**: **R45** (the policy is inferred,
> not declared) and **R49** (integrity is not publisher identity). That is the
> honest shape, and the walk found no gap between the row and the code.

> **STATUS — FIXED in Stage 1** (lane S3, `72d6437` / PR #22), with a documented
> per-prerequisite opt-out that fails closed. Two limitations are filed rather than
> claimed done: the policy is *inferred* from `SignDeclared` rather than declared
> (**R45**), and `WinVerifyTrust` accepts any machine-trusted chain — including a
> per-user root any non-admin can install — so this is **integrity, not publisher
> identity** (**R49**).

`AuthenticodeVerifier.VerifyFile` exists and is AOT-clean
(`Engine/AuthenticodeVerifier.cs:63`), but its only caller in the entire tree is
`Engine/WrapperBlob.cs:498`, which uses it to decide whether to render a
cosmetic "Signed by …" line. No prerequisite installer
(`Engine/PrerequisiteRunner.cs:122`), no update package
(`Update/UpdateRunner.cs:204`), and no web-stub payload is signature-checked
before `Process.Start`. SHA-256 is the sole gate, so R5's and R12's TOCTOU
windows have no second line of defence.

**Fix:** call `AuthenticodeVerifier.VerifyFile` immediately before launching any
downloaded binary; fail closed, with a documented per-prerequisite opt-out for
unsigned redistributables.

---

### R12 — Prerequisite and update binaries: verify→launch gap, default ACLs, no handle held
**Component:** Wrapper.Core · **Effort: M**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane S3,
> [#22](https://github.com/Sigil-build/sigil/pull/22) `72d6437`. **Evidence T:**
> `StagedExecutionTests` (5), `SecureStagingTests` (20), `StagingDirTokenTests` (8)
> — all `Passed` on run `34362414470`.

> **STATUS — FIXED in Stage 1** (lane S3, `72d6437` / PR #22). `SecureStaging` gives
> a private per-run directory; `OpenVerified` re-hashes **from the open handle** and
> returns a `FileShare.Read` handle held across the launch. Orchestrator-run at the
> parent commit `31ae3a3`: all four `StagedExecutionTests` fail there with
> meaningful assertions, including *"a staged setup whose bytes no longer match the
> sha256 it was verified under must never be launched, **but found True**"* — the
> TOCTOU firing live. An elevated run that cannot establish an admin-only staging
> root now **throws rather than degrading**.

```
src/SigilBuild.Wrapper.Core/Engine/PrerequisiteRunner.cs:237   Path.Combine(Path.GetTempPath(), $"sigil-prereq-{Guid.NewGuid():N}.exe")
src/SigilBuild.Wrapper.Core/Engine/PrerequisiteRunner.cs:111-122  AcquireAsync … then launcher(exePath, …)
src/SigilBuild.Wrapper.Core/Update/UpdateRunner.cs:179-204        same shape
```

Structurally identical to R5 but harder to exploit — the GUID name blocks
pre-planting, so an attacker must win a directory-change-notification race. The
file is created with default ACLs and no lock is retained between verification
and launch.

**Fix:** stage into a per-run randomly named admin-only subdirectory and hold an
open handle denying write/delete from hash verification through process launch.

---

### R13 — No freshness or replay protection on the signed channel manifest
**Component:** Wrapper.Core / update · **Effort: M**

> **STATUS (V1.1, 2026-09-09):** CLOSED in-process; the **live replay is deferred in
> writing** — lane S4, [#28](https://github.com/Sigil-build/sigil/pull/28)
> `3e94b8b`. **Evidence T + D:** `UpdateFreshnessTests` (17),
> `UpdateEndToEndTests` (4), `ChannelManifestParserTests` (15) — `Passed` on run
> `34362414470`. **Deferral, and the honest form of one:** G2 check 6 is left
> **unticked on purpose** — a live `Setup.exe /Update` replay against a hosted,
> signed channel manifest is a G3 / VM-matrix item (see the G2 check results above
> and `10-G2_G3_RUNBOOK.md`). Nobody ticked a box the evidence did not support.

`Update/ChannelManifest.cs:54-59` carries no timestamp, expiry, nonce, or
sequence field, and the only monotonicity check is against the *locally
installed* version (`Update/UpdateRunner.cs:130`). No "highest version ever
seen" is persisted.

**Why it matters:** signature authenticity is intact, but freshness is entirely
absent. An on-path attacker or compromised CDN replays yesterday's correctly
signed manifest indefinitely — the client reports "up to date" and exits 0
(`:134`) while a security fix exists (freeze attack) — or replays a signed
manifest for an intermediate *vulnerable* version that is still newer than
installed, and the client installs it.

**Fix:** add a required signed `issuedAt`/`expiresAt` (reject manifests older
than N days) and/or a monotonic `sequence` persisted in machine-scope state;
treat a decreasing sequence as SIG0321.

---

### R14 — `updates.manifestUrl` is never required to be HTTPS
**Component:** Core / parser + update · **Effort: S**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane S4,
> [#28](https://github.com/Sigil-build/sigil/pull/28) `3e94b8b`. **Evidence T + M:**
> `NetworkTrustParseTests.Updates_manifestUrl_must_be_https` (2 cases) `Passed` on
> run `34362414470`; G2 check 5 → `SIG0324`, and doubly so via schema `SIG0010`.
> Red assertion on the parent, shared with **R8** (14 failed / 9 passed).

`schemas/sigil-schema.json:457-461` constrains only `"format": "uri"` despite
its own description saying "HTTPS URL"; `Configuration/ManifestParser.cs:156`
passes it through unvalidated; `Update/UpdateSeams.cs:71-72` fetches it verbatim
(and the `.sig` URL is that string + `".sig"`, `UpdateRunner.cs:94`).
Code execution is still gated by the signature, so impact is limited to
cleartext leakage of app-id/version/channel plus a reliable update-suppression
DoS (which R13 makes worse).

**Fix:** enforce `https://` at pack time and re-check before the fetch.

---

### R15 — Uninstall swallows undo failures, reports success, then deletes the state that would allow a retry
**Component:** Wrapper.Core / engine · **Effort: S**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane S5,
> [#29](https://github.com/Sigil-build/sigil/pull/29) `50e5de4`. **Evidence T:**
> `UninstallEngineTests` (6) and `ReplayAnchoringTests` (115) — the `UndoFailureCode`
> path and the state-and-ARP retention on a failed undo — `Passed` on run
> `34362414470`.

```
src/SigilBuild.Wrapper.Core/Engine/RollbackJournal.cs:111   catch { /* Best-effort; swallow individual undo failures. */ }
src/SigilBuild.Wrapper.Core/Engine/UninstallEngine.cs:50    await loaded.Journal.UndoAsync(ct, progress);   // result never inspected
src/SigilBuild.Wrapper.Core/Engine/UninstallEngine.cs:59    UninstallStateStore.Delete(appId, loaded.Scope);
```

The `schtasks` / `netsh` / COM / `sc` undos also ignore spawn failures and exit
codes (`:531-535`, `:553-575`, `:641-656`). An access-denied or missing tool
therefore leaves a permanent **SYSTEM scheduled task**, machine COM
registration, or open firewall port behind — with the record that would have
removed it deleted.

**Fix:** capture per-record outcomes, surface failures to the user and the log,
and retain the state file when any record failed.

---

### R16 — No path containment on any step destination; config edits follow junctions and inherit attacker DACLs
**Component:** Wrapper.Core / steps · **Effort: M**

> **STATUS (V1.1, 2026-09-09):** **PARTIALLY CLOSED**, and the remainder is written —
> lane S2, [#21](https://github.com/Sigil-build/sigil/pull/21) `4505b24`.
> **Evidence T + D:** `PathContainmentTests` (25),
> `StepDestinationContainmentTests` (13), `StepDestinationGuardTests` (6),
> `UnresolvedPathTokenTests` (25), `AllowOutsideInstallDirParseTests` (20) — all
> `Passed` on run `34362414470`. **Narrower than recorded — clause 3 is
> unimplemented**, re-verified during the walk: `grep -rn
> "ResetDacl\|SetAccessControl"` across `src/SigilBuild.Wrapper.Core/Steps/` and
> `StepContext.cs` returns **zero hits**. Do not read this row as "fixed as
> written"; the Stage-1 note below scopes it correctly.

> **STATUS — PARTIALLY FIXED in Stage 1** (lane S2, `4505b24` / PR #21). Clauses 1
> and 2 are done: destinations are contained with a documented per-step opt-out
> (`allow_outside_install_dir`), and the unresolved-`{brace}` check moved into the
> **resolver** so it covers every path field rather than one step.
> **Clause 3 — resetting the DACL on machine-scope files the installer creates — was
> never in the lane's task list and is NOT implemented.** Do not read this row as
> closed. The opt-out also has no counterpart in the replay anchor: that is **R44**.

Containment logic is re-implemented three times and shared nowhere —
`StepContext.cs:523-537` (payload sources only), `PayloadExtraction.cs:108-118`
(zip-slip), `NativeRuntimeBootstrap.cs:190-191`. **No step destination is checked
at all:** `Steps/ConfigFileEditor.cs:28,59-64`, `Steps/FileDeleteStep.cs:30`,
`Steps/DirectoryDeleteStep.cs:33`, `Steps/HttpDownloadStep.cs:33`;
`Steps/FileCopyStep.cs:23` does not even call `ResolvePath`. Pack time is no
better — `ManifestParser.cs:1589-1631` accepts any `path` scalar.

`ConfigFileEditor.cs:64` uses `File.WriteAllText` (`FileMode.Create`), which
traverses reparse points — and **directory junctions require no privilege**. An
existing target is truncated in place and keeps its prior DACL, so an
attacker-created placeholder stays attacker-writable after the elevated
installer writes to it. `Directory.CreateDirectory` at `:62` will materialize a
whole tree outside `install_dir`. No reparse-point check exists anywhere in
`src/`.

**Fix:** add one `PathContainment.EnsureUnder(root, candidate)` helper (the
`StepContext.cs:527-532` logic) and route every step destination through it
against `ctx.InstallDir`, with an explicit manifest opt-out for deliberate
out-of-tree writes; reject reparse points in the ancestor chain; reset the DACL
on files the installer creates in machine scope.

---

### R17 — `AuthenticodeVerifier` disables revocation checking
**Component:** Wrapper.Core · **Effort: S**

> **STATUS (V1.1, 2026-09-09):** CLOSED with a written limitation — lane S3,
> [#22](https://github.com/Sigil-build/sigil/pull/22) `72d6437`. **Evidence T + D:**
> `DownloadedBinaryTrustTests` (35), `AuthenticodeVerifierTests` (2) — `Passed` on
> run `34362414470`. Negative-test re-check: `AuthenticodeStatus` — the tri-state
> (`trusted` / `unavailable` / `forged`) that is the whole point of this fix — is
> absent at the parent. **Narrower than recorded:** no revocation behaviour has
> real-fixture proof, and the `0x80092010..14` error band is mapped against
> **documented semantics only**, never against an observed API response. That is
> written here, in **R46**, and in ADR-011 — three places, which is why the walk
> counts it as a limitation rather than a gap.

> **STATUS — FIXED in Stage 1** (lane S3, `72d6437` / PR #22). Whole-chain revocation,
> with "unavailable" rendered as a state **distinct** from both trusted and forged.
> The lane widened the scope on its own initiative and found two more defects in the
> contiguous `0x80092010..0x80092014` band: `CRYPT_E_NO_REVOCATION_DLL` was an
> over-refusal, and **`CRYPT_E_REVOKED` was waivable by `allow_unsigned`** — a
> revoked signature and an unsigned binary had become the same class of problem,
> decided by which Windows layer noticed. Now unconditional.
> **No revocation behaviour has real-fixture proof**; the band is verified by
> mapping against documented semantics, not by observed API returns. See **R46**,
> **R47**, **R48**.

`Engine/AuthenticodeVerifier.cs:31,95` — `fdwRevocationChecks = WTD_REVOKE_NONE`.
A revoked publisher certificate still renders the "Signed by …" trust line in
the wizard, which is precisely the assurance that line exists to give.
Re-verified from the prior review; unchanged.

**Fix:** use `WTD_REVOKE_WHOLECHAIN` with a cached/offline-tolerant policy, and
render a distinct state when revocation status is unavailable.

---

### R18 — Secret parameter values travel on process command lines
**Component:** Wrapper.Core · **Effort: M**

> **STATUS (V1.1, 2026-09-09):** CLOSED for its mechanism; **one half ships
> untested** — lane S5, [#29](https://github.com/Sigil-build/sigil/pull/29)
> `50e5de4`. **Evidence T:** `ElevationSecretHandoffTests` (11),
> `WizardLogRedactionTests` (2) — `Passed` on run `34362414470`. Negative-test
> re-check, and the walk's one negative result: the log-leak half is proven absent
> at the parent (`Program.RenderWizardStartedLine` does not exist there), **but all
> 11 handoff tests still pass with `Elevation.cs` reverted to its pre-R18 form** —
> they exercise `ElevationSecretHandoff.cs` in isolation, so the
> `childMayStillBeRunning` relaunch/cleanup gating and the two hosts' relaunch
> wiring are pinned by nothing. That does **not** reopen this row — the DPAPI
> envelope replacing the command line is demonstrably this fix's — but it is filed
> as **R72**.

Logs, journal, and state are correctly redacted (`StepContext.cs:91-107`,
`InstallLog.cs:100`, `UninstallStateStore.cs:100-115`), but the UAC relaunch
re-emits `/P<secret>=<value>` on the child's command line
(`Engine/Elevation.cs:92-96,143-155` ← `Installer.Host/Program.cs:76`), and any
`run_program`/hook argument containing a resolved secret lands in the child's
command line (`Steps/RunProgramStep.cs:51-56`). Both are visible to
process-creation auditing (Sysmon/EDR/WMI). Extends the prior review's finding,
which covered `run_program` but not the elevation relaunch.

**Fix:** pass secrets to the elevated child over an inherited pipe or a
DPAPI-protected temp file; document that `run_program` arguments are not a
secret channel.

---

### R19 — Hostile `uninstall.json` crashes the elevated process; unbounded read
**Component:** Wrapper.Core · **Effort: S**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane S1,
> [#20](https://github.com/Sigil-build/sigil/pull/20) `5b65712`. **Evidence T:**
> `HostileStateJsonTests` (10) `Passed` on run `34362414470`. **Narrower than
> recorded:** the note below claims that "reverting only `UninstallStateStore.cs`
> reproduces the four escapes". That experiment is **no longer re-runnable at
> HEAD** — the same single-file reversion now fails to compile against
> `InstallSession` and `UninstallEngine` (`EnsureDirectory`, `StashDirectoryFor`, a
> 6-argument `Save`, `LoadedState.InstallDir`), and it fails identically against the
> S1 parent `8ad077d`. The negative-test evidence for this row is **historical**;
> the ten tests that assert the behaviour are current and green.

> **STATUS — FIXED in Stage 1** (lane S1, `5b65712` / PR #20). Rehydration moved
> inside `TryLoad`'s `catch` and is treated as "state unreadable"; file-size and
> record-count ceilings added. Reverting only `UninstallStateStore.cs` reproduces
> the four exact pre-fix escapes: `unknown rollback record type`, `missing required
> field 'path'`, `ArgumentNullException`, `NullReferenceException`.
> A refusal is reported as a refusal, never as an absence — reporting "no uninstall
> state found" for a file the operator can see on disk is the same cover for an
> attacker that R1 closed.

```
src/SigilBuild.Wrapper.Core/Engine/UninstallStateStore.cs:157-160   catch { continue; }   // covers ONLY Deserialize
src/SigilBuild.Wrapper.Core/Engine/UninstallStateStore.cs:168-171   rec.ToRollbackRecord()  // OUTSIDE the try
```

`ToRollbackRecord` throws on an unknown discriminator
(`Json/SerializableRollbackRecord.cs:288-289`) or a missing required field
(`:376-377`), and `records:[null]` throws at `:223`. Nothing catches it here, in
`UninstallEngine.RunAsync`, or in `InstallSession.cs:621`. A one-line planted
file makes **every install and uninstall of that AppId** die with an unhandled
exception — a persistent per-app DoS. It fails closed (nothing is replayed), but
noisily. Separately, `:148` `File.ReadAllText` materializes the whole
attacker-controlled file with no cap.

**Fix:** widen the `try` to cover rehydration and treat failure as "state
unreadable"; cap file size and record count before reading.

---

### R20 — `dotnet format`, PR-title lint, and schema lockstep are documented as CI-enforced but were never installed; `main` currently fails format
**Component:** CI / repo · **Effort: S**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane F0,
> [#16](https://github.com/Sigil-build/sigil/pull/16) `c82f5eb`. **Evidence M:**
> `pr-guards.yml` carries `conventional-commit PR title` (:21), `schema / docs
> lockstep` (:39) and `dotnet format` (:73); the G0 proof-of-failure ceremony —
> throwaway PR #17, titled `broken title` — was **observed failing** that job and
> then closed; `dotnet format Sigil.slnx --verify-no-changes` exits 0 both locally
> and on run `34362414470`. A gate that has been watched refusing something is the
> strongest form of evidence in this register.

`AGENTS.md:16` marks `dotnet format --verify-no-changes` "CI-enforced";
`AGENTS.md:96` claims "PR titles are lint-gated"; `AGENTS.md:104` lists format
in the PR checklist. **Grep for `dotnet format` across `.github/workflows/`
returns nothing.**

`CONTRIBUTING.md:69` repeats the claim: "The `pr-guards` workflow enforces
conventional-commit PR titles, `dotnet format`, and schema/docs lockstep on
every PR."

The root cause is concrete and the fix is cheap: the workflow that implements
all three checks exists in the repo but was **never installed** —
`_agent-setup/github-workflows/pr-guards.yml` contains a `pr-title` job, a
`schema-lockstep` job, and a `format` job, and is not present in
`.github/workflows/`. `_agent-setup/apply.ps1` is the copy step that was never
run.

The same unrun migration breaks a second set of references: `CLAUDE.md` points
at `.claude/skills/` and `.claude/settings.json`, and `CONTRIBUTING.md:59`
points contributors at both — but **`git ls-files .claude` returns nothing**.
The skills actually live at `_agent-setup/claude-config/skills/`. So the
project's own agent-guidance files reference paths that are not in the repo.

Consequence, measured: `dotnet format Sigil.slnx --verify-no-changes` **exits 2
on current `main`**, reporting 28 of 465 files needing formatting — including
`src/SigilBuild.Wrapper.Core/Json/SerializableInstallStep.cs`,
`Steps/ScheduledTaskCreateStep.cs`, and `Steps/FileCopyStep.cs`.

**Fix:** install `pr-guards.yml` into `.github/workflows/` and run
`dotnet format` once to clear the 28-file backlog (do these together — installing
the gate alone turns every subsequent PR red).

---

### R21 — Coverage gate is line-only and project-wide-only; per-assembly targets unenforced and unmet; three shipping assemblies absent from the denominator
**Component:** CI · **Effort: S**

> **STATUS (V1.1, 2026-09-09):** CLOSED with a written tolerance — and with almost
> no margin left — lane T1, [#23](https://github.com/Sigil-build/sigil/pull/23)
> `86c2799`. **Evidence T + M:** `ci.yml`'s `ASSEMBLY_FLOORS` and union floor
> (`:88`, `:100`, `:200`); measured on run `34362414470`: union **78.12 %** (floor
> 77), `SigilBuild.Core` **69.02 %** (floor **69**), `SigilBuild.Packaging`
> **86.62 %** (72), `SigilBuild.Signing` **68.79 %** (68),
> `SigilBuild.Wrapper.Core` **79.64 %** (79). **Narrower than recorded, twice
> over:** three shipping assemblies (`Cli`, `Wrapper`, `Installer.Host`) still
> report **0 lines** and are a `::warning::` only — tolerated in writing, not fixed
> — and `SigilBuild.Core` now sits **0.02 pp** above its floor, roughly one to two
> uncovered lines of slack on the assembly every lane touches. That margin is filed
> as **R71**. The ratchet policy itself (floors pinned to the measured value rounded
> down, nothing ever lowered) is the right one and must not be spent to make a red
> check green.

> **STATUS — FIXED in Stage 1** (lane T1, `86c2799` / PR #23), with one deliberate
> tolerance. Per-assembly floors are enforced, set at the **measured value rounded
> down** — a ratchet, not the aspirational `AGENTS.md` targets. The three
> zero-line assemblies are reported via `::warning::` but do **not** fail the build:
> hard-failing would have turned the required `build` check red at this merge and
> blocked every Stage 2/3 lane behind it. **This tolerance is temporary** — move
> `SigilBuild.Cli`, `SigilBuild.Wrapper` and `SigilBuild.Installer.Host` into
> `ASSEMBLY_FLOORS` the day each reports nonzero coverage.

`ci.yml:64` `THRESHOLD = 0.65`; `ci.yml:79-84` parses only `lines/line` `hits`,
so **branch coverage is never evaluated**; `ci.yml:96` *prints* the per-assembly
rate and `ci.yml:99` compares only the project-wide total. The
`CLAUDE.md`/`AGENTS.md:62` targets (Core ≥ 80 %, Signing ≥ 85 %) are enforced
nowhere — the gate's own comment (`ci.yml:58-59`) concedes they are aspirational.

Running the gate's exact algorithm over the local cobertura reports:
**project-wide union 13369/17786 = 75.17 %** (passes), but **`SigilBuild.Core`
= 63.89 %** — below its 80 % target *and* below the 65 % project gate — and
**`SigilBuild.Signing` = 68.79 %**. `ci.yml:60`'s comment claiming 65 % is "6pp
under the measured baseline" no longer matches. *(UNVERIFIED for CI proper:
these come from a local 2026-07-24 report tree that may contain multiple
generations; a CI run log is the authoritative source.)*

Separately, the `asm.startswith('SigilBuild')` filter (`ci.yml:75`) silently
admits only six assemblies. **`SigilBuild.Cli`, `SigilBuild.Wrapper`, and
`SigilBuild.Installer.Host` contribute zero lines** — the CLI entry point and
the entire Avalonia wizard could be at 0 % and the gate would not notice, since
`ci.yml:86-88` errors only when *no* package is found.

**Fix:** add an enforced per-assembly floor map and an expected-assembly
allowlist that fails when one is missing; parse branch coverage.

---

### R22 — Two of three VM jobs can pass vacuously
**Component:** CI · **Effort: S**

> **STATUS (V1.1, 2026-09-09):** CLOSED IN STRUCTURE, **claim-only in effect** —
> lane T1, [#23](https://github.com/Sigil-build/sigil/pull/23) `86c2799`.
> **Evidence C:** the marker and staged-host assertions are present
> (`wrapper-vm-tests.yml:86`, `:143`, `:175`), but the guards have **still never
> been observed refusing a marker-unset run**. The first-ever VM run
> ([34361541578](https://github.com/Sigil-build/sigil/actions/runs/34361541578),
> `da792fb`) failed on rotted fixtures (**R66**) before reaching them. This row
> already admits it, and the walk confirms that admission is the honest reading —
> it is one of only two claim-only rows in the register (the other is **R38**). The
> first automatic run
> ([34368896457](https://github.com/Sigil-build/sigil/actions/runs/34368896457),
> `b07021e`) is the next chance to observe the refusal for real.

> **STATUS — FIXED in Stage 1, UNPROVEN** (lane T1, `86c2799` / PR #23). Both jobs
> now assert the `SIGIL_VM_TESTS` marker **and** the staged host exe before
> `dotnet test`. The guards could **not** be proven by watching a VM job fail with
> the marker unset: `wrapper-vm-tests.yml` is `workflow_dispatch`-only and needs
> admin plus a disposable VM. That proof belongs to gate **G3**, not G1.

`wrapper-vm-tests.yml:231-236` (p11) hard-fails when the runner is not elevated,
with the comment *"an unelevated runner would make this whole job go green
having exercised nothing, silently."* The scope matrix (`:35-96`) and
`p12-update-webinstaller-vm` (`:125-171`) have no equivalent guard, so a dropped
`SIGIL_VM_TESTS` or a silently empty runtime staging turns both green.

**Fix:** assert `SIGIL_VM_TESTS=1` and the presence of
`runtimes/win-x64/SigilBuild.Installer.Host.exe` before `dotnet test` in both.

---

### R23 — No `SECURITY.md`, no `CHANGELOG.md`, and a third-party attribution gap that is a licence-compliance defect
**Component:** repo · **Effort: M**

> **STATUS (V1.1, 2026-09-09):** **HALF CLOSED** — lane REL,
> [#32](https://github.com/Sigil-build/sigil/pull/32) `f9d3af5`. **Evidence M + D:**
> `SECURITY.md`, `CHANGELOG.md` and `THIRD-PARTY-NOTICES.md` are all present, and
> the notices name Skia, ANGLE, HarfBuzz and libsodium explicitly (41 matching
> lines; all four names verified individually, not by a single grep). **Still open,
> and it is an owner action:** `gh api
> repos/Sigil-build/sigil/private-vulnerability-reporting` re-run during the walk
> returns **`{"enabled":false}`**. No lane PR can flip a repo setting, so this half
> is correctly recorded as unticked at G2 and belongs to the G4 owner checklist.

Verified absent on disk: `CHANGELOG.md`, `SECURITY.md`,
`THIRD-PARTY-NOTICES.*`, `NOTICE`. `LICENSE` (MIT), `CODE_OF_CONDUCT.md`
(Contributor Covenant 2.1, real contact at `:40`), and `CONTRIBUTING.md` are
present and good.

Three distinct problems, in priority order:

1. **No `SECURITY.md`.** For software that self-elevates, writes HKLM,
   registers COM and opens firewall rules, a researcher has no disclosure
   channel and GitHub shows "Security policy: not set". Given R1–R5, this is
   the item with real consequence.
2. **The notices gap is licence compliance, not politeness.** Every *NuGet
   package* is permissive (MIT/Apache-2.0/BSD-3-Clause), but the **native
   binaries redistributed beside `sigil.exe` are not all MIT**:
   `libSkiaSharp.dll` (11.09 MB) bundles Skia and ANGLE — **BSD-3-Clause**, and
   HarfBuzz — MIT; `libsodium.dll` (0.33 MB, via NSec.Cryptography) is
   **ISC**. All carry binary-redistribution attribution requirements that an
   MIT-only `LICENSE` does not satisfy. Shipping them with no notice file is a
   defect, not a nicety.
3. **No `CHANGELOG.md` and no release tag**, so nothing describes what the
   first release contains. Thirteen feature phases landed with no user-facing
   record and no baseline to diff against.

Other runtime dependencies needing attribution (from
`Directory.Packages.props`): Avalonia / .Desktop / .Themes.Fluent 12.0.2,
Svg.Skia 2.0.0.4, ZstdSharp.Port 0.8.8, YamlDotNet 16.1.3, System.CommandLine
2.0.0-beta4, System.Text.Json 9.0.0, Azure.Identity 1.13.1, Polly 7.2.4 +
Polly.Extensions.Http 3.0.0 (BSD-3-Clause),
Microsoft.Extensions.FileSystemGlobbing 9.0.0.

**Fix:** add `SECURITY.md` (contact, supported versions, disclosure window) and
enable GitHub private vulnerability reporting; generate
`THIRD-PARTY-NOTICES.md` covering the native payloads explicitly and ship it
beside the binaries; add `CHANGELOG.md` (Keep-a-Changelog) seeded from the T-
and P-track history.

---

### R23a — No lockfiles, no `NuGet.config`: the build is not reproducible
**Component:** build / supply chain · **Effort: M**

> **STATUS (V1.1, 2026-09-09):** CLOSED **for the Debug restore graph only** — lane
> REL, [#32](https://github.com/Sigil-build/sigil/pull/32) `f9d3af5`. **Evidence
> M:** `NuGet.config` present, 21 tracked `packages.lock.json`, and G2 check 2 =
> `dotnet restore Sigil.slnx --locked-mode` exit 0 from a clean clone. **Narrower
> than recorded:** that check runs in the implicit **Debug** configuration. The
> Release graph is never validated against the committed lock files — see **R65**,
> which the walk **live-reproduced** at `102ea3f`: one clean `dotnet build
> Sigil.slnx -c Release` left `src/SigilBuild.Installer.Host/packages.lock.json`
> modified in the working tree. "The build is reproducible" is true for Debug and
> unverified for Release.

`find . -name packages.lock.json` → none.
`grep -rn "RestoreLockedMode\|RestorePackagesWithLockFile"` → none.
`ls nuget.config NuGet.config` → none. `ci.yml:36` is a bare
`dotnet restore Sigil.slnx`.

`Directory.Packages.props` pins every *direct* version exactly (with central
management and transitive pinning enabled — that part is good), but
**transitive** dependencies still resolve to whatever the feed serves at
restore time, and with no `NuGet.config` the feed set is inherited from the
machine rather than declared by the repo. For a tool that signs and installs
privileged payloads, a build you cannot reproduce is a build you cannot audit
after the fact.

**Fix:** set `RestorePackagesWithLockFile=true` in `Directory.Build.props`,
commit the lock files, add `--locked-mode` to the CI restore, and add a
`NuGet.config` declaring nuget.org as the only feed.

---

### R24 — Version `0.0.1-alpha` is duplicated in four places with no single source of truth
**Component:** repo / CI · **Effort: S**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane REL,
> [#32](https://github.com/Sigil-build/sigil/pull/32) `f9d3af5`. **Evidence T + M:**
> `VersionCommandTests` (5) `Passed` on run `34362414470`; `git grep
> "0\.0\.1-alpha" -- '*.cs' '*.csproj' '*.yml' '*.props'` returns **nothing
> tracked** (G2 check 9); one source of truth at `Directory.Build.props:36`
> (`<Version>0.1.0-alpha</Version>`). A stale `obj/…/AssemblyInfo.cs` on the walk
> box still carried the old literal — untracked build output from a pre-RC build,
> not a defect, and recorded so the next person who greps a dirty tree is not
> misled.

```
src/SigilBuild.Cli/SigilBuild.Cli.csproj:9      <Version>0.0.1-alpha</Version>
src/SigilBuild.Cli/Program.cs:11                public const string Version = "0.0.1-alpha";
tests/SigilBuild.Cli.Tests/VersionCommandTests.cs:36   .Should().Be("0.0.1-alpha");
.github/workflows/ci.yml:136                    if ($output.Trim() -ne "0.0.1-alpha") { throw ... }
```

Cutting a release means editing four files, two of which fail loudly if you
forget. Blocks R7's release workflow.

**Fix:** single source in `Directory.Build.props`, generate the constant via
`AssemblyInformationalVersion` / a source generator, and have the test and CI
smoke assert *agreement* rather than a literal.

---

### R25 — README describes a different product than the one that shipped
**Component:** docs · **Effort: S**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane DOC,
> [#34](https://github.com/Sigil-build/sigil/pull/34) `50da43c`. **Evidence M:**
> `README.md:50-53` now states that NuGet, `winget` and the install script are
> "pre-MVP roadmap items, not…" rather than channels that exist.

`README.md` is 49 lines and **never mentions the exe wizard, `Setup.exe`, the
install-step engine, the rollback journal, or the uninstaller** — the flagship
output of both completed tracks and the single largest body of shipped work.

Meanwhile it promises "zstd dictionary-mode delta updates with a built-in
client SDK" (`:16-17`). Both halves are false:
`docs/architecture/adr-010-delta-update-deferral.md:18-25` (Accepted,
2026-07-23) states "**Delta (binary-diff) patches are explicitly deferred** …
no zstd-dictionary patch format exists yet", and there is no Update SDK project
in `src/` — `docs/sprint-01/identifier-reservation.md:23` still lists
`SigilBuild.UpdateSdk` as unreserved. It also offers macOS/Linux install
commands (`:25-29`) for a Windows-only product. So the README simultaneously
undersells what exists and oversells what does not. See also R7 on the
nonexistent install channels.

**Fix:** rewrite around what exists — `sigil.yaml` → `pack --format exe` → a
branded elevating wizard — with an honest status line and a "not yet built"
list.

---

### R26 — The docs teach a silent-install command the parser rejects, and name output files that do not exist
**Component:** docs · **Effort: S**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane DOC,
> [#34](https://github.com/Sigil-build/sigil/pull/34) `50da43c`. **Evidence T + M:**
> `CommandLineParserTests` (49) `Passed` on run `34362414470`;
> `docs/setup-exe-reference.md` documents at least 16 distinct runtime tokens (`/?`,
> `/D=`, `/LOG=`, `/PName=Value`, `/Poption.Name=Value`, `/S`, `/Uninstall`,
> `/Update`, `/allusers`, `/closeapps`, `/currentuser`, `/help`, `/lang=`,
> `/launch`, `/silent`, `/verysilent`) — the DoD's "fifteen" is now a floor, not a
> count; zero bare `/install_dir=` or `/edition=` remain under `docs/`; zero
> `uninstaller.exe` references outside plan docs. **The strongest half is the
> ceremony:** G2 check 1 copy-pasted the documented silent line against a real
> CI-built `Setup.exe` — exit `0`, files, state and ARP row all present, cleanly
> removed afterwards.

**The worst instance first.** Two guides document this verbatim:

```
docs/guides/installer-wizard.md:97   setup.exe /S /install_dir="C:\Apps\MyApp" /edition=professional
docs/guides/parameters.md:78         setup.exe /S /install_dir="C:\Apps\MyApp" /edition=professional
```

Parameter overrides are accepted **only** under a `P` prefix
(`Cli/CommandLineParser.cs:497`). A bare `/install_dir=` or `/edition=` falls
through to `:503` and throws
`UsageException: unrecognized flag '/install_dir=...'`. The prose form
`/Name=Value` is repeated at `installer-wizard.md:100`, `parameters.md:81`, and
`packaging-formats.md:39`. **Every user who copy-pastes the documented silent
install gets a hard failure** — plausibly the most-copied line in the docs.

**Missing reference.** `docs/cli-reference.md` (84 lines, generated by
`scripts/docs/generate-cli-reference.ps1`) covers only `sigil validate|init|pack|sign`.
The installer accepts fifteen tokens, enumerated in the parser's own error text
(`CommandLineParser.cs:372,504`). Runtime flags are scattered across five
guides, and `/verysilent`, `/launch`, `/Poption.Name=Value`, and `/?`|`/help`
are documented **nowhere**. `/D=` — the input to **R3** — is mentioned only in
passing at `upgrades.md:45`. The existing generator cannot fix this: it
introspects the `sigil` command tree, not `CommandLineParser`.

**Wrong filenames.** Code truth: `Engine/InstallSurvivability.cs:17`
`UninstallerFileName = "uninstall.exe"`, and
`ExeWrapper/ExeWrapperPackager.cs:134` + `:40`/`:47` →
`{App.Name}-{Version}-{arch}-Setup.exe` / `-WebSetup.exe`. Docs say
`uninstaller.exe` in six places (`docs/guides/uninstaller.md:7,20,32`,
`docs/README.md:28`, `docs/getting-started.md:174`,
`docs/guides/installer-wizard.md:54`, `docs/guides/packaging-formats.md:36` —
only `upgrades.md:20` is right) and `setup.exe` in `getting-started.md:120,131,174`.
The documented `UninstallString` (`uninstaller.md:20`) is therefore wrong, so
anyone scripting a silent uninstall against the docs points at a nonexistent path.

**Also wrong in `getting-started.md`:** `:42` calls `sigil.exe` "a single-file
~1 MB binary" (actual: **13.98 MB** plus two sibling native DLLs — see R7);
`:118` claims ZIP output goes to `./dist/<app-id>-<version>/` (actual:
`Zip/ZipPackager.cs:24-25` writes a flat
`{out}/{App.Id}-{Version}-{arch}.zip`); `:109-112` says "only the ZIP path is
functional in the current alpha. The MSIX path lands in Sprint 4" — stale by
roughly ten phases.

**Fix:** global-replace the parameter syntax to `/PName=Value`; add a
hand-written `docs/setup-exe-reference.md` covering all fifteen tokens and link
it from `docs/README.md`; correct the filenames and the four
`getting-started.md` errors.

---

### R26a — `architecture-overview.md` misstates the compression library, the crypto, the project layout, and what CI enforces
**Component:** docs · **Effort: S**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane DOC,
> [#34](https://github.com/Sigil-build/sigil/pull/34) `50da43c`. **Evidence M:**
> `docs/architecture-overview.md:55` and `:104` now state BCL `ECDsa` P-256 with no
> native crypto, and `:43` names `libSkiaSharp.dll` and `libsodium.dll` — the two
> native DLLs that actually ship.

This is a live, user-facing doc, not plan history:

- `:90` "Compression | **ZstdNet** + native fallback (zstd 1.5+ dictionary
  mode)" — wrong package and a native fallback that does not exist.
  `Directory.Packages.props:38-43` pins **ZstdSharp.Port** 0.8.8, pure-managed,
  "nothing to bundle". (The *code* comments are the accurate ones.)
- `:91` "Crypto (Ed25519) | NSec.Cryptography" — NSec survives only in
  `src/SigilBuild.Signing/Local/ZipManifestSigner.cs`; the update engine signs
  with **ECDSA P-256 via BCL `ECDsa`** (`adr-009-update-manifest-signature.md:257`).
- `:70-77` the component layout omits `SigilBuild.Signing`,
  `SigilBuild.Wrapper.Core`, and `SigilBuild.Localization.Generator` — four of
  nine projects — and still describes `SigilBuild.Wrapper` as the wizard
  engine, which moved to `Wrapper.Core` in T1.
- `:98` "These numbers are quality bars **enforced by CI, not aspirations**" —
  of seven rows CI enforces one (the 15 MB gate, `ci.yml:133`) plus coverage.
  Cold-start, pack time, sign latency, and "Delta patch generation ≤ 30 s" are
  unenforced, and the last is for a feature ADR-010 deferred.

**Fix:** regenerate the tech-stack table and component layout from
`Directory.Packages.props` + `Sigil.slnx`; relabel the metrics table "targets"
and mark which are actually gated.

---

### R27 — Colliding ADR numbers across two directories; CODEOWNERS protects the stale one and a file that does not exist
**Component:** docs / governance · **Effort: S**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane DOC,
> [#34](https://github.com/Sigil-build/sigil/pull/34) `50da43c`. **Evidence M:**
> `sigil-docs/` is gone; `docs/architecture/` holds adr-008…adr-014 with no
> colliding numbers; `CODEOWNERS` routes `/docs/architecture/` and carries the
> correction comment naming this row.

```
docs/architecture/       adr-008-expression-policy, adr-009-update-manifest-signature, adr-010-delta-update-deferral, adr-avalonia-aot, adr-msix-companion
sigil-docs/architecture/ adr-009-brand-token-runtime-json-vs-source-gen, adr-010-schema-validator-monolith
```

Two different ADR-009s and two different ADR-010s on unrelated subjects.
`CODEOWNERS` routes `/sigil-docs/architecture/` and `/sigil-docs/decisions.md`
to `@Sigil-build/tech-leads` — the first is the **stale** two-file directory and
the second **does not exist**. The live directory, which contains the update
signature ADR, has no tech-lead review requirement.

**Fix:** renumber the two orphans, move them into `docs/architecture/`, delete
`sigil-docs/`, and repoint CODEOWNERS at `/docs/architecture/`.

---

### R28 — `.sigil-bak` backups survive a successful install
**Component:** Wrapper.Core · **Effort: S**

> **STATUS (V1.1, 2026-09-09):** CLOSED, **thinly** — lane S5,
> [#29](https://github.com/Sigil-build/sigil/pull/29) `50e5de4`. **Evidence T:**
> `StashLifecycleTests` (2) `Passed` on run `34362414470`. **Narrower than
> recorded:** two tests for an entire stash lifecycle. The walk found no defect and
> is not reopening the row — it is recording that "closed" here rests on thin
> coverage, the same note as **R56**.

Re-verified and **confirmed**: `RollbackJournal.DiscardTransientStashes`
(`RollbackJournal.cs:48-63`) handles only `RestoreDeletedFile` (`:50`),
`RestoreDeletedDirectory` (`:53`), and `RestoreConfigFile` (`:56`), with
`default: break` at `:61-62`. The `.sigil-bak` copies made by
`Steps/FileCopyStep.cs:44-45` and `Steps/HttpDownloadStep.cs:66-67` are
journaled as `RestoreFile` and therefore persist in Program Files after a
successful reinstall.

Note the tension: this is *required* for uninstall to restore a pre-existing
overwritten file, so it is not simply a bug to delete. Prior review called it
"not discarded"; the accurate framing is "retained by design, with no lifecycle
or cleanup story."

**Fix:** decide the contract explicitly — either move the stashes into the
per-app state directory (out of Program Files) or discard them on the success
path and accept that uninstall cannot restore pre-existing files. Document it
either way.

---

### R29 — De-elevation fallback silently launches the app with the installer's admin token
**Component:** Wrapper.Core · **Effort: S**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane S5,
> [#29](https://github.com/Sigil-build/sigil/pull/29) `50e5de4`. **Evidence T:**
> `LaunchDeElevationTests` (4) `Passed` on run `34362414470`. Note the one
> deliberately inverted skip that belongs to this surface:
> `LaunchTests.LaunchAppUnelevated_direct_spawn_produces_the_observable_side_effect`
> skips **because** the CI runner is elevated and runs on an unelevated box — it is
> the entire local-21 vs CI-22 skip delta, and it is correct in both places.

`Engine/Launcher.cs:37-47`. The primary path is correct — Explorer's primary
token via `CreateProcessWithTokenW` (`:79-176`) — but on any failure it falls
through to `TryLaunchDirect(path, args)` (`:47`), handing the launched app the
installer's admin token with no log line and no user-visible signal. That is the
exact bug class the code exists to prevent, and it silently un-does P2's
acceptance criterion ("launch checkbox starts the app unelevated").

**Fix:** on de-elevation failure, skip the launch and surface a notice on the
Done screen rather than launching elevated.

---

### R30 — `sigil init`'s own template tells publishers to put a private-key file path in the manifest
**Component:** CLI templates · **Effort: S**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane S4,
> [#28](https://github.com/Sigil-build/sigil/pull/28) `3e94b8b`. **Evidence T + M:**
> `NetworkTrustParseTests.Updates_signingKey_must_be_a_base64_p256_spki` (6 cases →
> `SIG0325`), `ManifestParserFullTests` (6) and the schema fixture
> `invalid/signing-key-file-path.yaml` — `Passed` on run `34362414470`; G2 check 3
> ran `sigil init --template full-config` and then packed the result, exit `0`, with
> a negative control on the pre-R30 shape. Red assertion on the parent, shared with
> **R8**.

`src/SigilBuild.Cli/Commands/Templates/full-config.yaml:42` —
`signingKey: ./keys/update-signing.ed25519` — versus
`schemas/sigil-schema.json:469-471`, which says the field is "Base64-encoded
X.509 SubjectPublicKeyInfo (SPKI) DER of the ECDSA P-256 **PUBLIC** key … never
a private key, and never a file path". The value is passed through unvalidated
(`ManifestParser.cs:158` → `ExeWrapperPackager.cs:389`).

Following the template produces an installer whose every update attempt dies at
SIG0321 (fails closed, so not exploitable) — while actively steering publishers
toward committing a private-key path. Also names the wrong algorithm (Ed25519
vs the implemented P-256).

**Fix:** correct the template to a base64 SPKI placeholder and add a pack-time
diagnostic that `signingKey` decodes as base64 and imports as a P-256 SPKI.

---

### R31 — `schtasks /TR` is built by string concatenation with unescaped quotes
**Component:** Wrapper.Core / steps · **Effort: S**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane S2,
> [#21](https://github.com/Sigil-build/sigil/pull/21) `4505b24`. **Evidence T:**
> `StepValueInjectionTests` (15) `Passed` on run `34362414470`. Negative-test
> re-check — a **red assertion**, not a compile break: reverting
> `Steps/ScheduledTaskCreateStep.cs` to `4505b24~1` fails exactly 3 of 15
> (`Task_program_containing_a_quote_is_refused`,
> `Task_program_with_a_single_leading_quote_is_refused_too`,
> `The_step_reports_a_refused_program_as_a_step_failure_and_journals_nothing`) with
> 12 passing as positive controls. This also settles the audit's UNVERIFIED note on
> this row.

> **STATUS — FIXED in Stage 1** (lane S2, `4505b24` / PR #21). `"` is rejected in
> `program`, and the validation runs **before anything is journalled**. 12 of the 15
> R31/R32 tests fail at the parent; the 3 that pass are "still accepted" controls.

```
src/SigilBuild.Wrapper.Core/Steps/ScheduledTaskCreateStep.cs:110-112
    var trValue = string.IsNullOrEmpty(arguments) ? $"\"{program}\"" : $"\"{program}\" {arguments}";
```

The single concatenated command fragment in the privileged-step set. `program`
is substitutable (R3/R9) and an embedded `"` re-tokenizes the task's own command
line. It cannot reach `/RU` or `/RL` — those are separate `ArgumentList` entries
(`:114-128`) — so impact is confined to the task action.
*UNVERIFIED:* whether a leading-quote value can shift which token Task Scheduler
treats as the executable.

**Fix:** reject `"` in `program`, or emit the task via `schtasks /XML` with
proper escaping.

---

### R32 — `ini_write` does not escape CRLF: line injection into the INI
**Component:** Wrapper.Core / steps · **Effort: S**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane S2,
> [#21](https://github.com/Sigil-build/sigil/pull/21) `4505b24`. **Evidence T:**
> `StepValueInjectionTests` (15) `Passed` on run `34362414470`. **Narrower than
> recorded in one respect:** the parent revert is not isolable for this row —
> reverting `Steps/IniWriteStep.cs` alone does not compile (`error CS7036`, the
> required `stepType` parameter of `ConfigFileEditor.Edit`), so R32's fail-on-parent
> evidence is API-level, while **R31**'s from the same lane is a red assertion.

> **STATUS — FIXED in Stage 1** (lane S2, `4505b24` / PR #21). `\r`, `\n` and a
> leading `[` are rejected. The leading-`[` rejection on a *value* is a conservative
> over-rejection, as specified.

`Steps/IniWriteStep.cs:94` `lines[i] = key + "=" + value;` and `:105`
(insert path). `section`, `key`, and `value` are `ctx.Resolve`-expanded and
concatenated verbatim, so a value containing `\n[OtherSection]\nkey=…` writes
arbitrary INI entries into other sections. Matters when the value comes from a
wizard field or a `registry_read` var rather than a literal.

**Fix:** reject or escape `\r`, `\n`, and a leading `[` in section/key/value.

---

# POST-v1

### R33 — `XmlEditStep` relies on a framework default for XXE and has no entity-expansion cap

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane S6,
> [#30](https://github.com/Sigil-build/sigil/pull/30) `3be9187`. **Evidence T:**
> `ConfigEditorTests` (31) `Passed` on run `34362414470`, including
> `Xml_edit_refuses_a_document_declaring_an_internal_dtd_subset`,
> `Xml_edit_never_resolves_an_external_entity` and
> `Xml_edit_refuses_a_dtd_even_with_no_entities_at_all`. **The strongest
> negative-test evidence in the whole walk:** reverting `Steps/XmlEditStep.cs` to
> `3be9187~1` builds cleanly and then fails exactly those 3, with the other 28
> passing as positive controls — a red assertion with the invariant quoted in each
> failure message ("no DTD reaches this parser", not "no expensive DTD").

`Steps/XmlEditStep.cs:42-45` uses `new XmlDocument{…}` + `LoadXml` with no
`XmlResolver`/`DtdProcessing` assignment — and no such assignment exists
anywhere in the repo. On .NET 10 `XmlDocument.XmlResolver` defaults to `null`,
so external-entity file disclosure and SSRF are blocked — but that is an
unasserted framework default, not a stated invariant, and the internal DTD
subset is still parsed with no expansion cap (billion-laughs → OOM/hang of the
elevated installer, reachable when the target config sits somewhere an attacker
can write, per R16). No test asserts the XXE posture. **Fix:** set
`XmlResolver = null` explicitly, load via `XmlReader.Create` with
`DtdProcessing.Prohibit`, add a `<!DOCTYPE>` regression test. **S**

### R34 — Setup single-instance mutex fails open on the `NULL` branch

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane S5,
> [#29](https://github.com/Sigil-build/sigil/pull/29) `50e5de4`. **Evidence T:**
> `FilesInUseTests.A_squatted_guard_name_fails_closed_rather_than_pretending_to_hold_a_lock`
> `Passed` on run `34362414470` — and it earns the mention: it reaches the `NULL`
> branch by creating a *different* kernel object under the derivable name, rather
> than by mocking the branch.

`Engine/SetupInstanceLock.cs:49-53` names it
`Global\sigil-setup-<appId>-machine` (machine) / `Local\…` (user) — fully
predictable from the public app id. `:71-93` uses raw `CreateMutexW` and
branches only on `ERROR_ALREADY_EXISTS` (`:85`, fails closed → exit code) versus
`NULL` (`:77`), and the `NULL` branch — which is what a DACL-denied squat
produces — returns a non-owning sentinel (`:82`) indistinguishable from a real
lock, so two installs can proceed concurrently. Mitigating: creating a `Global\`
object needs `SeCreateGlobalPrivilege`, which standard users do not hold, so the
cross-user squat is unavailable on a default box; `Local\` squatting is
same-user only. **Fix:** distinguish `ERROR_ACCESS_DENIED` from other failures
and treat it as contention. **S**

### R35 — `json_edit` re-parses the resolved value as JSON

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane S6,
> [#30](https://github.com/Sigil-build/sigil/pull/30) `3be9187`. **Evidence T:**
> `ConfigStepsParseTests` (9), `ConfigStepRoundtripTests` (6), `ConfigEditorTests`
> (31), `ConfigStepIntegrationTests` (5) — all `Passed` on run `34362414470`.

`Steps/JsonEditStep.cs:163` `return JsonNode.Parse(value);` — documented as
intentional literal inference, but a value sourced from a wizard field or
registry var writes an object/array/`true` where the manifest author expected a
string. Encoding itself is safe. **Fix:** add `value_type: string|json`,
defaulting to `string`. **S**

### R36 — `com_register` runs publisher DLL code inside the elevated installer process

> **STATUS (V1.1, 2026-09-09):** CLOSED **as a decision** — lane S6,
> [#30](https://github.com/Sigil-build/sigil/pull/30) `3be9187`. **Evidence D:**
> `docs/architecture/adr-012-com-registration-isolation.md` records the decision to
> keep `com_register`'s in-process DLL load, with the rejected alternatives. No code
> change and no test, which is the correct shape for a decision row — what makes it
> closed is that the reasoning is written somewhere a reader will find it, unlike
> **R53**.

`Steps/Win32/ComRegistration.cs:66-101`, `ComRegisterStep.cs:62` —
`DllRegisterServer` executes in-process at high integrity, so a malformed or
hijacked DLL takes over the installer rather than a disposable child. The choice
is deliberate and documented (AOT/interop rationale, `ComRegistration.cs:9-29`).
**Fix:** document the trust assumption in an ADR, or invoke via a child process.
**S**

### R37 — `minFromVersion` floor is skipped when the installed version is malformed

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane S4,
> [#28](https://github.com/Sigil-build/sigil/pull/28) `3e94b8b`. **Evidence T:**
> `UpdateRunnerTests` (24) `Passed` on run `34362414470`.

`Update/UpdateRunner.cs:141-163` enforces the floor only when
`VersionComparison.IsWellFormed(state.InstalledVersion)`; otherwise it logs and
proceeds. For a user-scope install the version comes from HKCU, so a user can
steer their own eligibility — publisher policy, not a security boundary, and at
least logged. **Fix:** treat an incomparable version as not-eligible. **S**

### R38 — Restart Manager session key is a mutated managed `string`

> **STATUS (V1.1, 2026-09-09):** CLOSED, **claim-only** — lane S5,
> [#29](https://github.com/Sigil-build/sigil/pull/29) `50e5de4`. **Evidence C:** the
> `ref ushort` / caller-allocated `char[]` signature change and its explaining
> comments at `Engine/FilesInUse.cs:235` and `:618` are present in the tree, but
> **no test anywhere in `tests/` names R38, `strSessionKey` or `RmStartSession`**.
> The justification — "no behaviour change; the existing files-in-use tests are the
> coverage" — lives only in [#29](https://github.com/Sigil-build/sigil/pull/29)'s
> PR body, and until this line it was written nowhere in the register. It is one of
> only two claim-only rows (the other is **R22**), and it is the same shape as
> **R72**. Fix shape if it is ever worth one: a test that pins the marshalling
> contract, not the caller.

`Engine/FilesInUse.cs:209-210` — `[LibraryImport]` with UTF-16 marshalling pins
the managed string's buffer and `RmStartSession` writes 32 chars + NUL into it.
The size is exact and `new string(char,count)` is never interned, so there is no
overflow today, but it is one refactor away from corrupting an interned literal.
**Fix:** use a `char[33]`/`Span<char>` with a `ref char` signature. **S**

### R39 — Channel manifest JSON is parsed before its signature is verified

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane S4,
> [#28](https://github.com/Sigil-build/sigil/pull/28) `3e94b8b`. **Evidence T:**
> `UpdateRunnerTests` (24), `ChannelManifestParserTests` (15) — `Passed` on run
> `34362414470`.

`Update/UpdateRunner.cs:105` (parse) precedes `:116` (verify). No parsed field is
used before verification, so this is not exploitable — but it exposes the JSON
parser to unverified network input and lets an attacker choose which diagnostic
the user sees. Verify-then-parse is the cheaper invariant to keep true.
**Fix:** swap the blocks. **S**

---

# NOTE

### R40 — `.gitignore:41` contains `./docs/`, which git never matches

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane F0,
> [#16](https://github.com/Sigil-build/sigil/pull/16) `c82f5eb`. **Evidence M:**
> `.gitignore` re-read during the walk — the never-matching `./docs/` line is gone
> and there is **no `docs` entry at all**, which is the correct end state.

A leading `./` makes the pattern inert. Harmless today (`docs/` is tracked and
should be), but it silently does nothing, so whatever it was meant to exclude
isn't. **Fix:** delete the line or write the intended pattern. **S**

### R41 — Repo hygiene for a first public read

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane F0,
> [#16](https://github.com/Sigil-build/sigil/pull/16) `c82f5eb`. **Evidence M:**
> `git check-ignore -v .superpowers` → `.gitignore:44:.superpowers/` (exit 0);
> `_agent-setup/` absent; `git ls-files .claude` non-empty (the agent config is
> deliberately tracked); `git ls-remote --heads origin` shows only `main`,
> `release/v0.1.0-alpha` and the live fix-lane branches.

> **Corrected 2026-07-28 and RESOLVED.** The audit originally reported 15 stale
> remote branches from `git branch -r`. That count was wrong: 7 had already been
> deleted on GitHub (auto-delete-on-merge) and only stale local remote-tracking
> refs made them appear live. The true figure was **8**. All 8 have since been
> archived as `archive/<name>` tags (pushed to origin, so every commit stays
> recoverable) and deleted. `git ls-remote --heads origin` now returns `main`
> and `release/v0.1.0-alpha` only. Lesson recorded because it bit this audit:
> run `git fetch --prune` before reading `git branch -r`.

`git ls-remote --tags origin` → one non-release tag (`exe-installer-v1`), plus
the 15 `archive/*` tags added during cleanup (safe to delete once the RC merges).

`_agent-setup/` is tracked (8 files). Nothing in it is private or embarrassing,
but `apply.ps1:25` ends by telling the reader to **delete `_agent-setup/`** —
it is a half-finished migration whose own instructions say to remove it, and
because it was never run, a real CI gate is missing (R20).

`.superpowers/` is **not tracked** (`git ls-files .superpowers` → empty) — but
the prior review's concern is **confirmed**, not dismissed: `git check-ignore -v
.superpowers` **exits 1**, so the root `.gitignore` does not exclude it. The
sole exclusion is a nested `.superpowers/sdd/.gitignore` containing `*`. The
moment any tool writes `.superpowers/<anything-but-sdd>`, internal agent review
diffs become tracked files in a public repo.

`publish/` is untracked and the repo is small (1.99 MiB packed), though a stale
~168 MB `publish/win-x64/` (including an 84 MB `libSkiaSharp.pdb`) sits in the
working tree — correctly ignored, worth deleting before any archive or export.

**Fix:** delete the merged branches; run `_agent-setup/apply.ps1`, commit
`.claude/` + `.github/workflows/pr-guards.yml`, then `git rm -r _agent-setup/`;
add `.superpowers/` to the root `.gitignore`.

### R41a — `docs/sprint-01/identifier-reservation.md`: the NuGet ID is still unclaimed

> **STATUS (V1.1, 2026-09-09):** **NOT CLOSED — DOCUMENTED ONLY. This row stays
> OPEN, and the Stage 2/3 table above is corrected accordingly.** That table listed
> R41a among [#34](https://github.com/Sigil-build/sigil/pull/34) `50da43c`'s
> closures. **Evidence D:** what #34 actually landed is a written banner at
> `docs/sprint-01/identifier-reservation.md:5-15` recording that both IDs are still
> unclaimed — an honest doc, not a fix. **Both `SigilBuild` and
> `SigilBuild.UpdateSdk` remain unreserved on nuget.org**, while `README.md` still
> tells users `dotnet tool install -g SigilBuild`. Reserving them is an **owner
> action on the G4 checklist** (also called out in `02-READINESS_REPORT.md`'s
> sequencing note), not something any lane PR can deliver. This is the first of the
> two consequences that put **R73** on the board: a row can be recorded as closed
> in a merge table while its actual disposition is "documented open".

`docs/sprint-01/identifier-reservation.md:13` marks `SigilBuild` as a "Reserved
placeholder **to be published** before Sprint 1 ends"; `:23`
`SigilBuild.UpdateSdk` "Pending public reservation"; `:88-89` the social handle
and a USPTO/EUIPO trademark search are both "to complete before public launch".
Meanwhile `README.md:32` tells users to `dotnet tool install -g SigilBuild`.
Going public with an unclaimed package ID that your own README advertises
invites a name squat — the classic supply-chain attack on a new project.
**Fix:** reserve both IDs on nuget.org **before** the repo goes public; then
update or delete the doc. **S**

### R42 — Supply chain: preview/beta dependencies, no vulnerability scanning

> **STATUS (V1.1, 2026-09-09):** PARTIALLY CLOSED — lane SUP,
> [#33](https://github.com/Sigil-build/sigil/pull/33) `4dc7820`; the missing SBOM
> half is in flight as [#42](https://github.com/Sigil-build/sigil/pull/42),
> **open**. **Evidence M + D:** `.github/dependabot.yml` declares `nuget` and
> `github-actions`; the `vulnerability-scan` job (`ci.yml:375`) **ran and
> succeeded** on run `34362414470` — confirmed from the run, not inferred from a
> green build (G2 check 10); SkiaSharp is on stable `3.119.4`; the
> `System.CommandLine` beta deferral is written in the row. **Narrower than
> recorded — the SBOM deliverable was orphaned between two merged lanes:** `grep
> -in "sbom\|cyclonedx\|spdx" .github/workflows/release.yml` returned **zero hits**
> at `102ea3f`. SUP wrote `docs/plan/release/sup-sbom-handoff.md` for REL to fold
> in, but REL ([#32](https://github.com/Sigil-build/sigil/pull/32)) merged
> **before** SUP ([#33](https://github.com/Sigil-build/sigil/pull/33)), so no lane
> could apply it and nothing in either PR was wrong. Filed as **R70**.


> **STATUS — PARTIALLY CLOSED in Stage 3** (lane SUP, `rc/sup-supply-chain`).
> Done: `.github/dependabot.yml` (nuget + github-actions, weekly);
> `dotnet list package --vulnerable --include-transitive` as a **separate**
> failing CI job (`vulnerability-scan` in `ci.yml`, not folded into `build`,
> so an external advisory landing mid-track goes red on its own check rather
> than on the coverage-gated build — see the job's comment for the reasoning);
> a real local run of the scan (recorded below — **zero** vulnerable packages,
> including for the four UNVERIFIED names this row called out); SkiaSharp
> moved off the preview build entirely (`3.119.4-preview.1.1` ->
> **`3.119.4`** stable), which required bumping Avalonia `12.0.2` ->
> **`12.0.5`** (Avalonia.Skia's own `.nuspec` pins SkiaSharp to an exact
> version per release; 12.0.2 still requires the preview, 12.0.5+ requires
> the stable release — verified against nuget.org, not assumed). Full suite
> green after the bump (1538/1538, 27 skipped, matching the pre-bump
> baseline exactly).
>
> **Not done, on purpose:** `System.CommandLine` stays at
> `2.0.0-beta4.22272.1`. 2.0.0 GA has since shipped (now at 2.0.11 on
> nuget.org, with a 3.0 preview line already underway) — the beta is not
> just stale, it's obsolete. But the 2.0 API changed from the beta series,
> five files under `src/SigilBuild.Cli/Commands/` and `Program.cs` (~450
> lines) plus ~48 references across `SigilBuild.Cli.Tests` would need
> rewriting, and this is a security/supply-chain lane, not a CLI-surface
> lane. Budgeted as a small, self-contained follow-up task (estimate: a few
> hours — narrow surface, no cross-project fan-out) for a future PR, not
> attempted here.
>
> Also not done: SBOM generation, because it targets `release.yml`, which
> lane REL creates and which does not exist yet in SUP's worktree (branch
> cut before REL merges). The exact step and its placement are written down
> at `docs/plan/release/sup-sbom-handoff.md` for REL to fold in.

Every package in `Directory.Packages.props` is pinned to an exact version — no
wildcards, no floating ranges — with central management and transitive pinning
on. That part is good. (Reproducibility is R23a.)

Two **runtime** dependencies are not stable releases:
`SkiaSharp` / `SkiaSharp.NativeAssets.Win32|Linux` at **`3.119.4-preview.1.1`**
(`Directory.Packages.props:45-47`) — a preview *native* binary inside
privileged software, taken per the inline comment only "to satisfy Avalonia 12
transitive requirement" — and `System.CommandLine` at
**`2.0.0-beta4.22272.1`** (`:35`), the September-2022 build: roughly four years
stale, API-incompatible with later betas, and unlikely to receive a security
fix.

No `.github/dependabot.yml`, no `dotnet list package --vulnerable` step, no
SBOM (`grep -rn "dependabot\|--vulnerable\|sbom\|cyclonedx"` → zero hits). The
only security automation is gitleaks (`secret-scan.yml`), which addresses an
unrelated threat. *UNVERIFIED:* I did not assess these specific versions
against advisory databases — `Azure.Identity 1.13.1`, `Polly 7.2.4`,
`System.Text.Json 9.0.0`, `NJsonSchema 11.0.2` all warrant a real scan. Add the
scan rather than trust a judgement call.

*(`FluentAssertions` staying on 6.12.1 is correct — 8.x is commercially
licensed.)*

**Fix:** add Dependabot (nuget + github-actions, weekly) and a
`dotnet list package --vulnerable --include-transitive` CI step failing on
High/Critical; add SBOM generation to the release workflow; move SkiaSharp to
the newest stable that satisfies Avalonia 12 or record the constraint in an
ADR. **M**

### R43 — Plan docs are stale about their own state

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane DOC,
> [#34](https://github.com/Sigil-build/sigil/pull/34) `50da43c`. **Evidence M:**
> `docs/plan/ORCHESTRATION_PLAN.md:3-8` and
> `docs/plan/feature-parity/01-IMPLEMENTATION_PLAN.md` now carry read-only-history
> banners pointing at this register.

`docs/plan/ORCHESTRATION_PLAN.md:6` claims "527 tests green"; the measured total
is **1097**. `ORCHESTRATION_PLAN.md` never mentions the P-track at all.
`docs/plan/feature-parity/01-IMPLEMENTATION_PLAN.md:188` still shows P13 as
Pushed ☐ / Merged ☐ — while P13 is commit `1be494c` on `main`, i.e. the audited
HEAD. Per `AGENTS.md`, `docs/plan/*` is read-only history and should **not** be
edited to match; this register is the correction. **Fix:** none — cite this file
instead. **S**

### R44 — S2's `allow_outside_install_dir` has no counterpart in S1's replay anchor: a supported opt-out leaves the app unremovable
**Component:** Wrapper.Core / Engine + manifest · **Effort: M** · **Cross-lane: S1 × S2**

> **STATUS (V1.1, 2026-09-09):** CLOSED with a deliberate residual — lane S7,
> [#31](https://github.com/Sigil-build/sigil/pull/31) `2e32c83`. **Evidence T + D:**
> `SignedAnchorageTests` (28), `UninstallAnchorSelectionTests` (6) — `Passed` on run
> `34362414470`. **Deferral, written in two places:** the "anchor floor stays
> equality-only" residual is deliberately unfixed, with the reasoning in
> `UninstallEngine.IsPlausibleInstallDirectory`'s `<remarks>` (verified present
> during the walk) and in this row. Negative-test re-check: `ReplayAnchor.Notices`
> is absent at the parent (`2e32c83~1`).

Raised by lane S1 during its branch-review fix wave, from finding I-2 of
`reports/s1-branch-review.md`. Neither lane can see this alone; it appears only
once both are merged.

**Lane S2** adds `allow_outside_install_dir` as a first-class, schema-and-docs
manifest opt-out for a step whose destination is outside `install_dir`, and
**documents `%ProgramData%\MyApp` as the example** of when to use it.

**Lane S1** anchors rollback-journal replay (R1 clause (c)). The allowed roots are
`install_dir`, the replayed scope's Desktop and Start Menu folders, and the app's
own `<StateRoot>\Sigil\<AppId>` directory (`ReplayAnchor.For`). There is no
counterpart to S2's opt-out, and nothing carries it into the persisted journal.

**Composite failure.** A publisher follows S2's documented guidance and
`file_copy` / `directory_create` / `ini_write`s into `%ProgramData%\MyApp`. Every
one of those records is refused at uninstall; the data stays on disk; the ARP row
and the uninstall state are removed anyway; and the log reports refusals for a
**supported feature**. That is the "silently unremovable" class S1 closed four
separate routes into, arriving through a door neither lane owns.

**Why S1 did not fix it in-lane** — considered and rejected deliberately, not
overlooked:

1. It needs a persisted-format change (a per-record "this step declared itself
   out-of-tree" marker) at the final fix wave of a branch already verdicted READY,
   with no producer for the field on the branch: it would ship unpopulated and
   untestable end to end.
2. **More importantly, the naive form is unsafe.** The journal is the untrusted
   artefact. A record carrying "I was declared out-of-tree" is a record saying "do
   not anchor me", so a planted journal could opt itself out of the whole
   mechanism R1 exists to build. To be safe the marker must be cross-checked
   against a trusted copy of the manifest at replay time — and `UninstallEngine`
   does not have the blob today. That is a design change, not a field.

**Fix (Stage 2, to land before or with the first shipped build containing S2):**
resolve the manifest's declared out-of-tree destinations from the **signed blob**
at replay time and widen `ReplayAnchorage` with them; the journal records nothing
new and nothing is trusted from it. Until then `docs/guides/uninstaller.md`
documents the symptom and points publishers at an `uninstall:` step, which runs
before the journal replay and is not anchored. **M**

**Related, and deliberately unfixed — the anchor floor stays equality-only.**
`UninstallEngine.IsPlausibleInstallDirectory` rejects only a volume root and exact
matches against the well-known system directories, so a journal recording
`installDir: C:\ProgramData\Sigil` re-widens the anchor to the shared state-root
parent — i.e. back inside the threat model that the per-app narrowing
(branch-review finding 3) closes. Tightening the floor to require
`StateDirectorySecurity.IsAdminOnlyWritable` for machine scope would refuse the
uninstall of exactly the installs **this row's own lane S2 grandfathers** — those
sitting outside the `%ProgramFiles%` roots because they predate containment — so
the human partner ruled it stays as it is. The reasoning is recorded in the
method's remarks; the residual is bounded because the escalating consequences (a
machine-wide execution mapping, a machine `PATH` entry) each independently require
an admin-only-writable target. Recorded here so the S1 × S2 interaction is not
rediscovered as a new finding.

---

# Filed during Stage 1

Rows **R45–R57** were not in the 2026-07-28 audit. Every one was found while
fixing something else — by a lane, or by a review of a lane — and filed rather
than absorbed, per the "a lane that finds a gap not in the register stops and
files a new row" rule in `03-RC_ORCHESTRATION.md`. They are grouped by the lane
that raised them; severity is per row.

## From lane S3 (downloaded-binary trust)

The lane's own call: **R45 and R48 are the two it would not ship without.**

### R45 — The downloaded-binary signature policy is inferred, not declared
**Component:** Wrapper.Core + manifest · **Effort: S** · **SHOULD-FIX**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane S4,
> [#28](https://github.com/Sigil-build/sigil/pull/28) `3e94b8b`. **Evidence T:**
> `NetworkTrustParseTests.An_unknown_require_signed_downloads_value_is_refused` (4
> cases), `DownloadPolicyTests` (9), and the schema fixtures
> `valid/network-trust.yaml` + `invalid/unknown-require-signed-downloads.yaml` —
> `Passed` on run `34362414470`. Red assertion on the parent, shared with **R8**.

R11's gate decides whether a downloaded binary must be Authenticode-valid by
reading `SignDeclared` — i.e. "did this publisher configure signing for their own
output" — and using it as a proxy for "should downloads be verified". Those are
different questions, and a publisher who signs nothing gets no verification on
anything they download and run elevated.

**Fix:** an explicit `installer.require_signed_downloads`, defaulting to
`SignDeclared` so behaviour is unchanged, blob-carried and pack-time validated.
Smallest row of the four, and it names the policy the other three argue about.

### R46 — A blackholed CRL/OCSP responder suppresses revocation
**Component:** Wrapper.Core · **Effort: M** · **SHOULD-FIX**

> **STATUS (V1.1, 2026-09-09):** CLOSED with a written limitation — lane S4,
> [#28](https://github.com/Sigil-build/sigil/pull/28) `3e94b8b`. **Evidence T + D:**
> `DownloadPolicyTests` (9) covering `require_signed_downloads:
> always_verified_revocation`, `Passed` on run `34362414470`; ADR-011 records the
> chosen opt-in-hard-fail mechanism **and the three rejected alternatives**.
> **Narrower than recorded:** there is still no observed-API revocation fixture, so
> the behaviour under a genuinely blackholed responder is reasoned, not observed.
> Stated here and in **R17**.

R17 now checks revocation, and correctly renders "unavailable" as distinct from
"trusted". But `RevocationUnavailable` is not a refusal, so revocation of a stolen
signing key is suppressible by anyone who can blackhole two hostnames — on a
corporate network, a captive portal, or a compromised resolver.

**The only row in this group with a live adversary.** Candidate fixes — OCSP
stapling via the signed channel manifest, Microsoft's disallowed-certificate list,
known-good caching with transition detection, opt-in hard-fail — are all design
conversations, and one of them touches the unbuilt publish stage. Pick one
deliberately; do not let the default stand by inattention.

### R47 — One `fdwRevocationChecks` constant serves two callers that want opposite policies
**Component:** Wrapper.Core · **Effort: M** · **POST-v1**

> **STATUS (V1.1, 2026-09-09):** CLOSED **as a written deferral** — lane S4,
> [#28](https://github.com/Sigil-build/sigil/pull/28) `3e94b8b`. **Evidence D:**
> ADR-011 § "Stated limitations (filed, not built)" files this row as post-v1 and
> records the trap for whoever eventually splits the constant. **Bookkeeping note
> from the walk:** #28's *title* names only R8, R13, R14, R30, R37, R39, R45, R46 —
> R47 and R49 are in its diff and in the Stage 2/3 outcome table, but not the
> title. Both are genuinely discharged; an auditor reading PR titles alone would
> wrongly count them dropped. Title/table mismatch only, no missing work.

The security gate wants to be strict and online. The wizard's cosmetic "Signed
by …" line wants to be fast and never block. They share one constant.

**Caveat for whoever splits them:** a cache-only trust line that renders
*identically* to an online-verified one reintroduces R17's bug in a new place —
the operator cannot tell the two apart, which was the whole defect.

### R48 — The trust-line lookup blocks the wizard's UI thread
**Component:** Installer.Host · **Effort: S** · **RELEASE-GATING**

> **STATUS (V1.1, 2026-09-09):** CLOSED for the fix; the measurement is explicitly
> the human partner's — lane S5,
> [#29](https://github.com/Sigil-build/sigil/pull/29) `50e5de4`. **Evidence T + D:**
> `TrustLineActivationTests` (3) for the off-thread fix, `Passed` on run
> `34362414470`. **Deferral, written:** the worst-case cold-cache measurement stays
> a human-partner item, with the 335 ms best-case floor and two measurement traps
> recorded in this row. The walk's cross-check confirms **the split held** — no
> lane quietly absorbed the measurement with a convenient fast run, which was the
> stated risk when it was split.

The call site is **confirmed** UI-blocking.

**A best-case floor was measured 2026-08-11 and it already justifies the fix.**
`AuthenticodeVerifier.VerifyFileStatus` against an embedded-signed, DigiCert/
GlobalSign-chained binary, on this box, **online, with a warm OS certificate
cache**:

```
run 1:  335 ms   Trusted
run 2:    9 ms   Trusted
run 3:    6 ms   Trusted
```

**335 ms is the happy path** — network up, responder reachable, cache populated —
and it is already past the ~100 ms at which a UI reads as unresponsive. Every
condition that makes this worse (cold cache, captive portal, unreachable CRL
distribution point) moves in one direction only.

Two measurement traps, both hit while producing that number:

1. **Do not measure a Windows system binary.** They are *catalog*-signed, and
   `WinVerifyTrust` with `WTD_CHOICE_FILE` reports `NoSignature` without ever
   reaching a revocation lookup — `notepad.exe` measures **0 ms** no matter how
   broken the stall is. The target must carry an **embedded** signature.
2. **A fast run is a run that was not set up.** Anything under ~200 ms means the
   cache was warm and the network was up.

**Still outstanding: the worst case.** The ~15 s figure quoted originally is
Windows' documented CRL timeout, not an observation. That needs an offline,
cold-cache first run on real hardware — `certutil -URLCache * delete`, then
disable the adapter — which no agent run can produce from an online box. It is
the human partner's measurement.

**The off-thread fix does not wait for it.** 335 ms on the happy path settles
whether the fix is worth doing; the offline number only settles how bad the
worst case was.

### R49 — Authenticode validity is integrity, not publisher identity
**Component:** Wrapper.Core · **Effort: M** · **POST-v1**

> **STATUS (V1.1, 2026-09-09):** CLOSED **as a written deferral** — lane S4,
> [#28](https://github.com/Sigil-build/sigil/pull/28) `3e94b8b`. **Evidence D:**
> ADR-011 § Stated limitations records that Authenticode validity proves integrity,
> not publisher identity, and files the gap rather than half-building a
> subject-pinning mechanism. Same title/table mismatch as **R47** — present in
> #28's diff, absent from its title.

`WinVerifyTrust` accepts any chain the machine trusts, **including a root any
non-administrator can install into their own store**. So "Authenticode-valid"
means the bytes were not altered after signing — not that the publisher signed
them. Pinning needs an authenticated publisher identity in the pack-time manifest,
which does not exist yet. Filed rather than half-built; recorded here so nobody
reads R11's fix as identity verification.

### R50 — One `New-Item` at `%ProgramData%\sigil-runtime` costs every elevated install ~18 MB
**Component:** Wrapper.Core / native bootstrap · **Effort: M** · **SHOULD-FIX**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane S6,
> [#30](https://github.com/Sigil-build/sigil/pull/30) `3be9187`. **Evidence T:**
> `NativeRuntimeReclaimTests` (10) `Passed` on run `34362414470`.

The R4 fix falls back to a per-run GUID directory when the shared cache root
cannot be established or repaired. A squat that cannot be repaired — a *file* at
that path, or an owner-pinned deny ACE — is not a security hole (that is the
designed refusal), but the fallback is **never reclaimed**, so an unprivileged
user can arm an unbounded ~18 MB-per-install disk leak with one command.

**Constraints any fix must meet — these are what killed the original sweep:**
guards must read from an **open handle** with reparse-point checks, never through
the path, and a reclaim must not race a concurrent install that is using the
directory it is about to delete.

## From lane S1 (trusted state)

### R51 — Registry replay anchoring is a denylist, and it is not converging
**Component:** Wrapper.Core / Engine + wire schema · **Effort: M** · **SHOULD-FIX**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane S7,
> [#31](https://github.com/Sigil-build/sigil/pull/31) `2e32c83`. **Evidence T:**
> `SignedAnchorageTests` (28), `RegistryRecordProducerTests` (2),
> `ReplayAnchoringTests` (115) — `Passed` on run `34362414470`. Negative-test
> re-check: `ReplayAnchor.Notices` absent at the parent, which is where the denylist
> stopped being a denylist.

R1's replay anchor permits registry writes by denying known-dangerous key shapes.
Three consecutive review rounds each produced another name the denylist missed —
`Classes\…\shell\open\command` and `App Paths\*`, then `txtfile`, `lnkfile`,
`mscfile`, `Drivers32`. The lane was told to stop adding names and deny the
*shape*, which it did; that bought time, it did not close the class.
**Enumeration is losing to the search space.**

**Durable fix:** a manifest-declared registry key allowlist, resolved at replay
time from the **signed blob** — not carried in the journal, for exactly the reason
in R44: the journal is the untrusted artefact, so a permission it carries about
itself is worthless. Ruled by the human partner as a later stage because it is a
wire-schema change.

### R52 — `ScopeLayout` models one install root while containment accepts three
**Component:** Wrapper.Core / Engine · **Effort: S** · **SHOULD-FIX**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane S6,
> [#30](https://github.com/Sigil-build/sigil/pull/30) `3be9187`. **Evidence T:**
> `ScopeLayoutTests` (7) `Passed` on run `34362414470`.

`ScopeLayout.cs:61-65` hardcodes `SpecialFolder.ProgramFiles` as *the* machine
install root. Lane S2's containment accepts both `%ProgramFiles%` roots, and
correctly declined to widen a shared surface mid-lane. The result is that the
**permitted** destinations and the **default** destination are now described in
two places and can drift apart silently.

**Fix:** give `ScopeLayout` a root *set* and derive containment from it.

### R53 — An elevated process replays user-scope state at all
**Component:** Wrapper.Core / Engine · **Effort: M** · **POST-v1**

> **STATUS (V1.1, 2026-09-09):** CLOSED **as a decision**, with a discoverability
> defect now fixed — lane S5, [#29](https://github.com/Sigil-build/sigil/pull/29)
> `50e5de4`. **Evidence D:** the decision — an elevated run *may* replay user-scope
> state, and why that is acceptable given R1's provenance gate — is recorded at
> `src/SigilBuild.Wrapper.Core/Engine/InstallSession.cs:1130`. **Narrower than
> recorded:** until this status line, that code comment was the **only** place the
> justification existed. No register note, no ADR. A reader of this row could not
> tell the behaviour had been decided rather than forgotten — which is the second
> of the two consequences that put **R73** on the board.

`PerformReinstallCleanupAsync` with `_scope == User` replays state out of the
user's own profile from an elevated process. R1 clause (b) stopped a *machine*
operation from crossing into `%LocalAppData%`; this is the different question of
whether an elevated run should replay user-scope state **even when the scope is
genuinely user**. The records are anchored, so this is not the pre-R1 hole — but
the privilege asymmetry is the same shape and deserves a decision rather than an
inheritance. Raised by S1.3.

### R54 — `shortcut_create.location`'s explicit-path branch has no containment
**Component:** Wrapper.Core / steps · **Effort: S** · **SHOULD-FIX**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane S6,
> [#30](https://github.com/Sigil-build/sigil/pull/30) `3be9187`. **Evidence T:**
> `ShortcutCreateStepTests` (8), `ReinstallIdempotencyTests` (1) — `Passed` on run
> `34362414470`. Negative-test re-check:
> `ShortcutCreateStep.CheckLocationContained` is absent at the parent.

Pre-existing, and outside lane S2's task list. The named anchors
(`start_menu`, `desktop`, …) are contained; an explicit path is not.

## From lane T1 and the merge gate

### R55 — The docs still teach the broken `parameters.install_dir` idiom, and two migration guides state something false
**Component:** docs · **Effort: S** · **SHOULD-FIX** · **route to lane DOC (Stage 3)**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane DOC,
> [#34](https://github.com/Sigil-build/sigil/pull/34) `50da43c`. **Evidence M:**
> zero "auto-inserted" / "automatically inserted" claims remain under
> `docs/migration/` or `docs/guides/`, and zero instances of the broken
> `install_dir:` parameter-declaration idiom survive in the five named files.

Declaring a parameter named `install_dir` creates a *second*, unrelated value that
diverges from the real one the moment a user installs anywhere but the default.
The correct idiom is `{install_dir}` — the single value that the default,
`installer.install_dir:`, the wizard's Destination screen, `/D=`, upgrade-in-place
and the containment guards all agree on. Lane S2 converted 13 snippets in the
files it owned; the idiom survives in:

- `docs/guides/parameters.md:63`
- `docs/guides/uninstaller.md:61`
- `docs/migration/from-inno.md:21`
- `docs/migration/from-wix.md:65`
- `docs/migration/from-nsis.md:55`

**`from-wix.md` and `from-nsis.md` additionally assert that a destination screen is
"auto-inserted when `parameters.install_dir` is declared". That is false** —
`InstallerViewModel.cs:1045` adds it unconditionally. Migration guides are the
first thing a switching publisher reads.

**Also for lane DOC, a release note rather than a doc fix:** S2's
`directory_create` containment required the `allow_outside_install_dir` opt-out in
**11 pre-existing fixtures**. That is real ergonomic friction for any publisher
creating a `%ProgramData%` directory, and they should meet it in the release notes
rather than in a failed install.

### R56 — Hook-phase refusal notices go nowhere
**Component:** Wrapper.Core / Engine · **Effort: S** · **SHOULD-FIX**

> **STATUS (V1.1, 2026-09-09):** CLOSED, **thinly** — lane S5,
> [#29](https://github.com/Sigil-build/sigil/pull/29) `50e5de4`. **Evidence T:**
> `HookProgressSinkTests.A_hook_phase_gives_its_steps_somewhere_to_report_refusals`
> `Passed` on run `34362414470`. **Narrower than recorded:** that is **one** test
> for all four hook phases. No defect found; the coverage is thin, same note as
> **R28**.

`ctx.ProgressSink` is set only by `InstallEngine`, so the disarm and
staging-refusal notices raised during a `pre_install` hook or an uninstall hook are
reported to nothing. A security refusal that is not logged is, from the operator's
side, indistinguishable from a silent success — the exact failure mode R1 and R19
were fixed to remove, surviving in the one phase nobody checked.

### R57 — A test deletes an HKLM key on an elevated runner
**Component:** tests · **Effort: S** · **NOTE**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane S5,
> [#29](https://github.com/Sigil-build/sigil/pull/29) `50e5de4`. **Evidence M:**
> `grep -rn "DeleteSubKeyTree" tests/` returns no source hits (binary matches under
> `bin/` only, from stale build output).

`tests/SigilBuild.Wrapper.Tests/…/UninstallEngineTests.cs:76` calls
`DeleteSubKeyTree` against `HKLM\…\Uninstall\sigil.test.<guid>` in a swallowing
`finally`. Pre-existing (present at the merge base), and harmless today because no
test creates that key — but **CI runs elevated**, so it is the one line in the
suite that would do something real to the host given an app-id collision.
One-line removal whenever that file is next touched.

---

# Verified sound

Checked and cleared. Do not re-audit these without new information.

**Update engine (P12) — signature handling is correct.**
`Update/UpdateRunner.cs:102` captures `manifestBytes`; `:105` parses
`Encoding.UTF8.GetString(manifestBytes)`; `:116` verifies that **same array**.
Every consumed field (`schemaVersion`, `version`, `packageUrl`, `sha256`,
`minFromVersion` — the complete record at `Update/ChannelManifest.cs:54-59`)
lies inside the signed byte range. No canonicalization, no signed subset, no
unsigned siblings. The key is pack-time pinned in the stamped blob
(`InstallSession.cs:1165` ← `SerializableWrapperBlob.cs:193` ←
`ExeWrapperPackager.cs:389`) with no env var, config, flag, or manifest override.
Fail-closed on missing key (`ChannelManifestVerifier.cs:46-49`), missing
signature (`:51-54`), non-base64 input (`:62-76`), non-P-256 curve (`:84-87`),
and any crypto exception (`:94-101`). Verification precedes every consequential
action. This was the single most important thing to get right in the P-track,
and it is right.

**SHA-256 is mandatory and unskippable on every path that executes a downloaded
artifact.** Pack time: `ManifestParser.cs:1559-1568` (SIG0236) and `:413-417`
(SIG0280) both refuse. Run time: `HttpDownloadStep.cs:42-46`,
`PrerequisiteRunner.cs:231-235`. Update: `ChannelManifestParser.cs:87-90` plus a
64-hex-char shape check before spending a download
(`UpdateRunner.cs:169-173`). There is **no** code path reaching a process launch
with a downloaded file whose hash was not compared. Comparison itself is a
streaming `IncrementalHash` over the exact bytes written
(`SigilDownloader.cs:122,136`), and a mismatch is explicitly non-retryable
(`:72-75`).

**HTTPS enforced with defence in depth on the hash-gated paths.**
`HttpDownloadStep.cs:37-40` re-checks the token-substituted URL at run time even
though SIG0235 already rejected a literal `http://` at pack time;
`PrerequisiteRunner.cs:227-230` mirrors it. (The gaps are R8 and R14, elsewhere.)

**No TLS weakening anywhere.** Grep across `src/` for
`ServerCertificateCustomValidationCallback`, `SslProtocols`, and
`HttpClientHandler` → zero hits. The test seam
(`Engine/SigilHttpClient.cs:50-55`) swaps the whole client and is `internal`.
Timeouts exist on every request (`SigilDownloader.cs:107-109`,
`UpdateSeams.cs:67-68`, `HttpOptionsLoader.cs:45-46`) with a 30 s
`ConnectTimeout`. Retry classification is conservative (`:156-171`).

**No shell, anywhere in the install path.** All external tools launch with an
explicit filename and per-argument `ProcessStartInfo.ArgumentList`:
`schtasks.exe` (`ScheduledTaskCreateStep.cs:150-157`), `netsh.exe`
(`FirewallRuleStep.cs:148-155`), `sc.exe` (`ServiceInstallStep.cs:141-148`),
`run_program` (`RunProgramStep.cs:42-57`), and the same in every rollback record.
The only `UseShellExecute = true` in the repo opens the installer's own log
(`InstallerViewModel.cs:908-911`). **No scheduled-task XML is constructed at
all** — the `schtasks /Create` flag path means the XML-injection class (forged
`Principal`/`RunLevel`) does not exist here, and `/RU SYSTEM` and `/RL` are
separate argv tokens unreachable from manifest values. (R31 is the one
concatenated fragment, and it is confined to the task's own action.)

**Enum-valued privileged fields are closed sets, validated twice** — pack time
(`ManifestParser.cs:1277-1289,1355-1373`) and again at runtime with safe
defaults (`ScheduledTaskCreateStep.cs:132-144`,
`ServiceInstallStep.cs:119-135`). `service_account` can never become an
arbitrary account+password.

**All three P11 steps are pack-time pinned to machine scope** via
`MachineScopeGuard.cs:52-67` + SIG0310, and `auto` correctly fails the guard.
Each journals its inverse **before** the mutation
(`ScheduledTaskCreateStep.cs:83`, `ComRegisterStep.cs:60`,
`FirewallRuleStep.cs:80`); `firewall_rule` additionally pre-deletes by name for
reinstall idempotency (`:86`). (R15 is about undo *failure* handling, not
ordering.)

**Wizard localization is inert — no injection surface.** This was an explicit
audit question and the answer is clean. `string.Format`/`AppendFormat`/
`CompositeFormat` appear **nowhere** in `src/` (the only textual hit is a comment
in `Localization.Generator/StringsEmitter.cs:111`), so there is no
manifest-controlled format-string surface at all. The chrome catalog compiles
from repo-owned `Strings.*.txt` into pure concatenation (`:113-148`) with
build-time positional placeholders. Manifest text is only ever an *argument*:
`LocalizedText` values resolve at `WizardField.cs:581-588` into plain strings
bound to `TextBlock.Text` / `CheckBox.Content` / `Window.Title`. In Avalonia a
string bound to `TextBlock.Text` is inert — no markup parsing, no HTML. No
runtime XAML is built from manifest content and no manifest string reaches a
shell. Length is unbounded, but the worst case is an oversized TextBlock
authored by the publisher who signed the installer.

**Traversal containment is individually correct everywhere it exists.**
`StepContext.cs:518-537` (payload sources), `PayloadExtraction.cs:103-121`
(zip-slip), `NativeRuntimeBootstrap.cs:185-203` — all normalize with
`GetFullPath` *before* comparing, terminate the root prefix with a separator (so
`C:\rootevil` cannot pass as `C:\root`), and compare case-insensitively. The
defect is that they are three copies rather than one helper, and that
destinations get none of it (R16).

**Redaction is applied on every path that could carry a resolved secret** —
`InstallEngine.cs:91,151,159`, `HookRunner.cs:102-111`,
`UninstallStateStore.cs:91-97` (before bytes touch disk), and
`InstallEngine.Describe`/`DescribeUndo` render *unresolved* declared fields only
(`:174-193`). Journal records for the P11 steps carry names and paths, never
values. (R18 is the command-line channel, which redaction cannot reach.)

**Restart Manager handle hygiene is correct.** `RmEndSession` is in a `finally`
on every path (`FilesInUse.cs:147-150`, `:199-202`), the early returns correctly
skip it because no session was opened, and `RmRestart` is never called.
Registration failure fails **open** deliberately and is documented at `:40-46`
("a false 'clear' degrades to the pre-P6 behaviour… a false 'blocked' would
wedge a perfectly good install") — a defensible call for an installer.

**Config-edit rollback stash hygiene is correct.** `ConfigFileEditor.cs:40-43`
snapshots before the write and journals it; `File.Copy(..., overwrite: false)`
at `:41` makes stash pre-creation a hard failure and the GUID name is
unguessable; stashes are reclaimed on the success path
(`RollbackJournal.cs:56-60`).

**XML and JSON output encoding is safe.** `XmlEditStep.cs:63,67` uses
`SetAttribute`/`InnerText`; `JsonEditStep.cs:59-61` uses the `JsonNode` DOM.
Values are escaped by the serializer.

**Ordering guarantees on the install path.** Files-in-use gate → prerequisites →
prior-version teardown all run *before* the journal opens
(`InstallSession.cs:781-823`), so a refused run mutates nothing. Elevation
precedes all scope-requiring work (`Installer.Host/Program.cs:61-77`).

**AOT-safe deserialization throughout.** `Json/WrapperBlobJsonContext.cs:46-50`
registers the journal types; the hand-rolled discriminator
(`SerializableRollbackRecord.cs:225`) avoids reflective polymorphism as
`AGENTS.md` requires; unknown discriminators and missing fields are rejected,
not silently defaulted (the plumbing gap is R19).

**Security-critical unit tests are genuinely good** — this is worth saying
plainly given R6. `ChannelManifestVerifierTests.cs:53-175` covers tampered
bytes, wrong key, wrong curve (P-384), malformed base64 on both inputs,
null/empty/whitespace key, and DER-vs-IEEE-P1363 encoding confusion.
`PayloadExtractionTests.cs:65-76` is a real zip-slip test that also asserts no
temp directory is left behind. `HttpDownloadIntegrationTests.cs:67-102` uses a
real TLS server and covers checksum mismatch → rollback, timeout → retry, and
retries exhausted. `ComRegisterStepTests.cs:31-106` asserts the journal records
the inverse *before* the native call. Negative security tests exist and are
meaningful. The thin spots are `ElevationTests.cs:52-59` (asserts only
non-throw), `InstallEngineRollbackTests.cs` (3 tests), and
`UninstallEngineTests.cs` (3 tests: happy roundtrip `:14`, missing state `:85`,
serialization `:95`) — notably, nothing feeds `UninstallEngine` a hostile
journal, which is plausibly *why* R1's control was never built.

**The one real skip is legitimate.**
`tests/SigilBuild.Wrapper.IntegrationTests/ComRegisterInstallTests.cs:107-115`,
`Live_register_then_unregister_a_real_self_registering_dll` — needs a
purpose-built self-registering DLL that does not exist in the repo; using a
system DLL was deliberately rejected because its CLSID is pre-registered (so
"assert present" proves nothing) and unregistering system COM on a shared runner
is a fragile-fixture risk. It is the only `Skip=` in the entire repo.

**Elevation command-line quoting and exit-code propagation** (`Engine/Elevation.cs`
`BuildCommandLine`, `RelaunchElevatedAndWait`) and **`WrapperBlob` resource
parsing failing safe on tampered input** — re-checked, unchanged from the prior
review's finding, still sound.

**Repo size and artifact hygiene.** `publish/` and `TestResults/` are untracked;
the packed repo is 1.99 MiB. No build output or coverage report is committed.

---

# Filed at gate G2 (2026-09-09)

Rows **R58–R63** were found while running the G2 manual checks (Section 3 of
`10-G2_G3_RUNBOOK.md`) against the merged RC at `3ba97f6`, and while preparing
this gate-close pass. Full command-level evidence for the four defects behind
R58–R61: `.superpowers/sdd/2026-09-08-g2-release-prep/g2-checks-report.md`
(gitignored, not part of this PR). **R64 and R65** were found afterward, while
landing R58's fix on `rc/p6-fix-uninstall-self-block`
([PR #39](https://github.com/Sigil-build/sigil/pull/39), commit `48e864f`).
**R66–R68** were found later still, in the **first real run** of
`wrapper-vm-tests.yml` (run 34361541578) — the run R64 had just established
would be worth less than it looked. It was worth even less than that: R66 (the
fixtures had rotted), R67 (the P11 legs assert a pre-S2 message) and R68 (the
P12 job's build step could never run) between them account for every failure in
that run, and none of the three is about installer behaviour.

### R58 — The ARP `UninstallString` cannot complete: the uninstaller blocks on its own pid
**Component:** Wrapper.Core / Engine · **Effort: S** · **RELEASE BLOCKER**

> **STATUS (V1.1, 2026-09-09):** FIXED AND MERGED; **the end-to-end proof is still
> pending** — [#39](https://github.com/Sigil-build/sigil/pull/39) merged as
> `102ea3f` (the row body below still says "open, not yet merged"; it merged on
> 2026-09-09). **Evidence T:** `FilesInUseTests` (21) `Passed` on run
> `34362414470`, including 8+ R58-annotated cases —
> `Scan_never_reports_the_running_process_as_its_own_blocker`,
> `A_process_running_the_installers_own_image_is_excluded_but_no_other_is`,
> `The_same_image_named_a_different_way_is_still_recognised`,
> `A_different_file_with_the_same_name_is_not_the_installers_image` — the last two
> pinning the file-identity comparison the review required instead of a string
> compare. **Narrower than recorded — the release blocker's own e2e test has never
> executed:**
> `ArpUninstallStringTests.Registered_uninstall_string_completes_from_inside_the_install_dir`
> is VM-only, reported `NotExecuted` on run `34362414470`, and is skip #1 of the 21
> local skips. The first automatic VM run
> ([34368896457](https://github.com/Sigil-build/sigil/actions/runs/34368896457),
> `b07021e`) is the first that could run it. **Until a VM run executes that test,
> this row is fixed in code and unproven in the field.**

`uninstall.exe` ships **inside** `install_dir` (T15). Add/Remove Programs — and
a user running the registered `UninstallString` directly — launches exactly
that binary. `InstallSession.CheckFilesInUse` runs `FilesInUse.Scan` on the
uninstall path, and `src/SigilBuild.Wrapper.Core/Engine/FilesInUse.cs` has
**no self-exclusion** — no `Environment.ProcessId`, no `GetCurrentProcessId`,
nothing. The Restart Manager reports the running uninstaller's own process as
a blocker and the run refuses with exit `4` (`FilesInUseExitCode`) before
anything is touched. `/closeapps`, the remedy the message itself names, cannot
help: the Restart Manager cannot close the caller.

**Evidence (own pid known independently, not inferred).** Launched via
`Start-Process -PassThru` so the pid is known before the process reports
anything itself:

```
uninstall.exe OWN PID = 7484   exit = 4
[11:49:43Z] blocked by: installer (pid 7484)
[11:49:43Z] result: blocked - these applications are using files this install
  needs and must be closed first: installer (pid 7484) - close them and
  retry, or re-run with /closeapps
```

Re-run with the suggested remedy — still refuses, against a fresh pid:

```
uninstall.exe /S /Uninstall /currentuser /closeapps   -> own pid = 5244, exit = 4
[11:51:21Z] close-apps: closing 1 blocking application(s)
[11:51:21Z] blocked by: installer (pid 5244)
```

Differential control — the same uninstall driven from the original
`Setup.exe`, which lives **outside** `install_dir`, succeeds cleanly (exit
`0`, full rollback of the payload, registry value, and uninstaller). That
isolates the defect to "the uninstaller is inside the directory it scans,"
not to the uninstall logic itself.

**Pre-existing on `main`, not new to Stage 2/3.** P6 landed `FilesInUse` with
no self-exclusion; T15 is what puts `uninstall.exe` inside `install_dir` for
every install, so the two combine into a shipped uninstall path that is dead
for an end user who no longer has the original `Setup.exe` — which is the
common case. `installer.scope` is not the variable: `uninstall.exe` lives in
`install_dir` for machine scope too (not tested here — would need elevation,
out of scope for this check), so per-machine installs are expected to fail
identically. The interactive (non-`/S`) `uninstall.exe` path goes through the
same gate and was not separately exercised, but its Close-applications screen
would be asked to close the process driving it.

**Fix lane:** `rc/p6-fix-uninstall-self-block` — **[PR #39](https://github.com/Sigil-build/sigil/pull/39)**
(commit `48e864f`), **open, not yet merged**. The fix excludes not only the
running process itself but **any process executing the same image path**
(compared via `Environment.ProcessPath`) from `FilesInUse.Scan` when the
blocking image is the uninstaller's own. Excluding only the current pid is
not enough: the Restart Manager reports a blocker **per loaded image**, not
per process, so a **machine-scope `/allusers` uninstall** — where the
un-elevated parent (the same `uninstall.exe` image) stays alive waiting in
`WaitForSingleObject` on its elevated child — would still see its own parent
reported as a blocking instance of that image and refuse identically, even
with the launching pid itself excluded. A test that installs, then runs the
**registered `UninstallString` verbatim** (not the engine in-process, not
`Setup.exe /Uninstall`) would have caught this — the existing uninstall tests
apparently drive the engine or `Setup.exe`, never the deployed
`uninstall.exe` in place. **Blocks G3**: the VM matrix must not be treated as
proof of a working uninstall path until PR #39 merges. PR #39's own work
surfaced two more rows, filed below: **R64** (the VM matrix advertises
scenario coverage several toggles never exercise, including the one —
`SIGIL_VM_CLOSEAPPS` — that is this row's own P6 leg) and **R65** (the
committed lock files cover only the Debug restore graph).

### R59 — `from: payload/**` is the wrong idiom; only `payload://` rebases onto the extracted payload
**Component:** docs + examples · **Effort: S** · **SHOULD-FIX**

> **STATUS (V1.1, 2026-09-09):** CLOSED — lane DOC-G2,
> [#37](https://github.com/Sigil-build/sigil/pull/37) `da792fb`, with the five
> occurrences that sweep missed fixed by
> [#41](https://github.com/Sigil-build/sigil/pull/41) `b07021e`. **Evidence M:**
> `grep -rn "payload/" docs/guides/*.md examples/ | grep -v "payload://"` → zero
> hits; both shipped examples use `payload://`. **Scope, stated rather than
> implied:** #41's always-on `VmFixtureManifestTests` enforces the scheme **in the
> VM fixtures only**. Docs and examples were swept **by hand**, nothing gates a
> Markdown code fence, and `InstallStepsSchemaTests` keeps a bare `payload/**` on
> purpose — so a future doc can reintroduce this. A docs-wide grep gate would close
> it and is not implemented.

> **STATUS — FIXED in this PR.**

`StepContext.ResolvePath` (`src/SigilBuild.Wrapper.Core/Engine/StepContext.cs:734-797`)
rebases a `from:`/`to:` path onto the extracted payload root **only** when it
begins with the literal `payload://` scheme; anything else — including the
plausible-looking `payload/**` — passes through unchanged and is resolved
against the process's current working directory at install time. Static
validation cannot catch it: `payload/**` is a schema-legal string, so CI's
example-manifest gate stays green while the install fails:

```
error: step 'copy-app' failed: install_steps: glob root 'payload' does not exist
rollback: reverting changes
exit code: 1
```

(Rollback was clean — nothing left behind.) The wrong spelling appeared in
`docs/guides/install-steps.md` (the page's own worked example, `from:
payload/**`) and in both shipped exe-wrapper examples,
`examples/exe-wrapper/hello-wix-killer/sigil.yaml` and
`examples/exe-wrapper/multi-edition/sigil.yaml` — i.e. a publisher who copied
either shipped example verbatim would ship a broken installer. The identical
bug, found in the same pass, also appeared in `docs/guides/conditional-installs.md`
and `docs/guides/parameters.md`; this PR fixes all five files by changing
`from: payload/…` to `from: payload://…` (matching the working spelling used
by the G2 check's own test manifest) and correcting `install-steps.md`'s
prose about how `from:` is resolved. Every changed example manifest remains
schema-valid (CI's example validation still passes).

**That sweep missed five more occurrences**, all fixed with **R66** by
[PR #41](https://github.com/Sigil-build/sigil/pull/41) (lane
`rc/vm-fix-fixtures`):

- `tests/SigilBuild.Packaging.IntegrationTests/Fixtures/localized-uk/sigil.yaml`
- `tests/SigilBuild.Packaging.IntegrationTests/Fixtures/localized-uk-fixed/sigil.yaml`
- `tests/SigilBuild.Packaging.IntegrationTests/Fixtures/localized-de/sigil.yaml`
- `docs/getting-started.md` (the tutorial's copy-pasteable manifest)
- `docs/migration/from-inno.md` (the `[Files]` → `file_copy` table, whose prose
  also claimed "`from` is relative to the packed `payload/` directory" —
  contradicting the corrected `docs/guides/install-steps.md`)

The first sweep looked at the guides and the shipped examples; the first two
files above are *fixtures* packed and run by the VM localization legs (which
duly failed with exit 1 in run 34361541578), and the last two are docs the
sweep did not reach. `tests/SigilBuild.Core.Tests/Manifest/InstallStepsSchemaTests.cs`
keeps a bare `payload/**` **on purpose** — it asserts the schema accepts any
string for `from:`, which is the whole reason this row cannot be caught by
validation — and now carries a comment saying so.

**How much of this is now guarded, precisely.** R66's always-on
`VmFixtureManifestTests` refuses a `file_copy` source that omits the scheme in
**the VM fixtures only** (the manifests those legs pack). Docs and examples were
swept **by hand**: nothing enforces the spelling in a Markdown code fence, so a
future doc can reintroduce it. A docs-wide grep gate would close that, and is
not implemented here.

### R60 — The schema validator's `additionalProperties`-as-subschema form is never applied
**Component:** Core / Configuration · **Effort: M** · **SHOULD-FIX**

> **STATUS (V1.1, 2026-09-09):** **OPEN, correctly** — filed at G2 by
> [#37](https://github.com/Sigil-build/sigil/pull/37) `da792fb`; no lane owns it
> yet. **Evidence D:** re-verified during the walk —
> `src/SigilBuild.Core/Configuration/SchemaValidator.cs:125-126` is still the
> boolean-only `additionalProperties` gate. The fix shape and the fixture that would
> have caught it are written in the row; nothing was claimed. Note the interaction
> with **R71**: this fix lands in `SigilBuild.Core`, the assembly with 0.02 pp of
> coverage headroom.

`SchemaValidator.ValidateObject` (`src/SigilBuild.Core/Configuration/SchemaValidator.cs:125-126`)
treats `additionalProperties` purely as a boolean gate:

```csharp
var additionalAllowed = !schema.TryGetProperty("additionalProperties", out var addProp)
    || addProp.ValueKind != JsonValueKind.False;
```

When the schema value is a **subschema object** — which is how the root
`parameters` map is declared (`schemas/sigil-schema.json:613`) — `ValueKind`
is `Object`, so `additionalAllowed` is `true` and the subschema is **never
applied to any map value**. The entire `parameters.<name>` subtree is
consequently dead schema: a manifest with a bogus field under
`parameters.edition.source` (which declares `"additionalProperties": false`)
validates clean —

```
> sigil validate c4-badfield.yaml     # bogus_field: 1 added under parameters.edition.source
OK: c4-badfield.yaml
EXIT=0
```

— and `parameters.*.source.url`'s own `"pattern": "^https://"` never fires
`SIG0010`. Contrast `updates.manifestUrl`, declared under `properties`, where
`SIG0010` *and* `SIG0324` both fire (see the G2 check-5 result above). G2
checks 4 and 5 both still **pass** because the typed parser (`SIG0323`,
`SIG0324`) enforces what matters independently of the schema layer — this row
is about the schema silently providing less coverage than it appears to, not
about a live bypass. Any future constraint added under `parameters` in
`schemas/sigil-schema.json` will be silently inert until this is fixed.

**Fix shape (not implemented here):** implement the subschema form of
`additionalProperties` in `SchemaValidator.ValidateObject` — for each object
property not matched by `properties`/`patternProperties`, validate it against
the subschema instead of only checking presence — plus a fixture in
`tests/SigilBuild.Schema.Tests` shaped like `c4-badfield.yaml` above, which
would have caught this. Touches `schemas/sigil-schema.json`'s lockstep
surfaces per `AGENTS.md` if the fix changes what schema authors can rely on.

### R61 — `docs/guides/uninstaller.md` has drifted from the real ARP entry and uninstaller shape
**Component:** docs + Wrapper.Core · **Effort: S (doc half) / M (code half)** · **SHOULD-FIX**

> **STATUS (V1.1, 2026-09-09):** **OPEN, correctly** — filed at G2 by
> [#37](https://github.com/Sigil-build/sigil/pull/37) `da792fb`. **Evidence D:** all
> four `uninstaller.md` drifts plus the fifth item (the `registry_write` key
> residue) are written out with their measured values, and the row says explicitly
> that none of the four is fixed. Items 1 and 4 are closable by a docs pass alone;
> items 2, 3 and the key residue need a code decision.

Four drifts observed on a real installed app's ARP entry and disk footprint,
none individually severe but compounding into a doc a publisher cannot trust:

1. **`EstimatedSize = 0`.** The guide's table (line 20) says "Total install
   footprint in KB"; the real value written is `0` regardless of actual
   footprint (measured ~33.4 MB for the G2 check's own install).
2. **Undocumented `/currentuser` suffix.** The real `UninstallString` is
   `"<install_dir>\uninstall.exe" /S /Uninstall /currentuser`; the guide's
   table shows only `/S /Uninstall`.
3. **No `QuietUninstallString` is ever written**, though the guide (line 134)
   explains ARP's silent-uninstall handling in terms of
   `QuietUninstallString` semantics — the value that would make it real is
   simply absent from the registry.
4. **Uninstaller size.** The guide says the dropped `uninstall.exe` is "~4 MB";
   it is actually a **full copy of `Setup.exe`** (payload and embedded runtime
   included) — 34,145,792 bytes in the G2 check's build. Every install leaves
   a ~33 MB uninstaller behind, not ~4 MB.

A fifth, related but not a doc issue: `registry_write`'s rollback record is
`restore_registry_value`, not a key-level record, so an uninstall deletes only
the *value* it wrote and leaves the *key* it created behind — the G2 check's
own install left an empty `HKCU\Software\<App>` key after a clean uninstall.

**Fix:** items 1 (doc-only, correct the table's claim to match reality or fix
`EstimatedSize`'s computation — code) and 4 (doc-only, correct "~4 MB" to
reflect the full-copy design) can be closed by a docs pass alone. Items 2 and 3
need a code decision (write `QuietUninstallString`, and decide whether
`/currentuser` belongs in the documented contract or should be suppressed) and
the registry key-cleanup gap needs a rollback-journal record shape change.
None of the four is fixed in this PR.

### R62 — SDK bumps must regenerate lock files in the same commit
**Component:** CI / dependency management · **Effort: S** · **SHOULD-FIX**

> **STATUS (V1.1, 2026-09-09):** **OPEN, correctly** — filed at G2 by
> [#37](https://github.com/Sigil-build/sigil/pull/37) `da792fb`. **Evidence D:**
> re-verified during the walk — `.github/dependabot.yml` declares only `nuget`
> (`:4`) and `github-actions` (`:11`); there is no `dotnet-sdk` ecosystem entry, so
> the next deliberate SDK bump hits the same `NU1004` wall REL (#32) hit for real.

Hit for real during the merge chain (2026-09-09, not theoretical): `global.json`
previously pinned `10.0.100` with `rollForward: latestFeature`; the CI runner
picked up the freshly released SDK `10.0.401` overnight, whose SDK-injected
`Microsoft.DotNet.ILCompiler` / `Microsoft.NET.ILLink.Tasks` moved to `10.0.12`
and no longer matched any of the 21 `packages.lock.json` files, which were
generated against `10.0.303`'s `10.0.11`. Every locked restore failed with
`NU1004`. REL (#32) fixed the immediate break by pinning `global.json` to
`10.0.303` with `rollForward: disable` — correct, but it also means nothing
now bumps the SDK, so the next deliberate bump will hit the identical failure
mode unless the lock-file regeneration is part of the same change.

**Fix:** add a `dotnet-sdk` ecosystem entry to `.github/dependabot.yml`
(SUP's Dependabot config today covers `nuget` and `github-actions` only), and
make its PRs run `dotnet restore Sigil.slnx --force-evaluate` and commit the
regenerated lock files before the PR can go green — mirroring the manual step
SUP's own rebase had to perform for the SkiaSharp preview→stable bump (Trap 2
in `10-G2_G3_RUNBOOK.md`).

### R63 — The unit suite has no seam keeping the install-state root off the real `%ProgramData%`
**Component:** Wrapper.Core / Engine + tests · **Effort: M** · **SHOULD-FIX**

> **STATUS (V1.1, 2026-09-09):** **OPEN, correctly** — filed at G2 out of hotfix
> [#36](https://github.com/Sigil-build/sigil/pull/36)'s work. **Evidence D:**
> re-verified during the walk — no `…ForTesting` seam exists in `ScopeLayout.cs` or
> `UninstallStateStore.cs`, so the next test touching machine-scope state can
> reintroduce the order-dependent failure #36 patched at one fixture.

Found by hotfix #36 (2026-09-09): `CreateHardened` creates missing **ancestor**
directories with the same admin-only DACL it applies to its target, so the
first machine-scope `CreateHardened` call in a test run hardens the shared
`%ProgramData%\Sigil` root on the runner itself — not just the directory the
test intended to create. Any later fixture that plants state under
`%ProgramData%\Sigil` then inherits admin-only permissions it never asked
for, and whether a given test run trips this depends on **assembly execution
order**, which is why the same suite passed and failed on the same runner
image across consecutive runs. #36's fix was a single fixture made
order-independent (grants `BUILTIN\Users` write on the planted directory,
mirroring the file-level ACE already granted) — a targeted patch, not a
structural one. Per-component test seams already exist elsewhere
(`SecureStaging.NeverStageElevatedForTesting`,
`UpdateSequenceStore.UseDirectoryForTesting`, `InMemorySequenceStore`), but
nothing equivalent exists at the `ScopeLayout` / `UninstallStateStore` level,
so the next test that touches machine-scope state can reintroduce the same
class of order-dependent failure — and, on an unelevated dev box whose real
`%ProgramData%\Sigil` is already hardened from a prior elevated run, the
affected fixture throws rather than reporting a clean skip.

**Fix (not implemented here):** a test-only override of the machine
install-state root at the `ScopeLayout` / `UninstallStateStore` level, so no
unit test ever creates or hardens a path under the real `%ProgramData%`.

### R64 — `wrapper-vm-tests.yml` advertises coverage no test reads
**Component:** tests / CI · **Effort: M** · **RELEASE BLOCKER class**

> **STATUS (V1.1, 2026-09-09):** **OPEN, narrowed.** The env-var half was closed by
> [#41](https://github.com/Sigil-build/sigil/pull/41) `b07021e` — all five
> still-orphaned toggles **removed, none faked** — so what remains is the
> **coverage** itself. **Evidence M:** independently re-verified during the walk at
> `102ea3f`, before #41 landed: 10 `SIGIL_VM_*` toggles declared across
> `.github/workflows/`, 5 read by a test (`PREREQ`, `SYSTEMSTEPS`, `TESTS`,
> `UNINSTALL_SURVIVE`, `UPGRADE`), 5 orphaned (`ARP_VALUES`, `CLOSEAPPS`,
> `DOUBLE_INSTALL`, `SCOPE`, `SCOPE_MATRIX`); `SIGIL_VM_UNINSTALL_SURVIVE: "1"`
> confirmed at `wrapper-vm-tests.yml:52`. **Confirmed after #41: machine-scope
> end-to-end install is still covered by no leg.** Collapsing the vacuous
> `currentuser × allusers` matrix removed a duplicate, not a gap — both legs had
> been running the identical per-user suite — so the per-machine half of every leg,
> double-install idempotency, real `manifest.App.*` ARP value assertions, and the
> P6 `/closeapps` + setup-mutex legs all remain uncovered. #40's fix report adds
> two more to that list: `service_install` has **no** VM leg at all, and the live
> COM `HKCR` leg is still a `Skip=`. The honest one-line summary of the whole
> matrix: **the pack → `Setup.exe` → install → uninstall path has still never been
> executed by CI**, and 16 of the 21 local skips are this one toggle on this one
> assembly.
>
> **What the first automatic run on `b07021e` then showed (2026-09-09), diagnosed in
> `.superpowers/sdd/2026-09-08-g2-release-prep/vm-install-matrix-diagnosis.md`:** the
> `vm (install matrix)` leg failed six tests, and **five of the six were fixture
> bugs #41 missed** — `file_copy.to` given a *file* path where the step contract
> wants a destination **directory** (three fixture builders), and one app id reused
> across two install roots in the localization legs, so the second install's
> reinstall-cleanup emptied the first's directory. Both are the R59/R66 shape again:
> schema-legal, silently wrong, latent for exactly as long as the leg never ran.
> They are fixed on `rc/vm-fix-fixtures-round2` (PR number pending), which also
> extends #41's always-on `VmFixtureManifestTests` guard to refuse a `file_copy`
> `to:` that is not a directory template — the guard gap belongs to this row's
> family: *the matrix advertises coverage a fixture silently voids.* **The sixth
> failure is not a fixture bug: it is `R74`.** Because the install-matrix leg runs
> **elevated** on the hosted runner, the two elevation-sensitive upgrade assertions
> become **honest skips** in that same PR — `!Elevation.IsProcessElevated()` with a
> reason naming **R2**, a real skip per **R6**, not a vacuous pass — and stay
> skipped until R74 lands. Note what that costs: per-user upgrade and downgrade
> behaviour remains unexercised end to end, which is more coverage this row still
> owes, on top of the machine-scope gap above.

Found while landing R58's fix (PR #39); the count below is the #39 reviewer's,
verified precisely, not an estimate. Across `wrapper-vm-tests.yml` and its
companion workflows, **ten** `SIGIL_VM_*` scenario toggles are declared, and
at the RC base (`3ba97f6`, before #39) only **four** were read by any test —
leaving **six** consumed by **no test at all**: `SIGIL_VM_SCOPE`,
`SIGIL_VM_SCOPE_MATRIX`, `SIGIL_VM_ARP_VALUES`, `SIGIL_VM_CLOSEAPPS`,
`SIGIL_VM_UNINSTALL_SURVIVE`, and `SIGIL_VM_DOUBLE_INSTALL`. Toggling any of
them on or off changed nothing about what actually ran; the workflow
advertised coverage that did not exist.

**The good news, also verified by the #39 reviewer: PR #39 closes one of the
six for real, not just on paper.** `wrapper-vm-tests.yml:52` sets
`SIGIL_VM_UNINSTALL_SURVIVE: "1"` on **both** scope-matrix legs, and #39's new
`ArpUninstallStringTests` sits behind the suite's existing "refuse to pass
vacuously" precondition (the same shape T1's **R6** fix established) — so the
test does not merely exist, it genuinely executes at G3 rather than silently
no-op'ing for want of the env var. That leaves **five** still orphaned:
`SIGIL_VM_SCOPE`, `SIGIL_VM_SCOPE_MATRIX`, `SIGIL_VM_ARP_VALUES`,
`SIGIL_VM_CLOSEAPPS`, `SIGIL_VM_DOUBLE_INSTALL`.

**Why this is the release-blocker class, not a housekeeping note:**
`SIGIL_VM_CLOSEAPPS` is the P6 files-in-use gate's own leg — the exact
surface **R58** lives on — and it is a live, no-test toggle. That is
mechanically *why* R58 reached the merged RC undetected: the one workflow
whose stated job is to exercise this scenario for real never did, so nothing
short of running the G2 manual checks against a real `Setup.exe` by hand was
ever going to catch it. Per T1's "no vacuous skips" rule (`00-GAP_REGISTER.md`
**R6**, `AGENTS.md`), a scenario toggle nothing reads is the same failure
shape as a test that reports `Passed` without asserting — it makes the G3
"VM matrix green" gate checkbox vacuous for every leg it silently covers.

**Also found in the same pass:** PR #39 corrected a comment in
`WixClassInstallUninstallTests` that falsely claimed `Setup.exe /Uninstall`
is the ARP `UninstallString` code path — it is not (that is exactly R58's
subject: the ARP entry points at the deployed `uninstall.exe`, not back at
`Setup.exe`). Fixed in-lane by #39, no separate row needed for the comment
itself; recorded here because it is the same confusion R58 exists to correct.

**Fix shape:** for each of the five still-orphaned toggles, either wire it to
a real test (as `ArpUninstallStringTests` genuinely did for
`SIGIL_VM_UNINSTALL_SURVIVE`, per #39) or remove it from the workflow — no
toggle should exist that changes nothing about what runs. A G3 prerequisite
alongside R58: the VM matrix run required at G3 (`03-RC_ORCHESTRATION.md`'s
G3 checklist, `wrapper-vm-tests.yml` run for real) is only as meaningful as
the toggles it actually exercises.

**Update (lane `rc/vm-fix-fixtures`, with R66/R68): all five orphans REMOVED,
none faked.** Wiring any of them would have meant inventing the test it
advertised, which is a coverage decision, not a workflow edit — so the workflow
now declares only the three toggles a test genuinely reads
(`SIGIL_VM_UNINSTALL_SURVIVE`, `SIGIL_VM_UPGRADE`, `SIGIL_VM_PREREQ`) and its
header lists the five scenarios as **uncovered** instead of advertising them.
`SIGIL_VM_SCOPE` / `SIGIL_VM_SCOPE_MATRIX` took the two-leg
`currentuser × allusers` job matrix with them: no test read the scope, so both
legs ran the identical per-user suite twice — visible in run 34361541578, where
the two legs failed the same 11 tests for the same reasons. **This row stays
OPEN**, and its scope is now the coverage itself, not the env vars: the
per-machine half of every leg, double-install idempotency, real
`manifest.App.*` ARP value assertions, and the P6 `/closeapps` + setup-mutex
legs — the last of which is still, as this row's own text notes, the surface
R58 lived on.

### R65 — The committed lock files cover the Debug restore graph only
**Component:** build / dependency management · **Effort: M** · **SHOULD-FIX**

> **STATUS (V1.1, 2026-09-09):** **OPEN — and live-reproduced by the V1.1 walk.**
> **Evidence M:** one clean `dotnet build Sigil.slnx -c Release` at `102ea3f`, on an
> otherwise clean tree, left `src/SigilBuild.Installer.Host/packages.lock.json`
> **modified** in the working tree. That is now **two independent agents observing
> it on an unmodified tree**, so this row's severity note should read "reproduces on
> every Release build", not "may": any V1 or G3 worker following
> `10-G2_G3_RUNBOOK.md` will produce this diff and needs to know to discard it
> rather than commit it. Fix shape (b) — a second, real CI step running `dotnet
> restore Sigil.slnx -p:Configuration=Release --locked-mode` — would have caught it,
> and is what also scopes **R23a**'s "reproducible" claim honestly.

Found while landing R58's fix (PR #39), independently observed by two agents
on an unmodified tree: `EnableTrimAnalyzer` is Release-conditioned in
`Directory.Build.props`, so a **Release**-configuration restore injects
`Microsoft.NET.ILLink.Tasks` as a dependency that a Debug restore never sees —
and `dotnet build Sigil.slnx -c Release` on a clean tree rewrites tracked
`packages.lock.json` files as a side effect, exactly the same class of
drift R62's SDK-bump incident produced, but from a **configuration** axis
CI's locked restore never walks.

**Why CI stays green despite this.** `ci.yml`'s locked restore
(`dotnet restore Sigil.slnx --locked-mode`) runs in the implicit Debug
configuration, and every Release build/publish step in the same workflow
passes `--no-restore`, so the Release-configuration restore graph is never
actually validated against the committed lock files in CI — only reproduced,
silently, by whoever's local Release build touches it next. R23a's own claim
("a clean clone's locked restore succeeds, therefore the tree is
reproducible") is true for Debug and unverified for Release.

**Fix shape (a design choice for the orchestrator, not made here):** either
(a) make the trim-analyzer package reference itself config-independent for
**restore** purposes (so the dependency graph, not just static analysis,
stays Release-conditioned only where it must) so one graph serves both
configurations, or (b) generate and lock the Release graph explicitly —
`dotnet restore Sigil.slnx -p:Configuration=Release --locked-mode` as a
second, real CI step, not merely a local habit. Either closes the gap; filed
here so R23a's "reproducible" claim is scoped to what was actually checked.

### R66 — The VM fixtures had rotted under a suite nothing ever ran
**Component:** tests / CI · **Effort: M** · **SHOULD-FIX**

Rubric note: **SHOULD-FIX**, not RELEASE BLOCKER — nothing here ships a defect
to a user; what it broke is the *evidence* the G3 gate rests on. It has to be
fixed **in** the release, because the G3 "VM matrix green" checkbox cannot
honestly be ticked until it is.

> **STATUS (V1.1, 2026-09-09):** CLOSED —
> [#41](https://github.com/Sigil-build/sigil/pull/41) `b07021e`. **Evidence T:**
> `VmFixtureManifestTests` — an always-on xUnit class with no `SIGIL_VM_*` toggle,
> no staged AOT runtime and no sandbox, so it runs in every `ci.yml` run. Verified
> to bite: **11 failed / 22** against the rotted fixtures, **22 passed** against the
> fix, on #41's own CI (`3826ac6`). This row postdates run `34362414470` and does
> not cite it. **Not claimed, by the lane or by this line:** whether the VM legs now
> *pass*. That is the matrix's own verdict; the first automatic run
> ([34368896457](https://github.com/Sigil-build/sigil/actions/runs/34368896457),
> `b07021e`) was still in flight when V1.1 was written.

> **STATUS — FIXED by [PR #41](https://github.com/Sigil-build/sigil/pull/41)**
> (lane `rc/vm-fix-fixtures`), together with an always-on guard so it cannot
> recur.

`wrapper-vm-tests.yml` was `workflow_dispatch`-only and, until 2026-09-09, had
never actually been dispatched. Its first real run — **run
[34361541578](https://github.com/Sigil-build/sigil/actions/runs/34361541578)**
on `da792fb` — failed **11 of 19** tests in
`tests/SigilBuild.Wrapper.IntegrationTests`, and **not one** failure was about the
behaviour under test. Every one was the fixtures having drifted away from
surfaces that moved underneath them while the suite sat unexecuted behind
`[VmFact]`:

1. **Invalid YAML — 12 diagnostics across the two scope legs.** The fixture
   writers interpolated Windows paths and registry keys into **double**-quoted
   YAML scalars, where `\` is an escape character. `PrerequisiteInstallTests`
   built `detect: "registry_exists('HKCU', 'Software\SigilPrereqTest\<id>', 'Installed')"`
   and an `args:` entry containing `reg add HKCU\Software\…`; `\S` is not a legal
   YAML escape, so all three prerequisite legs died in `Sigil.PackAsync` with
   `manifest validation failed: While scanning a quoted scalar, found unknown
   escape character.` (SIG0001), before packing anything.

2. **Schema-invalid `app.id` — 6 diagnostics.** `UpgradeInstallTests` and
   `ArpUninstallStringTests` built their per-run unique id as
   `"com.sigil.p3." + Guid.NewGuid().ToString("N")`. `app.id`'s schema pattern
   (`^[A-Za-z][A-Za-z0-9]*(\.[A-Za-z][A-Za-z0-9]*)+$`) requires **every** dotted
   segment to be letter-led, and a 32-char hex GUID starts with a digit about
   five times in eight — so the id was a coin toss the schema usually lost:
   `app.id: string does not match pattern …` (SIG0010).

3. **Command-line grammar drift — exit 64.** `MultiEditionInstallTests` ran the
   packed Setup.exe as `/Edition=enterprise /InstallDir=<dir>` and
   `WixClassInstallUninstallTests` as `/install_dir=<dir> /registered_user=alice`.
   **None of those four tokens exists** in the wrapper's deliberately closed
   grammar (`CommandLineParser`): a declared parameter is overridden with
   `/P<name>=<value>` and the install directory with `/D=`. All three legs
   therefore exited **64** (usage error) having installed nothing — the
   snapshot-diff assertion the WiX-class test exists for was never reached.

4. **A bare `payload/` glob in the localization fixtures — exit 1.** Exactly
   **R59**, which was fixed in the docs and both shipped examples but **missed**
   in `tests/SigilBuild.Packaging.IntegrationTests/Fixtures/localized-{uk,uk-fixed,de}/`,
   which the VM localization legs pack. Only the literal `payload://` scheme is
   rebased onto the extracted payload; a bare relative glob resolves against the
   install process's working directory, so both legs failed with exit **1**.

**Why it matters beyond the wasted run.** The G3 checklist treats "the VM matrix
ran green" as the evidence that the shipped installer works end-to-end. A suite
whose fixtures cannot even be parsed produces no evidence at all, and — because
it was dispatch-only — produced no *signal* either: the rot was invisible for as
long as nobody dispatched it. This is the **R6** vacuous-skip failure shape one
level up: not a test that passes without asserting, but a whole matrix that never
runs at all.

**Fix (lane `rc/vm-fix-fixtures`).**
- One shared `Sigil.YamlQuote` emits **single**-quoted YAML scalars, in which
  there are no escapes at all (only `''` for an apostrophe), so a Windows path or
  a registry key is safe to interpolate and no call site has to remember to
  hand-double backslashes. Every interpolated scalar in the project uses it.
- Every generated `app.id` gets a letter-led final segment.
- The four rotted argv are corrected to real tokens, and each is now defined
  **once** per test class (`SilentInstallArgs(...)`) so the leg and its guard
  cannot drift apart.
- The three localization fixtures get `payload://**`, and so do the two
  remaining docs the R59 sweep missed (`docs/getting-started.md`,
  `docs/migration/from-inno.md` — the latter's prose too). See R59 for the file
  list and for exactly how much of that is guarded versus swept by hand.
- **The guard: `VmFixtureManifestTests`** — a plain, always-runs xUnit class in
  the same project, so it executes in every `ci.yml` run with no `SIGIL_VM_*`
  toggle, no staged AOT runtime, and no Windows Sandbox. The fixture builders
  were refactored into **pure functions returning the YAML**, which it validates
  through the real `ManifestLoader` (the same schema + typed-parser pipeline
  `Sigil.PackAsync` runs), parses each leg's argv through the real
  `CommandLineParser` with the parameter set the packer writes into the blob, and
  refuses a `file_copy` source that omits the `payload://` scheme — **in the VM
  fixtures**, which is the whole scope of that guard. Verified to bite: against
  the rotted fixtures it reports **11 failed / 22**, naming all four classes
  above; against the fix, **22 passed**.
- `wrapper-vm-tests.yml` now also runs on **push** to `main` / `release/**`,
  which is the half that satisfies gate G3's "on a schedule or on merge, not
  only on demand" for the release branch, plus `concurrency`
  cancel-in-progress so back-to-back RC merges do not pile up real-install
  jobs. The weekly `schedule` in the same file is **default-branch-only** —
  GitHub runs `schedule` from the default branch's copy of the workflow — so it
  gives `main` a floor once this file reaches `main` and covers `release/**`
  not at all. A quiet release branch gets its verdict from `push` and from
  manual dispatch, not from the cron.

**Not fixed here, and not claimed:** whether the legs now *pass*. This lane
proves their inputs parse and their invocations are accepted; the install
behaviour is the VM matrix's own verdict and only a real run can give it. The box
this lane was developed on cannot run the VM legs at all (no MSVC C++ workload,
so no Native AOT installer host to stage).

### R67 — The P11 VM legs target a System32 path that S2's anchoring refuses
**Component:** tests (P11 system steps) · **Effort: S** · **SHOULD-FIX**

> **STATUS (V1.1, 2026-09-09):** CLOSED —
> [#40](https://github.com/Sigil-build/sigil/pull/40) merged as `c71bd8c` (the row
> body below still says "open, not yet merged"). **Evidence T:** the P11 legs now
> run under a resolved `install_dir` built the way `InstallSession` builds it on the
> silent route (`/D=` → `CommandLineParser.Parse` → `StepContext.From` →
> `InstallDirResolver`, so R3's scope-root containment applies to the fixture too),
> with their targets copied into it; 5 new unelevated anchor tests pass, and the
> reviewer reproduced them independently. Postdates run `34362414470`. **The policy
> read matters as much as the fix:** relaxing the guard was never available —
> `allow_outside_install_dir` is `SIG0231` (unrecognized) on privileged steps by
> design — so the test was wrong, not the product. Coverage gaps that surfaced in
> the same lane (`service_install` has no VM leg; the live COM `HKCR` leg is still a
> `Skip=`) are folded into **R64**'s scope rather than filed separately.

Found in the same first real VM run (34361541578), in the `vm (p11 system steps)`
job — a **different** failure class from R66 and a different lane's fix.
`ComRegisterInstallTests` and `ScheduledTaskCreateInstallTests` point their steps
at real system binaries under `%SystemRoot%\System32`, and lane **S2**'s
containment work (register row R16 and its privileged-target siblings R3/R9) now
refuses exactly that when the run has no resolved `install_dir`:

```
Expected string "com_register: refusing the privileged 'path' target
'C:\Windows\system32\kernel32.dll' — this run has no resolved install_dir, so the
target cannot be anchored. This step runs with SYSTEM-level authority; see the
containment note in docs/guides/install-steps.md." to contain
"self-registering COM DLL".
```

The refusal is **correct** — a `com_register` of an arbitrary System32 path under
SYSTEM authority is precisely what S2 exists to stop. What is wrong is the test:
it asserts on a message from before the guard existed, and it uses a system path
as a convenient stand-in because these tests drive the step classes directly
rather than through a packed Setup.exe, so no `install_dir` is resolved. The fix
is to give these legs an anchored scratch target (a resolved `install_dir` in the
`StepContext`) and assert the current message.

**Fix lane:** `rc/vm-fix-p11-anchoring` — **[PR #40](https://github.com/Sigil-build/sigil/pull/40)**,
**open, not yet merged** (a separate agent). Deliberately **not** touched by
`rc/vm-fix-fixtures` / [PR #41](https://github.com/Sigil-build/sigil/pull/41),
which owns R66 and R68 in the same files but leaves `ComRegisterInstallTests` /
`ScheduledTaskCreateInstallTests` alone. **A green VM matrix at G3 needs both
PRs**: #41 for the install-matrix and P12 jobs, #40 for the
`vm (p11 system steps)` job.

### R68 — `wrapper-vm-tests.yml`'s P12 job could never build: MSB1008
**Component:** CI · **Effort: S** · **SHOULD-FIX**

> **STATUS (V1.1, 2026-09-09):** CLOSED —
> [#41](https://github.com/Sigil-build/sigil/pull/41) `b07021e`. **Evidence M:** the
> `build P12 test projects` step now gives each csproj its own `dotnet build`
> invocation, so the `vm (p12 update + web-installer)` job can reach its test steps
> for the first time since T12.6 added it. Postdates run `34362414470`. Whether the
> P12 legs then *pass* is the matrix's verdict, not this row's.

> **STATUS — FIXED by [PR #41](https://github.com/Sigil-build/sigil/pull/41)**
> (lane `rc/vm-fix-fixtures`).

The `build P12 test projects` step passed **two** csproj paths to a single
`dotnet build`:

```
dotnet build tests/SigilBuild.Wrapper.Tests/SigilBuild.Wrapper.Tests.csproj
  tests/SigilBuild.Packaging.Tests/SigilBuild.Packaging.Tests.csproj
  --configuration Release
```

`dotnet build` accepts at most one project or solution, so the run ended at that
step with `MSBUILD : error MSB1008: Only one project can be specified.` — before
staging the AOT runtime and before either test step. The whole
`vm (p12 update + web-installer)` job had therefore **never** executed a single
P12 test, and could not have, on any invocation of this workflow since T12.6
added it. Fixed by giving each project its own `dotnet build` invocation
(`--configuration Release` unchanged, both still ahead of the `--no-build` test
steps). Like R66, this one is a consequence of a dispatch-only workflow: a step
that cannot even start is caught by the first run that happens, and the first run
took until 2026-09-09 to happen — which is why R66's fix also puts this workflow
on a merge + weekly trigger.

---

# Filed during Stage 4 (2026-09-09)

**R69–R73** come out of task **V1.1**, the Stage-4 register walk (report:
`.superpowers/sdd/2026-09-08-g2-release-prep/v1-1-register-walk.md`, gitignored, not
part of this PR). Two of them — **R69** and **R70** — already have a fix in flight as
[PR #42](https://github.com/Sigil-build/sigil/pull/42) (`rc/v1-sbom-and-kiosk`,
**open**); **R71**, **R72** and **R73** are open with a fix shape and no owner.

**R74** and **R75** come from the same stage but a different source: the **first
automatic `wrapper-vm-tests.yml` run** on `b07021e` and its diagnosis
(`vm-install-matrix-diagnosis.md`). R74 is the one product finding among the
install-matrix leg's six failures — the other five were fixture bugs, fixed on
`rc/vm-fix-fixtures-round2`. R75 came out of the P11 round-two lane and is fixed in
[PR #44](https://github.com/Sigil-build/sigil/pull/44), **open**. Both are worth
reading next to **R64**: they are what an unrun matrix was hiding, and R75 in
particular was found only because the test there had been *asserting the wrong
behaviour as correct*.

One walk finding was deliberately **not** filed as a new row: the live reproduction of
the Release-configuration lock-file churn is **R65** happening again, not a new defect,
so it is recorded on R65's own status line instead.

### R69 — `KioskSetupFactAttribute.SetupPath` walked outside the repo, so its test could never run and its skip reason misdirected
**Component:** tests (Packaging) · **Effort: S** · **SHOULD-FIX**

> **STATUS (V1.1, 2026-09-09):** FIXED in
> [#42](https://github.com/Sigil-build/sigil/pull/42) (`a9f5e20`), **open, not yet
> merged**. **The test still skips here** — see "does it run now" below.

`tests/SigilBuild.Packaging.Tests/ExeWrapper/KioskSetupFactAttribute.cs` composed the
sample's path from `AppContext.BaseDirectory` plus **six** fixed `".."` segments.
`AppContext.BaseDirectory` is `<repo>/tests/SigilBuild.Packaging.Tests/bin/Release/net10.0/`
— five levels below the repo root — so six `".."` landed **one level above the repo**,
and the skip message printed the proof:

```
Kiosk sample test: C:\projects\tests\kiosk\dist\Embed.Infinity.Kiosk-1.0.0-x64-Setup.exe not found
                     ^^^^^^^^^^^^^^  should be C:\projects\Sigil\tests\...
```

`IconResourceWriterTests.Kiosk_Setup_HasEmbeddedUninstaller` therefore **could not
execute on any machine**, and its message told the reader to "build the `tests/kiosk`
sample first" — an action that would not have helped, because the path it checked was
outside the repository entirely. That is precisely the class **R6** exists to
eliminate — a skip whose stated precondition is not the real one — surviving *inside*
R6's own fix, which is why it is filed rather than quietly patched.

**Fix (in #42, `a9f5e20`).** The attribute now walks up to the nearest `Sigil.slnx`
(the repo-root marker) and appends the sample's known repo-relative path.
`IconResourceWriterTests` had independently recomputed the same broken six-`".."` path
inline; it now calls `KioskSetupFactAttribute.SetupPath` directly, so the guard and the
test body cannot disagree again. A new plain unit test,
`KioskSetupFactAttributeTests.SetupPath_IsInsideRepositoryRoot`, pins the invariant and
was **verified to bite** by temporarily restoring the old logic (it failed with the
`C:\projects\tests\kiosk\...` path) before being reverted to the fix.

**Does the test run now? No — it still skips, and that is correct.** `tests/kiosk/` is
a separate, out-of-band sample build that is not in the repository, and the machine
this was fixed on has no Native AOT toolchain to produce it. What changed is that the
skip reason is now honest and actionable and names a path **inside** the repo, so if
the sample is ever produced at that location the test genuinely executes. Nobody
should read this row as adding coverage; it removes a lie about coverage.

### R70 — R42's CycloneDX SBOM handoff was orphaned between two merged lanes
**Component:** CI / release · **Effort: S** · **SHOULD-FIX**

> **STATUS (V1.1, 2026-09-09):** FIXED in
> [#42](https://github.com/Sigil-build/sigil/pull/42) (`7003b20`, plus `f4472c0` for
> `-dpr`), **open, not yet merged**. First real execution is the `v0.1.0-alpha` tag
> push — like the rest of `release.yml`, it has never run (**R7**).

`docs/plan/release/sup-sbom-handoff.md` is a complete, ready-to-paste workflow step
that lane SUP wrote for lane REL, because SUP's branch was cut before `release.yml`
existed. But **REL ([#32](https://github.com/Sigil-build/sigil/pull/32)) merged before
SUP ([#33](https://github.com/Sigil-build/sigil/pull/33))**, so no lane was ever in a
position to apply it: `grep -in "sbom\|cyclonedx\|spdx" .github/workflows/release.yml`
returned **zero hits** at `102ea3f`. **R42** was counted closed and its deferral text
is honest; the SBOM deliverable simply had no owner. Neither lane did anything wrong —
the merge order ate it, which is why this is filed as its own row rather than as a
correction to R42.

**Fix (in #42).** Two steps in `release.yml`'s `publish` job, placed after the
win-x64 / win-arm64 archive steps (so `dist/` exists and signing has happened) and
before `SHA256SUMS`: install the `CycloneDX` dotnet global tool pinned to **6.2.0**,
then generate `dist/sigil-<tag>.sbom.json` with

```
dotnet CycloneDX Sigil.slnx -o dist -fn sigil-<tag>.sbom.json -F Json -dpr
```

verified by a `Test-Path` throw, with no `continue-on-error`. Three details worth
keeping:

1. **`-dpr` (`--disable-package-restore`) came out of review and is load-bearing.**
   Without it CycloneDX runs its own **unlocked** restore after the workflow's
   `restore (locked)` step, quietly undoing **R23a**'s guarantee for exactly the
   dependency graph the SBOM claims to describe. The flag was confirmed present in the
   pinned `v6.2.0` tag's own README, not assumed.
2. The handoff doc's `--json` flag **does not exist** on the real CLI; the actual flag
   is `-F` / `--output-format`.
3. `~/.dotnet/tools` is appended to `GITHUB_PATH` explicitly rather than assuming the
   runner image carries it after a global-tool install.

**Not proven:** the step has never executed. `release.yml` is tag-triggered on a
non-default branch, so its first run is the V1 release dry-run — which is itself
blocked on the six Trusted Signing secrets. The SBOM's placement (after packaging,
before `SHA256SUMS`) reconciles the handoff doc's two conflicting placement statements
and deserves a second look from whoever owns `release.yml`.

### R71 — `SigilBuild.Core` sits 0.02 pp above its coverage floor, so the next merge is a coin flip
**Component:** CI / coverage · **Effort: S** · **SHOULD-FIX**

> **STATUS (V1.1, 2026-09-09):** **OPEN.** Measured, not projected: run
> `34362414470`. No owner. The fix is a coverage investment, not a config change.

On run `34362414470`, `SigilBuild.Core` measured **69.02 %** against a floor of
**69 %** — roughly **one to two uncovered lines of slack** on the assembly every lane
touches. The floors were deliberately ratcheted to the measured value rounded down
(**R21**), which is the right policy; the problem is the margin it leaves on this
particular assembly.

**The risk, stated concretely.** The required `build` check goes red on a change that
adds a handful of uncovered lines with no security or behavioural meaning — a new
diagnostic code, a parser branch, an argument-null guard. At that moment the cheapest
apparent fix is to lower the floor by one point, which is **the one thing R21
forbids** ("Coverage floors were re-pinned upward … Nothing was lowered"). A ratchet
spent to turn a red check green stops being a ratchet.

**Fix shape.** *Raise Core's coverage before anything else touches Core.* The next lane
to work in `SigilBuild.Core` pays down coverage first, so the floor has real headroom —
and there is plenty of room to aim at, since `AGENTS.md`'s stated target for Core is
**≥ 80 %** against a measured 69.02 %. **Never lower the floor.** If the orchestrator
ever concludes the floor genuinely must move, that is an explicit written decision with
a reason, taken at a gate — not a number edited mid-lane to unblock a merge. Note the
collision with **R60**, whose fix lands in this very assembly.

### R72 — `Elevation.cs`'s relaunch/cleanup gating and the two hosts' relaunch wiring are exercised by no test
**Component:** Wrapper.Core / Engine + tests · **Effort: S** · **SHOULD-FIX**

> **STATUS (V1.1, 2026-09-09):** **OPEN.** Found by the walk's Step-3 sample 8a. Does
> **not** reopen **R18**. No owner; the fix is a VM/elevated end-to-end case, not a
> unit test.

Reverting `src/SigilBuild.Wrapper.Core/Engine/Elevation.cs` to its pre-**R18** form
(`50e5de4~1`) leaves the tree building **and all 11 `ElevationSecretHandoffTests`
passing** — `Passed: 11, Failed: 0`. Those tests exercise `ElevationSecretHandoff.cs`,
the envelope and its parser, in isolation. What ships unpinned is `Elevation.cs`'s own
half of R18: the `childMayStillBeRunning` out-parameter of
`RelaunchElevatedAndWait`, which exists precisely so the parent does **not** delete the
DPAPI envelope out from under an elevated child that is still starting, plus the two
hosts' wiring that consumes it. The failure mode is "the install the handoff was
enabling fails", intermittently, only under real elevation — the kind of bug a unit
suite structurally cannot see.

**This does not reopen R18.** That row's central mechanism — a DPAPI envelope replacing
the command line — is proven absent at the parent by the same sample's other half
(`Program.RenderWizardStartedLine` does not exist at `50e5de4~1`, so
`WizardLogRedactionTests` cannot even compile there). R18 is fixed; one of its two
files is untested.

**Same shape as R38**, which is why the two are worth reading together: a change whose
only justification is "the existing tests are the coverage", where the existing tests
demonstrably do not touch it.

**Fix shape.** A VM / elevated end-to-end case: a real elevated relaunch that consumes
a handoff, asserting the envelope survives until the child has read it and is then
cleaned up. That is a `wrapper-vm-tests.yml` leg, not a unit test, and it belongs with
the machine-scope coverage **R64** still owes — one elevated leg could carry both.

### R73 — Per-row status notes were missing on most of the register, so closure lived only in a merge table
**Component:** process / register hygiene · **Effort: M** · **SHOULD-FIX**

> **STATUS (V1.1, 2026-09-09):** **OPEN as a standing rule.** The backfill is done —
> this PR gives all 71 rows a status line — but the obligation it creates is per-PR
> from here, and nothing enforces it yet.

Until this PR, **49 of the 68 rows** the V1.1 walk covered carried no per-row status
line at all; only 19 did. **Forty of the missing ones were rows closed by Stages 2 and
3**, whose closure existed *only* as a cell in the "Stage 2/3 outcome" merge table two
hundred lines up. A reader who landed on R33, R44 or R52 directly could not tell they
were closed, by whom, or on what evidence.

**Two consequences were already real when the walk found this**, which is what makes it
a row rather than a style preference:

- **R41a** was listed under "rows closed" while actually being a *documented open*
  owner action — both NuGet IDs unreserved, a G4 checklist item. The merge table said
  closed; the row said unclaimed; nothing reconciled them until now.
- **R53**'s deferral justification existed nowhere but a code comment at
  `InstallSession.cs:1130`. A reader of R53 could not tell the behaviour had been
  decided rather than forgotten.

The register is the artifact G3 and G4 are decided from. A row whose disposition
requires cross-referencing a table elsewhere in the file is a row that can be
mis-read — and, as both examples show, mis-read in the direction of "done".

**The rule, from here.** Every closure carries its own `> **STATUS …**` line **in the
same PR that closes it**, naming (a) the PR number and merge sha, (b) the evidence
class — named test on a named CI run, manual ceremony, written deferral with its
location, or claim-only — and (c) anything narrower than the row's own headline claim.
A merge table is a summary; it is never the record. This pass backfills all 71 rows
that existed at `b07021e` (and every row filed since carries its own), but keeping it
true is a per-PR obligation, and the merge gate should check it the way it checks the
lockstep surfaces in `AGENTS.md`.

### R74 — An elevated process installing per-user plans the upgrade blind but cleans up sighted, so it can silently downgrade
**Component:** Wrapper.Core / Engine · **Effort: M** · **SHOULD-FIX**

> **STATUS (V1.1, 2026-09-09):** **OPEN.** Filed from the diagnosis of the first
> automatic `wrapper-vm-tests.yml` run's `vm (install matrix)` leg on `b07021e`
> (`.superpowers/sdd/2026-09-08-g2-release-prep/vm-install-matrix-diagnosis.md` §1.4,
> §2.4, §2.5, §5). **Reproduced**, not inferred: the elevated probe was simulated
> unelevated against the RC binaries. **Not a G3 blocker** — see the rubric note
> below. Owner: lane **S5 / S1** (engine).

Prior-install detection has **two independent sources at two different trust levels**,
and under one specific combination they disagree:

| Consumer | Source | Elevation-sensitive? |
|---|---|---|
| the upgrade plan, the downgrade block, prior-install-dir preservation | **ARP**, via `InstalledStateResolver` | **yes** — HKLM only when elevated |
| `ExistingInstallDetected` → `PerformReinstallCleanupAsync` | the **state store** under `%LocalAppData%\Sigil\<appId>`, via `UninstallStateStore.TryLoad` (`InstallSession.cs:726-740`) | no |

`InstalledStateResolver.ScopeProbeOrder` (`InstalledStateResolver.cs:89-92`) probes
**HKLM only** when the process is elevated. That is lane S1's **R2** fix and it is
correct: an elevated process must never act on HKCU-sourced data — least of all spawn
an attacker-plantable `UninstallString` as administrator. The consequence, which R2
did not have to reason about, is that **an elevated per-user install cannot see its own
prior per-user install**:

- `UpgradePlanner.Plan` short-circuits on `!state.Found` (`UpgradePlanner.cs:32`) →
  `UpgradeAction.FreshInstall`, so `DowngradeBlocked` (`:73`) is unreachable;
- the silent path therefore never reaches `return DowngradeBlockedExitCode;`
  (`InstallSession.cs:552-555`) — **exit 0 where exit 3 was the contract**;
- `priorInstallDir` is null, so the new version lands in its own manifest default
  rather than preserving the directory the user already installed into;
- **but the reinstall cleanup still fires.** `RunInstallCoreAsync` calls
  `PerformReinstallCleanupAsync` (`InstallSession.cs:994`) which returns early only on
  `!ExistingInstallDetected` (`:1111`) — and that flag comes from the state store, not
  ARP. The previous install's recorded uninstall is replayed and its files are deleted
  at the path the previous run used, while the plan believes this is a fresh install.

**Reproduction (§1.4).** Removing the HKCU ARP row to simulate the elevated HLKM-only
probe, then re-running the identical v1 setup over a v2 install, gives the runner's
exact symptom — `EXIT_V1_SIM=0` — and the `/LOG` shows both halves in one place: a
`delete …\app\uninstall.exe` from the cleanup, then a fresh `copy payload://app.txt`,
then `result: success`, with ARP rewritten back to `1.0.0`. **A silent downgrade.**

**Who hits this in the field:** anyone whose per-user installer runs from an elevated
context — an admin double-clicking it, an elevated shell, SCCM/Intune as SYSTEM, or a
CI runner. It is not exotic.

**Rubric note — SHOULD-FIX, not a RELEASE BLOCKER.** Nothing crosses a trust boundary:
no elevated run wrote outside its declared destination, and `install_dir`, containment,
ARP registration and `uninstall.exe` all behaved correctly in the same diagnosis. What
degrades is a **UX guard** (the downgrade refusal and directory preservation), and only
in a session where the user already holds the privilege. Recorded explicitly so nobody
re-triages it upward at the gate: **this does not block G3.**

**Relationship to the rows it comes from.** **R2** is why the probe is HLKM-only and
must stay that way — the fix is not "let the elevated process trust HKCU". **R53**
("an elevated process replays user-scope state at all", decided *keep*, POST-v1) is the
same split brain, but R53 is phrased as a privilege question; this row is the
**behavioural divergence** that phrasing does not capture, which is why it is filed
separately rather than appended to R53.

**Fix shape (not implemented).** Make `_plan` and `ExistingInstallDetected` agree about
what "installed" means for an elevated user-scope run — **either both blind or both
sighted**, never one of each. Two viable routes: let the plan consult the same trusted
state store the cleanup already uses, or apply R2's trust gate to the HKCU ARP row
(verify it, then use it) instead of hiding the row entirely. Ship it with a unit test
that runs under a **simulated elevated probe** — the seam
`ScopeProbeOrder(tentativeScope, elevated)` already takes `elevated` as a parameter, so
this is testable without an elevated runner, and the absence of such a test is why the
divergence survived R2's own review. Until then, the two elevation-sensitive upgrade
assertions in the VM install-matrix leg are **honest skips** naming R2 — see **R64**.

### R75 — `com_register` journaled an undo for a registration that never took effect, so one failed step could make the app permanently unremovable
**Component:** Wrapper.Core / steps + Engine · **Effort: S** · **SHOULD-FIX**

> **STATUS (V1.1, 2026-09-09):** FIXED in
> [#44](https://github.com/Sigil-build/sigil/pull/44) (lane `rc/vm-fix-p11-round2`,
> commit `3e0ba90`), **open, not yet merged**.
> **Evidence T:** two unit tests that fail with the retraction reverted. Found while
> fixing the P11 VM legs — the test there **had been asserting the wrong behaviour as
> correct**, which is why nothing caught it earlier.

`ComRegisterStep.cs:75` appended a `RollbackRecord.UnregisterCom` record **before**
attempting the registration — the right instinct, since a crash between "acted" and
"journaled" would otherwise leave an unrecorded change — but it **kept** that record on
two paths where the registration provably never happened:

- `ExportMissing` (`:86-88`) — the DLL has no `DllRegisterServer` export;
- `LoadFailed` (`:82-84`) — the DLL could not be loaded at all.

Both return `StepResult.Failed` with the undo record still in the journal.

**Why a stale record is worse than it sounds.** Since **R15**, a failed
`DllUnregisterServer` is interpreted as *"still registered"* (`RollbackJournal.cs:1202`)
— which is the correct fail-closed reading, because silently swallowing an undo failure
is precisely what R15 was filed to stop. So the stale record is a **guaranteed
`UndoFailedException` for a registration that never existed**. With
`on_failure: continue`, the failed step does not abort the install, so the record
reaches `uninstall.json` — and there R15's retain-on-failure rule (keep the state and
the ARP row so the user can retry) means **every subsequent uninstall fails the same
way**. The app can never leave Add/Remove Programs. One faulty or mis-pathed DLL, and
the installer has produced exactly the "silently unremovable" outcome R44 and R51 exist
to prevent, by a route neither of them covers.

**Fix (#44, `3e0ba90`).** Journal-before-act is **kept** — the crash window it protects
is real — and a **tail-only** `RollbackJournal.RetractLast` withdraws the record on the
two paths where the action demonstrably did not occur (`LoadFailed`, `ExportMissing`).
Tail-only matters: it can only ever remove the record the step itself just appended,
so it cannot be used to rewrite journal history. `HResultFailure` **still journals, by
design** — `DllRegisterServer` returning a failure HRESULT does not prove nothing was
written to the registry, so the fail-closed reading is the right one there.

**Follow-up worth doing once, not filed as a row:** audit every journal-before-act
record whose action has **no OS query surface** to confirm afterwards. This class of
bug — an undo recorded for a change that never landed, discoverable only at uninstall
time, on a machine that no longer has the installer — is invisible to any test that
does not actually run an uninstall. Cross-references **R15** (the retain-on-failure
rule that makes this permanent) and **R36** (the decision to keep `com_register`'s
in-process DLL load, which is what makes `LoadFailed` a reachable state at all).
