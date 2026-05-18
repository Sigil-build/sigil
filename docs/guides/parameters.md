# Parameters

The `parameters:` map is a typed declaration of inputs your manifest needs at pack time, install time, or both. Each entry has a name, a type, and (almost always) a default.

## Minimal example

```yaml
parameters:
  install_dir:
    type: path
    default: "%ProgramFiles%\\MyApp"
    install_time: true
  edition:
    type: enum
    values: [community, professional]
    default: community
    install_time: true
```

The wizard surfaces every `install_time: true` parameter; non-install-time parameters resolve at pack time only.

## Types

|Type|Use for|Widget when `install_time: true`|
|---|---|---|
|`string`|Free-form text|TextBox|
|`path`|Filesystem paths|TextBox (Browse... for `install_dir`)|
|`bool`|True/false flags|CheckBox|
|`int`|Whole numbers|TextBox|
|`enum`|Closed set of strings|ComboBox|
|`secret`|Passwords, tokens, keys|Masked TextBox; redacted from logs|

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

Step arguments support `${parameters.<name>}` and the `app.*` namespace (`${app.name}`, `${app.version}`, `${app.id}`, `${app.publisher}`, `${app.description}`, `${app.homepage}`). Resolution happens just before each step runs:

```yaml
install_steps:
  - id: copy-app
    type: file_copy
    from: payload/**
    to: ${parameters.install_dir}
  - id: stamp-registry
    type: registry_write
    hive: HKLM
    key: "Software\\${app.name}"
    name: InstalledVersion
    type_value: REG_SZ
    value: "${app.version}"
```

Unknown identifiers are a hard runtime error - typos surface as a `FormatException` from the step engine, never as an empty string.

## CLI overrides at install time

```bash
setup.exe /S /install_dir="C:\Apps\MyApp" /edition=professional
```

- One `/Name=Value` token per parameter. Last write wins.
- Names match the canonical schema spelling case-insensitively; values preserve case.
- Undeclared names are rejected (`UsageException`) - silent typos can't reach the step engine.
- The wizard's silent-install child process uses the same syntax to forward the user's edits.

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

## Screen grouping

The optional `screen:` field on each parameter assigns it to a wizard page. Parameters with the same `screen:` value share a page in declaration order; parameters without one collapse into a trailing "Install Options" page. See [Installer wizard - multi-screen parameter grouping](installer-wizard.md#multi-screen-parameter-grouping).

## See also

- [Manifest reference - parameters.<name>](../manifest-reference.md#parametersname)
- [Manifest reference - parameters.<name>.source](../manifest-reference.md#parametersnamesource)
- [Installer wizard](installer-wizard.md)
- [Conditional installs](conditional-installs.md)
