# Plan: working `.exe` installer + wizard redesign

> **Status: SUPERSEDED (2026-07-09) by [`IMPLEMENTATION_SPEC.md`](IMPLEMENTATION_SPEC.md).**
> Kept as the historical record of the 2026-07-08 planning session. Do not
> execute from this document — the spec's T1–T17 replace the 8-phase sequence
> below and add decisions this doc predates: install scope + elevation (T12), a
> dedicated **destination screen** (T13 — the install-location field moves off
> the Options screen described below), license manifest backing (T14),
> `uninstall.exe` survivability (T15), MSIX-companion reconciliation (T16), the
> silent-mode `/Pname=value` + `/D=` contract, and brand tokens embedded in the
> WrapperBlob (gradient tokens removed outright, not deprecated).

Scope: make `sigil pack --format exe` produce a real, wizard-driven single-file
installer — the direct NSIS / Inno Setup / WiX replacement — and modernize the
installer wizard's design.

This document consolidates the decisions from the 2026-07-08 planning session.
It superseded the "Task 14 / Tasks 15+" TODO markers scattered in the code.

## Decisions locked this session

- **Wizard-first.** The `.exe` launches the branded Avalonia wizard by default
  (double-click → GUI). A `/silent` flag runs headless for CI. This requires
  merging the wrapper's step engine into the installer host (see restructure).
- **zstd payload from the start.** The embedded payload uses zstd (the codec
  already planned for delta updates), not Deflate/zip — one codec shared with
  the future update engine.
- **Refined side-rail wizard.** Keep a vertical brand rail, but flat (not the
  current dark gradient), with a real vertical step indicator, a signed-
  publisher trust line, lighter chrome, and a live step log on the install
  screen. See "Wizard redesign" below.
- **Manifest-driven flow.** The wizard renders exactly the steps `sigil.yaml`
  declares — no fixed welcome→license→options→install→done default. Steps that
  aren't configured don't appear.

## Current state (what already exists)

The exe-wrapper path is ~80% written and blocked on build wiring, not logic:

- `ExeWrapperPackager` — builds the `SIGIL_BLOB_V1` step/param JSON and a
  payload archive, then stamps both as Win32 `RT_RCDATA` resources. Complete.
- `WrapperResourceWriter` — Win32 `BeginUpdateResource` embed flow. Complete
  (Windows-host-only).
- `SigilBuild.Wrapper` — real step engine: file/dir/registry/shortcut/env/run
  steps, rollback journal, expression evaluator, ARP registration, uninstall
  engine. Complete, but **console-only, no GUI**.
- `SigilBuild.Installer.Host` — Avalonia wizard (welcome/license/options/
  custom/installing/finish) with brand-token theming. Complete UI, but wired
  to a **throwaway copy-loop engine**, not the real one.

The two engines are disconnected — that is the core problem wizard-first solves.

## Blocking gaps

1. **AOT runtime never built into the SDK (the "Task 14" blocker).**
   `WrapperRuntimeLocator.Locate()` expects `runtimes/win-x64/…exe` next to the
   packaging assembly; nothing publishes it there.
2. **`PackageFormat.Exe` throws by design** in `PackCommand` — one-line swap,
   gated on (1).
3. **Payload is never extracted.** `WrapperBlob.LoadPayloadBytes()` returns raw
   bytes; nothing unzips them and no `payload://` source resolution exists in
   `FileCopyStep`. A stamped `.exe` currently installs nothing.
4. **ARP registration uses placeholders** — `DisplayName=AppId`, version
   `"1.0.0"`, `Publisher="Unknown"`; `App.*` isn't threaded into `WrapperBlob`.
5. **Resource embed is Windows-only** — `pack --format exe` won't run from a
   Linux/macOS CI host without a managed PE-resource writer.

## The central restructure (do first)

Wizard-first means the **stamped runtime becomes the Installer.Host**, driving
the **real** step engine. Enabling refactor:

1. **Extract the engine into a class library.** `SigilBuild.Wrapper` is an
   `Exe`, so `Installer.Host` can't cleanly reference its engine. Move
   `Engine/`, `Steps/`, `Expressions/`, `Json/`, `WrapperBlob` into a new
   `SigilBuild.Wrapper.Core` classlib. Both `SigilBuild.Wrapper` (thin console
   exe = the `/silent` path) and `Installer.Host` reference it.
2. **Installer.Host as the runtime.** On launch: parse args → `/silent` runs
   `InstallEngine` headless (console, exit codes preserved for CI); otherwise
   show the wizard. The wizard's Install screen drives the *same* `InstallEngine`
   via an `IProgress<InstallProgress>` adapter. Delete
   `Installer.Host/Services/InstallerEngine.cs` (the fake copy loop).

## Implementation sequence

Ordered so each phase leaves `main` shippable.

1. **Engine split** — extract `SigilBuild.Wrapper.Core`; both exe + host
   reference it. Enabling refactor, no behavior change.
2. **Merge host + engine** — Installer.Host becomes the runtime; wizard Install
   screen calls the real engine; delete the fake `InstallerEngine`.
3. **AOT build wiring** — MSBuild target / CI step:
   `dotnet publish src/SigilBuild.Installer.Host -c Release -r win-x64
   -p:PublishAot=true` → copy into `runtimes/win-x64/` in the packaging output.
   Point `WrapperRuntimeLocator` at `Installer.Host.exe`. **Validate Avalonia 11
   AOT-publishes clean under `TreatWarningsAsErrors`** — highest-risk item.
4. **Flip the PackCommand switch** — `PackageFormat.Exe → new ExeWrapperPackager()`.
   Now `pack --format exe` produces a launchable wizard.
5. **Payload extraction + `payload://`** — extract embedded payload to a temp
   dir at install start; resolve `payload://rel/path` in `FileCopyStep`/
   `StepContext`; clean up temp on success and on rollback. Now it installs.
6. **zstd codec** — replace `ZipArchive`/Deflate in
   `ExeWrapperPackager.BuildPayloadBytes` with zstd (`ZstdNet` + native
   fallback); decompress on the extraction side. Keep deterministic: fixed
   level/dict, sorted entries, pinned mtime.
7. **Thread App fields into ARP** — add `DisplayName`/`Version`/`Publisher` to
   `WrapperBlob` from `manifest.App.*`; fix the placeholder `ArpRegistration`
   call. Correct Add/Remove Programs entry + uninstall.
8. **Tests** — un-skip `WrapperResourceWriterTests`; add a Windows-VM
   integration test (extend `wrapper-vm-tests.yml`): pack a fixture `.exe`, run
   `/silent`, assert files land + ARP entry present + uninstall reverses it;
   plus a headed smoke test that the wizard launches and drives the engine.

Milestones: **1→4** = launchable wizard-driven `.exe`; **5→6** = installs real
files; **7→8** = correct + verified.

## Risks

- **Avalonia + Native AOT (phase 3)** — highest uncertainty. Fallback: drop AOT
  for the host (self-contained JIT, larger exe), or keep a tiny console wrapper
  that launches the wizard as a child process.
- **Installer size** — a wizard-bearing AOT exe is several MB before payload
  (vs. the ~1 MB console wrapper). Set a target number and add a CI size gate,
  mirroring the CLI's 15 MB gate.

## Wizard redesign

Direction: **refined side rail**, **manifest-driven flow**. Fully themeable via
the existing `BrandTokens` (`AppName`, `Publisher`, `PrimaryColor`,
`AccentColor`, gradient stops, `LogoFile`, `HeroFile`).

Changes from the current `InstallerWindow.axaml`:

- **Rail:** replace the three-stop dark gradient with a flat brand color block
  (drive from `PrimaryColor`, accents from `AccentColor`). Add a **vertical step
  indicator** listing the manifest-declared steps with done/current/upcoming
  states. Keep logo + app name + publisher; add version + payload size at the
  foot of the rail.
- **Title bar:** custom slim bar with app name + minimize/close (keeps
  `WindowDecorations="BorderOnly"`).
- **Welcome:** surface the **signed-publisher line** ("Signed by <Publisher>")
  as an up-front trust cue — a concrete advantage over NSIS/Inno. Single primary
  action.
- **Options:** install-location field + component checkboxes that map
  one-to-one to declarative install steps; helper subtext per component.
- **Installing:** flat progress bar + percent + a **live monospace step log**
  fed by the real engine's `IProgress` (copy/reg/path/link lines). Reinforces
  that it's rollback-safe.
- **Finish:** success glyph, a short "what changed" summary (Start-menu entry,
  PATH, etc.), optional "Launch now" checkbox, single Finish action.

Flow is driven by the manifest: if `sigil.yaml` declares no license, the License
step (and its rail entry) is omitted; likewise options/custom. The rail's step
list is generated from the resolved screen set, not hardcoded.

Copy: sentence case, verb-first buttons, no exclamation marks, no "successfully".

### Follow-up design directions (not chosen, parked)

- Full-bleed no-rail layout (top brand bar + centered column).
- Hero-image rail panel using the `HeroFile` token.
- Progressive one-screen default (collapse to a single install screen with an
  "Advanced" disclosure).

## Out of scope here

Publish stage (`sigil publish`) and the delta-update SDK (zstd dictionary-mode +
Ed25519 client verification) remain unbuilt but are separate tracks; only the
shared zstd codec choice (phase 6) touches them.
