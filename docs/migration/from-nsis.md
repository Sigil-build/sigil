# Migrating from NSIS to Sigil

> **Skeleton (Sprint 5a / WBS 2.13f).** Detailed migration recipes are filled in during Sprint 11 (WBS 6.1). Tier assignments reflect the [Sprint 5a catalog lock](../../../sigil-docs/implementation/sprints/sprint-05a.md).

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
| `InetC::get` / `NSISdl::download` | (POST-MVP) | bundle the dependency in the package payload instead |
| `System::Call` | (declined) | use a signed `run_program` invoking a small helper exe |
| `Push` / `Pop` | (declined — declarative model) | use parameters + conditional steps |
| `LangString` | (POST-MVP) | English only in v1.x; localisation is a v1.x cross-cutting epic, not a step-library addition |

## Concept differences

- **Imperative vs declarative:** NSIS scripts execute top-to-bottom with mutable state. Sigil step lists are pure descriptions of intent; no shared variables.
- **Sections:** NSIS `Section` blocks group commands and contribute to component selection. Sigil's `parameters: { type: enum }` + `when:` clauses replace this.
- **`OnInit` / `.onInstSuccess`:** map to `pre_install:` / `post_install:` blocks.
- **Plugin model:** NSIS's plugin DLLs (`SimpleSC`, `AccessControl`, `nsisFirewall`, `InetC`/`NSISdl`, `System::Call`) are absent from Sigil's surface. The functionality they provide is split between dedicated MUST/SHOULD step types (`service_install`, `acl_grant`, `firewall_rule`), the `run_program` escape hatch, and explicit declines.
- **Modern UI macros:** `MUI_*` macros (e.g., `!insertmacro MUI_PAGE_STARTMENU`) belong to the wizard host (Sprint 5b), not the step engine. The migration story for a Modern UI installer goes through the wizard's screen 6 (Custom template) plus `parameters:`.
- **Case-insensitivity:** NSIS commands are case-insensitive (`File`, `file`, `FILE` all work). Sigil step names are case-**sensitive** lowercase. Migrate canonical-cased.

## Examples

> _To be filled in Sprint 11 with worked examples for: bare-bones installer (file copy + uninstall), multi-user installer (HKLM/HKCU split), conditional install (Modern UI components page → `when:` clauses)._
