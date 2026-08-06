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

## Privileged step targets are anchored — read this before the next four steps

`service_install`, `scheduled_task_create`, `com_register` and `firewall_rule` hand a path from your manifest to something running with SYSTEM-level authority: the SCM launches a service binary, `schtasks` is always invoked with `/RU SYSTEM`, `com_register` loads the DLL **into the elevated installer process** and calls its `DllRegisterServer`, and `firewall_rule`'s `program=` grants that executable the exemption you asked for.

> **Two conditions, both enforced at install time.** The resolved target must
>
> 1. **resolve inside `install_dir`** — after `${…}` / `{…}` substitution and canonicalization, with **no directory junction anywhere on the way down** (junctions need no privilege on Windows, so a link planted inside the install directory is the realistic redirection primitive); and
> 2. **sit in a directory only administrators can write** — owned by `NT AUTHORITY\SYSTEM`, `BUILTIN\Administrators` or `NT SERVICE\TrustedInstaller`, with no write-class right granted to anyone else.
>
> A step whose target fails either condition **fails with a message naming the condition it failed**. Nothing is created and nothing is journaled.

The second condition is the one that matters. A path can be perfectly contained inside `install_dir` and still be replaceable by any unprivileged user — which is exactly the attack: a legitimately signed installer, a UAC prompt an administrator approves, and a SYSTEM-level task or service left pointing at a binary anyone can overwrite.

In practice this means **these four steps need a machine-scope install into an admin-only location**. `%ProgramFiles%` and `%ProgramFiles(x86)%` qualify. A per-user install root under `%LocalAppData%` does not — it is writable by the user who owns it — and neither does `%ProgramData%`, which grants `BUILTIN\Users` create rights by inheritance. `install_dir` is separately constrained to the scope root, so `Setup.exe /allusers /D=C:\Users\Public\evil` is refused before any step runs.

The examples below all use `${parameters.install_dir}\…`, which satisfies both conditions for a machine-scope install. A path hard-coded outside the install directory does not, and will fail the step.

## `service_install`

Registers a Windows service via `sc.exe create`, optionally starts it. Records a `RemoveService` rollback so a failed install or `setup.exe /Uninstall` stops + deletes the service.

> **`binary_path` is a privileged target.** It must resolve inside `install_dir` and sit in a directory no non-administrator can write — see [the anchoring rules above](#privileged-step-targets-are-anchored--read-this-before-the-next-four-steps). Otherwise the step fails and no service is created.

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

## `scheduled_task_create`

Creates a Windows Scheduled Task via `schtasks.exe /Create`, always running the task as `SYSTEM` (`/RU SYSTEM`).

> **Machine-scope only.** This step touches machine-global state, so the manifest must set `installer.scope: machine`. Under `user` or `auto` scope, packing fails with **SIG0310** (`installer.scope: machine` required for this step).

> **`program` is a privileged target.** The task always runs as `SYSTEM` (`/RU SYSTEM`), so `program` must resolve inside `install_dir` and sit in a directory no non-administrator can write — see [the anchoring rules above](#privileged-step-targets-are-anchored--read-this-before-the-next-four-steps). Otherwise the step fails and no task is created.

|Field|Type|Required|Default|Notes|
|---|---|---|---|---|
|`name`|string|yes|-|Task name (`schtasks`'s `/TN`).|
|`program`|string|yes|-|Path to the program the task runs (`/TR`).|
|`arguments`|string|-|-|Arguments appended to `program` in the task's command line.|
|`trigger`|enum|yes|-|`logon`, `daily`, or `onstart`.|
|`run_level`|enum|-|`limited`|`limited` or `highest` (`/RL`).|

For `trigger: daily`, the step always passes a fixed `/ST 00:00` start time rather than the packing machine's wall-clock time — this keeps the produced task deterministic across repeated pack runs of the same manifest. `logon` and `onstart` triggers need no start time. The create is run with `/F` (force overwrite), so a repeat install/repair is idempotent.

Journals a `DeleteScheduledTask` record (task name only) **before** the create, so a mid-install crash or `setup.exe /Uninstall` both run `schtasks /Delete /TN <name> /F` to tear the task down.

```yaml
- id: register-heartbeat-task
  type: scheduled_task_create
  name: MyAppHeartbeat
  program: "${parameters.install_dir}\\heartbeat.exe"
  trigger: daily
  run_level: limited
```

## `com_register`

Self-registers a COM DLL by loading it and invoking its exported `HRESULT DllRegisterServer(void)`.

> **Machine-scope only.** `DllRegisterServer` writes machine-global registration (`HKLM\Software\Classes` / `HKCR\CLSID`), so the manifest must set `installer.scope: machine`. Under `user` or `auto` scope, packing fails with **SIG0310**.

> **`path` is a privileged target — and the sharpest of the four.** The DLL is loaded into the *elevated installer process* and one of its exports is called, so a user-writable path here is arbitrary code execution as administrator. It must resolve inside `install_dir` and sit in a directory no non-administrator can write — see [the anchoring rules above](#privileged-step-targets-are-anchored--read-this-before-the-next-four-steps). Otherwise the step fails and the DLL is never loaded.

|Field|Type|Required|Default|Notes|
|---|---|---|---|---|
|`path`|string|yes|-|Path to the COM DLL to register.|

Journals an `UnregisterCom` record (DLL path only) **before** the register, so a mid-install crash or `setup.exe /Uninstall` both call `DllUnregisterServer` on the same path. A DLL that fails to load or has no `DllRegisterServer` export fails the step with a diagnostic message; on rollback, the same failure modes are tolerated best-effort (mirrors `service_install`'s `RemoveService` pattern).

```yaml
- id: register-shell-extension
  type: com_register
  path: "${parameters.install_dir}\\ShellExt.dll"
```

## `firewall_rule`

Creates a Windows Defender Firewall rule via `netsh advfirewall firewall add rule`.

> **Machine-scope only.** There is no per-user firewall policy store — firewall rules are always machine-global — so the manifest must set `installer.scope: machine`. Under `user` or `auto` scope, packing fails with **SIG0310**.

> **`program` is a privileged target.** When set, it must resolve inside `install_dir` and sit in a directory no non-administrator can write — see [the anchoring rules above](#privileged-step-targets-are-anchored--read-this-before-the-next-four-steps). Otherwise the step fails and no rule is added. A rule with no `program` has no target to anchor and is unaffected.

|Field|Type|Required|Default|Notes|
|---|---|---|---|---|
|`name`|string|yes|-|Rule name (`name=`).|
|`direction`|enum|yes|-|`in` or `out` (`dir=`).|
|`action`|enum|yes|-|`allow` or `block` (`action=`).|
|`program`|string|-|-|Restricts the rule to this executable (`program=`).|
|`port`|int|-|-|Restricts the rule to this local port (`localport=`).|
|`protocol`|enum|-|-|`tcp` or `udp` (`protocol=`). Defaults to `tcp` when `port` is set and `protocol` is left unset; stays unset for a whole-program rule with no port.|

**Reinstall idempotency:** unlike `service_install`/`scheduled_task_create`, a repeated `netsh advfirewall firewall add rule` with a duplicate `name=` adds a *second* rule rather than erroring. To keep a reinstall/repair idempotent, this step deletes any existing rule with the same name (best-effort, tolerating "no rules match") immediately before the add.

Journals a `DeleteFirewallRule` record (rule name only) **before** the delete-then-add, so a mid-install crash or `setup.exe /Uninstall` both run `netsh advfirewall firewall delete rule name=<name>` to tear the rule down.

```yaml
- id: open-app-port
  type: firewall_rule
  name: MyApp Inbound
  direction: in
  action: allow
  program: "${parameters.install_dir}\\app.exe"
  port: 8443
  protocol: tcp
```

## Common fields

Every step accepts the same envelope:

|Field|Required|Default|Notes|
|---|---|---|---|
|`id`|yes|-|Stable identifier. Appears in logs and rollback journal entries.|
|`type`|yes|-|One of the step types above.|
|`when`|-|-|Expression gating execution. See [Conditional installs](conditional-installs.md).|
|`on_failure`|-|`fail`|`rollback` (undo journaled steps), `continue` (log + proceed), or `fail` (abort without rollback).|
|`allow_outside_install_dir`|-|`false`|Opts this step out of destination containment (below). Accepted only by `file_copy`, `file_delete`, `directory_delete`, `http_download`, `ini_write`, `json_edit` and `xml_edit`; on any other step type it is an unrecognized field.|

## Every step destination is contained to `install_dir`

The steps that write, download or delete — `file_copy` (`to`), `file_delete` (`path`), `directory_delete` (`path`), `http_download` (`dest`), `ini_write` / `json_edit` / `xml_edit` (`path`) — resolve their destination and then require it to be **inside `install_dir`**, with no directory junction anywhere on the way down.

Two things this stops. A path that escapes the install directory (`..\..`, an absolute path elsewhere, a junction planted inside `install_dir`) is refused rather than followed; and because `File.WriteAllText` truncates an existing file **in place**, keeping its owner and its access control list, a config edit that landed on an attacker-created placeholder would leave that file attacker-writable after your elevated installer wrote to it.

If a step genuinely needs to write outside the installed application — a machine-wide configuration file under `%ProgramData%` is the usual case — say so on that step:

```yaml
- id: write-machine-config
  type: ini_write
  allow_outside_install_dir: true
  path: "%ProgramData%\\MyApp\\machine.ini"
  section: service
  key: endpoint
  value: "https://api.example.com"
```

The opt-out is per step and deliberate: it is a declaration that this particular write is meant to leave the install tree, not a global switch. It does **not** relax the [privileged-target rules](#privileged-step-targets-are-anchored--read-this-before-the-next-four-steps) on `service_install`, `scheduled_task_create`, `com_register` or `firewall_rule` — those have no opt-out.

### An unresolved token in a path fails the step

A `{token}` that is still present in a path after substitution — `{var.instal_dir}` for a typo'd `installer.vars` entry, say — **fails the step**. It is never written to disk. Previously an unknown brace token was left literal, so a single typo silently created a directory named `{var.instal_dir}` and the install "succeeded". `allow_outside_install_dir` does not suppress this: an unresolved token is a manifest mistake under any containment policy.

## See also

- [Manifest reference - install_steps](../manifest-reference.md#install-steps)
- [Conditional installs](conditional-installs.md)
- [Uninstaller](uninstaller.md)
