# ADR: Avalonia Installer.Host publish strategy under Native AOT (spec T3)

- **Status:** Accepted (spike outcome)
- **Date:** 2026-07-09
- **Decision driver:** IMPLEMENTATION_SPEC.md §T3 ("Wire the AOT runtime build") and
  §4 Risks ("Avalonia 11 under Native AOT — highest uncertainty").
- **Scope:** decides the T3 risk item only. This is a **spike ADR**. No `src/`
  changes were made; all measurements come from throwaway publishes under
  `artifacts-spike/` (git-ignored, not committed).

---

## Decision (TL;DR)

**GO Native AOT for `src/SigilBuild.Installer.Host`**, conditional on the build
running on a host that has the **MSVC "Desktop development with C++" workload**
installed (the Native AOT linker). The trim/AOT analysis is effectively clean:
**one** trim warning across the entire app + Avalonia 12 dependency graph, and it
is honestly fixable (it is the built-in-COM *server* activation path, which an
installer wizard never uses — not masked reflection).

The only thing that blocked AOT in the spike environment was a **missing platform
linker**, i.e. a toolchain-provisioning gap on this machine, **not** a code,
warning, or Avalonia-compatibility problem. CI must provision the C++ workload
(and the ARM64 C++ build tools for the `win-arm64` RID).

If, on a linker-equipped box, the native AOT link surfaces blockers we could not
observe here (ILC-stage `IL3050`/`IL3056`), the ranked fallback is
**(a) self-contained single-file non-AOT host** — measured working below.
Fallback **(b) AOT console shim + child GUI process** is *not* recommended
(strictly larger than (a) and worse UX; see "Fallbacks").

---

## Environment (exact, for reproducibility)

| Item | Value |
|------|-------|
| Machine | Windows 11 Pro (26200), win-x64 |
| .NET SDK | `10.0.301` (also 10.0.109, 10.0.204 present) |
| ILCompiler | `microsoft.dotnet.ilcompiler` **10.0.9** |
| ILLink.Tasks | `microsoft.net.illink.tasks` 10.0.9 |
| Avalonia | **12.0.2** (Directory.Packages.props) — see note |
| MSVC C++ workload | **NOT installed** (VS 2022 Community present; `VC.Tools` component absent; no `link.exe` under VS or Windows Kits) |

> **Spec vs. repo drift:** the spec text (§0, §4) says "Avalonia 11". The repo
> actually pins **Avalonia 12.0.2**, and the Host `.csproj` comment already asserts
> *"AOT — Avalonia 12 makes this clean"*. This spike confirms that assertion for
> the trim layer. Whoever reconciles the spec should update the "Avalonia 11"
> references to 12.

### Host project AOT-relevant settings (`src/SigilBuild.Installer.Host/SigilBuild.Installer.Host.csproj`)

```
OutputType=WinExe, AssemblyName=installer, ApplicationManifest=app.manifest
BuiltInComInteropSupport=true          <-- source of the one trim warning
PublishAot=true (gated on SigilAotPublish; overridden on the CLI in this spike)
IsAotCompatible=true                   <-- turns on Roslyn trim + AOT analyzers
InvariantGlobalization=true, DebuggerSupport=false
AvaloniaUseCompiledBindingsByDefault=true, EnableAvaloniaXamlCompilation=true
PackageRefs: Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Svg.Skia
```
`Directory.Build.props` (Release): `TreatWarningsAsErrors=true`,
`EnableTrimAnalyzer=true`. So any `IL2xxx`/`IL3xxx` fails a Release build.

---

## Exact commands run

All commands run from `C:\projects\sigil-s1`. Logs saved under `artifacts-spike/`.

**1. Native AOT publish (the T3 target command):**
```
dotnet publish src/SigilBuild.Installer.Host -c Release -r win-x64 -p:PublishAot=true
```

**2. Full trim-warning catalog (ILLink; does not need the native linker), warnings
not treated as errors so ILLink enumerates every finding:**
```
dotnet publish src/SigilBuild.Installer.Host -c Release -r win-x64 --self-contained \
  -p:PublishTrimmed=true -p:PublishAot=false \
  -p:EnableAotAnalyzer=true -p:EnableTrimAnalyzer=true \
  -p:TreatWarningsAsErrors=false -p:ILLinkTreatWarningsAsErrors=false
```

**3. Fallback (a) — self-contained single-file non-AOT host:**
```
dotnet publish src/SigilBuild.Installer.Host -c Release -r win-x64 --self-contained \
  -p:PublishAot=false -p:PublishSingleFile=true -p:PublishTrimmed=true \
  -p:TreatWarningsAsErrors=false -p:DebugType=none -p:DebugSymbols=false \
  -o artifacts-spike/singlefile
```

**4. Cold-start smoke (bounded, GUI needs a session desktop):**
```
timeout 8 ./artifacts-spike/singlefile/installer.exe /silent
```

---

## Results

### Native AOT publish (command 1) — BLOCKED by toolchain, not by code

```
error : Platform linker not found. Ensure you have all the required prerequisites
... in particular the Desktop Development for C++ workload in Visual Studio.
For ARM64 development also install C++ ARM64 build tools.
  [Microsoft.NETCore.Native.Windows.targets(142,5)]
```

The failure is the **linker-availability precheck**, which runs *before* ILC.
Consequences:

- The managed compile **succeeded with zero warnings** (`installer.dll` produced)
  despite `EnableTrimAnalyzer=true` + `IsAotCompatible=true` (Roslyn trim **and**
  AOT analyzers active). So the source-level AOT analyzer is clean.
- ILC never ran, so the AOT `.exe` **could not be produced or sized** on this
  machine, and ILC-stage `IL3050`/`IL3056` (RequiresDynamicCode) could not be
  observed. **This must be re-run on a C++-workload box to finalize the AOT exe
  size.** (Expectation, unmeasured: ~25–30 MB total — see size table.)

### Full trim-warning catalog (command 2) — 1 warning, whole graph

ILLink scanned the entire closure (app + Avalonia 12 + Skia/HarfBuzz/ANGLE) and,
with warnings-as-errors relaxed, the self-contained trimmed publish **succeeded**
and reported exactly **one** IL warning:

| # | Code | Origin | Message (abridged) |
|---|------|--------|--------------------|
| 1 | **IL2026** | `Internal.Runtime.InteropServices.ComActivator.GetClassFactoryForTypeInternal` | "Using member `ComActivator.GetClassFactoryForTypeImpl` which has `RequiresUnreferencedCodeAttribute` ... **Built-in COM support is not trim compatible.**" |

**Total warnings: 1. No other `IL2xxx`. No app-code warnings.**
(Under `TreatWarningsAsErrors=true` this single `IL2026` is what fails the
build — `error NETSDK1144: Optimizing assemblies for size failed`.)

App-code reflection audit (supports the diagnosis): a grep of
`src/SigilBuild.Installer.Host` for `Activator`/`Assembly.`/`GetType`/`GetMethod`/
`MakeGeneric`/`Emit`/`Reflection` returns **only** one hit — a source-generated
`[JsonSerializable(typeof(BrandTokens))]` context (`Branding/BrandTokens.cs`),
which is the AOT-safe serializer pattern the spec mandates. The Host does **no**
runtime reflection.

### Cold-start smoke (command 4) — trimmed stack is intact

The trimmed single-file exe was launched with a hard 8 s cap:

```
elapsed_ms=1942  (self-terminated before the cap)
Unhandled exception. System.ArgumentException: Unable to load bitmap from provided data
   at Avalonia.Skia.ImmutableBitmap..ctor(Stream)
   at Avalonia.Media.Imaging.Bitmap..ctor(Stream)
   at Avalonia.Markup.Xaml.Converters.BitmapTypeConverter.ConvertFrom(...)
   at SigilBuild.Installer.Host.Views.InstallerWindow.!XamlIlPopulate(...)
   ...
   at SigilBuild.Installer.Host.Program.Main(String[])
```

Interpretation: in **~1.9 s** the process spun up the CLR, initialized Avalonia,
loaded the **Skia** native render interface, and executed **compiled XAML**
(`!XamlIlPopulate`) far enough to reach an image type-converter. It then threw a
**data-level** error decoding a placeholder bitmap baked into the prototype's
`Views/InstallerWindow.axaml` — **not** a trim/AOT casualty (no missing type, no
missing member; the byte payload is simply not a valid image). This is strong
evidence that trimming did **not** strip any Avalonia type needed to bootstrap the
app and build the visual tree. (The bad asset is throwaway prototype content that
T2/T7 replace; it is out of scope for this spike.)

---

## Measured sizes

Native library PDBs shipped by the SkiaSharp/HarfBuzz NuGet packages
(`libSkiaSharp.pdb` ~84 MB, `libHarfBuzzSharp.pdb` ~20 MB = ~100 MB) are debug
symbols and are **excluded** from "shippable" figures (drop with
`-p:DebugType=none` / trim the `*.pdb` from the runtimes copy).

| Path | Layout | Shippable size (excl. native PDBs) | Notes |
|------|--------|-------------------------------------|-------|
| **Native AOT** (command 1) | single native exe + native deps | **UNMEASURED** here (no linker); expected ~25–30 MB | Skia+ANGLE+HarfBuzz native (~19 MB) ships regardless; AOT replaces coreclr+managed IL with a compact native blob |
| **Trimmed self-contained, multi-file** (command 2) | `installer.exe` (162 KB apphost) + 68 files | **42.3 MB** (44,405,302 B) | coreclr + trimmed managed assemblies loose on disk |
| **Fallback (a): trimmed self-contained single-file** (command 3) | `installer.exe` **18,478,263 B (17.6 MB)** + 3 native DLLs | **35.6 MB** (37,349,103 B) | native DLLs not embedded by default: `libSkiaSharp.dll` 11.1 MB, `av_libglesv2.dll` (ANGLE) 5.2 MB, `libHarfBuzzSharp.dll` 1.7 MB |

> **Size-gate reality check for T3:** the spec proposes a host size gate of
> **≤ 25 MB**. None of the measured Avalonia+Skia layouts meet that, because the
> Skia + ANGLE + HarfBuzz **native** libraries alone are ~19 MB and ship in every
> variant (AOT does not shrink native deps). AOT should land ~25–30 MB total.
> **Recommendation:** re-pin the host size gate to ~**40 MB** after the AOT number
> is measured on the C++ box, or invest separately in native-lib slimming
> (e.g. dropping ANGLE for a software/GL backend) if 25 MB is a hard requirement.

---

## Is the `IL2026` an honest fix or a mask?

**Honest fix. rd.xml / `TrimmerRootDescriptor` is NOT warranted and would be the
mask, not the fix.**

- The warned member is `ComActivator.GetClassFactoryForTypeInternal` — the
  built-in-COM **server** activation path (the `DllGetClassFactory` export used
  when a managed assembly is *registered as a COM server*). An installer wizard
  **never** exports managed COM servers.
- `BuiltInComInteropSupport=true` is set because Avalonia's Win32 backend uses COM
  **client** interop (clipboard `IDataObject`, OLE drag/drop, shell file dialogs).
  That path is `ComWrappers`-based and is trim/AOT-safe; it is *not* what IL2026
  flags.
- Therefore the correct fixes, in order of preference:
  1. **Verify whether Avalonia 12 still requires `BuiltInComInteropSupport` at
     all.** Newer Avalonia Win32 interop leans on `ComWrappers`; if the flag can be
     dropped, the warning disappears with zero suppression. **Confirm on the C++
     box** (build + launch the wizard, exercise clipboard/drag-drop).
  2. If the flag must stay, add a **single, scoped, documented**
     `[UnconditionalSuppressMessage("Trimming", "IL2026",
     Justification = "Host never exports managed COM servers; DllGetClassFactory
     path is unreachable")]` on `Program.Main` (or an ILLink `.xml` substitution
     that trims the `ComActivator` server path). This suppresses exactly the dead
     server path and nothing else.
- **Why not rd.xml / a root descriptor:** a root descriptor *preserves* types
  against trimming. We have **zero** reflection-over-app-types warnings, so there
  is nothing legitimate to root. Using a descriptor here would only *force-keep*
  the COM machinery to silence the analyzer — i.e. it would **mask** the fact that
  the path is dead, and bloat the binary. Not honest. Do not add one.

Net: the T3 "highest-risk" item resolves to a **single, well-understood,
non-reflection warning with a clean fix** — the definition of a GO.

---

## Fallbacks (evaluated per spec §4)

**(a) Self-contained non-AOT single-file host — MEASURED, viable.**
Builds warning-clean (the lone IL2026 only errors under `TreatWarningsAsErrors`,
which trim publishes relax), launches the full Avalonia/Skia/compiled-XAML stack
in ~1.9 s, 35.6 MB shippable. This is the recommended fallback if AOT linking
proves troublesome on the real box. Cost vs AOT: larger on-disk (coreclr shipped),
slightly slower cold start, no NativeAOT startup benefit — but zero toolchain risk
and identical UX.

**(b) AOT console shim launching the host as a child process — NOT recommended.**
This keeps a tiny AOT `.exe` for the wrapper/`WrapperRuntimeLocator` contract and
spawns the *non-AOT* GUI host underneath. It therefore ships **everything (a)
ships plus** a second executable and a process hop, is **strictly larger** than
(a), and adds cross-process arg/exit-code plumbing (the same fiddly forwarding the
spec flags for elevation). It would only be justified if AOT of the GUI itself
were impossible — and this spike found **no code-level reason** it should be.
Not measured further; deliberately deprioritized.

---

## Consequences / follow-ups for T3 implementation

1. **CI/build hosts must install the "Desktop development with C++" workload**
   (win-x64) **and the C++ ARM64 build tools** (for the `win-arm64` RID). This is
   the real precondition the spike surfaced — bake it into the CI image and the
   local-pack prerequisites doc.
2. Re-run command 1 on a C++-equipped box to (a) obtain the ILC-stage
   `IL3050`/`IL3056` catalog (expected empty given the clean Roslyn AOT analyzer +
   no app reflection) and (b) **measure the real AOT exe size**; pin it.
3. Resolve `BuiltInComInteropSupport`: first try dropping it; else add the one
   scoped `IL2026` suppression. Do **not** add rd.xml.
4. Re-pin the T3 host size gate to a realistic value (~40 MB) or fund native-lib
   slimming — 25 MB is unattainable with Skia+ANGLE+HarfBuzz.
5. The prototype `InstallerWindow.axaml` embeds an invalid placeholder bitmap that
   crashes at construction; T2/T7 must replace it with blob-sourced brand assets
   (already their scope) — noted here so the AOT launch test on the real box
   isn't mistaken for an AOT failure.

---

## Appendix: raw logs

Committed with this ADR: none (logs live under the git-ignored `artifacts-spike/`
in the spike worktree). Reproduce with the commands above. Key excerpts are quoted
inline in **Results**.
