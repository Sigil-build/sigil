# ADR-008: Expression, variable & extensibility policy

- **Status:** Accepted (policy ADR; enables feature-parity lanes P1/P4/P9)
- **Date:** 2026-07-13
- **Decision driver:** `docs/plan/feature-parity/00-GAP_ANALYSIS.md` §4
  ("Cross-cutting prerequisite") and `01-IMPLEMENTATION_PLAN.md` P0 — the
  wrapper's expression engine, packager, and localization deferral **already
  cite ADR-008** as their governing contract, but the file was never written.
  Four in-tree citation sites and the packaging test suite point here; this ADR
  makes those citations resolve and unblocks every P-lane that extends the
  expression, variable, step, or localization surface.
- **Scope:** codifies (1) the closed expression/function catalog and the
  criteria for admitting a new function; (2) the variable model that P1 builds
  on; (3) the secret-redaction contract; (4) the localization stance; (5) the
  closed step catalog and its packaging/overhead bounds; (6) the explicit
  non-goals. This is a **policy ADR** — no `src/` behavior changes are made
  here (the four citation-comment edits that ship with it only add the file
  path). Concrete code lands in the P-lanes, each of which must stay inside the
  envelope this ADR draws, and must **amend this ADR** to widen it.

---

## Decision (TL;DR)

Sigil's manifest is **declarative, closed, and auditable by design**. The
expression engine, the function table, and the step catalog are *closed sets*:
an author can only combine primitives Sigil ships, never introduce new
executable behavior. This is the property Sigil exists to protect — it is what
lets a `sigil.yaml` be reviewed, diffed, reproduced bit-for-bit, and published
under a trusted signature without auditing arbitrary embedded code.

Therefore:

1. **Functions are pure, side-effect-free, and bounded** — evaluated at
   condition-eval time, they may read read-only system state (registry/file/env)
   but must never shell out, reflect, mutate, or touch the network. The table is
   closed; additions require an amendment to this ADR (§1).
2. **Variables (`installer.vars`) are computed once at session start**, exposed
   under the `var.*` namespace and as `{var.*}` brace tokens — the sanctioned
   cross-step data-flow mechanism, replacing the ad-hoc `$var`/stack machinery
   of NSIS/Inno (§2).
3. **Secretness is transitive**: any value derived from a secret is itself a
   secret and inherits redaction everywhere it could surface (§3).
4. **Localization stays AOT-safe**: `InvariantGlobalization=true` is retained;
   wizard strings are localized via **source-generated, culture-neutral string
   tables**, never `.resx` satellite assemblies (§4).
5. **Steps are a closed catalog too** — install-time behavior is the union of
   Sigil's typed step records, stamped deterministically into the Setup.exe as
   Win32 resources; the packaging overhead this adds is bounded (§5).
6. **No embedded scripting, no plugin DLLs** — ever. These are non-goals, not
   backlog items (§6).

---

## Context

The wrapper runtime (`SigilBuild.Wrapper.Core`) ships a small conditional
expression engine (`Expressions/Evaluator.cs`, `Expressions/Functions.cs`) used
for `When` conditions on steps and screens, screen-field defaults, and option
gating. Its source comments already declare a security model "see ADR-008" and
forbid new functions "without amending ADR-008 first" — but no such ADR existed,
so the boundary was folklore. The feature-parity track (P1–P12) is about to
*extend* that surface substantially (data-retrieval functions, a variable model,
new steps, localization). Without a written contract there is nothing to amend
against and no shared definition of what an admissible extension looks like.

This ADR writes that contract down. It is deliberately conservative: the goal is
to close the top ~90% of what installer authors reach for scripting to do, using
*declarative* primitives, while never re-opening the door to arbitrary code that
would defeat Sigil's auditability, determinism, and AOT-safety guarantees.

### Citation sites this ADR governs

| Site | What it cites ADR-008 for |
|------|---------------------------|
| `src/SigilBuild.Wrapper.Core/Expressions/Functions.cs` | closed function table; "amend ADR-008 first" to add a function that does I/O beyond the read-only allowance |
| `src/SigilBuild.Wrapper.Core/Expressions/Evaluator.cs` | the security model: closed function table, closed identifier set, no reflection/shell/dynamic dispatch |
| `src/SigilBuild.Packaging/ExeWrapper/ExeWrapperPackager.cs` | the closed step catalog stamped into Setup.exe as Win32 resources |
| `src/SigilBuild.Wrapper/SigilBuild.Wrapper.csproj` | the localization stance (`InvariantGlobalization` + revisit-here pointer) |
| `tests/SigilBuild.Packaging.Tests/ExeWrapper/ExeWrapperPackagerTests.cs` | the wrapper-overhead cap and its two-component "option (b)" reconciliation (§5) |

---

## 1. Closed function catalog & admission criteria

### 1.1 The table today

`Functions.Table` (ordinal-keyed, closed) currently admits exactly:

| Function | Signature → | Nature |
|----------|-------------|--------|
| `defined(x)` | `bool` | pure; observes a missing identifier as absent |
| `empty(x)` | `bool` | pure; null / empty-string / empty-collection test |
| `version_gte(a, b)` | `bool` | pure; `System.Version` compare, ordinal fallback |
| `os_version()` | `string` | reads `Environment.OSVersion` (read-only OS state) |
| `arch()` | `string` | reads `RuntimeInformation.ProcessArchitecture` |
| `locale()` | `string` | reads the OS UI language via `GetUserPreferredUILanguages` (top preference; `""` when unavailable / non-Windows) |
| `file_exists(path)` | `bool` | bounded read-only filesystem probe |
| `registry_exists(hive, key, name)` | `bool` | bounded read-only registry probe (Windows-guarded) |

Identifiers are a **closed set** too: the evaluator resolves against a supplied
context dictionary keyed by full path (`param.*`, `option.*`, `app.*`,
`system.*`, `scope*`, `install_dir`) and **throws** on any unknown identifier —
except `defined()`/`empty()`, which observe a missing identifier as `null` so
authors can guard optional inputs. There is no dynamic dispatch, no reflection,
no user-defined function.

### 1.2 Admission criteria (a function may be added iff **all** hold)

A candidate function is admissible only if it is:

1. **Pure w.r.t. side effects** — evaluating it changes nothing. No writes, no
   process spawn, no environment mutation, no user prompt.
2. **Network-free at eval time.** Conditions are evaluated repeatedly while
   rendering the wizard and deciding step applicability; a network call there
   would be non-deterministic, slow, and un-auditable. (Network I/O belongs to
   **steps**, which run once at install time under the journal — see §5.)
3. **Reflection-free and AOT-safe.** No `Activator`, `Assembly.*`, `GetType`,
   `MakeGeneric`, `Emit`. Any `IL2xxx`/`IL3xxx` warning is a build failure under
   the wrapper's `PublishAot` + `TreatWarningsAsErrors`, so this is enforced by
   the compiler, not just review.
4. **Bounded, read-only I/O only.** Reading a single registry value, a file's
   existence/version, or an environment variable is allowed — these are the
   `RegistrySearch`/`ReadRegStr` equivalents installer authors need. Enumerating
   a whole hive, globbing a tree, or reading unbounded content is not.
5. **Total and deterministic on its inputs.** Returns a defined value (empty
   string / `false`) on the absent/denied path rather than throwing; never
   depends on wall-clock or randomness.

### 1.3 Functions pre-admitted for P1

Because P1 is on the critical path and this ADR exists to unblock it, the
following data-retrieval functions are **admitted now** under §1.2 — P1 may add
them without a further amendment:

| Function | Returns | Notes |
|----------|---------|-------|
| `registry_read(hive, key, value)` | `string` | `""` when absent/denied; read-only; Windows-guarded like `registry_exists` |
| `env(name)` | `string` | `""` when unset; process environment read |
| `file_version(path)` | `string` | `""` when file absent or unversioned; `FileVersionInfo`, no load/execute |
| `installed_version(app_id)` | `string` | reads Sigil's own ARP entry; `""` when not installed; feeds P3 upgrade logic |

All four return `string` (never null), do bounded read-only I/O, and are
AOT-safe. Any *further* function — and in particular anything that would breach
a §1.2 rule — requires a new amendment appended to this ADR with the
justification and the review sign-off, mirroring the note in `Functions.cs`.

---

## 2. Variable model (enables P1)

`installer.vars` is a manifest-level map of **named computed values**:

```yaml
installer:
  vars:
    old_path: "registry_read('HKLM', 'Software\\Acme\\App', 'InstallPath')"
    is_upgrade: "installed_version(app.id) != ''"
```

Rules:

- **Evaluated once, at session start**, in declaration order, before the wizard
  renders and before the journal opens. A var expression may reference earlier
  vars, `param.*`, `app.*`, `system.*`, and any admitted function. Evaluation is
  a single pass — no lazy re-evaluation, no cyclic references (a cycle is a
  pack-time / load-time error, not a runtime surprise).
- **Exposed as the `var.<name>` namespace** in every place an identifier is
  legal: `When` conditions, screen-field defaults, option gating.
- **Usable as `{var.<name>}` brace tokens** inside step paths and arguments —
  this is the sanctioned cross-step data-flow channel (e.g. a `file_copy` whose
  destination is `{var.old_path}` read from the registry). Brace-token
  substitution is textual and happens at step-materialization time.
- **Serialized into the blob**: `vars` map into `SerializableWrapperBlob` +
  its `JsonSerializerContext` + `schemas/sigil-schema.json` in a single owned
  pass (the M0 discipline), so the closed schema stays the source of truth.
- **Type**: vars resolve to the same value domain as the expression engine
  (string / integer / boolean / list). Absent reads yield `""`, keeping `When`
  guards like `var.old_path != ''` well-defined.

This replaces the imperative `$var`/stack idioms of NSIS and the typed-var
`RegQueryStringValue` pattern of Inno with a single declarative mechanism that is
evaluated deterministically and is fully visible in the manifest.

---

## 3. Redaction contract (secretness is transitive)

Sigil already treats designated secret inputs (secret `param.*` values, e.g.
license keys, tokens) as redactable. This ADR makes the rule **transitive and
total**:

> **Any value derived from a secret is itself a secret.**

Concretely:

- A `var` whose expression references a secret `param.*` (directly, or through
  another secret var, or as a function argument) **inherits secretness**. Secret
  provenance propagates through the whole derivation graph computed in §2.
- A secret value must be redacted **everywhere it could surface**: the install
  log (P7), the rollback journal, wizard display fields, diagnostic/error
  messages, and any brace-token expansion echoed into a trace. Redaction is
  applied at the sink, so a new logging or variable surface added by any P-lane
  inherits it by construction rather than by remembering to call a redactor.
- Redaction is value-based, not name-based: it is the *taintedness of the value*
  that triggers masking, so copying a secret into a differently-named var or
  step argument does not launder it.
- When in doubt, a value is treated as secret. Over-redaction is a cosmetic
  cost; under-redaction is a security defect.

Any new variable, logging, or diagnostic surface introduced by a P-lane **must
preserve this contract** and add a test proving a secret-derived value is absent
from the new sink (the standing "secret grep" test, extended per-lane).

---

## 4. Localization stance (enables P9)

The wrapper builds with `InvariantGlobalization=true`. This is **not** an
accident to be undone for localization — it is a hard requirement of the
Native-AOT size/behavior contract (skips ICU, avoids the Turkish-I class of
path/registry comparison bugs). Constructing `new CultureInfo("ru-RU")` throws
at runtime under this setting, so `.resx` **satellite assemblies are off the
table** — they would also break single-file AOT packaging and reintroduce
per-culture DLLs into a deterministic artifact.

Decision for P9:

- **Keep `InvariantGlobalization=true`.** Do not add satellite assemblies.
- Localize built-in wizard strings via a **source-generated, culture-neutral
  string table** — string keys resolved against compiled-in translation maps,
  AOT-safe, no reflection, no ICU dependency. All comparisons/formatting stay
  ordinal/invariant.
- **Language selection** is explicit (`installer.language`) or auto-detected via
  `locale()`, with a safe fallback to English.
- **Manifest-supplied translations** for declared screen text use a
  `{ en: ..., de: ... }` shape on the relevant fields, serialized through the
  closed schema/blob like every other declared value.
- **A language ships only with a named reviewer** recorded in its catalog
  file's provenance header. The initial set is English plus Ukrainian; further
  languages are admitted under this rule as ordinary content contributions,
  not as amendments. What protects users is review, not count — a
  machine-translated language nobody has read is worse than an honest English
  fallback. A pseudo-localization pass is the test that catches any hardcoded
  (untabled) string.

This is exactly the "revisit together with ADR-008" the `SigilBuild.Wrapper.csproj`
comment points at: localization is enabled **without** relaxing
`InvariantGlobalization`.

---

## 5. Closed step catalog & packaging bounds

Install-time behavior is the union of Sigil's **typed, journaled step records**
(`file_copy`, `directory_create`/`delete`, `file_delete`,
`registry_write`/`delete_value`/`delete_key`, `shortcut_create`, `run_program`,
`service_install`, `env_set`, …). Like the function table, this catalog is
**closed and extended only in-repo, with an amendment to this ADR** — the same
discipline the `Functions.cs` comment states for functions applies to steps.

Steps, unlike functions, run **once at install time under the rollback
journal**, so they *may* perform real I/O — including, where a lane admits it,
bounded network I/O (P4's `http_download`, HTTPS + mandatory sha256) and
config-file mutation (P8's `ini_write`/`json_edit`/`xml_edit`, each snapshotting
prior state for rollback). The eval-time purity rule of §1 is precisely what
keeps *conditions* deterministic while allowing *steps* to act. Planned
additions (P4/P8/P11/P12 steps) are admitted through this section as they land;
each amends the catalog here and lands in the closed schema/blob.

### 5.1 Deterministic stamping into Setup.exe

`ExeWrapperPackager` stamps the resolved step blob and payload into the
Native-AOT runtime as Win32 resources — `SIGIL_BLOB_V1` (the JSON step +
parameter blob), `SIGIL_PAYLOAD_V2` (deterministic zstd payload container), and
`SIGIL_RUNTIME_V1` (the embedded wizard host + native deps, T18). Output is
byte-deterministic for a given manifest + payload.

### 5.2 Wrapper-overhead cap — the two-component "option (b)"

ADR-008 **originally** proposed a single flat cap of **5 MB** on wrapper
overhead, on the assumption the stamped runtime was a thin AOT console host.
T18 invalidated that assumption: the Setup.exe now bundles the full Native-AOT
wizard host plus its Skia/ANGLE/HarfBuzz native runtime, so a real stamped exe
carries ~28 MB over the payload and a single flat cap is meaningless.

Two reconciliation options were considered:

- **(a)** Re-pin a single, larger flat magic number for total overhead —
  rejected as fragile (it conflates two unrelated things and drifts every time
  the runtime changes).
- **(b)** **Split the measurement into two independent components** —
  **adopted**:
  1. **Bundled AOT runtime** (host exe + raw native deps) — the legitimately
     large part, governed separately by the **host size gate** in
     `scripts/publish-installer-runtime.ps1`. Re-pinned **40 → 45 MB** by P9
     (measured win-x64 footprint ~42 MB; see the 2026-07-15 amendment). This gate
     is on the *runtime it bundles*, decoupled from the 5 MB wrapper-overhead cap
     below — a feature adding legitimate weight to the host (localization here)
     re-pins this consciously and does not touch the 5 MB cap.
  2. **Wrapper-code / packaging overhead** — everything the packager *adds* on
     top: the stamped `SIGIL_BLOB_V1`, the compressed-payload framing, and PE
     resource-table alignment. **This** is what ADR-008's **5 MB cap** governs,
     and it stays enforced (`ExeWrapperPackagerTests`).

The 5 MB cap therefore lives on, scoped precisely to the overhead Sigil itself
introduces, decoupled from the size of the runtime it bundles.

---

## 6. Non-goals (explicit, not backlog)

These are **permanent** design boundaries, not features awaiting a sprint:

- **No embedded scripting language.** No NSIS-style script, no Inno Pascal
  `[Code]`, no MSI custom actions, no expression that executes author-supplied
  code. Arbitrary code re-introduces the entire class of problems Sigil exists
  to eliminate: un-auditable behavior, non-determinism, AOT-incompatibility, and
  an unbounded trust surface under a publisher signature. Gaps G1–G9 are closed
  with *declarative* equivalents (typed steps, admitted functions, lifecycle
  phases, variables) instead.
- **No plugin DLL ecosystem.** No NSIS-style plugin `.dll`s, no loadable
  extensions. Extensibility is "closed catalog, extended in-repo, amended
  through this ADR." A third party wanting a new capability contributes a typed
  step or function upstream, where it is reviewed against §1.2 — it does not ship
  a binary that Setup.exe loads.

Reversing either non-goal is not an "amendment"; it is a different product and
would require replacing this ADR wholesale, not extending it.

---

## Consequences

- **Enables** P1 (functions + variable model + redaction), P4 (`http_download`
  step under §5), and P9 (localization under §4) without further architectural
  decisions — each stays inside this envelope.
- Every future function/step addition **amends this ADR** (append a dated row +
  justification) and lands in the closed `sigil-schema.json` + blob context.
  This is the single, enforceable definition of "closed catalog" the standing
  rules require.
- The redaction contract (§3) is a **cross-cutting test obligation**: each lane
  that adds a value sink extends the secret-absence test.
- The 5 MB wrapper-overhead cap (§5.2) is retained and unambiguous, keeping the
  `ExeWrapperPackagerTests` citation truthful after the T18 runtime change.
- Because the boundaries are compiler- and test-enforced (AOT analyzers,
  `TreatWarningsAsErrors`, deterministic-output and secret-grep tests), this ADR
  is not merely aspirational — a change that breaches it fails the build or a
  test rather than silently eroding the model.

---

## Amendment log

| Date | Change | Justification |
|------|--------|---------------|
| 2026-07-13 | Initial policy: closed function/step catalogs, admission criteria (§1.2), P1 functions pre-admitted (§1.3), variable model (§2), transitive redaction (§3), localization stance (§4), packaging bounds + option (b) (§5), non-goals (§6). | P0 — write down the contract the four citation sites + packaging tests already assume. |
| 2026-07-15 | §1.1: `locale()` re-pointed from `CurrentUICulture.Name` to `GetUserPreferredUILanguages`. §4: the "5–10 languages" seed count replaced by a standing named-reviewer rule; initial set en + uk. §5.2: host size gate re-pinned **40 → 45 MB**. | P9/G10 — `locale()` returned `""` under `InvariantGlobalization`, so the documented language-resolution chain could not work. **This is a behavior change, not only a source change:** a `When` using `locale()` moves from an always-`""` result to a real tag, which can flip conditions. Practical risk is ~zero precisely because the function was useless, but it is recorded here rather than assumed. `InvariantGlobalization` stays on; no satellite assemblies; no `CultureInfo` is constructed. **Size gate:** localization added ~2.26 MB to the win-x64 host (→ ~42 MB); `main` was already at 39.8 MB against the old 40 MB gate (0.2 MB headroom), so this is the P13-anticipated "globalization adds weight — re-pin consciously" case, not profligacy. Verified no ICU/globalization data was pulled in (InvariantGlobalization intact, all `CultureInfo` uses are the `InvariantCulture` singleton). New gate carries ~3 MB headroom. |

*(Append one row per future function/step/localization change that widens the
surface. Never rewrite prior rows.)*
