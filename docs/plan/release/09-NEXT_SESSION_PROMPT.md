# Next-session kickoff — Stage 1 (security core)

This file is a **paste-ready prompt**. Open a fresh Claude Code session at the
repo root and paste everything inside the fenced block in §2. §1 is for the human
running it.

---

## 1. How to run it

**Model: `claude-opus-5`.** Stage 1 fixes five local privilege-escalation paths
in software that elevates to admin. Three of its four lanes are opus by design
(see the lane table). Do not economise here.

**Before you paste anything**, do the one outstanding item from Stage 0 — it
takes ten seconds and everything downstream depends on it:

```bash
gh api -X PUT repos/Sigil-build/sigil/rulesets/19919273 -f enforcement=active
gh api "repos/Sigil-build/sigil/rules/branches/release%2Fv0.1.0-alpha" --jq '.[].type'
```

The second command must print a non-empty list. Until it does, ruleset 19919273
is `enforcement: disabled` and **lane PRs can merge fully red**.

Environment: run on the Windows dev machine. Expect ~1 week of wall-clock for
Stage 1. The session will spawn subagents; that is intended.

---

## 2. The prompt

```text
Continue executing Sigil's release-candidate track. Stage 0 is complete and
merged; you are starting Stage 1.

READ FIRST, in this order:
  1. docs/plan/release/03-RC_ORCHESTRATION.md — branch policy, gates, the
     lane→finding map, and the status block at the top
  2. docs/plan/release/05-STAGE-1-security-core.md — your four lanes
  3. docs/plan/release/00-GAP_REGISTER.md — rows R1-R6, R9-R12, R16, R17, R19,
     R21, R22, R31, R32 (the evidence behind every fix you are about to make)
Do NOT read the other stage documents yet; they are later waves.

Use the superpowers:subagent-driven-development skill to execute
docs/plan/release/05-STAGE-1-security-core.md. Its four lanes are file-disjoint
and run in parallel:

  S1 trusted state       opus-5    rc/s1-trusted-state       R1, R2, R19
  S2 path containment    opus-5    rc/s2-path-containment    R3, R9, R16, R31, R32
  S3 staged execution    opus-5    rc/s3-staged-execution    R4, R5, R10, R11, R12, R17
  T1 test truth          sonnet-5  rc/t1-test-truth          R6, R21, R22

Branch policy: cut every lane branch from release/v0.1.0-alpha, never from main.
Each lane opens a PR into release/v0.1.0-alpha. You merge at the gate, in the
order S1 → S2 → S3 → T1. Lanes never merge their own PRs. main stays untouched.

WHAT STAGE 0 LEARNED THE HARD WAY — carry these forward:

- Do a pre-flight scan of the stage document before dispatching anything, and
  batch what you find into ONE question rather than interrupting per discovery.
  Stage 0's scan caught a gate-proof step that would have proved nothing.
- Every verification command in a plan is a claim about this machine. Confirm
  the tool exists before trusting the step. Stage 0 shipped three steps calling
  python3, which here is a Windows Store alias stub, not an interpreter.
- A subagent reporting "verified" is not evidence. Ask for the command and its
  output, and re-run the decisive ones yourself. Three separate Stage 0
  subagents corrected the orchestrator, every time because they were told to
  REPRODUCE rather than assert.
- Local green does not imply CI green. Stage 0's format gate passed locally and
  could never have passed in CI, because the dev box had build artifacts a clean
  checkout does not. When a check is cheap, run it from a clean state.
- The one rule enforced hardest in this track: every security fix lands with a
  negative test, and YOU verify it by checking out the parent commit and
  confirming the test actually FAILS there. Do not take the lane's word for it.
  This track exists because ~24 tests were passing while asserting nothing.

SPECIFIC THINGS TO GET RIGHT IN STAGE 1:

- S1 must implement StateDirectorySecurity.IsAdminOnlyWritable EARLY and say so,
  because S2 (Task S2.3) and S3 (Task S3.1) both consume it while running in
  parallel. S1 is first in the merge order precisely for this. Do not let the
  other two lanes write their own ACL check.
- S3 must not edit InstallSession.cs — S1 owns it. If S3 needs the update temp
  path moved, it reports and you route it.
- T1 owns ci.yml and the gating constructs in the eight VM-gated test classes.
  Security lanes add NEW test files freely but must not touch those.
- T1's coverage floors go at the CURRENT MEASURED values rounded down, not at
  the aspirational targets. CI-measured actuals from Stage 0: project-wide union
  74.74%, Core 63.89%, Signing 68.79%, Wrapper.Core 77.64%, Packaging 72.00%.
  Three shipping assemblies — Cli, Wrapper, Installer.Host — contribute ZERO
  lines to the denominator today; T1 adds the allowlist that makes that visible.
- Gate G1 is not satisfied by green CI. It requires FIVE attacks run BY HAND as
  a standard user, each refused with a log line: plant uninstall.json in
  %ProgramData% and in %LocalAppData%; plant an HKCU ARP entry with an
  UninstallString; pass /D=C:\Users\Public\evil; pre-create the native runtime
  cache with a valid completion marker. G1 also requires recording the new
  non-zero skip count from `dotnet test` — that number is the honest size of the
  untested surface.

KNOWN ENVIRONMENT CONSTRAINTS (do not rediscover these):
- Native AOT publish FAILS on this machine (vswhere / MSVC linker, MSB3073 exit
  123). Anything needing a real Setup.exe is CI-only. Say so; never imply a
  green you did not observe. CI does publish successfully.
- No local YAML parser and no actionlint. GitHub is the authoritative parser.
- dotnet format has NO --configuration flag. pr-guards.yml's generator build and
  its format step must BOTH leave the configuration unset; a guard step asserts
  the analyzer DLL is discoverable and explains why if it is not.
- dotnet test takes ~90s, dotnet build -c Release ~45s, dotnet format ~60-90s.
  Use 600000 ms timeouts and do not assume slowness is failure.
- Baseline to preserve: 1097 tests, 1096 passed, 1 skipped, 0 failed; build 0
  warnings 0 errors; format exit 0. The single skip is
  ComRegisterInstallTests.Live_register_then_unregister_a_real_self_registering_dll
  and is legitimate.

Once ruleset 19919273 is active, direct pushes to release/** are blocked and
strict_required_status_checks_policy means each merge invalidates the other open
lane PRs — so the S1 → S2 → S3 → T1 merge order is a serial rebase chain. Plan
for that rather than being surprised by it.

Work continuously through the four lanes with a task review after each and a
whole-branch review at the end. Stop only for: a BLOCKED status you cannot
resolve, a review finding that collides with what the plan text mandates (that
is my decision, not yours), or Stage 1 complete at gate G1.
```

---

## 3. What you should get back

Four merged lane PRs into `release/v0.1.0-alpha`, and gate G1 satisfied with:

- All five hand-run privilege-escalation attacks **refused**, each with the log
  line quoted
- `dotnet test` reporting a **non-zero skip count** for the first time, with the
  exact number recorded
- No test soft-skipping by early `return`
- CI green including the new per-assembly coverage floors
- For each security PR, evidence that its negative test was **confirmed failing
  on the parent commit**

Sanity checks before you accept it:

- If a lane reports a fix with no negative test, it is not done.
- If the skip count is still 1, T1 did not do its job — that number should jump
  by roughly 24.
- If someone claims the VM matrix passed, check the trigger: it is
  `workflow_dispatch` only and does not run on push or PR. Stage 4 owns that.
