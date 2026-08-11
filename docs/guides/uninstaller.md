# Uninstaller

When you produce an `exe` package, Sigil ships an uninstaller automatically. There is no `WriteUninstaller`-equivalent step to call - the wrapper handles deployment, ARP registration, and rollback-journal replay on your behalf.

## How it works

On a successful install, the wrapper drops a stamped copy of itself to `<install_dir>\uninstall.exe` (~4 MB, embedded inside `setup.exe` as the `SIGIL_UNINSTALLER_V1` resource). It then writes a per-app entry under:

```
HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\<AppId>
```

with these values:

|Value|Contents|
|---|---|
|`DisplayName`|`app.name`|
|`DisplayVersion`|`app.version`|
|`Publisher`|`app.publisher`|
|`UninstallString`|`"<install_dir>\uninstall.exe" /S /Uninstall`|
|`EstimatedSize`|Total install footprint in KB|
|`InstallDate`|YYYYMMDD|
|`NoModify` / `NoRepair`|`1`|

Add or Remove Programs reads this key, so your app surfaces there with no extra YAML.

## Two flows

Run either path; both are equivalent:

```bash
"<install_dir>\uninstall.exe" /S          # silent (Add/Remove Programs uses this)
setup.exe /Uninstall                        # interactive, from the original setup.exe
```

Both enter the wrapper in `Uninstall` mode:

1. Run the manifest's `uninstall:` step list (top to bottom).
2. Replay the rollback journal in reverse order - file restores, registry restores, env-var restores, service stops + deletes, shortcut deletions, etc. Every record is checked against the installation it belongs to before it is replayed; see [Anchored replay and refused records](#anchored-replay-and-refused-records).
3. Remove the ARP entry.

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

`/S` suppresses any wizard chrome the uninstall flow would otherwise show. Add or Remove Programs always invokes the silent path (`QuietUninstallString` semantics).

## See also

- [Manifest reference - uninstall](../manifest-reference.md#uninstall)
- [Install steps](install-steps.md)
- [Migrating from NSIS - uninstaller mapping](../migration/from-nsis.md#uninstaller-mapping)
