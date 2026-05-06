# Sprint 5b — Installer UI: Manual Acceptance Test Plan

**Sprint dates:** 2026-07-13 → 2026-07-24
**Author:** QA / Sigil team
**Status:** Ready for execution
**Prerequisites:** All automated tests green (`dotnet test Sigil.sln`)

---

## Prerequisites & Environment Setup

### Required hardware / VMs

| Environment | Purpose | How to provision |
|---|---|---|
| Windows 11 x64 VM (clean) | Primary acceptance tests | Hyper-V or Azure `Standard_D4s_v5` |
| Windows 11 ARM64 VM (Snapdragon X Elite equivalent) | TC-008 ARM64 test | Azure `Standard_D4pls_v6` (Cobalt ARM64) or physical Snapdragon X Elite device |
| Display with scaling options | TC-009 DPI test | Any 4K monitor or VM with display scaling settings |

### Build the test artifacts

Run these commands from `C:\Projects\hw\repos\Ascendly-Tools\ascendly-installer\sigil\`:

```powershell
# 1. Build the CLI
dotnet publish src/SigilBuild.Cli -c Release -r win-x64 -p:PublishAot=true -o publish/win-x64

# 2. Build the installer host (win-x64)
dotnet publish src/SigilBuild.Installer.Host -c Release -r win-x64 `
  -p:PublishAot=true -p:SigilAotPublish=true -o publish/installer-x64

# 3. Build the installer host (win-arm64) — run on Windows ARM64 machine or with cross-compilation
dotnet publish src/SigilBuild.Installer.Host -c Release -r win-arm64 `
  -p:PublishAot=true -p:SigilAotPublish=true -o publish/installer-arm64

# 4. Pack a test MSIX with branding
$env:SIGIL_INSTALLER_HOST_EXE = "publish/installer-x64/installer.exe"
& ./publish/win-x64/sigil.exe pack examples/full/sigil.yaml --out dist/full

# Verify the MSIX contains installer.exe and BrandTokens.g.json:
Add-Type -Assembly System.IO.Compression.FileSystem
$entries = [System.IO.Compression.ZipFile]::OpenRead("dist/full/<app-id>-<version>-x64.msix").Entries | Select-Object -ExpandProperty Name
$entries -contains "installer.exe"       # must be True
$entries -contains "BrandTokens.g.json"  # must be True
```

### Test fixture files

Two branded `sigil.yaml` fixtures are required:

**`examples/brand-verdant/sigil.yaml`** (create if missing):
```yaml
spec: v1.0
app:
  id: com.verdant.Studio
  name: Verdant Studio
  version: 1.0.0
  publisher: Verdant Inc.
build:
  source: ./out
installer:
  brand:
    primaryColor: "#16A34A"
    accentColor: "#15803D"
    gradientStart: "#022C22"
    gradientMid:   "#064E3B"
    gradientEnd:   "#16A34A"
```

**`examples/brand-lumen/sigil.yaml`** (create if missing):
```yaml
spec: v1.0
app:
  id: com.lumen.Compose
  name: Lumen Compose
  version: 1.0.0
  publisher: Lumen Inc.
build:
  source: ./out
installer:
  brand:
    primaryColor: "#DB2777"
    accentColor: "#BE185D"
    gradientStart: "#1F0729"
    gradientMid:   "#4A044E"
    gradientEnd:   "#DB2777"
```

---

## Test Cases

### TC-001 — Cold-start timing: Welcome screen ≤ 800 ms

**Priority:** P0 (launch blocker)
**Automated:** No
**Acceptance criterion:** From double-click on the MSIX to first paint of Welcome screen ≤ 800 ms.

**Prerequisites:** Clean Windows 11 x64 VM, no prior installation of this app.

**Steps:**
1. Copy `dist/full/<app>.msix` to the test VM.
2. Open a stopwatch app (Windows Clock → Stopwatch).
3. Double-click the MSIX file.
4. Start the stopwatch the moment you double-click.
5. Stop the stopwatch when the Welcome screen's heading text ("Welcome to … setup") becomes fully visible.
6. Record the elapsed time.

**Pass:** Elapsed ≤ 800 ms.
**Fail:** Elapsed > 800 ms → investigate with Process Monitor for slow DLL loads; check installer.exe is AOT-published (not JIT).

**Repeat:** Run 3 times, record all 3 values. All 3 must be ≤ 800 ms.

---

### TC-002 — Visual fidelity: 6 screens vs Figma Page 4

**Priority:** P0
**Automated:** Partially (snapshot tests handle pixel-level regression after baselines are captured)
**Acceptance criterion:** Each rendered screen matches `design/installer-ui.md` Figma file layout, type, and spacing.

**Prerequisites:** Figma file `8X1Tiw7JOzLafiOPAS22I2` accessible. Installed MSIX on Windows 11 VM.

**Steps for each screen (Welcome, License, InstallOptions, Installing, Finish, Custom):**
1. Navigate to the screen by clicking Next (accept license on License screen).
2. Take a screenshot (`Win + Shift + S` → crop to the 800×500 window).
3. Open Figma `8X1Tiw7JOzLafiOPAS22I2`, Page 4, select the corresponding screen frame.
4. Export the Figma frame at 1× (PNG).
5. Overlay the two images using any diff tool (e.g., `magick compare` from ImageMagick, or Figma's "compare" feature).
6. Verify: column widths (240px sidebar / 560px content), font sizes, button positions, gradient colors match.

**Pass:** No structural layout differences; minor anti-aliasing differences (< 2px) are acceptable.
**Fail:** Sidebar width wrong, missing gradient, wrong font size, or button misalignment.

**Capture snapshot baselines (one-time, on first passing run):**
```powershell
$env:SIGIL_SNAPSHOT_CAPTURE = "1"
dotnet test tests/SigilBuild.Installer.Host.Tests --filter "ScreenSnapshotTests" -c Release
# Commit the generated PNG files in tests/.../Snapshots/Baseline/
git add tests/SigilBuild.Installer.Host.Tests/Snapshots/Baseline/
git commit -m "test(installer): capture initial snapshot baselines"
```

---

### TC-003 — Single-customer rebrand: 1 image + 4 hex + 1 app name

**Priority:** P0
**Automated:** No
**Acceptance criterion:** Customer supplies 1 hero image + 4 hex values + 1 app name → installer is fully rebranded.

**Steps:**
1. Use `examples/brand-verdant/sigil.yaml` fixture (green brand).
2. Pack: `sigil.exe pack examples/brand-verdant/sigil.yaml --out dist/verdant`
3. Install the MSIX on the test VM.
4. Verify:
   - Sidebar gradient is green (`#022C22 → #064E3B → #16A34A`)
   - App name "Verdant Studio" appears in the window title and Welcome heading
   - Publisher "Verdant Inc." appears below the logo in the sidebar
   - Next button accent color is `#15803D` (green)
5. Repeat with `examples/brand-lumen/sigil.yaml` (magenta brand).

**Pass:** Both fixtures render with the correct brand colors and names.
**Fail:** Any color shows the default Sigil blue, or app name is wrong.

---

### TC-004 — Two test themes render correctly

**Priority:** P1
**Automated:** Partially (snapshot tests after baseline capture)
**Acceptance criterion:** Verdant Studio and Lumen Compose fixtures both render correctly from compiled tokens.

**Steps:**
1. Complete TC-003 for both fixtures.
2. For each brand, navigate all 6 screens and verify no screen shows default/fallback colors.

**Pass:** All 6 screens × 2 themes = 12 screens show correct branding.
**Fail:** Any screen falls back to default blue/indigo theme.

---

### TC-005 — WCAG warning with low-contrast brand

**Priority:** P1
**Automated:** Yes (covered by NegativeTests.BrandTokenEmitter_LowContrast_ProducesWarning)
**Acceptance criterion:** `sigil pack` with `primaryColor: "#FFEE00"` exits non-zero and prints WCAG warning; passes with `--allow-low-contrast`.

**Steps:**
1. Create `examples/brand-lowcontrast/sigil.yaml` with `primaryColor: "#FFEE00"`.
2. Run: `sigil.exe pack examples/brand-lowcontrast/sigil.yaml --out dist/lc`
3. Verify: exit code non-zero, output contains "WCAG AA".
4. Run: `sigil.exe pack examples/brand-lowcontrast/sigil.yaml --out dist/lc --allow-low-contrast`
5. Verify: exit code 0, output contains "WCAG AA" warning but proceeds.

**Pass:** Step 3 non-zero + warning, Step 5 zero exit with warning.
**Fail:** Step 3 exits 0 (silently packs), or Step 5 exits non-zero.

**Note:** This is partially covered by automated tests. The CLI flag `--allow-low-contrast` needs to be wired in `SigilBuild.Cli` — verify it exists before running this test. If the flag is not yet implemented, document as "pending CLI integration" and track in backlog.

---

### TC-006 — Cancel mid-install → modal confirm → rollback → exit 1602

**Priority:** P0
**Automated:** No
**Acceptance criterion:** Clicking Cancel during Installing screen triggers confirmation dialog; confirming causes MSIX rollback and process exits with code 1602.

**Steps:**
1. Install the MSIX on a clean VM (or simulate by launching the installer with a large payload that takes > 5 seconds).
2. When the Installing screen and progress bar are visible, click the **Cancel** button.
3. Verify: a modal dialog "Are you sure you want to cancel installation?" appears.
4. Click **Yes** (or equivalent confirm button).
5. Verify: the MSIX framework rolls back the installation (App does NOT appear in "Apps & Features").
6. Check the process exit code:
   ```powershell
   Start-Process "dist/full/<app>.msix" -Wait -PassThru | Select-Object ExitCode
   ```
   Expected exit code: **1602** (Windows standard "user cancelled installation").

**Pass:** Dialog appears, rollback completes, exit code is 1602, no partial install left.
**Fail:** No dialog, or app partially installs, or wrong exit code.

**Note:** The Cancel button currently calls `Close()` on the window in `InstallerWindow.axaml.cs`. The modal confirmation dialog and 1602 exit code are NOT yet implemented — this test case is the acceptance criterion for that future work. Log as: "Cancel confirmation modal — pending Sprint 6 (signing focus) carry-over."

---

### TC-007 — Crash recovery: force-kill mid-install → rollback

**Priority:** P0
**Automated:** No
**Acceptance criterion:** Force-killing `installer.exe` mid-install triggers MSIX-level rollback; re-launch completes cleanly with no half-installed state.

**Steps:**
1. Start the MSIX install on a clean VM.
2. Navigate to the Installing screen and wait for ~25% progress.
3. Open Task Manager, locate `installer.exe`, and click **End Task**.
4. Wait 10 seconds for MSIX rollback to complete.
5. Check "Apps & Features" — the app should NOT appear.
6. Re-launch the MSIX.
7. Verify: installation wizard starts fresh (Welcome screen), completes without errors.

**Pass:** No partial install after force-kill; second install completes successfully.
**Fail:** App appears in "Apps & Features" in a broken state, or second install fails.

**Note:** MSIX rollback on force-kill is handled by the Windows App Installer framework, not by Sigil code. This test verifies the MSIX packaging correctness.

---

### TC-008 — ARM64 Snapdragon X Elite VM

**Priority:** P1
**Automated:** No (requires ARM64 hardware or VM)
**Acceptance criterion:** All 6 screens render and install completes on ARM64.

**Prerequisites:** Windows 11 ARM64 VM. Use the `publish/installer-arm64/installer.exe` artifact.

**Steps:**
1. Transfer `dist/full/<app>-arm64.msix` to the ARM64 VM.
2. Double-click the MSIX to install.
3. Navigate all 6 screens (Welcome → License → InstallOptions → Installing → Finish).
4. Verify: no crashes, no "not a valid Win32 application" error, installer completes.
5. Check "Apps & Features" for the installed app.

**Pass:** All 6 screens render, install completes, app appears in "Apps & Features".
**Fail:** Crash, rendering garble, or install failure.

**Note:** Requires the ARM64 MSIX to be built with `installer-arm64/installer.exe`. The `SIGIL_INSTALLER_HOST_EXE` env var must point to the arm64 binary when packing the arm64 MSIX.

---

### TC-009 — Per-monitor DPI: 100% / 150% / 200%

**Priority:** P1
**Automated:** No
**Acceptance criterion:** At 100%, 150%, and 200% scaling, installer chrome stays sharp with no bitmap blur.

**Prerequisites:** Windows 11 VM with display scaling controls. 4K display recommended.

**Steps for each scaling level (100%, 150%, 200%):**
1. Open Settings → Display → Scale. Set to the target percentage.
2. Sign out and sign back in (required for some apps to pick up new DPI).
3. Launch the MSIX installer.
4. Observe:
   - Window size: should be 800×500 device-independent pixels (appears larger at higher DPI — this is correct).
   - Text: should be crisp, not blurry.
   - Logo PNG: should be sharp (not pixelated).
   - Sidebar gradient: should be smooth.
   - Buttons: should have sharp edges.
5. Take a screenshot at each scaling level and compare.

**Pass:** No blurriness at any scaling level; window content scales correctly.
**Fail:** Blurry text or controls at 150%+ (indicates PerMonitorV2 DPI awareness is not working; check `app.manifest`).

---

### TC-010 — End-to-end: pack with branding → MSIX contains installer + tokens → install on clean VM

**Priority:** P0
**Automated:** Partially (MSIX content verified by script in Prerequisites)
**Acceptance criterion:** Full pipeline from `sigil pack` to Windows installer wizard.

**Steps:**
1. Run the build commands from the Prerequisites section.
2. Verify the MSIX contains `installer.exe` and `BrandTokens.g.json` (script in Prerequisites).
3. Transfer the MSIX to a **clean Windows 11 x64 VM** (no .NET runtime, no Sigil installed).
4. Double-click the MSIX.
5. Verify the Welcome screen appears within 800 ms (see TC-001).
6. Navigate all 6 screens.
7. Complete the installation (click Next on InstallOptions to start Installing).
8. Verify the app appears in "Apps & Features" after Finish.

**Pass:** Full pipeline end-to-end on a clean VM.
**Fail:** MSIX doesn't open, installer.exe missing from package, or install doesn't complete.

---

## Test Results Matrix

| Test Case | Result | Tester | Date | Notes |
|---|---|---|---|---|
| TC-001 Cold-start ≤ 800 ms | ⬜ Pending | | | Run 1: ___ Run 2: ___ Run 3: ___ |
| TC-002 Visual fidelity vs Figma | ⬜ Pending | | | Baselines captured? Y/N |
| TC-003 Single-customer rebrand | ⬜ Pending | | | Verdant: ✓/✗ Lumen: ✓/✗ |
| TC-004 Two test themes | ⬜ Pending | | | Depends on TC-003 |
| TC-005 WCAG warning + override | ⬜ Auto | | | Automated in NegativeTests |
| TC-006 Cancel → modal → rollback | ⬜ Blocked | | | Cancel modal not yet implemented |
| TC-007 Crash recovery | ⬜ Pending | | | |
| TC-008 ARM64 | ⬜ Pending | | | Requires ARM64 VM |
| TC-009 DPI 100/150/200% | ⬜ Pending | | | |
| TC-010 End-to-end pack+install | ⬜ Pending | | | |

---

## Sprint 5b Exit Gate

**ALL P0 test cases must be ✅ Pass before Sprint 5b is considered complete.**

P0 tests: TC-001, TC-002, TC-003, TC-006, TC-007, TC-010.

TC-006 is currently blocked (Cancel modal not implemented). This is a known carry-over to Sprint 6 per `implementation/sprints/sprint-05b.md`.

---

## Known Limitations & Carry-overs

| Item | Status | Target sprint |
|---|---|---|
| Cancel confirmation modal + 1602 exit code | Pending implementation | Sprint 6 |
| ARM64 MSIX pack toolchain (cross-compile) | Needs CI matrix job | Sprint 6 |
| Snapshot baseline capture (36 PNGs) | Requires display VM + manual run | Before Sprint 6 |
| `--allow-low-contrast` CLI flag | Needs CLI integration in SigilBuild.Cli | Sprint 6 |
| Custom screen (TC-002, Screen 6) | Placeholder — v1.x carry-over | M9 |
