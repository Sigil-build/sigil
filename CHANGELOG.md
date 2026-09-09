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

- The `exe-wrapper` packaging backend (`sigil pack --format exe`): a
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
- The install-step catalog, built out incrementally across the P-track below
  on top of the initial set (`file_copy`, `directory_create`,
  `registry_write`, `shortcut_create`, `env_var_set`, `uninstaller`, and
  others) — see `docs/guides/install-steps.md` for the full list.
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
ID (`Gn`) are the track's own references, kept here for traceability against
`docs/plan/feature-parity/01-IMPLEMENTATION_PLAN.md`:

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
> - **Update manifests are authenticated but not yet freshness-checked.** A
>   signed manifest is verified against a pinned P-256 key, but an attacker
>   who can serve stale content may be able to suppress an update. Serve
>   update manifests over HTTPS from infrastructure you control.
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
> - **Preview dependencies.** The rendering stack pins a preview `SkiaSharp`
>   build (`3.119.4-preview.1.1`) to satisfy an Avalonia 12 transitive
>   requirement, and the CLI uses a `System.CommandLine` beta
>   (`2.0.0-beta4.22272.1`, from September 2022) that is unlikely to receive
>   a security fix. Both are tracked for re-evaluation once a stable
>   alternative satisfies the same constraints.
> - **Native AOT publish is Windows-only and cannot be verified from every
>   environment.** `release.yml`'s signed, checksummed release path is
>   tag-triggered and only exercised by CI (`windows-latest`); it has not
>   been run end-to-end from every development environment used on this
>   project.
> - **Coverage:** roughly project-wide floor of 77% enforced in CI as of
>   this release; `SigilBuild.Core` and `SigilBuild.Signing` sit below their
>   aspirational targets (80% / 85%) and are the areas most likely to hold
>   undiscovered bugs.
> - **Report security issues privately** via `SECURITY.md`. Please do not
>   open a public issue for a privilege-escalation finding.
