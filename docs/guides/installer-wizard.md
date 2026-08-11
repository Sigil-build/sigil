# Installer wizard

When you produce an `exe`-format package (see [Packaging formats](packaging-formats.md)), Sigil ships your app inside a branded 800x500 Windows wizard. The wizard host is a stock Sigil binary; everything user-visible is driven from the `installer:` and `parameters:` blocks of `sigil.yaml`.

## Screen flow

The screen list is built at runtime from your manifest. Welcome, Choose Install Location, Installing, and Finish are always rendered; License and the built-in Options page are the only conditional ones. The middle pages are dynamic:

1. **Welcome** — branded splash with app name + version.
2. **Choose Install Location** — **always rendered**, immediately after Welcome, whether or not your manifest declares any `parameters:` at all. `InstallerViewModel.RebuildFlow` adds this node (`InstallerStep.InstallOptions`) unconditionally; `License` and the built-in `Options` page, added right after it, are each gated behind their own `if` (`src/SigilBuild.Installer.Host/ViewModels/InstallerViewModel.cs:1041-1055`). It is not tied to declaring a parameter named `install_dir` — see the warning under [Parameters](parameters.md#cli-overrides-at-install-time). Includes a TextBox, Browse..., and a live disk-space readout via `DriveInfo`.
3. **License** — license text (placeholder today; a `installer.license_path` field lands post-MVP), shown only when the manifest has one.
4. **N x parameter pages** — one page per unique `screen:` value declared on install-time parameters, in first-appearance order. Parameters without a `screen:` value collapse into a trailing synthetic "Install Options" page.
5. **Installing** — progress feed driven by the step engine.
6. **Finish** — completion summary.

`/S` on the command line suppresses every interactive screen and runs the manifest end-to-end using parameter defaults plus any `/PName=Value` overrides. See [Parameters](parameters.md).

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

The `.ico` is stamped into the produced `setup.exe`'s Explorer icon, into the wizard process, and into the deployed `uninstall.exe`. Omit the field and Sigil uses a bundled default (the Saki / Alexandre Moore icon, credited in the OSS README).

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
- **There is no reserved `install_dir` parameter.** The Choose Install Location screen (previous section) is a fixed part of the flow, wired to the engine's own `{install_dir}` resolution — it does not read a manifest parameter of that name. Do not declare one; see the warning in [Parameters](parameters.md#cli-overrides-at-install-time).
- If the manifest declares no install-time parameters at all, the wizard still renders an empty Install Options page so the flow has a slot between License and Installing.

## Silent install

```bash
setup.exe /S /D="C:\Apps\MyApp" /Pedition=professional
```

`/S` skips every screen and runs the step list non-interactively. `/D=path` overrides the install directory (see [the setup.exe reference](../setup-exe-reference.md#d)); any `/PName=Value` tokens override the matching `install_time: true` parameter default — the `P` prefix is required, a bare `/Name=Value` is rejected (`CommandLineParser.cs:497,503-504`). Undeclared parameter names are rejected at parse time. Bool values write back as the literal strings `True` / `False`.

## See also

- [Manifest reference - installer](../manifest-reference.md#installer)
- [Manifest reference - installer.brand](../manifest-reference.md#installerbrand)
- [Parameters](parameters.md)
- [Packaging formats](packaging-formats.md)
