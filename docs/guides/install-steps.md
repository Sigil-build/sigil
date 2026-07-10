# Install steps

`install_steps:` is an ordered list of typed actions the wrapper runs at install time. Each step records a reverse operation in the rollback journal before mutating state, so a failure (or a later `setup.exe /Uninstall`) can undo every change byte-identical.

The MUST-tier step set below is what's shipped today. `pre_install:` and `post_install:` accept the same step shapes and run before / after `install_steps:` respectively.

## `file_copy`

Copies one file or a glob pattern. Source paths are evaluated relative to the wrapper's extracted `payload/` directory; the destination is created if missing.

|Field|Type|Required|Default|Notes|
|---|---|---|---|---|
|`from`|string|yes|-|File path or glob. `**` recurses; `*.txt` is non-recursive.|
|`to`|string|yes|-|Destination directory.|
|`overwrite`|bool|-|`true`|Overwrite existing files. When `false`, an existing file at `to` is left alone (but the prior bytes are still journaled).|

```yaml
- id: deploy-payload
  type: file_copy
  from: payload/**
  to: ${parameters.install_dir}
  overwrite: true
```

## `directory_create`

Creates a directory (recursively, like `mkdir -p`). No-op if the directory already exists.

```yaml
- id: create-logs-dir
  type: directory_create
  path: "${parameters.install_dir}\\logs"
```

## `directory_delete`

Stashes the entire subtree to a temp location for rollback, then deletes. With `recursive: false`, fails on a non-empty directory rather than leaving partial state.

```yaml
- id: wipe-prior-install
  type: directory_delete
  path: "${parameters.install_dir}"
  recursive: true
  on_failure: continue
```

## `file_delete`

Stashes the file's bytes for rollback, then deletes. `if_missing: skip` (default `fail`) lets the step succeed silently when the file is already gone.

```yaml
- id: clear-stale-config
  type: file_delete
  path: "${parameters.install_dir}\\old.cfg"
  if_missing: skip
```

## `registry_write`

Writes a typed value under the requested hive / key / view. Snapshots the prior value for rollback first.

|Field|Notes|
|---|---|
|`hive`|`HKLM`, `HKCU`, `HKCR`, `HKU`, `HKCC`.|
|`key`|Subkey path. Supports `${...}` substitution.|
|`name`|Value name (use `""` for the default value). Supports `${...}` substitution.|
|`type_value`|`REG_SZ` (default), `REG_EXPAND_SZ`, `REG_DWORD`, `REG_QWORD`, `REG_MULTI_SZ`, `REG_BINARY`.|
|`value`|Scalar, list (for `REG_MULTI_SZ`), or hex string (for `REG_BINARY`).|
|`view`|`native` (default), `32bit`, `64bit`.|

> `value_type` is accepted as a legacy alias for `type_value`.

```yaml
- id: stamp-install-dir
  type: registry_write
  hive: HKLM
  key: "Software\\${app.name}"
  name: InstallDir
  type_value: REG_EXPAND_SZ
  value: ${parameters.install_dir}
```

## `registry_delete_value`

Snapshots the prior value for rollback, then deletes. Absent values are tolerated silently.

```yaml
- id: drop-legacy-flag
  type: registry_delete_value
  hive: HKCU
  key: "Software\\MyApp"
  name: LegacyMode
```

## `registry_delete_key`

Snapshots the immediate key's values for rollback, then deletes the key. Optionally recursive.

> KNOWN GAP: with `recursive: true`, only the top-level values are journaled; nested subkeys are not currently restorable. Use sparingly.

```yaml
- id: drop-old-config-tree
  type: registry_delete_key
  hive: HKCU
  key: "Software\\MyApp\\OldConfig"
  recursive: true
```

## `shortcut_create`

Writes a `.lnk` to a named anchor or an explicit directory. The journal records a `DeleteShortcut` so rollback removes it.

|Field|Notes|
|---|---|
|`target`|Path to the program.|
|`location`|`start_menu`, `desktop`, or any explicit directory path.|
|`name`|Display name; `.lnk` is appended automatically.|
|`args`|List of CLI args appended to the target.|
|`working_dir`|Optional.|
|`icon`|Optional `.ico` path or `exe,index`.|
|`description`|Tooltip.|

```yaml
- id: shortcut-desktop
  type: shortcut_create
  target: "${parameters.install_dir}\\app.exe"
  location: desktop
  name: MyApp
  description: "Launch MyApp"
```

## `env_set`

Writes a Windows environment variable to the user or machine hive (machine scope requires admin) and broadcasts `WM_SETTINGCHANGE` so running shells pick the change up without a logoff. Snapshots the prior value for rollback.

|Field|Notes|
|---|---|
|`scope`|`user` (default) or `machine`.|
|`action`|`set` (default), `append`, or `prepend`.|
|`separator`|Delimiter for `append` / `prepend` (default `;`). Ignored when the prior value is empty or absent.|

```yaml
- id: env-app-home
  type: env_set
  scope: machine
  name: MYAPP_HOME
  value: "${parameters.install_dir}"
```

## `run_program`

Spawns an external executable and (optionally) waits for it to exit, asserting the exit code is in `expected_exit_codes`.

Records NO journal entry: an external process is not invertible. If `run_program` fails with `on_failure: rollback`, the engine walks back over previous steps' journal records.

|Field|Notes|
|---|---|
|`program`|Path or PATH-resolved binary.|
|`args`|List of arguments; each is independently quoted by the runtime.|
|`wait`|`true` (default) blocks until exit; `false` fires-and-forgets.|
|`cwd`|Working directory.|
|`expected_exit_codes`|List of acceptable exit codes (default `[0]`).|
|`timeout_seconds`|Kill + fail if the child exceeds this.|

```yaml
- id: run-system-setup
  type: run_program
  program: "${parameters.install_dir}\\SystemActions.exe"
  args: ["${parameters.domain_name}", "${parameters.server_ip}"]
  wait: true
  expected_exit_codes: [0]
  timeout_seconds: 600
```

## `service_install`

Registers a Windows service via `sc.exe create`, optionally starts it. Records a `RemoveService` rollback so a failed install or `setup.exe /Uninstall` stops + deletes the service.

|Field|Notes|
|---|---|
|`name`|Service name (`sc.exe`'s positional name).|
|`binary_path`|Path to the service executable. Must exist before this step runs (sequence `file_copy` first).|
|`display_name`|Defaults to `name`.|
|`description`|Optional `sc description` value.|
|`start_type`|`auto` (default), `demand`, `disabled`, `boot`, `system`.|
|`service_account`|`LocalSystem` (default), `NetworkService`, `LocalService`.|
|`start_after_install`|Default `true`. `sc start` is best-effort; "already running" is treated as success.|

```yaml
- id: install-update-service
  type: service_install
  name: MyAppUpdateService
  binary_path: "${parameters.install_dir}\\Updater.exe"
  display_name: "MyApp Update Service"
  description: "Background updater for MyApp."
  start_type: auto
  service_account: LocalSystem
  start_after_install: true
```

## Common fields

Every step accepts the same envelope:

|Field|Required|Default|Notes|
|---|---|---|---|
|`id`|yes|-|Stable identifier. Appears in logs and rollback journal entries.|
|`type`|yes|-|One of the step types above.|
|`when`|-|-|Expression gating execution. See [Conditional installs](conditional-installs.md).|
|`on_failure`|-|`fail`|`rollback` (undo journaled steps), `continue` (log + proceed), or `fail` (abort without rollback).|

## See also

- [Manifest reference - install_steps](../manifest-reference.md#install-steps)
- [Conditional installs](conditional-installs.md)
- [Uninstaller](uninstaller.md)
