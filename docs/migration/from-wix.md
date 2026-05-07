# Migrating from WiX to Sigil

> **Skeleton (Sprint 5a / WBS 2.13f).** Detailed migration recipes are filled in during Sprint 11 (WBS 6.1 — `docs.sigil.build`). Tier assignments below reflect the [Sprint 5a catalog lock](../../../sigil-docs/implementation/sprints/sprint-05a.md), not the as-drafted ADR-008 summary.

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
| `<fire:FirewallException>` | `firewall_rule` (SHOULD-tier) | promoted from POST-MVP per Sprint 5a survey findings — bias caveat noted in catalog |
| `<difx:DriverPackage>` | (post-MVP) | gap acknowledged; users with driver-install needs stay on WiX for v1.0. Note: WiX 5 itself deprecated DIFx |
| `<Condition>` attribute | `when:` clause on the step | pure expression; no IL or VBScript |

## Concept differences

- **Component model:** WiX's `<Component>` groups files for ref-counting on uninstall. Sigil tracks rollback per step; refcounting across products is not in MVP.
- **Sequence tables:** WiX exposes the InstallExecuteSequence ordering. Sigil's order is the textual order of `install_steps:`.
- **Bootstrapper / Burn:** declined — see [ADR-008](../../../sigil-docs/architecture/adr-008-install-step-engine.md).
- **Merge modules:** declined — author shared step blocks via YAML anchors.
- **Custom-action DLL types:** declined — Sigil does not load arbitrary DLLs at install time. Use a signed `run_program` invoking a small helper exe instead.
- **Numeric `CustomAction Type=`:** WiX 4/5 abandoned numeric Types entirely; the linker computes them from declarative attributes. The Sprint 5a survey shows 0 corpus-wide for numeric Types — this is a measurement artefact of modern WiX, not an indicator that custom actions are unused.

## Examples

> _To be filled in Sprint 11 with worked examples for: copy + register, install service, multi-edition with conditionals, file-association registration._
