# Gap analysis — Sigil vs NSIS / Inno Setup / WiX / InstallShield

Date: 2026-07-13. Baseline: `main` @ `b1e21d5` (exe-installer track T1–T18
complete). Method: repo capability inventory (steps, expression engine, schema,
docs) cross-checked against a tiered survey of the most-used functionality in
NSIS, Inno Setup, WiX Toolset, and InstallShield (evidence: official docs
prominence, canonical plugins/extensions, Stack Overflow tag frequency).

Companion docs: [`01-IMPLEMENTATION_PLAN.md`](01-IMPLEMENTATION_PLAN.md) (what
to build, in what order), [`02-DOCS_UPDATES.md`](02-DOCS_UPDATES.md) (doc
work). Tiers: **T1** = nearly every real-world installer uses it, **T2** = very
common, **T3** = niche but expected of a mature tool.

## 1. What Sigil already has (parity confirmed)

| Capability | Sigil implementation |
|---|---|
| File / dir / registry / shortcut install with rollback | step catalog: `file_copy`, `directory_create/delete`, `file_delete`, `registry_write/delete_value/delete_key`, `shortcut_create` — all journaled |
| ARP registration (real fields, scope-correct hive) | `ArpRegistration` (T10) |
| Silent install conventions | `/silent`, `/S`, `/verysilent`, `/D=`, `/Pname=value`, `/allusers`, `/currentuser`, exit codes 0/1/2/64 |
| Wizard: license, destination, options, progress, custom param screens | T8/T9/T13/T14; declared `screens` + widget inference |
| Per-user vs per-machine + self-elevation | `ScopeResolver`/`ScopeLayout`/`Elevation` (T12) — dual scope like Inno 6 `PrivilegesRequiredOverridesAllowed` |
| External process execution | `run_program` (args, cwd, expected exit codes, timeout) |
| Windows services | `service_install` (+start, rollback stop+delete) |
| Env vars / PATH | `env_set` + `add_to_path` option, WM_SETTINGCHANGE broadcast |
| File associations | `file_associations` option component |
| Signing + verified-publisher trust line | `SigilBuild.Signing`, `AuthenticodeVerifier` (WinVerifyTrust) |
| Uninstaller that survives deleting Setup.exe | `uninstall.exe` copy + self-deletion (T15) |
| Transactional rollback | `RollbackJournal` — stronger than NSIS/Inno, MSI-like |
| Deterministic compressed payload | zstd `SIGIL_PAYLOAD_V2` |
| Conditions on steps/screens | `When` expression engine: `param.*`, `option.*`, `app.*`, `system.*`, `scope*`, `install_dir`; fns `defined, empty, version_gte, os_version, arch, locale, file_exists, registry_exists` |
| Reinstall idempotency / repair | T10 existing-install detection, double-install safe |
| Scheduled tasks | `scheduled_task_create` step (P11) — wraps `schtasks.exe /Create`, always runs as `SYSTEM`, machine-scope only (SIG0310), journaled rollback via `schtasks /Delete` |
| COM/DLL registration | `com_register` step (P11) — invokes the target DLL's exported `DllRegisterServer`/`DllUnregisterServer` via a statically-bound `[LibraryImport]`/unmanaged function pointer (AOT-safe, no reflection), machine-scope only (SIG0310) |
| Firewall rules | `firewall_rule` step (P11) — wraps `netsh advfirewall firewall add/delete rule`, delete-then-add for idempotent reinstalls, machine-scope only (SIG0310) |
| Auto-update (full package) | `updates:` manifest block + `Setup.exe /Update` (P12) — ECDSA P-256-signed channel manifest (detached `.sig`, ADR-009), version compare vs installed ARP entry, sha256-verified full-package download and hand-off to the downloaded installer; dedicated exit codes 0/6/7/8/9. **Delta/binary-diff updates are explicitly deferred** — `deltaTargets` stays a parsed, schema-validated, functionally inert field (ADR-010) |
| Web (net) installer | `sigil pack --payload web --package-url <https-url>` (P12) — emits a tiny stub `Setup.exe` (`WebSetup`) whose only install action is an `http_download` of the full package (sha256-verified) followed by handing off to it; refuses without a resolvable HTTPS `--package-url` (SIG0322) |

## 2. Gaps

### Tier 1 — highest priority (competitors treat as table stakes)

| # | Gap | What competitors have | Sigil today | Plan task |
|---|---|---|---|---|
| G1 | **Data retrieval into variables (cross-step data flow)** | NSIS `ReadRegStr`/$vars/stack; Inno `RegQueryStringValue` into typed vars; WiX `RegistrySearch` → property. The single most common custom-code building block. | `registry_exists()` returns bool only; no way to read a registry value, file version, or env var into a value usable by later steps/screens/paths | P1 |
| G2 | **Lifecycle hooks** | NSIS `.onInit/.onInstSuccess`, Inno `InitializeSetup/CurStepChanged`, WiX CA sequencing, InstallScript `OnBegin` | steps run only in the install body; no pre-install / post-install / pre-uninstall / post-uninstall phases for `run_program` etc. | P2 |
| G3 | **Upgrade semantics (version-aware)** | WiX `MajorUpgrade` + downgrade block; Inno/NSIS detect-old-and-uninstall idiom — top SO topic for all three | reinstall/repair of the *same* build exists (T10); no version compare vs installed ARP entry, no uninstall-old-first, no downgrade guard | P3 |
| G4 | **Run app after install** | MUI_FINISHPAGE_RUN, Inno `[Run] postinstall`, WixShellExec | Done screen has no launch checkbox | P2 (small) |

### Tier 2 — very common, expected by most teams

| # | Gap | What competitors have | Sigil today | Plan task |
|---|---|---|---|---|
| G5 | **HTTP download at install time** | NSIS INetC/NScurl; Inno `DownloadTemporaryFile` + `CreateDownloadPage` (built-in since 6.1); Burn `DownloadUrl` | `HttpOptionsLoader` fetches JSON for a ComboBox only; no file download step, no web-installer mode | P4 |
| G6 | **Prerequisite detect + install (VC++ redist, .NET, etc.)** | Burn `ExePackage`+DetectCondition; InnoDependencyInstaller; InstallShield .prq — every ecosystem grew a canonical solution | expressible by hand via `When` + `run_program`, but no first-class prereq unit (detect → bundled/downloaded → exit-code 3010 handling) | P5 |
| G7 | **Files-in-use / close running app** | Inno `AppMutex`+`CloseApplications` (Restart Manager); MSI RM dialog; NSIS KillProc/LockedList plugins | nothing — install fails on locked files | P6 |
| G8 | **Install logging to file** | MSI `/l*v` (enterprise expectation); Inno `/LOG=` | journal exists but no user-facing `/LOG=` install log | P7 |
| G9 | **Config file editing (INI/XML/JSON)** | NSIS `WriteINIStr`; Inno `[INI]`; WiX `IniFile`, `util:XmlFile/XmlConfig` | no config-edit steps; `run_program` workaround only | P8 |
| G10 | **Multi-language wizard** | NSIS ~60 languages + selector; Inno `[Languages]` .isl; InstallShield built-in | `InvariantGlobalization`; en-only; localization explicitly deferred pending ADR-008 | P9 |
| G11 | **Component/feature selection depth** | NSIS SectionGroups; MSI feature tree | flat 4-component fixed `options` set; no custom app-defined components mapping to file sets | P10 |

### Tier 3 — niche but expected of a mature tool

| # | Gap | What competitors have | Sigil today | Plan task |
|---|---|---|---|---|
| G12 | ~~Scheduled tasks~~ **Shipped P11** | `schtasks` via Exec everywhere; WiX community ext | `scheduled_task_create` step — machine-scope only (SIG0310), journaled rollback | P11 — closed |
| G13 | ~~COM/DLL registration~~ **Shipped P11** | MSI Class tables; NSIS `RegDLL`; Inno `regserver` | `com_register` step — `DllRegisterServer`/`DllUnregisterServer` via AOT-safe function pointer, machine-scope only (SIG0310) | P11 — closed |
| G14 | ~~Firewall rules~~ **Shipped P11** | WiX firewall extension; `netsh` elsewhere | `firewall_rule` step — wraps `netsh advfirewall firewall add/delete rule`, machine-scope only (SIG0310) | P11 — closed |
| G15 | ~~Auto/delta updates~~ **Full-package shipped P12; delta deferred** | Velopack/Squirrel core feature; MSIX AppInstaller | `updates:` block + `Setup.exe /Update`: ECDSA-P256-signed channel manifest (ADR-009), version-aware fetch/verify/install, dedicated exit codes. `deltaTargets` stays parsed-but-inert **by design** — delta/binary-diff patches are explicitly deferred to a later release (ADR-010), not implemented | P12 — closed (full-package); delta tracked by ADR-010, not a plan task |
| G16 | ~~Web (net) installer — stub exe, payload downloaded~~ **Shipped P12** | Burn; NSIS web installers | `sigil pack --payload web --package-url <url>` — tiny stub `Setup.exe` downloads + sha256-verifies + hands off to the full package | P12 — closed |
| G17 | Installer self single-instance mutex | Inno `SetupMutex`; NSIS CreateMutex idiom | none | P6 (bundled) |
| G18 | Drivers (pnputil) | InstallShield/Advanced Installer | none — **explicitly out of scope for now** | — |
| G19 | IIS/SQL configuration | WiX extensions | none — **out of scope** (server-app niche) | — |

## 3. Deliberate non-goals (keep Sigil's design intact)

- **No embedded scripting language** (NSIS script / Pascal [Code] / custom
  actions). Sigil's differentiator is a declarative, AOT-safe, auditable
  manifest; arbitrary code re-introduces the class of problems Sigil exists to
  kill. Gaps G1–G9 are closed with *declarative* equivalents (typed steps,
  expression functions, lifecycle phases) that cover the top ~90% of what
  people actually script.
- **No plugin DLL ecosystem** (NSIS-style). Extensibility remains "closed step
  catalog, extended in-repo" per the security note in `Functions.cs`; every
  new function/step amends ADR-008.
- Drivers, IIS, SQL (G18/G19): revisit only on user demand.

## 4. Cross-cutting prerequisite

`Wrapper.Core/Expressions/Functions.cs` and the localization deferral both
cite **ADR-008**, but no `docs/architecture/adr-008-*.md` exists. Before any
P-task extends the expression surface, ADR-008 (expression/function security
policy) must actually be written — it is task P0 in the implementation plan.
