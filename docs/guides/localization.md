# Localization

Sigil-produced installers can present a localized wizard. This is opt-in and
additive: an English-only manifest behaves exactly as before.

Two independent things get localized:

1. **Built-in wizard chrome** — button labels, screen titles, the CloseApps /
   DowngradeBlocked / Failed screens, and similar strings Sigil itself owns.
   These come from a compiled-in string catalog (source-generated at build
   time from `Localization/Strings.*.txt`), never `.resx` satellite
   assemblies — see [ADR-008 §4](../architecture/adr-008-expression-policy.md#4-localization-stance-enables-p9).
2. **Manifest-supplied text** — `title` / `subtitle` on `installer.screens`,
   parameter `description`, and `installer.license` — anything the author
   writes. These use the `LocalizedText` shape (below) and are localized
   independently of the chrome.

## The `LocalizedText` shape

Any localizable field accepts either a plain string or a map of language tag
to text:

```yaml
installer:
  license: ./LICENSE.txt   # plain string == English only

  screens:
    - id: server-settings
      title:
        en: "Server settings"
        uk: "Налаштування сервера"
      subtitle: "Configure the connection"   # plain string is also legal here
```

Rules:

- A plain string is treated as `{ en: <string> }`.
- A map **must** include an `en` entry. A map with no `en` key is a fatal
  pack-time error (**`SIG0290`**) — every runtime fallback bottoms out at
  English, so an `en`-less map has no safe rendering and is rejected rather
  than shipped half-blank. This applies to every `LocalizedText` field,
  including the composite case where a license is itself a map of per-language
  file paths and every path in the map turns out unreadable except one
  language — the *resulting* map still needs an `en` entry to pass.
- A map key that is not a well-formed language tag is a fatal pack-time error
  (**`SIG0291`**). The same tag-validation rule governs `installer.language`
  and the `/lang` CLI flag (one shared implementation, so pack-time and
  install-time never disagree about what a valid tag looks like).

## `installer.language`

```yaml
installer:
  language: uk
```

An optional fixed installer language. When set (and valid), it wins outright
— no OS detection, no `/lang` override. Omit it to let the installer
auto-detect. An invalid tag is diagnosed at pack time (`SIG0291`).

## Language resolution chain

The session's language is resolved once, in this order, and is **immutable**
for the rest of the run (there is no in-wizard language switcher — see
Known limitations):

1. `installer.language` (manifest, fixed)
2. `/lang=<tag>` (CLI flag)
3. The OS's ordered UI-language preference list (`GetUserPreferredUILanguages`)
4. `en` (final fallback)

Each declared surface (chrome, and every manifest `LocalizedText` map)
resolves **independently** against this same preference list via ordinal
best-match — an exact tag match, then a primary-subtag match (`de-CH` matches
a catalog entry for `de`), then a deterministic ordinal tie-break among
same-primary candidates. There is no shared "the installer is now in German"
state; a manifest that ships German screens but no German chrome renders
English chrome alongside the German screens, by design.

## CLI

```
/lang=<tag>        force the wizard language
                   chrome ships in: en, uk
                   manifest screens may supply any tag
/?, /help          show usage and exit
```

`/lang` is silently ignored if `installer.language` is set (the manifest
wins). An invalid `/lang` value is a usage error (exit code 64). Both flags
are documented in full in [`cli-reference.md`](../cli-reference.md)'s
Setup.exe section.

`/lang=uk /silent` behaves identically to `/silent` in every way *except*
which language renders — the same steps run, in the same order, with the
same effect on disk. Silent installs are otherwise unaffected by language.

## Known limitations

These are recorded design boundaries, not bugs waiting on a fix:

- **No RTL layout.** A manifest *may* supply `ar` / `he` entries in a
  `LocalizedText` map — the text renders — but the wizard's layout does not
  mirror for right-to-left scripts.
- **Log and console output stay English**, for supportability: an admin
  pasting a log line into a support ticket should get something any Sigil
  maintainer can read. Engine console/silent-mode messages that name a CLI
  flag (e.g. `/closeapps`, `/force-downgrade`) are English by design — their
  user-facing twins are the localized wizard **screens** (`CloseApps`,
  `DowngradeBlocked`), which do get the full chrome treatment.
- **A narrow TOCTOU edge**: if a blocking file re-locks between the CloseApps
  screen's pre-check and the engine actually executing the step, the English
  files-in-use blocker line can surface on the otherwise-localized `Failed`
  screen. This is a cosmetic, low-probability race, not a functional gap.
- **No in-wizard language-selection dialog.** Resolution is automatic
  (manifest → `/lang` → OS → `en`) or flag-driven; once resolved, the session
  language is immutable for the rest of the run.
- **Number and date rendering stays invariant** — the wrapper keeps
  `InvariantGlobalization=true` (required for the AOT size/behavior budget),
  so numeric and date formatting is always culture-invariant regardless of
  the wizard's display language.
- **Chrome ships in English and Ukrainian only.** Declared manifest screens
  may supply any language tag an author is willing to review and translate;
  adding a new *chrome* language is a reviewed content contribution to the
  catalog (see [ADR-008 §4](../architecture/adr-008-expression-policy.md#4-localization-stance-enables-p9)), not a code change.

## See also

- [ADR-008 §4 — localization stance](../architecture/adr-008-expression-policy.md#4-localization-stance-enables-p9)
- [Manifest reference](../manifest-reference.md)
- [CLI reference](../cli-reference.md)
