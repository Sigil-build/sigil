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

The register rows below (`docs/plan/release/00-GAP_REGISTER.md`) were found
by a pre-release security audit and fixed before `0.1.0-alpha` was cut, not
discovered in the wild:

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

See `docs/plan/release/00-GAP_REGISTER.md` for the full register, including
rows not yet closed (tracked as known limitations below or left for a future
release).

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
  the full VM matrix first, AOT-publishes `win-x64` and `win-arm64`, signs with
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
> - **Native AOT publish is Windows-only and cannot be verified from every
>   environment.** `release.yml`'s signed, checksummed release path is
>   tag-triggered and only exercised by CI (`windows-latest`); it has not
>   been run end-to-end from every development environment used on this
>   project.
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
