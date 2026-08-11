# Migrating from Inno Setup to Sigil

> Accurate as of the feature-parity track's close (P0–P13, `main`). Every
> mapping below points at a shipped Sigil feature — nothing here is
> aspirational. Where Inno has no Sigil equivalent, that is called out
> explicitly as a deliberate non-goal rather than left implicit.

Inno Setup's `.iss` script is organized into `[Section]` blocks. Sigil's
`sigil.yaml` is one declarative manifest: no sections, no execution order to
reason about beyond `install_steps:`'s own list order (plus the lifecycle
hooks and prerequisites that run around it — see below).

## `[Setup]` → manifest top-level + `installer:`

| Inno directive | Sigil equivalent | Notes |
|---|---|---|
| `AppId` | `app.id` | Reverse-DNS style recommended; same role as Inno's GUID — identifies the ARP entry and upgrade target across versions. |
| `AppName` | `app.name` | — |
| `AppVersion` | `app.version` | Plain numeric dotted version (`1.2.3`) for precise [upgrade/downgrade](../guides/upgrades.md) comparison; SemVer pre-release tags (`1.2.0-rc1`) are not parsed as pre-release and fall back to lexicographic comparison. |
| `AppPublisher` | `app.publisher` | Stamped into the ARP entry and the Authenticode signature's subject line. |
| `DefaultDirName` | `installer.install_dir` | Sigil's install-dir resolution chain (wizard-collected path → `/D=` → prior install dir on upgrade → `installer.install_dir` → scope-root default, `InstallDirResolver.cs:15-23`) supersedes Inno's `{autopf}`-style constant expansion; use the `{scope_root}` / `{app.name}` brace tokens inside the value, not a `parameters.install_dir` declaration — see the warning in [Parameters](../guides/parameters.md#cli-overrides-at-install-time). |
| `DefaultGroupName` | `shortcut_create` step(s) with `location: start_menu` | No separate "group" concept — declare one `shortcut_create` per shortcut you want in the Start Menu folder. |
| `PrivilegesRequired` (`admin`/`lowest`/`none`) | `installer.scope` (`machine`/`user`/`auto`) | Direct analog. |
| `PrivilegesRequiredOverridesAllowed` | `installer.scope: auto` | Sigil's `auto` scope resolves per-machine-vs-per-user the same way Inno 6's override mechanism does, without a separate flag. |
| `OutputBaseFilename` / `OutputDir` | `sigil pack --out <dir>` | Sigil names the artifact `<App>-<version>-<arch>-Setup.exe` deterministically; there is no free-form rename knob. |
| `Compression` / `SolidCompression` | (automatic) | Sigil always packs the payload with zstd (`SIGIL_PAYLOAD_V2`) for deterministic, reproducible builds — not user-tunable. |
| `SetupMutex` | (automatic) | Sigil's `Setup.exe` takes its own single-instance mutex unconditionally (gap G17, shipped with P6) — there is no manifest field to name or disable it, unlike Inno's opt-in `SetupMutex`. |
| `UninstallDisplayIcon` / `UninstallDisplayName` | (automatic, from `installer.icon` / `app.name`) | The generated `uninstall.exe` and its ARP row inherit these from the manifest; no separate keys. |
| `MinVersion` / `OnlyBelowVersion` | `when: os_version(...)` on a step, or a `prerequisites[]` entry with a `detect` expression | No dedicated OS-floor field yet; expression-gate the steps that need it. |
| `Uninstallable=no` | omit `uninstall:` entirely | An app with no `uninstall:` block gets no `uninstall.exe` and no ARP registration, matching Inno's `Uninstallable=no`. |

## `[Files]` → `file_copy`

| Inno directive | Sigil equivalent | Notes |
|---|---|---|
| `Source: "..."; DestDir: "{app}"` | `file_copy` step (`from` / `to`) | `from` is relative to the packed `payload/` directory; `to` is the destination, created if missing. |
| `Flags: recursesubdirs` | `from: payload/**` | `**` recurses; `*.ext` (no `**`) is non-recursive, matching the guide's glob semantics. |
| `Flags: onlyifdoesntexist` | `file_copy` with `overwrite: false` | An existing file at `to` is left alone; its prior bytes are still journaled for rollback either way. |
| `Flags: deleteafterinstall` | (no direct equivalent) | Stage transient files under a hook (`pre_install`/`post_install`) with `run_program`, or omit them from the payload entirely. |

## `[Icons]` → `shortcut_create`

| Inno directive | Sigil equivalent | Notes |
|---|---|---|
| `Name: "{group}\MyApp"; Filename: "{app}\MyApp.exe"` | `shortcut_create` (`location: start_menu`) | `name` is the display name (`.lnk` appended automatically); `target` is the program path. |
| `Name: "{commondesktop}\MyApp"` | `shortcut_create` (`location: desktop`) | — |
| `Parameters: "..."` | `args` (list) | — |
| `WorkingDir: "..."` | `working_dir` | — |
| `IconFilename: "..."` | `icon` (`.ico` path, or `exe,index`) | — |
| `Comment: "..."` | `description` | Shortcut tooltip. |
| `Tasks: desktopicon` (gating a shortcut by a `[Tasks]` checkbox) | `when: option.<name>` on the `shortcut_create` step | See `[Tasks]` mapping below. |

## `[Registry]` → `registry_write` / `registry_delete_value` / `registry_delete_key`

| Inno directive | Sigil equivalent | Notes |
|---|---|---|
| `Root: HKLM; Subkey: "..."; ValueType: string; ValueName: "..."; ValueData: "..."` | `registry_write` (`hive`, `key`, `name`, `type_value`, `value`) | `type_value` accepts `REG_SZ`/`REG_EXPAND_SZ`/`REG_DWORD`/`REG_QWORD`/`REG_MULTI_SZ`/`REG_BINARY`. |
| `Flags: uninsdeletevalue` | (automatic) | `registry_write` snapshots the prior value and journals a delete; rollback and `uninstall.exe` both reverse it without a separate flag. |
| `Flags: uninsdeletekeyifempty` / `uninsdeletekey` | `registry_delete_key` (`recursive: true` for the tree form) | Declare the delete explicitly in `uninstall:` rather than relying on an implicit flag. |
| `RegQueryStringValue` / reading a value in `[Code]` | `registry_read(hive, key, value)` expression function → `installer.vars` | Declarative equivalent of Inno's `[Code]`-based registry read; exposed as `var.<name>` in `when:` clauses, screen defaults, and `{var.<name>}` brace tokens (gap G1, shipped P1). |

## `[Run]` postinstall → `installer.run_after_install` / hooks

| Inno directive | Sigil equivalent | Notes |
|---|---|---|
| `Filename: "{app}\MyApp.exe"; Flags: postinstall nowait skipifsilent` | `installer.run_after_install` (`path`, `args`) | Drives the Done screen's checked-by-default "Launch \<App\>" checkbox; a headless `/silent /launch` run launches it too. Always launched **unelevated**, de-elevated from an admin install token — Inno's `postinstall` entry runs at the installer's own privilege level, so this is a deliberate hardening, not a gap. |
| `Filename: "..."; Flags: runhidden` (silent post-install command) | `installer.hooks.post_install` step list | Ordinary steps (typically `run_program`) run outside the rollback journal, governed only by their own `on_failure` (default `continue` for `post_install` — the install is already committed). |
| `Filename: "..."; Flags: skipifdoesntexist runasoriginaluser` (pre-install command) | `installer.hooks.pre_install` | Default `on_failure: fail` — aborts before the journal opens, matching a failed pre-install `[Run]` entry blocking setup. |
| `[UninstallRun]` | `installer.hooks.pre_uninstall` / `post_uninstall` | Same warning applies: hooks have no rollback obligations. |

## `[Tasks]` → `installer.options.components`

| Inno directive | Sigil equivalent | Notes |
|---|---|---|
| `Name: "desktopicon"; Description: "Create a &desktop icon"` | `installer.options.components[]` entry (`name`, `label`, `default`, `locked`) | App-defined custom components (gap G11, shipped P10) — Sigil's direct analog of `[Tasks]`. Flat list, no hierarchy, rendered as checkboxes on the Options screen in declared order. |
| `Tasks: desktopicon` gate on a `[Files]`/`[Icons]`/`[Run]` entry | `when: option.<name>` on the corresponding step | A task generates no step of its own — it exists only as `option.<name>` in the expression engine, gating whichever steps reference it. |
| `Flags: unchecked` | `default: false` | — |
| `Flags: exclusive` (radio-button task groups) | (no direct equivalent) | Model as a single `parameters.<name>.type: enum` screen field instead of mutually-exclusive tasks. |

## `[Languages]` → `installer.language` + localization

| Inno directive | Sigil equivalent | Notes |
|---|---|---|
| `[Languages]` section listing `.isl` files | `installer.language` (fixed) or auto-detect | Built-in wizard chrome (buttons, titles, CloseApps/DowngradeBlocked/Failed screens) comes from a compiled-in, source-generated string catalog — no `.resx` satellite assemblies (gap G10, shipped P9). |
| Custom `[CustomMessages]` strings | `LocalizedText` fields (`installer.screens[].title`/`subtitle`, `parameters.<name>.description`, `installer.license`) | Accepts a plain string (English) or a `{ en: ..., de: ... }` map; a map without an `en` entry is a pack-time error (`SIG0290`). |
| `/LANG=` command-line override | `/lang` | Same language-preference chain as `installer.language`: manifest fixed value → `/lang` → OS list → `en`. |

## `AppMutex` + `CloseApplicationsFilter` → `installer.app_mutex` + Restart Manager

| Inno directive | Sigil equivalent | Notes |
|---|---|---|
| `AppMutex` | `installer.app_mutex` (array of names) | Direct analog (gap G7, shipped P6) — setup opens each named mutex before touching the install directory; a mutex that opens means the app is running. Use the exact name the app passes to `CreateMutex`, including any `Global\` prefix. |
| `CloseApplications` / `CloseApplicationsFilter` (Restart Manager integration) | (automatic, complements `app_mutex`) | Sigil always sweeps the install directory with Windows Restart Manager, catching processes holding files open even when no mutex is declared — a superset of Inno's filter-based approach. |
| Wizard "these applications are using files that need to be updated" screen | Close-applications wizard screen | Offers Retry / close-for-me. |
| `/CLOSEAPPLICATIONS` silent switch | `/closeapps` | Without it, a blocked silent install exits with a dedicated nonzero code rather than failing on a locked file. |

## `PrivilegesRequired` → `installer.scope`

Already covered under `[Setup]` above — repeated here because it is one of
the most consequential single-line ports: `admin` → `installer.scope:
machine`, `lowest` → `installer.scope: user`, and Inno 6's
`PrivilegesRequiredOverridesAllowed` → `installer.scope: auto` (dual-scope
resolution, self-elevating as needed).

## `[Code]` Pascal scripting → declarative equivalents (non-goal)

Inno's `[Code]` section is a full embedded Pascal Script runtime:
`InitializeSetup`, `CurStepChanged`, custom wizard pages, arbitrary
conditionals, arbitrary file/registry manipulation. **Sigil deliberately has
no embedded scripting language** — this is the same design line WiX and NSIS
custom actions/plugins are declined at (see `from-wix.md` / `from-nsis.md`).
The declarative surface that replaces the ~90% of `[Code]` that actual Inno
scripts use:

| `[Code]` pattern | Sigil equivalent |
|---|---|
| `InitializeSetup` / `CurStepChanged(ssInstall)` / `CurUninstallStepChanged` | `installer.hooks` (`pre_install`/`post_install`/`pre_uninstall`/`post_uninstall`) |
| `RegQueryStringValue`, `GetEnv`, reading a file's version, reading the previously-installed version | `installer.vars` + the expression functions `registry_read`, `env`, `file_version`, `installed_version` |
| `if` / `case` branches gating which files or registry entries get written | `when:` expressions on the individual step (closed grammar: `param.*`, `option.*`, `var.*`, `app.*`, `system.*`, `scope*`, plus `defined`/`empty`/`version_gte`/`os_version`/`arch`/`locale`/`file_exists`/`registry_exists`) |
| Custom wizard pages (`CreateInputQueryPage`, etc.) | `installer.screens` (declared screens over `parameters`, with per-parameter widget inference) |
| Arbitrary `Exec`/`ShellExec` calls | `run_program` step, or an `installer.hooks` entry |
| Detecting/installing a prerequisite by hand (`InitializeSetup` + `Exec`) | `installer.prerequisites[]` (detect → acquire → run → re-detect, gap G6) |
| Anything else procedural | Not supported by design. A declarative, AOT-safe, auditable manifest is Sigil's differentiator; arbitrary code reintroduces the class of problems Sigil exists to remove. Use a signed `run_program` invoking a small helper executable for logic that genuinely cannot be expressed declaratively. |

## Scheduled tasks, COM registration, firewall rules (shipped P11)

Inno has no first-class support for any of these three — script authors
universally reach for `[Run]` entries invoking the underlying command-line
tool. Sigil ships typed, journaled steps instead (gaps G12–G14):

| Inno idiom | Sigil equivalent | Notes |
|---|---|---|
| `Filename: "schtasks.exe"; Parameters: "/Create /TN ... /TR ... /SC ..."` | `scheduled_task_create` | Fields `name`, `program`, `arguments`, `trigger` (`logon`/`daily`/`onstart`), `run_level`. Always runs the task as `SYSTEM`; rollback runs `schtasks /Delete /TN <name> /F`. |
| `Filename: "regsvr32.exe"; Parameters: "/s ""{app}\MyLib.dll"""` | `com_register` | Invokes the DLL's exported `DllRegisterServer`/`DllUnregisterServer` directly via a statically-bound, AOT-safe unmanaged function pointer — no shelling out to `regsvr32`. |
| `Filename: "netsh.exe"; Parameters: "advfirewall firewall add rule ..."` | `firewall_rule` | Fields `name`, `direction`, `action`, `program`/`port`/`protocol`. Deletes any same-named rule before adding, so reinstalls stay idempotent. |

**All three require `installer.scope: machine`** — they touch machine-global
state, so packing a manifest that uses any of them under `user` or `auto`
scope fails at pack time with `SIG0310`.

## `DownloadTemporaryFile` → `http_download`

| Inno directive | Sigil equivalent | Notes |
|---|---|---|
| `DownloadTemporaryFile` / `CreateDownloadPage` (built in since Inno 6.1) | `http_download` step (`url`, `dest`, `sha256`, `timeout_seconds`, `retries`) | HTTPS only; `sha256` is **required** — the packer refuses to pack a download step without one. Rollback deletes the downloaded file; transient failures (network/timeout/5xx) retry with backoff, a checksum mismatch fails immediately (gap G5, shipped P4). |

## Update checks → `/Update`

Inno itself ships no update engine — script authors hand-roll version
checks in `[Code]` against a self-hosted URL, or reach for a third-party
tool. Sigil ships one built in (gap G15, shipped P12):

| Inno idiom | Sigil equivalent | Notes |
|---|---|---|
| `[Code]`-based "check a URL for a newer version" | `updates:` manifest block + `Setup.exe /Update` | Reads an ECDSA P-256-signed channel manifest (`manifestUrl`, detached `.sig`), compares versions against the installed ARP entry, and — if newer — downloads the full package (sha256-verified) and hands off to it. Dedicated exit codes (`0` up to date, `6` not configured, `7` check/apply failed, `8` signature rejected, `9` below `minFromVersion`). See [Updates](../guides/updates.md). |
| Hand-rolled "download and self-replace" web installer | `sigil pack --payload web --package-url <https-url>` | Emits a tiny stub `Setup.exe` (`...-WebSetup.exe`) whose only install action is an `http_download` of the full package, then a handoff to it (gap G16, shipped P12). |
| Delta/binary-patch updates | **Not implemented** | `updates.deltaTargets` is parsed and schema-validated but has zero runtime effect today — delta updates are explicitly deferred, not silently unsupported. See [ADR-010](../architecture/adr-010-delta-update-deferral.md). Every `/Update` today fetches the full package. |

## Concept differences

- **Imperative vs. declarative:** Inno's `[Code]` section executes Pascal
  top-to-bottom with mutable global state and event callbacks. Sigil's
  `install_steps:` list is a pure description of intent; the only "state"
  that crosses steps is the explicit `installer.vars` mechanism.
- **Sections as organization, not scope:** Inno's `[Section]` headers group
  unrelated concerns (files vs. registry vs. icons) that must all be kept in
  sync by hand when adding a feature. Sigil's `install_steps:` is one
  ordered list; a new feature is a handful of new step records plus one
  `installer.options.components` entry gating them.
- **No plugin DLL ecosystem:** Inno's third-party `.dll` plugins (used for
  things like advanced firewall control before Sigil-equivalent built-ins
  existed) have no Sigil analog — the step catalog is closed and extended
  only in-repo (every new step amends the closed-catalog policy in
  [ADR-008](../architecture/adr-008-expression-policy.md)).
- **Rollback is transactional, not best-effort:** Inno's uninstall log
  (`Uninstall.exe`) replays recorded actions but has no notion of a failed
  *install* rolling itself back mid-run. Sigil's `RollbackJournal` reverses
  every completed step the moment any step fails, before the wizard even
  reports failure — closer to an MSI transaction than Inno's uninstall-log
  model.
- **Wizard pages are inferred, not hand-assembled:** Inno wizards are built
  page-by-page in `[Code]` (`CreateInputQueryPage`, etc.) or from `[Types]`/
  `[Components]`. Sigil infers screens from `parameters:` (grouped by
  `screen:`) and renders the widget from the parameter's `type`/`source` —
  there is no page-construction API to call.

## Uninstaller mapping

| Inno construct | Sigil equivalent | Notes |
|---|---|---|
| Auto-generated `unins000.exe` | `uninstall.exe`, generated whenever the manifest has an `uninstall:` block | Packaged as a resource in `Setup.exe` and dropped to `install_dir\uninstall.exe` on install success. |
| ARP `UninstallString` / `QuietUninstallString` | (automatic) | The wrapper writes both, pointing at the deployed `uninstall.exe`, mirroring Inno's own ARP registration. |
| `[UninstallDelete]` | `uninstall:` step list | The install journal already replays the reverse of every install step automatically; declare `uninstall:` only for extra tear-down the journal can't infer (stopping a service, clearing an AppData cache). |
| `UninstallDisplayIcon` | `installer.icon` | Same icon stamps `Setup.exe`, the wizard, and `uninstall.exe`. |

## Examples

See the worked manifests under [`examples/`](../../examples/) in the repo
root — each is validated in CI against the current schema, so they stay
accurate to shipped behavior by construction.
