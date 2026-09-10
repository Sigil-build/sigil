# ADR-015: Wizard localization — catalog, resolution chain and tag validator

- **Status:** Accepted (records the shipped P9 design)
- **Date:** 2026-09-10
- **Decision driver:** [ADR-008](adr-008-expression-policy.md) §4 settled the
  localization *stance*
  (`InvariantGlobalization=true` stays; no satellite assemblies; a
  source-generated, culture-neutral string table; a language ships only with a
  named reviewer). It deliberately said nothing about the *mechanism*: how a
  session picks its language, how a surface matches a tag, or where the one
  language-tag rule lives. That detail was written down in the P9 design
  document, which four in-tree comments cited by path and section for their
  rationale. This ADR is the durable home for the three pieces those comments
  need, so the design document could be retired without orphaning them.
- **Scope:** the string catalog's shape and its one hard authoring rule (§1);
  the language-resolution chain and matching algorithm (§2); the shared
  language-tag validator (§3); the deliberate boundary of what the catalog does
  *not* cover (§4). Behaviour is unchanged — this ADR describes code that
  already shipped in P9.

---

## Decision (TL;DR)

1. Built-in wizard chrome lives in a **plain-text catalog per language**,
   compiled in by a source generator. A count may appear in a value but must
   **never inflect the sentence around it**.
2. Language resolution produces an **ordered list of preference tags**, not a
   single tag, and each surface best-matches that list **independently**.
   Resolution runs **once per session, before any UI exists**, and the result is
   immutable for the session.
3. The language-tag grammar is a deliberate **ordinal subset of BCP-47** with
   **one implementation and two call sites** — pack-time validation (`SIG0291`)
   and the installer's `/lang` flag.
4. The catalog covers **prose only**. Per-step failure detail, the log, and
   manifest-authored licence text stay English/verbatim by design.

---

## 1. The catalog

Each language is one plain-text `Strings.<tag>.txt` file under
`src/SigilBuild.Wrapper.Core/Localization/`, read at build time by the
localization source generator (`SigilBuild.Localization.Generator`) and compiled
into a static, reflection-free lookup. English is the baseline and the source of
truth for the key set; a language file that omits a key falls back to English
rather than rendering the key.

### 1.1 The no-inflecting-count rule

**A count may appear in a value; it must never inflect the sentence around it.**

```text
allowed   — "Applications to close: {count}"
forbidden — "{count} applications must be closed"
```

The reason is structural, not stylistic: Ukrainian has three plural forms and
English has two, and string concatenation cannot select between them. Admitting
an inflecting count would mean carrying a plural-rule engine — which is exactly
the ICU dependency `InvariantGlobalization=true` exists to keep out (ADR-008
§4). Phrasing the count as trailing data sidesteps the whole problem, and it is
cheap to obey when the catalog is authored rather than harvested.

Number and date rendering likewise stay invariant. There is no RTL layout: a
manifest **may** supply `ar`/`he` text and it will render, but the layout does
not mirror.

---

## 2. Language resolution

### 2.1 The chain

Resolution yields an **ordered list of preference tags**:

| Source | Yields |
|---|---|
| `installer.language` (fixed by the manifest) | `[tag]` |
| `/lang=<tag>` | `[tag]` |
| OS probe | the full ordered `GetUserPreferredUILanguages` list |
| fallback | `["en"]` |

Tags are **strings, not an enum** — the manifest side must be able to carry tags
Sigil ships no chrome for.

The list matters only for the OS-probe case, and it is the reason to walk it
rather than take the head: a user whose OS preference list is `[de-DE, uk-UA]`
has *said* they read Ukrainian better than English. Taking only the first entry
would hand them English chrome while discarding a signal they explicitly set.
Walking the list costs one dictionary lookup per entry against a table of a
handful of languages.

`locale()` remains a **scalar** — the first (top-preference) entry. It answers
"where is this machine", which is what a manifest author writing
`locale() == 'de-DE'` wants. Its re-pointing from `CultureInfo.CurrentUICulture`
(always `""` under `InvariantGlobalization`) to `GetUserPreferredUILanguages` is
recorded in ADR-008's amendment log as a **behaviour change**, not merely a
source change. Every failure path — non-Windows, a failed API call, a
zero-length list — yields `""`, keeping the function total per ADR-008 §1.2. No
`CultureInfo` is ever constructed; `new CultureInfo("uk-UA")` throws under this
setting.

### 2.2 Each surface matches independently

Two surfaces match the preference list separately:

- **Chrome** — `LanguageResolver.MatchChrome(preferences) → Lang`, over the
  closed generated `Strings.Tags` set, falling back to `Lang.En`.
- **Manifest maps** — `LanguageResolver.Match(preferences, keys)` over the raw
  tags the manifest actually supplied, falling back to `en`.

So `/lang=de` against a manifest that supplies a `de` map renders **German
declared screens with English chrome**. That mixed result is correct: each
surface gives the best it actually has. The alternative — restricting resolution
to the languages Sigil's own chrome ships — would make manifest-supplied
translations useless for every language Sigil does not itself ship, which
defeats the point of the feature.

The manifest-map `en` fallback is **total**, and that totality is exactly what
the missing-`en` pack diagnostic (`SIG0290`) buys. The two are one mechanism.

Because matching is per-surface, "the resolved language" has more than one
answer. `system.language` is therefore defined narrowly as **the resolved chrome
language** — the language Sigil's own UI renders. Accepted consequence: a
manifest supplying `de` screens cannot observe that its own screens resolved to
`de`. The gap is marginal (an author gating on locale wants `locale()`) and
narrows as chrome languages are added.

### 2.3 Matching algorithm

Given an ordered preference list and a surface's available tag set, return the
first hit. Ordinal only, no ICU:

```text
for each tag in the preference list, in order:
    1. exact match, OrdinalIgnoreCase        (pt-BR matches pt-br)
    2. primary subtag                        (de-AT -> de)
    3. ordinal-first among candidates sharing a primary subtag
finally:
    4. en
```

Step 3 exists purely for determinism: it resolves `de` → {`de-AT`, `de-CH`} the
same way on every machine and every pack. Step 4 is reachable for chrome (which
ships a closed set) and **guaranteed to succeed** for manifest maps, because
`SIG0290` makes an `en`-less map a pack-time error.

### 2.4 Where resolution runs

**Once per session, at session start** — immediately after the blob loads (it
needs `installer.language`) and **before any UI is constructed**, including
`Program.cs`'s pre-Avalonia single-instance `MessageBoxW`, which is itself a
catalog string. The resolver depends on the blob and Win32 only, never on
Avalonia, so this ordering is available. Both entry points — the Avalonia
`Installer.Host` and the console `Wrapper` — resolve identically.

The resolved tag is then stored on the session, exposed as `system.language` to
the expression context, used to set the static string accessor's language, and
passed to the view models and the engine. Once set it is **immutable for the
session**, which is what makes the static `{x:Static}` accessor legal and what
lets Sigil ship no language-selection dialog.

---

## 3. The tag validator — one implementation, two call sites

`SIG0291`'s "well-formed language tag" check and the malformed-`/lang` check are
**the same rule** and must not be written twice, or pack time and parse time
drift into accepting different tags. The validator therefore lives in
`SigilBuild.Core` (`Manifest/LanguageTag.cs`), which is viable because
`SigilBuild.Wrapper.Core` already references it. Its two call sites are the
manifest parser (`SIG0291`) and `CommandLineParser` (`/lang`).

The accepted grammar is a deliberate **ordinal subset** of BCP-47, written down
here so it is a contract rather than an implementation detail:

```text
primary  = ALPHA{2,3}
subtag   = ALPHANUM{1,8}
tag      = primary ( "-" subtag )*
```

Matched `OrdinalIgnoreCase`. This accepts everything Sigil realistically needs
(`en`, `uk`, `pt-BR`, `zh-Hans`, `de-AT`) and rejects the malformed. Full
BCP-47 — grandfathered tags, extensions, private-use sequences — buys nothing
here and would need a parser the AOT constraints of ADR-008 would rather not
carry.

---

## 4. Scope boundary: what the catalog does not cover (D2)

The catalog covers **prose engine messages only**. Per-step failure detail stays
English **by design**, and so do three neighbouring surfaces:

| Stays English / verbatim | Why |
|---|---|
| Per-step failure detail (every `StepResult.Failed(...)` string) | Localizing all of them means structured error codes across the whole of `Steps/` — large, collides with other lanes, and translated diagnostics are *harder* to support, not easier |
| The install log | Supportability: the log is what gets pasted into an issue |
| `/?` console help | The console is the support surface |
| Manifest-authored licence text | User-authored content, loaded verbatim by `InstallerLicenseLoader`, never routed through the catalog |

This boundary is load-bearing for the pseudo-localization test: the pass proves
the chrome around a surface is catalogued by bracketing every catalog string, so
the surfaces above must be excluded from it deliberately rather than showing up
as failures. Blocker descriptions from the Restart Manager scan (live process
names and PIDs) and engine failure strings are user-machine data on the same
footing.

Structured step error codes remain **out of scope**, not rejected — the day they
exist for their own reasons, per-step detail becomes localizable for free.

---

## Consequences

- The four in-tree comments that carried this rationale by path+section
  (`LanguageTag.cs`, `LanguageResolver.cs`, `Strings.en.txt`,
  `PseudoLocRenderTests.cs`) now cite a permanent architecture document, so the
  P9 design document could be retired without losing the reasoning.
- ADR-008 §4 remains the *stance*; this ADR is the *mechanism*. A change that
  relaxes `InvariantGlobalization`, adds satellite assemblies, or admits a
  language without a named reviewer amends **ADR-008**. A change to the
  resolution chain, the matching algorithm, the tag grammar, or the catalog's
  authoring rules amends **this** ADR.
- Adding a language is an ordinary content contribution under ADR-008's
  named-reviewer rule: drop in `Strings.<tag>.txt`, no code change.

## Verification

- `LanguageResolverTests` — the chain's precedence, the ordered-list behaviour
  for the OS-probe case, and the matching steps including the
  shared-primary-subtag determinism rule.
- `LanguageTagTests` (Core) plus `LangFlagTests` (Wrapper) — the grammar's
  accepted and rejected forms, exercised from both call sites so the "one
  implementation" claim is enforced rather than asserted.
- `SessionResolutionTests` / `SessionLanguageTests` — resolution runs once, at
  session start, and the language is immutable thereafter.
- `PseudoLocRenderTests` / `NoHardcodedStringsTests` — every chrome string
  renders bracketed under the pseudo language, with §4's exclusions supplied as
  fixtures so their presence is deliberate and visible.
- `LocalizedTextTests` — `SIG0290` (an `en`-less localized map is a pack-time
  error, which is what makes §2.2's fallback total) and `SIG0291` (malformed
  tag).

## Amendment log

- 2026-09-10 — initial version. Records the P9 design as shipped; no behaviour
  change. Written so the retired P9 design document's §3, §4, §6.2 and design
  D2 keep a durable home.
