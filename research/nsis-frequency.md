# NSIS usage frequency — 5-slot survey

> **Data source.** This survey counts NSIS command and plugin-call occurrences across 5 representative `.nsi` slots drawn from the upstream NSIS source tree (commit `1929435c23fc08342cb2fdcea2950021ce31c2ec`, repo `https://github.com/kichik/nsis`). Each slot maps to a real-world installer category. *Caveat:* these are upstream `Examples/` scripts, not field installers — they are intentionally compact demonstrations that bias toward the feature each example was written to illustrate. For a sanity-check baseline, the bottom of this document also reports corpus-wide counts across all 57 `.nsi` files in the NSIS repo.

## Slots surveyed

1. **app-basic** — `Examples/example2.nsi` — bare-bones installer (file copy, registry uninstall key, optional Start-Menu shortcut). See `samples/nsis/app-basic/SOURCE.md`.
2. **app-shared** — `Examples/install-shared.nsi` — all-users (HKLM-only) install with explicit admin-rights enforcement and Add/Remove Programs uninstall key. See `samples/nsis/app-shared/SOURCE.md`.
3. **app-multiuser** — `Examples/MultiUser.nsi` — dual-context HKCU + HKLM installer demonstrating the MultiUser.nsh helper. See `samples/nsis/app-multiuser/SOURCE.md`.
4. **app-bigtest** — `Examples/bigtest.nsi` — comprehensive feature exerciser (multiple `Section`s, `MessageBox` flows, `IfFileExists`, `WriteINIStr`, `ExecWait`, license/components/directory pages). See `samples/nsis/app-bigtest/SOURCE.md`.
5. **app-modernui** — `Examples/Modern UI/StartMenu.nsi` — Modern UI installer using the Start-Menu page (`MUI_PAGE_STARTMENU`) and `LangString` / `MUI_DESCRIPTION_TEXT` patterns. See `samples/nsis/app-modernui/SOURCE.md`.

All 5 primary files were present at HEAD, so no fallbacks (`example1.nsi`, `Modern UI/Basic.nsi`) were needed.

## Per-slot counts

| Command / construct | app-basic | app-shared | app-multiuser | app-bigtest | app-modernui | Total |
|---|---|---|---|---|---|---|
| `File` | 1 | 1 | 1 | 2 | 0 | 5 |
| `CreateDirectory` | 1 | 0 | 0 | 2 | 1 | 4 |
| `Delete` | 3 | 3 | 3 | 6 | 2 | 17 |
| `RmDir` | 2 | 1 | 1 | 5 | 2 | 11 |
| `WriteRegStr/DWORD/Bin/MultiStr/ExpandStr` | 5 | 6 | 3 | 8 | 1 | 23 |
| `DeleteRegValue` | 0 | 0 | 0 | 0 | 0 | 0 |
| `DeleteRegKey` | 2 | 1 | 1 | 2 | 1 | 7 |
| `CreateShortCut` | 2 | 1 | 1 | 3 | 1 | 8 |
| `Exec/ExecWait/nsExec::Exec` | 0 | 0 | 0 | 2 | 0 | 2 |
| `WriteINIStr` | 0 | 0 | 0 | 4 | 0 | 4 |
| `IfFileExists` | 0 | 0 | 0 | 2 | 0 | 2 |
| `${If}` | 0 | 2 | 0 | 0 | 0 | 2 |
| `MessageBox` | 0 | 2 | 0 | 19 | 0 | 21 |
| `nsDialogs::Create` | 0 | 0 | 0 | 0 | 0 | 0 |
| `SimpleSC::*` | 0 | 0 | 0 | 0 | 0 | 0 |
| `AccessControl::*` | 0 | 0 | 0 | 0 | 0 | 0 |
| `nsisFirewall/SimpleFC::*` | 0 | 0 | 0 | 0 | 0 | 0 |
| `InetC::get/NSISdl::download` | 0 | 0 | 0 | 0 | 0 | 0 |
| `System::Call` | 0 | 0 | 0 | 0 | 0 | 0 |
| `Push/Pop` | 0 | 1 | 0 | 0 | 0 | 1 |
| `LangString` | 0 | 0 | 0 | 0 | 1 | 1 |

## Top-3 most-frequent constructs (per-slot survey)

1. `WriteRegStr/DWORD/Bin/MultiStr/ExpandStr` — **23 occurrences**, present in every slot. Almost all are uninstall-key writes (`DisplayName`, `DisplayIcon`, `UninstallString`, etc.) under `Software\Microsoft\Windows\CurrentVersion\Uninstall\…`. Registry writes are the universal payload of an NSIS installer.
2. `MessageBox` — **21 occurrences**, dominated by `app-bigtest` (19) which exercises informational, error, and yes/no dialog variants. `app-shared` adds 2 admin-rights guards (`MessageBox MB_IconStop "Administrator rights required!"`).
3. `Delete` — **17 occurrences**, present in every slot. Uninstall sections enumerate per-file deletions explicitly — there is no NSIS equivalent of MSI's automatic component teardown.

> Honourable mentions: `RmDir` (11), `CreateShortCut` (8), `DeleteRegKey` (7), `File` (5).

## Corpus-wide totals (all 57 `.nsi` files in the NSIS repo)

> Aggregate across the entire NSIS upstream `Examples/` and `Contrib/<plugin>/Example.nsi` corpus as a sanity check on whether the 5-slot sample is representative.

| Command / construct | Total occurrences |
|---|---|
| `File` | 243 |
| `CreateDirectory` | 12 |
| `Delete` | 53 |
| `RmDir` | 28 |
| `WriteRegStr/DWORD/Bin/MultiStr/ExpandStr` | 89 |
| `DeleteRegValue` | 12 |
| `DeleteRegKey` | 33 |
| `CreateShortCut` | 12 |
| `Exec/ExecWait/nsExec::Exec` | 24 |
| `WriteINIStr` | 19 |
| `IfFileExists` | 13 |
| `${If}` | 341 |
| `MessageBox` | 87 |
| `nsDialogs::Create` | 7 |
| `SimpleSC::*` | 0 |
| `AccessControl::*` | 0 |
| `nsisFirewall/SimpleFC::*` | 0 |
| `InetC::get/NSISdl::download` | 1 |
| `System::Call` | 67 |
| `Push/Pop` | 195 |
| `LangString` | 61 |

### Top-3 corpus-wide

1. `${If}` — **341** (LogicLib `${If} … ${ElseIf} … ${EndIf}` chains saturate the corpus; many appear inside macros and helper functions, especially in the `Contrib/Modern UI 2/` and `Contrib/InstallOptions/` examples).
2. `File` — **243** (the canonical payload primitive — every example that ships any binary or asset uses `File` at least once, often inside `Section` blocks repeated across `r0..rN` install paths).
3. `Push/Pop` — **195** (NSIS's argument-passing convention — every plugin-style function uses it; macros that wrap plugin calls multiply the count).

> Honourable mentions: `WriteRegStr*` (89), `MessageBox` (87), `System::Call` (67), `LangString` (61).

## Counting methodology

- Counts come from `nsis-survey.sh` using `grep -E -i -o`.
- Patterns and the canonical row set are listed in `nsis-survey.sh`.
- Counts measure *static occurrences in the .nsi source*, not runtime install actions performed by the compiled installer.
- Most rows are anchored with `^\s*` (line start with optional indentation) so that occurrences inside comments (`; File "foo"`) or quoted strings don't over-count. Plugin-style rows (`nsExec::*`, `nsDialogs::Create`, `System::Call`, `SimpleSC::*`, `AccessControl::*`, `nsisFirewall/SimpleFC::*`, `InetC::*` / `NSISdl::*`) are NOT line-anchored because plugin calls sometimes follow labels or macro expansions on the same line; the `::` token is itself a sufficient discriminator. The LogicLib `${If}` family is also unanchored — see the script header for the full rationale.
- All matching is **case-insensitive** (`grep -i`). NSIS as a language is case-insensitive (`File` and `file` and `FILE` are the same command), and the upstream `Examples/` corpus mixes casings freely — `install-shared.nsi` writes `CreateShortcut` while NSIS docs use `CreateShortCut`. Without `-i` the per-slot `CreateShortCut` row would read 0 across the board, which is a counting artefact, not a real signal.
- All counts are reproducible: run `bash research/nsis-survey.sh <slot-spec>...` from the `sigil/` repo root. Each `<slot-spec>` is either a bare `.nsi` path (column header derives from parent dir) or a `label=path1:path2` form for slots that span multiple files.

### Reproduce the per-slot table

```bash
NSIS=/c/Projects/hw/repos/Ascendly-Tools/ascendly-installer/nsis
bash research/nsis-survey.sh \
  "app-basic=$NSIS/Examples/example2.nsi" \
  "app-shared=$NSIS/Examples/install-shared.nsi" \
  "app-multiuser=$NSIS/Examples/MultiUser.nsi" \
  "app-bigtest=$NSIS/Examples/bigtest.nsi" \
  "app-modernui=$NSIS/Examples/Modern UI/StartMenu.nsi"
```

Note: pass paths in MSYS form (`/c/...`), not Windows form (`C:/...`). The slot-spec parser uses `:` as a multi-file separator, which collides with `C:` drive letters on Windows.

### Reproduce the corpus-wide totals

```bash
NSIS=/c/Projects/hw/repos/Ascendly-Tools/ascendly-installer/nsis
files=$(find "$NSIS" -name "*.nsi" -type f -print)
joined=$(echo "$files" | tr '\n' ':' | sed 's/:$//')
bash research/nsis-survey.sh "corpus=$joined"
```

## Caveats

- **Imperative-vs-declarative mismatch.** NSIS is imperative Pascal-flavoured scripting; counts here are static *occurrences of a command in source*, not the number of times the command will fire at runtime. A single `File /r "App\*.*"` may install thousands of files; a `File` inside `${If}` may never fire at all. Direct comparisons against the WiX survey (`wix-frequency.md`), which counts MSI-table-equivalent declarations, are apples-to-oranges. The two surveys answer different questions: WiX answers "how many *install actions* are declared," NSIS answers "how many *script statements* of a given kind appear."
- **LogicLib expansion is invisible.** `${If} … ${EndIf}` is a LogicLib macro that expands to a chain of `StrCmp`/`IntCmp` + jumps. We count the macro invocation, not the expanded form. Conversely, custom macros that *wrap* `${If}` (e.g. `${IfNot}`, `${IfThen}`) won't be counted under this row. The corpus-wide `${If}` total of 341 understates dynamic conditional density.
- **Third-party plugins are absent from the upstream tree.** The rows `SimpleSC::*` (service control), `AccessControl::*` (ACL grants), `nsisFirewall::` / `SimpleFC::*` (firewall rules), and `InetC::get` / `NSISdl::download` (HTTP download) all rely on Contrib plugins that the upstream NSIS repo does not bundle — they are distributed separately on the NSIS Wiki. The corpus shows `0` for SimpleSC, AccessControl, nsisFirewall/SimpleFC and `1` for InetC/NSISdl precisely because the source examples for those plugins live in their own download archives, not in `kichik/nsis`. In a field-installer corpus these would be present; here their absence is a property of the data source, not a real-world frequency signal.
- **Example-driven bias.** The `Examples/` directory is engineered to teach individual NSIS features, not to be representative of production installers. Examples that demonstrate Modern UI custom pages will spike `nsDialogs::Create` / `${If}`; examples for `Push/Pop` plumbing will spike those rows. A field-installer survey would show flatter distributions, with `File`, `WriteRegStr*`, `CreateShortCut`, `Delete`, and `RmDir` taking a heavier share than the corpus suggests, and macro-heavy LogicLib usage growing with project size.
- **Case-insensitive matching is a deliberate choice.** Without `-i`, the regex `^\s*CreateShortCut\b` returns 0 across all 5 slots even though every slot has a Start-Menu shortcut, because the upstream `.nsi` files spell it `CreateShortcut` (lowercase 'c'). Since NSIS is case-insensitive at the language level, case-insensitive matching is the more honest count.
- **MSYS path requirement.** Slot-specs use `:` as the multi-file separator, which collides with Windows drive-letter colons (`C:`). The script accepts MSYS-style paths (`/c/...`) only. This is a CLI-shape compatibility decision with `survey.sh`, which has the same constraint.
