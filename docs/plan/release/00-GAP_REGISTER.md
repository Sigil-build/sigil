# Release gap register — first public release

Audit date: **2026-07-28**. Tree audited: `main` @ `1be494c`, clean working tree
(only `docs/plan/RELEASE_AUDIT_PROMPT.md` untracked).

This file is the single source of truth for what stands between `main` and a
first public release. It supersedes the prior review's notes: every prior
finding is re-verified here against current line numbers, and prior claims that
turned out **narrower than stated** are marked as such.

Scope of this audit: the full `src/` tree (300 C# files), `.github/workflows/`,
`docs/`, and the release surface. The P0–P13 feature-parity track — which had
never had a security review — was the main effort.

## Measured facts (this machine, Windows 11, .NET SDK 10.0.302)

| Check | Command | Result |
|---|---|---|
| Release build | `dotnet build Sigil.slnx -c Release` | **0 warnings, 0 errors**, 41.5 s |
| Test suite | `dotnet test Sigil.slnx -c Release --no-build` | **1097 total: 1096 passed, 1 skipped, 0 failed** |
| Format gate | `dotnet format Sigil.slnx --verify-no-changes` | **FAILS — exit 2, 28 of 465 files need formatting** |
| AOT publish | `dotnet publish src/SigilBuild.Cli -c Release -r win-x64 -p:PublishAot=true` | **FAILS on this box** — `vswhere.exe` not found / MSVC linker unresolvable (`MSB3073`, exit 123) |
| VM matrix | `wrapper-vm-tests.yml` | **NOT RUN** — `workflow_dispatch` only; not run here (needs admin + a disposable VM) |

Two numbers to hold onto: the suite reports **1096 passes with a single skip**,
and that single skip is the only honest one — see **R6**. The plan docs'
"527 tests green" (`ORCHESTRATION_PLAN.md:6`) is stale by half.

---

## Severity rubric

- **RELEASE BLOCKER** — ship this and you have a CVE or a broken promise.
- **SHOULD-FIX** — fix in the release, not after.
- **POST-v1** — real, but a documented limitation is honest.
- **NOTE** — informational.

Effort: **S** ≤ 1 day · **M** 1–3 days · **L** ≈ 1 week.

---

# RELEASE BLOCKERS

### R1 — Elevated replay of unauthenticated, user-writable install state
**Component:** Wrapper.Core / Engine · **Effort: L**

This is the prior review's B1. It is **confirmed, and materially worse than
described** — the blast radius extends to the install path, not just uninstall.

Evidence, all current:

| Clause | Location |
|---|---|
| Machine state dir created with a bare `Directory.CreateDirectory`, no DACL | `src/SigilBuild.Wrapper.Core/Engine/UninstallStateStore.cs:70-71` |
| No ACL hardening exists anywhere in `src/` | `grep -r "DirectorySecurity\|SetAccessControl\|FileSystemAccessRule" src/` → **zero hits** |
| Machine state root is `%ProgramData%` | `src/SigilBuild.Wrapper.Core/Engine/ScopeLayout.cs:72-74`, `UninstallStateStore.cs:42` |
| `TryLoad` falls back to the **opposite scope** | `UninstallStateStore.cs:136-140` |
| Authoritative scope is read from **inside the found file** | `UninstallStateStore.cs:174` |
| Replayed by elevated **uninstall** | `Engine/UninstallEngine.cs:42` |
| Replayed by elevated **install** (new) | `Engine/InstallSession.cs:621` (`ExistingInstallDetected` → `TryLoad`) → `:918-922` (`PerformReinstallCleanupAsync` → `UninstallEngine.RunAsync`) |

Live confirmation, not just code reading: `icacls C:\ProgramData` grants
`BUILTIN\Users:(CI)(WD,AD,WEA,WA)` and `CREATOR OWNER:(OI)(CI)(IO)(F)`. On this
machine `C:\ProgramData\Sigil` already exists **owned by the standard user with
full control**. `File.WriteAllText` truncates in place and preserves the
existing owner and DACL, so an attacker who pre-creates the file keeps control
of it after the elevated installer writes to it.

The replayed records are not path-limited in any way — no anchoring to
`install_dir`, no registry-subtree limit:

| Record | Location | Elevated primitive |
|---|---|---|
| `UnregisterCom` | `Engine/RollbackJournal.cs:602` | `LoadLibrary` + call an export from an **attacker-chosen DLL path** — arbitrary code as admin |
| `RestoreFile` | `RollbackJournal.cs:156-164` | arbitrary file overwrite / delete |
| `RestoreDeletedFile` / `RestoreDeletedDirectory` | `:388-398`, `:412-423` | arbitrary file / tree write from an attacker-chosen stash |
| `RestoreRegistryValue` / `RestoreRegistryKey` | `:238-266`, `:359-373` | arbitrary **HKLM** write (`RegistryHelper.ParseHive` accepts `"HKLM"`, `RegistryHelper.cs:22`) |
| `RestoreEnv` (`scope: machine`) | `:299-325` | machine `PATH` hijack |
| `RemoveService` | `:510-511` | stop/delete any service |
| `RemoveUninstaller` | `:494` | arbitrary (or reboot-scheduled) delete |

**Why it matters:** a local unprivileged user plants `uninstall.json`, then
waits. The next time an administrator runs the publisher's signed
`Setup.exe` — a *plain install*, not just an uninstall — the elevated process
loads and executes the planted records. `unregister_com` alone is a one-JSON-record
arbitrary-code-execution-as-admin primitive. There is no integrity protection of
any kind: no signature, no HMAC, no ownership check (`grep -r "HMAC" src/SigilBuild.Wrapper.Core` → zero hits).

**Fix:** (a) create the machine state directory with an explicit DACL — SYSTEM +
Administrators full, Users read, inheritance disabled — and refuse to load state
whose directory/file is not owned by SYSTEM or Administrators; (b) delete the
opposite-scope fallback and derive scope from the directory the file was found
in, not from a field inside it; (c) anchor replay — reject any record whose
target path is not under the recorded `install_dir` or a scope-root allowlist,
and whose registry key is not under the app's own subtree; (d) re-derive
`unregister_com`'s DLL path from `install_dir` rather than persisting a
free-form path.

---

### R2 — Elevated installer spawns an executable path taken from HKCU
**Component:** Wrapper.Core / upgrade · **Effort: M**

`InstalledStateResolver.Resolve` probes the **user** hive as a fallback even
when the tentative scope is machine:

```
src/SigilBuild.Wrapper.Core/Engine/InstalledStateResolver.cs:38-40
    var order = tentativeScope == InstallScope.Machine
        ? new[] { InstallScope.Machine, InstallScope.User }
        : new[] { InstallScope.User, InstallScope.Machine };
```

`UninstallString` is read from that key (`:70`), parsed to an exe path, and then
spawned by the already-elevated install session:

```
src/SigilBuild.Wrapper.Core/Engine/InstallSession.cs:937   var exe = _plan.PriorUninstallExe;
src/SigilBuild.Wrapper.Core/Engine/InstallSession.cs:962   psi.FileName = exe;
src/SigilBuild.Wrapper.Core/Engine/InstallSession.cs:970   using var proc = System.Diagnostics.Process.Start(psi);
```

Elevation happens first (`src/SigilBuild.Installer.Host/Program.cs:71-77`), so
this runs at high integrity. There is no signature check, no path validation,
and no admin-writable-directory requirement — only `File.Exists` (`:945`).

**Why it matters:** an unprivileged user writes
`HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\<AppId>` with a
`DisplayVersion` low enough to classify as an upgrade (`UpgradePlanner.cs:69`)
and `UninstallString` pointing at their own binary. The next admin-approved run
of the publisher's legitimate installer executes it as admin. Found
independently by two audit lanes.

**Fix:** when the effective scope is machine (or the process is elevated), probe
HKLM only. Additionally require `PriorUninstallExe` to be Authenticode-verified
or to live under an admin-only directory before spawning.

---

### R3 — `/D=` is unvalidated and privileged step targets are unanchored → SYSTEM binary in a user-writable directory
**Component:** Wrapper.Core / steps + install-dir resolution · **Effort: M**

`InstallDirResolver.Resolve` accepts the `/D=` command-line override and only
canonicalizes it — there is **no containment check against the scope root**:

```
src/SigilBuild.Wrapper.Core/Engine/InstallDirResolver.cs:66-69
    var template = FirstNonBlank(collected, cliOverride, priorInstallDir, manifestInstallDir) ?? DefaultTemplate;
    var resolved = SubstituteDirTokens(template, scopeRoot, appName, appId);
    return Canonicalize(resolved);          // :102 → Path.GetFullPath only
```

`{install_dir}` then substitutes into step fields (`Engine/StepContext.cs:419`),
and `ResolvePath` guards **only** the `payload://` scheme — every other resolved
string is returned verbatim:

```
src/SigilBuild.Wrapper.Core/Engine/StepContext.cs:505
    if (!resolved.StartsWith(PayloadScheme, StringComparison.Ordinal)) { return resolved; }
```

The privileged steps consume that unvalidated path directly:

- `Steps/ScheduledTaskCreateStep.cs:68` → `/TR` with **`/RU SYSTEM`** hardcoded at `:127-128`
- `Steps/ServiceInstallStep.cs:49-50` → `sc create binPath=` (checks `File.Exists` at `:62`, not location)
- `Steps/Win32/ComRegisterStep.cs:51` → `LoadLibrary` in the elevated process
- `Steps/FirewallRuleStep.cs:67-69` → `program=`

**This is reachable from the documented example manifest.**
`docs/guides/install-steps.md:223` shows exactly:

```yaml
- type: scheduled_task_create
  program: "${parameters.install_dir}\\heartbeat.exe"
```

So: `Setup.exe /allusers /D=C:\Users\Public\evil` → an administrator approves the
UAC prompt for a legitimately signed installer → payload lands in a
user-writable directory → a **SYSTEM scheduled task** (or auto-start service)
is created pointing at a binary any user can replace. A publisher who follows
the documentation correctly still produces a vulnerable installer; that is what
makes this a blocker rather than a footgun.

`/D=` is a first-class documented flag (`Cli/CommandLineParser.cs:427-435`,
`:372`) and is forwarded verbatim to the elevated child (`Engine/Elevation.cs`
via `Installer.Host/Program.cs:76`).

**Fix:** reject a resolved `install_dir` that is not under
`ScopeLayout.For(scope).InstallRoot` (for machine scope, under an admin-only
root); and for the four machine-scope steps, require the resolved target to be
contained in `install_dir` and to sit in a directory not writable by
non-administrators.

---

### R4 — Elevated process loads native DLLs from a per-user cache gated only by a marker file
**Component:** Wrapper.Core / native bootstrap · **Effort: M**

```
src/SigilBuild.Wrapper.Core/Engine/NativeRuntimeBootstrap.cs:85-86
    var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    return Path.Combine(baseDir, "Sigil", "runtime", hash);

src/SigilBuild.Wrapper.Core/Engine/NativeRuntimeBootstrap.cs:98-101
    var marker = Path.Combine(targetDir, CompletionMarkerName);
    if (File.Exists(marker)) { return; }        // extraction skipped wholesale

src/SigilBuild.Wrapper.Core/Engine/NativeRuntimeBootstrap.cs:171
    var cookie = AddDllDirectory(directory);
```

`Installer.Host/Program.cs:136` calls `EnsureNativeDependenciesLoadable()`
**after** the elevation branch at `:71-77` — i.e. inside the elevated process.

**Why it matters:** an attacker pre-creates the content-keyed directory with a
malicious `libSkiaSharp.dll` and touches the completion marker. The elevated
wizard skips extraction entirely, then registers the attacker-controlled
directory on the process DLL search path; Skia/ANGLE/HarfBuzz `DllImport`s
resolve to the planted binary. The SHA-256 directory name is not a defence — the
archive is readable straight out of the setup exe, so the hash is derivable.
Even the incremental path only checks file *length* (`:139-144`), not content.

This affects the GUI install path (the headless `/silent` path returns before
this point, per the comment at `Program.cs:79-86`) — i.e. the default
double-click experience.

**Fix:** for elevated runs, extract to an admin-only directory (`%ProgramData%`
with a hardened DACL, or `%WINDIR%\Temp`), and verify each extracted file's hash
against the embedded archive before `AddDllDirectory` rather than trusting a
marker file.

---

### R5 — Web-installer stub verifies, then executes, a predictably-named `%TEMP%` file
**Component:** Packaging / ExeWrapper · **Effort: M**

The `--payload web` stub's blob is two independent steps — an `http_download`
followed by a `run_program` of the same path:

```
src/SigilBuild.Packaging/ExeWrapper/ExeWrapperPackager.cs:230
    var downloadDest = "{temp_dir}/" + fullPackageFileName;   // <App>-<ver>-<arch>-Setup.exe — no GUID
src/SigilBuild.Packaging/ExeWrapper/ExeWrapperPackager.cs:234-248   // HttpDownload step, then RunProgram step
src/SigilBuild.Wrapper.Core/Engine/StepContext.cs:433-437           // {temp_dir} → Path.GetTempPath()
```

The download handle is closed before the hash is compared and is never re-opened
or re-checked (`Engine/SigilDownloader.cs:126-148`). The stub runs
`requireAdministrator`.

**Why it matters:** the filename is a pack-time constant derived from the public
artifact name, so it can be both **pre-planted** and **swapped after
verification**. Any medium-integrity process running as the same user (the
normal split-token-admin case) converts a user foothold into elevated code
execution. The SHA-256 check protects the download, not the execution.
`HttpDownloadStep.cs:62-69` will back up and overwrite a pre-existing file at
that path rather than refusing it, and `FileMode.Create`
(`SigilDownloader.cs:126`) follows a planted hardlink or reparse point.

**Fix:** stage into a freshly created, randomly named, admin-only directory, and
re-verify the hash immediately before `run_program` — or hold the verified file
open with sharing that denies write/delete across the launch.

---

### R6 — VM-gated and runtime-gated tests soft-skip by returning early, so they report **Passed**
**Component:** tests / CI · **Effort: S** (the fix; the consequence is large)

The uniform construct is an early `return`, not `Assert.Skip`:

```
tests/SigilBuild.Wrapper.IntegrationTests/UpgradeInstallTests.cs:39-45
tests/SigilBuild.Wrapper.IntegrationTests/MultiEditionInstallTests.cs:54-59
tests/SigilBuild.Wrapper.IntegrationTests/WixClassInstallUninstallTests.cs:61-66
tests/SigilBuild.Wrapper.IntegrationTests/PrerequisiteInstallTests.cs:35-41
tests/SigilBuild.Wrapper.IntegrationTests/ScheduledTaskCreateInstallTests.cs:58-63
tests/SigilBuild.Wrapper.IntegrationTests/FirewallRuleInstallTests.cs:61-66
tests/SigilBuild.Wrapper.IntegrationTests/ComRegisterInstallTests.cs:76-79
tests/SigilBuild.Wrapper.IntegrationTests/LocalizationEndToEndTests.cs:87-92
```

The codebase says so itself: `UpgradeInstallTests.cs:22` — *"Soft-skips
(**returns Passed**) unless Windows + `SIGIL_VM_TESTS=1` …"*. The gate is
`TestEnvironment.IsEnabled` / `IsRuntimeAvailable`
(`tests/SigilBuild.Wrapper.IntegrationTests/TestEnvironment.cs:20`, `:33-47`).

The same pattern gates the **packaging** tests on a staged AOT runtime, using
`Console.WriteLine("SKIP: …"); return;` — which never reaches the trx summary:

```
tests/SigilBuild.Packaging.Tests/ExeWrapper/ExeWrapperPackagerTests.cs:70-79, :164
tests/SigilBuild.Packaging.Tests/ExeWrapper/ExeWrapperWebInstallerPackTests.cs:56-62, :139, :192
```

**Why it matters:** in the run I measured, **1096 tests passed and exactly 1 was
reported as skipped**. Among those "passes" are
`Upgrade_replaces_older_version_preserving_install_dir_and_single_arp_row`,
`Silent_downgrade_is_blocked_with_exit_code_3`,
`WixClass_install_then_uninstall_yields_empty_diff`, and
`PackAsync_produces_arch_tagged_Setup_exe_with_sigil_resources_per_architecture`.
None of them asserted anything. Roughly **14 VM-gated facts plus ~10
runtime-gated packaging facts** are vacuous, and nothing in the reported totals
distinguishes "verified" from "never ran". A green suite is not evidence.

Compounding it: `ci.yml:41-42` runs `dotnet test` in the `build` job, while
`scripts/publish-installer-runtime.ps1` only runs in the *later, separate*
`aot-publish` job (`ci.yml:111,114,181-188`) — so on every push the AOT host is
never staged and the whole pack→`Setup.exe` stamping path soft-skips.

Only one VM job guards against a vacuous green
(`wrapper-vm-tests.yml:231-236`, the p11 job, whose comment names the exact
risk: *"refusing to pass vacuously"*). The scope matrix (`:35-96`) and the p12
job (`:125-171`) have no such guard.

**Fix:** convert every early return to xUnit v3 `Assert.Skip(reason)` (or a
`[VmFact]` attribute with a computed `Skip`) so the totals show them as skipped;
stage the runtime in the `build` job before `dotnet test`; add the p11-style
pre-flight assertion to the other two VM jobs.

---

### R7 — No release pipeline, no artifact publication, no signed output; README promises install channels that do not exist
**Component:** CI / repo · **Effort: M**

- `git ls-files .github/workflows` → exactly four: `ci.yml`, `docs.yml`,
  `secret-scan.yml`, `wrapper-vm-tests.yml`. **No tag trigger anywhere, no
  `release.yml`.**
- `ci.yml:220-224` uploads **unsigned** AOT artifacts as CI job artifacts —
  which require a login, expire, and are not a distribution channel.
- **The one artifact that exists is broken by construction.** `ci.yml:224`
  uploads `path: publish/win-x64/sigil.exe` — the single file. But the AOT
  output is not single-file: `publish/win-x64/` also contains
  `libSkiaSharp.dll` (11.09 MB) and `libsodium.dll` (0.33 MB), which
  `sigil.exe` needs for logo resizing and ZIP manifest signing. A user who
  downloads the artifact gets a binary that throws `DllNotFoundException` on
  those paths.
- No workflow Authenticode-signs `sigil.exe` or any produced `Setup.exe`.
- `git ls-remote --tags origin` → only `exe-installer-v1`. No release tag.
  (`pre-merge-backup-p9` is local-only.)
- `README.md:19-30` advertises `winget install Sigil-build.sigil`,
  `curl -sSL https://sigil.build/install.sh | sh`, and
  `dotnet tool install -g SigilBuild`. None of these exist. The `curl` and
  `dotnet tool` lines are offered for **macOS / Linux**, for a Windows-only
  product.
- `docs/sprint-01/identifier-reservation.md` still carries the NuGet ID as an
  unresolved placeholder.

**Why it matters:** you cannot ship a release without a way for a user to obtain
the artifact, and an unsigned installer-builder distributed by an unknown
publisher will be flagged by SmartScreen and rightly distrusted. The README
currently misrepresents the product's availability and platform support.

**Fix:** add a tag-triggered `release.yml` that runs the VM matrix as a required
gate, Authenticode-signs the CLI, publishes checksummed artifacts to a GitHub
Release, and generates the notices file; correct the README install section to
"build from source" until a channel actually exists.

---

# SHOULD-FIX

### R8 — Parameter `source.url` accepts `http://` end to end; its values feed elevated install steps
**Component:** Core / parser + Installer.Host · **Effort: S**

`schemas/sigil-schema.json:51` (and again at `:624`) declares
`"url": { "type": "string" }` with no scheme constraint;
`Configuration/ManifestParser.cs:1092-1107` checks presence only; and
`Installer.Host/Services/HttpOptionsLoader.cs:47` GETs it verbatim. This is the
**only** HTTP consumer with no scheme validation — `http_download`
(`HttpDownloadStep.cs:37-40`, SIG0235/0236) and the channel manifest's
`packageUrl` (`ChannelManifestParser.cs:82-85`) both enforce HTTPS at pack *and*
run time.

The fetched values become parameter values, which are substituted into step
fields (paths, registry coordinates, arguments) executed elevated. Graded
SHOULD-FIX rather than blocker only because it requires the publisher to write
an `http://` URL — but nothing warns them, and the fix is trivial.

**Fix:** reject non-`https://` `source.url` in `ManifestParser` (mirroring
SIG0235) and re-check the substituted URL in `HttpOptionsLoader.LoadAsync`.

---

### R9 — `/P<name>=` values flow unvalidated into privileged step fields
**Component:** Wrapper.Core / steps · **Effort: M**

`Cli/CommandLineParser.cs:590-594` accepts `/PName=Value`; the values are
forwarded verbatim to the elevated child and expand via `ctx.Resolve*` into
`scheduled_task_create.program` (`/RU SYSTEM`), `service_install.binary_path`,
`com_register.path`, and `firewall_rule.program`. Same missing containment as
**R3**, reached through a different input. Listed separately because the fix is
the same helper but a different set of call sites, and because R3's `/D=` route
is exploitable against the documented example while this one needs the manifest
to declare a parameter in a privileged field.

**Fix:** resolve privileged step targets only from `payload://` or a contained
`{install_dir}`; reject substituted values that escape it.

---

### R10 — No size cap on any download; the channel manifest is fully buffered before its signature is checked
**Component:** Wrapper.Core / net · **Effort: S**

```
src/SigilBuild.Wrapper.Core/Engine/SigilDownloader.cs:118   var total = resp.Content.Headers.ContentLength;  // progress only
src/SigilBuild.Wrapper.Core/Engine/SigilDownloader.cs:133   while ((n = await src.ReadAsync(...)) > 0)       // no cap
src/SigilBuild.Wrapper.Core/Update/UpdateSeams.cs:78        var bytes = await resp.Content.ReadAsByteArrayAsync(...)
```

`Content-Length` is read but never enforced as a ceiling. `FetchAsync` buffers
the entire channel manifest into memory **before**
`ChannelManifestVerifier.Verify` runs (`UpdateRunner.cs:116`), so memory
exhaustion is a *pre-authentication* attack surface. Timeouts do not bound a
slow-drip large body.

**Fix:** add a `maxBytes` ceiling to `DownloadVerifiedAsync` (reject up front
when `ContentLength` exceeds it, abort mid-stream otherwise); cap the manifest
fetch at a few hundred KB.

---

### R11 — Nothing downloaded is Authenticode-verified before elevated execution
**Component:** Wrapper.Core · **Effort: S**

`AuthenticodeVerifier.VerifyFile` exists and is AOT-clean
(`Engine/AuthenticodeVerifier.cs:63`), but its only caller in the entire tree is
`Engine/WrapperBlob.cs:498`, which uses it to decide whether to render a
cosmetic "Signed by …" line. No prerequisite installer
(`Engine/PrerequisiteRunner.cs:122`), no update package
(`Update/UpdateRunner.cs:204`), and no web-stub payload is signature-checked
before `Process.Start`. SHA-256 is the sole gate, so R5's and R12's TOCTOU
windows have no second line of defence.

**Fix:** call `AuthenticodeVerifier.VerifyFile` immediately before launching any
downloaded binary; fail closed, with a documented per-prerequisite opt-out for
unsigned redistributables.

---

### R12 — Prerequisite and update binaries: verify→launch gap, default ACLs, no handle held
**Component:** Wrapper.Core · **Effort: M**

```
src/SigilBuild.Wrapper.Core/Engine/PrerequisiteRunner.cs:237   Path.Combine(Path.GetTempPath(), $"sigil-prereq-{Guid.NewGuid():N}.exe")
src/SigilBuild.Wrapper.Core/Engine/PrerequisiteRunner.cs:111-122  AcquireAsync … then launcher(exePath, …)
src/SigilBuild.Wrapper.Core/Update/UpdateRunner.cs:179-204        same shape
```

Structurally identical to R5 but harder to exploit — the GUID name blocks
pre-planting, so an attacker must win a directory-change-notification race. The
file is created with default ACLs and no lock is retained between verification
and launch.

**Fix:** stage into a per-run randomly named admin-only subdirectory and hold an
open handle denying write/delete from hash verification through process launch.

---

### R13 — No freshness or replay protection on the signed channel manifest
**Component:** Wrapper.Core / update · **Effort: M**

`Update/ChannelManifest.cs:54-59` carries no timestamp, expiry, nonce, or
sequence field, and the only monotonicity check is against the *locally
installed* version (`Update/UpdateRunner.cs:130`). No "highest version ever
seen" is persisted.

**Why it matters:** signature authenticity is intact, but freshness is entirely
absent. An on-path attacker or compromised CDN replays yesterday's correctly
signed manifest indefinitely — the client reports "up to date" and exits 0
(`:134`) while a security fix exists (freeze attack) — or replays a signed
manifest for an intermediate *vulnerable* version that is still newer than
installed, and the client installs it.

**Fix:** add a required signed `issuedAt`/`expiresAt` (reject manifests older
than N days) and/or a monotonic `sequence` persisted in machine-scope state;
treat a decreasing sequence as SIG0321.

---

### R14 — `updates.manifestUrl` is never required to be HTTPS
**Component:** Core / parser + update · **Effort: S**

`schemas/sigil-schema.json:457-461` constrains only `"format": "uri"` despite
its own description saying "HTTPS URL"; `Configuration/ManifestParser.cs:156`
passes it through unvalidated; `Update/UpdateSeams.cs:71-72` fetches it verbatim
(and the `.sig` URL is that string + `".sig"`, `UpdateRunner.cs:94`).
Code execution is still gated by the signature, so impact is limited to
cleartext leakage of app-id/version/channel plus a reliable update-suppression
DoS (which R13 makes worse).

**Fix:** enforce `https://` at pack time and re-check before the fetch.

---

### R15 — Uninstall swallows undo failures, reports success, then deletes the state that would allow a retry
**Component:** Wrapper.Core / engine · **Effort: S**

```
src/SigilBuild.Wrapper.Core/Engine/RollbackJournal.cs:111   catch { /* Best-effort; swallow individual undo failures. */ }
src/SigilBuild.Wrapper.Core/Engine/UninstallEngine.cs:50    await loaded.Journal.UndoAsync(ct, progress);   // result never inspected
src/SigilBuild.Wrapper.Core/Engine/UninstallEngine.cs:59    UninstallStateStore.Delete(appId, loaded.Scope);
```

The `schtasks` / `netsh` / COM / `sc` undos also ignore spawn failures and exit
codes (`:531-535`, `:553-575`, `:641-656`). An access-denied or missing tool
therefore leaves a permanent **SYSTEM scheduled task**, machine COM
registration, or open firewall port behind — with the record that would have
removed it deleted.

**Fix:** capture per-record outcomes, surface failures to the user and the log,
and retain the state file when any record failed.

---

### R16 — No path containment on any step destination; config edits follow junctions and inherit attacker DACLs
**Component:** Wrapper.Core / steps · **Effort: M**

Containment logic is re-implemented three times and shared nowhere —
`StepContext.cs:523-537` (payload sources only), `PayloadExtraction.cs:108-118`
(zip-slip), `NativeRuntimeBootstrap.cs:190-191`. **No step destination is checked
at all:** `Steps/ConfigFileEditor.cs:28,59-64`, `Steps/FileDeleteStep.cs:30`,
`Steps/DirectoryDeleteStep.cs:33`, `Steps/HttpDownloadStep.cs:33`;
`Steps/FileCopyStep.cs:23` does not even call `ResolvePath`. Pack time is no
better — `ManifestParser.cs:1589-1631` accepts any `path` scalar.

`ConfigFileEditor.cs:64` uses `File.WriteAllText` (`FileMode.Create`), which
traverses reparse points — and **directory junctions require no privilege**. An
existing target is truncated in place and keeps its prior DACL, so an
attacker-created placeholder stays attacker-writable after the elevated
installer writes to it. `Directory.CreateDirectory` at `:62` will materialize a
whole tree outside `install_dir`. No reparse-point check exists anywhere in
`src/`.

**Fix:** add one `PathContainment.EnsureUnder(root, candidate)` helper (the
`StepContext.cs:527-532` logic) and route every step destination through it
against `ctx.InstallDir`, with an explicit manifest opt-out for deliberate
out-of-tree writes; reject reparse points in the ancestor chain; reset the DACL
on files the installer creates in machine scope.

---

### R17 — `AuthenticodeVerifier` disables revocation checking
**Component:** Wrapper.Core · **Effort: S**

`Engine/AuthenticodeVerifier.cs:31,95` — `fdwRevocationChecks = WTD_REVOKE_NONE`.
A revoked publisher certificate still renders the "Signed by …" trust line in
the wizard, which is precisely the assurance that line exists to give.
Re-verified from the prior review; unchanged.

**Fix:** use `WTD_REVOKE_WHOLECHAIN` with a cached/offline-tolerant policy, and
render a distinct state when revocation status is unavailable.

---

### R18 — Secret parameter values travel on process command lines
**Component:** Wrapper.Core · **Effort: M**

Logs, journal, and state are correctly redacted (`StepContext.cs:91-107`,
`InstallLog.cs:100`, `UninstallStateStore.cs:100-115`), but the UAC relaunch
re-emits `/P<secret>=<value>` on the child's command line
(`Engine/Elevation.cs:92-96,143-155` ← `Installer.Host/Program.cs:76`), and any
`run_program`/hook argument containing a resolved secret lands in the child's
command line (`Steps/RunProgramStep.cs:51-56`). Both are visible to
process-creation auditing (Sysmon/EDR/WMI). Extends the prior review's finding,
which covered `run_program` but not the elevation relaunch.

**Fix:** pass secrets to the elevated child over an inherited pipe or a
DPAPI-protected temp file; document that `run_program` arguments are not a
secret channel.

---

### R19 — Hostile `uninstall.json` crashes the elevated process; unbounded read
**Component:** Wrapper.Core · **Effort: S**

```
src/SigilBuild.Wrapper.Core/Engine/UninstallStateStore.cs:157-160   catch { continue; }   // covers ONLY Deserialize
src/SigilBuild.Wrapper.Core/Engine/UninstallStateStore.cs:168-171   rec.ToRollbackRecord()  // OUTSIDE the try
```

`ToRollbackRecord` throws on an unknown discriminator
(`Json/SerializableRollbackRecord.cs:288-289`) or a missing required field
(`:376-377`), and `records:[null]` throws at `:223`. Nothing catches it here, in
`UninstallEngine.RunAsync`, or in `InstallSession.cs:621`. A one-line planted
file makes **every install and uninstall of that AppId** die with an unhandled
exception — a persistent per-app DoS. It fails closed (nothing is replayed), but
noisily. Separately, `:148` `File.ReadAllText` materializes the whole
attacker-controlled file with no cap.

**Fix:** widen the `try` to cover rehydration and treat failure as "state
unreadable"; cap file size and record count before reading.

---

### R20 — `dotnet format`, PR-title lint, and schema lockstep are documented as CI-enforced but were never installed; `main` currently fails format
**Component:** CI / repo · **Effort: S**

`AGENTS.md:16` marks `dotnet format --verify-no-changes` "CI-enforced";
`AGENTS.md:96` claims "PR titles are lint-gated"; `AGENTS.md:104` lists format
in the PR checklist. **Grep for `dotnet format` across `.github/workflows/`
returns nothing.**

`CONTRIBUTING.md:69` repeats the claim: "The `pr-guards` workflow enforces
conventional-commit PR titles, `dotnet format`, and schema/docs lockstep on
every PR."

The root cause is concrete and the fix is cheap: the workflow that implements
all three checks exists in the repo but was **never installed** —
`_agent-setup/github-workflows/pr-guards.yml` contains a `pr-title` job, a
`schema-lockstep` job, and a `format` job, and is not present in
`.github/workflows/`. `_agent-setup/apply.ps1` is the copy step that was never
run.

The same unrun migration breaks a second set of references: `CLAUDE.md` points
at `.claude/skills/` and `.claude/settings.json`, and `CONTRIBUTING.md:59`
points contributors at both — but **`git ls-files .claude` returns nothing**.
The skills actually live at `_agent-setup/claude-config/skills/`. So the
project's own agent-guidance files reference paths that are not in the repo.

Consequence, measured: `dotnet format Sigil.slnx --verify-no-changes` **exits 2
on current `main`**, reporting 28 of 465 files needing formatting — including
`src/SigilBuild.Wrapper.Core/Json/SerializableInstallStep.cs`,
`Steps/ScheduledTaskCreateStep.cs`, and `Steps/FileCopyStep.cs`.

**Fix:** install `pr-guards.yml` into `.github/workflows/` and run
`dotnet format` once to clear the 28-file backlog (do these together — installing
the gate alone turns every subsequent PR red).

---

### R21 — Coverage gate is line-only and project-wide-only; per-assembly targets unenforced and unmet; three shipping assemblies absent from the denominator
**Component:** CI · **Effort: S**

`ci.yml:64` `THRESHOLD = 0.65`; `ci.yml:79-84` parses only `lines/line` `hits`,
so **branch coverage is never evaluated**; `ci.yml:96` *prints* the per-assembly
rate and `ci.yml:99` compares only the project-wide total. The
`CLAUDE.md`/`AGENTS.md:62` targets (Core ≥ 80 %, Signing ≥ 85 %) are enforced
nowhere — the gate's own comment (`ci.yml:58-59`) concedes they are aspirational.

Running the gate's exact algorithm over the local cobertura reports:
**project-wide union 13369/17786 = 75.17 %** (passes), but **`SigilBuild.Core`
= 63.89 %** — below its 80 % target *and* below the 65 % project gate — and
**`SigilBuild.Signing` = 68.79 %**. `ci.yml:60`'s comment claiming 65 % is "6pp
under the measured baseline" no longer matches. *(UNVERIFIED for CI proper:
these come from a local 2026-07-24 report tree that may contain multiple
generations; a CI run log is the authoritative source.)*

Separately, the `asm.startswith('SigilBuild')` filter (`ci.yml:75`) silently
admits only six assemblies. **`SigilBuild.Cli`, `SigilBuild.Wrapper`, and
`SigilBuild.Installer.Host` contribute zero lines** — the CLI entry point and
the entire Avalonia wizard could be at 0 % and the gate would not notice, since
`ci.yml:86-88` errors only when *no* package is found.

**Fix:** add an enforced per-assembly floor map and an expected-assembly
allowlist that fails when one is missing; parse branch coverage.

---

### R22 — Two of three VM jobs can pass vacuously
**Component:** CI · **Effort: S**

`wrapper-vm-tests.yml:231-236` (p11) hard-fails when the runner is not elevated,
with the comment *"an unelevated runner would make this whole job go green
having exercised nothing, silently."* The scope matrix (`:35-96`) and
`p12-update-webinstaller-vm` (`:125-171`) have no equivalent guard, so a dropped
`SIGIL_VM_TESTS` or a silently empty runtime staging turns both green.

**Fix:** assert `SIGIL_VM_TESTS=1` and the presence of
`runtimes/win-x64/SigilBuild.Installer.Host.exe` before `dotnet test` in both.

---

### R23 — No `SECURITY.md`, no `CHANGELOG.md`, and a third-party attribution gap that is a licence-compliance defect
**Component:** repo · **Effort: M**

Verified absent on disk: `CHANGELOG.md`, `SECURITY.md`,
`THIRD-PARTY-NOTICES.*`, `NOTICE`. `LICENSE` (MIT), `CODE_OF_CONDUCT.md`
(Contributor Covenant 2.1, real contact at `:40`), and `CONTRIBUTING.md` are
present and good.

Three distinct problems, in priority order:

1. **No `SECURITY.md`.** For software that self-elevates, writes HKLM,
   registers COM and opens firewall rules, a researcher has no disclosure
   channel and GitHub shows "Security policy: not set". Given R1–R5, this is
   the item with real consequence.
2. **The notices gap is licence compliance, not politeness.** Every *NuGet
   package* is permissive (MIT/Apache-2.0/BSD-3-Clause), but the **native
   binaries redistributed beside `sigil.exe` are not all MIT**:
   `libSkiaSharp.dll` (11.09 MB) bundles Skia and ANGLE — **BSD-3-Clause**, and
   HarfBuzz — MIT; `libsodium.dll` (0.33 MB, via NSec.Cryptography) is
   **ISC**. All carry binary-redistribution attribution requirements that an
   MIT-only `LICENSE` does not satisfy. Shipping them with no notice file is a
   defect, not a nicety.
3. **No `CHANGELOG.md` and no release tag**, so nothing describes what the
   first release contains. Thirteen feature phases landed with no user-facing
   record and no baseline to diff against.

Other runtime dependencies needing attribution (from
`Directory.Packages.props`): Avalonia / .Desktop / .Themes.Fluent 12.0.2,
Svg.Skia 2.0.0.4, ZstdSharp.Port 0.8.8, YamlDotNet 16.1.3, System.CommandLine
2.0.0-beta4, System.Text.Json 9.0.0, Azure.Identity 1.13.1, Polly 7.2.4 +
Polly.Extensions.Http 3.0.0 (BSD-3-Clause),
Microsoft.Extensions.FileSystemGlobbing 9.0.0.

**Fix:** add `SECURITY.md` (contact, supported versions, disclosure window) and
enable GitHub private vulnerability reporting; generate
`THIRD-PARTY-NOTICES.md` covering the native payloads explicitly and ship it
beside the binaries; add `CHANGELOG.md` (Keep-a-Changelog) seeded from the T-
and P-track history.

---

### R23a — No lockfiles, no `NuGet.config`: the build is not reproducible
**Component:** build / supply chain · **Effort: M**

`find . -name packages.lock.json` → none.
`grep -rn "RestoreLockedMode\|RestorePackagesWithLockFile"` → none.
`ls nuget.config NuGet.config` → none. `ci.yml:36` is a bare
`dotnet restore Sigil.slnx`.

`Directory.Packages.props` pins every *direct* version exactly (with central
management and transitive pinning enabled — that part is good), but
**transitive** dependencies still resolve to whatever the feed serves at
restore time, and with no `NuGet.config` the feed set is inherited from the
machine rather than declared by the repo. For a tool that signs and installs
privileged payloads, a build you cannot reproduce is a build you cannot audit
after the fact.

**Fix:** set `RestorePackagesWithLockFile=true` in `Directory.Build.props`,
commit the lock files, add `--locked-mode` to the CI restore, and add a
`NuGet.config` declaring nuget.org as the only feed.

---

### R24 — Version `0.0.1-alpha` is duplicated in four places with no single source of truth
**Component:** repo / CI · **Effort: S**

```
src/SigilBuild.Cli/SigilBuild.Cli.csproj:9      <Version>0.0.1-alpha</Version>
src/SigilBuild.Cli/Program.cs:11                public const string Version = "0.0.1-alpha";
tests/SigilBuild.Cli.Tests/VersionCommandTests.cs:36   .Should().Be("0.0.1-alpha");
.github/workflows/ci.yml:136                    if ($output.Trim() -ne "0.0.1-alpha") { throw ... }
```

Cutting a release means editing four files, two of which fail loudly if you
forget. Blocks R7's release workflow.

**Fix:** single source in `Directory.Build.props`, generate the constant via
`AssemblyInformationalVersion` / a source generator, and have the test and CI
smoke assert *agreement* rather than a literal.

---

### R25 — README describes a different product than the one that shipped
**Component:** docs · **Effort: S**

`README.md` is 49 lines and **never mentions the exe wizard, `Setup.exe`, the
install-step engine, the rollback journal, or the uninstaller** — the flagship
output of both completed tracks and the single largest body of shipped work.

Meanwhile it promises "zstd dictionary-mode delta updates with a built-in
client SDK" (`:16-17`). Both halves are false:
`docs/architecture/adr-010-delta-update-deferral.md:18-25` (Accepted,
2026-07-23) states "**Delta (binary-diff) patches are explicitly deferred** …
no zstd-dictionary patch format exists yet", and there is no Update SDK project
in `src/` — `docs/sprint-01/identifier-reservation.md:23` still lists
`SigilBuild.UpdateSdk` as unreserved. It also offers macOS/Linux install
commands (`:25-29`) for a Windows-only product. So the README simultaneously
undersells what exists and oversells what does not. See also R7 on the
nonexistent install channels.

**Fix:** rewrite around what exists — `sigil.yaml` → `pack --format exe` → a
branded elevating wizard — with an honest status line and a "not yet built"
list.

---

### R26 — The docs teach a silent-install command the parser rejects, and name output files that do not exist
**Component:** docs · **Effort: S**

**The worst instance first.** Two guides document this verbatim:

```
docs/guides/installer-wizard.md:97   setup.exe /S /install_dir="C:\Apps\MyApp" /edition=professional
docs/guides/parameters.md:78         setup.exe /S /install_dir="C:\Apps\MyApp" /edition=professional
```

Parameter overrides are accepted **only** under a `P` prefix
(`Cli/CommandLineParser.cs:497`). A bare `/install_dir=` or `/edition=` falls
through to `:503` and throws
`UsageException: unrecognized flag '/install_dir=...'`. The prose form
`/Name=Value` is repeated at `installer-wizard.md:100`, `parameters.md:81`, and
`packaging-formats.md:39`. **Every user who copy-pastes the documented silent
install gets a hard failure** — plausibly the most-copied line in the docs.

**Missing reference.** `docs/cli-reference.md` (84 lines, generated by
`scripts/docs/generate-cli-reference.ps1`) covers only `sigil validate|init|pack|sign`.
The installer accepts fifteen tokens, enumerated in the parser's own error text
(`CommandLineParser.cs:372,504`). Runtime flags are scattered across five
guides, and `/verysilent`, `/launch`, `/Poption.Name=Value`, and `/?`|`/help`
are documented **nowhere**. `/D=` — the input to **R3** — is mentioned only in
passing at `upgrades.md:45`. The existing generator cannot fix this: it
introspects the `sigil` command tree, not `CommandLineParser`.

**Wrong filenames.** Code truth: `Engine/InstallSurvivability.cs:17`
`UninstallerFileName = "uninstall.exe"`, and
`ExeWrapper/ExeWrapperPackager.cs:134` + `:40`/`:47` →
`{App.Name}-{Version}-{arch}-Setup.exe` / `-WebSetup.exe`. Docs say
`uninstaller.exe` in six places (`docs/guides/uninstaller.md:7,20,32`,
`docs/README.md:28`, `docs/getting-started.md:174`,
`docs/guides/installer-wizard.md:54`, `docs/guides/packaging-formats.md:36` —
only `upgrades.md:20` is right) and `setup.exe` in `getting-started.md:120,131,174`.
The documented `UninstallString` (`uninstaller.md:20`) is therefore wrong, so
anyone scripting a silent uninstall against the docs points at a nonexistent path.

**Also wrong in `getting-started.md`:** `:42` calls `sigil.exe` "a single-file
~1 MB binary" (actual: **13.98 MB** plus two sibling native DLLs — see R7);
`:118` claims ZIP output goes to `./dist/<app-id>-<version>/` (actual:
`Zip/ZipPackager.cs:24-25` writes a flat
`{out}/{App.Id}-{Version}-{arch}.zip`); `:109-112` says "only the ZIP path is
functional in the current alpha. The MSIX path lands in Sprint 4" — stale by
roughly ten phases.

**Fix:** global-replace the parameter syntax to `/PName=Value`; add a
hand-written `docs/setup-exe-reference.md` covering all fifteen tokens and link
it from `docs/README.md`; correct the filenames and the four
`getting-started.md` errors.

---

### R26a — `architecture-overview.md` misstates the compression library, the crypto, the project layout, and what CI enforces
**Component:** docs · **Effort: S**

This is a live, user-facing doc, not plan history:

- `:90` "Compression | **ZstdNet** + native fallback (zstd 1.5+ dictionary
  mode)" — wrong package and a native fallback that does not exist.
  `Directory.Packages.props:38-43` pins **ZstdSharp.Port** 0.8.8, pure-managed,
  "nothing to bundle". (The *code* comments are the accurate ones.)
- `:91` "Crypto (Ed25519) | NSec.Cryptography" — NSec survives only in
  `src/SigilBuild.Signing/Local/ZipManifestSigner.cs`; the update engine signs
  with **ECDSA P-256 via BCL `ECDsa`** (`adr-009-update-manifest-signature.md:257`).
- `:70-77` the component layout omits `SigilBuild.Signing`,
  `SigilBuild.Wrapper.Core`, and `SigilBuild.Localization.Generator` — four of
  nine projects — and still describes `SigilBuild.Wrapper` as the wizard
  engine, which moved to `Wrapper.Core` in T1.
- `:98` "These numbers are quality bars **enforced by CI, not aspirations**" —
  of seven rows CI enforces one (the 15 MB gate, `ci.yml:133`) plus coverage.
  Cold-start, pack time, sign latency, and "Delta patch generation ≤ 30 s" are
  unenforced, and the last is for a feature ADR-010 deferred.

**Fix:** regenerate the tech-stack table and component layout from
`Directory.Packages.props` + `Sigil.slnx`; relabel the metrics table "targets"
and mark which are actually gated.

---

### R27 — Colliding ADR numbers across two directories; CODEOWNERS protects the stale one and a file that does not exist
**Component:** docs / governance · **Effort: S**

```
docs/architecture/       adr-008-expression-policy, adr-009-update-manifest-signature, adr-010-delta-update-deferral, adr-avalonia-aot, adr-msix-companion
sigil-docs/architecture/ adr-009-brand-token-runtime-json-vs-source-gen, adr-010-schema-validator-monolith
```

Two different ADR-009s and two different ADR-010s on unrelated subjects.
`CODEOWNERS` routes `/sigil-docs/architecture/` and `/sigil-docs/decisions.md`
to `@Sigil-build/tech-leads` — the first is the **stale** two-file directory and
the second **does not exist**. The live directory, which contains the update
signature ADR, has no tech-lead review requirement.

**Fix:** renumber the two orphans, move them into `docs/architecture/`, delete
`sigil-docs/`, and repoint CODEOWNERS at `/docs/architecture/`.

---

### R28 — `.sigil-bak` backups survive a successful install
**Component:** Wrapper.Core · **Effort: S**

Re-verified and **confirmed**: `RollbackJournal.DiscardTransientStashes`
(`RollbackJournal.cs:48-63`) handles only `RestoreDeletedFile` (`:50`),
`RestoreDeletedDirectory` (`:53`), and `RestoreConfigFile` (`:56`), with
`default: break` at `:61-62`. The `.sigil-bak` copies made by
`Steps/FileCopyStep.cs:44-45` and `Steps/HttpDownloadStep.cs:66-67` are
journaled as `RestoreFile` and therefore persist in Program Files after a
successful reinstall.

Note the tension: this is *required* for uninstall to restore a pre-existing
overwritten file, so it is not simply a bug to delete. Prior review called it
"not discarded"; the accurate framing is "retained by design, with no lifecycle
or cleanup story."

**Fix:** decide the contract explicitly — either move the stashes into the
per-app state directory (out of Program Files) or discard them on the success
path and accept that uninstall cannot restore pre-existing files. Document it
either way.

---

### R29 — De-elevation fallback silently launches the app with the installer's admin token
**Component:** Wrapper.Core · **Effort: S**

`Engine/Launcher.cs:37-47`. The primary path is correct — Explorer's primary
token via `CreateProcessWithTokenW` (`:79-176`) — but on any failure it falls
through to `TryLaunchDirect(path, args)` (`:47`), handing the launched app the
installer's admin token with no log line and no user-visible signal. That is the
exact bug class the code exists to prevent, and it silently un-does P2's
acceptance criterion ("launch checkbox starts the app unelevated").

**Fix:** on de-elevation failure, skip the launch and surface a notice on the
Done screen rather than launching elevated.

---

### R30 — `sigil init`'s own template tells publishers to put a private-key file path in the manifest
**Component:** CLI templates · **Effort: S**

`src/SigilBuild.Cli/Commands/Templates/full-config.yaml:42` —
`signingKey: ./keys/update-signing.ed25519` — versus
`schemas/sigil-schema.json:469-471`, which says the field is "Base64-encoded
X.509 SubjectPublicKeyInfo (SPKI) DER of the ECDSA P-256 **PUBLIC** key … never
a private key, and never a file path". The value is passed through unvalidated
(`ManifestParser.cs:158` → `ExeWrapperPackager.cs:389`).

Following the template produces an installer whose every update attempt dies at
SIG0321 (fails closed, so not exploitable) — while actively steering publishers
toward committing a private-key path. Also names the wrong algorithm (Ed25519
vs the implemented P-256).

**Fix:** correct the template to a base64 SPKI placeholder and add a pack-time
diagnostic that `signingKey` decodes as base64 and imports as a P-256 SPKI.

---

### R31 — `schtasks /TR` is built by string concatenation with unescaped quotes
**Component:** Wrapper.Core / steps · **Effort: S**

```
src/SigilBuild.Wrapper.Core/Steps/ScheduledTaskCreateStep.cs:110-112
    var trValue = string.IsNullOrEmpty(arguments) ? $"\"{program}\"" : $"\"{program}\" {arguments}";
```

The single concatenated command fragment in the privileged-step set. `program`
is substitutable (R3/R9) and an embedded `"` re-tokenizes the task's own command
line. It cannot reach `/RU` or `/RL` — those are separate `ArgumentList` entries
(`:114-128`) — so impact is confined to the task action.
*UNVERIFIED:* whether a leading-quote value can shift which token Task Scheduler
treats as the executable.

**Fix:** reject `"` in `program`, or emit the task via `schtasks /XML` with
proper escaping.

---

### R32 — `ini_write` does not escape CRLF: line injection into the INI
**Component:** Wrapper.Core / steps · **Effort: S**

`Steps/IniWriteStep.cs:94` `lines[i] = key + "=" + value;` and `:105`
(insert path). `section`, `key`, and `value` are `ctx.Resolve`-expanded and
concatenated verbatim, so a value containing `\n[OtherSection]\nkey=…` writes
arbitrary INI entries into other sections. Matters when the value comes from a
wizard field or a `registry_read` var rather than a literal.

**Fix:** reject or escape `\r`, `\n`, and a leading `[` in section/key/value.

---

# POST-v1

### R33 — `XmlEditStep` relies on a framework default for XXE and has no entity-expansion cap
`Steps/XmlEditStep.cs:42-45` uses `new XmlDocument{…}` + `LoadXml` with no
`XmlResolver`/`DtdProcessing` assignment — and no such assignment exists
anywhere in the repo. On .NET 10 `XmlDocument.XmlResolver` defaults to `null`,
so external-entity file disclosure and SSRF are blocked — but that is an
unasserted framework default, not a stated invariant, and the internal DTD
subset is still parsed with no expansion cap (billion-laughs → OOM/hang of the
elevated installer, reachable when the target config sits somewhere an attacker
can write, per R16). No test asserts the XXE posture. **Fix:** set
`XmlResolver = null` explicitly, load via `XmlReader.Create` with
`DtdProcessing.Prohibit`, add a `<!DOCTYPE>` regression test. **S**

### R34 — Setup single-instance mutex fails open on the `NULL` branch
`Engine/SetupInstanceLock.cs:49-53` names it
`Global\sigil-setup-<appId>-machine` (machine) / `Local\…` (user) — fully
predictable from the public app id. `:71-93` uses raw `CreateMutexW` and
branches only on `ERROR_ALREADY_EXISTS` (`:85`, fails closed → exit code) versus
`NULL` (`:77`), and the `NULL` branch — which is what a DACL-denied squat
produces — returns a non-owning sentinel (`:82`) indistinguishable from a real
lock, so two installs can proceed concurrently. Mitigating: creating a `Global\`
object needs `SeCreateGlobalPrivilege`, which standard users do not hold, so the
cross-user squat is unavailable on a default box; `Local\` squatting is
same-user only. **Fix:** distinguish `ERROR_ACCESS_DENIED` from other failures
and treat it as contention. **S**

### R35 — `json_edit` re-parses the resolved value as JSON
`Steps/JsonEditStep.cs:163` `return JsonNode.Parse(value);` — documented as
intentional literal inference, but a value sourced from a wizard field or
registry var writes an object/array/`true` where the manifest author expected a
string. Encoding itself is safe. **Fix:** add `value_type: string|json`,
defaulting to `string`. **S**

### R36 — `com_register` runs publisher DLL code inside the elevated installer process
`Steps/Win32/ComRegistration.cs:66-101`, `ComRegisterStep.cs:62` —
`DllRegisterServer` executes in-process at high integrity, so a malformed or
hijacked DLL takes over the installer rather than a disposable child. The choice
is deliberate and documented (AOT/interop rationale, `ComRegistration.cs:9-29`).
**Fix:** document the trust assumption in an ADR, or invoke via a child process.
**S**

### R37 — `minFromVersion` floor is skipped when the installed version is malformed
`Update/UpdateRunner.cs:141-163` enforces the floor only when
`VersionComparison.IsWellFormed(state.InstalledVersion)`; otherwise it logs and
proceeds. For a user-scope install the version comes from HKCU, so a user can
steer their own eligibility — publisher policy, not a security boundary, and at
least logged. **Fix:** treat an incomparable version as not-eligible. **S**

### R38 — Restart Manager session key is a mutated managed `string`
`Engine/FilesInUse.cs:209-210` — `[LibraryImport]` with UTF-16 marshalling pins
the managed string's buffer and `RmStartSession` writes 32 chars + NUL into it.
The size is exact and `new string(char,count)` is never interned, so there is no
overflow today, but it is one refactor away from corrupting an interned literal.
**Fix:** use a `char[33]`/`Span<char>` with a `ref char` signature. **S**

### R39 — Channel manifest JSON is parsed before its signature is verified
`Update/UpdateRunner.cs:105` (parse) precedes `:116` (verify). No parsed field is
used before verification, so this is not exploitable — but it exposes the JSON
parser to unverified network input and lets an attacker choose which diagnostic
the user sees. Verify-then-parse is the cheaper invariant to keep true.
**Fix:** swap the blocks. **S**

---

# NOTE

### R40 — `.gitignore:41` contains `./docs/`, which git never matches
A leading `./` makes the pattern inert. Harmless today (`docs/` is tracked and
should be), but it silently does nothing, so whatever it was meant to exclude
isn't. **Fix:** delete the line or write the intended pattern. **S**

### R41 — Repo hygiene for a first public read

> **Corrected 2026-07-28 and RESOLVED.** The audit originally reported 15 stale
> remote branches from `git branch -r`. That count was wrong: 7 had already been
> deleted on GitHub (auto-delete-on-merge) and only stale local remote-tracking
> refs made them appear live. The true figure was **8**. All 8 have since been
> archived as `archive/<name>` tags (pushed to origin, so every commit stays
> recoverable) and deleted. `git ls-remote --heads origin` now returns `main`
> and `release/v0.1.0-alpha` only. Lesson recorded because it bit this audit:
> run `git fetch --prune` before reading `git branch -r`.

`git ls-remote --tags origin` → one non-release tag (`exe-installer-v1`), plus
the 15 `archive/*` tags added during cleanup (safe to delete once the RC merges).

`_agent-setup/` is tracked (8 files). Nothing in it is private or embarrassing,
but `apply.ps1:25` ends by telling the reader to **delete `_agent-setup/`** —
it is a half-finished migration whose own instructions say to remove it, and
because it was never run, a real CI gate is missing (R20).

`.superpowers/` is **not tracked** (`git ls-files .superpowers` → empty) — but
the prior review's concern is **confirmed**, not dismissed: `git check-ignore -v
.superpowers` **exits 1**, so the root `.gitignore` does not exclude it. The
sole exclusion is a nested `.superpowers/sdd/.gitignore` containing `*`. The
moment any tool writes `.superpowers/<anything-but-sdd>`, internal agent review
diffs become tracked files in a public repo.

`publish/` is untracked and the repo is small (1.99 MiB packed), though a stale
~168 MB `publish/win-x64/` (including an 84 MB `libSkiaSharp.pdb`) sits in the
working tree — correctly ignored, worth deleting before any archive or export.

**Fix:** delete the merged branches; run `_agent-setup/apply.ps1`, commit
`.claude/` + `.github/workflows/pr-guards.yml`, then `git rm -r _agent-setup/`;
add `.superpowers/` to the root `.gitignore`.

### R41a — `docs/sprint-01/identifier-reservation.md`: the NuGet ID is still unclaimed
`docs/sprint-01/identifier-reservation.md:13` marks `SigilBuild` as a "Reserved
placeholder **to be published** before Sprint 1 ends"; `:23`
`SigilBuild.UpdateSdk` "Pending public reservation"; `:88-89` the social handle
and a USPTO/EUIPO trademark search are both "to complete before public launch".
Meanwhile `README.md:32` tells users to `dotnet tool install -g SigilBuild`.
Going public with an unclaimed package ID that your own README advertises
invites a name squat — the classic supply-chain attack on a new project.
**Fix:** reserve both IDs on nuget.org **before** the repo goes public; then
update or delete the doc. **S**

### R42 — Supply chain: preview/beta dependencies, no vulnerability scanning
Every package in `Directory.Packages.props` is pinned to an exact version — no
wildcards, no floating ranges — with central management and transitive pinning
on. That part is good. (Reproducibility is R23a.)

Two **runtime** dependencies are not stable releases:
`SkiaSharp` / `SkiaSharp.NativeAssets.Win32|Linux` at **`3.119.4-preview.1.1`**
(`Directory.Packages.props:45-47`) — a preview *native* binary inside
privileged software, taken per the inline comment only "to satisfy Avalonia 12
transitive requirement" — and `System.CommandLine` at
**`2.0.0-beta4.22272.1`** (`:35`), the September-2022 build: roughly four years
stale, API-incompatible with later betas, and unlikely to receive a security
fix.

No `.github/dependabot.yml`, no `dotnet list package --vulnerable` step, no
SBOM (`grep -rn "dependabot\|--vulnerable\|sbom\|cyclonedx"` → zero hits). The
only security automation is gitleaks (`secret-scan.yml`), which addresses an
unrelated threat. *UNVERIFIED:* I did not assess these specific versions
against advisory databases — `Azure.Identity 1.13.1`, `Polly 7.2.4`,
`System.Text.Json 9.0.0`, `NJsonSchema 11.0.2` all warrant a real scan. Add the
scan rather than trust a judgement call.

*(`FluentAssertions` staying on 6.12.1 is correct — 8.x is commercially
licensed.)*

**Fix:** add Dependabot (nuget + github-actions, weekly) and a
`dotnet list package --vulnerable --include-transitive` CI step failing on
High/Critical; add SBOM generation to the release workflow; move SkiaSharp to
the newest stable that satisfies Avalonia 12 or record the constraint in an
ADR. **M**

### R43 — Plan docs are stale about their own state
`docs/plan/ORCHESTRATION_PLAN.md:6` claims "527 tests green"; the measured total
is **1097**. `ORCHESTRATION_PLAN.md` never mentions the P-track at all.
`docs/plan/feature-parity/01-IMPLEMENTATION_PLAN.md:188` still shows P13 as
Pushed ☐ / Merged ☐ — while P13 is commit `1be494c` on `main`, i.e. the audited
HEAD. Per `AGENTS.md`, `docs/plan/*` is read-only history and should **not** be
edited to match; this register is the correction. **Fix:** none — cite this file
instead. **S**

### R44 — S2's `allow_outside_install_dir` has no counterpart in S1's replay anchor: a supported opt-out leaves the app unremovable
**Component:** Wrapper.Core / Engine + manifest · **Effort: M** · **Cross-lane: S1 × S2**

Raised by lane S1 during its branch-review fix wave, from finding I-2 of
`reports/s1-branch-review.md`. Neither lane can see this alone; it appears only
once both are merged.

**Lane S2** adds `allow_outside_install_dir` as a first-class, schema-and-docs
manifest opt-out for a step whose destination is outside `install_dir`, and
**documents `%ProgramData%\MyApp` as the example** of when to use it.

**Lane S1** anchors rollback-journal replay (R1 clause (c)). The allowed roots are
`install_dir`, the replayed scope's Desktop and Start Menu folders, and the app's
own `<StateRoot>\Sigil\<AppId>` directory (`ReplayAnchor.For`). There is no
counterpart to S2's opt-out, and nothing carries it into the persisted journal.

**Composite failure.** A publisher follows S2's documented guidance and
`file_copy` / `directory_create` / `ini_write`s into `%ProgramData%\MyApp`. Every
one of those records is refused at uninstall; the data stays on disk; the ARP row
and the uninstall state are removed anyway; and the log reports refusals for a
**supported feature**. That is the "silently unremovable" class S1 closed four
separate routes into, arriving through a door neither lane owns.

**Why S1 did not fix it in-lane** — considered and rejected deliberately, not
overlooked:

1. It needs a persisted-format change (a per-record "this step declared itself
   out-of-tree" marker) at the final fix wave of a branch already verdicted READY,
   with no producer for the field on the branch: it would ship unpopulated and
   untestable end to end.
2. **More importantly, the naive form is unsafe.** The journal is the untrusted
   artefact. A record carrying "I was declared out-of-tree" is a record saying "do
   not anchor me", so a planted journal could opt itself out of the whole
   mechanism R1 exists to build. To be safe the marker must be cross-checked
   against a trusted copy of the manifest at replay time — and `UninstallEngine`
   does not have the blob today. That is a design change, not a field.

**Fix (Stage 2, to land before or with the first shipped build containing S2):**
resolve the manifest's declared out-of-tree destinations from the **signed blob**
at replay time and widen `ReplayAnchorage` with them; the journal records nothing
new and nothing is trusted from it. Until then `docs/guides/uninstaller.md`
documents the symptom and points publishers at an `uninstall:` step, which runs
before the journal replay and is not anchored. **M**

**Related, and deliberately unfixed — the anchor floor stays equality-only.**
`UninstallEngine.IsPlausibleInstallDirectory` rejects only a volume root and exact
matches against the well-known system directories, so a journal recording
`installDir: C:\ProgramData\Sigil` re-widens the anchor to the shared state-root
parent — i.e. back inside the threat model that the per-app narrowing
(branch-review finding 3) closes. Tightening the floor to require
`StateDirectorySecurity.IsAdminOnlyWritable` for machine scope would refuse the
uninstall of exactly the installs **this row's own lane S2 grandfathers** — those
sitting outside the `%ProgramFiles%` roots because they predate containment — so
the human partner ruled it stays as it is. The reasoning is recorded in the
method's remarks; the residual is bounded because the escalating consequences (a
machine-wide execution mapping, a machine `PATH` entry) each independently require
an admin-only-writable target. Recorded here so the S1 × S2 interaction is not
rediscovered as a new finding.

---

# Verified sound

Checked and cleared. Do not re-audit these without new information.

**Update engine (P12) — signature handling is correct.**
`Update/UpdateRunner.cs:102` captures `manifestBytes`; `:105` parses
`Encoding.UTF8.GetString(manifestBytes)`; `:116` verifies that **same array**.
Every consumed field (`schemaVersion`, `version`, `packageUrl`, `sha256`,
`minFromVersion` — the complete record at `Update/ChannelManifest.cs:54-59`)
lies inside the signed byte range. No canonicalization, no signed subset, no
unsigned siblings. The key is pack-time pinned in the stamped blob
(`InstallSession.cs:1165` ← `SerializableWrapperBlob.cs:193` ←
`ExeWrapperPackager.cs:389`) with no env var, config, flag, or manifest override.
Fail-closed on missing key (`ChannelManifestVerifier.cs:46-49`), missing
signature (`:51-54`), non-base64 input (`:62-76`), non-P-256 curve (`:84-87`),
and any crypto exception (`:94-101`). Verification precedes every consequential
action. This was the single most important thing to get right in the P-track,
and it is right.

**SHA-256 is mandatory and unskippable on every path that executes a downloaded
artifact.** Pack time: `ManifestParser.cs:1559-1568` (SIG0236) and `:413-417`
(SIG0280) both refuse. Run time: `HttpDownloadStep.cs:42-46`,
`PrerequisiteRunner.cs:231-235`. Update: `ChannelManifestParser.cs:87-90` plus a
64-hex-char shape check before spending a download
(`UpdateRunner.cs:169-173`). There is **no** code path reaching a process launch
with a downloaded file whose hash was not compared. Comparison itself is a
streaming `IncrementalHash` over the exact bytes written
(`SigilDownloader.cs:122,136`), and a mismatch is explicitly non-retryable
(`:72-75`).

**HTTPS enforced with defence in depth on the hash-gated paths.**
`HttpDownloadStep.cs:37-40` re-checks the token-substituted URL at run time even
though SIG0235 already rejected a literal `http://` at pack time;
`PrerequisiteRunner.cs:227-230` mirrors it. (The gaps are R8 and R14, elsewhere.)

**No TLS weakening anywhere.** Grep across `src/` for
`ServerCertificateCustomValidationCallback`, `SslProtocols`, and
`HttpClientHandler` → zero hits. The test seam
(`Engine/SigilHttpClient.cs:50-55`) swaps the whole client and is `internal`.
Timeouts exist on every request (`SigilDownloader.cs:107-109`,
`UpdateSeams.cs:67-68`, `HttpOptionsLoader.cs:45-46`) with a 30 s
`ConnectTimeout`. Retry classification is conservative (`:156-171`).

**No shell, anywhere in the install path.** All external tools launch with an
explicit filename and per-argument `ProcessStartInfo.ArgumentList`:
`schtasks.exe` (`ScheduledTaskCreateStep.cs:150-157`), `netsh.exe`
(`FirewallRuleStep.cs:148-155`), `sc.exe` (`ServiceInstallStep.cs:141-148`),
`run_program` (`RunProgramStep.cs:42-57`), and the same in every rollback record.
The only `UseShellExecute = true` in the repo opens the installer's own log
(`InstallerViewModel.cs:908-911`). **No scheduled-task XML is constructed at
all** — the `schtasks /Create` flag path means the XML-injection class (forged
`Principal`/`RunLevel`) does not exist here, and `/RU SYSTEM` and `/RL` are
separate argv tokens unreachable from manifest values. (R31 is the one
concatenated fragment, and it is confined to the task's own action.)

**Enum-valued privileged fields are closed sets, validated twice** — pack time
(`ManifestParser.cs:1277-1289,1355-1373`) and again at runtime with safe
defaults (`ScheduledTaskCreateStep.cs:132-144`,
`ServiceInstallStep.cs:119-135`). `service_account` can never become an
arbitrary account+password.

**All three P11 steps are pack-time pinned to machine scope** via
`MachineScopeGuard.cs:52-67` + SIG0310, and `auto` correctly fails the guard.
Each journals its inverse **before** the mutation
(`ScheduledTaskCreateStep.cs:83`, `ComRegisterStep.cs:60`,
`FirewallRuleStep.cs:80`); `firewall_rule` additionally pre-deletes by name for
reinstall idempotency (`:86`). (R15 is about undo *failure* handling, not
ordering.)

**Wizard localization is inert — no injection surface.** This was an explicit
audit question and the answer is clean. `string.Format`/`AppendFormat`/
`CompositeFormat` appear **nowhere** in `src/` (the only textual hit is a comment
in `Localization.Generator/StringsEmitter.cs:111`), so there is no
manifest-controlled format-string surface at all. The chrome catalog compiles
from repo-owned `Strings.*.txt` into pure concatenation (`:113-148`) with
build-time positional placeholders. Manifest text is only ever an *argument*:
`LocalizedText` values resolve at `WizardField.cs:581-588` into plain strings
bound to `TextBlock.Text` / `CheckBox.Content` / `Window.Title`. In Avalonia a
string bound to `TextBlock.Text` is inert — no markup parsing, no HTML. No
runtime XAML is built from manifest content and no manifest string reaches a
shell. Length is unbounded, but the worst case is an oversized TextBlock
authored by the publisher who signed the installer.

**Traversal containment is individually correct everywhere it exists.**
`StepContext.cs:518-537` (payload sources), `PayloadExtraction.cs:103-121`
(zip-slip), `NativeRuntimeBootstrap.cs:185-203` — all normalize with
`GetFullPath` *before* comparing, terminate the root prefix with a separator (so
`C:\rootevil` cannot pass as `C:\root`), and compare case-insensitively. The
defect is that they are three copies rather than one helper, and that
destinations get none of it (R16).

**Redaction is applied on every path that could carry a resolved secret** —
`InstallEngine.cs:91,151,159`, `HookRunner.cs:102-111`,
`UninstallStateStore.cs:91-97` (before bytes touch disk), and
`InstallEngine.Describe`/`DescribeUndo` render *unresolved* declared fields only
(`:174-193`). Journal records for the P11 steps carry names and paths, never
values. (R18 is the command-line channel, which redaction cannot reach.)

**Restart Manager handle hygiene is correct.** `RmEndSession` is in a `finally`
on every path (`FilesInUse.cs:147-150`, `:199-202`), the early returns correctly
skip it because no session was opened, and `RmRestart` is never called.
Registration failure fails **open** deliberately and is documented at `:40-46`
("a false 'clear' degrades to the pre-P6 behaviour… a false 'blocked' would
wedge a perfectly good install") — a defensible call for an installer.

**Config-edit rollback stash hygiene is correct.** `ConfigFileEditor.cs:40-43`
snapshots before the write and journals it; `File.Copy(..., overwrite: false)`
at `:41` makes stash pre-creation a hard failure and the GUID name is
unguessable; stashes are reclaimed on the success path
(`RollbackJournal.cs:56-60`).

**XML and JSON output encoding is safe.** `XmlEditStep.cs:63,67` uses
`SetAttribute`/`InnerText`; `JsonEditStep.cs:59-61` uses the `JsonNode` DOM.
Values are escaped by the serializer.

**Ordering guarantees on the install path.** Files-in-use gate → prerequisites →
prior-version teardown all run *before* the journal opens
(`InstallSession.cs:781-823`), so a refused run mutates nothing. Elevation
precedes all scope-requiring work (`Installer.Host/Program.cs:61-77`).

**AOT-safe deserialization throughout.** `Json/WrapperBlobJsonContext.cs:46-50`
registers the journal types; the hand-rolled discriminator
(`SerializableRollbackRecord.cs:225`) avoids reflective polymorphism as
`AGENTS.md` requires; unknown discriminators and missing fields are rejected,
not silently defaulted (the plumbing gap is R19).

**Security-critical unit tests are genuinely good** — this is worth saying
plainly given R6. `ChannelManifestVerifierTests.cs:53-175` covers tampered
bytes, wrong key, wrong curve (P-384), malformed base64 on both inputs,
null/empty/whitespace key, and DER-vs-IEEE-P1363 encoding confusion.
`PayloadExtractionTests.cs:65-76` is a real zip-slip test that also asserts no
temp directory is left behind. `HttpDownloadIntegrationTests.cs:67-102` uses a
real TLS server and covers checksum mismatch → rollback, timeout → retry, and
retries exhausted. `ComRegisterStepTests.cs:31-106` asserts the journal records
the inverse *before* the native call. Negative security tests exist and are
meaningful. The thin spots are `ElevationTests.cs:52-59` (asserts only
non-throw), `InstallEngineRollbackTests.cs` (3 tests), and
`UninstallEngineTests.cs` (3 tests: happy roundtrip `:14`, missing state `:85`,
serialization `:95`) — notably, nothing feeds `UninstallEngine` a hostile
journal, which is plausibly *why* R1's control was never built.

**The one real skip is legitimate.**
`tests/SigilBuild.Wrapper.IntegrationTests/ComRegisterInstallTests.cs:107-115`,
`Live_register_then_unregister_a_real_self_registering_dll` — needs a
purpose-built self-registering DLL that does not exist in the repo; using a
system DLL was deliberately rejected because its CLSID is pre-registered (so
"assert present" proves nothing) and unregistering system COM on a shared runner
is a fragile-fixture risk. It is the only `Skip=` in the entire repo.

**Elevation command-line quoting and exit-code propagation** (`Engine/Elevation.cs`
`BuildCommandLine`, `RelaunchElevatedAndWait`) and **`WrapperBlob` resource
parsing failing safe on tampered input** — re-checked, unchanged from the prior
review's finding, still sound.

**Repo size and artifact hygiene.** `publish/` and `TestResults/` are untracked;
the packed repo is 1.99 MiB. No build output or coverage report is committed.
