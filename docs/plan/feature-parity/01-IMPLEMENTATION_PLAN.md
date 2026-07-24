# Implementation plan — feature-parity track (P0–P13)

Companion to [`00-GAP_ANALYSIS.md`](00-GAP_ANALYSIS.md). Same execution model
as `docs/plan/ORCHESTRATION_PLAN.md` §0–§1 (standing rules, worktrees, gates,
CI VM verdict) — those rules apply verbatim; this file only defines the lanes.
Branch naming: `task/p<n>-<slug>`.

> **Status (2026-07-24).** Waves 0–6 are **merged**: P0–P13 all landed on
> `main` (see the progress checklist) — the feature-parity track is closed.
> P11 (PR #13, `f61bfbe`) and P12 (PR #14, `110b7d5`) shipped the last
> product behavior (system steps + the update engine/web installer); P13
> (this branch) is the docs-only verification sweep that reconciles this
> plan and `00-GAP_ANALYSIS.md` against that merged state. The detailed task
> breakdowns for P11–P13 are in the [Remaining
> work](#remaining-work--detailed-breakdown-p11p13) section at the end; the
> lane sketches for P0–P10 above are kept as historical record of what shipped.

Hard constraints carried over: Native AOT + `TreatWarningsAsErrors`,
source-generated serialization only, deterministic packaging, closed
step/function catalog (every addition amends ADR-008), tests with every
behavior change, secret redaction preserved in any new logging/variable
surface.

## Waves

| Wave | Lanes (parallel) | Blocked by |
|------|------------------|-----------|
| **0** | ★ **P0** ADR-008 authoring (docs only) | — |
| **1** | ★ **P1** variables + data-retrieval functions · **P7** install logging | P0 |
| **2** | **P2** lifecycle hooks + run-after-install · **P4** http_download step · **P8** config-file steps | P1 (P4 also P7 for progress logging) |
| **3** | **P3** upgrade semantics · **P5** prerequisites · **P6** files-in-use + setup mutex | P1; P5 also P4 |
| **4** | **P9** localization · **P10** custom components | P2 (wizard flow churn serialized) |
| **5** | **P11** task/COM/firewall steps · **P12** update engine + web installer | P4/P5 merged |
| **6** | ★ **P13** verification sweep | everything |

Gate rule as before: a failing lane holds only its dependents.

---

### P0 — ADR-008: expression & extensibility policy (wave 0, docs only)

- **Problem:** `Functions.cs` forbids new functions "without amending ADR-008",
  but ADR-008 was never written. P1/P4/P9 all need it.
- **Deliver:** `docs/architecture/adr-008-expression-policy.md` codifying: the
  closed function table; criteria for admitting functions (pure, no
  shell/reflection/network at eval time, bounded I/O — registry/file reads
  allowed read-only); variable lifetime/scoping model for P1; the redaction
  contract (any value derived from a secret is secret); localization stance.
- **Verify:** ADR reviewed; `Functions.cs` comment updated to point at the file.

### P1 — variables & data retrieval (wave 1) ★ critical

- **Gap G1.** Declarative equivalent of NSIS `ReadRegStr`/Inno
  `RegQueryStringValue`/WiX `RegistrySearch`.
- **Do:**
  - New expression functions (per ADR-008): `registry_read(hive, key, value)`,
    `env(name)`, `file_version(path)`, `installed_version(app_id)` (reads own
    ARP entry; feeds P3). All return string ("" when absent), AOT-safe, read-only.
  - Manifest `installer.vars`: named computed values evaluated once at session
    start, e.g. `vars: { old_path: "registry_read('HKLM', 'Software\\X', 'Path')" }`;
    exposed as `var.<name>` in `When`, screen defaults, `{var.<name>}` brace
    tokens in step paths/args — this is the cross-step data-flow mechanism.
  - Blob/schema: `vars` map into `SerializableWrapperBlob` + JSON context +
    `sigil-schema.json` (single M0-style pass, one owner).
  - Redaction: a var referencing `param.<secret>` inherits secretness.
- **Verify:** unit tests per function (present/absent/access-denied);
  round-trip blob test; integration: step path containing `{var.x}` from a
  registry value lands the file; secret-derived var absent from journal/log.

### P7 — install logging (wave 1)

- **Gap G8.** `/LOG=<path>` (and `/LOG` → default `%TEMP%\sigil-<appid>.log`);
  engine emits timestamped step begin/end/result, rollback, exit code; wizard
  Failed screen offers "open log". Redaction applies. Uninstall logs too.
- **Verify:** log created in both modes; secret grep test extended to log;
  failed install log contains the failing step + rollback trail.

### P2 — lifecycle hooks + run-after-install (wave 2)

- **Gap G2, G4.** Manifest gains `installer.hooks`: `pre_install`,
  `post_install`, `pre_uninstall`, `post_uninstall` — each a list of existing
  step records (typically `run_program`), running outside the journaled body
  with explicit `on_failure: fail|continue` (no rollback obligations;
  document loudly). `post_install` runs before the Done screen.
  Done screen: optional "Launch <App>" checkbox (`installer.run_after_install:
  path + args`, default on, honored by `/silent` only with `/launch`).
- **Verify:** hook ordering integration test; failing pre_install aborts before
  journal opens; launch checkbox starts the app unelevated (ShellExecute from
  the de-elevated context — test both scopes).

### P4 — http_download step (wave 2)

- **Gap G5.** New step `http_download { url, dest, sha256, timeout, retries }`.
  sha256 **required** (refuse to pack without it); HTTPS only; proxy from
  system; wizard shows a download progress row; rollback deletes the file;
  `/silent` honors it headlessly. Reuses/replaces `HttpOptionsLoader`'s HTTP
  plumbing behind one AOT-safe client helper.
- **Verify:** integration with local HTTPS test server: success, checksum
  mismatch → step fails + rollback, timeout/retry; determinism unaffected
  (network only at install time).

### P8 — config-file steps (wave 2)

- **Gap G9.** Steps `ini_write { path, section, key, value }`,
  `json_edit { path, pointer, value }` (RFC 6901 pointer),
  `xml_edit { path, xpath, attribute?, value }` (System.Xml, AOT-safe subset).
  All journaled: snapshot prior value/file for rollback. Brace tokens + vars
  usable in values.
- **Verify:** per-step unit tests incl. rollback restores byte-exact prior
  content; missing file → configurable create-or-fail.

### P3 — upgrade semantics (wave 3)

- **Gap G3.** Using `installed_version()` (P1): pack embeds version; at start,
  compare vs installed ARP entry → states fresh / same (existing repair path) /
  older-installed (upgrade: run previous `uninstall.exe /S`, then install; keep
  user data by honoring prior install_dir) / newer-installed (block with exit
  code + wizard notice; `/force-downgrade` override).
- **Verify:** VM matrix legs: v1→v2 upgrade preserves install_dir and leaves
  one ARP row; v2→v1 blocked; `/force-downgrade` succeeds.

### P5 — prerequisites (wave 3)

- **Gap G6.** Manifest `installer.prerequisites[]`:
  `{ name, detect: <expression>, source: payload://... | https://... (sha256),
  args, exit_codes_ok: [0, 3010], scope_required }`. Runs before the journaled
  body, sequential, with wizard progress rows; 3010 sets reboot-required flag
  surfaced on Done screen. Ship documented recipes (VC++ redist, .NET runtime)
  in `docs/guides/prerequisites.md`.
- **Verify:** fixture with fake prereq exe: detect-true skips, detect-false
  installs then re-detects; 3010 → Done shows reboot notice; silent honors all.

### P6 — files-in-use + setup mutex (wave 3)

- **Gap G7, G17.** `installer.app_mutex` (name list) + Restart Manager
  (`RmStartSession`/`RmGetList` via `[LibraryImport]`) over target install dir:
  wizard shows "close these apps" screen with Retry/Close-for-me;
  `/closeapps` flag for silent (default: fail with exit code). Uninstall path
  gets the same check. Setup exe itself takes a named mutex (second instance →
  friendly exit).
- **Verify:** integration: lock a payload-target file → screen lists the
  process; `/silent` without `/closeapps` exits nonzero; with it, process
  closed and install proceeds; double-launch shows single-instance notice.

### P9 — wizard localization (wave 4)

- **Gap G10.** Per ADR-008 stance: string table for all built-in wizard
  strings; `installer.language` / auto from `locale()`; manifest-supplied
  translations for declared screen text (`title: { en: ..., de: ... }`);
  start with en + 5–10 seed languages; keep `InvariantGlobalization` by
  shipping culture-neutral resources (source-generated, AOT-safe — no .resx
  satellite assemblies).
- **Verify:** pseudo-loc test catches hardcoded strings; German fixture
  renders translated chrome + declared screens; silent unaffected.

### P10 — custom components (wave 4)

- **Gap G11.** `installer.options.components[]` becomes extensible: app-defined
  components `{ name, label, default, locked, when }` gating arbitrary step
  groups via existing `option.<name>` expressions (generation already in
  `OptionStepGenerator` — generalize the table). No hierarchy in v1 (flat list
  matches Inno `[Tasks]`, the most-used shape).
- **Verify:** fixture with custom component toggling a file group; locked +
  when interplay; silent `/Pcomponent=off` maps onto options.

**P11, P12, P13** are the open lanes — their full task breakdowns live in
[Remaining work](#remaining-work--detailed-breakdown-p11p13) below, past the
progress checklist. (The original one-paragraph sketches were promoted into that
section.)

## Progress checklist

| Task | Branch | Started | Pushed | Merged | Wave | Evidence |
|------|--------|:---:|:---:|:---:|------|---|
| P0  | task/p0-adr-008 | ☑ | ☑ | ☑ | 0 | `39823b8` — `adr-008-expression-policy.md` |
| P1  | task/p1-vars-data-retrieval | ☑ | ☑ | ☑ | 1 | `8804672` — `SIG0270`, `installer.vars`, `var.*` |
| P7  | task/p7-logging | ☑ | ☑ | ☑ | 1 | `560c1ad` — `/LOG` sink |
| P2  | task/p2-lifecycle-hooks | ☑ | ☑ | ☑ | 2 | `61fc69b` — hooks + `run_after_install` |
| P4  | task/p4-http-download | ☑ | ☑ | ☑ | 2 | `3deae36` — `HttpDownloadStep`, `SIG0235/6` |
| P8  | task/p8-config-steps | ☑ | ☑ | ☑ | 2 | `88f01b9` — Ini/Json/XmlEditStep |
| P3  | task/p3-upgrade | ☑ | ☑ | ☑ | 3 | `232915a` — `UpgradeDecision`, exit 3 |
| P5  | task/p5-prerequisites | ☑ | ☑ | ☑ | 3 | `0b1efde` — `SIG0280` |
| P6  | task/p6-files-in-use | ☑ | ☑ | ☑ | 3 | `d92cfa5` — Restart Manager + mutex |
| P9  | task/p9-localization | ☑ | ☑ | ☑ | 4 | `c033cfc` — `Localization/`, `SIG029x` |
| P10 | task/p10-custom-components | ☑ | ☑ | ☑ | 4 | `9088c72` — `SIG0300` |
| P11 | task/p11-system-steps | ☑ | ☑ | ☑ | 5 | PR #13, `f61bfbe` — `scheduled_task_create`/`com_register`/`firewall_rule`, `SIG0310` |
| P12 | task/p12-updates-webinstaller | ☑ | ☑ | ☑ | 5 | PR #14, `110b7d5` — signed channel manifest, `/Update` runtime, `--payload web` (`SIG0320`–`SIG0322`) |
| P13 | task/p13-verification | ☑ | ☐ | ☐ | 6 | this branch — VM matrix, size/coverage re-pin, status reconciliation + `from-inno.md` (docs-only; not yet merged) |

---

## Remaining work — detailed breakdown (P11–P13)

Everything above P11 is merged. This section turns the three open lanes from
one-paragraph sketches into ordered, TDD-shaped tasks. Follow the repo chain
discipline verbatim — for every new step use the `.claude/skills/add-install-step`
chain, for every schema/blob edit the `.claude/skills/schema-change` chain, and
the `.claude/skills/aot-safety` checklist before claiming AOT-clean. Each task is
red→green: write the failing xUnit test first, then the implementation.

**Anchor facts confirmed against `main` @ current HEAD (so the plan targets reality):**

- Current step catalog ends at `XmlEdit` in
  [`StepFactory.cs`](../../../src/SigilBuild.Wrapper.Core/Engine/StepFactory.cs)
  (FileCopy, DirectoryCreate/Delete, FileDelete, Registry×3, ShortcutCreate,
  EnvSet, ServiceInstall, RunProgram, HttpDownload, IniWrite, JsonEdit, XmlEdit).
- The "9-layer chain" a step must thread, evidenced by P4/P8: Core model
  (`InstallStep` union) → `ManifestParser` → `sigil-schema.json` (step-`type`
  enum appears in **multiple** places — update all) → `SerializableInstallStep`
  (hand-rolled discriminator, **not** `JsonDerivedType`) → runtime `*Step` in
  `Steps/` → `StepFactory` → wizard (only if UI-visible) → tests → docs.
- `/Update` currently prints `EngineUpdateUnsupported` and `return 64` at
  [`InstallSession.cs`](../../../src/SigilBuild.Wrapper.Core/Engine/InstallSession.cs)
  (`case WrapperMode.Update`). `WrapperMode.Update` and `UpdatesSection` already
  exist and are wired to parse — P12 fills the dead body, it does not add the mode.
- Free diagnostic bands (see `DiagnosticCodes.cs`, highest used = `SIG0300`):
  allocate **`SIG031x`** for P11 system steps, **`SIG032x`** for P12 updates.

### P11 — scheduled task / COM / firewall steps (wave 5, gaps G12–G14)

Three thin journaled steps, machine-scope only. Each is an independent slice of
the same chain; do them in the listed order (schtasks first — simplest rollback
model — then COM, then firewall). Ship all three behind one branch
`task/p11-system-steps`, one commit per step so review stays legible.

- **T11.0 — shared scope guard.** All three steps require allusers/machine scope
  (they touch machine-global state). Add one reusable pack-time diagnostic
  `SIG0310 SystemStepRequiresMachineScope` (Error) raised when a manifest uses
  any P11 step while `installer.scope` can resolve to `user`. One helper, three
  callers. Test: user-scope manifest with each step → `SIG0310`.
- **T11.1 — `scheduled_task_create`.** Fields
  `{ name, program, arguments?, trigger: logon|daily|onstart, run_level: limited|highest, when? }`.
  Runtime wraps `schtasks.exe /Create /TN <name> /TR ... /SC ... /RU SYSTEM /F`
  (no reflection — plain `Process` exec, mirrors `RunProgramStep`). Rollback:
  `schtasks /Delete /TN <name> /F`. Journal records the task name only.
  Full 9-layer chain. Tests: parse (missing `name`/`program` → `SIG0232`),
  blob round-trip, integration on VM (create → assert `schtasks /Query` finds it
  → rollback → assert gone).
- **T11.2 — `com_register`.** Field `{ path, when? }`. Runtime: `LoadLibrary`
  the DLL via `[LibraryImport]` (AOT-safe P/Invoke — **not** `Assembly.Load`),
  `GetProcAddress("DllRegisterServer")`, invoke via
  `Marshal.GetDelegateForFunctionPointer` on a `[UnmanagedFunctionPointer]`
  delegate; check `HRESULT`. Rollback calls `DllUnregisterServer`. Confirm the
  `LibraryImport`/function-pointer path passes the AOT analyzer under `-c Release`
  (this is the one AOT-risk step in P11 — run the `aot-safety` checklist).
  Tests: parse, round-trip, VM integration with a known self-registering DLL
  (register → assert `HKCR\CLSID\{..}` present → rollback → assert gone).
- **T11.3 — `firewall_rule`.** Fields
  `{ name, direction: in|out, action: allow|block, program?, port?, protocol?, when? }`.
  Runtime wraps `netsh advfirewall firewall add rule ...`. Rollback:
  `netsh advfirewall firewall delete rule name=<name>`. Full chain. Tests:
  parse, round-trip, VM integration (add → `netsh ... show rule` → rollback).
- **T11.4 — docs (same branch).** `docs/guides/install-steps.md` gains the three
  steps with the allusers-scope note; one migration-guide mapping row each
  (schtasks/RegDLL/netsh idioms → step); `docs/manifest-reference.md` regenerated;
  amend `adr-008-expression-policy.md` step catalog (closed-catalog rule).
- **Verify (P11 whole):** VM legs asserting creation **and** full reversal on
  both rollback (mid-install failure) and uninstall; secret-redaction grep over
  the new log lines; size gate re-measured (netsh/schtasks add little; COM P/Invoke
  is native — expect near-zero growth, but confirm the `sigil.exe`/host gates).

### P12 — update engine + web installer (wave 5, gaps G15–G16)

Implements the parsed-but-dead `UpdatesSection` and adds a web-installer pack
variant. Larger than P11 — split into an **update-engine** half (T12.1–T12.4)
and a **web-installer** half (T12.5–T12.6). Reuses P4's HTTP plumbing
(`SigilHttpClient`/`SigilDownloader`) and P3's upgrade path (`UpgradeDecision`).
Delta updates (`deltaTargets`, zstd dictionaries) stay **explicitly deferred** —
ship full-package updates first, write the delta-deferral ADR (T12.7).

- **T12.1 — channel manifest contract + parser.** Define the signed channel
  manifest schema (version, package URL, sha256, min-from-version,
  `signingKey` reference already in `UpdatesSection`). AOT-safe source-generated
  JSON context (mirror `WrapperBlobJsonContext`), **no** reflection
  `JsonSerializer`. Diagnostics `SIG0320` (malformed channel manifest),
  `SIG0321` (signature/key mismatch). Tests: valid parse, tampered field → reject.
- **T12.2 — signature verification.** Verify the channel manifest against
  `signingKey` using the existing `AuthenticodeVerifier`/signing primitives (no
  new crypto). Tampered manifest → hard reject, exit nonzero, logged. Test with a
  re-signed-vs-tampered fixture pair.
- **T12.3 — `/Update` runtime.** Replace the `return 64` body in
  `InstallSession.cs` `case WrapperMode.Update`: fetch channel manifest →
  verify (T12.2) → compare `installed_version()` (P1 fn) vs channel version via
  `UpgradeDecision` (P3) → if newer available, download package (P4 plumbing,
  sha256 mandatory) → run the P3 upgrade path (uninstall-old-then-install,
  preserve install_dir). No update available → clean exit 0 with a logged
  "up to date". Keep exit-64 only for genuinely-malformed invocation.
- **T12.4 — silent + logging parity.** `/Update /silent` runs headless;
  `/Update` honors `/LOG` (P7 — the log sink already opens before mode dispatch,
  so this is mostly assertion coverage). Wizard `/Update` shows a download +
  progress row (reuse P4's progress row + P5 prereq row plumbing).
- **T12.5 — web installer pack variant.** `pack --format exe --payload web`
  (new `--payload {embedded|web}` option on `PackCommand`, default `embedded`).
  Emits a stub `Setup.exe` whose payload is a single `http_download` (P4) of the
  full package + sha256, then chains into the normal install. Determinism
  unchanged (network only at install time, as with P4). Refuse `--payload web`
  without a resolvable package URL (`SIG0322`).
- **T12.6 — stub end-to-end.** Local channel-server + package-host fixture: stub
  downloads → verifies → installs full package; tampered package rejected; runs
  on the VM leg.
- **T12.7 — delta-deferral ADR + docs.** New `docs/guides/updates.md` (channels,
  signed manifest, web installer, security model); ADR recording why delta is
  deferred and the intended follow-up shape; update `docs/cli-reference.md`
  (`/Update` no longer "not supported"; new `--payload` flag); manifest-reference
  for the channel/updates fields; regenerate any generated reference output.
- **Verify (P12 whole):** local channel server fixture — update detected,
  downloaded, verified, installed, install_dir preserved, one ARP row after;
  tampered manifest **and** tampered package both rejected with distinct exit
  codes + log lines; stub web installer end-to-end on VM; size gates re-measured
  (the update path adds HTTP + signature verify weight — measure and, if it trips
  a gate, treat as a design conversation per AGENTS.md §3, re-pin consciously with
  a note).

### P13 — verification sweep (wave 6)

Runs only after P11 + P12 merge. No new product behavior — closes the track.

- **T13.1 — VM matrix expansion.** Extend `wrapper-vm-tests.yml` with legs for:
  P11 schtasks/COM/firewall create+reverse, P12 update (detect→download→verify→
  install) and web-installer stub, plus re-confirm the still-relevant upgrade /
  prereq / closeapps legs.
- **T13.2 — size gates re-pinned.** Re-measure `sigil.exe` (≤15 MB) and installer
  host (≤45 MB, ~3 MB headroom noted in AGENTS.md after P9). HTTP/XML/globalization
  already landed; P11 native P/Invoke + P12 update path are the new deltas.
  Document each measured number; re-pin only with justification.
- **T13.3 — coverage + un-skip.** Full coverage report against the ≥65% union
  gate; un-skip anything parked during waves 1–5; confirm secret-redaction grep
  covers every new log/journal/variable surface added in P11/P12.
- **T13.4 — status reconciliation.** Update
  [`00-GAP_ANALYSIS.md`](00-GAP_ANALYSIS.md) — flip G12–G16 from gap to
  parity-confirmed (§1), leave G18/G19 as documented non-goals; mark P11–P13 rows
  ☑ in the checklist above with commit evidence. Add the Inno migration guide
  (`docs/migration/from-inno.md`) still listed as open doc debt in
  [`02-DOCS_UPDATES.md`](02-DOCS_UPDATES.md) if not already landed.
