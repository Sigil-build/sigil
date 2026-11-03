# setup.exe command-line reference

> **This page is hand-written, not generated — and it must stay that way.**
> `scripts/docs/generate-cli-reference.ps1` (which produces
> [`cli-reference.md`](cli-reference.md)) introspects the `sigil` **CLI**'s
> `System.CommandLine` tree — the `sigil validate|init|pack|sign` commands. The
> tokens on this page belong to a completely different parser,
> `CommandLineParser` in `src/SigilBuild.Wrapper.Core/Cli/CommandLineParser.cs`,
> shared by the
> produced installer (`SigilBuild.Wrapper`, the headless entry point) and the
> branded wizard (`SigilBuild.Installer.Host`). The generator has no way to
> reach that parser's grammar, so it cannot regenerate this page — a future
> maintainer must not delete this page in favor of "just regenerate it."
> Every token and behaviour below is cited to the line of code that defines
> it; if the parser changes, update both.

This is the full grammar accepted by the produced `<App.Name>-<version>-<arch>-Setup.exe`
(and, in `/Uninstall` mode, the deployed `uninstall.exe`) — eighteen tokens you
can type, plus three reserved ones the installer passes to itself (below).
`CommandLineParser.Parse` is a **closed grammar**: anything not listed
here throws `UsageException: unrecognized flag '<token>'` and the process
exits `64` (`src/SigilBuild.Wrapper/Program.cs:163-167`,
`src/SigilBuild.Installer.Host/Program.cs:45-50, 110-114`; see
[Exit codes](#exit-codes)). The list a mis-typed flag is checked against
is reproduced verbatim in the parser's own error text
(`CommandLineParser.cs:585-586`).

## Mode flags

| Flag | Effect |
|---|---|
| `/silent` (alias `/S`) | Headless install: no wizard UI. Implies acceptance of the license — the headless path never shows the License screen (`CommandLineParser.cs:438-443`). |
| `/verysilent` | Headless install with the progress UI additionally suppressed. Implies `/silent` (`CommandLineParser.cs:444-449`). |
| `/Update` | Check the configured update channel and, if a newer version is advertised, download and run it (`CommandLineParser.cs:450-454`; see [Updates](guides/updates.md)). **It runs no step list of its own** — there is no `update_steps` manifest key. It fetches `updates.manifestUrl`, verifies the detached ECDSA P-256 signature over the exact bytes, checks freshness and replay, downloads the advertised package, verifies its `sha256` (and its Authenticode signature, subject to `installer.require_signed_downloads`), and spawns it. The upgrade itself is then performed by that downloaded `Setup.exe`. |
| `/Uninstall` | Run the auto-derived uninstall sequence instead of installing (`CommandLineParser.cs:455-459`). This is what the deployed `uninstall.exe`'s ARP `UninstallString` (interactive) and `QuietUninstallString` (with `/S`) invoke — see [Uninstaller](guides/uninstaller.md). |
| `/?` (alias `/help`) | Print help text and exit `0`. Handled **before** `CommandLineParser` even runs, and only when it is the *sole* argument (`src/SigilBuild.Wrapper/Program.cs:21`, `src/SigilBuild.Installer.Host/Program.cs:25`). |
| `--version` | Print the runtime's version banner and exit `0`. Like `/?`, handled before the parser and only as the *sole* argument — both hosts accept it (`src/SigilBuild.Wrapper/Program.cs:15-19`, `src/SigilBuild.Installer.Host/Program.cs:19-23`). |

With no mode flag and no `/silent`/`/verysilent`, double-clicking `Setup.exe` opens the interactive wizard in install mode.

## Install-time overrides

| Flag | Effect |
|---|---|
| `/D=path` | Install-dir override. **No space before or after `=`**, and no quotes needed unless `path` itself contains a space (`CommandLineParser.cs:493-505`). Highest-precedence source, above the manifest's `installer.install_dir` and the scope-root default — see [Precedence](#d-and-the-install-dir-precedence-chain) below. |
| `/PName=Value` | Override a declared `parameters:` entry or a built-in option (`desktop_shortcut`, `start_menu`, `add_to_path`, `file_associations`) by its canonical schema name, case-insensitively. **The `P` prefix is mandatory** — `/Name=Value` (no `P`) is not recognized and throws `UsageException: unrecognized flag`, which is exactly the bug this page exists to stop reproducing (register row R26; `CommandLineParser.cs:577-583`, binding at `:726-776`). Last write wins for a repeated name. Undeclared names are rejected — a typo can't silently reach the step engine. |
| `/Poption.Name=Value` | Override an app-defined custom component (`installer.options.components[]`, shipped P10) by its declared name, namespaced under `option.` so a component and a same-named parameter never collide — `/Pfoo=x` binds the parameter `foo`, `/Poption.foo=x` binds the component `foo` (`CommandLineParser.cs:724, 744-759`). |
| `/allusers` / `/currentuser` | Scope override for a manifest with `installer.scope: auto` (`CommandLineParser.cs:460-469`). `/currentuser` is refused with exit `64` in a run elevated with another account's credentials, where it would install into that account's profile (register row R118; `InstallSession.Build`). |
| `/force-downgrade` | Install an older version over an already-installed newer one instead of blocking (P3). Ignored for a fresh install or a same/newer-version upgrade (`CommandLineParser.cs:480-484`). |
| `/closeapps` | When the install directory is held open by a running process, close it via the Restart Manager instead of refusing the run (P6). Without it, a blocked silent install exits `4` (`FilesInUseExitCode`) rather than proceeding (`CommandLineParser.cs:475-479`; `InstallSession.cs:75-81`). |
| `/launch` | After a **silent** install completes, start `installer.run_after_install` unelevated. Ignored without `/silent` — the interactive wizard uses the Done-screen "Launch" checkbox instead (`CommandLineParser.cs:470-474`). |
| `/lang=tag` | Requested wizard/log language, e.g. `/lang=en`, `/lang=uk`, `/lang=pt-BR`. A fixed `installer.language` in the manifest wins over this flag; the conflict is logged, not a usage error (`CommandLineParser.cs:534-559`). |
| `/LOG` / `/LOG=path` | Write a timestamped install/uninstall/update log. Bare `/LOG` defaults to `%TEMP%\sigil-<appid>.log`; `/LOG=path` writes there instead (P7; `CommandLineParser.cs:507-532`). |

### `/SecretHandoff=path` — machine-to-machine, not for you to type

The grammar has three more real tokens, and this page would not be a closed
grammar without naming them. `/SecretHandoff=<path>` is how a `secret` parameter's
value crosses the UAC boundary when a per-machine install relaunches itself
elevated (register row R18): the value travels in a DPAPI-protected,
ACL-restricted, delete-after-read file, and only the *path* appears on the
relaunch command line — so no process-creation auditor ever sees the secret.
It is consumed **before** the token loop runs
(`CommandLineParser.cs:413-419, 670-714`; `Engine/ElevationSecretHandoff.cs`).

The installer mints and passes it to itself. Do not put it on a command line
you write; an unreadable or already-consumed handoff file is a usage error.

### `/RelaunchedFrom=path` — machine-to-machine, not for you to type

The second reserved token. `uninstall.exe`, started from the install
directory, runs the uninstall from a private temporary copy of itself so the
copy can remove the install directory, the original uninstaller included
(register row R106; see
[How `uninstall.exe` removes itself](guides/uninstaller.md#how-uninstallexe-removes-itself)).
`/RelaunchedFrom=<path>` is how the copy learns which `uninstall.exe` it was
made from (`CommandLineParser.cs:561-575`; `Engine/UninstallerRelaunch.cs`). An
empty path is a usage error. The value is a claim, not a trust decision: the
copy acts on it only when the named file is an `uninstall.exe` in another
directory carrying exactly the copy's own bytes, and otherwise logs it and
ignores it. A run carrying the token never relaunches again.

### `/ScopeHandoff` — machine-to-machine, not for you to type

The third reserved token, a bare flag (`CommandLineParser.cs:485-490`). When
the user picks **All users of this computer** in an unelevated wizard and
presses Next, the wizard starts a fresh elevated instance (register row R90)
with its own command line minus any `/currentuser` / `/allusers`, plus
`/allusers`, plus `/ScopeHandoff` — and, when the user changed the destination
from the pre-filled machine default, `/D=<that path>` in place of any `/D=` it
was given (register row R117; `InstallSession.BuildMachineScopeRelaunchArgs`).
Secret parameter values still travel in the `/SecretHandoff=` envelope, never
on that line.

The token tells the elevated wizard to open where the unelevated one left off
— on the screen after **Choose Install Location** — instead of on Welcome. It
is honoured only on an interactive install that also carries `/allusers` and
resolved to machine scope, and is ignored on every other run
(`InstallSession.ResumesFromScopeHandoff`). It selects the first wizard page
and nothing else: scope, containment and elevation are decided exactly as on a
run without it, and every earlier screen is one Back away. The forwarded `/D=`
is an ordinary `/D=` to the elevated instance, so machine-scope containment
still decides whether it is usable; if it is refused, the wizard opens on the
location screen on the scope default instead.

## `/D=` and the install-dir precedence chain

`/D=` is a **destination override, not a parameter**. It participates in one
resolution chain together with the wizard-collected path, the prior install
directory (on an upgrade or reinstall), the manifest's `installer.install_dir`, and the
scope-root default — highest precedence first
(`InstallDirResolver.cs:15-23`):

1. the wizard-collected destination path (interactive only);
2. `/D=path`;
3. the prior install directory, when this run is a same-scope upgrade, forced
   downgrade or same-version reinstall;
4. `installer.install_dir` (manifest);
5. the default, `<scope root>\<App.Name>`.

**`/D=` now refuses a path outside the scope's containment root** (register
row R3). For a machine-scope install the accepted roots are the scope's own
install root plus **both** `%ProgramFiles%` and `%ProgramFiles(x86)%` — the
x86 root is accepted too because it is equally admin-only and
TrustedInstaller-owned, so nothing is lost by allowing the standard 32-bit-on-64-bit
install shape (`InstallDirResolver.cs:257-312`, see `IsContained` /
`ContainmentRoots`). For a user-scope install the accepted roots widen to the
whole user profile, since nothing there crosses a privilege boundary
(`InstallDirResolver.cs:274-282`). A path outside those roots — including one
that reaches back inside them through a directory junction — is refused
**before anything is installed**:

```
The install directory '<path>' is outside the <scope> scope root '<roots>'
(or reaches it through a directory junction). Refusing to install there —
{install_dir} feeds SYSTEM-level step targets. Nothing was installed.
```

(`InstallDirResolver.cs:317-332`, `EnsureContained`.) The one exception is
re-installing into the app's own existing (already out-of-root) location on a
machine where the app predates this rule — that grandfather case is logged,
never silent (`InstallDirResolver.cs:135-224`).

## Exit codes

| Code | Meaning | Source |
|---|---|---|
| `0` | Success (or, for `/Update`, "already up to date"). | — |
| `1` | Generic step failure (the install was rolled back). | `InstallSession.cs:599` (and `:1767`, `:1790` on the uninstall paths) |
| `2` | User cancelled. Covers three cases, not just one: declining the elevation (UAC) prompt, cancelling the wizard, and an engine `OperationCanceledException`. The install is rolled back in each. | `InstallSession.cs:604`; `Elevation.cs:144`; `InstallerOutcomeCode.UserCancelled` |
| `3` | Downgrade blocked — a newer version is installed and `/force-downgrade` was not supplied. | `InstallSession.DowngradeBlockedExitCode`, `InstallSession.cs:58` |
| `4` | Files in use — the install directory is held open by a running process and `/closeapps` was not supplied. | `InstallSession.FilesInUseExitCode`, `InstallSession.cs:81` |
| `5` | Another setup for this app + scope is already running. | `InstallSession.AlreadyRunningExitCode`, `InstallSession.cs:87` |
| `6` | `/Update`: not update-enabled — the manifest declares no `updates.manifestUrl`. | `InstallSession.UpdateNotConfiguredExitCode`, `InstallSession.cs:94` |
| `7` | `/Update`: check or apply failed (network, **malformed manifest** — including one missing the required `issuedAt`/`expiresAt`/`sequence`, SIG0320 — checksum, download/spawn failure). | `InstallSession.UpdateCheckFailedExitCode`, `InstallSession.cs:102` |
| `8` | `/Update`: a hard security reject. **Three distinct causes**, so do not read `8` as "tampering" alone: the channel manifest's **signature** did not verify (SIG0321); the manifest was authentic but **failed the freshness / replay check** (expired, older than 30 days, future-dated, empty window, or a `sequence` **lower than** one already accepted — an equal sequence is accepted); or the **downloaded package was refused by Authenticode** under `installer.require_signed_downloads`. The log line distinguishes them. | `InstallSession.UpdateManifestRejectedExitCode`, `InstallSession.cs:110`; `Update/UpdateRunner.cs:134-139, 155-161, 283-301` |
| `9` | `/Update`: a newer version exists but the installed version is below its `minFromVersion` floor. | `InstallSession.UpdateNotEligibleExitCode`, `InstallSession.cs:118` |
| `64` | Usage error — unrecognized flag, undeclared parameter/option, (in `/silent`) a required parameter with no default left unset, or `/currentuser` in a run elevated with another account's credentials (R118). | `src/SigilBuild.Wrapper/Program.cs:163-167`, `src/SigilBuild.Installer.Host/Program.cs:45-50, 110-114` |
| `78` | The embedded native runtime (Skia/ANGLE/HarfBuzz) could not be extracted to an administrator-only directory and verified, or one of `libSkiaSharp.dll`, `libHarfBuzzSharp.dll`, `av_libglesv2.dll` is missing from that verified cache or would not load from it — refused rather than loading unverified native code (register rows R4, R102). These three libraries are loaded **only** from the cache, by full path; the directory `Setup.exe` / `uninstall.exe` runs from is never searched for them, so a same-named DLL beside the exe is ignored rather than loaded. GUI path only. | `NativeRuntimeUntrustedExitCode`, `src/SigilBuild.Installer.Host/Program.cs:414`; `NativeRuntimeBootstrap.DefaultDllSearchFlags`, `NativeRuntimeBootstrap.LoadPinnedLibraries` |
| `3010` | Success, reboot required (standard MSI/Windows-installer convention). | `InstallSession.RebootRequiredExitCode`, `InstallSession.cs:73` |

## Worked example

```bash
Setup.exe /S /D="C:\Apps\MyApp" /Pedition=professional /LOG /closeapps
```

Silent install to `C:\Apps\MyApp` (subject to the containment check above),
overriding a declared `edition` parameter, writing a timestamped log to
`%TEMP%\sigil-<appid>.log`, and closing any app holding the install
directory open instead of refusing. See [Installer wizard](guides/installer-wizard.md#silent-install)
and [Parameters](guides/parameters.md#cli-overrides-at-install-time) for the
manifest side of `/PName=Value`.

## See also

- [Installer wizard](guides/installer-wizard.md)
- [Parameters](guides/parameters.md)
- [Uninstaller](guides/uninstaller.md)
- [Updates](guides/updates.md)
- [Upgrades & downgrades](guides/upgrades.md)
