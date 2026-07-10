# Installer UI manifest model — colors, options, custom screens

Status: adopted (2026-07-09) — absorbed into
[`IMPLEMENTATION_SPEC.md`](IMPLEMENTATION_SPEC.md) T7 (branding), T8 (options),
T9 (screens), T13 (destination), T14 (license). The spec is authoritative where
they differ. Validated against the Claude Design prototype
(`prototype/sigil-installer-wizard-prototype.html`).

## 1. Brand colors — two inputs, derived palette

The wizard's entire palette (light *and* dark) derives from two colors. The
manifest exposes exactly those; everything else is computed at pack time.

```yaml
installer:
  brand:
    primary_color: "#312E81"   # rail + primary surfaces
    accent_color:  "#4F46E5"   # buttons, active states, progress
    logo: ./brand/logo.png     # optional; default logo if omitted
    hero: ./brand/hero.png     # optional; reserved for future hero layout
```

- `InstallerBrand` keeps `PrimaryColor` + `AccentColor`; **`GradientStart/Mid/
  End` are removed outright** (the flat rail replaces the gradient). The JSON
  schema (`additionalProperties: false`) never exposed them, so no manifest can
  set them today — no deprecation window needed.
- The `colors()` derivation from the prototype (both light + dark token maps)
  ports to C# in `BrandTokenEmitter`. The derived token set — plus base64
  logo/hero — is embedded in the **WrapperBlob** (`SIGIL_BLOB_V1`), not a
  `BrandTokens.g.json` sidecar (that delivery only exists in the MSIX bundling
  path). Avalonia can't `color-mix` at runtime, so it's computed once at pack
  time.
- `color-mix(in srgb, A p%, B)` = per-channel linear interpolation of the 0–255
  sRGB values: `out = A*(p/100) + B*(1 - p/100)`. Faithful, trivial port.
- WCAG-AA-against-white check stays and extends to derived rail-muted text;
  `--allow-low-contrast` still overrides.

## 2. Built-in Options screen — configurable components

The Options screen ships batteries-included with the common desktop components,
each **individually configurable and disable-able** by the publisher. Omitting
a component (or setting it `false`) removes it from the screen and skips its
generated steps.

```yaml
installer:
  options:
    desktop_shortcut: true                 # shorthand: shown, default-on, user-toggleable
    add_to_path: { default: true }         # object form: set default check state
    file_associations:
      enabled: true
      extensions: [".acme"]
      default: false
    start_menu: false                      # disabled entirely — not shown
```

Each component form:

- shorthand `true` / `false` — show with sane defaults, or disable.
- object: `enabled` (appears at all), `default` (initial checkbox state),
  `locked` (shown but not user-toggleable, always applied), plus
  component-specific keys (e.g. `extensions`).

Each enabled component **auto-generates its install step(s)**, gated on the
checkbox value via the existing `when` mechanism:

| Component          | Generated step(s)                          | Gate                         |
|--------------------|--------------------------------------------|------------------------------|
| `desktop_shortcut` | `shortcut_create` (Desktop)                | `option.desktop_shortcut`    |
| `start_menu`       | `shortcut_create` (Start menu)             | `option.start_menu`          |
| `add_to_path`      | `env_set` (PATH, append)                   | `option.add_to_path`         |
| `file_associations`| `registry_write` per extension             | `option.file_associations`   |

Component values are exposed as `option.*` for use in custom `when` expressions
elsewhere.

## 3. Custom screens — declared forms over parameters

Parameters are the single source of truth for type, default, and validation.
Custom screens group them into titled, ordered forms. No arbitrary markup — this
keeps the wizard AOT-safe, themeable, and validatable.

```yaml
parameters:
  server_address: { type: string, default: "https://acme.internal", install_time: true, description: "Server address" }
  license_key:    { type: secret, install_time: true, description: "License key" }
  autostart:      { type: bool,   default: true,  description: "Start when I sign in" }
  channel:        { type: enum,   values: [stable, beta, nightly], default: stable, description: "Update channel" }

installer:
  screens:
    - id: configure
      title: "Configure {app.name}"
      subtitle: "Connect to your server and set preferences."
      when: "option.add_to_path == true"    # optional visibility expression
      fields:
        - server_address                     # bare name — widget inferred from type
        - license_key
        - { param: channel, widget: radio }  # object form overrides the widget
        - autostart
```

**Widget inference** from `ParameterType`:

| Param type | Default widget            | Override options   |
|------------|---------------------------|--------------------|
| `bool`     | checkbox                  | switch             |
| `enum`     | radio (≤4) / dropdown     | radio, dropdown    |
| `secret`   | masked input + show/hide  | —                  |
| `path`     | input + browse button     | —                  |
| `string`   | text input                | textarea           |
| `int`      | number input              | slider             |

**Value flow.** Collected values feed the wrapper blob and become available as
`param.*` in step `when` expressions and string interpolation — so a field gates
a step with `when: "param.autostart == true"`, reusing the expression engine
that already exists. This unifies UI and install logic on one data model.

**Validation.** Field input is validated against the parameter's `pattern` /
`min` / `max` / `enum` before the wizard advances — the same rules the CLI
already enforces, now surfaced inline.

## 4. Wizard order

```
welcome → destination → license? → options? → [declared screens, in order] → installing → done
```

- `welcome`, `destination`, `installing`, `done` — always present (built-in).
- `destination` — install path + browse (spec T13); hosts the per-user /
  per-machine scope toggle when `installer.scope: auto` (spec T12). The
  install-location field lives here, **not** on the Options screen.
- `license` — appears only if `installer.license` is set.
- `options` — appears only if `installer.options` has ≥1 enabled component.
- declared `installer.screens` — appear in declaration order, each subject to
  its own `when`.

The rail's step indicator is generated from the resolved screen set, so hidden
screens never show. A screen whose `when` is false at runtime is skipped and
removed from the rail.

## 5. Sign trust line

The wizard's "Signed by {publisher}" line is gated on the `sign` block being
present **and** the artifact verifying at install time — not on `App.publisher`
alone. Unsigned build → no trust line (or a neutral "Publisher: {name}"), so the
UI can never imply a signature that isn't there.

## 6. Change checklist

Core / manifest:
- Simplify `InstallerBrand` to `PrimaryColor` + `AccentColor`; delete the
  gradient fields outright (never schema-exposed).
- Add `InstallerOptions` record (per-component config) + `InstallerScreen`
  record (`Id`, `Title`, `Subtitle`, `When`, `Fields`) and `ScreenField`
  (`Param`, `Widget?`).
- Extend `installer` schema in `schemas/sigil-schema.json` for `options` and
  `screens`; keep parameter block as-is.
- Parser: resolve field refs to declared parameters (error on unknown ref);
  infer widgets; validate `when` expressions.

Packaging / host:
- Port `colors()` (light + dark, `color-mix`) into `BrandTokenEmitter`; embed the
  full derived token set + logo/hero in the WrapperBlob.
- Generate Options components' install steps at pack time, gated on `option.*`.
- Thread declared screens + parameter defs into the wrapper blob.

Installer.Host (Avalonia):
- Data-drive the rail step indicator from the resolved screen set.
- Render declared screens from the field list via a widget factory keyed on
  param type.
- Bind collected values back into `param.*` / `option.*` for the engine.

## 7. Open follow-ups (not blocking)

- Field layout hints (two-column, section dividers) — defer; single column now.
- Screen-level `after:`/`before:` positioning vs. plain declaration order —
  declaration order for MVP.
- Localization of titles/labels — out of scope (wrapper is InvariantGlobalization).
