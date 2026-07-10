# Conditional installs

Every step accepts a `when:` clause: a boolean expression evaluated against the live install context. When `when:` returns false the step is skipped entirely (no journal entry, no logs beyond a one-line "skipped" trace).

## Minimal example

```yaml
install_steps:
  - id: copy-pro-files
    type: file_copy
    from: payload/pro/**
    to: "${parameters.install_dir}\\pro"
    when: "parameters.edition == 'professional'"
```

Note: inside `when:` you write `parameters.edition`, not `${parameters.edition}`. The `${...}` template syntax is for string substitution in step arguments; `when:` is a real expression evaluated by the wrapper's expression engine.

## Operators

|Operator|Meaning|
|---|---|
|`==`, `!=`|Equality.|
|`<`, `<=`, `>`, `>=`|Ordering (numeric and lexical).|
|`&&`, `\|\|`|Boolean and/or.|
|`!`|Negation.|
|`in`, `not_in`|Membership in a list literal: `parameters.edition in ['pro', 'enterprise']`.|

Literals: integers, single- or double-quoted strings (no escape sequences), `true`, `false`, and list literals using `[...]`.

## Identifier namespaces

|Prefix|Source|
|---|---|
|`parameters.<name>`|Install-time parameter values (CLI override - default).|
|`app.<field>`|Manifest `app.*` fields (`app.version`, `app.name`, ...).|
|`system.os`|OS version string at install time.|
|`system.arch`|Process architecture (`x64`, `arm64`, ...).|
|`env.PATH`|The live `PATH` env var on the target machine.|

Identifiers are dotted paths the lexer treats as single tokens; the evaluator looks them up in the context dictionary by full key. Identifier resolution is install-time, so `env.PATH` reads the live environment on the user's machine - not the pack-time host's.

## Built-in functions

|Function|Returns|
|---|---|
|`defined(x)`|`true` if the identifier was supplied (non-null).|
|`empty(x)`|`true` if x is null, an empty string, or an empty collection.|
|`version_gte(a, b)`|`true` when version `a` >= version `b` (semantic compare with ordinal fallback).|
|`os_version()`|OS version string.|
|`arch()`|Process architecture.|
|`locale()`|Current UI culture name.|
|`file_exists(path)`|`true` if the file exists at install time.|
|`registry_exists(hive, key, name)`|`true` if the value exists; pass `null` for `name` to check key existence.|

The function table is closed - anything outside this list is a hard parse error. Functions can't shell out or do reflection by design.

## `on_failure` policy

Each step has an `on_failure:` field (default `fail`):

|Value|Behaviour|
|---|---|
|`rollback`|Undo the journal up to and including this step, then abort.|
|`continue`|Log a warning and proceed with the next step. The journal entry from any partial mutation stays in place.|
|`fail`|Abort immediately. No rollback of preceding steps.|

`continue` does not protect preceding steps from being rolled back if a LATER step then aborts with `rollback`. Best-effort cleanup (e.g. tearing down a third-party service that may not be installed) is the canonical use of `continue`.

## Worked example: a multi-edition installer

```yaml
parameters:
  edition:
    type: enum
    values: [community, professional, enterprise]
    default: community
    install_time: true
    description: Which feature set to install.
  install_drivers:
    type: bool
    default: false
    install_time: true
    description: Install the hardware driver (requires reboot).
  install_dir:
    type: path
    default: "%ProgramFiles%\\MyApp"
    install_time: true

install_steps:
  - id: copy-base
    type: file_copy
    from: payload/base/**
    to: ${parameters.install_dir}

  - id: copy-pro
    type: file_copy
    from: payload/pro/**
    to: "${parameters.install_dir}\\pro"
    when: "parameters.edition in ['professional', 'enterprise']"

  - id: copy-enterprise
    type: file_copy
    from: payload/enterprise/**
    to: "${parameters.install_dir}\\enterprise"
    when: "parameters.edition == 'enterprise'"

  - id: install-driver
    type: run_program
    program: "${parameters.install_dir}\\drivers\\setup-driver.exe"
    args: ["/quiet"]
    wait: true
    expected_exit_codes: [0, 3010]   # 3010 = success, reboot required
    when: "parameters.install_drivers && !file_exists('C:\\Windows\\System32\\drivers\\myapp.sys')"
```

Reading the last `when:`: install the driver only if the user opted in AND the driver isn't already present.

## See also

- [Install steps](install-steps.md)
- [Parameters](parameters.md)
- [Manifest reference - InstallStep](../manifest-reference.md#definition-installstep)
