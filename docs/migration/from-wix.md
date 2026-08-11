# Migrating from WiX to Sigil

> **Skeleton (Sprint 5a / WBS 2.13f).** Detailed migration recipes are filled in during Sprint 11 (WBS 6.1 — `docs.sigil.build`). Tier assignments below reflect the [Sprint 5a catalog lock](../../../sigil-docs/implementation/sprints/sprint-05a.md), not the as-drafted ADR-008 summary.
>
> **Uninstaller + ARP entry shipped (D-016, 2026-05-14).** WiX customers relying on the `Product` element's automatic Add/Remove Programs registration get equivalent behaviour from Sigil's `uninstall:` block — see "Uninstaller mapping" below.

## Element-to-step mapping

| WiX construct | Sigil equivalent | Notes |
|---|---|---|
| `<File>` | `file_copy` | — |
| `<Component>` | (no direct equivalent — Sigil is a flat declarative step list) | YAML anchors give similar reuse; ref-counted component model not in MVP |
| `<Directory>` / `<Component Directory=...>` | `directory_create` | implicit when target paths nest |
| `<RegistryValue>` | `registry_write` | — |
| `<RegistryKey>` (parent) | (no direct equivalent — Sigil writes leaf values) | `registry_write` creates intermediate keys implicitly |
| `<RemoveRegistryValue>` | `registry_delete_value` | — |
| `<RemoveRegistryKey>` | `registry_delete_key` | `recursive: true` for tree removal |
| `<Shortcut>` | `shortcut_create` | — |
| `<Environment>` | `env_set` | — |
| `<Environment Action='remove'>` | (auto-derived on uninstall) | manual override is post-MVP |
| `<RemoveFile>` | `file_delete` | — |
| `<RemoveFolder>` | `directory_delete` | `recursive: false` matches WiX semantics |
| `<MoveFile>` | `file_move` (POST-MVP) | workaround: `file_copy` + `file_delete` |
| `<IniFile>` | `ini_write` (POST-MVP) | workaround: `file_write` (also POST-MVP) — render config from a template instead |
| `<ProgId>` / `<Extension>` / `<Verb>` | `file_association` (SHOULD-tier) | — |
| `<ServiceInstall>` | `service_install` (SHOULD-tier) | — |
| `<ServiceControl>` | `service_control` (SHOULD-tier) | — |
| `<CustomAction>` Type 50/226 (exec) | `run_program` | with `on_failure: rollback` for transactional behaviour |
| `<CustomAction>` Type 1/17 (DLL) | (declined) | use a signed `run_program` with a small helper exe |
| `<util:PermissionEx>` | `acl_grant` / `acl_revoke` (SHOULD-tier) | promoted from POST-MVP per Sprint 5a survey findings — bias caveat noted in catalog |
| `<fire:FirewallException>` | `firewall_rule` (SHOULD-tier) | promoted from POST-MVP per Sprint 5a survey findings — bias caveat noted in catalog; machine-scope only (SIG0310), delete-then-add keeps reinstalls idempotent |
| Custom action invoking `schtasks.exe` (Type 50/226) | `scheduled_task_create` (P11) | machine-scope only (SIG0310); always runs as `SYSTEM`; `daily` trigger uses a fixed `/ST 00:00` |
| `<Class>` (COM class registration) | `com_register` (P11) | machine-scope only (SIG0310); invokes `DllRegisterServer`/`DllUnregisterServer` directly rather than MSI's advertised registry-based registration |
| `<difx:DriverPackage>` | (post-MVP) | gap acknowledged; users with driver-install needs stay on WiX for v1.0. Note: WiX 5 itself deprecated DIFx |
| `<Condition>` attribute | `when:` clause on the step | pure expression; no IL or VBScript |

## Concept differences

- **Component model:** WiX's `<Component>` groups files for ref-counting on uninstall. Sigil tracks rollback per step; refcounting across products is not in MVP.
- **Sequence tables:** WiX exposes the InstallExecuteSequence ordering. Sigil's order is the textual order of `install_steps:`.
- **Bootstrapper / Burn:** declined — see [ADR-008](../../../sigil-docs/architecture/adr-008-install-step-engine.md).
- **Merge modules:** declined — author shared step blocks via YAML anchors.
- **Custom-action DLL types:** declined — Sigil does not load arbitrary DLLs at install time. Use a signed `run_program` invoking a small helper exe instead.
- **Numeric `CustomAction Type=`:** WiX 4/5 abandoned numeric Types entirely; the linker computes them from declarative attributes. The Sprint 5a survey shows 0 corpus-wide for numeric Types — this is a measurement artefact of modern WiX, not an indicator that custom actions are unused.

## Uninstaller mapping

> Shipped in D-016 (2026-05-14). WiX's `Product` element auto-registers an Add/Remove Programs entry with `MsiExec.exe /x{ProductCode}` as the uninstall command. Sigil produces a sibling `uninstall.exe` and writes the same ARP entry.

| WiX construct | Sigil equivalent | Notes |
|---|---|---|
| `<Product>` (auto-registers ARP entry) | top-level `uninstall:` step list + automatic deployment | the packager generates a ~4 MB stamped wrapper copy, embeds it as the `SIGIL_UNINSTALLER_V1` resource, and the wrapper drops it to `install_dir\uninstall.exe` on install success |
| `<RegistrySearch>` for `UninstallString` | (automatic) | the wrapper writes both `HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\<AppId>\UninstallString` and `QuietUninstallString` to point at the deployed exe |
| `MsiExec.exe /x{...}` invocation | `<install_dir>\uninstall.exe` (silent: append `/S`) | the dedicated exe replays the install journal automatically; the manifest's `uninstall:` block runs first for tear-down the journal can't infer |
| `<Product Icon=`...`>` for the ARP icon | `installer.icon` | the same icon stamps `setup.exe`, the bundled wizard exe, and the produced `uninstall.exe` |
| `<Property Id="ARPNOREMOVE" Value="1"/>` | (no direct equivalent — omit `uninstall:` to skip generation) | when the manifest has no `uninstall:` block, no uninstall.exe is generated and no ARP entry is written |

## Wizard page mapping

> Shipped in D-016 (2026-05-14). WiX UI sets (`WixUI_InstallDir`, `WixUI_FeatureTree`, …) are realised by Sigil's wizard host, configured via the `parameters:` block.

| WixUI dialog | Sigil equivalent | Notes |
|---|---|---|
| `LicenseAgreementDlg` | rendered only when the manifest has a license (`installer.license_path` / `installer.license`) | placeholder text today; `InstallerViewModel.cs:1046-1049` gates this screen on `_hasLicense` |
| `InstallDirDlg` (and the WixUI_InstallDir set's path picker) | **always rendered**, second after Welcome — **not** conditioned on declaring any parameter | dedicated screen with TextBox + Browse… + drive selector + free-space readout. There is no `parameters.install_dir` concept: `InstallerViewModel.RebuildFlow` adds this screen (`InstallerStep.InstallOptions`) unconditionally, unlike `License`/`Options` right after it (`InstallerViewModel.cs:1041-1055`). See the warning in [Parameters](../guides/parameters.md#cli-overrides-at-install-time). |
| `CustomizeDlg` (WixUI_FeatureTree feature selector) | `parameters.<feature>.type: bool` with `screen:` grouping | one CheckBox per bool parameter; group with the new `screen:` field |
| `WixUI_*` themed sequences | `parameters.<name>.screen: "Page Title"` | one wizard page per unique `screen` value, in declaration order |
| `ProgressDlg` | always rendered (Installing step) | locked layout |
| `ExitDialog` | always rendered (Finish step) | "Launch now" checkbox configurable post-MVP |

## Examples

> _To be filled in Sprint 11 with worked examples for: copy + register, install service, multi-edition with conditionals, file-association registration._
