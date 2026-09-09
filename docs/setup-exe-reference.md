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

This is the full grammar accepted by the produced `<App>-<version>-<arch>-Setup.exe`
(and, in `/Uninstall` mode, the deployed `uninstall.exe`) — fifteen tokens in
total. `CommandLineParser.Parse` is a **closed grammar**: anything not listed
here throws `UsageException: unrecognized flag '<token>'` and the process
exits `64` (`src/SigilBuild.Wrapper/Program.cs:66`,
`src/SigilBuild.Installer.Host/Program.cs:41`; see
[Exit codes](#exit-codes)). The full list a mis-typed flag is checked against
is reproduced verbatim in the parser's own error text
(`CommandLineParser.cs:372,504`).

## Mode flags

| Flag | Effect |
|---|---|
| `/silent` (alias `/S`) | Headless install: no wizard UI. Implies acceptance of the license — the headless path never shows the License screen (`CommandLineParser.cs:270-272,379-384`). |
| `/verysilent` | Headless install with the progress UI additionally suppressed. Implies `/silent` (`CommandLineParser.cs:385-390`). |
| `/Update` | Run the manifest's `update_steps` instead of `install_steps` — the path `Setup.exe /Update` takes when invoked by the Update SDK or a scripted check (`CommandLineParser.cs:391-395`; see [Updates](guides/updates.md)). |
| `/Uninstall` | Run the auto-derived uninstall sequence instead of installing (`CommandLineParser.cs:396-400`). This is what the deployed `uninstall.exe`'s ARP `UninstallString` invokes — see [Uninstaller](guides/uninstaller.md). |
| `/?` (alias `/help`) | Print help text and exit `0`. Handled **before** `CommandLineParser` even runs, and only when it is the *sole* argument (`src/SigilBuild.Wrapper/Program.cs:18`, `src/SigilBuild.Installer.Host/Program.cs:22`). |

With no mode flag and no `/silent`/`/verysilent`, double-clicking `Setup.exe` opens the interactive wizard in install mode.

## Install-time overrides

| Flag | Effect |
|---|---|
| `/D=path` | Install-dir override. **No space before or after `=`**, and no quotes needed unless `path` itself contains a space (`CommandLineParser.cs:428-439`). Highest-precedence source, above the manifest's `installer.install_dir` and the scope-root default — see [Precedence](#d-and-the-install-dir-precedence-chain) below. |
| `/PName=Value` | Override a declared `parameters:` entry or a built-in option (`desktop_shortcut`, `start_menu`, `add_to_path`, `file_associations`) by its canonical schema name, case-insensitively. **The `P` prefix is mandatory** — `/Name=Value` (no `P`) is not recognized and throws `UsageException: unrecognized flag`, which is exactly the bug this page exists to stop reproducing (register row R26; `CommandLineParser.cs:495-501,566-567,603-604`). Last write wins for a repeated name. Undeclared names are rejected — a typo can't silently reach the step engine. |
| `/Poption.Name=Value` | Override an app-defined custom component (`installer.options.components[]`, shipped P10) by its declared name, namespaced under `option.` so a component and a same-named parameter never collide — `/Pfoo=x` binds the parameter `foo`, `/Poption.foo=x` binds the component `foo` (`CommandLineParser.cs:497,573-587`). Documented **nowhere else** before this page (register row R26). |
| `/allusers` / `/currentuser` | Scope override for a manifest with `installer.scope: auto` (`CommandLineParser.cs:401-410`). |
| `/force-downgrade` | Install an older version over an already-installed newer one instead of blocking (P3). Ignored for a fresh install or a same/newer-version upgrade (`CommandLineParser.cs:421-425`). |
| `/closeapps` | When the install directory is held open by a running process, close it via the Restart Manager instead of refusing the run (P6). Without it, a blocked silent install exits `4` (`FilesInUseExitCode`) rather than proceeding (`CommandLineParser.cs:416-420`; `InstallSession.cs:75-81`). |
| `/launch` | After a **silent** install completes, start `installer.run_after_install` unelevated. Documented **nowhere else** before this page (register row R26). Ignored without `/silent` — the interactive wizard uses the Done-screen "Launch" checkbox instead (`CommandLineParser.cs:411-415`; `ParsedCommandLine.Launch` doc comment, `CommandLineParser.cs:119-124`). |
| `/lang=tag` | Requested wizard/log language, e.g. `/lang=en`, `/lang=uk`, `/lang=pt-BR`. A fixed `installer.language` in the manifest wins over this flag; the conflict is logged, not a usage error (`CommandLineParser.cs:468-493`). |
| `/LOG` / `/LOG=path` | Write a timestamped install/uninstall/update log. Bare `/LOG` defaults to `%TEMP%\sigil-<appid>.log`; `/LOG=path` writes there instead (P7; `CommandLineParser.cs:441-466`). |

## `/D=` and the install-dir precedence chain

`/D=` is a **destination override, not a parameter**. It participates in one
resolution chain together with the wizard-collected path, the prior install
directory (on an upgrade), the manifest's `installer.install_dir`, and the
scope-root default — highest precedence first
(`InstallDirResolver.cs:15-23`):

1. the wizard-collected destination path (interactive only);
2. `/D=path`;
3. the prior install directory, when this run is an upgrade;
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
| `1` | Generic step failure. | `InstallSession.cs:565` |
| `2` | User cancelled the elevation (UAC) prompt. | `Elevation.cs:72-89` |
| `3` | Downgrade blocked — a newer version is installed and `/force-downgrade` was not supplied. | `InstallSession.DowngradeBlockedExitCode`, `InstallSession.cs:58` |
| `4` | Files in use — the install directory is held open by a running process and `/closeapps` was not supplied. | `InstallSession.FilesInUseExitCode`, `InstallSession.cs:81` |
| `5` | Another setup for this app + scope is already running. | `InstallSession.AlreadyRunningExitCode`, `InstallSession.cs:87` |
| `6` | `/Update`: not update-enabled — the manifest declares no `updates.manifestUrl`. | `InstallSession.UpdateNotConfiguredExitCode`, `InstallSession.cs:94` |
| `7` | `/Update`: check or apply failed (network, malformed manifest, checksum, download/spawn failure). | `InstallSession.UpdateCheckFailedExitCode`, `InstallSession.cs:102` |
| `8` | `/Update`: the channel manifest's signature did not verify — a hard security reject. | `InstallSession.UpdateManifestRejectedExitCode`, `InstallSession.cs:110` |
| `9` | `/Update`: a newer version exists but the installed version is below its `minFromVersion` floor. | `InstallSession.UpdateNotEligibleExitCode`, `InstallSession.cs:118` |
| `64` | Usage error — unrecognized flag, undeclared parameter/option, or (in `/silent`) a required parameter with no default left unset. | `src/SigilBuild.Wrapper/Program.cs:66`, `src/SigilBuild.Installer.Host/Program.cs:41` |
| `78` | The embedded native runtime (Skia/ANGLE/HarfBuzz) could not be extracted to an administrator-only directory — refused rather than loading unverified native code (register row R4). GUI path only. | `src/SigilBuild.Installer.Host/Program.cs:247` |
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
