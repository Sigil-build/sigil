# Changelog

All notable changes to Sigil are documented in this file. The format is based
on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and Sigil aims to
follow [Semantic Versioning](https://semver.org/) once it reaches `1.0`.

Until then, **every `0.x` release may contain breaking manifest-schema or
CLI-flag changes** — see "Known limitations" below.

## [0.1.0-alpha] — Unreleased

The first public release. Sigil packs, signs, and installs real Windows
software from a single `sigil.yaml`; it has not yet been run in production
outside its own test suite. See "Known limitations" before you rely on it.

### Changed — licence

**Sigil is source-available, not open source.** It was MIT-licensed until
2026-09-14 and now ships under the **Sigil License 1.0**
(`docs/architecture/adr-016-licensing.md`).

- You may use Sigil for anything, **including commercially**, on any number of
  machines.
- The `Setup.exe` and packages it generates are **yours to distribute
  royalty-free** to as many users as you like. `sigil pack` stamps the installer
  host, which invalidates any prior signature by design, so a `Setup.exe` carries
  *your* signature — applied by `sigil sign` after packing — not ours.
- You may **not** copy, modify or reuse Sigil's source, or build a competing tool
  from it.
- Third-party notice obligations travel with the installers you ship: the Skia,
  HarfBuzz and ANGLE binaries embedded in a `Setup.exe` carry attribution
  requirements the licensor cannot waive on your behalf. Keep
  `THIRD-PARTY-NOTICES.md` with what you distribute.
- Code contributions are closed; bug reports and security reports are not. See
  `CONTRIBUTING.md`.

Versions published before 2026-09-14 remain under the MIT grant they were
released with. That grant is not revoked and does not extend to this release.

### Changed — breaking manifest changes

Each of these was a field that validated and then did something other than what
it said. They are refused now rather than silently mismapped, so a manifest that
used them fails validation instead of behaving unexpectedly.

- **`on_failure: fail` is removed from journalled phases** (`install_steps`,
  `pre_install`, `post_install`, `uninstall`). It had always behaved identically
  to `rollback` — there has never been an abort-without-rollback mode — so it is
  refused with **`SIG0233`**, and the default for those phases is now `rollback`.
  In an `installer.hooks.*` phase `fail` remains correct and remains the `pre_*`
  default; there, `rollback` is refused instead, because a hook runs outside the
  journal and has nothing to unwind.
- **`installer.brand.primary_color` / `accent_color` are removed.** The schema
  accepted them and the parser read only `primaryColor` / `accentColor`, so a
  snake_case manifest validated, packed, and shipped a wizard in Sigil's default
  grey and blue with no diagnostic anywhere. Use the camelCase spellings.
- **`installer.brand.logo` / `hero` and `installer.license` paths resolve against
  the manifest file's directory, not `build.source`.** `installer.icon` already did,
  so the same manifest resolved its icon and its logo from two different places and
  a manifest-relative logo was silently dropped. A declared `logo` or `hero` that
  cannot be read now packs with the non-fatal warning **`SIG0251`**. Move the files
  or rewrite the paths relative to the manifest.
- **`installer.hooks.*` accepts four more step types** — `http_download`,
  `ini_write`, `json_edit`, `xml_edit`. Not new capability: the engine always
  accepted them and only the hook schema omitted them, so manifests using them
  were refused for no reason.
- **Signing diagnostic codes are renumbered** into a `SIG04xx` band.
  `SIG0200`/`SIG0210`/`SIG0220`/`SIG0300`/`SIG0301` collided with
  manifest-validation codes — one number naming two unrelated failures, so
  neither could be documented at its own URL. Packaging failures likewise moved
  to a `SIG01xx` band.

### Added — packaging and signing (pre-installer-track foundation)

- `sigil validate` / `sigil init` / `sigil pack` / `sigil sign` CLI commands
  over a declarative `sigil.yaml` manifest, with JSON-schema validation and
  `SIG0xxx` diagnostics.
- ZIP packaging backend (flat `{out}/{App.Id}-{Version}-{arch}.zip`) and MSIX
  packaging backend.
- Local Authenticode signing (`signtool`) and Azure Trusted Signing, with a
  signing audit log.

### Added — wizard-driven `.exe` installer (the T1–T18 track)

- The `exe-wrapper` packaging backend (selected with `package.formats: [exe]`
  in the manifest — `sigil pack` has no `--format` flag): a
  self-contained, Native-AOT `Setup.exe` that stamps an app-specific payload,
  brand assets, and an install-step blob into a shared installer-host
  runtime via Win32 resource updates.
- A shared install engine (`SigilBuild.Wrapper.Core`) used by both the
  silent console wrapper (`/S`, `/silent`) and the Avalonia wizard UI
  (`SigilBuild.Installer.Host`), so both paths execute the identical step
  sequence.
- A rollback journal: a failed or cancelled install unwinds every completed
  step in reverse, including registry writes, files, shortcuts, and
  registered COM servers.
- The install-step catalog — 18 types, closed — built out incrementally across
  the P-track below on top of the initial set (`file_copy`,
  `directory_create`, `registry_write`, `shortcut_create`, `env_set`, and
  others) — see `docs/guides/install-steps.md` for the full list. Deploying
  the uninstaller is an engine action, not a step type.
- Two-scope installs (`/allusers`, `/currentuser`) with scope-correct ARP
  (Add/Remove Programs) registration, PATH updates, and shortcut placement.
- A generated `uninstall.exe` per install, driven by the same rollback
  journal.
- Brand-aware wizard chrome: `SigilBuild.Installer.BrandGenerator` derives a
  light/dark palette from two manifest-supplied colors at pack time.
- Manifest-driven localization of wizard chrome
  (`SigilBuild.Localization.Generator`, a source generator — no reflection).

### Added — feature-parity track (P0–P13)

Each item below shipped as its own reviewed increment; the P-number and gap
ID (`Gn`) are the track's own internal references, kept here for traceability
against the pull requests that landed them:

- **P0** — ADR-008: the expression, variable, and `when:`-clause
  extensibility policy that every later step type builds on.
- **P1** (G1) — Declarative data retrieval and cross-step variables.
- **P2** (G2/G4) — Lifecycle hooks and run-after-install launch (`/launch`).
- **P3** (G3) — Version-aware install: upgrade-in-place, repair, and
  downgrade-block (with `/force-downgrade` to override).
- **P4** (G5) — `http_download` install-time step, with SHA-256 and
  Authenticode verification before execution.
- **P5** (G6) — First-class prerequisite units: detect → install →
  re-detect, including MSI-style exit code 3010 (reboot required) handling.
- **P6** (G7/G17) — Files-in-use handling via Windows Restart Manager
  (`/closeapps`), and a setup single-instance mutex.
- **P7** (G8) — `/LOG[=path]` install logging, for both silent and wizard
  modes.
- **P8** (G9) — `ini_write`, `json_edit`, and `xml_edit` config-file steps.
- **P9** (G10) — Localized wizard chrome driven by manifest-supplied
  translations (the Localization.Generator above).
- **P10** (G11) — App-defined custom components, an Inno Setup `[Tasks]`
  equivalent.
- **P11** (G12–G14) — System-level steps: `scheduled_task_create`,
  `com_register`, `firewall_rule`.
- **P12** (G15/G16) — The update engine: a signed channel manifest,
  `/Update`, and a `--payload web` (web-installer) packaging mode.
- **P13** — Verification sweep closing the track: gate proofs, the P11 VM
  test legs, and status reconciliation across the whole P0–P12 set.

### Security — hardening landed ahead of this release (Stage 1 of the RC track)

The rows below were found by a pre-release security audit and fixed before
`0.1.0-alpha` was cut, not discovered in the wild. The `R` numbers are the
audit register's own ids; the register is kept in a private development
repository, so they are provenance here rather than a link you can follow:

- **R1, R2, R19** — Machine-scope install state is hardened: journal replay
  is anchored to `install_dir` and registered subtrees, the machine-scope
  probe no longer trusts unverified state, and hostile/tampered state fails
  closed instead of proceeding.
- **R3, R9, R16, R31, R32** — Every privileged step's destination
  (`install_dir`, `/D=`, registry coordinates, directory creation) is
  contained to the install scope; targets outside it are refused rather
  than silently followed.
- **R4, R5, R10, R11, R12, R17** — Downloaded and cached binaries (updates,
  prerequisites) are re-verified immediately before launch, staged in an
  admin-only randomly named directory, capped in size, and gated on
  Authenticode verification; a pre-planted native-runtime cache is no
  longer trusted implicitly.
- **R6, R21, R22** — Every CI test skip is now a real, actionable
  `Assert.Skip`, not a silent early return; per-assembly coverage floors are
  enforced; and all three VM-gated CI jobs fail loudly instead of passing
  vacuously when their preconditions are absent.

The **Known limitations** section below is the public list of what remains —
rows that are still open, and behaviour you should know about before relying
on this release. The full audit register is internal.

### Security — network trust and the download policy (Stage 2 of the RC track)

Landed in [#28](https://github.com/Sigil-build/sigil/pull/28). Read the first
item before you publish an update channel: it is a **breaking change** to the
channel-manifest wire format.

- **R13 — the channel manifest now carries freshness and replay protection,
  and three of its fields are newly REQUIRED.** A signature proves *who* minted
  a document, never *when*, so a correctly signed manifest could be replayed
  indefinitely to freeze a fleet on a version with a known vulnerability.
  `issuedAt`, `expiresAt` and `sequence` are now required, inside the signed
  byte range, and the client enforces a ±5-minute clock-skew tolerance, a
  30-day maximum age measured from `issuedAt` **independent of `expiresAt`**,
  and a persisted monotonic `sequence` high-water mark per app and scope. See
  `docs/architecture/adr-011-update-manifest-freshness.md`.
  - **Migration:** a manifest built to the previous five-field shape is
    rejected as malformed (`SIG0320`) by every shipped installer and `/Update`
    exits `7`; a manifest that is well-formed but stale, future-dated or
    replayed exits `8`. Re-mint and re-sign before `expiresAt` lapses even when
    the advertised version has not changed, and increment `sequence` on every
    publish.
- **R45 / R46 — `installer.require_signed_downloads`, a new manifest field.**
  Whether a binary pulled off the network is Authenticode-verified before it is
  launched was previously *inferred* from whether the publisher had configured
  signing for their own output — a different question. It is now declared:
  `sign_declared` (the default, preserving existing behaviour), `always`, or
  `always_verified_revocation`, which additionally refuses a binary whose
  revocation status could not be established. When the check is not armed, the
  log says so rather than passing silently.
- **R39 — verify, then parse.** The channel manifest's signature is now checked
  before its JSON is parsed at all, so unverified network input never reaches
  the parser and whoever answers the request no longer chooses which diagnostic
  the user sees.
- **R8 / R14 / R30 — HTTPS and key hygiene at pack time.** A
  `parameters.*.source.url` that is not `https://` is now `SIG0323`; an
  `updates.manifestUrl` that is not `https://` is `SIG0324`; and an
  `updates.signingKey` that is not a base64 X.509 SubjectPublicKeyInfo — a file
  path, or a *private* key — is `SIG0325`. `sigil init`'s own template
  previously demonstrated the private-key mistake.
- **R37 — a malformed installed version no longer skips the `minFromVersion`
  floor.**
- **New diagnostics:** `SIG0323`, `SIG0324`, `SIG0325`, `SIG0326`
  (`require_signed_downloads` given an unrecognized value).

### Security — secrets across the elevation boundary

- **R18 — secret parameter values no longer travel on a process command line.**
  A per-machine install started unelevated relaunches itself under UAC, and
  used to forward `/PName=Value` verbatim — publishing the value to every
  process-creation auditor on the machine (Sysmon, EDR agents, WMI
  `Win32_Process`, Task Manager's command-line column). Secrets now cross that
  boundary in a DPAPI-protected, ACL-restricted, delete-after-read handoff
  file; the relaunch command line carries only a path to it, via the reserved
  `/SecretHandoff=<path>` token.

### Fixed — install and uninstall correctness (Stage 3 of the RC track)

- **R58** ([#39](https://github.com/Sigil-build/sigil/pull/39)) — the ARP
  `UninstallString` could not complete: the files-in-use gate counted the
  running uninstaller itself as a blocker, so an uninstall launched from
  Add/Remove Programs refused on its own pid.
- **R75** ([#44](https://github.com/Sigil-build/sigil/pull/44)) —
  `com_register` journaled an undo for a registration that never took effect,
  so one failed step could leave the app permanently unremovable. The undo is
  now withdrawn when the DLL fails to load or exports no `DllRegisterServer`.
- **R76** ([#46](https://github.com/Sigil-build/sigil/pull/46)) — every
  unelevated per-user upgrade failed with exit `5`: the installer's
  single-instance guard rejected the prior version's `uninstall.exe` that the
  upgrade itself spawns, because that child derives the same app+scope mutex
  name. It is now admitted through an explicit `SIGIL_SETUP_LOCK_HANDOFF`
  token, validated against the real parent pid and its creation time. A second
  `Setup.exe` launched by hand is still refused.
- **R66–R70** ([#40](https://github.com/Sigil-build/sigil/pull/40),
  [#41](https://github.com/Sigil-build/sigil/pull/41),
  [#42](https://github.com/Sigil-build/sigil/pull/42),
  [#45](https://github.com/Sigil-build/sigil/pull/45)) — the VM
  install/uninstall matrix could not actually run: rotted fixtures, P11 legs
  targeting a path the containment rules refuse, a P12 job that could never
  build (MSB1008), and a kiosk-sample path that walked outside the repo. The
  matrix now runs green, and its fixtures are validated in every CI run.

### Added — release mechanics and CI

- **Signed, tag-triggered releases.** `release.yml` fires on a `v*` tag, runs
  the full VM matrix first, AOT-publishes `win-x64` (and `win-arm64`
  best-effort — that leg is `continue-on-error` and warns that the release may
  ship x64 assets only if the runner image stops provisioning the ARM64 C++
  build tools), signs with
  Azure Trusted Signing, generates a **CycloneDX SBOM**, emits **`SHA256SUMS`**,
  and publishes a GitHub prerelease. What is missing for a first release is a
  pushed tag, not the automation.
- **A docs-only CI gate.** `ci.yml`'s cheap `changes` job decides whether the
  expensive build, AOT-publish and vulnerability-scan jobs need to run. It is
  gated at the *job* level, never with a workflow-level `paths:` filter, so a
  skipped job still reports "skipped" and satisfies a required status check —
  and it **fails closed**: only a positive docs-only verdict skips the build.

### Fixed — release mechanics (this stage, R7/R23/R23a/R24)

- **R24** — The version string was duplicated across four files with
  nothing reconciling them (`SigilBuild.Cli.csproj`, a hand-maintained
  `const` in `Program.cs`, a test literal, and a CI smoke-test literal).
  There is now exactly one source of truth
  (`Directory.Build.props` → `AssemblyInformationalVersionAttribute`, read at
  runtime); `sigil --version` is asserted against it, not against a literal.
- **R7** — CI's AOT-publish artifact previously uploaded only
  `sigil.exe` on its own. The AOT output is not single-file: `sigil.exe`
  needs `libSkiaSharp.dll` and `libsodium.dll` beside it (logo resizing and
  ZIP-manifest signing) and throws `DllNotFoundException` without them.
  Both `ci.yml` and the new `release.yml` now upload the whole publish
  directory (minus `.pdb` files).
- **R23a** — The build was not reproducible: no lockfiles, and no
  `NuGet.config` meant the feed set was inherited from the machine rather
  than declared by the repo. `RestorePackagesWithLockFile` is now on,
  `packages.lock.json` is committed per project, `NuGet.config` pins
  nuget.org as the only feed, and CI restores with `--locked-mode`.

### Fixed — what the clean-machine run found (R7)

Both of these were found by running the release artifact on a fresh Windows VM
with no developer tooling, which is the one thing a checksum and a green test
suite cannot do for you. Both had been in the repository for months.

- **The installer wizard had never opened — on any machine.**
  `Assets/default-logo.png` and `Assets/default-hero.png` carried a corrupt
  IDAT chunk CRC. Every other byte was valid, so the files looked fine to
  anything that did not decode them; libpng inside Skia checks chunk CRCs and
  refuses, and the refusal arrived as
  `ArgumentException: Unable to load bitmap from provided data` thrown out of
  `XamlIlPopulate` — before `InstallerWindow` finished construction, so the
  process died with no window and no message. `UninstallWindow` and
  `UpdateWindow` bind the same asset and were dead the same way. Only `/S`
  (silent) installs worked.

  The test suite could not see it: `TestAppBuilder` configures
  `UseHeadless(new AvaloniaHeadlessPlatformOptions())`, whose
  `UseHeadlessDrawing` defaults to `true`, and that platform's `LoadBitmap`
  returns a stub **without parsing the bytes**. Five test classes construct
  these windows and all five stayed green. The new guard
  (`ShippedAssetDecodeTests`) resolves every `avares://installer/Assets/*`
  exactly as the XAML does and decodes it with SkiaSharp directly, so the
  drawing stub is out of the loop.

- **`sigil pack` produced installers with no payload, and reported success.**
  A `build.source` naming a directory that is not on disk was never checked:
  `ExeWrapperPackager.BuildPayloadBytes` returned an empty array,
  `WrapperResourceWriter` then skipped the `SIGIL_PAYLOAD_V2` stamp entirely,
  and `pack` printed an artifact path and exited `0`. The first sign of trouble
  was on the end user's machine — a failed step and a rollback reading
  `'payload://' source used but no payload was extracted for this run`.

  `pack` now refuses with **`SIG0122`** before writing anything, naming both
  what you wrote and what it resolved to, and stating the rule that decided it
  (relative paths resolve against the manifest's directory, not the working
  directory). The output directory is no longer created for a refused pack.

### Fixed — two ways an install could quietly do the wrong thing

- **An empty `build.source` produced an installer that lied.** `SIG0122` already
  refuses a source directory that is not on disk. An *empty* one packed, ran,
  reported success and registered the application in Add/Remove Programs, having
  laid down nothing but its own uninstaller — an installed application
  containing nothing, which is much harder to notice than a failure.

  `pack` now refuses with **`SIG0123`** when the manifest resolves `payload://`
  and no payload was packed. The condition is deliberately about `payload://`
  and not about emptiness: an installer that legitimately carries no payload —
  one that only writes registry values, or a `--payload web` stub — still packs.
  It is checked against the serialized steps rather than a list of step types,
  so a step added later cannot fall outside it.

  A refused pack also no longer leaves the copied-but-unstamped installer host
  behind in the output directory.

- **Uninstalling left the directory skeleton behind.** `file_copy` created the
  destination tree and journalled only the files in it, so nothing recorded that
  the install had made those directories — rollback and uninstall both left them
  in place, empty, permanently.

  Each directory the step actually creates is now journalled, parents included,
  and removed on rollback or uninstall. Directories that already existed are
  deliberately not recorded: removing one the user had is a worse mistake than
  leaving one behind. Removal is conditional on the directory being empty, so
  anything the application wrote there afterwards keeps it alive.
### Fixed — what walking the wizard found (R7, step 4)

The install wizard had never been opened by a person. The corrupt-asset defect
above is why; once it rendered, a single walkthrough of the four screens found
everything below. Three of these are not cosmetic.

- **The OS close gesture was not handled at all.** Nothing listened for
  `Closing`, so Alt+F4 during an install tore the wizard down mid-run — no
  confirmation, no cancellation of the engine, no rollback. The window had no X
  to click, which is the only reason it went unnoticed. The X, Alt+F4 and the
  taskbar's Close now reach the same decision as the Cancel button.
- **A finished install trapped the user.** The footer showed Back / Next /
  Cancel on every screen; on the Finish screen Back and Next are disabled and
  `CancelAsync` returns `false` by design — "install completed, nothing to
  cancel". So the only enabled button did nothing, and the window could be
  closed only from the taskbar or Task Manager. `UninstallWindow` and
  `UpdateWindow` had a terminal Close button already; the install wizard never
  got one. The view model's `CanCancel` existed, was maintained, was covered by
  a test — and was bound to nothing.
- **`installer.brand.logo` was packed and ignored.** The logo and hero travel
  from the manifest into the wrapper blob and on into `BrandTokens`, where no
  view read either: all three windows bound their image to the bundled default
  by a literal `avares://` path, and no window set an icon at all. A declared
  brand logo now renders in the rail, the title bar and the taskbar; an
  undecodable one falls back to the default rather than throwing, because a
  decorative image is not worth failing an install over.

Also fixed, all of it visible on first use: no control ever changed the mouse
cursor; the window had no title bar and so could not be moved; the primary
button used the accent colour beside a rail of a different colour; cancelling
asked for confirmation only while files were copying, so Cancel on any earlier
screen silently discarded a chosen destination or a typed licence key; and the
confirmation dialog had no visible frame against the content behind it.

The dialog's own text claimed "files already copied will be removed during
rollback", which stopped being true once it appeared before copying starts. It
now reads "Cancel setup? Any files already copied will be removed", in both
shipped languages.

### Fixed — installing and uninstalling over a running app (R93–R95)

Installing a real tray application — a GUI plus a background process the GUI
restarts — over a running copy, and uninstalling it, found three defects.

- **R93 — Uninstall in Apps & features did nothing while the app was running,
  and left no trace.** The registered `UninstallString` carried `/S`, so the
  button ran the silent path. That path checked for files in use *before* running
  the manifest's `pre_uninstall` hooks, so the hook that would have stopped the
  app (its own `--quit`) never ran; the run exited `4` with no window, and with no
  `/LOG` it wrote no log either.
  - `installer.hooks.pre_install` and `pre_uninstall` now run **before** the
    files-in-use check. `pre_install` therefore also runs before prerequisites
    and before an upgrade removes the previous version — while the previous
    version's files are still in place. In the wizard the `pre_install` hooks run
    when Next is pressed into Installing, before the Close-applications screen
    would be shown, and never a second time in the same run. A hook naming a
    program under `{install_dir}` fails on a fresh install as it always did; give
    it `on_failure: continue`.
  - `UninstallString` is now the interactive uninstall (`/Uninstall <scope>`, no
    `/S`), and a new `QuietUninstallString` carries the `/S` form for scripts and
    deployment tooling.
  - A run with no `/LOG` now writes its run log into the always-on diagnostic
    log, `%TEMP%\sigil-wizard-<timestamp>-<pid>.log`, on the silent path too, so a
    refused run names its blockers somewhere.
- **R94 — "Close for me" froze the wizard.** The Restart Manager shutdown ran on
  the UI thread and blocked for as long as it waited on the applications; it then
  showed the same list as if nothing had been tried. It now runs in the
  background with a "Closing applications…" notice and Retry / Close for me
  disabled, re-scans, and says Setup could not close what is still running.
- **R95 — the Installing screen never rendered.** The install started on the UI
  thread, and its first act is reading and decompressing the whole embedded
  payload, so the window froze until the install was nearly done and then jumped
  to Finish. The install, uninstall and headed `/Update` runs now start on a
  background thread; progress still reaches the window as before.

### Security — the wizard loads its native libraries only from the verified cache (R102)

- **A `libSkiaSharp.dll`, `libHarfBuzzSharp.dll` or `av_libglesv2.dll` beside
  `Setup.exe` was loaded into the installer** — elevated after UAC, and after the
  machine-scope relaunch — instead of the verified copies extracted from the
  installer itself. The directory the exe runs from answered before the
  administrator-only native-runtime cache (R4) was ever consulted, so anything
  planted next to a downloaded `Setup.exe` ran as administrator. The same search
  order made the interactive uninstall of an Avalonia application fail:
  `uninstall.exe` loaded the application's own copies of those three DLLs from the
  install directory and then could not delete them.
  - The GUI path now takes the application directory off the process DLL search
    path entirely: the default set is System32 plus the verified cache
    (`LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_USER_DIRS`).
  - Skia, HarfBuzz and ANGLE are loaded from the cache **by full path** straight
    after it is verified, and .NET's native-library resolution for SkiaSharp,
    HarfBuzzSharp and Avalonia's ANGLE import is pinned to those handles, so the
    runtime's own probing — which tries the exe's directory first — never runs for
    them.
  - A library missing from the cache, or one the loader rejects, refuses to start
    with exit `78`, exactly as an unverifiable cache does. There is no fallback to
    loading it by name.
  - The silent path is unchanged: it never loads these libraries.

### Fixed — the uninstall window (R103, R104)

- **R103 — the interactive uninstall never checked for running applications.**
  Only the silent path ran the files-in-use check, and since R93 Apps & features
  opens the interactive uninstall, so every user took the unchecked path. An app
  daemon still running after the publisher's `pre_uninstall` `--quit` hook sent
  the uninstall straight into the journal replay, which failed on every file the
  daemon held and told the user to "re-run the uninstaller after closing the
  application" without naming it. The interactive uninstall now runs the
  `pre_uninstall` hooks, then the files-in-use check, before anything is removed
  — the silent path's order — and the uninstall window gains the install
  wizard's Close-applications step: it lists each blocking application with its
  process id, Retry re-scans, Close for me asks the applications to shut down in
  the background and says so if any are still running, and Cancel exits `2` with
  nothing removed. Once they are closed the uninstall continues without running
  the hooks a second time.
- **R104 — the uninstall window had no title bar.** It and the headed `/Update`
  window were frameless (`BorderOnly`), so they had no title, no X, and could
  not be moved; the install wizard had already been given its title bar. All
  three windows now have the same title bar and can be dragged by the rail. In
  the uninstall window the X means Cancel before anything is removed, closes on
  Done or Failed, and is refused while the uninstall is removing files.

### Fixed — a packed installer's file properties named Sigil's host (R105)

- **R105 — every packed `Setup.exe` carried the installer host's own version
  resource.** `sigil pack` stamped the payload and the icon but left the host's
  `RT_VERSION` in place, so Explorer's Properties → Details on a publisher's
  installer (and on the `uninstall.exe` copied from it) showed the description
  and product name `installer`, the original file name `installer.dll`, Sigil's
  version and Sigil's copyright. Pack now replaces it with one built from the
  manifest: file description `<app.name> Setup`, product name `app.name`,
  company `app.publisher`, file and product version the full `app.version`,
  original and internal name the produced file's name, and a numeric version of
  `major.minor.patch.0` from the SemVer core (each part clamped to 0–65535).
  Both the full package and the `--payload web` stub get it, before signing.
- **New optional manifest field `app.copyright`** — the installer's
  LegalCopyright. When it is omitted the field is left empty.

### Fixed — wizard polish (R98, R108, R109)

- **R98 — an upgrade forgot the options chosen at install.** 0.5.0 installed
  with Add to PATH and a custom component ticked; the 0.5.1 upgrade's Options
  screen showed both back at their manifest defaults, and a silent upgrade
  applied the defaults. An install now records the resolved value of every
  applicable option component in its uninstall state (`uninstall.json`, an
  additive `options` field — older state still loads and simply has none). An
  upgrade, forced downgrade or same-version reinstall reads it back before the
  prior version is torn down and uses it as the Options screen's starting values
  and the silent path's values. A `/P` override still wins, a `locked` component
  keeps its manifest default, a component the new version dropped is ignored and
  one it added starts at its default.

- **R108 — the location screen read "Free space on drive C:: …".** The catalog
  string supplies the colon and the view model passed the drive root with its
  own. The drive is now passed as its bare letter; a UNC share is shown as-is.
- **R109 — the step rail never marked a step as done.** A completed step looked
  like a pending one apart from its opacity, and on the Finish screen nothing
  said the steps had gone through — the rail still showed Install as current.
  A done step now shows a check mark in place of its bullet; the current step
  keeps the full-strength bullet and bold label, and pending steps stay muted.
  On Finish every step shows done; on the Failed screen the steps before the
  install show done and Install shows a cross. The marks are drawn vector paths,
  so they do not depend on a font carrying the glyph.

### Fixed — what the uninstaller leaves behind (R106, R107)

- **R106 — `uninstall.exe` stayed in the install directory until the next
  reboot.** A running program cannot delete its own executable, so after a
  successful uninstall the install folder still held one file — the uninstaller,
  a full copy of `Setup.exe` — scheduled for deletion at a restart a per-user
  uninstall was not even allowed to schedule. `uninstall.exe` now does what the
  NSIS and Inno Setup uninstallers do: it copies itself into a fresh private
  directory (`%ProgramData%\sigil-uninstall-<guid>`, administrator-only, when
  elevated; a private `%TEMP%\sigil-uninstall-<guid>` otherwise), verifies the
  copy against its own bytes from a handle that blocks replacement, runs the
  uninstall from the copy with the same privilege and the same arguments, waits
  for it and exits with its exit code. The copy moves the original out of the
  install directory, so the directory is removed with the rest of the uninstall,
  and the private directory is cleaned up or scheduled for the next restart.
  - The interactive uninstall waits too, so Apps & features refreshes its list
    only once the app is gone.
  - An upgrade's removal of the previous version keeps working: the previous
    version's `uninstall.exe` hands the single-instance guard it was admitted on
    to its copy. An `uninstall.exe` from before this change uninstalls in place,
    as it always did.
  - `uninstall.exe` now moves its current directory off the install directory
    before doing anything else, and resolves a relative `/LOG=` first.
  - When no copy can be made — or the install and temporary directories are on
    different volumes — the uninstall runs in place, as before.
  - New reserved token `/RelaunchedFrom=<path>`, passed only from the uninstaller
    to its own copy; see `docs/setup-exe-reference.md`.
- **R107 — a leftover `uninstall.exe` reported a raw failure.** Running the
  uninstaller of an app that was already removed opened the uninstall window on
  **Uninstall failed** with `no uninstall state found for '<AppId>' (expected at
  …\uninstall.json)`. The app is simply not installed: the window now opens on a
  neutral "<App> is not installed" screen (English and Ukrainian) and closes with
  exit `0`, and the silent path says so, logs `result: not installed` and exits
  `0`. No hook runs. State that exists but is refused still fails as before (R1).

### Fixed — reinstalling (R112)

- **R112 — running the same `Setup.exe` again moved the app to the default
  folder.** 0.5.6 was installed per-user into a folder the user picked
  (`…\Programs\Dictum12`), and the 0.5.7 upgrade correctly stayed there; running
  the 0.5.7 `Setup.exe` a second time opened the location screen on the default
  `…\Programs\Dictum`, removed the copy in `Dictum12` and installed into
  `Dictum`. A same-version reinstall now keeps the recorded install directory as
  its default exactly like an upgrade or forced downgrade: the location screen
  opens on it, a silent run installs into it, and `{install_dir}` expands to it.
  `/D=` and a folder picked in the wizard still win, and a reinstall into a
  different scope (`/allusers` over a per-user install) still uses the new
  scope's default. Picking a different folder on a reinstall moves the app, the
  same way it does on an upgrade.

### Known limitations

> **Sigil 0.1.0-alpha is Windows-only and pre-production.** It builds and
> installs real software, but it has not yet been run at scale outside its
> own test suite. Do not use it to ship an installer to end users you cannot
> reach with a correction.
>
> - **Windows only.** The pack host must be Windows (`BeginUpdateResourceW`
>   has no cross-platform equivalent), and the produced installers are
>   Windows-only. There is no macOS or Linux story, now or planned for v1.
> - **Delta updates are not implemented.** `/Update` performs full-package
>   updates. The zstd-dictionary delta format and the client SDK described
>   in earlier planning material are deferred — see
>   `docs/architecture/adr-010-delta-update-deferral.md`.
> - **Channel manifests are on a clock.** Freshness and replay protection now
>   ship (R13 / ADR-011), which closes the stale-content suppression gap — but
>   it means an update channel needs maintaining: a manifest whose `expiresAt`
>   has passed, or that is more than 30 days old, stops being acted on even
>   though its signature still verifies. Re-mint and re-sign on a cadence
>   comfortably shorter than the expiry you choose.
> - **Machine-scope installs require care with `install_dir`.** Installing
>   to a directory writable by non-administrators is refused; do not work
>   around it.
> - **`directory_create` now enforces install-scope containment.** Any
>   directory a manifest creates outside `install_dir` (for example, a
>   shared `%ProgramData%` location) must opt in explicitly with
>   `allow_outside_install_dir` — 11 existing internal fixtures needed this
>   opt-out when the containment check landed. If your manifest creates
>   directories outside the app's own install tree, expect to add this flag
>   rather than have the install fail partway through.
> - **`com_register` runs the publisher's `DllRegisterServer` inside the
>   elevated installer process.** A faulty DLL takes the installer with it.
> - **Prerequisite and update payloads are verified by SHA-256 and
>   Authenticode before execution**, but Sigil cannot vouch for what a
>   third-party redistributable does once it runs.
> - **One pre-release dependency.** The CLI uses a `System.CommandLine` beta
>   (`2.0.0-beta4.22272.1`, from September 2022) that is unlikely to receive a
>   security fix; it is tracked for re-evaluation once a stable alternative
>   satisfies the same constraints. (The rendering stack's `SkiaSharp` preview
>   pin is gone — Avalonia.Skia 12.0.5 dropped its dependency to the stable
>   `3.119.4` release, so no preview native binary ships in privileged
>   software. Recorded as SUP.4 / R42.)
> - **An elevated per-user install can silently downgrade an existing one.**
>   When an administrator runs a per-user install, scope resolution probes
>   HKLM and does not see the prior per-user install, so the run plans a
>   fresh install and the downgrade guard never fires — while the reinstall
>   cleanup, which reads the state store rather than Add/Remove Programs,
>   tears the newer version down anyway. Reproduced, not inferred. It is
>   confined to sessions where the user already holds Administrator;
>   installing per-user from an ordinary session is unaffected.
> - **Machine-scope installs are not covered end to end.** Every hosted CI
>   runner is already elevated, so the VM matrix cannot exercise the
>   unelevated paths that matter most for scope resolution, and two upgrade
>   assertions are honest skips for the same reason. The behaviour is
>   unit-tested; what is missing is the real-machine leg.
> - **Parameter-level `screen:` grouping does nothing.** The field parses,
>   validates and is documented, and the wizard renders every parameter on a
>   single Install Options page regardless. Declaring it is harmless and
>   pointless.
> - **The wizard renders its "Upgrading from x.y.z" banner twice** on the
>   Install Options screen. Cosmetic.
> - **Schema validation is weaker than it reads.** `additionalProperties` is
>   applied as a boolean gate only, so where the schema uses its *subschema*
>   form to constrain open-ended maps — parameter declarations, localized-text
>   maps — those values are not schema-checked. The parser catches the cases
>   that matter (a non-scalar localized value is `SIG0292`), but do not treat
>   `sigil validate` as the whole gate.
> - **`docs/guides/uninstaller.md` has drifted** from the Add/Remove Programs
>   entry and uninstaller the code actually produces. Trust
>   `docs/setup-exe-reference.md` and the observed behaviour over that page
>   until it is rewritten.
> - **Native AOT publish is Windows-only.** `release.yml`'s signed,
>   checksummed release path is tag-triggered and runs on `windows-latest`;
>   it cannot be exercised from a non-Windows development environment. It was
>   run end to end for the first time on 2026-09-16, against a throwaway tag.
> - **The release archive is large: ~47 MB compressed, ~116 MB expanded.**
>   Each per-architecture zip carries the installer-host runtime for **both**
>   `win-x64` and `win-arm64`, because a manifest may declare
>   `architectures: [x64, arm64]` and `sigil pack` needs the runtime for every
>   target it produces. Deliberate, not bloat — but budget for it in CI caches.
> - **Coverage:** CI enforces a project-wide union floor of **77%** as of this
>   release, plus four **hard per-assembly floors** — `SigilBuild.Core` 69%,
>   `SigilBuild.Signing` 68%, `SigilBuild.Wrapper.Core` 79%,
>   `SigilBuild.Packaging` 72%. They are a ratchet, set at the measured value
>   rounded down and raised as coverage rises; dropping any one of them fails
>   CI. `SigilBuild.Core` and `SigilBuild.Signing` sit furthest below their
>   aspirational targets and are the areas most likely to hold undiscovered
>   bugs.
> - **Report security issues privately** via `SECURITY.md`. Please do not
>   open a public issue for a privilege-escalation finding.
