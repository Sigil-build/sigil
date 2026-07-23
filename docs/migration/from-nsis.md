# Migrating from NSIS to Sigil

> **Skeleton (Sprint 5a / WBS 2.13f).** Detailed migration recipes are filled in during Sprint 11 (WBS 6.1). Tier assignments reflect the [Sprint 5a catalog lock](../../../sigil-docs/implementation/sprints/sprint-05a.md).
>
> **Wizard + uninstaller shipped (D-016, 2026-05-14).** The Modern UI page model and the auto-generated uninstaller — features previously called out as needing a workaround — are now first-class in Sigil. See "Wizard page mapping" and "Uninstaller mapping" below.

## Command-to-step mapping

| NSIS command / plugin | Sigil equivalent | Notes |
|---|---|---|
| `File` | `file_copy` | — |
| `CreateDirectory` | `directory_create` | — |
| `Delete` | `file_delete` | — |
| `RmDir /r` | `directory_delete` with `recursive: true` | — |
| `WriteRegStr` / `WriteRegDWORD` / `WriteRegBin` / `WriteRegMultiStr` / `WriteRegExpandStr` / `WriteRegNone` | `registry_write` | — |
| `DeleteRegValue` | `registry_delete_value` | — |
| `DeleteRegKey` | `registry_delete_key` | — |
| `CreateShortCut` (also `CreateShortcut`) | `shortcut_create` | NSIS is case-insensitive; Sigil canonicalises to `shortcut_create` |
| `Exec` / `ExecWait` / `ExecShell` / `nsExec::Exec` / `nsExec::ExecToLog` / `nsExec::ExecToStack` | `run_program` | `wait: true` covers `ExecWait` / `nsExec::*`; capture-output options live in step properties |
| `WriteINIStr` | `ini_write` (POST-MVP) | workaround: render INI from a template (POST-MVP) — bundle the file instead in v1.0 |
| `IfFileExists` | `when: file_exists(path)` | — |
| `${If} <expr>` (LogicLib) | `when:` expression | operators map 1:1 — `==`, `!=`, `<=`, `>=`, `&&`, `\|\|`, `!`, `in`, `not_in` |
| `${ElseIf}` / `${Else}` / `${EndIf}` | (no direct equivalent) | model an `${If}/${ElseIf}/${Else}` chain as separate steps each with its own `when:` clause |
| `MessageBox` | `show_message` (SHOULD-tier; wizard-only) | non-interactive (`/S`) flag suppresses |
| `nsDialogs::Create` | `prompt_text` / `prompt_yes_no` (SHOULD-tier; wizard screen 6) | declarative widget set; custom widgets post-MVP |
| `SimpleSC::InstallService` | `service_install` (SHOULD-tier) | — |
| `SimpleSC::StartService` / `SimpleSC::StopService` | `service_control` (SHOULD-tier) | — |
| `AccessControl::*` | `acl_grant` / `acl_revoke` (SHOULD-tier) | promoted from POST-MVP per Sprint 5a survey findings |
| `nsisFirewall::*` / `SimpleFC::*` | `firewall_rule` (SHOULD-tier) | promoted from POST-MVP per Sprint 5a survey findings |
| `RegDLL` / `UnRegDLL` | `com_register` (P11) | machine-scope only (SIG0310); invokes `DllRegisterServer`/`DllUnregisterServer` directly |
| `ExecWait "schtasks.exe /Create ..."` / `nsExec::ExecToLog "schtasks.exe ..."` | `scheduled_task_create` (P11) | machine-scope only (SIG0310); always runs as `SYSTEM`; `daily` trigger uses a fixed `/ST 00:00` |
| `nsExec::Exec "netsh advfirewall firewall add rule ..."` | `firewall_rule` (P11) | machine-scope only (SIG0310); alternative to the `nsisFirewall::*`/`SimpleFC::*` row above — deletes any same-named rule before adding, so reinstalls stay idempotent |
| `InetC::get` / `NSISdl::download` | (POST-MVP) | bundle the dependency in the package payload instead |
| `System::Call` | (declined) | use a signed `run_program` invoking a small helper exe |
| `Push` / `Pop` | (declined — declarative model) | use parameters + conditional steps |
| `LangString` | (POST-MVP) | English only in v1.x; localisation is a v1.x cross-cutting epic, not a step-library addition |

## Concept differences

- **Imperative vs declarative:** NSIS scripts execute top-to-bottom with mutable state. Sigil step lists are pure descriptions of intent; no shared variables.
- **Sections:** NSIS `Section` blocks group commands and contribute to component selection. Sigil's `parameters: { type: enum }` + `when:` clauses replace this.
- **`OnInit` / `.onInstSuccess`:** map to `pre_install:` / `post_install:` blocks.
- **Plugin model:** NSIS's plugin DLLs (`SimpleSC`, `AccessControl`, `nsisFirewall`, `InetC`/`NSISdl`, `System::Call`) are absent from Sigil's surface. The functionality they provide is split between dedicated MUST/SHOULD step types (`service_install`, `acl_grant`, `firewall_rule`), the `run_program` escape hatch, and explicit declines.
- **Modern UI macros:** `MUI_*` macros are realised by the wizard host. The `MUI_PAGE_DIRECTORY` page is auto-generated when the manifest declares an `install_dir` parameter (with a disk-space readout); `MUI_PAGE_INSTFILES` is the wizard's locked Installing screen. See "Wizard page mapping" below.
- **Case-insensitivity:** NSIS commands are case-insensitive (`File`, `file`, `FILE` all work). Sigil step names are case-**sensitive** lowercase. Migrate canonical-cased.

## Wizard page mapping

> Shipped in D-016 (2026-05-14). The wizard flow is built dynamically from the `parameters:` block — there's no Sigil equivalent of hand-coding `!insertmacro MUI_PAGE_*` directives.

| NSIS Modern UI directive | Sigil equivalent | Notes |
|---|---|---|
| `!insertmacro MUI_PAGE_WELCOME` | always rendered (Welcome step) | brand slots customise the logo / app name / version |
| `!insertmacro MUI_PAGE_LICENSE` | always rendered (License step) | manifest will accept a `installer.license_path` field; today the wizard ships a placeholder |
| `!insertmacro MUI_PAGE_DIRECTORY` | auto-inserted when `parameters.install_dir` is declared | dedicated screen with TextBox + Browse… + drive selector + free-space readout |
| `!insertmacro MUI_PAGE_COMPONENTS` | (no direct equivalent) | model components as `parameters.<feature>.type: bool` with `screen:` grouping; the wizard renders one CheckBox per bool |
| `!insertmacro MUI_PAGE_CUSTOMFUNCTION_*` (themed pages) | `parameters.<name>.screen: "Page Title"` | one wizard page per unique `screen` value, in declaration order |
| `!insertmacro MUI_PAGE_INSTFILES` | always rendered (Installing step) | locked layout |
| `!insertmacro MUI_PAGE_FINISH` | always rendered (Finish step) | "Launch now" checkbox configurable post-MVP |
| `!insertmacro MUI_UNPAGE_*` | (no direct equivalent) | uninstall flow is currently silent — see Uninstaller mapping below |

**Per-parameter widget selection (FR-IU-16):**

| Manifest declaration | Widget |
|---|---|
| `type: enum`, `values: [...]` | static ComboBox |
| any `type` + `source: { url, items_path, value_property, label_property }` | dynamic ComboBox (HTTPS-fetched on page-attach; URL template substitution via `${parameters.X}`) |
| `type: bool` | CheckBox |
| anything else (`string`, `path`, `int`, `secret`) | TextBox |

## Uninstaller mapping

> Shipped in D-016 (2026-05-14). NSIS auto-generates `uninstall.exe` from a `Section Uninstall`; Sigil does the same from the manifest's `uninstall:` block.

| NSIS construct | Sigil equivalent | Notes |
|---|---|---|
| `Section "Uninstall"` | top-level `uninstall:` step list | renamed from `pre_uninstall:` per D-016; the legacy key is no longer accepted |
| `WriteUninstaller "$INSTDIR\Uninstall.exe"` | (automatic) | the packager generates a ~4 MB stamped wrapper copy, embeds it as the `SIGIL_UNINSTALLER_V1` resource, and the wrapper drops it to `install_dir\uninstaller.exe` on install success |
| `WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\$AppId" "UninstallString" "..."` | (automatic) | the wrapper writes both `UninstallString` and `QuietUninstallString` to point at the deployed exe |
| `Delete $INSTDIR\file.dat` inside the uninstall section | (use the install journal) | the wrapper's rollback journal already replays a reverse-install when `uninstaller.exe` runs; declare `uninstall:` only for tear-down that the journal can't infer (e.g. stopping a service, deleting an AppData cache) |
| `RMDir /r $INSTDIR` at the end of the uninstall section | (automatic) | the journal replay removes everything the installer wrote, then the install dir itself |
| Custom uninstaller icon via `!define MUI_UNICON` | (single icon for installer + uninstaller) | the `installer.icon` manifest field stamps the icon on `setup.exe`, the bundled wizard exe, and the produced `uninstaller.exe` |

## Examples

> _To be filled in Sprint 11 with worked examples for: bare-bones installer (file copy + uninstall), multi-user installer (HKLM/HKCU split), conditional install (Modern UI components page → `when:` clauses)._
