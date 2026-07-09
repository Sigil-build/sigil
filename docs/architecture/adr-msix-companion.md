# ADR: Reconcile the MSIX companion host after the Installer.Host repurpose (spec T16)

- **Status:** Accepted (decision ADR for T16a; implemented by T16b)
- **Date:** 2026-07-09
- **Decision driver:** IMPLEMENTATION_SPEC.md §T16 ("Reconcile the MSIX companion
  host") plus the two changes that broke it — §T2 (Installer.Host now drives the
  real `Wrapper.Core` engine and **deletes** `Services/InstallerEngine.cs`) and
  §T7 (removes the `BrandTokens.g.json` sidecar in favour of blob-embedded brand
  tokens).
- **Scope:** decides *what MSIX ships as its "companion installer"* and *the fate
  of the `BrandTokens.g.json` sidecar in MSIX*. This is a **decision ADR** — no
  `src/` changes are made here. The concrete edits are T16b (wave 3); a minimal
  implementation outline for T16b is in the last section.

---

## Decision (TL;DR)

**Option (b): the MSIX companion is NOT the engine-driven host — and in its
minimal, honest limit it is removed entirely, not replaced by a second exe.**

MSIX is a **native OS deployment format**: Windows lays down the payload and
launches the declared app entry point. `MsixPackager` already stages the app's
own files (`CopyTree`) and declares the real app exe as the entry point
(`AppxManifest.xml` → `Executable="{execName}.exe"`,
`EntryPoint="Windows.FullTrustApplication"`). A bundled wizard has **no install
job left to do** — and, tellingly, nothing in the generated `AppxManifest.xml`
references the bundled `installer.exe` at all. It is a **vestigial extra binary**
that survived from the pre-T2 world.

Concretely, T16b **stops bundling** `installer.exe` + `BrandTokens.g.json` into
MSIX: delete the `manifest.Installer is not null` arm in `MsixPackager.PackAsync`
and delete `InstallerHostBundler`. Branding in MSIX flows through the surfaces the
OS actually honours and that `MsixPackager` **already emits** — `AppxManifest.xml`
`<VisualElements>` and the logo `Assets/` produced by `LogoAssetGenerator` — not
through a bundled wizard reading a loose file.

**Why not option (a):** both sub-variants of (a) are actively worse than doing
nothing:

- *"Feed it a stamped blob"* means `MsixPackager` would have to invoke the
  exe-wrapper stamping pipeline (`WrapperResourceWriter`) to embed a
  `SIGIL_BLOB_V1` (steps) + `SIGIL_PAYLOAD_V1` (payload) resource into the bundled
  companion — **shipping the payload twice** (once as MSIX-deployed files, once
  inside the companion's blob) and duplicating the entire `ExeWrapperPackager`
  machinery inside the MSIX packager, all to run a wizard that redundantly
  re-installs what the OS already installed. Pure bloat (~35 MB Avalonia+Skia host
  per MSIX) and confusing UX.
- *"Keep a directory-source mode"* means re-introducing a loose-file source the
  host reads at runtime — i.e. re-inventing exactly the sidecar mechanism **T7
  deletes**. It contradicts T7's "the blob is the single source of truth, no loose
  files beside the exe" contract head-on.

The branded wizard runtime is, after T2, **exclusively the exe-wrapper track's
concern** (a single stamped `-Setup.exe`). MSIX and the `.exe` installer are two
*parallel distribution formats* for the same app, not layers of one pipeline.

---

## Context: what the code actually does today

### `MsixPackager.PackAsync` (`src/SigilBuild.Packaging/Msix/MsixPackager.cs`)

1. `CopyTree(options.SourceDirectory, staging, …)` — stages the app's **own**
   payload files (lines 45, 97–105).
2. `AppxManifestBuilder.Build(...)` — writes `AppxManifest.xml`. The launched
   application is `Executable="{execName}.exe"` where `execName` is derived from
   `App.Id` (`DeriveExeName`, `AppxManifestBuilder.cs:81`), with
   `EntryPoint="Windows.FullTrustApplication"` (`AppxManifestBuilder.cs:46–56`).
   **This is the real app exe, not `installer.exe`.**
3. `LogoAssetGenerator.Generate(...)` or `CreatePlaceholderAssets(...)` — writes
   the `Assets/*Logo.png` referenced by `<VisualElements>` (lines 50–56).
4. **Only if `manifest.Installer is not null`** (lines 58–66): locate a host exe
   (env `SIGIL_INSTALLER_HOST_EXE`, else `installer/installer.exe` under
   `AppContext.BaseDirectory`) and call `InstallerHostBundler.Bundle`.
5. `MakeAppxRunner.PackAsync(...)` → `.msix`.

### `InstallerHostBundler.Bundle` (`src/SigilBuild.Packaging/Installer/InstallerHostBundler.cs`)

Two side effects, both into MSIX staging:

```csharp
File.Copy(installerExeSource, Path.Combine(stagingDir, "installer.exe"), overwrite: true);   // (1)
var tokens = BrandTokenEmitter.Emit(manifest);
File.WriteAllText(Path.Combine(stagingDir, "BrandTokens.g.json"), tokens);                     // (2)
```

Nothing in `AppxManifest.xml` references either file. `InstallerHostBundler` is
called from exactly one place — `MsixPackager` (line 65) — and `BrandTokenEmitter`
has exactly one **production** caller: `InstallerHostBundler` (line 20).

### Why T2 already made this companion inert

Pre-T2, the bundled host ran the throwaway copy-loop
`Installer.Host/Services/InstallerEngine.cs` to copy files, and read its branding
from the `BrandTokens.g.json` dropped beside it
(`App.axaml.cs:29 → BrandTokens.LoadOrDefault("BrandTokens.g.json")`, reading from
CWD). T2 **deleted** that copy loop and rewired the host to drive the real engine
through `InstallSession.Create` → `WrapperBlob.LoadFromSelf()`
(`InstallSession.cs`; `WrapperBlob.cs:50`), which reads steps + parameters from a
Win32 resource **`SIGIL_BLOB_V1` embedded in the exe**.

`InstallerHostBundler` copies the **raw AOT-published** `installer.exe` — which has
**no `SIGIL_BLOB_V1` resource stamped into it** (only `ExeWrapperPackager` /
`WrapperResourceWriter` stamps blobs). So inside an MSIX, the bundled companion's
`WrapperBlob.LoadFromSelf()` finds no resource, returns `WrapperBlob.Empty`
(`WrapperBlob.cs:31–37, 52–53`) → **zero steps, zero parameters**. The MSIX
companion is already a do-nothing wizard even before T7 lands. T16 is the moment we
stop pretending it installs anything.

---

## The `BrandTokens.g.json` sidecar's fate

**Deleted from the MSIX path. Not migrated to a blob-in-MSIX, and not needed.**

- The sidecar only ever existed so the *bundled wizard* could self-brand at
  runtime. With no bundled wizard in MSIX, there is nothing to brand at runtime —
  the OS renders the MSIX's identity from `AppxManifest.xml` `<VisualElements>`
  (`DisplayName`, `Description`, `BackgroundColor`) and the `Assets/` logos, which
  `MsixPackager` already produces. Those are the branding surfaces Windows honours
  for a Start-menu tile / Store listing; a JSON of hex colours next to an
  unreferenced exe is not.
- T7 removes the sidecar for the **exe-wrapper** host by threading the derived
  light/dark token maps + base64 logo/hero bytes **into `WrapperBlob`**
  (`SerializableWrapperBlob` + its `JsonSerializerContext`), and rewrites
  `App.axaml.cs` to read brand data from the blob instead of
  `LoadOrDefault("BrandTokens.g.json")`. That is the single-stamped-`.exe`
  delivery mechanism. **MSIX does not participate in it** — there is no host in the
  MSIX to feed.
- Net for MSIX: the `BrandTokens.g.json` write in `InstallerHostBundler.Bundle`
  (line 21) is deleted outright. MSIX carries **no** brand-token file, in any form
  (sidecar or blob).

## What feeds the host in MSIX now that the copy-loop `InstallerEngine` is gone

**Nothing — because there is no host in the MSIX.** This is the crux of choosing
(b) over (a):

- In the **exe-wrapper** track, the stamped `SIGIL_BLOB_V1` resource feeds the host
  (`WrapperBlob.LoadFromSelf` → `InstallSession` → `InstallEngine`).
- In the **MSIX** track, the *OS* is the installer. The payload that the deleted
  copy-loop used to lay down is exactly what `MsixPackager.CopyTree` already stages
  and MakeAppx/Windows deploys. There is no residual install work for a companion
  to perform, so there is nothing to feed it. Bundling a host purely so it can load
  an empty (or, under option (a), redundantly payload-duplicating) blob is the
  bloat this ADR rejects.

---

## Consequences

- **Smaller, honest MSIX packages.** No ~35 MB Avalonia+Skia companion, no
  unreferenced files, no payload duplication.
- **One branded-wizard runtime, one delivery path.** The stamped-blob mechanism
  (T7) lives solely in the exe-wrapper track; the MSIX packager stays a thin
  manifest+assets+MakeAppx pipeline.
- **Determinism preserved (spec §0).** Removing the two staged files removes the
  only inputs to the MSIX that were not deterministic file-tree copies; output
  stays byte-identical across builds.
- **Dead code retires cleanly.** `InstallerHostBundler` and its only-consumer
  chain (`BrandTokenEmitter` in `SigilBuild.Packaging`) become removable. Note:
  T7 independently ports the *real* colour-derivation logic into the exe-wrapper /
  blob path, so `SigilBuild.Packaging.Installer.BrandTokenEmitter` is not a loss —
  it is a pre-T7 stub that emits hardcoded gradient defaults the schema never
  exposed. Coordinate the deletion with T7 (see risks).
- **Future option preserved.** If a genuine requirement ever emerges for a branded
  *first-run* experience *inside* MSIX, the correct shape is a purpose-built
  minimal exe wired as an MSIX `<Extensions>`/`StartupTask` and stamped with a
  blob — **never** the directory-source sidecar of option (a). This ADR closes the
  vestigial companion; it does not preclude a deliberately-designed one later.

---

## Minimal implementation outline for T16b (wave 3)

Ordered, and scoped to keep MSIX packaging green after T2/T7 (spec T16
acceptance: "MSIX packaging tests stay green; no orphaned copy-loop code
remains").

1. **`MsixPackager.PackAsync`** — delete the whole
   `if (manifest.Installer is not null) { … }` block (`MsixPackager.cs:58–66`),
   including the `SIGIL_INSTALLER_HOST_EXE` env lookup and the
   `installer/installer.exe` fallback probe. MSIX no longer cares whether the
   manifest declares an `installer` section.
2. **Delete `src/SigilBuild.Packaging/Installer/InstallerHostBundler.cs`** — its
   only caller is the block removed in step 1.
3. **Retire `BrandTokenEmitter` from `SigilBuild.Packaging`** (coordinate with
   T7). Its only production caller was `InstallerHostBundler`. Remaining callers
   are tests: `tests/SigilBuild.Packaging.Tests/Installer/BrandTokenEmitterTests.cs`
   and `tests/SigilBuild.Installer.Host.Tests/Negative/NegativeTests.cs`. The WCAG
   contrast check those exercise moves with T7 into the blob token-derivation path;
   delete or migrate these tests to the T7 assertions. Do **not** silently leave a
   dead emitter behind.
4. **Host side (verify T7 landed it):** confirm `App.axaml.cs` no longer calls
   `BrandTokens.LoadOrDefault("BrandTokens.g.json")` and that `BrandTokens.cs`'s
   sidecar-read API and gradient fields are gone. MSIX correctness does not depend
   on this, but the sidecar file must have **zero** remaining producers or
   consumers repo-wide.
5. **Lock the decision with a test.** In `MsixPackagerTests` (Windows+SDK gated,
   mirroring `Pack_OnWindows_ProducesMsixWhenSdkPresent`), pack a manifest that
   **does** declare an `installer` section and assert the produced package's
   staging/extraction contains **no** `installer.exe` and **no**
   `BrandTokens.g.json`. This pins "MSIX never bundles the wizard" so it cannot
   regress. (Today's two tests assert nothing about the companion, so they stay
   green regardless — but they also would not catch a reintroduction.)
6. **Grep-gate the cleanup** (spec T16b VERIFY): confirm no references to the
   deleted `Services/InstallerEngine` remain anywhere in `src/`/`tests/` (only the
   three `docs/plan/*.md` narrative mentions are acceptable), and no references to
   `InstallerHostBundler` / `BrandTokens.g.json` remain outside docs.

**Risks / coordination:**

- **T7 ordering.** T16b removes `BrandTokenEmitter`'s only production caller; T7
  removes gradient fields and moves colour derivation into the blob. Land T16b
  after (or same wave as) T7 so the emitter/sidecar are deleted once, coherently,
  without a window where one half references a removed member. If T16b runs first,
  keep `BrandTokenEmitter` compiling (delete only the bundler) and let T7 finish
  the emitter retirement.
- **`AppxManifestBuilder` is unaffected.** It already derives the executable name
  from `App.Id` and never referenced `installer.exe`; no manifest change is needed.
- **No behavioural change for MSIX branding** — `<VisualElements>` + logo `Assets`
  were always the OS-honoured surfaces and are already emitted. Optionally (nice-to-
  have, not required): default `manifest.Package.Msix.Logo` from
  `manifest.Installer?.Brand?.Logo` when the MSIX logo is unset, so a brand logo
  declared once still reaches the tile. Flag as optional in T16b, not blocking.
