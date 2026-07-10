# Sigil — implementation spec: wizard-driven `.exe` installer

**Audience:** an autonomous coding agent.
**Goal:** make `sigil pack --format exe` produce a working, single-file,
wizard-driven Windows installer (the NSIS / Inno Setup / WiX replacement), and
drive its look + screens declaratively from `sigil.yaml`.

This document is self-contained. Execute the tasks in order; each has explicit
file targets and acceptance criteria. Do not start a task until its dependencies
are green. Keep `main` shippable after every task.

---

## 0. Repository constraints (read first — these are hard gates)

- **.NET 10 LTS, C# 14, Native AOT.** No reflection-heavy patterns. Use source
  generators for all JSON/YAML (`System.Text.Json` source-gen contexts,
  YamlDotNet AOT source gen). New serializable types MUST be added to an existing
  or new `JsonSerializerContext`.
- `TreatWarningsAsErrors=true` and `EnableTrimAnalyzer=true` (Release). Any
  IL2xxx/IL3xxx trim/AOT warning fails the build. This is the single biggest
  constraint on new code.
- `Directory.Build.props` sets `Nullable=enable`, `ImplicitUsings=enable`,
  `AnalysisLevel=latest-recommended`. Match existing code style
  (`.editorconfig`).
- Determinism: packaging output must be byte-identical across builds (sorted
  file enumeration, pinned timestamps).
- Quality bars enforced by CI: `sigil --version` cold-start ≤ 200 ms, CLI binary
  ≤ 15 MB, Core coverage ≥ 80 %, signing/update coverage ≥ 85 %. Add a size gate
  for the installer host too (see Task 3).
- Tests use the existing xUnit projects under `tests/`. Every task that changes
  behavior adds or updates tests.

### Current component layout

```
src/SigilBuild.Cli/                 # `sigil` binary. Commands/{Validate,Init,Pack,Sign}Command.cs, Program.cs
src/SigilBuild.Core/                # Manifest models + parser + hand-rolled schema validator + diagnostics
src/SigilBuild.Packaging/           # ZIP + MSIX + ExeWrapper packagers; Installer/BrandTokenEmitter.cs
src/SigilBuild.Signing/             # Local PFX + Azure Trusted Signing
src/SigilBuild.Wrapper/             # AOT console step-engine runtime (Exe project)
src/SigilBuild.Installer.Host/      # Avalonia branded wizard (separate Exe project)
src/SigilBuild.Installer.BrandGenerator/  # WcagContrast + brand asset helpers
schemas/sigil-schema.json           # draft-07 manifest schema (hand-validated in Core)
tests/…                             # xUnit per project, plus *.IntegrationTests
.github/workflows/wrapper-vm-tests.yml   # Windows-VM harness for the wrapper
```

### Key existing types (do not re-derive — reuse)

- `SigilBuild.Core.Manifest.SigilManifest` — root record; has `App`, `Build`,
  `Package?`, `Sign?`, `Publish?`, `Updates?`, `Installer?`, `Parameters?`,
  `InstallSteps?`, `PreInstall?`, `PostInstall?`.
- `InstallerSection(InstallerBrand? Brand)` and
  `InstallerBrand(Logo, Hero, PrimaryColor, AccentColor, GradientStart, GradientMid, GradientEnd)`
  in `Manifest/InstallerSection.cs`.
- `ParameterDefinition(Name, Type, Default, EnumValues, InstallTime, Description, Pattern, Min, Max)`
  and `enum ParameterType { String, Path, Bool, Int, Enum, Secret }` in
  `Manifest/ParameterDefinition.cs`.
- `InstallStep` (abstract) with nested `FileCopy`, `DirectoryCreate`, `FileDelete`,
  `DirectoryDelete`, `RegistryWrite`, `RegistryDeleteValue`, `RegistryDeleteKey`,
  `ShortcutCreate`, `EnvSet`, `RunProgram`; `enum OnFailure { Rollback, Continue, Fail }`.
- `SigilBuild.Wrapper.Engine.{InstallEngine, UninstallEngine, StepContext, WrapperBlob, RollbackJournal}`,
  `Steps/*`, `Expressions/{Lexer,Parser,Evaluator}` (the `when` expression engine).
- `SigilBuild.Wrapper.Cli.CommandLineParser`, `WrapperMode { Install, Update, Uninstall }`.
- `SigilBuild.Packaging.ExeWrapper.{ExeWrapperPackager, WrapperResourceWriter, WrapperRuntimeLocator}`
  — packager + Win32 resource embed already implemented; blocked only on the AOT runtime existing.
- `SigilBuild.Packaging.Installer.{BrandTokenEmitter, InstallerHostBundler}`.
- `SigilBuild.Installer.Host` — `Views/InstallerWindow.axaml`, `Views/Screens/*View.axaml`,
  `ViewModels/InstallerViewModel.cs`, `Services/InstallerEngine.cs` (throwaway),
  `Branding/{BrandTokens.cs, BrandPalette.axaml}`.

---

## 1. Locked design decisions

1. **Wizard-first.** The `.exe` shows the branded Avalonia wizard by default;
   `/silent` (and `/verysilent`) run headless for CI. The stamped runtime is the
   **Installer.Host**, driving the real step engine.
2. **zstd payload.** Embedded payload uses zstd, not Deflate — shared codec with
   the future delta-update engine.
3. **Two-color branding.** `brand.primary_color` + `brand.accent_color`; the full
   light + dark palette is derived at pack time. Gradient fields are **removed
   outright** (not deprecated): the JSON schema is `additionalProperties: false`
   and never exposed them, so no manifest can currently set them.
4. **Manifest-driven flow.** The wizard renders only the screens the manifest
   declares. Order: `welcome → destination → license? → options? →
   [declared screens] → installing → done`.
5. **Options = built-in but configurable.** Desktop shortcut / PATH / file
   associations / start menu ship built-in; each is individually configurable and
   can be disabled. Each enabled component auto-generates its install step(s).
6. **Custom screens = declared forms over parameters.** No arbitrary markup.
7. **Sign trust line** is gated on a verified `sign` block, never on
   `App.publisher` alone.
8. **Uninstall survives deletion of the setup exe.** Install copies the running
   installer into the install dir as `uninstall.exe`; ARP's `UninstallString`
   points there — never at `Environment.ProcessPath` (the current placeholder
   behavior, which breaks the moment the user deletes the download).
9. **Dual install scope in v1.** `installer.scope: user | machine | auto`
   (default `auto`) plus `/allusers` / `/currentuser` flags. Machine scope →
   Program Files, HKLM ARP, machine PATH, elevation required. User scope →
   `%LocalAppData%\Programs`, HKCU ARP, user PATH, no UAC prompt. `auto` =
   user scope unless `/allusers` or the wizard's scope toggle says otherwise.
10. **Silent parameters via `/Pname=value`**, install dir via `/D=path`
    (NSIS-style). `/S` is an alias for `/silent`. Everything the wizard can
    collect must be suppliable headlessly.
11. **Brand tokens + assets travel inside the WrapperBlob** (`SIGIL_BLOB_V1`):
    derived token maps plus base64 logo/hero. One resource, one source-generated
    serializer; no loose `BrandTokens.g.json` next to the stamped exe.

---

## 2. Task list (execute in order)

Dependency graph: `T1 → T2 → T3 → T4` (launchable wizard); `T5 → T6` (installs
files, depends on T2); `T7,T8,T9` (manifest-driven UI, depend on T2); `T12`
(scope/elevation — decide during T2, implement after T5; blocks T13, T15);
`T13,T14,T15` (destination screen, license, uninstall survivability); `T10,T11,T16`
(polish + MSIX reconciliation); `T17` (verification, last). T5/T6 and T7–T9 are
parallelizable after T2.

---

### T1 — Extract the step engine into a class library

**Why:** `SigilBuild.Wrapper` is an `Exe`; `Installer.Host` can't cleanly
reference its engine. Wizard-first requires both the console `/silent` path and
the GUI to share one engine.

**Do:**
- Create `src/SigilBuild.Wrapper.Core/SigilBuild.Wrapper.Core.csproj`
  (`Microsoft.NET.Sdk`, classlib, net10.0, `AllowUnsafeBlocks=true` for the
  `[LibraryImport]` P/Invokes, references `SigilBuild.Core`).
- Move `Engine/`, `Steps/`, `Expressions/`, `Json/`, `WrapperBlob.cs`, and the
  `Cli/CommandLineParser.cs` + related types into it. Keep `internal` visibility;
  add `InternalsVisibleTo` for `SigilBuild.Wrapper`, `SigilBuild.Installer.Host`,
  `SigilBuild.Packaging`, and the relevant test projects.
- `SigilBuild.Wrapper` becomes a thin `Exe` referencing `Wrapper.Core`; its
  `Program.cs` stays as the console/`/silent` entry.
- Update `Sigil.sln`, all `ProjectReference`s, and moved namespaces if renamed
  (prefer keeping `SigilBuild.Wrapper.*` namespaces to minimize churn).

**Acceptance:** solution builds; all existing wrapper tests pass unchanged;
`SigilBuild.Packaging` still compiles against the moved `WrapperBlob`/DTOs.

---

### T2 — Make Installer.Host the runtime and drive the real engine

**Why:** unify the GUI and the engine; delete the throwaway copy loop.

**Do:**
- `Installer.Host` references `Wrapper.Core`.
- Rewrite `Installer.Host/Program.cs`: load `WrapperBlob.LoadFromSelf()`; parse
  args via `CommandLineParser`. If `/silent`/`/verysilent` (or mode ≠ interactive
  install), run `InstallEngine`/`UninstallEngine` headless with console output.
  Otherwise start Avalonia and show the wizard.
- **Command-line contract** (extend `CommandLineParser`; this is the single
  parser for both entry paths):
  - `/silent` (alias `/S`), `/verysilent`, `/Uninstall` — note
    `ArpRegistration.BuildUninstallString` already emits `/S /Uninstall`, so
    the `/S` alias is mandatory, not optional.
  - `/Pname=value` — sets a declared parameter or option (`/Pdesktop_shortcut=false`).
    Unknown name → exit 64. In silent mode, undeclared values fall back to
    parameter/option defaults; a required parameter (no default, e.g. a `secret`)
    that is missing → exit 64 with a message naming it.
  - `/D=path` — install dir override (see T13).
  - `/allusers`, `/currentuser` — scope override (see T12).
  - Exit codes: **0** ok, **1** step failure (rollback completed), **2** user
    cancelled (rollback completed), **64** usage/validation error.
  - `WrapperMode.Update` is not implemented in this track: exit with a distinct
    "not supported" message and code 64. Say so explicitly rather than silently
    running zero steps (`WrapperBlob.UpdateSteps` is currently always empty).
- The Installing screen gets a **Cancel** button: cancellation triggers the same
  rollback path as a step failure and exits 2.
- The wizard's Installing screen drives the **same** `InstallEngine.RunAsync`,
  passing an `IProgress<T>` adapter that updates `InstallerViewModel`
  (progress fraction, current step, and a growing log line list matching the
  prototype's `copy/reg/path/link` output). Surface the engine's error + rollback
  path to the Failed screen.
- **Delete** `Installer.Host/Services/InstallerEngine.cs` and its tests; migrate
  any still-relevant assertions onto the real engine.
- Persist the rollback journal + write ARP on success (already in wrapper
  `Program.cs` — move that logic into a shared method used by both entry paths).

**Acceptance:** running the host `.exe` with no args shows the wizard;
`/silent` installs headlessly with correct exit codes; `/Pname=value` overrides a
parameter and an option; a missing required parameter fails silent install with
exit 64; a forced step failure drives the Failed screen and the rollback journal
reverses changes; Cancel mid-install rolls back and exits 2.

---

### T3 — Wire the AOT runtime build into the SDK (the core blocker)

**Why:** `WrapperRuntimeLocator.Locate()` expects
`runtimes/win-x64/…exe` next to the packaging assembly; nothing publishes it.

**Do:**
- Add an MSBuild target (or a `scripts/` step invoked by CI + local pack) that
  runs `dotnet publish src/SigilBuild.Installer.Host -c Release -r win-x64
  -p:PublishAot=true` and copies the output exe into
  `runtimes/win-x64/SigilBuild.Installer.Host.exe` within the packaging/CLI
  package output. Add `win-arm64` as a second RID.
- Point `WrapperRuntimeLocator.Locate()` at `SigilBuild.Installer.Host.exe`
  (it currently expects `SigilBuild.Wrapper.exe`) **and make it take the target
  architecture** — it is hardcoded to `win-x64`, but the manifest example declares
  `architectures: [x64, arm64]`. `ExeWrapperPackager` must select the runtime per
  arch and produce one `-Setup.exe` per declared architecture.
- **Validate Avalonia 11 publishes clean under Native AOT + `TreatWarningsAsErrors`.**
  This is the highest-risk item. If trim warnings are unavoidable: fall back to a
  self-contained (non-AOT) host publish, or keep a tiny AOT console wrapper that
  launches the host as a child process. Record the outcome in an ADR under
  `docs/architecture/` (note: there is no `sigil-docs/` directory; docs live in
  `docs/`).
- Add a CI artifact-size gate for the host exe (set a target, e.g. ≤ 25 MB;
  measure and pin).

**Acceptance:** a Release build produces the stamped runtime under
`runtimes/win-x64/` and `runtimes/win-arm64/`; `WrapperRuntimeLocator` resolves
each by arch; the AOT publish is warning-clean or the fallback is documented in
an ADR.

---

### T4 — Enable `PackageFormat.Exe` dispatch

**Do:**
- In `src/SigilBuild.Cli/Commands/PackCommand.cs`, replace the
  `PackageFormat.Exe => throw new NotSupportedException(...)` arm with
  `PackageFormat.Exe => new ExeWrapperPackager()`.
- **`ManifestParser.ParseFormat` currently rejects `"exe"`** (the throw arm is
  unreachable today) — accept it and add it to the schema's `formats` enum if
  missing.
- Note: `WrapperResourceWriter` uses `BeginUpdateResourceW`, so `--format exe`
  only works on a **Windows pack host**. Emit a clear diagnostic on other OSes
  and document the limitation.

**Acceptance:** `sigil pack sigil.yaml --format exe` (or a manifest with
`formats: [exe]`) produces `<App>-<ver>-<arch>-Setup.exe` that launches the
wizard. Add/enable an integration test in
`tests/SigilBuild.Packaging.IntegrationTests`.

---

### T5 — Payload extraction + `payload://` resolution

**Why:** the packager embeds the payload as `SIGIL_PAYLOAD_V1`, and
`WrapperBlob.LoadPayloadBytes()` reads it, but nothing extracts it — a stamped
`.exe` currently installs nothing.

**Do:**
- At install start (both `/silent` and GUI), extract the embedded payload archive
  to a temp dir (e.g. `%TEMP%\sigil-<appid>-<rand>\`).
- Extend `StepContext` with the extracted payload root; teach `FileCopy` (and any
  path-taking step) to resolve `payload://relative/path` against it.
- Register temp-dir cleanup on success **and** on rollback (add to the journal /
  a `finally`).

**Acceptance:** an integration test packs a fixture with a `file_copy` from
`payload://app/app.exe`, runs the installer `/silent`, and asserts the file lands
at the target and the temp dir is cleaned up. Rollback also cleans temp.

---

### T6 — Switch payload codec to zstd

**Do:**
- In `ExeWrapperPackager.BuildPayloadBytes`, replace the `ZipArchive`/Deflate with
  zstd (`ZstdNet` + native fallback, per the stack table). Keep deterministic:
  fixed compression level, sorted entry order, pinned mtime, stable container
  framing.
- Decompress on the extraction side (T5). Bump the payload resource marker if the
  container format changes (`SIGIL_PAYLOAD_V2`) and gate the reader on it.
- Confirm the native zstd dependency AOT-publishes (static link or bundled native
  lib); if it fights AOT, isolate it behind the existing packager interface.

**Acceptance:** payload round-trips through zstd; deterministic byte-identical
output across two packs of the same input; T5's tests still pass.

---

### T7 — Two-color branding + derived palette

**Do:**
- Simplify `InstallerBrand` to `PrimaryColor` + `AccentColor` (+ `Logo`, `Hero`).
  **Delete `GradientStart/Mid/End` outright** from the record, `BrandTokenEmitter`
  (which currently emits hardcoded gradient defaults), and anything downstream —
  the schema never exposed them (`additionalProperties: false`), so no deprecation
  window is needed.
- Port the prototype's `colors()` derivation into `BrandTokenEmitter`: produce the
  full **light and dark** token maps at pack time (Avalonia can't `color-mix` at
  runtime). The maps go into the blob (below), not a sidecar file.
- `color-mix(in srgb, A p%, B)` = per-channel linear interpolation of 0–255 sRGB
  values: `out_c = round(A_c*(p/100) + B_c*(1 - p/100))`. Implement a small
  `SrgbMix(hexA, pct, hexB)` helper. Derive at minimum: `railBg`, `railText`,
  `railMuted`, `logoTile`, `accent`, `accentHover`, `frame`, `winBg`, `paneBg`,
  `titleBg`, `border`, `textPri`, `textSec`, `textMut`, `successText/Bg`,
  `inputBg`, `track`, `logBg`, `logText`, `dangerText/Bg`, `ghostHover` — for
  both modes. The reference `colors()` implementation with the exact light/dark
  constants is in `docs/plan/prototype/sigil-installer-wizard-prototype.html`.
- Extend `BrandTokens.cs` + `BrandPalette.axaml` to consume the derived tokens.
- **Delivery into the stamped exe:** thread the derived token maps plus base64
  `Logo`/`Hero` bytes into `WrapperBlob` (`SerializableWrapperBlob` + its
  `JsonSerializerContext`). The current `BrandTokens.g.json`-next-to-exe delivery
  is an **MSIX bundling** mechanism (`InstallerHostBundler`) and does not exist
  for a single stamped `.exe` — without this the wizard launches unbranded. The
  host reads brand data from the blob only.
- Keep the WCAG-AA-against-white check; extend it to the derived rail-muted text.

**Acceptance:** a manifest with only `primary_color`/`accent_color` yields a fully
themed wizard in both light and dark; changing the two colors reskins everything;
a stamped `.exe` renders logo + palette with no loose files beside it; unit tests
cover `SrgbMix` and a golden token map for a known input.

---

### T8 — Built-in configurable Options screen

**Do:**
- Add `InstallerOptions` to the manifest model — per-component config for
  `desktop_shortcut`, `start_menu`, `add_to_path`, `file_associations`. Each
  accepts shorthand `true`/`false` **or** an object
  `{ enabled: bool, default: bool, locked: bool, …component keys }`
  (`file_associations` adds `extensions: [".x"]`). Add to `InstallerSection`.
- Schema: extend `installer` in `schemas/sigil-schema.json` for `options`.
- At pack time, for each enabled component, **auto-generate** the install step(s)
  gated on the checkbox value:

  | Component           | Generated step        | Gate                        |
  |---------------------|-----------------------|-----------------------------|
  | `desktop_shortcut`  | `ShortcutCreate` (Desktop)   | `option.desktop_shortcut`   |
  | `start_menu`        | `ShortcutCreate` (Start menu)| `option.start_menu`         |
  | `add_to_path`       | `EnvSet` (PATH, append)      | `option.add_to_path`        |
  | `file_associations` | `RegistryWrite` per ext      | `option.file_associations`  |

- Expose component values as `option.*` to the expression engine.
- The Options screen appears only if ≥ 1 component is enabled; `locked` components
  render disabled (always applied).

**Acceptance:** a manifest enabling desktop shortcut + PATH renders the Options
screen with two checkboxes; toggling one off skips its generated step; disabling a
component in YAML removes it entirely; `option.*` is usable in a step `when`.
Tests cover component→step generation and gating.

---

### T9 — Declared custom screens over parameters

**Do:**
- Add manifest records: `InstallerScreen(Id, Title, Subtitle?, When?, Fields)` and
  `ScreenField(Param, Widget?)`; add `Screens` to `InstallerSection`. Parameters
  remain declared in the existing top-level `parameters:` block.
- Schema: extend `installer` for `screens` (list of screen objects; `fields` is a
  list of bare param-name strings or `{ param, widget }` objects).
- Parser: resolve each field's `param` to a declared `ParameterDefinition` (error
  on unknown ref, `DiagnosticCodes`); validate `Title` interpolation tokens
  (`{app.name}` etc.) and each screen's `When` expression.
- Widget inference from `ParameterType` (override via `widget`):

  | Type    | Default widget           | Overrides       |
  |---------|--------------------------|-----------------|
  | `bool`  | checkbox                 | switch          |
  | `enum`  | radio (≤4) / dropdown    | radio, dropdown |
  | `secret`| masked input + show/hide | —               |
  | `path`  | input + browse           | —               |
  | `string`| text input               | textarea        |
  | `int`   | number input             | slider          |

- `Installer.Host`: render declared screens from the field list via a widget
  factory keyed on param type; validate input against `pattern`/`min`/`max`/`enum`
  before advancing; bind collected values into `param.*` for the engine.
- Thread declared screens + parameter defs into the wrapper blob
  (`SerializableWrapperBlob` + its `JsonSerializerContext`).
- Generate the rail step indicator from the resolved screen set; skip screens
  whose `When` is false at runtime.
- **Secret hygiene:** `ParameterType.Secret` values must never reach the rollback
  journal, the on-screen/console log lines, `%TEMP%`, or any persisted state —
  redact to `***` in logs and exclude from `UninstallStateStore`. Add a test that
  greps journal + log output for a known secret value.

**Acceptance:** the prototype's Configure screen (server / license_key / channel /
autostart) reproduces from a manifest; unknown param ref is a validation error;
a field value gates a later install step via `param.*`; the rail reflects only
visible screens. Tests cover parse, widget inference, and value flow.

---

### T10 — Real ARP registration fields

**Do:** Add `DisplayName`/`Version`/`Publisher`/`EstimatedSize` to `WrapperBlob`
sourced from `manifest.App.*` and the packed size; fix the placeholder
`ArpRegistration` call in the runtime (currently `DisplayName=AppId`, version
`"1.0.0"`, `Publisher="Unknown"`, `EstimatedSizeBytes=0`). Registry hive (HKLM vs
HKCU) follows the install scope (T12); `UninstallString` points at the copied
`uninstall.exe` (T15), not `Environment.ProcessPath`.

**Acceptance:** after `/silent` install, the Add/Remove Programs entry shows the
real name/version/publisher in the scope-correct hive; uninstall string works
after the setup exe is deleted. VM test asserts the registry values.

Also handle **re-install/upgrade**: if an ARP entry for the AppId already exists,
the wizard offers repair/reinstall (v1: uninstall-then-install is acceptable) and
`/silent` reinstalls idempotently; two consecutive installs must not duplicate
PATH entries, shortcuts, or ARP rows. Add a VM test for the double-install case.

---

### T11 — Sign trust-line gating

**Do:** The wizard's "Signed by {publisher}" line renders only when the `sign`
block is present and the artifact verifies at install time. Unsigned → no trust
line (or neutral "Publisher: {name}"). Pass a `SignDeclared` flag through the
blob; the **runtime self-verifies** its own Authenticode signature via
`WinVerifyTrust` (`[LibraryImport]`, AOT-safe) — the trust line requires
`SignDeclared && WinVerifyTrust(self) == valid`, so a tampered or re-stamped exe
drops it. Pipeline ordering is pack (stamp resources) → `sigil sign`: resource
stamping invalidates any prior signature, so signing must be last; document this
in the pack/sign command help.

**Acceptance:** signed fixture shows the trust line; unsigned fixture does not;
a signed-then-modified fixture does not. Test covers all three.

---

### T12 — Install scope + elevation

**Why:** nothing in the codebase or the original plan addresses UAC. ARP writes
to HKLM, Program Files and machine PATH need admin — an unelevated install would
fail at the first step. This decision shapes T2/T5/T8/T10/T13/T15; decide the
shape during T2, implement once files actually land (after T5).

**Do:**
- Manifest: add `Scope` to `InstallerSection` — `user | machine | auto` (default
  `auto`); extend the schema.
- Host exe manifest: `requestedExecutionLevel level="asInvoker"`. Machine scope
  from a non-elevated process → relaunch self elevated (`ShellExecuteW` with
  `runas`, forwarding all args) rather than requesting admin unconditionally —
  user-scope installs must stay prompt-free.
- Scope resolution: `machine`/`user` in the manifest is fixed (`/allusers` /
  `/currentuser` against a fixed opposite scope → exit 64). `auto` defaults to
  user; overridable by flag or a wizard scope toggle on the destination screen
  (T13).
- Per-scope behavior: install root (`%ProgramFiles%` vs
  `%LocalAppData%\Programs`), ARP hive (HKLM vs HKCU — parameterize
  `ArpRegistration`), `EnvSet` PATH target (machine vs user), shortcut folders
  (common vs user desktop/start menu), journal location (`%ProgramData%` vs
  `%LocalAppData%`). Expose `scope` to the expression engine.
- Uninstall must run in the scope it was installed with (record scope in the
  journal/state).

**Acceptance:** user-scope install completes without elevation and writes only
HKCU/user paths; machine scope elevates and writes HKLM/Program Files; `/allusers`
and `/currentuser` behave; VM tests cover both scopes end-to-end.

---

### T13 — Destination screen + `{install_dir}` contract

**Why:** the example manifest uses `{install_dir}` but nothing defines its
default, lets the user change it, or lets CI override it.

**Do:**
- Default: `<scope root>\<App.Name>` (per T12). Manifest override:
  `installer.install_dir` (may reference `{app.*}` tokens).
- Wizard: a destination screen (default position: right after `welcome`, before
  `license`) with the path input + browse button and the T12 scope toggle when
  scope is `auto`. Validate: absolute path, writable (or elevatable), non-file.
- `/D=path` overrides in both silent and GUI modes; `{install_dir}` resolves in
  step paths and expressions (it already appears in step templates — pin the
  semantics).

**Acceptance:** default path reflects scope; changing it in the wizard or via
`/D=` relocates the install; `{install_dir}` resolves in steps; invalid paths
block Next with an inline error.

---

### T14 — License screen manifest backing

**Why:** the Host ships `LicenseView` and the flow declares `license?`, but no
manifest field feeds it — the screen is unreachable dead UI.

**Do:**
- Add `License` (file path, plain text or RTF-as-text v1) to `InstallerSection`;
  extend the schema. Pack time: read the file, embed its text in `WrapperBlob`
  (diagnostic if missing/unreadable/empty).
- Wizard: License screen appears iff license text is present; Next disabled until
  the "I accept" checkbox is set. `/silent` implies acceptance (document this).

**Acceptance:** a manifest with `installer.license: ./LICENSE.txt` shows the
License screen with the embedded text; omitting it skips the screen; the flow
matches decision 4.

---

### T15 — Uninstall survivability (`uninstall.exe`)

**Why:** `ArpRegistration.BuildUninstallString` currently points at
`Environment.ProcessPath` — the original downloaded setup exe. Delete the
download and ARP uninstall is broken. The journal in state storage survives, but
nothing runnable references it.

**Do:**
- As a final install step (engine-level, journaled), copy the running installer
  exe into `{install_dir}\uninstall.exe`; point `UninstallString` at
  `"{install_dir}\uninstall.exe" /S /Uninstall` (v1 ships the full exe including
  payload; a payload-stripped stub is a follow-up optimization).
- Interactive uninstall (`uninstall.exe` double-clicked, no `/S`): minimal
  branded confirm → progress → done flow, driving `UninstallEngine`; `/S
  /Uninstall` stays headless.
- Self-deletion: `uninstall.exe` cannot delete its own running image — use the
  standard trick (relaunch from `%TEMP%` copy, or schedule
  `MoveFileExW(..., MOVEFILE_DELAY_UNTIL_REBOOT)` as v1 fallback). Journal replay
  must tolerate the exe's own entry.
- Uninstall reads the journal (as today), not the blob's steps; record scope
  (T12) in state and honor it.

**Acceptance:** install → delete the original setup exe → ARP uninstall still
works and removes files, registry, PATH, shortcuts, and (eventually) itself;
interactive uninstall shows the confirm flow; VM test covers the
deleted-original scenario.

---

### T16 — Reconcile the MSIX companion host

**Why:** `Installer.Host` is today the **MSIX companion installer** —
`InstallerHostBundler` bundles `installer.exe` + `BrandTokens.g.json` into MSIX
staging, fed by the copy-loop `InstallerEngine` that T2 deletes. Repurposing the
Host as the exe-wrapper runtime silently changes what MSIX ships.

**Do:** decide and implement one of: (a) MSIX bundles the same engine-driven
host (it now carries `Wrapper.Core`; feed it a blob or keep a directory-source
mode), or (b) the MSIX companion becomes a separate minimal exe. Update
`InstallerHostBundler` accordingly (including the `BrandTokens.g.json` sidecar,
which T7 removes in favor of blob-embedded tokens). Record as an ADR in
`docs/architecture/`.

**Acceptance:** MSIX packaging tests stay green after T2/T7; the decision is
documented; no orphaned copy-loop code remains.

---

### T17 — Verification (do last, and continuously)

**Do:**
- Un-skip the `Roundtrip_blob_via_resource_apis` fact in
  `tests/SigilBuild.Packaging.Tests/ExeWrapper/WrapperResourceWriterTests.cs`
  (the only skipped test there — the three JSON-context round-trips already run).
- Extend `.github/workflows/wrapper-vm-tests.yml`: pack a fixture `.exe`, run
  `/silent`, assert payload files land + ARP entry present + uninstall reverses
  everything (files, registry, PATH, shortcuts) — in **both scopes** (T12),
  including the deleted-original-exe uninstall (T15) and double-install (T10)
  scenarios.
- Add a headed smoke test that the wizard launches, walks welcome → … → done, and
  drives the real engine.
- Confirm all CI gates: AOT warning-clean, CLI ≤ 15 MB, host size gate, coverage
  thresholds.

**Acceptance:** full green CI including the VM job; no skipped installer tests.

---

## 3. Reference: end-to-end manifest example

```yaml
spec: v1.0

app:
  id: com.acme.Studio
  name: Acme Studio
  version: 3.2.0
  publisher: Acme, Inc.

build:
  source: ./out

package:
  formats: [exe]
  architectures: [x64, arm64]

sign:
  provider: azure-trusted-signing   # trust line shows only if this verifies

parameters:
  server_address: { type: string, default: "https://acme.internal", install_time: true, description: "Server address" }
  license_key:    { type: secret, install_time: true, description: "License key" }
  autostart:      { type: bool,   default: true,  description: "Start when I sign in" }
  channel:        { type: enum,   values: [stable, beta, nightly], default: stable, description: "Update channel" }

installer:
  scope: auto                 # user | machine | auto (T12)
  install_dir: "{scope_root}/Acme Studio"   # optional override (T13)
  license: ./LICENSE.txt      # shows the License screen (T14)
  brand:
    primary_color: "#312E81"
    accent_color:  "#4F46E5"
    logo: ./brand/logo.png
  options:
    desktop_shortcut: true
    add_to_path: { default: true }
    file_associations: { enabled: true, extensions: [".acme"], default: false }
    start_menu: false
  screens:
    - id: configure
      title: "Configure {app.name}"
      subtitle: "Connect to your server and set preferences."
      fields:
        - server_address
        - license_key
        - { param: channel, widget: radio }
        - autostart

install_steps:
  - { id: run_first_launch, type: run_program, program: "{install_dir}/acme.exe", args: ["--register"], when: "param.autostart == true", on_failure: continue }
```

Resolved wizard order for the above:
`welcome → destination → license → options → configure → installing → done`
(drop `installer.license` and the License screen disappears).

Silent equivalent:
`Setup.exe /S /D="C:\Tools\Acme" /Plicense_key=XYZ /Pchannel=beta /Pdesktop_shortcut=false /currentuser`

---

## 4. Risks

- **Avalonia 11 under Native AOT (T3)** — highest uncertainty. Fallbacks: non-AOT
  self-contained host, or console-AOT wrapper launching the host as a child
  process. Decide via a spike before committing T3, record as an ADR.
- **Installer size** — a wizard-bearing host is several MB before payload. Set and
  gate a target in CI.
- **Native zstd under AOT (T6)** — verify static/bundled linking; isolate behind
  the packager interface if problematic.
- **Dual scope in v1 (T12)** — the biggest scope-add of this revision. Every
  path/hive/PATH decision doubles its test matrix; the elevation relaunch is
  fiddly (arg forwarding, exit-code propagation from the elevated child). If it
  slips, per-user-only is the acceptable fallback — per-machine is the mode that
  needs UAC work, and per-user is the mode that needs no prompt.
- **Self-deleting uninstaller (T15)** — Windows offers no clean primitive;
  the `%TEMP%` relaunch trick needs care around races and AV heuristics.
- **Windows-only pack for `--format exe` (T4)** — `BeginUpdateResourceW` has no
  cross-platform equivalent; cross-compiling installers from Linux/macOS CI is
  out until a PE resource writer replaces it.

---

## 5. Out of scope

`sigil publish` (GitHub Releases) and the delta-update SDK (zstd dictionary-mode +
Ed25519 client verification) are separate tracks. Only the shared zstd codec (T6)
touches them; keep the codec factored so the update engine can reuse it.
