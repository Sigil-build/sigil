# Orchestration plan — exe-installer track (spec T1–T17)

Status: **COMPLETE (2026-07-09)** — all waves G0–G5 executed on integration
branch `feat/exe-installer-track` (subagent-per-lane worktrees, merged at each
gate). T1–T18 + an install-dir-unify fix all merged; 527 tests green, 0 skipped;
build warning-clean under Native AOT + `TreatWarningsAsErrors`. `pack --format
exe` produces a working stamped `Setup.exe` verified end-to-end (silent install
lands files, real ARP, dual scope, uninstall survives deleting the setup exe).
Size gates: CLI 13.59 MB ≤ 15 MB; host 37.4 MB ≤ 40 MB (re-pinned from the
spec's unattainable 25 MB per the AOT spike). **Merged to `main`** as PR #9
(`b1e21d5`, squash) — verified against the tree 2026-07-13: all 20 lanes
(T1–T18, S1, M0) present with code + tests; `wrapper-vm-tests.yml` carries the
unified both-scope × install/uninstall × deleted-original × double-install
matrix but is manual-dispatch only (run on demand, not per-push).
Coverage note: CI hard-gates 65% union (75.6% actual);
the spec's aspirational Core ≥80% / Signing ≥85% per-assembly bars are unmet
(Core 63%, Signing 69%) — reported, not weakened.

Execution companion to
[`IMPLEMENTATION_SPEC.md`](IMPLEMENTATION_SPEC.md) — the spec defines *what*;
this defines *who runs when*. Runner: **parallel Claude Code sessions in git
worktrees** on a Windows dev machine, with CI (`wrapper-vm-tests.yml`) as the
install/uninstall proof. One branch per task, merge at wave gates, `main` stays
shippable.

---

## 0. Standing rules (paste into every agent prompt — see §4 preamble)

- The spec section for your task is the contract. Read it fully before coding.
  Where this plan and the spec disagree, the spec wins.
- Hard gates: Native AOT + `TreatWarningsAsErrors` (any IL2xxx/IL3xxx fails),
  source-generated serialization only (new types go in a `JsonSerializerContext`),
  deterministic packaging output, `.editorconfig` style.
- Every behavior change lands with tests in the matching `tests/` project.
- Do not touch files owned by another in-flight task (see conflict map, §3).
  If you must, stop and report instead.
- Never rewrite `docs/plan/*` history; append an ADR under `docs/architecture/`
  for any decision the spec leaves open.
- Definition of done = your task's **Acceptance** block in the spec passes
  locally: `dotnet build Sigil.sln -c Release` clean, `dotnet test Sigil.sln -c
  Release` green, plus the task-specific verification in your prompt.

## 1. Environment

- **Dev machine (Windows):** full builds, unit + integration tests, local
  `pack --format exe` smoke runs, headed wizard launches. This is where all
  agents run — the resource writer (`BeginUpdateResourceW`) and AOT publish for
  `win-x64` require Windows.
- **CI:** `wrapper-vm-tests.yml` VM job is the only trusted verdict for
  install/uninstall side effects (registry, PATH, ARP, elevation). Local admin
  runs are for debugging, not sign-off.
- **Worktrees:** from the repo root:
  `git worktree add ../sigil-t5 -b task/t5-payload-extraction`
  One worktree per running agent. Remove after merge
  (`git worktree remove ../sigil-t5`).
- **Branch naming:** `task/t<n>-<slug>` (e.g. `task/t12-scope-elevation`).
- **Merging:** the orchestrator (you) merges to `main` at gates, in the order
  given per wave. Agents never merge; they finish with a pushed branch and a
  summary of files touched + test results.

## 2. Waves and gates

Spec dependency graph, arranged for maximum safe parallelism. ★ = on the
critical path.

| Wave | Lanes (parallel) | Blocked by |
|------|------------------|-----------|
| **0** | ★ **T1** engine split · **S1** Avalonia-AOT spike (T3 decision, ADR only) | — |
| **1** | ★ **T2** host-as-runtime + CLI contract · **M0** manifest/blob surface (below) | T1 |
| **2** | **T3** AOT build wiring · **T5** payload extraction · **T7** branding · **T9** custom screens · **T14** license · **T16a** MSIX ADR (decision only) | T2 (+S1 for T3) |
| **3** | **T4** exe dispatch · **T6** zstd codec · **T8** options screen · ★ **T12** scope + elevation · **T16b** MSIX implementation | T3→T4, T5→T6, T9→T8*, T5→T12, T7+T16a→T16b |
| **4** | **T13** destination screen · **T15** uninstall.exe · **T10** ARP fields · **T11** trust line | T12→T13/T15, T12+T15→T10, T4→T11 |
| **5** | ★ **T17** verification sweep (single lane) | everything |

\* T8 is spec-parallel with T9, but both rework the wizard flow/rail; running
T8 after T9's merge avoids the messiest rebase (see §3).

**M0 — manifest/blob surface (new, not in the spec):** one agent lands *all*
new Core records + schema + blob DTOs in a single pass — `InstallerOptions`,
`InstallerScreen`/`ScreenField`, `License`, `Scope`, `InstallDir` on
`InstallerSection`; matching `schemas/sigil-schema.json` entries; matching
`SerializableWrapperBlob` fields + `JsonSerializerContext` registrations
(including brand-token/asset and ARP fields for T7/T10). Parser validation and
consumption stay with the feature tasks. This exists purely to serialize the
schema/blob edits that T7/T8/T9/T10/T14 would otherwise all fight over.

**Gates** (orchestrator checklist before opening the next wave):

- **G0:** T1 merged; all pre-existing tests green; S1 ADR committed
  (`docs/architecture/adr-avalonia-aot.md`) with a go/fallback decision.
- **G1:** T2 + M0 merged. Manual check: stamped fixture exe shows wizard;
  `/silent` exits 0; `/S` alias works; forced failure → Failed screen +
  rollback. CI green.
- **G2:** wave-2 branches merged **in order: T5 → T3 → T7 → T9 → T14** (T16a is
  docs-only, any time). Manual check: `pack --format exe` on the reference
  manifest produces a themed wizard whose declared screens render.
- **G3:** merge **T4 → T6 → T12 → T8 → T16b**. Manual check: `/silent` install
  lands payload files; `/Pchannel=beta` overrides; user-scope install writes
  no HKLM; `/allusers` elevates. CI VM job green.
- **G4:** merge **T15 → T10 → T13 → T11**. Manual check: delete the original
  setup exe → ARP uninstall still works; ARP shows real name/version/publisher;
  destination screen relocates the install; signed fixture shows trust line.
- **G5:** T17 done — full CI green including both-scope VM matrix, headed smoke
  test, size gates. Tag the milestone.

If a lane fails its gate, hold **only** dependent lanes; independent lanes of
the next wave may start (e.g. T5 failing does not hold T7/T9/T14).

## 3. Conflict map (file ownership while in flight)

| Hotspot | Owner | Everyone else |
|---------|-------|----------------|
| `Wrapper.Core` project layout, moved namespaces | T1 | rebase after G0, never before |
| `Installer.Host/Program.cs`, entry/exit codes, `CommandLineParser` | T2 | T12 extends after G1; others read-only |
| `Manifest/InstallerSection.cs`, `sigil-schema.json`, `SerializableWrapperBlob` + JSON context | M0 | feature tasks *consume*, adding only parser/validation logic in their own files |
| `InstallerViewModel`, `InstallerWindow.axaml`, rail/flow | T9 (wave 2) → T8 → T13/T14 (sequential merges) | coordinate via gate order |
| `ExeWrapperPackager` | T5 (wave 2) → T6 → T15 hook | T3 touches only `WrapperRuntimeLocator` |
| `ArpRegistration`, journal/state stores | T12 | T10/T15 extend after T12 merges |
| `wrapper-vm-tests.yml` | each task appends its own job/step; T17 consolidates | append-only, never reorder others' steps |

## 4. Agent prompts

Common preamble — prepend to every prompt below:

```text
You are implementing one task of Sigil's exe-installer track in a dedicated
git worktree on branch task/<id>. Read docs/plan/IMPLEMENTATION_SPEC.md
section 0 (constraints) and your task's section in full, plus the files listed
under READ. The spec's Acceptance block is your definition of done.
Rules: Native AOT-safe code only (no reflection; source-gen serialization;
any IL2xxx/IL3xxx warning is a build failure), TreatWarningsAsErrors, match
.editorconfig, deterministic packaging, tests for every behavior change.
Touch only the files in SCOPE; if you believe you must edit something else,
stop and report why instead of editing it. Do not merge; finish by pushing
the branch and summarizing files touched, tests added, and test results.
Verify before finishing: dotnet build Sigil.sln -c Release &&
dotnet test Sigil.sln -c Release, plus the VERIFY commands.
```

### S1 — Avalonia AOT spike (wave 0)

```text
TASK: Spike, not production code. Decide spec T3's risk item: does
src/SigilBuild.Installer.Host publish clean under Native AOT?
READ: spec T3 + §4 Risks; src/SigilBuild.Installer.Host/*.csproj;
Directory.Build.props.
DO: In a scratch branch, run dotnet publish src/SigilBuild.Installer.Host
-c Release -r win-x64 -p:PublishAot=true. Catalog every trim/AOT warning and
whether rd.xml/TrimmerRootDescriptor suppressions are honest fixes. Measure
exe size and cold-start. If blocked, evaluate the spec's two fallbacks
(self-contained non-AOT publish; AOT console shim + child process) with
sizes. DELIVER: docs/architecture/adr-avalonia-aot.md recommending one path
with numbers; no src/ changes.
VERIFY: the ADR contains reproducible commands + measured sizes.
```

### T1 — engine split (wave 0)

```text
TASK: Spec T1. Extract SigilBuild.Wrapper's engine into a new
SigilBuild.Wrapper.Core classlib.
READ: spec T1; src/SigilBuild.Wrapper/** (Engine/, Steps/, Expressions/,
Json/, Cli/); Sigil.sln; Directory.Build.props.
SCOPE: new src/SigilBuild.Wrapper.Core/; src/SigilBuild.Wrapper/ (thins to
console shell); Sigil.sln; ProjectReferences in SigilBuild.Packaging and test
projects; InternalsVisibleTo attributes.
NOTE: pure mechanical move — no behavior change, keep SigilBuild.Wrapper.*
namespaces. All existing wrapper tests must pass UNCHANGED (edits to test
csproj references only).
VERIFY: dotnet test — zero test-code diffs outside csproj files.
```

### T2 — host as runtime + command-line contract (wave 1)

```text
TASK: Spec T2, including the full command-line contract block. Installer.Host
becomes the stamped runtime driving the real engine; delete the copy-loop
InstallerEngine.
READ: spec T2 + decision 1; src/SigilBuild.Installer.Host/** (Program.cs,
Services/InstallerEngine.cs, ViewModels/InstallerViewModel.cs);
src/SigilBuild.Wrapper/Program.cs (ARP/journal logic to lift);
Wrapper.Core: InstallEngine, UninstallEngine, CommandLineParser, WrapperBlob.
SCOPE: Installer.Host/**; Wrapper.Core/Cli/CommandLineParser.cs (new flags:
/S alias, /Pname=value, /D=, /allusers, /currentuser, exit codes 0/1/2/64);
shared install-completion helper used by both entries; delete
Services/InstallerEngine.cs + its tests, migrating live assertions.
OUT: actual scope/elevation behavior (T12 — parse flags, store, don't act);
payload extraction (T5); update mode = exit 64 "not supported".
VERIFY: launch host exe with no args → wizard; /silent → headless exit 0;
/S alias parses; unknown /Pfoo → 64; forced step failure → Failed screen +
journal rollback; Cancel mid-install → rollback, exit 2.
```

### M0 — manifest/blob surface (wave 1)

```text
TASK: Land the complete new manifest + blob data surface in one pass, so
wave-2/3 features don't collide on shared files. Records + schema + DTOs +
serializer registration ONLY — no parser validation, no UI, no engine logic.
READ: spec T7/T8/T9/T10/T12/T13/T14 "Do" blocks (data shapes only);
src/SigilBuild.Core/Manifest/InstallerSection.cs, ParameterDefinition.cs;
schemas/sigil-schema.json; Wrapper.Core Json/Serializable*.
SCOPE: InstallerSection gains Options (InstallerOptions), Screens
(InstallerScreen/ScreenField), License, Scope, InstallDir; delete
GradientStart/Mid/End end-to-end; schema entries for all of the above;
SerializableWrapperBlob gains brand tokens + base64 logo/hero, license text,
DisplayName/Version/Publisher/EstimatedSize, scope, declared screens +
parameter defs; register everything in the JsonSerializerContext.
VERIFY: round-trip serialization tests for every new blob field; schema
validates the spec §3 reference manifest; solution builds warning-free.
```

### T3 — AOT build wiring (wave 2)

```text
TASK: Spec T3, implementing whichever path adr-avalonia-aot.md chose.
READ: spec T3; the ADR; src/SigilBuild.Packaging/ExeWrapper/
WrapperRuntimeLocator.cs; .github/workflows/*.
SCOPE: MSBuild target or scripts/ step publishing Installer.Host for win-x64
AND win-arm64 into runtimes/<rid>/SigilBuild.Installer.Host.exe;
WrapperRuntimeLocator takes the target architecture; CI size gate for the
host exe (measure, then pin ≤ target from the ADR).
VERIFY: clean-clone Release build produces both runtime exes;
WrapperRuntimeLocator.Locate(arch) resolves each; CI job fails if oversize.
```

### T5 — payload extraction (wave 2)

```text
TASK: Spec T5. Extract the embedded payload and resolve payload:// sources.
READ: spec T5; Wrapper.Core: WrapperBlob.LoadPayloadBytes, StepContext,
Steps/FileCopyStep.cs, RollbackJournal; ExeWrapperPackager.BuildPayloadBytes
(container format).
SCOPE: extraction to %TEMP%\sigil-<appid>-<rand>\ at install start (both
entries); StepContext payload root; payload:// resolution in path-taking
steps; temp cleanup on success, rollback, and cancel.
VERIFY: integration test — pack fixture with file_copy from
payload://app/app.exe, run /silent, assert file lands + temp dir gone;
rollback path also cleans temp.
```

### T7 — branding pipeline (wave 2)

```text
TASK: Spec T7 + decision 11. Two-color derived palette, embedded in the blob.
READ: spec T7; docs/plan/prototype/sigil-installer-wizard-prototype.html —
port its colors() constants EXACTLY (both modes, including frame);
SigilBuild.Packaging/Installer/BrandTokenEmitter.cs;
SigilBuild.Installer.BrandGenerator/WcagContrast;
Installer.Host/Branding/{BrandTokens.cs, BrandPalette.axaml}.
SCOPE: SrgbMix helper + full light/dark token derivation in
BrandTokenEmitter; write tokens + base64 logo/hero into the M0 blob fields;
host reads brand exclusively from the blob; purge gradient code paths
(records already deleted by M0); WCAG check extended to derived railMuted.
OUT: InstallerHostBundler's sidecar for MSIX (T16 decides).
VERIFY: golden-file test — known primary/accent → exact token maps matching
the prototype's constants; stamped fixture exe renders logo + palette with
no loose files beside it; light and dark both themed.
```

### T9 — declared custom screens (wave 2)

```text
TASK: Spec T9 including secret hygiene. Manifest-declared parameter forms.
READ: spec T9 + decision 6; M0's InstallerScreen/ScreenField records;
Core parser + DiagnosticCodes; Installer.Host Views/Screens/CustomView,
ViewModels; Wrapper.Core Expressions (when engine), UninstallStateStore.
SCOPE: parser validation (unknown param ref → diagnostic; title token +
When validation); widget inference table + factory in the host; inline
pattern/min/max/enum validation before advancing; param.* binding into the
engine; rail generated from resolved screen set, runtime When skipping;
secret redaction in logs/journal/state + the grep test.
VERIFY: spec §3 reference manifest reproduces the prototype Configure screen
(masked secret, radio channel, checkbox); param.autostart gates a step;
secret value absent from journal + log output.
```

### T14 — license screen (wave 2)

```text
TASK: Spec T14. installer.license file → embedded text → License screen.
READ: spec T14; M0's License field; Installer.Host Views/Screens/LicenseView;
pack-time blob assembly in ExeWrapperPackager.
SCOPE: pack-time file read + embed (diagnostic if missing/empty); screen
appears iff text present; accept-checkbox gates Next; /silent implies
acceptance (document in CLI help).
VERIFY: fixture with LICENSE.txt shows the screen with the text; without it
the screen and rail entry vanish.
```

### T16a — MSIX reconciliation ADR (wave 2, docs only)

```text
TASK: Spec T16 decision only. The Avalonia host T2 repurposed is also the
MSIX companion bundled by InstallerHostBundler.
READ: spec T16; SigilBuild.Packaging/Installer/InstallerHostBundler.cs;
Msix/* packagers; T7's blob-embedded token decision.
DELIVER: docs/architecture/adr-msix-companion.md choosing (a) engine-driven
host in MSIX too, or (b) separate minimal companion — with the
BrandTokens.g.json sidecar's fate. No code.
```

### T4 — exe dispatch (wave 3)

```text
TASK: Spec T4. Turn on pack --format exe.
READ: spec T4; PackCommand.cs; Core ManifestParser.ParseFormat; schema
formats enum; WrapperResourceWriter (Windows-only note).
SCOPE: dispatch arm; ParseFormat accepts "exe"; schema enum; clear
non-Windows-host diagnostic; enable the integration test.
VERIFY: sigil pack fixture --format exe emits <App>-<ver>-<arch>-Setup.exe
per declared architecture; it launches the wizard.
```

### T6 — zstd codec (wave 3)

```text
TASK: Spec T6. Deflate → zstd, deterministic, AOT-clean.
READ: spec T6 + §5 out-of-scope (codec must stay reusable by the future
update engine); ExeWrapperPackager.BuildPayloadBytes; T5's extraction side.
SCOPE: codec behind the packager interface; SIGIL_PAYLOAD_V2 marker + reader
gate; native lib bundling for win-x64 + win-arm64 AOT publish.
VERIFY: two packs of the same input are byte-identical; T5's tests pass on
V2; AOT publish stays warning-free.
```

### T8 — options screen (wave 3, after T9 merges)

```text
TASK: Spec T8. Built-in configurable components + pack-time step generation.
READ: spec T8 + decision 5; M0's InstallerOptions; T9's merged rail/flow
code (rebase on it); Wrapper.Core step records.
SCOPE: pack-time component→step generation gated on option.* (table in
spec); option.* in the expression engine; Options screen with locked-state
rendering; screen hidden when no components enabled.
VERIFY: spec §3 manifest → checkboxes render, toggling desktop_shortcut off
skips its ShortcutCreate; start_menu: false removes it entirely; locked row
renders disabled; option.* usable in a step when.
```

### T12 — scope + elevation (wave 3) ★ critical

```text
TASK: Spec T12 + decision 9. Dual scope with self-elevation.
READ: spec T12 in full; T2's merged flag parsing; ArpRegistration,
UninstallStateStore, EnvSetStep, ShortcutCreateStep; app.manifest handling
in Installer.Host.csproj.
SCOPE: asInvoker manifest; ShellExecuteW runas relaunch (forward ALL args,
propagate child exit code); scope resolution rules (fixed manifest scope vs
/allusers//currentuser → 64 on conflict; auto defaults user); per-scope
install root, ARP hive, PATH target, shortcut folders, journal location;
scope recorded in state; scope exposed to expressions.
VERIFY: user-scope /silent install as non-admin succeeds, zero HKLM writes
(assert via test); /allusers triggers elevation and lands in HKLM/Program
Files; VM workflow gains a both-scopes matrix entry.
```

### T16b — MSIX implementation (wave 3)

```text
TASK: Implement adr-msix-companion.md. Keep MSIX packaging green after
T2/T7 changed the host and killed the token sidecar.
READ: the ADR; InstallerHostBundler; Msix packager tests.
SCOPE: per the ADR; remove orphaned copy-loop remnants and the
BrandTokens.g.json sidecar path (or rewire it per the ADR).
VERIFY: all MSIX packaging tests green; no references to the deleted
Services/InstallerEngine remain anywhere.
```

### T13 — destination screen (wave 4)

```text
TASK: Spec T13. Destination screen + {install_dir} contract.
READ: spec T13; T12's merged scope roots; design brief Destination prompt
(docs/plan/claude-design-wizard-brief.md) for the layout incl. scope radios;
T9's flow/rail code.
SCOPE: default <scope root>\<App.Name>; installer.install_dir override
resolution; screen after welcome (path + browse + scope toggle when auto +
inline invalid-path state); /D= override both modes; {install_dir}
resolution pinned in steps/expressions + tests.
VERIFY: default reflects scope; wizard edit and /D= both relocate the
install; invalid path blocks Next inline.
```

### T15 — uninstall survivability (wave 4)

```text
TASK: Spec T15 + decision 8. uninstall.exe copy + interactive uninstall.
READ: spec T15 in full (self-deletion approach); ArpRegistration.
BuildUninstallString; UninstallEngine, RollbackJournal; T12's scope state.
SCOPE: journaled final step copying self to {install_dir}\uninstall.exe;
UninstallString → "{install_dir}\uninstall.exe" /S /Uninstall; interactive
confirm→progress→done flow (design brief has the screens); self-deletion via
%TEMP% relaunch with MoveFileExW reboot fallback; journal tolerates its own
entry; scope honored on uninstall.
VERIFY: VM test — install, DELETE the original setup exe, uninstall from ARP:
files/registry/PATH/shortcuts reversed and uninstall.exe gone (or
reboot-scheduled); interactive run shows confirm flow.
```

### T10 — ARP fields + reinstall (wave 4)

```text
TASK: Spec T10. Real ARP values, scope-correct hive, idempotent reinstall.
READ: spec T10; M0's blob fields; T12's parameterized ArpRegistration;
T15's UninstallString.
SCOPE: thread App.* + packed size into the blob at pack time; fix the
placeholder Register call; existing-install detection → wizard
repair/reinstall (uninstall-then-install ok) and idempotent /silent.
VERIFY: VM asserts real name/version/publisher in the right hive; double
/silent install → no duplicate PATH entries/shortcuts/ARP rows.
```

### T11 — trust line (wave 4)

```text
TASK: Spec T11 + decision 7. Verified-signature gating via WinVerifyTrust.
READ: spec T11 in full (SignDeclared flag, pack→sign ordering);
SigilBuild.Signing/*; Wrapper.Core P/Invoke patterns ([LibraryImport]).
SCOPE: SignDeclared in the blob; WinVerifyTrust self-check in the host;
trust line = SignDeclared && valid; neutral publisher line otherwise;
pack/sign command help documents stamp-then-sign ordering.
VERIFY: three fixtures — signed (trust line), unsigned (none),
signed-then-tampered (none). All three in tests.
```

### T17 — verification sweep (wave 5, single lane)

```text
TASK: Spec T17. Close every gate.
READ: spec T17; all VM workflow jobs added by T5/T12/T15/T10.
SCOPE: un-skip Roundtrip_blob_via_resource_apis; consolidate the VM matrix
(both scopes × install/uninstall × deleted-original × double-install);
headed wizard smoke test welcome→done on the real engine; confirm size and
coverage gates.
VERIFY: full CI green, zero skipped installer tests. Report final exe sizes
vs gates.
```

## 5. Orchestrator loop

1. Open wave lanes: create worktree + branch, paste preamble + task prompt
   into a fresh Claude Code session per lane.
2. On each "done" report: pull the branch, run build + tests yourself, review
   the diff against the task's SCOPE (files outside scope = send back), check
   the spec Acceptance block line by line.
3. At the gate: merge in the stated order, rebasing later branches; run the
   gate's manual checks; push; wait for CI including the VM job.
4. Anything red: reopen the owning lane with the failure attached — do not fix
   forward in main.
5. After G5: tag, update `docs/plan/exe-installer-and-wizard.md` status line,
   and archive this plan's checklist below.

## 6. Progress checklist

Verified against `main` (`b1e21d5`, PR #9) on 2026-07-13. Per-task branches
were squashed into the PR, so branch provenance is historical; ☑ Merged =
code + tests confirmed present on `main`.

| Task | Branch | Agent started | Pushed | Merged | Gate | Evidence (verified 2026-07-13) |
|------|--------|:---:|:---:|:---:|------|--------------------------------|
| T1   | task/t1-engine-split | ☑ | ☑ | ☑ | G0 | `src/SigilBuild.Wrapper.Core/`; Wrapper thinned to 43-line shell |
| S1   | spike/avalonia-aot | ☑ | ☑ | ☑ | G0 | `docs/architecture/adr-avalonia-aot.md` (Accepted, with numbers) |
| T2   | task/t2-host-runtime | ☑ | ☑ | ☑ | G1 | `HostRuntime.cs`; `Services/InstallerEngine.cs` deleted; flags + exit 0/1/2/64 in `CommandLineParser` |
| M0   | task/m0-manifest-surface | ☑ | ☑ | ☑ | G1 | `InstallerSection` Options/Screens/License/Scope/InstallDir; gradients purged; schema + blob + JSON context |
| T3   | task/t3-aot-wiring | ☑ | ☑ | ☑ | G2 | `scripts/publish-installer-runtime.ps1` (x64+arm64, size gate); `WrapperRuntimeLocator.Locate(arch)` |
| T5   | task/t5-payload-extraction | ☑ | ☑ | ☑ | G2 | `Engine/PayloadExtraction.cs`; `payload://` in `StepContext.ResolvePath`; 4 test files |
| T7   | task/t7-branding | ☑ | ☑ | ☑ | G2 | `BrandTokenEmitter.SrgbMix/Derive`; blob-only brand; golden tests |
| T9   | task/t9-custom-screens | ☑ | ☑ | ☑ | G2 | `ParseScreens` diagnostics; widget inference; secret redaction + `SecretHygieneTests` |
| T14  | task/t14-license | ☑ | ☑ | ☑ | G2 | `LicenseText` blob field; screen iff text; `/silent` implies accept |
| T16a | task/t16a-msix-adr | ☑ | ☑ | ☑ | G2 | `docs/architecture/adr-msix-companion.md` (option b) |
| T4   | task/t4-exe-dispatch | ☑ | ☑ | ☑ | G3 | `PackCommand` exe arm; `<App>-<ver>-<arch>-Setup.exe`; schema enum |
| T6   | task/t6-zstd | ☑ | ☑ | ☑ | G3 | `Codec/PayloadCodec.cs` SIGIL_PAYLOAD_V2; determinism tests |
| T8   | task/t8-options | ☑ | ☑ | ☑ | G3 | `OptionStepGenerator` (4 components); `option.*` in expressions |
| T12  | task/t12-scope-elevation | ☑ | ☑ | ☑ | G3 | `ScopeResolver`/`ScopeLayout`/`Elevation`; asInvoker manifest; VM both-scope matrix |
| T16b | task/t16b-msix-impl | ☑ | ☑ | ☑ | G3 | `InstallerHostBundler` removed; zero `InstallerEngine` refs; MSIX tests green |
| T13  | task/t13-destination | ☑ | ☑ | ☑ | G4 | `InstallDirResolver` precedence wizard→/D=→manifest→default; inline validation |
| T15  | task/t15-uninstall-exe | ☑ | ☑ | ☑ | G4 | `InstallSurvivability.cs` + `SelfDelete.cs` (MoveFileExW fallback); VM survive leg |
| T10  | task/t10-arp-fields | ☑ | ☑ | ☑ | G4 | real ARP values, scope hive; `ReinstallIdempotencyTests` |
| T11  | task/t11-trust-line | ☑ | ☑ | ☑ | G4 | `AuthenticodeVerifier` (WinVerifyTrust); 3-fixture `TrustLineGatingTests` |
| T17  | task/t17-verification | ☑ | ☑ | ☑ | G5 | roundtrip test un-skipped; unified VM matrix; headed smoke; size gates |
| T18  | (added in-flight) | ☑ | ☑ | ☑ | G5 | native-runtime bundling: `NativeRuntimeBootstrap.cs`, SIGIL_RUNTIME_V1 resource |

Residual items (documented deviations, not regressions): per-assembly coverage
bars unmet (Core 63% vs 80%, Signing 69% vs 85%); VM matrix is
workflow-dispatch only; ADR-008 is cited by `Functions.cs` and the
localization deferral but no `adr-008-*.md` file exists — authored as part of
the feature-parity track (`docs/plan/feature-parity/`).
