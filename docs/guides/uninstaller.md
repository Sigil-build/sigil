# Uninstaller

When you produce an `exe` package, Sigil ships an uninstaller automatically. There is no `WriteUninstaller`-equivalent step to call - the wrapper handles deployment, ARP registration, and rollback-journal replay on your behalf.

## How it works

On a successful install, the wrapper drops a stamped copy of itself to `<install_dir>\uninstaller.exe` (~4 MB, embedded inside `setup.exe` as the `SIGIL_UNINSTALLER_V1` resource). It then writes a per-app entry under:

```
HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\<AppId>
```

with these values:

|Value|Contents|
|---|---|
|`DisplayName`|`app.name`|
|`DisplayVersion`|`app.version`|
|`Publisher`|`app.publisher`|
|`UninstallString`|`"<install_dir>\uninstaller.exe" /S /Uninstall`|
|`EstimatedSize`|Total install footprint in KB|
|`InstallDate`|YYYYMMDD|
|`NoModify` / `NoRepair`|`1`|

Add or Remove Programs reads this key, so your app surfaces there with no extra YAML.

## Two flows

Run either path; both are equivalent:

```bash
"<install_dir>\uninstaller.exe" /S          # silent (Add/Remove Programs uses this)
setup.exe /Uninstall                        # interactive, from the original setup.exe
```

Both enter the wrapper in `Uninstall` mode:

1. Run the manifest's `uninstall:` step list (top to bottom).
2. Replay the rollback journal in reverse order - file restores, registry restores, env-var restores, service stops + deletes, shortcut deletions, etc.
3. Remove the ARP entry.

## The `uninstall:` block

The journal already reverses every install step. Use `uninstall:` only for tear-down the journal can't infer:

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
      - "${parameters.install_dir}\\StopAndRemoveServices.ps1"
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

A mid-install crash leaves the journal in a consistent state: each record is written before the mutation, so power-loss between record and mutation simply produces a no-op rollback entry.

## Silent uninstall

`/S` suppresses any wizard chrome the uninstall flow would otherwise show. Add or Remove Programs always invokes the silent path (`QuietUninstallString` semantics).

## See also

- [Manifest reference - uninstall](../manifest-reference.md#uninstall)
- [Install steps](install-steps.md)
- [Migrating from NSIS - uninstaller mapping](../migration/from-nsis.md#uninstaller-mapping)
