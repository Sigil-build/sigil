# Install steps

`install_steps:` is an ordered list of typed actions the wrapper runs at install time. Each step records a reverse operation in the rollback journal before mutating state, so a failure (or a later `setup.exe /Uninstall`) can undo every change byte-identical.

The catalog is a **closed set of 18 step types**, listed in full below. It is not extended by configuration — a new type requires an amendment to [ADR-008](../architecture/adr-008-expression-policy.md) and a change across the whole chain from the manifest model to the runtime. `pre_install:` and `post_install:` accept the same step shapes and run before / after `install_steps:` respectively; `uninstall:` accepts them too.

## Write the destination as `{install_dir}`

Every example on this page writes its destination as `{install_dir}`. That is the installer's own resolved destination, and it is the single value everything else agrees on:

- it defaults to `<scope root>\<app name>` — `%ProgramFiles%\MyApp` for a machine install, `%LocalAppData%\Programs\MyApp` for a per-user one;
- `installer.install_dir:` in the manifest overrides that default;
- the wizard's **Destination** screen writes to it, and `/D=<path>` overrides it on the command line;
- an upgrade reuses the prior version's location;
- and it is what the containment guards described further down anchor on.

**Do not declare a parameter called `install_dir`.** It is tempting — `${parameters.install_dir}` looks like it ought to mean the same thing — but a parameter is an unrelated second value: it does not follow the Destination screen, `/D=`, or an upgrade. The moment a user installs anywhere but the default, your steps write to one place while the installer records another.

**And there is no `%VAR%` expansion in a step path.** A parameter defaulting to `"%ProgramFiles%\\MyApp"` does not resolve to Program Files: `%ProgramFiles%` is taken as a literal directory name, so the path is relative to the installer's working directory and is refused for landing outside `install_dir`. That exact pair of mistakes is what the two shipped manifests under `examples/exe-wrapper/` used to make.

## `file_copy`

Copies one file or a glob pattern. A `from:` (or `to:`) path that starts with the `payload://` scheme is rebased onto the wrapper's extracted payload root — `payload://**` means "everything in the payload," `payload://app/**` means "the `app/` subtree of the payload." A path that does **not** start with `payload://` is resolved relative to the installer's current working directory instead, which is almost never what you want — use `payload://` for every payload-sourced `from:`. The destination is created if missing.

|Field|Type|Required|Default|Notes|
|---|---|---|---|---|
|`from`|string|yes|-|File path or glob. `**` recurses; `*.txt` is non-recursive.|
|`to`|string|yes|-|Destination directory.|
|`overwrite`|bool|-|`true`|Overwrite existing files. When `false`, an existing file at `to` is meant to be left alone (the prior bytes are journaled either way).|

> **Known issue (R77): `overwrite: false` is inert.** The step currently
> replaces an existing file regardless of this setting, so a manifest that
> relies on `overwrite: false` to preserve a user's existing config **will
> overwrite it**. The prior bytes are journaled, so an uninstall or a rollback
> puts the original back — but the file is replaced during the install. Until
> this is fixed, gate the step with `when: "!file_exists('…')"` instead of
> relying on `overwrite: false`.

```yaml
- id: deploy-payload
  type: file_copy
  from: payload://**
  to: "{install_dir}"
  overwrite: true
```

## `directory_create`

Creates a directory (recursively, like `mkdir -p`). No-op if the directory already exists.

```yaml
- id: create-logs-dir
  type: directory_create
  path: "{install_dir}\\logs"
```

## `directory_delete`

Stashes the entire subtree to a temp location for rollback, then deletes. `recursive` **defaults to `false`**, and with `recursive: false` the step fails on a non-empty directory rather than leaving partial state — so that failure mode is the default. Set `recursive: true` explicitly to delete a tree.

```yaml
- id: wipe-prior-install
  type: directory_delete
  path: "{install_dir}"
  recursive: true
  on_failure: continue
```

## `file_delete`

Stashes the file's bytes for rollback, then deletes. `if_missing: skip` (default `fail`) lets the step succeed silently when the file is already gone.

```yaml
- id: clear-stale-config
  type: file_delete
  path: "{install_dir}\\old.cfg"
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

> **None of `hive`, `type_value` or `view` is validated at pack time.** `sigil validate` accepts any spelling; a bad one throws at **install** time, on the end user's machine, not in your build. Copy the values above exactly.

```yaml
- id: stamp-install-dir
  type: registry_write
  hive: HKLM
  key: "Software\\${app.name}"
  name: InstallDir
  # REG_SZ, not REG_EXPAND_SZ: {install_dir} resolves to a literal absolute path,
  # so there is nothing left for the registry to expand at read time.
  type_value: REG_SZ
  value: "{install_dir}"
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
|`location`|`start_menu`, `desktop`, or an explicit directory path — see the containment note below.|
|`name`|Display name; `.lnk` is appended automatically.|
|`args`|List of CLI args appended to the target.|
|`working_dir`|Optional.|
|`icon`|Optional `.ico` path or `exe,index`.|
|`description`|Tooltip.|

```yaml
- id: shortcut-desktop
  type: shortcut_create
  target: "{install_dir}\\app.exe"
  location: desktop
  name: MyApp
  description: "Launch MyApp"
```

> **`location` is anchored, but not to `install_dir`.** The whole point of `desktop` and `start_menu` is to write outside the installed application, so the [`install_dir` containment](#every-step-destination-is-contained-to-install_dir) that governs `file_copy` and the config editors cannot apply here. A wider anchor does: the resolved directory must sit under `install_dir`, under a **Start Menu** folder, or under a **Desktop** folder — either scope's — with no directory junction on the way down. Everything an installer normally does is inside that: a vendor subfolder such as `location: "C:\\ProgramData\\Microsoft\\Windows\\Start Menu\\Programs\\Contoso"`, a Startup shortcut, a `{install_dir}\\Tools` folder.
>
> Anything else **fails the step**, with the directory not created and the `.lnk` not written. There is no `allow_outside_install_dir` opt-out on this step. Before this rule, an explicit `location` was checked by nothing at all: an elevated install would `mkdir -p` a tree anywhere on the volume and journal a `DeleteShortcut` that unlinks that exact path at rollback or uninstall, whether or not this installer created it — which matters as soon as `location` carries a `${parameters.…}` or `{var.…}` value sourced from a wizard field or a `registry_read`.

## `env_set`

Writes a Windows environment variable to the user or machine hive (machine scope requires admin) and broadcasts `WM_SETTINGCHANGE` so running shells pick the change up without a logoff. Snapshots the prior value for rollback.

|Field|Notes|
|---|---|
|`name`|**Required.** The environment variable's name.|
|`value`|**Required.** The value to set, append or prepend.|
|`scope`|`user` (default) or `machine`.|
|`action`|`set` (default), `append`, or `prepend`.|
|`separator`|Delimiter for `append` / `prepend` (default `;`). Ignored when the prior value is empty or absent.|

```yaml
- id: env-app-home
  type: env_set
  scope: machine
  name: MYAPP_HOME
  value: "{install_dir}"
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
  program: "{install_dir}\\SystemActions.exe"
  args: ["${parameters.domain_name}", "${parameters.server_ip}"]
  wait: true
  expected_exit_codes: [0]
  timeout_seconds: 600
```

## `http_download`

Downloads a file over HTTPS and verifies it against a mandatory SHA-256 checksum before anything else sees it. Records a `RestoreFile` rollback, exactly like `file_copy`: a pre-existing file at `dest` is stashed and put back on rollback or uninstall, and a newly created one is deleted.

|Field|Type|Required|Default|Notes|
|---|---|---|---|---|
|`url`|string|yes|-|**Must start with `https://`.** A literal `http://` URL is rejected at pack time (**SIG0235**); a URL built from `{var.…}` / `{install_dir}` tokens is re-checked at install time and fails the step.|
|`dest`|string|yes|-|Destination file path. Subject to the same [`install_dir` containment](#every-step-destination-is-contained-to-install_dir) as `file_copy`'s `to`.|
|`sha256`|string|**yes**|-|Hex SHA-256 of the expected file. **Required** — packing a download with no integrity check is refused with **SIG0236**. A mismatch fails the step immediately and is never retried: it is not a transient condition.|
|`timeout_seconds`|int|-|`300`|Per-attempt timeout.|
|`retries`|int|-|`0`|Additional attempts after the first, with exponential backoff, for transient network failures only. Total attempts are `1 + retries`. There is no resume — a retry restarts the download.|

```yaml
- id: fetch-model
  type: http_download
  url: "https://cdn.example.com/models/v3.bin"
  dest: "{install_dir}\\models\\v3.bin"
  sha256: "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"
  timeout_seconds: 600
  retries: 2
```

> `{staging_dir}` is the one destination that gets extra handling: a binary downloaded there is re-verified through a held file handle and Authenticode-checked immediately before it is launched. That is how the web-installer stub fetches its payload — see [Updates](updates.md). For an ordinary payload file, `{install_dir}\…` is the right destination.

## `ini_write`

Writes a single `key=value` entry into an INI file, stashing the file's prior content for rollback.

|Field|Type|Required|Default|Notes|
|---|---|---|---|---|
|`path`|string|yes|-|The INI file to edit.|
|`key`|string|yes|-|The key to write.|
|`section`|string|-|`""`|The section to write into. The default, an empty string, means the keys that appear **before the first `[section]` header**.|
|`value`|string|-|`""`|The value to write.|
|`create_if_missing`|bool|-|`false`|When `false` (the default) and the file does not exist, the step **fails**.|

See also [`ini_write` values cannot inject INI lines](#ini_write-values-cannot-inject-ini-lines) for the newline / leading-`[` rejection rules.

## `json_edit`

Sets one node in a JSON document, addressed by JSON Pointer, stashing the file's prior content for rollback.

|Field|Type|Required|Default|Notes|
|---|---|---|---|---|
|`path`|string|yes|-|The JSON file to edit.|
|`pointer`|string|yes|-|RFC 6901 JSON Pointer to the node. Must start with `/`.|
|`value`|string|-|`""`|The value to write.|
|`value_type`|enum|-|`string`|`string` or `json` — see [below](#json_edit-writes-a-string-unless-you-say-value_type-json).|
|`create_if_missing`|bool|-|`false`|When `false` (the default) and the file does not exist, the step **fails**.|

## `xml_edit`

Sets an element's text or an attribute's value in an XML document, addressed by XPath, stashing the file's prior content for rollback.

|Field|Type|Required|Default|Notes|
|---|---|---|---|---|
|`path`|string|yes|-|The XML file to edit.|
|`xpath`|string|yes|-|XPath selecting the element to edit.|
|`attribute`|string|-|(none)|The attribute to set. Omit it to set the selected element's **text** instead.|
|`value`|string|-|`""`|The value to write.|
|`create_if_missing`|bool|-|`false`|When `false` (the default), a missing file **fails** the step and so does an `xpath` that matches no element. When `true`, a missing element is created — but only for a **simple absolute path** of the form `/a/b/c`; anything with predicates, wildcards or axes still fails.|

See also [`xml_edit` refuses a document that declares a `<!DOCTYPE>`](#xml_edit-refuses-a-document-that-declares-a-doctype).

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

The examples below all use `{install_dir}\…`, which satisfies both conditions for a machine-scope install. A path hard-coded outside the install directory does not, and will fail the step.

> **A `payload://` target is refused for these four steps.** `payload://` resolves to the temporary directory the embedded payload is extracted into (`%TEMP%\sigil-…`), which fails both conditions: it is writable by the user who launched the installer, and Sigil **deletes it when the run finishes** — so a service or scheduled task pointing into it would be left pointing at a path that no longer exists. Sequence a `file_copy` from `payload://` into `install_dir` first, then point the privileged step at the copied location. That is the ordering `service_install` already required for its own reasons.

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

> **Neither `start_type` nor `service_account` is validated at pack time.** An unrecognized `start_type` silently falls back to `auto`, and an unrecognized `service_account` silently falls back to `LocalSystem` — so `start_type: automatic` or `service_account: NetworkServices` ships without a diagnostic and installs a service configured differently from what the manifest says. Spell them exactly as listed above.

```yaml
- id: install-update-service
  type: service_install
  name: MyAppUpdateService
  binary_path: "{install_dir}\\Updater.exe"
  display_name: "MyApp Update Service"
  description: "Background updater for MyApp."
  start_type: auto
  service_account: LocalSystem
  start_after_install: true
```

## `scheduled_task_create`

Creates a Windows Scheduled Task via `schtasks.exe /Create`, always running the task as `SYSTEM` (`/RU SYSTEM`).

> **Machine-scope only.** This step touches machine-global state, so the manifest must set `installer.scope: machine`. Under `user` or `auto` scope, packing fails with **SIG0310** (`installer.scope: machine` required for this step).
>
> **`program` is a privileged target.** The task always runs as `SYSTEM` (`/RU SYSTEM`), so `program` must resolve inside `install_dir` and sit in a directory no non-administrator can write — see [the anchoring rules above](#privileged-step-targets-are-anchored--read-this-before-the-next-four-steps). Otherwise the step fails and no task is created.

|Field|Type|Required|Default|Notes|
|---|---|---|---|---|
|`name`|string|yes|-|Task name (`schtasks`'s `/TN`).|
|`program`|string|yes|-|Path to the program the task runs (`/TR`).|
|`arguments`|string|-|-|Arguments appended to `program` in the task's command line.|
|`trigger`|enum|yes|-|`logon`, `daily`, or `onstart`.|
|`run_level`|enum|-|`limited`|`limited` or `highest` (`/RL`).|

> **`program` must not contain a double quote.** `schtasks` parses the `/TR` value as its own miniature command line, so an embedded `"` moves where the executable token ends — the task would run something other than what the manifest says. A quote in `program` fails the step. This is only about author-supplied quotes: the quoting a spaced path needs is added for you, and `arguments` is unrestricted (quoted flag values there are ordinary and cannot displace the executable).

For `trigger: daily`, the step always passes a fixed `/ST 00:00` start time rather than the packing machine's wall-clock time — this keeps the produced task deterministic across repeated pack runs of the same manifest. `logon` and `onstart` triggers need no start time. The create is run with `/F` (force overwrite), so a repeat install/repair is idempotent.

Journals a `DeleteScheduledTask` record (task name only) **before** the create, so a mid-install crash or `setup.exe /Uninstall` both run `schtasks /Delete /TN <name> /F` to tear the task down.

```yaml
- id: register-heartbeat-task
  type: scheduled_task_create
  name: MyAppHeartbeat
  program: "{install_dir}\\heartbeat.exe"
  trigger: daily
  run_level: limited
```

## `com_register`

Self-registers a COM DLL by loading it and invoking its exported `HRESULT DllRegisterServer(void)`.

> **Machine-scope only.** `DllRegisterServer` writes machine-global registration (`HKLM\Software\Classes` / `HKCR\CLSID`), so the manifest must set `installer.scope: machine`. Under `user` or `auto` scope, packing fails with **SIG0310**.
>
> **`path` is a privileged target — and the sharpest of the four.** The DLL is loaded into the *elevated installer process* and one of its exports is called, so a user-writable path here is arbitrary code execution as administrator. It must resolve inside `install_dir` and sit in a directory no non-administrator can write — see [the anchoring rules above](#privileged-step-targets-are-anchored--read-this-before-the-next-four-steps). Otherwise the step fails and the DLL is never loaded.

|Field|Type|Required|Default|Notes|
|---|---|---|---|---|
|`path`|string|yes|-|Path to the COM DLL to register.|

Journals an `UnregisterCom` record (DLL path only) **before** the register, so a rollback later in the run or `setup.exe /Uninstall` both call `DllUnregisterServer` on the same path. A DLL that fails to load or has no `DllRegisterServer` export fails the step with a diagnostic message **and withdraws that record** — not because Sigil can prove nothing was written, but because the undo it would be keeping cannot be *called*, so replaying it could only ever report a registration it never managed to probe. A `DllRegisterServer` that runs and returns a failure `HRESULT` keeps its record: it may have written part of its registration before giving up, and its `DllUnregisterServer` is callable.

> **Your `DllUnregisterServer` has to work.** Unlike a missing service or firewall rule, a COM registration cannot be queried — calling `DllUnregisterServer` is the only way to find out — so Sigil treats anything other than `S_OK` from it as *the registration is still in place*, reports the uninstall as failed, and **keeps** `uninstall.json` and the Add/Remove Programs entry so the user can retry. Ship both exports, and make `DllUnregisterServer` return `S_OK` when there is nothing left to remove.
>
> **And do not register from `DllMain`.** Put every registry write in `DllRegisterServer` and every removal in `DllUnregisterServer`. `DllMain` runs on load — before Sigil has looked for `DllRegisterServer` at all, and even on the load that ultimately *fails* (`LoadLibraryEx` reports failure when `DllMain` returns FALSE, after it has already executed). Anything your `DllMain` writes is therefore outside what the step can reason about or reverse.

> **What this step grants — read before you write one.** `com_register` is an explicit grant of **arbitrary code execution as administrator** to the DLL you name. Sigil loads it into the elevated installer process and calls one of its exports; it guarantees only that the DLL is the one you shipped, sitting where only administrators can have put it (the anchoring rules above). It cannot guarantee anything about what your `DllRegisterServer` then does.
>
> Two practical consequences. A DLL that **faults** takes the installer down with it, and **nothing is unwound**: the rollback journal is held in memory and is only written to `uninstall.json` when the install commits, so a process that dies mid-run leaves both the files already laid down and whatever your `DllRegisterServer` had written, with no record of either — the user has to clean up by hand. (An earlier version of this guide claimed the journal was already on disk and the install would still unwind; it is not, and it does not.) There is also **no timeout**, so a `DllRegisterServer` that hangs wedges the install. Keep self-registration code short and defensive, and prefer failing with a non-zero `HRESULT` over throwing.
>
> This is deliberate, not an oversight. Running the DLL in a `regsvr32.exe` child would not lower its privileges — a child inherits the installer's elevated token, and machine-global COM registration is what the step is *for* — so it would buy crash isolation at the cost of bitness guesswork, unusable exit codes, and a heavily EDR-flagged process launch in every installer. The reasoning is recorded in [ADR-012](../architecture/adr-012-com-registration-isolation.md).

```yaml
- id: register-shell-extension
  type: com_register
  path: "{install_dir}\\ShellExt.dll"
```

## `firewall_rule`

Creates a Windows Defender Firewall rule via `netsh advfirewall firewall add rule`.

> **Machine-scope only.** There is no per-user firewall policy store — firewall rules are always machine-global — so the manifest must set `installer.scope: machine`. Under `user` or `auto` scope, packing fails with **SIG0310**.
>
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
  program: "{install_dir}\\app.exe"
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
|`on_failure`|-|`fail`|`rollback` (undo journaled steps), `continue` (log + proceed), or `fail` (abort). Note the default differs by phase: it is `fail` for `install_steps:`, `pre_install:`, `post_install:` and `uninstall:`, and for `installer.hooks.pre_*`, but **`continue`** for `installer.hooks.post_*`.|
|`allow_outside_install_dir`|-|`false`|Opts this step out of destination containment (below). Accepted only by `file_copy`, `directory_create`, `file_delete`, `directory_delete`, `http_download`, `ini_write`, `json_edit` and `xml_edit`; on any other step type it is an unrecognized field and has no effect.|

> **Known issue (R78): `on_failure: fail` and `on_failure: rollback` behave identically.** Both take the same path in the engine and both replay the **entire** rollback journal in reverse, across all phases. There is currently no "abort without rollback" mode and no "undo only up to this step" mode. Only `continue` is distinct. Treat the two as synonyms until this is fixed; if you need a step's failure not to unwind the install, use `on_failure: continue` and check the condition yourself.

## Every step destination is contained to `install_dir`

The steps that create, write, download or delete — `file_copy` (`to`), `directory_create` (`path`), `file_delete` (`path`), `directory_delete` (`path`), `http_download` (`dest`), `ini_write` / `json_edit` / `xml_edit` (`path`) — resolve their destination and then require it to be **inside `install_dir`**, with no directory junction anywhere on the way down.

Two things this stops. A path that escapes the install directory (`..\..`, an absolute path elsewhere, a junction planted inside `install_dir`) is refused rather than followed; and because `File.WriteAllText` truncates an existing file **in place**, keeping its owner and its access control list, a config edit that landed on an attacker-created placeholder would leave that file attacker-writable after your elevated installer wrote to it.

If a step genuinely needs to write outside the installed application — a machine-wide configuration file under ProgramData is the usual case — say so on that step:

```yaml
- id: write-machine-config
  type: ini_write
  allow_outside_install_dir: true
  path: "C:\\ProgramData\\MyApp\\machine.ini"
  section: service
  key: endpoint
  value: "https://api.example.com"
```

> **There is no `%VAR%` expansion in a step path.** `%ProgramData%\MyApp` is not a path — it is a relative directory whose first component is literally `%ProgramData%`, and it would be created as such next to the running installer. The only substitutions a step path gets are the `{…}` runtime tokens listed under [Paths and tokens](#an-unresolved-token-in-a-path-fails-the-step) and `${…}` parameter templates. If you want the path written once rather than hard-coded per environment, `${ProgramData}` is expanded by **`sigil pack`** from the packing machine's environment — which means the resolved literal is baked into the package, so use it knowingly.

The opt-out is per step and deliberate: it is a declaration that this particular write is meant to leave the install tree, not a global switch. It does **not** relax the [privileged-target rules](#privileged-step-targets-are-anchored--read-this-before-the-next-four-steps) on `service_install`, `scheduled_task_create`, `com_register` or `firewall_rule` — those have no opt-out.

> **Known limitation — an out-of-tree write is not undone at uninstall (register row R44).** The step above *is* journaled, but the uninstaller's replay anchor currently accepts only records that point inside `install_dir`. A `RestoreConfigFile` naming `C:\ProgramData\MyApp\machine.ini` is therefore **refused during replay, while the Add/Remove Programs entry and the install state are removed anyway** — so the file you wrote stays on disk after uninstall and nothing tells the user.
>
> Until R44 lands (it resolves declared out-of-tree roots from the signed blob and widens the anchor to match), treat `allow_outside_install_dir` as **install-only**: use it for content you are content to leave behind, or delete that content explicitly from an `uninstall:` step, which runs before the journal replays.

### `ini_write` values cannot inject INI lines

`section`, `key` and `value` are substituted and then concatenated into a single `key=value` line, so a value of `9\n[admin]\nenabled=true` used to write entries into an entirely different section — which matters as soon as the value comes from a wizard field or a `registry_read` var rather than a literal. All three fields now **reject a carriage return, a line feed, or a leading `[`** (how a section header begins), and the step fails with the file untouched.

Rejected rather than escaped: an INI file has no escape for a newline inside a value, so escaping would silently mangle what you wrote. All three are pack-time-authored, so failing puts the problem in front of the publisher. The leading-`[` rule is deliberately conservative — a `[` in a *value* cannot itself create a section — so write `value: " [1,2,3]"` or quote it differently if you need one.

### `json_edit` writes a string unless you say `value_type: json`

|Field|Type|Required|Default|Notes|
|---|---|---|---|---|
|`value_type`|enum|-|`string`|`string` writes the resolved `value` as a JSON string, always. `json` parses it and writes the resulting number, boolean, `null`, array or object.|

```yaml
- id: set-endpoint          # writes  "endpoint": "https://api.example.com"
  type: json_edit
  path: "{install_dir}\\appsettings.json"
  pointer: /Api/endpoint
  value: "${parameters.endpoint}"

- id: set-worker-count      # writes  "workers": 4
  type: json_edit
  path: "{install_dir}\\appsettings.json"
  pointer: /Api/workers
  value: "4"
  value_type: json
```

The step used to infer the type from the value: it ran every resolved `value` through a JSON parser and kept whatever came back, falling back to a string only when the parse failed. That is reasonable for a literal you typed into the manifest and wrong for everything else, because the same field also carries values resolved from a **wizard field**, a `registry_read` var or a `/P<name>=` argument — and those then chose the *shape* of the node written into your application's configuration. A value of `{"admin":true}` where you wrote and reviewed a string becomes an object your application reads as one. The encoding was never unsafe; the output is always well-formed JSON. What was unsafe is that the value's supplier picked its type.

So `string` is the default and the old inference is the opt-in. Two consequences worth knowing:

- **A manifest that meant a number now writes a string** until you add `value_type: json`. That is a visible, one-line fix; the reverse — a silent type change driven by user input — is not.
- Under `value_type: json`, a value that is **not** valid JSON **fails the step** rather than quietly falling back to a string. With the intent declared, a non-parsing value is a manifest error, and the fallback would just be a second way for the supplier to pick the type.

### `xml_edit` refuses a document that declares a `<!DOCTYPE>`

The XML a step edits is parsed with `XmlResolver = null` and `DtdProcessing = Prohibit`. Two separate guarantees:

- **No external entity is ever dereferenced.** A `<!ENTITY x SYSTEM "file:///C:/…">` or `SYSTEM "http://…"` in the target file cannot make the elevated installer read a local file or reach the network on the document's behalf. .NET already defaults to this; Sigil sets it explicitly so that a future framework default, `AppContext` switch or runtimeconfig knob cannot revoke the guarantee without this assignment also changing.
- **No DTD is parsed at all**, including a purely internal subset with no external references. This is the half the resolver default never covered: an internal subset can define nested entities whose expansion is exponential (the "billion laughs" shape), and — per the containment note above — the file being edited can sit somewhere an attacker is able to write.

A document that declares a `<!DOCTYPE>` therefore **fails the step with the file untouched** rather than being edited, whether or not the doctype is expensive. If you must edit such a file, strip the doctype at pack time, or do the edit from a `run_program` step that owns its own parsing policy. Declarations, comments, processing instructions and whitespace all still survive an ordinary edit.

### An unresolved token in a path fails the step

A `{token}` that is still present in a path after substitution — `{var.instal_dir}` for a typo'd `installer.vars` entry, say — **fails the step**. It is never written to disk. Previously an unknown brace token was left literal, so a single typo silently created a directory named `{var.instal_dir}` and the install "succeeded".

This applies to **every** path-valued field of every step, not just the destinations listed above: it is enforced where paths are resolved, so `run_program.program`, `shortcut_create.target`, `service_install.binary_path` and `scheduled_task_create.program` are covered too. `allow_outside_install_dir` does not suppress it — an unresolved token is a manifest mistake under any containment policy.

The tokens a step path may use are:

|Token|Resolves to|
|---|---|
|`{install_dir}`|The installer's resolved destination.|
|`{scope_root}`|The install root for the resolved scope (`%ProgramFiles%` / `%LocalAppData%\Programs`).|
|`{app.name}` / `{app.id}`|The manifest's `app.name` / `app.id`.|
|`{var.<name>}`|Each declared `installer.vars` entry.|
|`{temp_dir}`|The per-user temp directory, with no trailing separator.|
|`{staging_dir}`|A freshly created, GUID-named private directory under temp. This is the security-relevant one: a binary downloaded here gets the held-handle re-verification and the pre-launch Authenticode check. It is created as a side effect of resolving the token.|

Plus `${parameters.<name>}` templates — `${param.<name>}` is an accepted alias for the same thing, and it is the spelling the schema and ADR-008 use. Anything else in braces that looks like an identifier is treated as a mistake.

Two different substitution passes, easily confused: `${…}` is a **template** over the identifier table (parameters, `app.*`, `option.*`, `var.*`, `scope`, …), and an unknown identifier there is a hard `FormatException` at install time. `{…}` is the **runtime token** pass listed above, and an unknown token left in a *path* fails the step. See [Conditional installs](conditional-installs.md) for the full identifier table.

## See also

- [Manifest reference](../manifest-reference.md)
- [Conditional installs](conditional-installs.md)
- [Uninstaller](uninstaller.md)
