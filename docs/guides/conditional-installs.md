# Conditional installs

Every step accepts a `when:` clause: a boolean expression evaluated against the live install context. When `when:` returns false the step is skipped entirely (no journal entry, no logs beyond a one-line "skipped" trace).

> **A custom component's `when:` is not the same thing.** `installer.options.components[].when` is evaluated **before** `installer.vars` are populated, so it may reference `param.*`, `scope`, `system.*` and earlier `option.*` — but **not** `var.*`. It also **fails open**: a malformed or erroring expression resolves the component to *applicable*, matching the wizard's fail-open row-visibility policy. A step's `when:` does not fail open. Same keyword, two different contracts.

## Minimal example

```yaml
install_steps:
  - id: copy-pro-files
    type: file_copy
    from: payload://pro/**
    to: "{install_dir}\\pro"
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

Literals: **non-negative** integers, single- or double-quoted strings (no escape sequences), `true`, `false`, and list literals using `[...]`. There is no unary minus in the lexer, so `-1` is a parse error — compare against `0` or use a `version_gte` style predicate instead.

## Identifier namespaces

|Prefix|Source|
|---|---|
|`parameters.<name>`|Install-time parameter values (CLI override - default).|
|`param.<name>`|An exact alias for `parameters.<name>`, and the spelling the schema and ADR-008 use.|
|`var.<name>`|Each declared `installer.vars` entry, evaluated once after every base identifier is seeded.|
|`option.<name>`|Each `installer.options` component's resolved on/off state — the built-ins and the app-defined `components[]`.|
|`app.<field>`|Manifest `app.*` fields. Exactly four keys are seeded: `app.id`, `app.name`, `app.version`, `app.publisher`.|
|`system.os`|OS version string at install time.|
|`system.arch`|Process architecture (`x64`, `arm64`, ...).|
|`system.language`|The **resolved chrome language** tag for this session (e.g. `uk`) — the one way a manifest can branch on the installer's language. See [Localization](localization.md).|
|`scope`|The resolved install scope, `user` or `machine`.|
|`scope.root` / `scope_root`|The install root for that scope.|
|`install_dir`|The resolved install directory, as a dotted identifier (the `{install_dir}` brace token is the same value in a path).|
|`env.PATH`|The live `PATH` env var on the target machine.|

Identifiers are dotted paths the lexer treats as single tokens; the evaluator looks them up in the context dictionary by full key. Identifier resolution is install-time, so `env.PATH` reads the live environment on the user's machine - not the pack-time host's.

**`env.PATH` is the only `env.*` identifier that exists.** `env.HOME`, `env.USERPROFILE` and everything else throw "unknown identifier". To read any other environment variable, use the `env("NAME")` **function** below.

## Built-in functions

|Function|Returns|
|---|---|
|`defined(x)`|`true` if the identifier was supplied (non-null).|
|`empty(x)`|`true` if x is null, an empty string, or an empty collection.|
|`version_gte(a, b)`|`true` when version `a` >= version `b` (semantic compare with ordinal fallback).|
|`os_version()`|OS version string.|
|`arch()`|Process architecture.|
|`locale()`|The **OS UI language** tag, read through Win32 — the user's top preference, not `CurrentUICulture` (which is always empty under `InvariantGlobalization`). `""` when unavailable.|
|`file_exists(path)`|`true` if the file exists at install time.|
|`registry_exists(hive, key, name)`|`true` if the value exists; pass `null` for `name` to check key existence.|
|`registry_read(hive, key, name)`|The registry value as a string.|
|`env(name)`|The named environment variable. This is how you read anything other than `PATH`.|
|`file_version(path)`|The file version of the binary at `path`.|
|`installed_version(app_id)`|The version of an already-installed Sigil app, by app id.|

The last four are the data-retrieval functions — the declarative equivalents of NSIS `ReadRegStr`, Inno `RegQueryStringValue` and WiX `RegistrySearch`. **All four are total: they return `""` when the value is absent or unreadable, rather than throwing.** So test with `empty(...)` rather than expecting a failure.

The function table is closed at these twelve - anything outside this list is a hard parse error. Functions can't shell out or do reflection by design.

## `on_failure` policy

Each step has an `on_failure:` field (default `fail`):

|Value|Behaviour|
|---|---|
|`rollback`|Abort, and undo the rollback journal.|
|`continue`|Log a warning and proceed with the next step. The journal entry from any partial mutation stays in place.|
|`fail`|Abort, and undo the rollback journal.|

> **Known issue (R78): `rollback` and `fail` are currently identical.** Both take the same path in the engine and both replay the **entire** journal in reverse, across all phases — not "up to and including this step", and not "abort without rollback". Only `continue` is a distinct policy today. The intent is that the two differ; the shipped behaviour is that they do not.

`continue` does not protect preceding steps from being rolled back if a LATER step then aborts. Best-effort cleanup (e.g. tearing down a third-party service that may not be installed) is the canonical use of `continue`.

The default is phase-dependent: `fail` for `install_steps:`, `pre_install:`, `post_install:` and `uninstall:` (and for `installer.hooks.pre_*`), but `continue` for `installer.hooks.post_*`.

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

install_steps:
  - id: copy-base
    type: file_copy
    from: payload://base/**
    to: "{install_dir}"

  - id: copy-pro
    type: file_copy
    from: payload://pro/**
    to: "{install_dir}\\pro"
    when: "parameters.edition in ['professional', 'enterprise']"

  - id: copy-enterprise
    type: file_copy
    from: payload://enterprise/**
    to: "{install_dir}\\enterprise"
    when: "parameters.edition == 'enterprise'"

  - id: install-driver
    type: run_program
    program: "{install_dir}\\drivers\\setup-driver.exe"
    args: ["/quiet"]
    wait: true
    expected_exit_codes: [0, 3010]   # 3010 = success, reboot required
    when: "parameters.install_drivers && !file_exists('C:\\Windows\\System32\\drivers\\myapp.sys')"
```

Reading the last `when:`: install the driver only if the user opted in AND the driver isn't already present.

Note what this example deliberately does **not** do: it declares no `install_dir` parameter and writes every destination as the `{install_dir}` token. A parameter named `install_dir` is a second, unrelated value that does not follow the wizard's Destination screen, `/D=`, or an upgrade — and a `%ProgramFiles%`-style default would not expand anyway, because [there is no `%VAR%` expansion in a step path](install-steps.md#every-step-destination-is-contained-to-install_dir): `%ProgramFiles%` would be taken as a literal first path component and the write refused for landing outside `install_dir`.

## See also

- [Install steps](install-steps.md)
- [Parameters](parameters.md)
- [Manifest reference - InstallStep](../manifest-reference.md#definition-installstep)
