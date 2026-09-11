# Installer wizard

When you produce an `exe`-format package (see [Packaging formats](packaging-formats.md)), Sigil ships your app inside a branded 800x500 Windows wizard. The wizard host is a stock Sigil binary; everything user-visible is driven from the `installer:` and `parameters:` blocks of `sigil.yaml`.

## Screen flow

The screen list is built at runtime from your manifest. Welcome, Choose Install Location, Installing and Finish are always rendered; License, the built-in Options page, and your declared custom screens are the conditional ones, in this order:

1. **Welcome** — branded splash with app name + version.
2. **Choose Install Location** — **always rendered**, immediately after Welcome, whether or not your manifest declares any `parameters:` at all. It is not tied to declaring a parameter named `install_dir` — see the warning under [Parameters](parameters.md). Includes a TextBox, Browse..., and a live disk-space readout via `DriveInfo`. When the manifest sets `installer.scope: auto`, this screen also carries the **user / machine scope toggle**, and flipping it recomputes the default path for the chosen scope.
3. **License** — the real license text from `installer.license`, shown only when the manifest declares one. Next is disabled until the user accepts.
4. **Options** — the built-in components page, shown only when at least one `installer.options` component is enabled. Rendered as checkboxes: the built-ins first (desktop shortcut, Start Menu, add to PATH, file associations), then any app-defined `components[]` in declared order.
5. **N x custom screens** — one page per `installer.screens[]` entry, in declared order. See [Custom screens](#custom-screens-installerscreens).
6. **Installing** — progress feed driven by the step engine.
7. **Finish** — completion summary.

Three further screens exist but are not part of that linear flow — the wizard diverts to them:

- **CloseApps** — reached when running applications hold the install directory open.
- **DowngradeBlocked** — jumped to at startup when the installed version is newer than this package; the run ends with exit code `3`.
- **Failed** — the terminal screen for a failed install.

`/S` on the command line suppresses every interactive screen and runs the manifest end-to-end using parameter defaults plus any `/PName=Value` overrides. See [Parameters](parameters.md).

### The other two wizards

The same host binary also renders two smaller flows:

- **Uninstall.** Double-clicking `<install_dir>\uninstall.exe` with no flags: **Confirm → Progress → Done | Failed**. (Add/Remove Programs passes `/S`, which skips all of it.)
- **Headed `/Update`.** A single status window rather than a paged flow: **Checking → Downloading → LaunchingChild → UpToDate | Done | Failed**. Headless `/Update` shows none of it.

## Brand slots

The wizard's chrome is themed from `installer.brand`:

```yaml
installer:
  icon: ./brand/setup.ico
  brand:
    logo:          ./brand/logo.svg
    hero:          ./brand/hero.png
    primaryColor:  "#1F2937"
    accentColor:   "#3B82F6"
```

Slots — this is the **complete** list. `installer.brand` is `additionalProperties: false`, so any other key fails `sigil validate` with SIG0010:

|Field|What it does|
|---|---|
|`logo`|Header logo (SVG or PNG).|
|`hero`|Welcome-screen hero artwork (SVG or PNG).|
|`primaryColor`|Primary button + accent fill.|
|`accentColor`|Secondary accent (progress + links).|

> **Use the camelCase spellings.** The schema also permits `primary_color` and `accent_color`, but the manifest parser reads only `primaryColor` and `accentColor` — a manifest that sets the snake_case keys alone passes `sigil validate` and then packs with **no brand colours at all**, silently falling back to the defaults.

All colours are `#RRGGBB` hex — the schema enforces the pattern. The full light and dark palette is **derived from those two colours** at pack time by the brand generator; there is no gradient, sidebar or per-surface colour field to set. `BrandTokenEmitter` enforces WCAG-AA contrast against white text at pack time; failing combos surface as a pack-time diagnostic, not a runtime surprise.

## Installer icon

```yaml
installer:
  icon: ./brand/setup.ico
```

The `.ico` is stamped into the produced `setup.exe`'s Explorer icon, into the wizard process, and into the deployed `uninstall.exe`. Omit the field and Sigil uses a bundled default (the Saki / Alexandre Moore icon, credited in the OSS README).

## Per-parameter widget selection

The wizard picks a widget per parameter from its declared shape:

|Manifest declaration|Widget|`widget:` override|
|---|---|---|
|`type: enum` with 4 or fewer `values:`|radio group|`dropdown`|
|`type: enum` with 5 or more `values:`|dropdown|`radio`|
|`type: enum` plus `source: { ... }`|dropdown, HTTPS-fetched on page-attach — never a radio group|(none)|
|`type: bool`|checkbox|`switch`|
|`type: string`|text input|`textarea`|
|`type: int`|number input|`slider`|
|`type: path`|path input|(none)|
|`type: secret`|masked input|(none)|

The `widget:` column lists the only override each type honours; any other value falls through to the default. Set one with the `{ param, widget }` form of a screen field.

Dynamic dropdowns defer their fetch until every `${parameters.X}` referenced in `source.url` has a non-empty value, then cache the result for the page lifetime. Full mechanics in [Parameters](parameters.md).

## Custom screens (`installer.screens`)

Custom wizard pages come from `installer.screens[]` — an ordered list, one page per entry — and **nowhere else**:

```yaml
installer:
  screens:
    - id: server
      title: "Server Settings"
      subtitle: "Where this workstation reports to."
      fields:
        - server_ip
        - param: log_level
          widget: dropdown
    - id: privacy
      title: "Privacy"
      when: "parameters.edition != 'community'"
      fields:
        - enable_telemetry
```

|Field|Required|What it does|
|---|---|---|
|`id`|yes|Stable identifier for the page.|
|`title`|yes|Page heading and rail label. A `LocalizedText` — a plain string, or a per-language map.|
|`subtitle`|-|Secondary line under the heading. Also `LocalizedText`.|
|`when`|-|Expression gating the page. Evaluated at **navigation** time, not when the flow is built, so a page can become visible after an earlier field is set — and is skipped when it is false.|
|`fields`|yes|The parameters to render, in order. Each entry is either a bare parameter name or a `{ param, widget }` object that overrides the widget choice.|

Rules:

- A `fields` entry naming a parameter that does not exist is dropped; the page renders without it.
- **A parameter no screen names is never rendered**, even with `install_time: true`. It resolves to its default or a `/PName=Value` override.
- **There is no reserved `install_dir` parameter.** The Choose Install Location screen is a fixed part of the flow, wired to the engine's own `{install_dir}` resolution — it does not read a manifest parameter of that name. Do not declare one; see the warning in [Parameters](parameters.md).

> **Known issue (R79): the parameter-level `screen:` field does not build pages.** The schema still accepts `screen:` on a parameter and the manifest reference still describes it as grouping parameters onto wizard pages. It does not: the value is written into a sidecar nothing reads, and no page is produced from it. Use `installer.screens[]`.

## Silent install

```bash
setup.exe /S /D="C:\Apps\MyApp" /Pedition=professional
```

`/S` skips every screen and runs the step list non-interactively. `/D=path` overrides the install directory (see [the setup.exe reference](../setup-exe-reference.md)); any `/PName=Value` tokens override the matching `install_time: true` parameter default — the `P` prefix is required, a bare `/Name=Value` is rejected. `/Poption.<Name>=<Value>` overrides an `installer.options.components[]` entry. Undeclared parameter and component names are rejected at parse time. Bool values write back as the literal strings `True` / `False`.

## See also

- [Manifest reference - installer](../manifest-reference.md#installer)
- [Manifest reference - installer.brand](../manifest-reference.md#installerbrand)
- [Parameters](parameters.md)
- [Packaging formats](packaging-formats.md)
