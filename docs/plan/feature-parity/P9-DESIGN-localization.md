# P9 design — localized wizard chrome + manifest-supplied translations

- **Status:** Approved (design); implementation not started
- **Date:** 2026-07-15
- **Gap:** G10 (multi-language wizard) — `00-GAP_ANALYSIS.md` Tier 2
- **Lane:** P9, wave 4 — `task/p9-localization`
- **Governing ADR:** `docs/architecture/adr-008-expression-policy.md` §4
  (localization stance), §1 (closed function catalog — `locale()` changes here)

---

## 1. Goal

Ship a localized wizard without relaxing `InvariantGlobalization=true`.

Two independent surfaces get localized:

1. **Built-in chrome** — the ~85 strings Sigil itself owns (wizard, uninstall,
   and the handful of engine prose messages). Compiled in, closed set.
2. **Manifest-supplied text** — declared screen titles/subtitles, parameter
   labels, and the license, which the *manifest author* translates. Open set:
   an author may supply any language tag, including ones Sigil ships no chrome
   for.

Both resolve against **one ordered preference list** (§4.2) but match against it
**independently** (§4.4) — so the two surfaces can land on different languages,
which is deliberate rather than an oversight.

## 2. Decisions

Five decisions were taken during design. Each is recorded with its rationale so
a later reader does not have to re-derive it.

| # | Decision | Rationale |
|---|---|---|
| D1 | `locale()` reads the **OS UI language** via Win32; the **resolved chrome language** is exposed separately as `system.language` | `locale()` returns `""` today (§4.1) and cannot feed resolution. Splitting the two keeps `locale()` meaning "where is this machine" while `system.language` means "what language is the UI in" — they genuinely diverge whenever `/lang` or `installer.language` is set. `locale()` also already answers "is this user Ukrainian", which is why `system.language` can be narrowly defined as the chrome language (§4.3) |
| D2 | Catalog covers **prose engine messages only**; per-step failure detail stays English | Every `StepResult.Failed(...)` string can reach the Failed screen (§7.3). Localizing all ~20 means structured error codes across all of `Steps/` — large, collides with other lanes, and translated diagnostics are *harder* to support. Same argument as the English log |
| D3 | `/?` help screen is **new** and stays **English** | No help text exists today at all (§6.3). Console output is the support surface; an admin grepping docs for `/lang=` should not get a translated page. Also avoids a `/lang` pre-pass, since usage errors stay English too |
| D4 | Seed set is **en + uk only** (not the ADR's 5–10) | Both are reviewable by the author, so **nothing unreviewed ships**. The other nine become a pure content follow-up once the mechanism is proven by two languages |
| D5 | **Roslyn incremental generator** (repo's first) | ADR-008 §4 says "source-generated" literally, and this is a repo whose ADR exists to end folklore-vs-contract. Buys compile-time key *and* argument checking (§3.2) |

### 2.1 Language conflict rule

`installer.language` is **fixed** and beats `/lang`. A conflicting `/lang` is
**ignored, logged, and exit stays 0**.

This deliberately does *not* follow T12's precedent, where a fixed manifest
scope conflicting with `/allusers` is exit 64. Scope is a trust and
consequence boundary; language is a display preference. Failing an install over
a cosmetic preference is hostile, so the precedent does not transfer.

## 3. The catalog

### 3.1 Location and format

The catalog lives in **`SigilBuild.Wrapper.Core/Localization/`**, not the host,
because the engine prose messages (D2) need it and the host already references
Core. One catalog, one generator invocation, both entry points served.

Size impact is negligible (~85 strings × 2 languages ≈ 8 KB against a 13.59 MB
CLI), but P13 requires it measured, not assumed — see §8.

Catalog files ship as `AdditionalFiles`:

```text
Strings.en.txt
Strings.uk.txt
```

Format is one `key = value` per line, `#` comments, with **named** placeholders:

```text
nav.back  = Back
upgrading = Upgrading {appName} from {fromVersion} to {toVersion}.
```

Every non-`en` file carries a provenance header naming its reviewer (§9).

#### The no-inflecting-count rule

Concatenation (§3.2) has no answer for pluralization, and `uk` has three plural
forms. The catalog therefore adopts a **phrasing rule**: *a count may appear in
a string, but must never inflect the sentence around it.*

```text
# allowed   — count is data, grammar is fixed
closeapps.pending = Applications to close: {count}

# forbidden — grammar depends on {count}; uk needs 3 forms, en needs 2
closeapps.pending = {count} applications must be closed
```

This costs **nothing today**: a sweep of the host UI found **zero** count-bearing
strings. `CloseAppsView` renders an `ItemsControl` over a list of blockers, and
`InstallSession.BuildBlockerMessage` `string.Join`s names — neither formats a
count. The rule is therefore purely preventive, and is enforced at key-review
time rather than by the generator (a heuristic that guessed at "count-adjacent"
`en` text would be brittle and would fail open).

If a future key genuinely needs an inflecting count, that is the trigger to add
plural-select to the generator — and to amend this section, not to work around
it at the call site.

### 3.2 Generator contract

The generator emits per-language **concatenation, never `string.Format`**:

```csharp
public static string Upgrading(Lang lang, string appName, string fromVersion, string toVersion) => lang switch
{
    Lang.Uk => "Оновлення " + appName + " з " + fromVersion + " до " + toVersion + ".",
    _       => "Upgrading " + appName + " from " + fromVersion + " to " + toVersion + ".",
};
```

Three properties fall out of this shape, and each is load-bearing:

- **No `CultureInfo` is touched anywhere.** "Number/date rendering stays
  invariant" becomes structural rather than a rule people must remember.
- **Each language owns its own expression**, so Ukrainian and Japanese may
  reorder placeholders freely. A positional `{0}` scheme would not allow this.
- **Named placeholders are checkable.** The generator compares the placeholder
  *set* between `en` and each translation, so a translator silently dropping
  `{fromVersion}` is a build error, not a truncated sentence in production.

The generator emits two accessors per key:

- `Strings.NavBack(Lang lang)` — explicit, for VMs and the engine.
- `S.NavBack` — static, resolved against the session language, for XAML
  `{x:Static}` (§7.1). Safe because the session language is immutable: there is
  no language-selection dialog (§10).

#### Guarding the static accessor

`S` reads a static session language that §4.6's ordering guarantees is
initialized first. That guarantee is an *ordering* one, so it survives only as
long as nobody reorders `Program.cs` — and the failure mode if they do is a
**silent wrong-language render**, which is worse than a crash because no test
and no user reports it.

`S` therefore guards its backing field: **throw in Debug** (a reorder fails the
test suite immediately), **fall back to `en` and log an error in Release** (a
shipped installer renders English rather than dying). The asymmetry is
deliberate: turn an unobservable defect into a loud one at development time
without turning it into an outage at install time.

#### `Lang.Pseudo`

`Lang.Pseudo` (§8.1) is **compiled into release binaries** but is `internal`,
reachable only via `InternalsVisibleTo` from the test projects. **No valid
language tag maps to it**: `pseudo` fails §6.2's `alpha{2,3}` primary-subtag
grammar and is rejected as a usage error for free, and a well-formed
`/lang=qps` finds no chrome and falls back to `en`.

Excluding it from release builds instead would require a `SIGIL_PSEUDOLOC`
constant applied to the *host compilation* — a separate build configuration that
the standing `dotnet test Sigil.sln -c Release` command cannot express, so the
pseudo-loc test would need its own CI leg and would rot. That trade is a whole
build configuration against **~5 KB in a 37.4 MB host**. Unreachability is
enforced by visibility and grammar, not by absence.

### 3.3 Generator diagnostics

| Code | Condition | Severity |
|---|---|---|
| `SIGLOC001` | Key in a translation that `en` does not have | Error |
| `SIGLOC002` | Key in `en` missing from a translation (falls back to `en`) | Warning — partial translations are legal |
| `SIGLOC003` | Placeholder set differs between `en` and a translation | Error |
| `SIGLOC004` | Duplicate key within a file | Error |
| `SIGLOC005` | Malformed line | Error |

`SIGLOC002` is a warning by design: it lets a language land incrementally. Note
`TreatWarningsAsErrors` is on repo-wide, so a partial translation must be
completed before merge regardless — the severity split exists so the *reason*
is legible, not to create an escape hatch.

## 4. Language resolution

### 4.1 The `locale()` problem

`Expressions/Functions.cs:50` today:

```csharp
// CurrentUICulture.Name is "" under InvariantGlobalization=true
["locale"] = _ => CultureInfo.CurrentUICulture.Name,
```

Under `InvariantGlobalization=true` this returns `""` — always. The task's
chain says "else `locale()` best-match", which **cannot work as written**.
`locale()` is re-pointed at `GetUserPreferredUILanguages` (`MUI_LANGUAGE_NAME`)
via `[LibraryImport]`, Windows-guarded.

This stays inside ADR-008 §1.2: pure, network-free, reflection-free, bounded
read-only, total and deterministic. Only its *source* changes; its signature
does not. It still requires an amendment row (§9), and that row must record it
as a **behavior change, not merely a source change**: any existing manifest
using `locale()` in a `When` goes from an always-`""` result to a real tag,
which can flip conditions. Practical risk is ~zero precisely because the
function was useless, but the amendment log is where that gets said rather than
assumed.

**`locale()` returns a scalar — the first (top-preference) entry** of the OS
list. It answers "where is this machine", and a manifest author writing
`locale() == 'de-DE'` wants the primary. The full ordered list is used by the
resolver (§4.2), not exposed through the function.

Failure paths all yield `""`, keeping the function total per ADR-008 §1.2:
non-Windows, a failed API call, and a zero-length list are indistinguishable to
a manifest and all fall through to `en`.

`CultureInfo` is **not** constructed anywhere — `new CultureInfo("uk-UA")`
throws `CultureNotFoundException` under this setting.

### 4.2 The chain

The chain produces an **ordered list of preference tags**, not a single tag:

| Source | Yields |
|---|---|
| `installer.language` (fixed) | `[tag]` |
| `/lang=<tag>` | `[tag]` |
| OS probe | the full ordered `GetUserPreferredUILanguages` list |
| fallback | `["en"]` |

Tags are **strings, not an enum** — the manifest side must carry tags Sigil
ships no chrome for.

The list matters only for the OS-probe case, and it is the reason to walk it:
a user whose OS preference is `[de-DE, uk-UA]` has *said* they read Ukrainian
better than English. Taking only the first entry would hand them English chrome
while discarding a signal they explicitly set. Walking the list is a dictionary
lookup per entry against a ≤2-entry table.

### 4.3 `system.language`

Because matching is per-surface (§4.4), "the resolved language" has more than
one answer: chrome may resolve `uk` while a manifest map resolves `de`.
`system.language` is therefore defined narrowly and unambiguously as **the
resolved chrome language** — the language Sigil's own UI renders, drawn from
the closed `Lang` set.

This is exactly D1's "what language is the UI in", and it does not overlap
`locale()`, which already answers the "is this user Ukrainian" question a
manifest would otherwise reach for. It lives in the existing `system.*`
namespace, so no new namespace is introduced.

Consequence, accepted: a manifest supplying `de` screens cannot observe that
its own screens resolved to `de` — `system.language` reports the chrome
language. The gap is marginal (an author gating on locale wants `locale()`) and
narrows as chrome languages are added.

### 4.4 Independent best-match — and why

Each surface matches the one resolved tag independently:

- **Chrome**: `Strings.Match(tag) → Lang`, `En` fallback.
- **Manifest maps**: best-match on the raw tag, `en` fallback.

So `/lang=de` against a manifest supplying a `de` map renders **German declared
screens with English chrome**. That mixed result is correct: each surface gives
the best it actually has.

The alternative — restricting resolution to the languages Sigil's chrome ships
— would make manifest-supplied translations useless for every language Sigil
does not itself ship, defeating the entire point of the feature.

Manifest-map `en` fallback is **total**, and that totality is exactly what the
missing-`en` pack diagnostic (§5.3) buys. The two are one mechanism.

### 4.5 Matching algorithm

Matching takes an **ordered preference list** (§4.2) and a surface's available
tag set, and returns the first hit. Ordinal only, no ICU:

```text
for each tag in preference list, in order:
    1. exact match, OrdinalIgnoreCase        (pt-BR matches pt-br)
    2. primary subtag                        (de-AT -> de)
    3. ordinal-first among candidates sharing a primary subtag
finally:
    4. en
```

Step 3 exists purely for determinism — it decides `de` → {`de-AT`, `de-CH`} the
same way on every machine and every pack.

Step 4 is reachable for chrome (which ships a closed set) and is **guaranteed
to succeed** for manifest maps, because `SIG0290` (§5.3) makes an `en`-less map
a pack-time error.

### 4.6 Where resolution runs

Resolution happens **once per session, at session start**, immediately after
the blob loads (it needs `installer.language`) and **before any UI is
constructed** — including `Program.cs`'s pre-Avalonia single-instance
`MessageBoxW`, which is itself a catalog string. The resolver depends on the
blob and Win32 only, never on Avalonia, so this ordering is available.

The resolved tag is then: stored on the session, exposed as `system.language`
to the expression context, used to set the static `S` accessor's language
(§3.2), and passed to the VMs and engine. Both entry points
(`Installer.Host` and the `Wrapper` console) resolve identically.

Once set, the language is **immutable for the session** — the property §3.2
depends on.

## 5. Data model

### 5.1 `LocalizedText`

A record in `SigilBuild.Core.Manifest` carrying the normalized map and nothing
else:

```csharp
public sealed record LocalizedText(IReadOnlyDictionary<string, string> Values)
{
    public static LocalizedText Plain(string value);   // -> { "en": value }
}
```

Picking a language is **not** a method on the record: `LocalizedText` is
manifest data shared with pack time, while matching (§4.5) belongs to
`Wrapper.Core/Localization` next to the resolver. Core carries the map;
Wrapper.Core resolves it.

### 5.2 Localizable fields

Plain strings **normalize at parse time** to `{"en": "..."}`, so the map is the
only shape that exists at runtime. No consumer branches on "string or map".

| Field | Today | After |
|---|---|---|
| `InstallerScreen.Title` | `string` | `LocalizedText` |
| `InstallerScreen.Subtitle` | `string?` | `LocalizedText?` |
| `ParameterDefinition.Description` | `string?` | `LocalizedText?` (renders as the field label) |
| `installer.license` | path | path **or** `{en: L.txt, uk: L.uk.txt}` — each file read at **pack time** into a `LicenseText` map |

`ScreenField` has no label of its own; field labels come via
`Param` → `ParameterDefinition.Description`. That is why `Description` is in
this table and `ScreenField` is untouched.

Option-component labels are host-side hardcoded (`SerializableOptionComponent`
carries no label). The four known components get catalog keys; an unknown
component still renders its raw manifest key. Making component labels
author-supplied is **P10's** job, not P9's.

### 5.3 Schema, blob, diagnostics

The blob carries plain `Dictionary<string,string>`, which is **already
registered** in `WrapperBlobJsonContext` — zero new `[JsonSerializable]`
entries. `SerializableWrapperBlob.ToWrapperBlob`/`FromWrapperBlob` must be
edited **symmetrically**.

Schema gains a `LocalizedText` definition, `oneOf [string, object]`, left
**permissive about `en`**. The parser diagnostic owns that rule instead: it
produces a better message than a `oneOf` "matches no subschema" error, and two
enforcement points for one rule would drift apart.

New codes (`SIG029x` — next free block):

| Code | Condition | Severity |
|---|---|---|
| `SIG0290` | A `LocalizedText` map lacks an `en` entry | **Error (fatal)** |
| `SIG0291` | `installer.language` or a map key is not a well-formed language tag | Error |

`SIG0290` is fatal for two reasons. Structurally, §4.4's runtime fallback
totality depends on `en` existing. More decisively: **pack diagnostics reach
manifest authors, who do not run under this repo's `TreatWarningsAsErrors`.**
`SIGLOC002` (§3.3) can afford to be a warning because repo CI backstops it;
`SIG0290` has no such backstop, so a warning here would genuinely ship blank
strings to end users.

#### License-map diagnostic ownership

`SIG0250` (T14, `LicenseFileUnreadable`) is explicitly **non-fatal** — the pack
succeeds and the License screen is omitted — while `SIG0290` is fatal. They
overlap on an unreadable `en` license file, so ownership is split by *failure
kind*, not by field:

| Failure | Owner | Severity |
|---|---|---|
| An entry's file is missing/unreadable/empty | `SIG0250` | Non-fatal — that entry drops |
| The **resulting** map is non-empty but lacks `en` | `SIG0290` | **Fatal** |
| The resulting map is empty (all entries dropped) | *neither* | Screen omitted — existing T14 behavior |

The invariant is asserted on the **post-read** map, which is what makes the
composite case correct: `{en: missing.txt, uk: ok.txt}` drops `en` via
`SIG0250`, leaving a uk-only map that `SIG0290` then rejects. Without ordering
it this way, that manifest would pack a license readable only by Ukrainian
users and render blank for everyone else — silently.

Records + schema + blob land in a **single owned pass** (M0 discipline).

## 6. CLI

### 6.1 `/lang=<tag>`

Prefix-form flag modelled exactly on `/D=` (`CommandLineParser.cs:396-408`).
Five edit sites: the `Lang` property on `ParsedCommandLine` (a `sealed class`
with `init` properties — *not* a record), `AuditSafeRendering()`, the local
`var`, the parse branch, and the object initializer. Plus the **two inline flag
enumerations** in the usage-error messages (`:341`, `:445`), which both list
the accepted flags and must both be updated.

No collision risk: `/launch` is a bare `string.Equals`, and the `/LOG` branch
tests `body[1] == 'O' || 'o'`, so `lang=` falls through cleanly.

Two failure modes, deliberately different:

- **Malformed** — `/lang=` with an empty value, or a value that is not a
  well-formed language tag (`/lang=!!`) — is a `UsageException` → **exit 64**,
  matching `/D=`'s treatment of an empty path.
- **Well-formed but unknown** — `/lang=zz`, or a real tag Sigil ships no chrome
  for (`/lang=de`) — is **not** an error. It falls through §4.5's match to
  `en` chrome, while a manifest map supplying that tag still resolves. Rejecting
  it would break §4.4: the author, not Sigil, decides which languages the
  declared screens support.

### 6.2 The tag validator — one implementation, two call sites

`SIG0291`'s "well-formed language tag" and the malformed-`/lang` check above are
**the same rule** and must not be written twice, or pack time and parse time
drift into accepting different tags.

The validator lives in **`SigilBuild.Core`**, which is viable because
`SigilBuild.Wrapper.Core` already references it
(`SigilBuild.Wrapper.Core.csproj:22`). Call sites: the manifest parser
(`SIG0291`) and `CommandLineParser` (`/lang`).

The accepted grammar is a deliberate **ordinal subset** of BCP-47, written down
here so it is a contract rather than an implementation detail:

```text
primary  = ALPHA{2,3}
subtag   = ALPHANUM{1,8}
tag      = primary ( "-" subtag )*
```

Matched `OrdinalIgnoreCase`. This accepts everything Sigil realistically needs
(`en`, `uk`, `pt-BR`, `zh-Hans`, `de-AT`) and rejects the malformed. Full BCP-47
— with its grandfathered tags, extensions, and private-use sequences — buys
nothing here and would need a parser the ADR's AOT constraints would rather not
carry.

### 6.3 `/?` help

New surface, **English** (D3). Routed in `Program.cs` **before** the closed
grammar — the same bypass `--version` already uses at `:12-16` — since the
parser would otherwise reject it. Lists every flag including `/lang`.

The `/lang` entry must **not** imply that the listed tags are the only legal
ones — chrome and manifest text are different surfaces (§4.4). Wording:

```text
/lang=<tag>   force the wizard language
              chrome ships in: en, uk
              manifest screens may supply any tag
```

## 7. Migration (~85 strings)

### 7.1 XAML (38 literals)

35 static literals become `Text="{x:Static l:S.NavBack}"` — compile-time
resolved by Avalonia's XAML compiler, no bindings, no `INotifyPropertyChanged`,
AOT-clean.

3 `StringFormat` compositions (`WelcomeView:8`, `FinishView:8`,
`UninstallWindow:24`) cannot use `{x:Static}` — they need an argument — so they
move to VM properties. These were already the awkward cases, with the format
string in XAML and the argument in a binding; this is a cleanup, not a
workaround.

Glyphs (`🔒`, `✓`, `•`, `••••••••`) are **not** catalog entries.

### 7.2 ViewModels and code-behind (41)

Direct `Strings.X(lang, ...)` calls. 17 are composed and take arguments. Folded
in, because they are the same defect:

- **Enum/id leaks**: `InstallerViewModel.cs:1028-1029` renders
  `node.Step.ToString()` and a raw `Screen.Id` into the rail. Both become keys.
- **Duplicate pairs collapse** to single keys: `"Browse…"`,
  `"Choose install location"`, `"Couldn't load options."`,
  `"Launch application"`, `"Application"`/`"Publisher"`.
- The 3-part concatenated downgrade notice (`:250-252`) becomes **one** key,
  not three — sentence fragments are not independently translatable.

### 7.3 Engine prose (6)

`InstallSession.LaunchLabel` (`:1066`) composes a UI string *in the engine* and
would silently pass a host-scoped test while remaining hardcoded. It moves to
the catalog. Also: downgrade-blocked message, files-in-use blocker,
`"Installing {prereq}…"`, `"Removing previous version"`, `/Update`-unsupported.

Everything else in `Steps/` and all `_log?.WriteLine` output stays English
(D2). The tell is mechanical and already consistent in the codebase:
lowercase-prefixed `"noun: detail"` lines are log convention; sentence-cased
lines are user-facing.

### 7.4 Out of the catalog

Log output, journal lines, developer exception messages, `[JsonPropertyName]`
values, widget-inference keys (`switch`/`radio`/`dropdown`), manifest
interpolation tokens (`{app.name}`), expression-context prefixes, resource keys,
`avares://` paths, and `ScreenSelector.cs:25`'s `"(no view)"`
unreachable-default.

## 8. Verification

### 8.1 Zero hardcoded UI strings — two mechanisms

Neither alone is honest, so both ship:

1. **Runtime pseudo-loc.** The generator emits a third `Lang.Pseudo`
   (§3.2) transformed from `en` (`Back` → `[Ƀàçķ‼]`). Headless-render every
   screen (`Avalonia.Headless` is already wired), walk the visual tree, assert
   every text run is bracketed. Anything plain-ASCII is hardcoded.
2. **Static XAML scan.** No literal containing a letter survives in `Views/`
   for any of: `Text=`, `Content=`, `Watermark=`, `ToolTip.Tip=`,
   `AutomationProperties.Name=`, `AutomationProperties.HelpText=`.

The scan exists because the render pass can only assert screens it can
**reach** — `Failed`, `CloseApps`, and `DowngradeBlocked` each need specific
state to instantiate. The two accessibility attributes are in the scan for a
second reason: they are user-facing chrome that the render walk would **not**
catch, since a visual-tree text sweep never reads them.

Those attributes are **preventive only**. A sweep found **zero** occurrences of
either in any `.axaml` — the host currently ships no screen-reader names at
all, and `AccessibilityTests.cs` covers keyboard navigation only. That is a
real accessibility gap, but authoring screen-reader names is accessibility
work, not localization; P9 adds the scan rule so the gap cannot be *widened* in
an untranslatable way, and leaves closing it to its own lane.

The pseudo-loc allowlist (glyphs, brand data, user-entered values, English step
detail) is explicit, and **a test asserts the allowlist's own size**, so it
cannot quietly grow into a loophole.

### 8.2 Behavioural tests

| Test | Asserts |
|---|---|
| uk fixture, headed | Ukrainian chrome **and** declared screens |
| **de fixture, no de chrome** | German declared screens + **English** chrome — proves §4.4 independent best-match |
| **OS list `[de-DE, uk-UA]`, chrome en+uk** | Chrome resolves **uk**, not en — proves §4.2 list-walk uses the later preference. First-only would fail this |
| `system.language` under that same list | Reports `uk` (the chrome language), while `locale()` reports `de-DE` — proves §4.3's split |
| `locale()` API failure / non-Windows / empty list | Returns `""`, chain falls to `en`; function stays total (ADR-008 §1.2) |
| `installer.language: en` + `/lang=uk` | English UI, log line records the ignored flag, exit 0 |
| Missing-`en` map | `SIG0290` at pack time, **fatal** |
| **License `{en: missing.txt, uk: ok.txt}`** | `SIG0250` drops `en`, then `SIG0290` rejects the uk-only map — proves §5.3's ordering |
| **License, all entries unreadable** | `SIG0250` only; screen omitted; **no** `SIG0290` — preserves T14 behavior |
| **Tag validator, one rule** | The same malformed tag is rejected by *both* `SIG0291` (pack) and `/lang` (exit 64); a shared-implementation test pins them together |
| Blob round-trip | Localized fields survive, both map and normalized-plain forms |
| `/lang=uk /silent` | **Byte-identical** install outcome; exit codes unchanged; log still English |
| **`S` read before init** | Throws in Debug — the reorder guard in §3.2 actually fires |
| Generator placeholder mismatch | Build error (`SIGLOC003`) |
| `/lang=pseudo`, `/lang=qps` | Exit 64 (grammar) and `en` fallback respectively — `Lang.Pseudo` unreachable at runtime (§3.2) |
| AOT publish | Warning-free (`[LibraryImport]` for `GetUserPreferredUILanguages`) |
| Size gates | Re-measured and re-pinned consciously — P13 anticipates globalization adding weight |

## 9. ADR-008 amendments

Three edits to `docs/architecture/adr-008-expression-policy.md`:

1. **§1.1** — `locale()`'s Nature column: `CurrentUICulture.Name` →
   `GetUserPreferredUILanguages`, no longer `""` under InvariantGlobalization.
2. **§4** — replace the "5–10 languages" **number** with a **standing rule**:

   > A language ships only with a named reviewer recorded in its catalog file's
   > provenance header.

   This is D4 generalized. Phrasing the amendment as a criterion rather than an
   exception means the remaining nine languages become **content PRs admitted
   under a standing rule**, not nine future deviations each needing its own
   argument. The rule also states the property the original number was only
   proxying for: what protects users is review, not count.
3. **Amendment log** — one dated row covering both, per the ADR's own rule
   ("append one row per future function/step/localization change that widens
   the surface. Never rewrite prior rows.").

The `locale()` row must record a **behavior change, not just a source change**:
a `When` using `locale()` moves from an always-`""` result to a real tag, which
can flip conditions. Risk is ~zero because the function was useless, but the
amendment log is exactly where a claim like that belongs in writing.

`SigilBuild.Wrapper.csproj:19-27`'s comment — the "revisit this together with
ADR-008" pointer — gets updated to state that localization has landed *without*
relaxing `InvariantGlobalization`, which is precisely what that comment
anticipated.

## 10. Deviations and known limitations

Recorded rather than quietly absorbed:

| Item | Contract it departs from | Why |
|---|---|---|
| en + uk, not 5–10 languages | ADR-008 §4 | D4 — nothing unreviewed ships. §9 amends the ADR to a reviewer rule, which subsumes this |
| `locale()` reads the OS, not `CurrentUICulture` | ADR-008 §1.1 | §4.1 — the old source cannot work |
| `/?` help stays English | Task scope ("every CLI-help string moves to a key") | D3 — console is the support surface |
| `ToolTip.Tip`/`AutomationProperties.*` scanned but not catalogued | Task scope implies catalogue | §8.1 — zero exist; there is nothing to move. Scan is preventive |

Known limitations, to be documented in user-facing docs:

- **No RTL layout.** A manifest *may* supply `ar`/`he` maps; the text renders,
  the layout does not mirror.
- **Log stays English**, for supportability.
- **No language-selection dialog.** Resolution is automatic or flag-driven,
  which is what makes the session language immutable and `{x:Static}` (§3.2)
  legal.
- **Number and date rendering stays invariant.**

## 11. Out of scope

RTL layout work; a language-selection dialog; translated log output; structured
step error codes (D2); author-supplied option-component labels (P10); the
remaining nine seed languages (content follow-up, mechanism-complete).
