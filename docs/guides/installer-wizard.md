# Installer wizard

When you produce an `exe`-format package (see [Packaging formats](packaging-formats.md)), Sigil ships your app inside a branded 800x500 Windows wizard. The wizard host is a stock Sigil binary; everything user-visible is driven from the `installer:` and `parameters:` blocks of `sigil.yaml`.

## Screen flow

The screen list is built at runtime from your manifest. Welcome, License, Installing, and Finish are always rendered. The middle pages are dynamic:

1. **Welcome** — branded splash with app name + version.
2. **License** — license text (placeholder today; a `installer.license_path` field lands post-MVP).
3. **Choose Install Location** — auto-inserted when the manifest declares an `install_dir` parameter. Includes a TextBox, Browse..., and a live disk-space readout via `DriveInfo`.
4. **N x parameter pages** — one page per unique `screen:` value declared on install-time parameters, in first-appearance order. Parameters without a `screen:` value collapse into a trailing synthetic "Install Options" page.
5. **Installing** — progress feed driven by the step engine.
6. **Finish** — completion summary.

`/S` on the command line suppresses every interactive screen and runs the manifest end-to-end using parameter defaults plus any `/Name=Value` overrides. See [Parameters](parameters.md).

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
    gradientStart: "#280B74"
    gradientMid:   "#6047A7"
    gradientEnd:   "#19D3D8"
```

Slots:

|Field|What it does|
|---|---|
|`logo`|Header logo (SVG or PNG).|
|`hero`|Welcome-screen hero artwork.|
|`primaryColor`|Primary button + accent fill.|
|`accentColor`|Secondary accent (progress + links).|
|`gradientStart` / `gradientMid` / `gradientEnd`|Three-stop sidebar gradient.|

All colours are `#RRGGBB` hex. `BrandTokenEmitter` enforces WCAG-AA contrast against white text at pack time; failing combos surface as a pack-time diagnostic, not a runtime surprise.

## Installer icon

```yaml
installer:
  icon: ./brand/setup.ico
```

The `.ico` is stamped into the produced `setup.exe`'s Explorer icon, into the wizard process, and into the deployed `uninstaller.exe`. Omit the field and Sigil uses a bundled default (the Saki / Alexandre Moore icon, credited in the OSS README).

## Per-parameter widget selection

The wizard picks a widget per parameter from its declared shape:

|Manifest declaration|Widget|
|---|---|
|`type: enum` with `values: [...]`|static ComboBox|
|any type plus `source: { ... }`|dynamic ComboBox (HTTPS-fetched on page-attach)|
|`type: bool`|CheckBox|
|`string` / `path` / `int` / `secret`|TextBox (secrets masked)|

Dynamic ComboBoxes defer their fetch until every `${parameters.X}` referenced in `source.url` has a non-empty value, then cache the result for the page lifetime. Full mechanics in [Parameters](parameters.md).

## Multi-screen parameter grouping

```yaml
parameters:
  server_ip:
    type: string
    install_time: true
    screen: "Server Settings"
  domain_name:
    type: string
    install_time: true
    screen: "Server Settings"
  enable_telemetry:
    type: bool
    install_time: true
    # No `screen:` -> lands on the trailing "Install Options" page.
```

Rules:

- Parameters sharing a `screen:` value render on the same wizard page in declaration order.
- Unlabelled parameters fall through to a synthetic `Install Options` page at the end.
- The reserved `install_dir` parameter is always pulled out onto the dedicated Choose Install Location page; it is excluded from every group even if you set `screen:` on it.
- If the manifest declares neither `install_dir` nor any install-time parameter, the wizard still renders an empty Install Options page so the flow has a slot between License and Installing.

## Silent install

```bash
setup.exe /S /install_dir="C:\Apps\MyApp" /edition=professional
```

`/S` skips every screen and runs the step list non-interactively. Any `/Name=Value` tokens override the matching `install_time: true` parameter default. Undeclared parameters are rejected at parse time. Bool values write back as the literal strings `True` / `False`.

## See also

- [Manifest reference - installer](../manifest-reference.md#installer)
- [Manifest reference - installer.brand](../manifest-reference.md#installerbrand)
- [Parameters](parameters.md)
- [Packaging formats](packaging-formats.md)
