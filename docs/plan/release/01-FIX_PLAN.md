# Fix plan — first public release

Status: **NOT STARTED (authored 2026-07-28)** against `main` @ `1be494c`.

Execution companion to [`00-GAP_REGISTER.md`](00-GAP_REGISTER.md) — the register
defines *what is wrong*; this defines *who fixes what, when*. Same execution
model as [`../ORCHESTRATION_PLAN.md`](../ORCHESTRATION_PLAN.md) §0–§1: parallel
Claude Code sessions in git worktrees on a Windows dev machine, one branch per
lane, orchestrator merges at wave gates, `main` stays shippable.

Every `R<n>` below is a row in the register. Do not start a lane without reading
its rows there — the evidence and the fix rationale live in the register, not
here.

**Sequencing principle:** the security fixes land first and alone. Everything
else — docs, scaffolding, supply chain — is worthless if the product still has
a local privilege-escalation path, and merging cosmetic changes alongside
security changes makes the security diff harder to review.

---

## 0. Standing rules (paste into every agent prompt — see §5 preamble)

- The register row is the contract. Read it fully, and read the cited code,
  before changing anything. Where this plan and the register disagree, the
  register wins.
- Hard gates, unchanged from the original track: Native AOT +
  `TreatWarningsAsErrors` (any `IL2xxx`/`IL3xxx` fails), source-generated
  serialization only, deterministic packaging output, `.editorconfig` style.
  **New:** `dotnet format --verify-no-changes` must stay clean — after W0 it is
  enforced in CI.
- Every behavior change lands with tests in the matching `tests/` project. For
  security lanes this means a **negative test**: the hostile input is rejected.
  A fix with no test that fails before it is not done.
- Do not touch files owned by another in-flight lane (§4). If you must, stop and
  report instead of editing.
- Never rewrite `docs/plan/*` history. `docs/plan/release/` is this track's
  workspace; append a new doc or an ADR under `docs/architecture/` for any
  decision the register leaves open.
- Definition of done = your lane's VERIFY block passes locally:
  `dotnet build Sigil.slnx -c Release` clean, `dotnet test Sigil.slnx -c Release`
  green, `dotnet format Sigil.slnx --verify-no-changes` clean, plus the
  lane-specific checks.

## 1. Environment

- **Dev machine (Windows):** all lanes run here. Note two measured constraints:
  - **Native AOT publish does not work on this box** — `vswhere.exe` is not
    resolvable and the MSVC linker fails (`MSB3073`, exit 123). Any lane that
    needs a real `Setup.exe` must rely on CI, and must say so rather than
    claiming a local pass.
  - `wrapper-vm-tests.yml` is the only trusted verdict for install/uninstall
    side effects. It is `workflow_dispatch` only today; **V1 changes that.**
- **Worktrees:** `git worktree add ../sigil-s1 -b fix/s1-trusted-state`, one per
  running lane, removed after merge.
- **Branch naming:** `fix/<lane>-<slug>`.
- **Merging:** the orchestrator merges at gates, in the stated order. Lanes
  never merge; they finish with a pushed branch and a summary of files touched,
  tests added, and results.

## 2. Waves and gates

★ = on the critical path to a releasable build.

| Wave | Lanes (parallel) | Blocked by |
|------|------------------|-----------|
| **0** | ★ **W0** format backlog + install the missing CI gates (**solo — no other lane in flight**) | — |
| **1** | ★ **S1** trusted state · ★ **S2** path containment · ★ **S3** staged-binary execution · ★ **T1** test truth | W0 |
| **2** | **S4** network + update hardening · **S5** residual security · **REL** release scaffolding · **DOC** docs truth | S1/S2/S3 merged (S4←S3, S5←S1); REL/DOC need only W0 |
| **3** | ★ **V1** release verification gate (single lane) | everything |

**W0 must run alone.** It reformats 28 files across `src/` and `tests/`
(**R20**). If any lane is in flight, every one of them eats a merge conflict.
It is one mechanical commit; do it first, merge it, then open wave 1.

### Gates

- **G0 (after W0):** `dotnet format --verify-no-changes` exits 0 on `main`;
  `.github/workflows/pr-guards.yml` present and green on a test PR; `.claude/`
  tracked; `_agent-setup/` gone. Manual check: open a throwaway PR titled
  `broken title` and confirm the `pr-title` job **fails**. A gate you have not
  seen fail is not a gate.
- **G1 (after wave 1):** merge in order **S1 → S2 → S3 → T1**. Manual checks:
  - As a standard user, create `C:\ProgramData\Sigil\<AppId>\uninstall.json`
    containing a `restore_file` record targeting `C:\Windows\System32\`. Run a
    machine-scope install elevated. **It must refuse the state and log why** —
    not replay it, and not crash (**R1**, **R19**).
  - As a standard user, plant `HKCU\…\Uninstall\<AppId>` with an
    `UninstallString` pointing at a scratch exe. Run an elevated machine-scope
    install of the same AppId. **The scratch exe must not run** (**R2**).
  - `Setup.exe /allusers /D=C:\Users\Public\evil` must be **rejected** with a
    clear diagnostic (**R3**).
  - `dotnet test -c Release` totals now show a **non-zero skip count**. Record
    the exact number — that number is the honest size of the untested surface
    (**R6**).
  - CI green, including the coverage gate with its new per-assembly floors.
- **G2 (after wave 2):** merge in order **S4 → S5 → REL → DOC**. Manual checks:
  - A manifest with `source: { url: "http://…" }` fails to pack (**R8**).
  - `sigil init --template full` produces a manifest that packs, and its
    `signingKey` placeholder is a base64 SPKI, not a file path (**R30**).
  - Copy-paste the silent-install line from `docs/guides/parameters.md` into a
    real `Setup.exe` — **it must succeed** (**R26**).
  - `THIRD-PARTY-NOTICES.md` names Skia, ANGLE, HarfBuzz, and libsodium
    explicitly (**R23**).
  - `dotnet restore --locked-mode` succeeds from a clean clone (**R23a**).
- **G3 (after V1):** the release checklist in
  [`02-READINESS_REPORT.md`](02-READINESS_REPORT.md) §"Definition of done" is
  fully ticked, with the VM matrix run **and its run URL recorded**. Only then
  tag.

If a lane fails its gate, hold only its dependents. S1 failing holds S5; it does
not hold REL or DOC.

## 3. Model per lane

| Lane | Model | Why |
|---|---|---|
| **W0** | `claude-haiku-4-5-20251001` | Mechanical: run a formatter, move files, edit `.gitignore`. Zero judgement. |
| **S1** | `claude-opus-5` | The trust-boundary redesign. Getting "which state do we trust" subtly wrong reproduces the blocker. |
| **S2** | `claude-opus-5` | Containment semantics across a dozen call sites; the failure mode of a wrong check is a silent bypass. |
| **S3** | `claude-opus-5` | TOCTOU and Windows file-handle/ACL semantics — easy to write a fix that looks right and still races. |
| **T1** | `claude-sonnet-5` | Well-specified mechanical change (early return → `Assert.Skip`, add YAML guards, extend a Python dict), but touches CI, so not trivia. |
| **S4** | `claude-opus-5` | R13 (freshness/replay) is a protocol design decision, not a patch. |
| **S5** | `claude-sonnet-5` | Nine small, individually-specified fixes with explicit register guidance. Escalate R18 to opus if the secret channel proves non-obvious. |
| **REL** | `claude-sonnet-5` | Specified authoring + YAML plumbing against a clear spec. |
| **DOC** | `claude-sonnet-5` | Bulk find/replace plus one new reference page, all against verified code truth. |
| **V1** | `claude-opus-5` | Adversarial re-review and the go/no-go call. Never delegate the verdict to a cheaper model. |

## 4. Conflict map (file ownership while in flight)

| Hotspot | Owner | Everyone else |
|---|---|---|
| Whole tree (formatting pass) | **W0**, solo | nobody branches until W0 merges |
| `.github/workflows/pr-guards.yml`, `.claude/**`, `_agent-setup/`, `.gitignore` | W0 | — |
| `Engine/UninstallStateStore.cs`, `RollbackJournal.cs`, `UninstallEngine.cs`, `InstalledStateResolver.cs`, `InstallSession.cs`, `ScopeLayout.cs` | **S1** | S5 extends `RollbackJournal`/`UninstallEngine` **after** S1 merges |
| `Engine/InstallDirResolver.cs`, `StepContext.cs`, new `PathContainment.cs`, all `Steps/*` path handling, `docs/guides/install-steps.md` | **S2** | DOC must not touch `install-steps.md`; S5 owns `XmlEditStep`/`JsonEditStep` *content* semantics only, not their paths |
| `Engine/NativeRuntimeBootstrap.cs`, `SigilDownloader.cs`, `PrerequisiteRunner.cs`, `AuthenticodeVerifier.cs`, `Update/UpdateRunner.cs`, `Update/UpdateSeams.cs`, `Packaging/ExeWrapper/ExeWrapperPackager.cs` | **S3** | S4 extends `UpdateRunner`/`ChannelManifest*` **after** S3 merges |
| The 8 VM-gated test classes + the runtime-gated packaging tests (gating constructs only), `ci.yml`, `wrapper-vm-tests.yml` | **T1** | security lanes add **new** test files freely; they must not edit those files' gating |
| `schemas/sigil-schema.json`, `ManifestParser.cs`, `docs/manifest-reference.md`, `examples/**` | **S4** | schema is a lockstep surface — S4 does the whole chain in one pass (`.claude/skills/schema-change`) |
| `README.md`, `docs/guides/**` (except `install-steps.md`), `docs/getting-started.md`, `docs/architecture-overview.md`, `docs/architecture/**`, `sigil-docs/`, `CODEOWNERS` | **DOC** | — |
| `SECURITY.md`, `CHANGELOG.md`, `THIRD-PARTY-NOTICES.md`, `release.yml`, `dependabot.yml`, `NuGet.config`, `Directory.Build.props`, `packages.lock.json`, version literals | **REL** | REL touches `ci.yml` **only** for the version smoke at `:136`; coordinate with T1 |

Two known cross-lane touchpoints, called out so nobody improvises:

- **`ci.yml`** is edited by T1 (coverage gate, runtime staging) and REL (version
  smoke, artifact contents). T1 merges first; REL rebases.
- **`Directory.Build.props`** is edited by REL only (version SoT + lock files).

## 5. Agent prompts

Common preamble — **prepend to every prompt below**:

```text
You are fixing one lane of Sigil's first-release hardening track, in a dedicated
git worktree on branch fix/<lane>. Read docs/plan/release/00-GAP_REGISTER.md in
full, then re-read the specific R<n> rows assigned to you and the code they
cite. The register's evidence has been verified against the tree — trust it, but
re-read the code before changing it, because line numbers move.

Sigil produces a Windows Setup.exe that elevates to admin, writes HKLM and
Program Files, mutates PATH, and installs a self-deleting uninstaller. Threat
model: an unprivileged local user on the same machine, and a network attacker.
"The publisher wouldn't do that" is not a mitigation; "an unprivileged user
cannot influence this input" is.

Rules: Native AOT-safe only (no reflection, source-gen serialization, any
IL2xxx/IL3xxx is a build failure), TreatWarningsAsErrors, match .editorconfig,
deterministic packaging. Every behavior change lands with tests, and every
security fix lands with a NEGATIVE test that fails before your change and passes
after — state explicitly in your summary that you ran it both ways.

Touch only the files in SCOPE. If you believe you must edit something else, stop
and report why instead of editing it. Do not merge; finish by pushing the branch
and summarizing files touched, tests added, and test results.

Verify before finishing: dotnet build Sigil.slnx -c Release &&
dotnet test Sigil.slnx -c Release && dotnet format Sigil.slnx
--verify-no-changes, plus your VERIFY block. Native AOT publish does NOT work on
this machine (vswhere/MSVC linker) — if your VERIFY needs it, say so plainly and
leave it to CI rather than claiming a pass you did not observe.
```

### W0 — format backlog + install the missing CI gates (wave 0, solo)

```text
TASK: Register R20, R40, R41. Mechanical only — no behavior changes.
READ: R20, R40, R41; _agent-setup/apply.ps1; _agent-setup/github-workflows/
pr-guards.yml; .gitignore; AGENTS.md "PR checklist"; CONTRIBUTING.md:59,69.
SCOPE:
 1. Run `dotnet format Sigil.slnx` and commit the result as its own commit.
    28 of 465 files are expected to change. Review the diff to confirm it is
    whitespace/ordering only — if any change alters semantics, STOP and report.
 2. Install the staged agent config: copy _agent-setup/github-workflows/
    pr-guards.yml to .github/workflows/pr-guards.yml and _agent-setup/
    claude-config/* to .claude/ (this is what _agent-setup/apply.ps1 does; read
    it, do not necessarily run it). Then `git rm -r _agent-setup/`.
 3. .gitignore: delete the no-op "./docs/" line; add ".superpowers/".
OUT: any src/ or tests/ change beyond the formatter's own output. Any docs
rewrite (that is DOC's lane). Deleting stale remote branches (orchestrator does
that at G0).
VERIFY: `dotnet format Sigil.slnx --verify-no-changes` exits 0.
`dotnet build Sigil.slnx -c Release` and `dotnet test Sigil.slnx -c Release` are
unchanged from before your commit — report both totals. `git check-ignore -v
.superpowers` exits 0. `git ls-files .claude` is non-empty.
```

### S1 — trusted state (wave 1) ★ critical

```text
TASK: Register R1, R2, R19 — the release-blocking privilege-escalation path.
Elevated install AND uninstall currently load and replay a JSON journal that an
unprivileged user can write, and spawn an executable path read from HKCU.
READ: R1, R2, R19 in full; Engine/UninstallStateStore.cs, RollbackJournal.cs,
UninstallEngine.cs, InstalledStateResolver.cs, InstallSession.cs (:605-630,
:905-975), ScopeLayout.cs; Json/SerializableRollbackRecord.cs.
SCOPE:
 - New Engine/StateDirectorySecurity.cs: create the machine-scope state
   directory with an explicit DACL (SYSTEM + Administrators FullControl, Users
   Read, inheritance disabled), and expose an ownership check
   (owner is SYSTEM or Administrators) used before any load.
 - UninstallStateStore: delete the opposite-scope fallback (:136-140); derive
   scope from the directory the file was found in, not from the field inside it
   (:174); gate every load on the ownership check; cap file size and record
   count; widen the try to cover record rehydration so hostile JSON fails
   closed rather than crashing (R19).
 - RollbackJournal: anchor replay. Reject any record whose target path is not
   under the recorded install_dir or an explicit scope-root allowlist, and any
   registry coordinate not under the app's own subtree. Re-derive
   unregister_com's DLL path from install_dir rather than trusting the
   persisted free-form path. A rejected record is logged and skipped, not
   silently ignored.
 - InstalledStateResolver: for a machine-scope resolve, probe HKLM only
   (:38-40). Do not fall back to HKCU.
 - InstallSession.RunPriorUninstallAsync: before spawning PriorUninstallExe,
   require it to be Authenticode-verified (AuthenticodeVerifier.VerifyFile
   already exists) or to sit under an admin-only directory. Refuse otherwise
   with a clear message; do not silently continue.
OUT: the %LocalAppData% native runtime cache (S3 owns R4). Elevation argv
secrets (S5 owns R18). Uninstall failure reporting (S5 owns R15) — but do not
make R15 worse.
NOTE: R1 and R2 are two routes to the same outcome. Fix both; a fix for one is
not a fix for the other.
VERIFY: negative tests, each failing before your change:
 (a) a journal in %LocalAppData% is NOT loaded by a machine-scope operation;
 (b) a journal whose restore_file path is outside install_dir is refused;
 (c) a journal with an unknown record type / null record / oversized file is
     refused without an unhandled exception;
 (d) a machine-scope resolve ignores an HKCU ARP entry;
 (e) a state directory not owned by Administrators/SYSTEM is refused.
Then, manually and as a standard user on this box: plant
C:\ProgramData\Sigil\<AppId>\uninstall.json and confirm an elevated install
refuses it. Report what the log said.
```

### S2 — path containment (wave 1) ★ critical

```text
TASK: Register R3, R9, R16, R31, R32. install_dir is attacker-steerable via
/D= and nothing anchors privileged step targets to it, so a SYSTEM scheduled
task or service can be pointed at a user-writable directory.
READ: R3, R9, R16, R31, R32 in full; Engine/InstallDirResolver.cs (:50-110),
Engine/StepContext.cs (:357, :399-440, :502-540), ScopeLayout.cs; Steps/
ScheduledTaskCreateStep.cs, ServiceInstallStep.cs, Win32/ComRegisterStep.cs,
FirewallRuleStep.cs, ConfigFileEditor.cs, IniWriteStep.cs, FileCopyStep.cs,
FileDeleteStep.cs, DirectoryDeleteStep.cs, HttpDownloadStep.cs;
docs/guides/install-steps.md:185-235.
SCOPE:
 - New Engine/PathContainment.cs: ONE helper, EnsureUnder(root, candidate),
   using the proven logic at StepContext.cs:527-532 (GetFullPath first, then a
   separator-terminated case-insensitive prefix compare). Also reject reparse
   points / junctions in the ancestor chain. Delete the duplicated logic where
   it makes sense to route through the helper — but do NOT weaken the existing
   payload:// or zip-slip guards; they are verified sound.
 - InstallDirResolver.Resolve: reject a resolved install_dir not under
   ScopeLayout.For(scope).InstallRoot. This is the /D= fix and it is the single
   highest-value line in this lane.
 - The four machine-scope privileged steps: require the resolved target to be
   contained in install_dir AND to sit in a directory not writable by
   non-administrators. Refuse the step otherwise.
 - Route every step DESTINATION through EnsureUnder against ctx.InstallDir,
   with an explicit, documented manifest opt-out for deliberate out-of-tree
   writes. Note FileCopyStep.cs:23 currently calls ctx.Resolve, not
   ctx.ResolvePath — fix that too.
 - Fail the step on an unresolved brace token in a path field (today an
   unknown {var.x} is left literal and a directory named "{var.x}" is created).
 - ScheduledTaskCreateStep: reject '"' in program, or build via /XML with
   escaping (R31). IniWriteStep: reject or escape CR/LF and a leading '[' in
   section/key/value (R32).
 - Update the scheduled_task_create and service_install examples in
   docs/guides/install-steps.md — they currently demonstrate the vulnerable
   pattern.
OUT: schema/parser-level URL validation (S4). XmlEditStep/JsonEditStep value
semantics (S5). Do not touch InstallSession.cs — S1 owns it.
VERIFY: negative tests, each failing before your change:
 (a) /D= pointing outside the scope root is rejected;
 (b) a scheduled_task_create whose program resolves outside install_dir is
     refused;
 (c) an ini_write/json_edit/xml_edit path escaping install_dir via "..", an
     absolute path, and a junction is refused (three cases);
 (d) a path field containing an unresolved {var.x} fails the step;
 (e) a task program containing '"' is refused;
 (f) an ini value containing "\n[Other]" does not create a second section.
Confirm the existing payload:// traversal and zip-slip tests still pass
unchanged.
```

### S3 — staged-binary execution (wave 1) ★ critical

```text
TASK: Register R4, R5, R10, R11, R12, R17. Everything this product downloads or
extracts is verified, then executed from a location an unprivileged same-user
process can modify in between.
READ: R4, R5, R10, R11, R12, R17 in full; Engine/NativeRuntimeBootstrap.cs
(:80-110, :165-175), SigilDownloader.cs (:107-171), PrerequisiteRunner.cs
(:100-130, :225-250), AuthenticodeVerifier.cs, Update/UpdateRunner.cs
(:160-220), Update/UpdateSeams.cs (:60-90); Packaging/ExeWrapper/
ExeWrapperPackager.cs (:225-255); Installer.Host/Program.cs:60-140.
SCOPE:
 - NativeRuntimeBootstrap (R4, the worst of these): when the process is
   elevated, do not use %LocalAppData%. Extract to an admin-only directory
   (%ProgramData% with a hardened DACL, or %WINDIR%\Temp) and verify each
   extracted file against the embedded archive's hashes before
   AddDllDirectory. The completion-marker shortcut must not be trustable by a
   non-admin. Note this runs AFTER the elevation branch at Program.cs:71-77 —
   that ordering is correct and must stay.
 - Web-installer stub (R5): ExeWrapperPackager.cs:230 uses a pack-time-constant
   filename under {temp_dir}. Give it a randomized, admin-only staging
   directory, and re-verify the SHA-256 immediately before the run_program
   step. A predictable path plus a verify/exec gap is the whole bug.
 - Prerequisites and update payload (R12): stage into a per-run randomly named
   admin-only subdirectory; hold an open handle denying write/delete from hash
   verification through process launch.
 - R11: call AuthenticodeVerifier.VerifyFile immediately before launching ANY
   downloaded binary — prerequisite, update package, web-stub payload. Fail
   closed. Add a documented per-prerequisite opt-out for unsigned redists.
 - R10: add a maxBytes ceiling to SigilDownloader (reject up front when
   ContentLength exceeds it, abort mid-stream regardless) and cap the channel
   manifest fetch in UpdateSeams at a few hundred KB — that buffer is
   pre-authentication.
 - R17: AuthenticodeVerifier currently passes WTD_REVOKE_NONE. Switch to
   whole-chain revocation, and render a DISTINCT state when revocation status
   is unavailable (offline) rather than silently trusting or silently failing.
OUT: channel manifest freshness/replay (S4 owns R13) and manifestUrl scheme
(S4 owns R14). Do not edit InstallSession.cs — S1 owns it; if the update temp
path at InstallSession.cs:1169 needs to change, report it and let the
orchestrator route it.
VERIFY: negative tests, each failing before your change:
 (a) a pre-planted native-runtime cache directory with a bogus DLL and a valid
     marker is NOT trusted;
 (b) a download exceeding maxBytes is aborted (both the ContentLength-declared
     and the lying-server cases);
 (c) an unsigned binary is refused at launch unless explicitly opted out;
 (d) a revoked-certificate fixture does NOT produce a trust line.
The web-stub end-to-end path needs a real Setup.exe, which cannot be built on
this machine — write the test, mark clearly that it is CI-only, and say so in
your summary.
```

### T1 — test truth (wave 1) ★ critical

```text
TASK: Register R6, R21, R22. The suite reports 1096 passes and 1 skip, but
roughly 24 of those "passes" are early returns that assert nothing — including
every real install, uninstall, scope, upgrade, and Setup.exe-stamping proof.
READ: R6, R21, R22 in full; tests/SigilBuild.Wrapper.IntegrationTests/
TestEnvironment.cs and the 8 gated classes named in R6; tests/
SigilBuild.Packaging.Tests/ExeWrapper/ExeWrapperPackagerTests.cs and
ExeWrapperWebInstallerPackTests.cs; .github/workflows/ci.yml (:20-105, :106-230)
and wrapper-vm-tests.yml (esp. :231-236, the one job that gets this right).
SCOPE:
 - Replace every `if (!ShouldRun()) { return; }` soft-skip with xUnit v3
   Assert.Skip(reason), or a [VmFact] attribute computing Skip. The reason
   string must name the missing precondition (SIGIL_VM_TESTS, staged runtime,
   admin) so the skip is actionable. Same for the Console.WriteLine("SKIP:")
   + return pattern in the packaging tests.
 - ci.yml: stage the installer-host runtime in the `build` job BEFORE
   `dotnet test`, so the pack/Setup.exe path is actually exercised per push.
   (Today publish-installer-runtime.ps1 only runs in the later aot-publish job.)
 - ci.yml coverage gate: add an enforced per-assembly floor map, and an
   expected-assembly allowlist that FAILS when an assembly is missing from the
   reports — SigilBuild.Cli, SigilBuild.Wrapper, and SigilBuild.Installer.Host
   currently contribute zero lines and nothing notices. Set each floor at the
   current measured value rounded DOWN, not at the aspirational target: the
   point is a ratchet, not a cliff. Print target-vs-actual so the gap stays
   visible.
 - wrapper-vm-tests.yml: copy the p11 job's pre-flight guard (:231-236) into the
   scope-matrix job and the p12 job — assert SIGIL_VM_TESTS=1 and assert the
   staged runtime exists before dotnet test.
OUT: writing new product tests (the security lanes do that). Changing what the
gated tests assert. Do not lower any existing threshold.
VERIFY: `dotnet test Sigil.slnx -c Release` now reports a NON-ZERO skip count —
report the exact number and the totals. Confirm the one legitimate
[Fact(Skip=)] in ComRegisterInstallTests is untouched. Push and confirm the CI
`build` job now executes the previously-skipped packaging tests (they must go
green, not skip). If the per-assembly floors would fail CI as written, report
the numbers rather than lowering them silently.
```

### S4 — network + update hardening (wave 2)

```text
TASK: Register R8, R13, R14, R30, R37, R39.
READ: those rows; Core/Configuration/ManifestParser.cs (:150-160, :1085-1110),
schemas/sigil-schema.json (:48-55, :457-471, :620-628); Installer.Host/Services/
HttpOptionsLoader.cs; Update/ChannelManifest.cs, ChannelManifestParser.cs,
UpdateRunner.cs (post-S3), UpdateSeams.cs; Cli/Commands/Templates/
full-config.yaml; docs/architecture/adr-009-update-manifest-signature.md.
SCOPE:
 - R8: reject non-https parameter `source.url` at pack time (new diagnostic in
   the SIG023x band, mirroring SIG0235) AND re-check the substituted URL in
   HttpOptionsLoader before the GET. This is the only HTTP consumer with no
   scheme check; http_download and packageUrl both already enforce it.
 - R14: same treatment for updates.manifestUrl, at pack time and before fetch.
   Constrain it in the schema too — the schema description already says HTTPS.
 - R13 (design work, not a patch): add freshness to the channel manifest. Add a
   required signed issuedAt/expiresAt and/or a monotonic sequence persisted in
   machine-scope state; reject stale manifests and non-increasing sequences.
   Every new field MUST be inside the signed byte range — the current signing
   scheme covers the whole document and that property is the thing most worth
   not breaking. Write an ADR under docs/architecture/ recording the choice,
   the replay threat, and the clock-skew tolerance.
 - R30: fix full-config.yaml's signingKey to a base64 SPKI placeholder (it
   currently shows a private-key FILE PATH and names the wrong algorithm), and
   add a pack-time diagnostic that signingKey decodes as base64 and imports as
   a P-256 SPKI.
 - R37: treat an incomparable installed version as not-eligible when a
   minFromVersion floor is declared. R39: verify the manifest signature BEFORE
   parsing it.
NOTE: this lane changes schemas/sigil-schema.json, which is a LOCKSTEP surface.
Follow .claude/skills/schema-change: docs/manifest-reference.md, examples/**,
and tests/SigilBuild.Schema.Tests/ fixtures all move in the same commit, and the
step-type enum appears in MULTIPLE places in the schema file.
OUT: TLS/redirect plumbing and download size caps (S3 did those).
VERIFY: negative tests — an http:// source.url fails to pack; an http://
manifestUrl fails to pack; a stale/replayed signed manifest is rejected; a
signingKey that is a file path fails to pack; a tampered manifest is still
rejected (regression). Confirm the existing ChannelManifestVerifier suite
passes unchanged — that code is verified sound and you must not regress it.
```

### S5 — residual security (wave 2)

```text
TASK: Register R15, R18, R28, R29, R33, R34, R35, R36, R38. Nine independent
fixes; one commit each so review stays legible.
READ: each row; Engine/UninstallEngine.cs (:42-60) and RollbackJournal.cs
(:48-63, :106-118) post-S1; Engine/Elevation.cs (:92-96, :143-155);
Steps/RunProgramStep.cs (:41-62); Engine/Launcher.cs (:37-47);
Steps/XmlEditStep.cs (:42-45), JsonEditStep.cs (:160-169);
Engine/SetupInstanceLock.cs (:49-93), FilesInUse.cs (:209-210).
SCOPE, in descending value order:
 - R15: uninstall currently swallows every undo failure, returns Ok, and then
   DELETES the state file — so a failed removal leaves an orphaned SYSTEM task
   or firewall rule with no record. Capture per-record outcomes, surface
   failures, and RETAIN the state file when any record failed.
 - R29: Launcher falls through to an admin-token launch when de-elevation
   fails, silently. Skip the launch and surface a notice instead.
 - R18: secrets ride the command line twice — the UAC relaunch re-emits
   /P<secret>=<value>, and run_program args carry resolved secrets. Move them
   to an inherited pipe or a DPAPI-protected temp file; document that
   run_program args are not a secret channel. If the elevation-relaunch channel
   turns out to need a design decision, STOP and report rather than improvising.
 - R28: .sigil-bak stashes persist in Program Files after a successful install.
   This is REQUIRED for uninstall to restore pre-existing files, so do not just
   delete them — decide the contract (move them into the per-app state dir, or
   discard and document that uninstall cannot restore) and write it down.
 - R33: XmlEditStep relies on a framework default for XXE. Set XmlResolver =
   null explicitly, load via XmlReader.Create with DtdProcessing.Prohibit, and
   add a <!DOCTYPE> regression test that asserts the posture.
 - R34: SetupInstanceLock's CreateMutexW NULL branch fails OPEN. Distinguish
   ERROR_ACCESS_DENIED (treat as contention) from other failures; log which.
 - R35: json_edit re-parses string values as JSON. Add value_type:
   string|json, defaulting to string. (Schema change — lockstep chain applies.)
 - R36: document com_register's in-process high-integrity execution as an ADR
   note, or move it to a child process. Decide and record.
 - R38: FilesInUse passes a mutated managed string to RmStartSession. Use a
   char[33]/Span<char> with a ref char signature.
OUT: anything S1/S2/S3 own. Rebase on them; do not re-fix their code.
VERIFY: a negative test per fix where one is meaningful — specifically a failed
undo retains state and reports (R15), a <!DOCTYPE> payload does not expand
(R33), and a de-elevation failure does not launch (R29).
```

### REL — release scaffolding (wave 2)

```text
TASK: Register R7, R23, R23a, R24, R42. There is currently no way for a user to
obtain sigil.exe, no disclosure channel, no attribution for redistributed
native binaries, and no reproducible restore.
READ: those rows; .github/workflows/ci.yml (:106-230); Directory.Build.props;
Directory.Packages.props; src/SigilBuild.Cli/Program.cs:11 and
SigilBuild.Cli.csproj:9; tests/SigilBuild.Cli.Tests/VersionCommandTests.cs:36.
SCOPE:
 - SECURITY.md: contact, supported-versions table, disclosure window. Also
   enable GitHub private vulnerability reporting (note it in the PR body — it
   is a repo setting, not a file).
 - THIRD-PARTY-NOTICES.md: generate from the package graph, and name the NATIVE
   payloads explicitly — libSkiaSharp.dll bundles Skia and ANGLE (BSD-3-Clause)
   and HarfBuzz (MIT); libsodium.dll (via NSec) is ISC. These carry binary
   redistribution requirements that the repo's MIT LICENSE does not satisfy, so
   this is compliance, not courtesy. Ship it beside the binaries in the release.
 - CHANGELOG.md: Keep-a-Changelog. One 0.1.0 entry covering T1-T18 and P0-P13,
   written from git history and the two plan docs. Include a "Known
   limitations" section lifted from 02-READINESS_REPORT.md.
 - Version single source of truth (R24): one value in Directory.Build.props;
   Program.cs reads AssemblyInformationalVersion instead of a const; the test
   and the ci.yml smoke assert AGREEMENT with the csproj value, not a literal.
   Four hardcoded copies exist today — leave zero.
 - release.yml, triggered on `push: tags: v*`: AOT-publish win-x64 and
   win-arm64, Authenticode-sign, attach the binaries PLUS a SHA256SUMS file and
   the notices to a GitHub Release. Require the wrapper-vm-tests matrix as a
   gate. Critically: the release asset must be the WHOLE publish directory, not
   a bare sigil.exe — ci.yml:224 currently uploads only sigil.exe, which cannot
   run without libSkiaSharp.dll and libsodium.dll beside it. Fix that artifact
   too.
 - R23a: RestorePackagesWithLockFile=true in Directory.Build.props, commit the
   lock files, add --locked-mode to CI restore, add a NuGet.config declaring
   nuget.org as the only feed.
 - R42: .github/dependabot.yml (nuget + github-actions, weekly) and a
   `dotnet list package --vulnerable --include-transitive` CI step failing on
   High/Critical. Report what that scan finds — do not fix findings in this
   lane, file them.
OUT: README and guide content (DOC's lane). Choosing the version number — the
orchestrator decides that at G3; parameterize and leave a clear TODO marker.
VERIFY: `dotnet restore --locked-mode` succeeds from a clean clone;
`dotnet run --project src/SigilBuild.Cli -- --version` matches
Directory.Build.props with no literal duplicated anywhere (grep to prove it);
release.yml passes `actionlint` or equivalent. The real signed release run is
CI-only and tag-triggered — do not claim you verified it locally.
```

### DOC — docs truth (wave 2)

```text
TASK: Register R25, R26, R26a, R27, R41a. The docs teach a silent-install
command that hard-fails, name output files that do not exist, and describe a
product with delta updates and an SDK that were never built.
READ: those rows; README.md; docs/guides/installer-wizard.md,
parameters.md, packaging-formats.md, uninstaller.md, upgrades.md,
localization.md, updates.md; docs/getting-started.md; docs/README.md;
docs/architecture-overview.md; docs/cli-reference.md; CODEOWNERS;
docs/sprint-01/identifier-reservation.md. Code truth: Wrapper.Core/Cli/
CommandLineParser.cs (the flag list at :372 and :504 is authoritative),
Engine/InstallSurvivability.cs:17, Packaging/ExeWrapper/ExeWrapperPackager.cs
:40,:47,:134, Packaging/Zip/ZipPackager.cs:24-25.
SCOPE:
 - HIGHEST VALUE, do it first: the documented silent-install syntax is
   REJECTED by the parser. installer-wizard.md:97 and parameters.md:78 show
   `setup.exe /S /install_dir="..." /edition=professional`; only a `P` prefix
   is accepted (/PName=Value), everything else throws UsageException. Fix those
   two lines and the prose at installer-wizard.md:100, parameters.md:81,
   packaging-formats.md:39.
 - New docs/setup-exe-reference.md enumerating ALL fifteen accepted tokens with
   semantics and exit codes. Four are documented nowhere today (/verysilent,
   /launch, /Poption.Name=Value, /? |/help) and /D= is mentioned only in
   passing. Link it from docs/README.md. Note the existing generator cannot
   produce this — it introspects the sigil command tree, not CommandLineParser
   — so this page is hand-written; say so in a comment so nobody "regenerates"
   it away.
 - uninstaller.exe -> uninstall.exe in all six files (uninstaller.md:7,20,32,
   docs/README.md:28, getting-started.md:174, installer-wizard.md:54,
   packaging-formats.md:36). The documented UninstallString is wrong.
 - getting-started.md: sigil.exe is 13.98 MB and NOT single-file (:42); ZIP
   output is flat, not ./dist/<app-id>-<version>/ (:118); MSIX/exe/web all ship
   now, delete the "Sprint 4" text (:109-112); output is
   <App>-<ver>-<arch>-Setup.exe (:120,131,174).
 - README.md: delete the delta-update + client-SDK claim (ADR-010 defers it and
   there is no SDK project) or mark it clearly as roadmap; delete the
   macOS/Linux install lines and the winget/curl/dotnet-tool commands that do
   not work (coordinate with REL — if release.yml ships a GitHub Release, point
   at that instead); ADD a section describing what actually exists: a signed,
   branded Setup.exe with an install-step engine, rollback journal,
   uninstaller, and /Update.
 - architecture-overview.md (R26a): ZstdSharp.Port not ZstdNet and no native
   fallback (:90); update signatures are ECDSA P-256 via BCL, not Ed25519/NSec
   (:91); the component layout omits 4 of 9 projects (:70-77); relabel the
   metrics table "targets" and mark which are actually CI-gated (:98 currently
   claims all are).
 - R27: renumber sigil-docs/architecture/'s two ADRs to 011/012, move them into
   docs/architecture/, delete sigil-docs/, and repoint CODEOWNERS at
   /docs/architecture/ (it currently guards the stale tree and a decisions.md
   that does not exist, leaving the live security ADRs unowned).
 - R41a: update docs/sprint-01/identifier-reservation.md to reflect reality, or
   delete it. Flag to the orchestrator that the NuGet IDs are still unclaimed
   while README advertises them — that is a squat risk to close BEFORE going
   public.
OUT: docs/guides/install-steps.md (S2 owns it). docs/plan/** (read-only
history). Generated files cli-reference.md and manifest-reference.md — change
their GENERATORS if needed, never the output.
VERIFY: for every flag and filename you write, cite the code line you took it
from in your summary. `docs.yml` must stay green (it fails on generated-doc
drift). Manually copy-paste the corrected silent-install line and confirm the
parser accepts it — if a real Setup.exe cannot be built here, run the tokens
through CommandLineParser in a scratch test instead and say that is what you
did.
```

### V1 — release verification gate (wave 3, single lane)

```text
TASK: Decide go/no-go. No new product behavior. You are the adversary here, not
the author — assume each lane's fix is incomplete until you have made it fail.
READ: 00-GAP_REGISTER.md end to end; the merged diffs of every lane; the gate
checklists in 01-FIX_PLAN.md §2; 02-READINESS_REPORT.md "Definition of done".
SCOPE:
 - Walk every R<n> in the register. For each, either demonstrate the fix (name
   the test or the manual step and its observed output) or record it as
   knowingly deferred with a one-line justification. No row may be silently
   dropped.
 - Re-attack R1-R5 by hand as a standard user on a real machine: plant the
   journal, plant the HKCU ARP entry, pass a hostile /D=, pre-create the native
   runtime cache, and race the web-stub staging path. Report what happened.
 - RUN wrapper-vm-tests.yml for real and record the run URL. This is the first
   time in this track that the install/uninstall/scope/upgrade matrix will have
   executed against non-vacuous tests — read the log and confirm the previously
   soft-skipping tests actually ran.
 - Re-measure and report: build warnings, test totals INCLUDING the skip count,
   coverage per assembly vs the new floors, sigil.exe and installer-host sizes
   vs the 15 MB / 45 MB gates (CI only — AOT publish does not work on the dev
   box).
 - Confirm the release dry-run: tag a throwaway prerelease and verify
   release.yml produces signed, checksummed artifacts with the notices, and
   that the downloaded artifact actually RUNS on a clean machine (the
   sibling-DLL trap from R7).
DELIVER: an updated "Definition of done" checklist in 02-READINESS_REPORT.md
with each box ticked or explicitly deferred, plus a one-paragraph go/no-go
recommendation. Update the status line at the top of this plan.
OUT: fixing anything you find. File it as a new register row and reopen the
owning lane — do not fix forward in main.
```

## 6. Orchestrator loop

1. Run **W0 alone**. Merge it. Do not open wave 1 until
   `dotnet format --verify-no-changes` is clean on `main` and you have watched
   `pr-guards` fail a deliberately bad PR title.
2. Open wave 1: four worktrees, four sessions, preamble + lane prompt each.
3. On each "done" report: pull the branch, run build + tests + format yourself,
   review the diff against the lane's SCOPE (files outside scope = send back),
   and for security lanes **check out the parent commit and confirm the negative
   test actually fails there**. A negative test that passes before the fix is
   not a test.
4. At the gate: merge in the stated order, rebasing later branches; run the
   gate's manual checks personally; push; wait for CI.
5. Anything red: reopen the owning lane with the failure attached. Do not fix
   forward in `main`.
6. Between G1 and G2, delete the 15 stale remote branches and reserve the NuGet
   IDs (R41, R41a) — both are orchestrator chores, not lane work.
7. After G3: decide the version, tag, publish. The release shape is argued in
   [`02-READINESS_REPORT.md`](02-READINESS_REPORT.md).

## 7. Progress checklist

| Lane | Branch | Model | Rows | Started | Pushed | Merged | Gate |
|------|--------|-------|------|:---:|:---:|:---:|------|
| W0  | `fix/w0-format-and-gates` | haiku-4.5 | R20, R40, R41 | ☐ | ☐ | ☐ | G0 |
| S1  | `fix/s1-trusted-state` | opus-5 | R1, R2, R19 | ☐ | ☐ | ☐ | G1 |
| S2  | `fix/s2-path-containment` | opus-5 | R3, R9, R16, R31, R32 | ☐ | ☐ | ☐ | G1 |
| S3  | `fix/s3-staged-execution` | opus-5 | R4, R5, R10, R11, R12, R17 | ☐ | ☐ | ☐ | G1 |
| T1  | `fix/t1-test-truth` | sonnet-5 | R6, R21, R22 | ☐ | ☐ | ☐ | G1 |
| S4  | `fix/s4-network-update` | opus-5 | R8, R13, R14, R30, R37, R39 | ☐ | ☐ | ☐ | G2 |
| S5  | `fix/s5-residual-security` | sonnet-5 | R15, R18, R28, R29, R33–R36, R38 | ☐ | ☐ | ☐ | G2 |
| REL | `fix/rel-scaffolding` | sonnet-5 | R7, R23, R23a, R24, R42 | ☐ | ☐ | ☐ | G2 |
| DOC | `fix/doc-truth` | sonnet-5 | R25, R26, R26a, R27, R41a | ☐ | ☐ | ☐ | G2 |
| V1  | `fix/v1-verification` | opus-5 | all | ☐ | ☐ | ☐ | G3 |

**Not scheduled, by decision:** R43 (plan docs stale — `docs/plan/*` is
read-only history; this register is the correction). R42's SkiaSharp-preview
migration is filed but may slip past v1 as a documented limitation if Avalonia
12 still requires it.
