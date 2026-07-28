# Release audit — kickoff prompt for Claude Code

Purpose: this file is a **paste-ready prompt**. Open a fresh Claude Code session
at the repo root and paste everything inside the fenced block in §2. §1 is
guidance for the human running it; §3 explains what comes back.

---

## 1. How to run it

**Model: `claude-opus-5`.** This is a cross-cutting security + architecture
audit over ~570 tracked files and two completed feature tracks, ending in a
plan. It rewards deep multi-file reasoning and adversarial thinking about an
installer that elevates to admin — use the strongest model. Cost is a rounding
error against one missed privilege-escalation bug in a v1.

For what comes *after* this audit: run the resulting fix lanes on
`claude-sonnet-5` (mechanical, well-specified work — CHANGELOG, notices, doc
rewrites, ACL hardening against an explicit spec), reserving `claude-opus-5`
for the security-fix lane (R1 below) and the final go/no-go re-review. Trivial
lanes (file moves, gitignore, stale-branch cleanup) are fine on
`claude-haiku-4-5-20251001`.

Environment: **run on the Windows dev machine.** The audit must build and test;
`BeginUpdateResourceW`, the AOT publish, and every install-side check are
Windows-only. Give the session permission to run `dotnet build` / `dotnet test`
and `git` reads. It must not push, merge, or change code.

Expect it to take a while and to spawn subagents. That's intended — tell it to.

---

## 2. The prompt

```text
You are auditing the Sigil repository (C:\projects\Sigil) for FIRST-RELEASE
readiness. Sigil is a .NET 10, Native-AOT CLI that builds Windows installers —
an NSIS / Inno Setup / WiX replacement. Its output is a stamped Setup.exe that
runs a branded Avalonia wizard, elevates to admin, writes to HKLM and Program
Files, mutates PATH, and installs a self-deleting uninstaller. Threat model
accordingly: this is privileged software that end users download and run.

This is an ANALYSIS AND PLANNING task. Do not fix anything. Do not modify any
file except the three deliverables named at the end. Do not commit, push, or
merge. If you find something so dangerous it should not wait, say so loudly in
your report — still don't fix it.

## Where the project stands

Two full tracks have shipped to main (HEAD 1be494c, remote
https://github.com/Sigil-build/sigil.git):

- The exe-installer track, T1-T18, specced in docs/plan/IMPLEMENTATION_SPEC.md
  and executed per docs/plan/ORCHESTRATION_PLAN.md.
- A feature-parity track, P0-P13, specced in
  docs/plan/feature-parity/01-IMPLEMENTATION_PLAN.md against the gap inventory
  in docs/plan/feature-parity/00-GAP_ANALYSIS.md. This added: variables/data
  retrieval, install logging, lifecycle hooks, http_download, config-file
  steps, upgrade semantics, prerequisites, files-in-use (Restart Manager),
  wizard localization, custom components, scheduled-task/COM/firewall steps,
  and an update engine + web installer.

Both plan docs describe themselves as COMPLETE. Treat that as a claim to
verify, not a fact. Note in particular that ORCHESTRATION_PLAN.md's status
block describes a 527-test snapshot from PR #9 and does not mention the P-track
at all — the tree has outgrown its own plan docs.

## What a prior review already established (verify, do not re-derive)

A prior session reviewed the T-track code and the release surface. Its findings
are below. Spot-check each one against the tree — flag anything now stale — but
spend your effort on what it did NOT cover (next section).

CONFIRMED BLOCKER — B1: elevated uninstall replays an unauthenticated,
user-writable journal. In
src/SigilBuild.Wrapper.Core/Engine/UninstallStateStore.cs, TryLoad(appId,
preferredScope) searches the preferred scope's directory and then THE OPPOSITE
SCOPE's (line ~136), returning the first uninstall.json it finds and taking the
authoritative scope from a field inside that file (line ~174). Machine-scope
state lives under %ProgramData%\Sigil\<AppId>\ created with a bare
Directory.CreateDirectory — no ACL hardening anywhere in src/. Journal records
carry absolute paths and full registry coordinates with no anchoring to
install_dir (RollbackJournal.cs RestoreFile / RemoveDirectory /
RestoreRegistryValue). Net: a local user plants a journal in their own
%LocalAppData%, an admin runs uninstall.exe /allusers, and the elevated child
replays attacker-chosen file deletions and HKLM writes.

Should-fix, previously found:
- .sigil-bak backups from FileCopyStep are not discarded on the success path
  (RollbackJournal.DiscardTransientStashes skips RestoreFile), leaving stale
  copies of replaced files in Program Files after a reinstall.
- Secret redaction is substring-replace only; run_program passes
  ParameterType.Secret values via ProcessStartInfo.ArgumentList (visible in the
  child's command line), and RunProgramStep failure messages embed raw child
  stdout/stderr that reaches console/stderr without ctx.Redact.
- AuthenticodeVerifier uses WTD_REVOKE_NONE, so a revoked publisher cert still
  renders the "Signed by ..." trust line.

Verified sound, do not spend time re-auditing unless something looks off:
zip-slip containment in PayloadExtraction.ResolveEntryPath and the payload://
resolver; the CommandLineToArgvW quoting in Elevation.BuildCommandLine and its
exit-code propagation; ChannelManifestVerifier's fail-closed, P-256-pinned
signature check; HttpDownloadStep's https:// enforcement and SHA-256
verification; WrapperBlob resource parsing failing safe on tampered input.

Release scaffolding gaps confirmed on disk: no CHANGELOG.md; no SECURITY.md; no
THIRD-PARTY-NOTICES despite bundling Avalonia, ZstdSharp and YamlDotNet; no
tag-triggered release workflow and no artifact publication of any kind; version
0.0.1-alpha hardcoded and duplicated in src/SigilBuild.Cli/SigilBuild.Cli.csproj
and src/SigilBuild.Cli/Program.cs (with a test and a CI smoke asserting the
literal); two local tags exist (exe-installer-v1, pre-merge-backup-p9), neither
a release tag; 16 stale task/p* branches on the remote.

Docs drift confirmed: README.md never mentions the exe wizard at all;
docs/cli-reference.md documents zero Setup.exe runtime flags (/silent, /S,
/allusers, /currentuser, /D=, /P<name>=<value>); docs/getting-started.md calls
the outputs setup.exe and uninstaller.exe while the code emits
<App>-<ver>-<arch>-Setup.exe and uninstall.exe. Two ADR directories exist with
colliding numbers — docs/architecture/adr-009,010 and
sigil-docs/architecture/adr-009,010 on different subjects — and CODEOWNERS
points at sigil-docs/architecture/ plus a sigil-docs/decisions.md that does not
exist.

## What has NOT been reviewed — this is your main job

1. THE ENTIRE P0-P13 SURFACE HAS HAD NO SECURITY REVIEW. Everything the prior
   pass examined was T-track. Audit the P-track code with the same adversarial
   lens, at minimum:
   - http_download / SigilDownloader / web-installer: TLS, redirect handling
     (does it follow http:// redirects from an https:// origin?), size limits,
     disk-exhaustion, temp file handling, checksum enforcement on every path.
   - The update engine: manifest fetch, signature verification coverage (is
     EVERY consumed field inside the signed payload?), rollback/downgrade
     protection, where update state is stored and who can write it.
   - System steps — scheduled_task_create, com_register, firewall_rule: these
     are the highest-privilege operations in the product. Command construction,
     argument injection from manifest/parameter values, what happens on
     uninstall, and whether any of them can be driven to a privileged action
     the publisher did not intend.
   - config-file steps: path containment, whether writes can escape install_dir.
   - prerequisites: what it downloads and executes, and under what verification.
   - files-in-use / Restart Manager and the setup single-instance mutex: mutex
     naming (Global\ vs Local\, squatting/DoS by an unprivileged user).
   - localization: manifest-supplied translations rendered in the wizard —
     any injection or format-string surface.
   - Lifecycle hooks and run-after-install: what runs, as whom, with what
     arguments, and at what integrity level after an elevated install.
2. Cross-track interactions the per-lane agents could not see: the journal and
   state stores now serve T-track uninstall AND P-track upgrade/update — reason
   about that combined trust model, including B1's blast radius across upgrade.
3. Whether the two shipped tracks actually satisfy their own specs' Acceptance
   blocks. Sample rather than exhaust: pick the highest-risk acceptance criteria
   and check them against code and tests.
4. Test integrity, not just test count. Are the assertions meaningful, or do
   tests assert that a method was called? Specifically check that VM-only tests
   soft-skip loudly rather than silently passing when SIGIL_VM_TESTS is unset —
   a silent skip on a per-push runner is a false green.
5. CI honesty. ci.yml enforces a 65% union coverage gate defined inline as a
   Python heredoc; per-assembly targets (Core 80%, Signing 85% per CLAUDE.md)
   are printed but not enforced, and actuals are below them. Every real
   install/uninstall/scope/upgrade/update proof lives in wrapper-vm-tests.yml,
   which is workflow_dispatch only. Assess what per-push CI actually proves,
   and what a first release would be shipping unproven.
6. Public-repo readiness: _agent-setup/ is tracked (agent tooling in a public
   repo — intended?); .superpowers/ is excluded only by a nested .gitignore
   inside itself, not repo policy; .gitignore contains a malformed "./docs/"
   entry; docs/sprint-01/identifier-reservation.md still lists the NuGet ID as
   an unresolved placeholder. Decide what must change before the repo is
   presentable, and flag anything embarrassing or confusing to a first
   external reader.
7. Supply chain: pin state of Directory.Packages.props, any package with a
   known-vulnerable version, and whether ZstdSharp.Port (pure-managed, chosen
   over a native libzstd) is correctly reflected in the spec text that still
   describes bundling a native lib.

## How to work

- Verify before you assert. Every finding cites file:line or a command you ran.
  If you cannot verify something, label it UNVERIFIED and say what you would
  need. Do not launder a plan doc's claim into a fact.
- Run the build and the suite: `dotnet build Sigil.slnx -c Release` and
  `dotnet test Sigil.slnx -c Release`. Report the real numbers — test count,
  pass/fail, skips (there should be exactly one documented skip in
  ComRegisterInstallTests), warnings. If anything is red or the numbers differ
  from the plan docs' claims, that is a finding.
- You may run wrapper-vm-tests.yml locally only if you can do it without
  damaging the machine; otherwise say it is unrun and treat that as a gap.
- Use subagents for breadth (one per P-lane area is a reasonable split), but
  synthesize yourself — do not paste subagent output into the deliverables.
- Severity language, used consistently:
  RELEASE BLOCKER (ship this and you have a CVE or a broken promise),
  SHOULD-FIX (fix in the release, not after),
  POST-v1 (real, but a known limitation is honest),
  NOTE (informational).
  Be willing to conclude that something is fine. A short honest register beats
  a padded one.
- Judge against a first PUBLIC release of privileged software by an unknown
  publisher. "It's only v0.1" is not a defense for a privilege-escalation path;
  it IS a fine reason to defer, say, delta updates.

## Deliverables — exactly three new files, nothing else

1. docs/plan/release/00-GAP_REGISTER.md
   Every finding as a numbered row: ID (R1, R2, ...), title, severity,
   component, evidence (file:line or command output), why it matters,
   recommended fix in 1-3 sentences, and rough effort (S/M/L). Sort by
   severity. Include the prior findings above, re-verified, so this file is the
   single source of truth. Include a short "verified sound" section so the next
   reader knows what was checked and cleared.

2. docs/plan/release/01-FIX_PLAN.md
   The execution plan, in the same style as docs/plan/ORCHESTRATION_PLAN.md
   (read it first and match its shape): waves, parallel lanes, explicit
   dependencies, a file-ownership conflict map, gates with concrete manual
   checks, and a paste-ready agent prompt per lane with READ / SCOPE / OUT /
   VERIFY sections and a common preamble. Recommend a model per lane
   (claude-opus-5 for security-critical or ambiguous work, claude-sonnet-5 for
   specified mechanical work, claude-haiku-4-5-20251001 for trivia) with a
   one-line reason. Sequence so main stays shippable and the security fix lands
   first. Every lane must state its test obligation.

3. docs/plan/release/02-READINESS_REPORT.md
   The decision document, for a reader who will not read the other two. Lead
   with a one-paragraph verdict: is this releasable, and if not, what is the
   shortest honest path. Then: what actually works (be specific and fair — the
   engineering here is largely strong); the blocker list with one line each;
   what ships unproven and what that risks; a "known limitations" draft
   suitable for lifting into release notes; the recommended release shape
   (v0.1.0-alpha vs 0.1.0 vs pre-announcement private tag) with reasoning; and
   a definition of done for v1 as a checklist. Include the real test/build
   numbers you measured. State plainly anything you could not verify.

Write for a reader who is technical, busy, and has to decide. No filler, no
restating the question, no praise. If the answer is "not ready and here is the
two-week path," say exactly that.
```

---

## 3. What you should get back

Three files under `docs/plan/release/`. The register is the source of truth, the
fix plan is what you hand to lane agents, the readiness report is what you read
yourself and what a release announcement gets built from.

Sanity checks on the output before you act on it:

- R1 should be the journal/scope-fallback blocker. If the audit downgrades it,
  make it justify that in writing.
- The P-track section should be substantial. If it comes back thin, the audit
  under-invested in the only large unreviewed surface and should be re-run on
  that scope alone.
- Every blocker must cite `file:line`. Uncited severity is a guess.
- The report must state the measured test numbers and admit whatever it could
  not run (the VM matrix, most likely).
