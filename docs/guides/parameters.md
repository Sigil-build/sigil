# Parameters

The `parameters:` map is a typed declaration of inputs your manifest needs at pack time, install time, or both. Each entry has a name, a type, and (almost always) a default.

## Minimal example

```yaml
parameters:
  log_dir:
    type: path
    default: "%ProgramData%\\MyApp\\Logs"
    install_time: true
  edition:
    type: enum
    values: [community, professional]
    default: community
    install_time: true
```

`install_time: true` marks a parameter as resolvable at install time; a parameter without it resolves at pack time only.

**The wizard renders only the install-time parameters that an `installer.screens[]` entry names in its `fields:` list.** An `install_time: true` parameter that no declared screen references is never shown to the user — it silently resolves to its default, or to a `/PName=Value` override on the command line. See [Installer wizard](installer-wizard.md#custom-screens-installerscreens) for how to declare a screen.

> **Do not declare a parameter named `install_dir`.** It reads as if it should
> mean "where the app is being installed", but a parameter is just another
> value — it does not follow the wizard's Destination screen, `/D=`, or an
> upgrade-in-place. The real, always-current install location is the
> `{install_dir}` brace token, resolved by the engine and substituted directly
> into step fields; it is not a `${parameters.*}`
> value at all. See [Install steps](install-steps.md#write-the-destination-as-install_dir)
> for the full rationale and worked examples.

## Types

|Type|Use for|Widget when `install_time: true`|
|---|---|---|
|`string`|Free-form text|TextBox|
|`path`|Filesystem paths|TextBox|
|`bool`|True/false flags|CheckBox|
|`int`|Whole numbers|TextBox|
|`enum`|Closed set of strings|ComboBox|
|`secret`|Passwords, tokens, keys|Masked TextBox; redacted from logs|

The **Browse...** button belongs to the wizard's always-present Choose Install Location screen, not to any parameter widget — there is no file-picker widget for a `path` parameter.

## Validation

|Field|Applies to|Effect|
|---|---|---|
|`pattern`|`string`, `path`, `secret`|ECMAScript regex the value must match.|
|`min` / `max`|`int`|Inclusive numeric bounds.|
|`values`|`enum`|Allowed values (required for static enums).|

## Defaults and pack-time env interpolation

Literal defaults are used verbatim. `${VAR}` inside a default resolves the named environment variable at pack time:

```yaml
parameters:
  api_endpoint:
    type: string
    default: "${MY_API_ENDPOINT}"
```

A missing env var is a hard pack-time error (SIG0020), not a silent empty string. Use `$${VAR}` to keep a literal `${VAR}` in the default.

## Install-time substitution inside steps

Step arguments support `${parameters.<name>}` (spelled `${param.<name>}` if you prefer — an exact alias) and the `app.*` namespace, plus the single-brace engine tokens `{install_dir}`, `{scope_root}`, `{app.name}`, `{app.id}`, `{var.<name>}`, `{temp_dir}` and `{staging_dir}`. Resolution happens just before each step runs:

**Exactly four `app.*` keys are seeded:** `${app.id}`, `${app.name}`, `${app.version}` and `${app.publisher}`. There is no `${app.description}` or `${app.homepage}` — those are unknown identifiers and throw `FormatException: unknown identifier 'app.description' in template` at install time, on the user's machine.

```yaml
install_steps:
  - id: copy-app
    type: file_copy
    from: payload://**
    to: "{install_dir}"
  - id: stamp-registry
    type: registry_write
    hive: HKLM
    key: "Software\\${app.name}"
    name: InstalledVersion
    type_value: REG_SZ
    value: "${app.version}"
```

`to: "{install_dir}"` is the resolved destination itself — not `${parameters.install_dir}`, which would only work if you had (incorrectly) declared a parameter by that name. See [Install steps](install-steps.md#write-the-destination-as-install_dir).

Unknown identifiers are a hard runtime error - typos surface as a `FormatException` from the step engine, never as an empty string.

## CLI overrides at install time

```bash
setup.exe /S /D="C:\Apps\MyApp" /Pedition=professional
```

- `/D=path` overrides the install directory — not a parameter override; see [the setup.exe reference](../setup-exe-reference.md#d).
- One `/PName=Value` token per declared parameter. Last write wins. The `P` prefix is mandatory: a bare `/Name=Value` is rejected with `UsageException: unrecognized flag`.
- Names match the canonical schema spelling case-insensitively; values preserve case.
- Undeclared names are rejected (`UsageException`) - silent typos can't reach the step engine.
- `/Poption.<Name>=<Value>` overrides an **`installer.options.components[]`** entry rather than a parameter. The `option.` prefix is a namespace, checked before the parameter table, so a custom component and a parameter may share a name — `/P<name>` still binds the parameter. An undeclared component name is rejected the same way an undeclared parameter is.
- The wizard's silent-install child process uses the same syntax to forward the user's edits.

## Secrets

A `secret` parameter is masked in the wizard and redacted (`***`) from the install log, the audit rendering of the command line, and the persisted uninstall state. Two further guarantees, and one limit worth reading before you design around it:

- **The elevated relaunch does not carry secrets.** A per-machine install started from a non-elevated process relaunches itself under UAC. It used to forward `/PName=Value` verbatim, which published the value to every process-creation auditor on the machine (Sysmon, EDR agents, WMI `Win32_Process`, the Task Manager command-line column). Secret values now cross that boundary in a DPAPI-protected, ACL-restricted, delete-after-read handoff file instead; the relaunch command line carries only a path to it.
- **Secretness is transitive.** An `installer.vars` entry whose expression references a `secret` parameter inherits that secretness: the derived `var.<name>` is redacted everywhere the parameter itself would be. You do not have to (and cannot) mark a var secret by hand.
- **`run_program` arguments are not a secret channel.** If your manifest interpolates `${parameters.<secret>}` into a `run_program` step's `args`, the resolved value necessarily lands on *that child process's* command line, where the same auditing sees it. Sigil cannot redact a command line it hands to another program. Pass secrets to a child through a file it reads and deletes, an environment variable, or stdin — not through `args`.

## Dynamic dropdowns

For a closed but server-provided set of options, declare a `source:` block:

```yaml
parameters:
  domain_name:
    type: string
    install_time: true
    default: "embed-infinity.com"
    screen: "Server Settings"
  application_id:
    type: enum
    default: ""
    install_time: true
    screen: "Kiosk Settings"
    source:
      url: "https://sales.${parameters.domain_name}/api/configuration/Kiosk"
      items_path:     data
      value_property: applicationId
      label_property: applicationName
```

Behaviour:

- The wizard fetches `url` over HTTPS when the page is attached.
- `items_path` is the JSON path (dotted) to the array of items.
- `value_property` / `label_property` pick which JSON field becomes the bound value vs the displayed label.
- The URL supports `${parameters.X}` template substitution. The fetch is deferred until every referenced parameter has a non-empty value (the previous page typically writes them).
- Responses are cached for the page lifetime.

## Putting a parameter on a wizard page

Declare an `installer.screens[]` entry and name the parameter in its `fields:` list. See [Installer wizard](installer-wizard.md#custom-screens-installerscreens).

> **Known issue (R79): the parameter-level `screen:` field is dead.** The schema still accepts it and the manifest reference still describes it as grouping parameters onto wizard pages — but no wizard page is produced from it. Its value is written into a sidecar the installer host never reads. The intent is that `screen:` groups parameters onto pages; the shipped behaviour is that only `installer.screens[]` builds pages. Do not rely on `screen:` — a parameter named by no screen is invisible in the wizard.

The two `screen:` values in the dynamic-dropdown example above are illustrative of the manifest field only; they do not produce the two pages their names suggest.

## See also

- [Manifest reference - `parameters.<name>`](../manifest-reference.md#parametersname)
- [Manifest reference - `parameters.<name>.source`](../manifest-reference.md#parametersnamesource)
- [Installer wizard](installer-wizard.md)
- [Conditional installs](conditional-installs.md)
