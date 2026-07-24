# Docs updates — feature-parity track

Doc work tied to [`01-IMPLEMENTATION_PLAN.md`](01-IMPLEMENTATION_PLAN.md).
Rule: each P-task lands its own reference/guide updates in the same branch;
items below without a P-task are standalone doc debt, fixable now.

## Standalone doc debt (no code dependency)

| Item | Action |
|---|---|
| **ADR-008 missing** | Cited by `Functions.cs` and the localization deferral but the file does not exist. Author `docs/architecture/adr-008-expression-policy.md` — this *is* task P0. |
| ~~`docs/migration/from-inno.md` missing~~ **Closed (P13, T13.4)** | Added, mirroring `from-nsis.md`/`from-wix.md`'s structure: `[Setup]`/`[Files]`/`[Icons]`/`[Registry]`/`[Run]` postinstall/`[Tasks]`/`[Languages]`/`AppMutex`+`CloseApplications`/`PrivilegesRequired`/`[Code]` Pascal (declarative-equivalents non-goal table) → manifest equivalents, plus the P11/P12 rows (scheduled task/COM `regserver`/firewall/`DownloadTemporaryFile`/update checks) that didn't exist when this row was first written. Linked from `docs/README.md`. |
| `ORCHESTRATION_PLAN.md` checklist | Done 2026-07-13 — marked ☑ with evidence column, T18 row added, status note updated to "merged as PR #9". |
| `docs/plan/exe-installer-and-wizard.md` status line | Verified 2026-07-13: already marked "SUPERSEDED (2026-07-09) by IMPLEMENTATION_SPEC.md" — no action. |
| CLI reference: `/Update` | Documented behavior is exit 64 "not supported"; keep, but add pointer to P12. |

## Per-task doc deliverables

| Task | Docs to add/update |
|---|---|
| P1 vars | `docs/manifest-reference.md` (`installer.vars`, `var.*`); `docs/guides/conditional-installs.md` (data-retrieval functions, cross-step data flow examples); migration guides: map `ReadRegStr`/`RegQueryStringValue`/`RegistrySearch` |
| P7 logging | `docs/cli-reference.md` (`/LOG`); troubleshooting section in `docs/guides/installer-wizard.md` |
| P2 hooks | manifest-reference (`installer.hooks`, `run_after_install`); `docs/guides/install-steps.md` — loud warning: hooks are outside the rollback journal |
| P4 http_download | `docs/guides/install-steps.md` new step; security note (sha256 mandatory, HTTPS only) |
| P8 config steps | install-steps guide: `ini_write`, `json_edit`, `xml_edit` + rollback semantics |
| P3 upgrade | new `docs/guides/upgrades.md` (fresh/repair/upgrade/downgrade matrix, `/force-downgrade`); uninstaller guide cross-link |
| P5 prerequisites | new `docs/guides/prerequisites.md` with VC++ redist and .NET runtime recipes (the two everyone asks for) |
| P6 files-in-use | installer-wizard guide (close-apps screen); cli-reference (`/closeapps`); manifest-reference (`app_mutex`) |
| P9 localization | new `docs/guides/localization.md`; manifest-reference (localized string objects) |
| P10 components | manifest-reference + `docs/guides/parameters.md` (custom components vs options vs params — decision table) |
| P11 system steps | install-steps guide additions; note allusers-scope requirement |
| P12 updates | new `docs/guides/updates.md` (channels, signed manifest, web installer); ADR for delta deferral |

## Consistency checks at each merge

- `schemas/sigil-schema.json`, `docs/manifest-reference.md`, and blob DTOs must
  move together (M0 discipline).
- Every new step/function appears in: schema, manifest-reference, the relevant
  guide, and at least one migration-guide mapping row.
- CLI help text (`/silent`, new flags) mirrored in `docs/cli-reference.md`.
