# WiX usage frequency — 5-slot survey

> **Data source.** This survey counts XML element/construct occurrences across 5 representative `.wxs` slots drawn from the WiX Toolset v5 source tree (commit `b8977d6f88e7b68e000bac226a2814f236770570`). Each slot maps to a real-world installer category. *Caveat:* these are upstream test fixtures, not field installers — they exercise individual features in isolation, not at production scale. For a sanity-check baseline, the bottom of this document also reports corpus-wide counts across all 902 `.wxs` files in the WiX repo.

## Slots surveyed

1. **app-ui** — `WixToolsetTest.CoreIntegration/.../Decompile/ExpectedUI.wxs` — UI-heavy, file-rich app installer (every `WixUI` dialog + `Publish` events). See `samples/wix/app-ui/SOURCE.md`.
2. **web-iis** — `WixToolsetTest.Iis/TestData/UsingIis/` — IIS-hosted web service (`iis:` extension namespace). See `samples/wix/web-iis/SOURCE.md`.
3. **db-sql** — `WixToolsetTest.Sql/TestData/UsingSql/` — SQL database/service install (`sql:` extension namespace). See `samples/wix/db-sql/SOURCE.md`.
4. **net-firewall** — `WixToolsetTest.Firewall/TestData/UsingFirewall/` — firewall-rule-bearing app (`fw:` extension namespace). See `samples/wix/net-firewall/SOURCE.md`.
5. **acl-permission** — `WixToolsetTest.Util/TestData/PermissionEx/` — privileged install with ACL grants (`util:PermissionEx`). See `samples/wix/acl-permission/SOURCE.md`.

## Per-slot counts

| Element / construct | app-ui | web-iis | db-sql | net-firewall | acl-permission | Total |
|---|---|---|---|---|---|---|
| `<File>` | 1 | 1 | 2 | 1 | 1 | 6 |
| `<Component>` | 1 | 2 | 1 | 1 | 1 | 6 |
| `<RegistryValue>` | 0 | 0 | 0 | 0 | 1 | 1 |
| `<RegistryKey>` | 0 | 0 | 0 | 0 | 1 | 1 |
| `<Shortcut>` | 0 | 0 | 0 | 1 | 0 | 1 |
| `<Environment>` | 0 | 0 | 0 | 0 | 0 | 0 |
| `<ServiceInstall>` | 0 | 0 | 0 | 1 | 1 | 2 |
| `<ServiceControl>` | 0 | 0 | 0 | 0 | 0 | 0 |
| `<CustomAction> Type 50/226 (exec)` | 0 | 0 | 0 | 0 | 0 | 0 |
| `<CustomAction> Type 1/17 (DLL)` | 0 | 0 | 0 | 0 | 0 | 0 |
| `<RemoveFile>` | 0 | 0 | 0 | 0 | 0 | 0 |
| `<RemoveFolder>` | 0 | 0 | 0 | 0 | 0 | 0 |
| `<MoveFile>` | 0 | 0 | 0 | 0 | 0 | 0 |
| `<IniFile>` | 0 | 0 | 0 | 0 | 0 | 0 |
| `<ProgId>/<Extension>/<Verb>` | 0 | 0 | 0 | 0 | 0 | 0 |
| `util:PermissionEx` | 0 | 0 | 0 | 0 | 5 | 5 |
| `fw:FirewallException` | 0 | 0 | 0 | 21 | 0 | 21 |
| `difx:DriverPackage` | 0 | 0 | 0 | 0 | 0 | 0 |
| `Conditional` | 141 | 0 | 0 | 0 | 0 | 141 |

## Top-3 most-frequent constructs (per-slot survey)

1. `Conditional` (`<Condition>` element + `Condition=` attribute) — **141 occurrences**, all in app-ui where `<Publish>` events on dialog controls carry per-event MSI conditions.
2. `<fw:FirewallException>` — **21 occurrences**, concentrated in net-firewall where program- / port- / service-scoped variants and ipv4/ipv6 scopes are exhaustively covered.
3. Tied between `<File>` and `<Component>` — **6 each**. Every slot has at least one of each, since `<Component>` is the indivisible install unit and `<File>` is the most common payload primitive.

> Honourable mention: `util:PermissionEx` (5 occurrences) — the canonical WiX expression for ACL grants on file/registry/service objects, isolated to the acl-permission slot.

## Corpus-wide totals (all 902 `.wxs` files in `wix/` repo)

> Aggregate across the entire WiX upstream test corpus as a sanity check on whether the 5-slot sample is representative.

| Element / construct | Total occurrences |
|---|---|
| `<File>` | 377 |
| `<Component>` | 379 |
| `<RegistryValue>` | 86 |
| `<RegistryKey>` | 32 |
| `<Shortcut>` | 11 |
| `<Environment>` | 7 |
| `<ServiceInstall>` | 8 |
| `<ServiceControl>` | 1 |
| `<CustomAction> Type 50/226 (exec)` | 0 |
| `<CustomAction> Type 1/17 (DLL)` | 0 |
| `<RemoveFile>` | 2 |
| `<RemoveFolder>` | 12 |
| `<MoveFile>` | 0 |
| `<IniFile>` | 6 |
| `<ProgId>/<Extension>/<Verb>` | 9 |
| `util:PermissionEx` | 23 |
| `fw:FirewallException` | 61 |
| `difx:DriverPackage` | 0 |
| `Conditional` | 736 |

### Top-3 corpus-wide

1. `Conditional` — **736** (`Condition=` attributes dominate; they appear on `<Publish>`, `<Component>`, `<Feature>`, `<Custom>`, MSI sequence rows, etc.)
2. `<Component>` — **379** (closely tracked by `<File>` at 377 — virtually every component declares one keyed file)
3. `<File>` — **377**

## Counting methodology

- Counts come from `survey.sh` using `grep -E -o`.
- Patterns and the canonical row set are listed in `survey.sh`.
- Counts measure *static occurrences in the .wxs source*, not runtime install actions executed by `msiexec`.
- The `Conditional` row counts the union of the `<Condition>` element and the `Condition=` attribute — a deliberate choice because both compile to the same MSI `Condition` field and represent the same authoring concept.
- All counts are reproducible: run `bash research/survey.sh <slot-spec>...` from the `sigil/` repo root. Each `<slot-spec>` is either a bare `.wxs` path (column header derives from parent dir) or a `label=path1:path2` form for slots that span multiple files.

### Reproduce the per-slot table

```bash
WIX_REPO=/c/Projects/hw/repos/Ascendly-Tools/ascendly-installer/wix
bash research/survey.sh \
  "app-ui=$WIX_REPO/src/wix/test/WixToolsetTest.CoreIntegration/TestData/Decompile/ExpectedUI.wxs" \
  "web-iis=$WIX_REPO/src/ext/Iis/test/WixToolsetTest.Iis/TestData/UsingIis/Package.wxs:$WIX_REPO/src/ext/Iis/test/WixToolsetTest.Iis/TestData/UsingIis/PackageComponents.wxs" \
  "db-sql=$WIX_REPO/src/ext/Sql/test/WixToolsetTest.Sql/TestData/UsingSql/Package.wxs:$WIX_REPO/src/ext/Sql/test/WixToolsetTest.Sql/TestData/UsingSql/PackageComponents.wxs" \
  "net-firewall=$WIX_REPO/src/ext/Firewall/test/WixToolsetTest.Firewall/TestData/UsingFirewall/Package.wxs:$WIX_REPO/src/ext/Firewall/test/WixToolsetTest.Firewall/TestData/UsingFirewall/PackageComponents.wxs" \
  "acl-permission=$WIX_REPO/src/ext/Util/test/WixToolsetTest.Util/TestData/PermissionEx/Package.wxs:$WIX_REPO/src/ext/Util/test/WixToolsetTest.Util/TestData/PermissionEx/PackageComponents.wxs"
```

### Reproduce the corpus-wide totals

```bash
WIX_REPO=/c/Projects/hw/repos/Ascendly-Tools/ascendly-installer/wix
FILES=$(find "$WIX_REPO/src" -name '*.wxs')
# For each pattern in survey.sh, run:
echo "$FILES" | xargs -d '\n' grep -E -ho '<File\b'         | wc -l   # 377
echo "$FILES" | xargs -d '\n' grep -E -ho '<Component\b'    | wc -l   # 379
# ...etc
```

## Caveats

- **Test-fixture bias.** The corpus is WiX's own test suite; each fixture is engineered to exercise a single feature surface. Edge-case features are over-represented (fixture for one feature has dense usage of that one feature), and "boring but ubiquitous" patterns may be under-represented. A true frequency survey of *field* installers would lean more heavily toward `<File>`, `<Component>`, `<Shortcut>`, `<RegistryValue>`, and `Condition=`, and far less toward `fw:FirewallException` and dialog `<Publish>` events.
- **WiX 5 vs WiX 3 regression.** The cloned repo is WiX 5. Counts of `<difx:DriverPackage>` are 0 across all 902 files because driver-install support (the DIFx extension) was deprecated in WiX 4 and is not in the WiX 5 ship. That zero is a **real signal**, not a regex bug: driver install is no longer a first-class WiX scenario.
- **`<CustomAction> Type=` regex returns 0.** Investigation of the corpus confirms this: WiX 4 / WiX 5 abandoned the numeric `Type=` attribute entirely in authoring. CustomActions are now declared with high-level attributes (`BinaryRef`, `DllEntry`, `ExeCommand`, `Property`, `Value`, `Execute`, `Return`) and the linker computes the low-level `Type` field. Any field installer using WiX 4+ syntax will likewise have zero raw `Type=50` / `Type=226` text. Raw numeric Types only appear in `Decompile` round-trip fixtures (which weren't part of our 5 slots). For Sigil's purposes, the relevant signal is "exec-style CAs exist; they're authored declaratively now," not the underlying numeric Type.
- **Component bundles.** WiX projects typically split across multiple `.wxs` files (`Package.wxs` + `PackageComponents.wxs`); slots that contain both are summed by passing both as a colon-separated list to `survey.sh`.
- **The single-slot survey for app-ui has very high `Conditional` density.** That's an artifact of the chosen fixture (`ExpectedUI.wxs` is a `Decompile` fixture covering all WixUI dialogs and their per-event publish conditions). For the corpus-wide `Conditional=736` total, the dominant contributor is the same: `<Publish>` events on dialog controls. A field installer without a custom UI will be far below this.
