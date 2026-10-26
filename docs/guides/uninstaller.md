# Uninstaller

When you produce an `exe` package, Sigil ships an uninstaller automatically. There is no `WriteUninstaller`-equivalent step to call - the wrapper handles deployment, ARP registration, and rollback-journal replay on your behalf.

## How it works

On a successful install, the wrapper copies its own running image to `<install_dir>\uninstall.exe`. That copy is made at **install time**, not at pack time: it is a byte-for-byte `File.Copy` of the Setup.exe that is executing, so its size is the size of the whole Setup.exe including the embedded payload — there is no separate, smaller uninstaller binary and no uninstaller resource inside Setup.exe. Because it is the same image, it also carries the same Authenticode signature you applied to Setup.exe.

Both the uninstaller copy and the ARP entry below are written on **every** successful `exe` install, with or without an `uninstall:` block in the manifest. There is no way to suppress them.

It then writes a per-app entry under the **scope-correct** hive — `HKLM` for a per-machine install, `HKCU` for a per-user one:

```
HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\<AppId>   # machine scope
HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\<AppId>   # user scope
```

with these values:

|Value|Contents|
|---|---|
|`DisplayName`|`app.name`|
|`DisplayVersion`|`app.version`|
|`Publisher`|`app.publisher`|
|`UninstallString`|`"<install_dir>\uninstall.exe" /Uninstall /allusers` (machine scope) or `… /currentuser` (user scope) — the **interactive** uninstall, which is what Apps & features runs when a person presses Uninstall|
|`QuietUninstallString`|The same command with `/S`: `"<install_dir>\uninstall.exe" /S /Uninstall /allusers` or `… /currentuser` — the **silent** uninstall, for scripts and deployment tooling|
|`InstallLocation`|The install directory actually used (written when non-empty). Load-bearing: a later upgrade reads it to recover the prior install directory.|
|`EstimatedSize`|`REG_DWORD`, in KB. Computed at pack time as the sum of the **uncompressed** payload file sizes — the bytes that land on disk — not the compressed Setup.exe size.|
|`InstallDate`|YYYYMMDD|
|`NoModify` / `NoRepair`|`1`|

The scope flag on both values is not optional decoration: without it the uninstall would re-resolve to the manifest's default scope and could miss the HKLM-vs-HKCU keys and `%ProgramData%`-vs-`%LocalAppData%` state the install actually wrote.

**No `DisplayIcon` value is written.** The icon Windows shows in Add or Remove Programs is whatever it derives from the executable named by `UninstallString` — there is no manifest field that sets it directly.

Add or Remove Programs reads this key, so your app surfaces there with no extra YAML.

## Two flows

Run either path; both are equivalent:

```bash
"<install_dir>\uninstall.exe" /Uninstall    # interactive (Apps & features' UninstallString)
"<install_dir>\uninstall.exe" /S            # silent (the QuietUninstallString form)
setup.exe /Uninstall                        # interactive, from the original setup.exe
```

Both enter the wrapper in `Uninstall` mode:

1. Run the `installer.hooks.pre_uninstall` steps. They run **first**, before anything checks for running applications, so a hook that stops your app — its own `--quit`, for instance — clears it out of the way. A hook that fails with `on_failure: fail` (the default) aborts the uninstall here. They run once per uninstall, however many times the check below is retried.
2. Check for files in use, on both paths, before anything is removed: a declared `installer.app_mutex` that is held, or a process the Restart Manager reports holding a file in the install directory, blocks the run. `/closeapps` asks the blockers to close first. On the silent path a block refuses the run with exit code **4** and names each blocker in the log; in the uninstall window it opens the Close-applications step (see [The uninstall window](#the-uninstall-window)).
3. Run the manifest's `uninstall:` step list (top to bottom).
4. Replay the rollback journal in reverse order - file restores, registry restores, env-var restores, service stops + deletes, shortcut deletions, etc. Every record is checked against the installation it belongs to before it is replayed; see [Anchored replay and refused records](#anchored-replay-and-refused-records).
5. Remove the ARP entry, then run the `installer.hooks.post_uninstall` steps.

Every run writes a log even without `/LOG`: the stamped `Setup.exe` / `uninstall.exe` writes its run log into the always-on diagnostic log, `%TEMP%\sigil-wizard-<timestamp>-<pid>.log`, on the silent path as well as in the wizard. `/LOG` (or `/LOG=<path>`) moves it to a file you name.

## How `uninstall.exe` removes itself

A running program cannot delete its own executable, so an uninstaller that ran from the install directory would leave itself — a copy of the whole `Setup.exe` — in that directory until the next restart. `uninstall.exe` does what the NSIS and Inno Setup uninstallers do instead: it runs the uninstall from a temporary copy of itself.

1. Started from the install directory in uninstall mode (interactive or `/S`), `uninstall.exe` first moves its current directory out of the install directory — a folder that is some process's current directory cannot be removed, and Explorer starts a double-clicked program in its own folder. A relative `/LOG=<path>` is resolved first, so it still names the same file.
2. A machine-scope uninstall elevates at this point, exactly as before. Everything below runs with whatever privilege the uninstall has: the copy is started directly, so it inherits it — it never elevates and never drops privilege.
3. After taking the single-instance guard, `uninstall.exe` copies itself into a fresh, randomly named private directory — `%ProgramData%\sigil-uninstall-<guid>`, writable only by administrators, for an elevated uninstall; `%TEMP%\sigil-uninstall-<guid>`, accessible only to the current user, SYSTEM and administrators, otherwise. It checks the copy's SHA-256 against its own bytes from a handle that stops anything replacing the file, starts the copy while still holding that handle, and hands it the guard.
4. It then **waits** for the copy and exits with the copy's exit code. That is what a `QuietUninstallString` caller, a script and an upgrading `Setup.exe` read, and it is when Apps & features refreshes its list, so the interactive uninstall waits too. Its own diagnostic log records the handoff and the copy's exit code; the copy writes the run log (and the `/LOG` file) as before.
5. The copy finds the install the way the original does — the app id from the installer it carries, the scope from the command line, the install directory from the uninstall state and the Add/Remove Programs entry — and runs the steps above. When the journal reaches the uninstaller itself, the original is still the image of the waiting process and cannot be deleted, so it is **moved** into the copy's private directory instead. The install directory is then empty and is removed with the rest of the journal.
6. When the copy exits, the original process deletes the copy, and schedules the private directory — which by then holds only its own renamed executable — for deletion at the next restart.

What is left behind:

- **Nothing in the install directory.** The folder itself is removed when the install created it.
- **One file in the private temporary directory** until the next restart. An elevated (machine-scope) uninstall schedules it for deletion at the next restart. An unelevated per-user uninstall cannot — Windows only lets administrators schedule that — so it stays in `%TEMP%` until the temporary folder is cleaned up, for example by Storage Sense or Disk Cleanup.

When the copy cannot be made — no private directory could be created, the copy does not verify, or it will not start — `uninstall.exe` uninstalls in place, as it did before: the run continues and only the uninstaller itself stays in the install directory until the next restart (machine scope) or until it is deleted (per-user). The same happens when the install directory and the temporary directory are on different volumes, because a running program can be renamed only within one volume. A copy that does not verify is never started.

An upgrade still works the same way: the new `Setup.exe` runs the previous version's `uninstall.exe /S`, waits for it, and requires exit code `0`. That uninstaller hands the single-instance guard it was admitted on to its own copy, so the copy runs inside the upgrade instead of being refused as a second setup. An `uninstall.exe` from a version before this change uninstalls in place, as it always did.

## The `uninstall:` block

The journal reverses the install steps it recorded, within the bounds described under [Anchored replay](#anchored-replay-and-refused-records). Use `uninstall:` only for tear-down the journal can't infer:

- stopping a Windows service before its files are deleted;
- killing a scheduled task or a still-running app process;
- removing an AppData cache the installer didn't write;
- running a vendor's bundled cleanup script.

```yaml
uninstall:
  - id: stop-and-remove-services
    type: run_program
    program: powershell.exe
    args:
      - -NoProfile
      - -ExecutionPolicy
      - Bypass
      - -File
      - "{install_dir}\\StopAndRemoveServices.ps1"
    wait: true
    timeout_seconds: 120
    on_failure: continue
```

`uninstall:` accepts the same step types as `install_steps:`. It runs BEFORE the journal replay, so this is the place to stop services that hold open file handles in the install dir.

## Rollback journal

Every install step records a reverse operation BEFORE mutating state. Examples:

|Step|Recorded inverse|
|---|---|
|`file_copy` (new file)|Delete the file.|
|`file_copy` (overwrite)|Restore the original bytes from a `.sigil-bak` stash.|
|`directory_create`|Remove the directory (only if newly created).|
|`directory_delete`|Restore the entire subtree from a temp stash.|
|`registry_write`|Restore the prior value (or delete if previously absent).|
|`registry_delete_value` / `registry_delete_key`|Re-create with the snapshotted prior value(s).|
|`env_set`|Restore the prior value and re-broadcast `WM_SETTINGCHANGE`.|
|`shortcut_create`|Delete the `.lnk`.|
|`service_install`|Stop the service and `sc delete` it.|
|`file_delete`|Restore the file from a temp stash.|
|`http_download`|Restore the prior file (or delete the downloaded one if nothing was there).|
|`ini_write` / `json_edit` / `xml_edit`|Restore the config file's prior content from a stash (or delete it if the step created it).|
|`scheduled_task_create`|Delete the scheduled task.|
|`com_register`|Unregister the COM server (withdrawn when the DLL failed to load or exported no `DllRegisterServer`, so nothing was registered).|
|`firewall_rule`|Delete the firewall rule.|
|The `uninstall.exe` copy itself|Remove `<install_dir>\uninstall.exe`.|
|`run_program`|None - external side effects are not invertible.|

Each row is what the journal *records*. Whether a given record is *replayed* also depends on the anchoring rules below.

A mid-install crash leaves the journal in a consistent state: each record is written before the mutation, so power-loss between record and mutation simply produces a no-op rollback entry.

## Anchored replay and refused records

`uninstall.json` is a file on disk. An uninstall - and a re-install, which replays the prior journal before laying down the new version - reads it and acts on it with whatever privilege the process holds, which for a machine-scope install is administrator. A record that named an arbitrary path, registry key or service would therefore be an instruction to an elevated process from a file, so the replay is **anchored**: each record is checked against the installation it belongs to, and one that does not fit is skipped and reported rather than executed.

Machine-scope state is also refused outright unless both its directory and `uninstall.json` itself are owned by SYSTEM/Administrators/TrustedInstaller and carry no non-administrator write permission. That check protects the file; anchoring protects against a file that passed it but should not have.

### What a record may touch

|Record kind|Replayed when|
|---|---|
|File and directory records (`file_copy`, `directory_create`, `file_delete`, `directory_delete`, config edits, shortcuts, the uninstaller itself)|The target - **and, for records that restore content, the backup or stash the content comes from** - is inside `install_dir`, the Desktop or Start Menu folder of the scope being uninstalled, or the app's own `%ProgramData%\Sigil\<AppId>` / `%LocalAppData%\Sigil\<AppId>` state directory. Nothing may *write* into the `Programs\Startup` folder, though a shortcut your installer placed there is still *removed* normally.|
|`registry_write` and the registry delete records|The key is under `Software\` in HKLM or HKCU. Keys that define how Windows runs something - shell verbs (`…\shell\<verb>\command`), COM server paths, driver maps, `Run`/`RunOnce`, `App Paths`, Image File Execution Options, policy keys - are only replayed when the value being restored points at a program inside `install_dir`, and for HKLM that program's directory must also be administrator-only writable. `HKU` and `HKCC` are never replayed.|
|`env_set`|The restore either puts back a value whose entries are all already present (the `append` / `prepend` shape), or replaces a variable that pointed wholly inside `install_dir` before the uninstall began (the `set` shape - so an app that repointed `JAVA_HOME` at its own JRE has that reversed). `PATH` and other variables Windows depends on may only have entries removed, never be replaced or deleted, and a machine-scope entry must be administrator-only writable.|
|`service_install`|The service's registered `ImagePath` runs a binary inside `install_dir`, or the service no longer exists.|
|`com_register`|The DLL path is re-derived from `install_dir`; a recorded path that does not resolve inside it is never loaded.|

The install directory used as the anchor is the one recorded at install time - the `/D=` or wizard-chosen destination, not a recomputed default - falling back to the ARP `InstallLocation` for installs that predate the recorded field.

### What a refusal looks like

A refused record is **skipped, logged, and the rest of the replay continues**. Aborting would let one bad record block an entire uninstall; skipping silently would hide it. The `/LOG` file and the wizard's log pane get one line per refusal:

```
refused: restore_file refused: 'C:\Windows\System32\drivers\etc\hosts' is outside the install directory 'C:\Program Files\Acme' and the scope roots the installer legitimately writes
```

followed by a summary line naming how many records were refused.

**A healthy uninstall emits no refusal lines at all.** In particular, `file_delete`, `directory_delete`, `ini_write`, `json_edit` and `xml_edit` stash the prior content under `%TEMP%` so a mid-install rollback can put it back; that stash is reclaimed the moment the install commits, so at uninstall time those records simply have nothing to restore and are replayed as no-ops, silently. Seeing nothing is the expected outcome.

**So if you do see refusals, that is worth investigating rather than ignoring.** Either the journal was tampered with, or the app writes somewhere the anchor does not cover - most commonly a step whose destination is outside `install_dir`, such as `%ProgramData%\<YourApp>`. Data written outside the anchored locations is left on disk when the app is removed; delete it from an `uninstall:` step, which runs before the journal replay and is not anchored.

### `cannot upgrade: … is not verified`

Separately from the journal, an upgrade or re-install that has to run the **previous version's** `uninstall.exe` first will refuse to launch it when privilege is at stake - that is, when the install is machine-scope, or when the current process is already elevated - unless that executable is either Authenticode-valid or sits somewhere only administrators can write. The run aborts before anything is installed, with:

```
cannot upgrade: 'C:\...\uninstall.exe' is not verified
```

This is reachable in practice for an **unsigned** machine-scope install whose directory grants ordinary users write access - for example an install onto a secondary volume such as `D:\Apps\MyApp`, whose default root permissions grant `Users` write. Signing your installer resolves it, because `uninstall.exe` is a copy of the signed `setup.exe`. Installing under `%ProgramFiles%` also resolves it. Unelevated per-user upgrades are not gated at all: there is no privilege boundary to protect.

## Silent uninstall

`/S` suppresses any wizard chrome the uninstall flow would otherwise show. Sigil registers it as the `QuietUninstallString`, which is what deployment tooling and scripts that remove software unattended look for.

Pressing **Uninstall** in Apps & features (or Add or Remove Programs) runs the `UninstallString`, which carries no `/S`, so the person who asked for the removal sees the uninstall window (below). That is deliberate — a silent run that is refused (an application still running, a `pre_uninstall` hook that aborted) has nobody to tell, and from the user's side it looks as if the button did nothing. Double-clicking `<install_dir>\uninstall.exe` directly, with no flags, opens the same window.

## The uninstall window

The interactive uninstall is **Confirm → [Close apps] → Progress → Done | Failed**, or **Not installed** when there is nothing to remove.

- **Confirm.** Uninstall starts the run; Cancel leaves with exit code `2`.
- **Close apps** — shown only when something is running. Pressing Uninstall runs the `pre_uninstall` hooks and then the files-in-use check; if an application is still holding the install, the run stops there, before anything is removed, and the window shows the same Close-applications screen as the install wizard: each blocking application with its process id, **Retry** (re-scan after closing them yourself), **Close for me** (asks each application to shut down normally through the Restart Manager, in the background, then re-scans and says so if any are still running), and **Cancel** (exit code `2`, nothing removed — the `pre_uninstall` hooks have already run by then). Once nothing is blocking, the uninstall continues to Progress without running the hooks again.
- **Progress** grows the reversal log as the journal replays. The title bar's X (and Alt+F4) is refused while it runs: stopping half-way would leave a partly removed install. On Confirm and Close apps the X means Cancel.
- **Done** or **Failed** close with exit code `0` or `1`.
- **Not installed** — shown instead of Confirm when there is nothing to remove: no uninstall state exists for the app in that scope, which is what a leftover `uninstall.exe` of an already-removed app finds. It says "<App> is not installed", runs no hook, and closes with exit code `0`.

## When the app is not installed

An uninstall that finds no uninstall state for the app in its scope has nothing to remove, and that is not a failure. The window opens on its **Not installed** screen (above); the silent path prints `<App> is not installed … there is nothing to uninstall`, writes `result: not installed` to the log, and exits `0`. No `pre_uninstall` or `post_uninstall` hook runs. An upgrade whose previous version's `uninstall.exe` finds no state therefore proceeds instead of being refused; an `uninstall.exe` from before this behaviour still exits `1` there.

This is only for state that is **absent**. State that exists but is refused — a machine-scope state file an ordinary user could have written, or one that will not parse — still fails the uninstall exactly as before, naming the refusal: reporting it as "not installed" would hide the very thing the refusal exists to surface.

## See also

- [Manifest reference - top-level `uninstall`](../manifest-reference.md#top-level)
- [Install steps](install-steps.md)
- [Migrating from Inno Setup](../migration/from-inno.md)
