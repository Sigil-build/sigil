# P9 Localization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Localize the Sigil wizard's chrome and manifest-supplied screen text into English and Ukrainian without relaxing `InvariantGlobalization=true`.

**Architecture:** A Roslyn incremental generator turns `Strings.<lang>.txt` catalog files into a compiled-in string table with typed, named-placeholder accessors that emit per-language concatenation (never `string.Format`, so no `CultureInfo` is ever touched). A language resolver picks an ordered preference list from `installer.language` → `/lang` → the OS UI-language list → `en`; chrome and manifest-supplied text then match against that list independently. Declared screen text becomes a `LocalizedText` map carried through records → schema → blob.

**Tech Stack:** .NET 10, C#, Roslyn incremental source generators (netstandard2.0), Avalonia 11 + Avalonia.Headless, xUnit + FluentAssertions, Native AOT (`PublishAot`), `[LibraryImport]` P/Invoke.

**Design spec:** `docs/plan/feature-parity/P9-DESIGN-localization.md` — read it in full before Task 1. Where this plan and the spec disagree, **the spec wins**; stop and report rather than reconciling silently.

## Global Constraints

- **Branch:** `task/p9-localization`. Never merge; finish with a pushed branch and a summary.
- **`InvariantGlobalization=true` stays.** Never construct `CultureInfo` — `new CultureInfo("uk-UA")` throws `CultureNotFoundException` at runtime.
- **Native AOT + `TreatWarningsAsErrors`.** Any `IL2xxx`/`IL3xxx` is a build failure. No reflection, no `Activator`, no `Assembly.*`.
- **Source-generated serialization only.** New serializable types go in a `JsonSerializerContext`.
- **Central package management.** Versions live in `Directory.Packages.props`; a `PackageReference` carries **no** `Version=` attribute. Add the version there if it is missing, do not inline it.
- **The solution file is `Sigil.slnx`** (the XML format), not `Sigil.sln`. `dotnet sln Sigil.slnx add …` works normally.
- **P/Invoke uses `[LibraryImport]`**, never `[DllImport]`.
- **Deterministic packaging output.** Two packs of one input are byte-identical.
- **Log output stays English.** Never route `_log?.WriteLine` or journal text through the catalog.
- **Match `.editorconfig`.** Every behavior change lands with tests in the matching `tests/` project.
- **Definition of done:** `dotnet build Sigil.slnx -c Release` clean and `dotnet test Sigil.slnx -c Release` green, plus each task's stated verification.
- **Do not touch** files owned by other in-flight lanes. If you believe you must, stop and report.

## File Structure

| Path | Responsibility |
|---|---|
| `src/SigilBuild.Localization.Generator/` | **New.** netstandard2.0 Roslyn incremental generator. Analyzer-only, never shipped. |
| `src/SigilBuild.Localization.Generator/CatalogParser.cs` | Parse `key = value` lines + named placeholders into a model. Pure, unit-testable. |
| `src/SigilBuild.Localization.Generator/StringsGenerator.cs` | `IIncrementalGenerator`: model → `Lang` enum, `Strings`, `S`. |
| `src/SigilBuild.Localization.Generator/CatalogDiagnostics.cs` | `SIGLOC001`–`SIGLOC005` descriptors. |
| `src/SigilBuild.Localization.Generator/PseudoTransform.cs` | `en` → `Lang.Pseudo` text transform. |
| `src/SigilBuild.Wrapper.Core/Localization/Strings.en.txt` | **The** source of truth for every UI string. |
| `src/SigilBuild.Wrapper.Core/Localization/Strings.uk.txt` | Ukrainian, with a provenance header naming its reviewer. |
| `src/SigilBuild.Wrapper.Core/Localization/LanguageResolver.cs` | The chain (§4.2), matching (§4.5), `system.language` (§4.3). |
| `src/SigilBuild.Wrapper.Core/Localization/SessionLanguage.cs` | Static session language + the pre-init guard backing `S`. |
| `src/SigilBuild.Core/Manifest/LocalizedText.cs` | The normalized `{tag → text}` map record. |
| `src/SigilBuild.Core/Manifest/LanguageTag.cs` | The one tag validator, two call sites (§6.2). |

---

### Task 1: Generator project + minimal en catalog

**Files:**
- Create: `src/SigilBuild.Localization.Generator/SigilBuild.Localization.Generator.csproj`
- Create: `src/SigilBuild.Localization.Generator/CatalogParser.cs`
- Create: `src/SigilBuild.Localization.Generator/StringsGenerator.cs`
- Create: `tests/SigilBuild.Localization.Generator.Tests/SigilBuild.Localization.Generator.Tests.csproj`
- Create: `tests/SigilBuild.Localization.Generator.Tests/CatalogParserTests.cs`
- Modify: `Sigil.slnx`

**Interfaces:**
- Produces: `CatalogParser.Parse(string fileName, string text) -> CatalogFile`, where `CatalogFile` has `string Lang` and `IReadOnlyList<CatalogEntry> Entries`; `CatalogEntry` has `string Key`, `string Value`, `IReadOnlyList<string> Placeholders`, `int Line`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SigilBuild.Localization.Generator.Tests/CatalogParserTests.cs
using FluentAssertions;
using SigilBuild.Localization.Generator;
using Xunit;

namespace SigilBuild.Localization.Generator.Tests;

public class CatalogParserTests
{
    [Fact]
    public void Parse_ReadsKeyValuePairs_AndSkipsCommentsAndBlanks()
    {
        var text = "# provenance header\n\nnav.back = Back\nnav.next = Next\n";

        var file = CatalogParser.Parse("Strings.en.txt", text);

        file.Lang.Should().Be("en");
        file.Entries.Should().HaveCount(2);
        file.Entries[0].Key.Should().Be("nav.back");
        file.Entries[0].Value.Should().Be("Back");
        file.Entries[1].Key.Should().Be("nav.next");
    }

    [Fact]
    public void Parse_ExtractsNamedPlaceholders_InOrder()
    {
        var text = "upgrading = Upgrading {appName} from {fromVersion} to {toVersion}.\n";

        var file = CatalogParser.Parse("Strings.en.txt", text);

        file.Entries[0].Placeholders.Should().Equal("appName", "fromVersion", "toVersion");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SigilBuild.Localization.Generator.Tests -c Release`
Expected: FAIL — `CatalogParser` does not exist (CS0246).

- [ ] **Step 3: Create the generator project**

```xml
<!-- src/SigilBuild.Localization.Generator/SigilBuild.Localization.Generator.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- netstandard2.0 is mandatory for Roslyn analyzers/generators: the compiler
         loads them into its own process. This is the pattern anticipated by
         SigilBuild.Installer.BrandGenerator.csproj's comment. -->
    <TargetFramework>netstandard2.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <IsRoslynComponent>true</IsRoslynComponent>
    <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <!-- No Version= : the repo uses central package management. Versions are
         already pinned in Directory.Packages.props (CodeAnalysis.CSharp 4.11.0,
         CodeAnalysis.Analyzers 3.11.0). -->
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" PrivateAssets="all" />
    <PackageReference Include="Microsoft.CodeAnalysis.Analyzers" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

Add to `Sigil.slnx` (both the generator and its test project) via:

```bash
dotnet sln Sigil.slnx add src/SigilBuild.Localization.Generator/SigilBuild.Localization.Generator.csproj
dotnet sln Sigil.slnx add tests/SigilBuild.Localization.Generator.Tests/SigilBuild.Localization.Generator.Tests.csproj
```

- [ ] **Step 4: Write the parser**

```csharp
// src/SigilBuild.Localization.Generator/CatalogParser.cs
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SigilBuild.Localization.Generator;

internal sealed class CatalogEntry
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public IReadOnlyList<string> Placeholders { get; set; } = Array.Empty<string>();
    public int Line { get; set; }
}

internal sealed class CatalogFile
{
    public string Lang { get; set; } = string.Empty;
    public IReadOnlyList<CatalogEntry> Entries { get; set; } = Array.Empty<CatalogEntry>();
    public IReadOnlyList<(string Message, int Line)> Malformed { get; set; }
        = Array.Empty<(string, int)>();
}

internal static class CatalogParser
{
    private static readonly Regex PlaceholderPattern = new(@"\{([A-Za-z][A-Za-z0-9]*)\}", RegexOptions.Compiled);
    private static readonly Regex FileNamePattern = new(@"^Strings\.([A-Za-z0-9-]+)\.txt$", RegexOptions.Compiled);

    public static CatalogFile Parse(string fileName, string text)
    {
        var langMatch = FileNamePattern.Match(fileName);
        var entries = new List<CatalogEntry>();
        var malformed = new List<(string, int)>();

        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                malformed.Add(($"expected 'key = value', got '{line}'", i + 1));
                continue;
            }

            var key = line.Substring(0, eq).Trim();
            var value = line.Substring(eq + 1).Trim();
            if (key.Length == 0)
            {
                malformed.Add(("key is empty", i + 1));
                continue;
            }

            var names = new List<string>();
            foreach (Match m in PlaceholderPattern.Matches(value))
            {
                names.Add(m.Groups[1].Value);
            }

            entries.Add(new CatalogEntry { Key = key, Value = value, Placeholders = names, Line = i + 1 });
        }

        return new CatalogFile
        {
            Lang = langMatch.Success ? langMatch.Groups[1].Value : string.Empty,
            Entries = entries,
            Malformed = malformed,
        };
    }
}
```

Make internals visible to the test project by adding to the generator csproj:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="SigilBuild.Localization.Generator.Tests" />
  </ItemGroup>
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/SigilBuild.Localization.Generator.Tests -c Release`
Expected: PASS, 2 tests.

- [ ] **Step 6: Commit**

```bash
git add src/SigilBuild.Localization.Generator tests/SigilBuild.Localization.Generator.Tests Sigil.slnx
git commit -m "feat(p9): catalog parser + generator project skeleton"
```

---

### Task 2: Emit the Lang enum and Strings accessors

**Files:**
- Create: `src/SigilBuild.Localization.Generator/StringsEmitter.cs`
- (StringsGenerator.cs is wired in Task 3, not here — leave it alone)
- Create: `tests/SigilBuild.Localization.Generator.Tests/StringsEmitterTests.cs`

**Interfaces:**
- Consumes: `CatalogParser.Parse` (Task 1).
- Produces: `StringsEmitter.Emit(IReadOnlyList<CatalogFile> files) -> string` (the generated C# source). Generated API:
  - `public enum Lang { En, Uk, Pseudo }`
  - `public static class Strings` — one method per key: `Strings.NavBack(Lang lang)`, `Strings.Upgrading(Lang lang, string appName, string fromVersion, string toVersion)`.
  - `public static class ChromeCatalog` — `string[] Tags` and `Lang FromTag(string)`, both **emitted from the catalog filenames**. `LanguageResolver` (Task 7) consumes these rather than hardcoding a list, so adding `Strings.de.txt` wires a language end-to-end with no code edit — what ADR-008 §4's "content contributions" rule requires.
  - `public static class S` — one **property** per *argless* key, resolved against `SessionLanguage.Current` (Task 4), for XAML `{x:Static}`: `S.NavBack`. Keys with placeholders get no `S` property; they need an argument, so they go through a ViewModel property instead (design §7.1).
  - Method names are the key's dot/underscore segments PascalCased and concatenated: `nav.back` → `NavBack`, `location.error.not_absolute` → `LocationErrorNotAbsolute`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SigilBuild.Localization.Generator.Tests/StringsEmitterTests.cs
using FluentAssertions;
using SigilBuild.Localization.Generator;
using Xunit;

namespace SigilBuild.Localization.Generator.Tests;

public class StringsEmitterTests
{
    private static string EmitFor(string enText, string ukText)
    {
        var files = new[]
        {
            CatalogParser.Parse("Strings.en.txt", enText),
            CatalogParser.Parse("Strings.uk.txt", ukText),
        };
        return StringsEmitter.Emit(files);
    }

    [Fact]
    public void Emit_ArglessKey_ProducesSwitchOverLang()
    {
        var src = EmitFor("nav.back = Back\n", "nav.back = Назад\n");

        src.Should().Contain("public static string NavBack(Lang lang)");
        src.Should().Contain("Lang.Uk => \"Назад\"");
        src.Should().Contain("_ => \"Back\"");
    }

    [Fact]
    public void Emit_NamedPlaceholders_BecomeTypedParametersAndConcatenation()
    {
        var src = EmitFor(
            "upgrading = Upgrading {appName} to {toVersion}.\n",
            "upgrading = Оновлення {appName} до {toVersion}.\n");

        src.Should().Contain("public static string Upgrading(Lang lang, string appName, string toVersion)");
        src.Should().Contain("\"Upgrading \" + appName + \" to \" + toVersion + \".\"");
        // No string.Format anywhere: formatting must never touch CultureInfo.
        src.Should().NotContain("string.Format");
    }

    [Fact]
    public void Emit_TranslationMayReorderPlaceholders()
    {
        // Word order differs; the uk expression must reflect its own order.
        var src = EmitFor(
            "greet = Hello {first} {last}\n",
            "greet = Вітаю {last} {first}\n");

        src.Should().Contain("\"Вітаю \" + last + \" \" + first");
    }

    [Fact]
    public void Emit_KeyMissingFromTranslation_FallsBackToEnglish()
    {
        var src = EmitFor("nav.back = Back\nnav.next = Next\n", "nav.back = Назад\n");

        src.Should().Contain("public static string NavNext(Lang lang)");
        // NavNext has no Lang.Uk arm — it lands on the `_ =>` English default.
        src.Should().NotContain("Lang.Uk => \"Next\"");
    }

    // ChromeCatalog is emitted from the catalog files so LanguageResolver never
    // hardcodes a language list (ADR-008 §4: languages ship as content).
    [Fact]
    public void Emit_ChromeCatalog_ListsEveryCatalogTag()
    {
        var src = EmitFor("nav.back = Back\n", "nav.back = Назад\n");

        src.Should().Contain("public static readonly string[] Tags = { \"en\", \"uk\" };");
        src.Should().Contain("\"uk\" => Lang.Uk,");
    }

    // The regression this design prevents: a third catalog wires itself with no
    // code edit. If this fails, adding a language silently half-works.
    [Fact]
    public void Emit_ThirdLanguage_AppearsInChromeCatalog_WithNoCodeChange()
    {
        var files = new[]
        {
            CatalogParser.Parse("Strings.en.txt", "nav.back = Back\n"),
            CatalogParser.Parse("Strings.uk.txt", "nav.back = Назад\n"),
            CatalogParser.Parse("Strings.de.txt", "nav.back = Zurück\n"),
        };

        var src = StringsEmitter.Emit(files);

        src.Should().Contain("public enum Lang { En, De, Uk, Pseudo }");
        src.Should().Contain("public static readonly string[] Tags = { \"en\", \"de\", \"uk\" };");
        src.Should().Contain("\"de\" => Lang.De,");
    }
}
```

Note the ordering in the last test: non-`en` languages are emitted in **ordinal** order (`de` before `uk`), because `Emit` sorts them — determinism matters, generated output must not depend on file-enumeration order. `en` always leads.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SigilBuild.Localization.Generator.Tests -c Release --filter StringsEmitterTests`
Expected: FAIL — `StringsEmitter` does not exist (CS0246).

- [ ] **Step 3: Write the emitter**

```csharp
// src/SigilBuild.Localization.Generator/StringsEmitter.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SigilBuild.Localization.Generator;

internal static class StringsEmitter
{
    public static string Emit(IReadOnlyList<CatalogFile> files)
    {
        var en = files.FirstOrDefault(f => f.Lang == "en");
        if (en is null)
        {
            return "// no Strings.en.txt found\n";
        }

        var langs = files.Select(f => f.Lang).Where(l => l != "en").OrderBy(l => l, StringComparer.Ordinal).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> Generated by SigilBuild.Localization.Generator. Do not edit.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("namespace SigilBuild.Wrapper.Core.Localization;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>Chrome languages Sigil ships. Closed set; see ADR-008 §4.</summary>");
        sb.Append("public enum Lang { En");
        foreach (var l in langs)
        {
            sb.Append(", ").Append(Pascal(l));
        }
        sb.AppendLine(", Pseudo }");
        sb.AppendLine();
        sb.AppendLine("public static class Strings");
        sb.AppendLine("{");

        foreach (var entry in en.Entries)
        {
            var method = MethodName(entry.Key);
            var args = entry.Placeholders;
            var paramList = args.Count == 0
                ? "Lang lang"
                : "Lang lang, " + string.Join(", ", args.Select(a => "string " + a));

            sb.AppendLine($"    public static string {method}({paramList}) => lang switch");
            sb.AppendLine("    {");

            foreach (var file in files.Where(f => f.Lang != "en").OrderBy(f => f.Lang, StringComparer.Ordinal))
            {
                var translated = file.Entries.FirstOrDefault(e => e.Key == entry.Key);
                if (translated is null)
                {
                    continue; // SIGLOC002 warns; falls through to the English default.
                }
                sb.AppendLine($"        Lang.{Pascal(file.Lang)} => {Expression(translated.Value)},");
            }

            sb.AppendLine($"        Lang.Pseudo => {Expression(PseudoTransform.Apply(entry.Value))},");
            sb.AppendLine($"        _ => {Expression(entry.Value)},");
            sb.AppendLine("    };");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        sb.AppendLine();

        // The tag map, emitted from the catalog files themselves. LanguageResolver
        // consumes this instead of hardcoding a list, so adding Strings.de.txt wires
        // the language end-to-end with no code edit — which is what ADR-008 §4's
        // "languages ship as content contributions" rule actually requires.
        sb.AppendLine("/// <summary>Chrome language tags, emitted from the catalog files. Never hand-edit.</summary>");
        sb.AppendLine("public static class ChromeCatalog");
        sb.AppendLine("{");
        sb.Append("    public static readonly string[] Tags = { \"en\"");
        foreach (var l in langs)
        {
            sb.Append(", \"").Append(l).Append('"');
        }
        sb.AppendLine(" };");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Maps a catalog tag to its Lang. Unknown tags fall back to English.</summary>");
        sb.AppendLine("    public static Lang FromTag(string tag) => tag switch");
        sb.AppendLine("    {");
        foreach (var l in langs)
        {
            sb.AppendLine($"        \"{l}\" => Lang.{Pascal(l)},");
        }
        sb.AppendLine("        _ => Lang.En,");
        sb.AppendLine("    };");
        sb.AppendLine("}");
        sb.AppendLine();

        // `S` — session-resolved static accessors for XAML {x:Static}. Only argless
        // keys get one: a key with placeholders needs an argument, so it must go
        // through a ViewModel property instead (design §7.1).
        sb.AppendLine("/// <summary>Session-resolved chrome strings for XAML <c>{x:Static}</c>.</summary>");
        sb.AppendLine("public static class S");
        sb.AppendLine("{");
        foreach (var entry in en.Entries.Where(e => e.Placeholders.Count == 0))
        {
            var method = MethodName(entry.Key);
            sb.AppendLine($"    public static string {method} => Strings.{method}(SessionLanguage.Current);");
        }
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// Turns "Upgrading {appName} to {v}." into: "Upgrading " + appName + " to " + v + "."
    /// Concatenation, never string.Format — no CultureInfo is involved at any point.
    /// </summary>
    private static string Expression(string value)
    {
        var parts = new List<string>();
        var literal = new StringBuilder();

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '{')
            {
                var close = value.IndexOf('}', i);
                if (close > i)
                {
                    var name = value.Substring(i + 1, close - i - 1);
                    if (name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_'))
                    {
                        if (literal.Length > 0)
                        {
                            parts.Add(Quote(literal.ToString()));
                            literal.Clear();
                        }
                        parts.Add(name);
                        i = close;
                        continue;
                    }
                }
            }
            literal.Append(value[i]);
        }

        if (literal.Length > 0)
        {
            parts.Add(Quote(literal.ToString()));
        }

        return parts.Count == 0 ? "\"\"" : string.Join(" + ", parts);
    }

    private static string Quote(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";

    private static string MethodName(string key) =>
        string.Concat(key.Split('.', '_').Select(Pascal));

    private static string Pascal(string s)
    {
        if (s.Length == 0) return s;
        var cleaned = s.Replace("-", string.Empty);
        return char.ToUpper(cleaned[0], CultureInfo.InvariantCulture) + cleaned.Substring(1);
    }
}
```

```csharp
// src/SigilBuild.Localization.Generator/PseudoTransform.cs
using System.Collections.Generic;
using System.Text;

namespace SigilBuild.Localization.Generator;

/// <summary>
/// Transforms an English value into its Lang.Pseudo form: accented letters plus
/// bracket sentinels. The pseudo-loc test asserts every rendered UI string is
/// bracketed, so any plain-ASCII text on screen is a hardcoded string.
/// Placeholders are copied through untouched — they are code, not text.
/// </summary>
internal static class PseudoTransform
{
    private static readonly Dictionary<char, char> Map = new()
    {
        ['a'] = 'à', ['b'] = 'Ƀ', ['c'] = 'ç', ['d'] = 'ď', ['e'] = 'è', ['g'] = 'ģ',
        ['i'] = 'ï', ['k'] = 'ķ', ['l'] = 'ĺ', ['n'] = 'ñ', ['o'] = 'ò', ['s'] = 'š',
        ['t'] = 'ť', ['u'] = 'ü', ['y'] = 'ý', ['z'] = 'ž',
        ['A'] = 'À', ['C'] = 'Ç', ['E'] = 'È', ['I'] = 'Ï', ['N'] = 'Ñ', ['O'] = 'Ò',
        ['S'] = 'Š', ['U'] = 'Ü',
    };

    public static string Apply(string value)
    {
        var sb = new StringBuilder("[");
        var inPlaceholder = false;

        foreach (var c in value)
        {
            if (c == '{') inPlaceholder = true;
            if (c == '}') { inPlaceholder = false; sb.Append(c); continue; }

            sb.Append(!inPlaceholder && Map.TryGetValue(c, out var mapped) ? mapped : c);
        }

        sb.Append("‼]");
        return sb.ToString();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SigilBuild.Localization.Generator.Tests -c Release --filter StringsEmitterTests`
Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add src/SigilBuild.Localization.Generator tests/SigilBuild.Localization.Generator.Tests
git commit -m "feat(p9): emit Lang enum + Strings accessors with per-language concatenation"
```

---

### Task 3: Generator diagnostics SIGLOC001–005

**Files:**
- Create: `src/SigilBuild.Localization.Generator/CatalogDiagnostics.cs`
- Modify: `src/SigilBuild.Localization.Generator/StringsGenerator.cs` (wire the incremental generator here — Task 2 deliberately left it a skeleton)
- Create: `tests/SigilBuild.Localization.Generator.Tests/CatalogDiagnosticsTests.cs`

**Interfaces:**
- Produces: `CatalogValidator.Validate(IReadOnlyList<CatalogFile>) -> IReadOnlyList<CatalogProblem>`, where `CatalogProblem` has `string Id`, `string Message`, `string File`, `int Line`.

Per spec §3.3, extended by the Task 2 review (see below):

| Id | Condition | Severity |
|---|---|---|
| `SIGLOC001` | Key in a translation that `en` lacks | Error |
| `SIGLOC002` | Key in `en` missing from a translation | Warning |
| `SIGLOC003` | Placeholder **set** differs between `en` and a translation | Error |
| `SIGLOC004` | Duplicate key within one file | Error |
| `SIGLOC005` | Malformed line | Error |
| `SIGLOC006` | Two distinct keys collide on one generated method name | Error |
| `SIGLOC007` | A placeholder is named for a C# keyword | Error |

#### Emission suppression — the condition the Task 2 review attached

**If any `Error`-severity problem is found, report the diagnostics and emit
NOTHING** (skip `AddSource`, or emit an empty compilation unit).

This is not cosmetic. Every Error above corresponds to generated code that
would *also* fail to compile with an opaque error pointing at generated source:
`SIGLOC004`/`SIGLOC006` → `CS0111` (duplicate member), `SIGLOC007` → `CS1041`
(identifier expected, keyword given). If the generator reports the useful
diagnostic *and then emits anyway*, the author sees both — and the opaque one
is louder. Suppressing emission means the catalog diagnostic is the only thing
they read.

`SIGLOC002` is a Warning and must **not** suppress emission: a partial
translation is legal and falls back to English by design.

#### SIGLOC006 — why it exists

`MethodName` splits keys on `.` **and** `_`, so `location.error.notAbsolute`
and `location.error.not_absolute` both produce `LocationErrorNotAbsolute`. The
emitter (Task 2) blindly writes both methods; nothing catches it. The emitter
cannot own this check — it returns a `string` and has no diagnostic sink, so it
could only throw (crashing the compiler) or silently dedupe (hiding an
authoring error). It belongs here.

Detect the collision on the **generated method name**, not on the key: that is
the property that actually breaks.

#### The `eq < 0` decision Task 1 deferred to you

`CatalogParser`'s `key.Length == 0` / `"key is empty"` branch is currently
**unreachable**. The guard `eq <= 0` conflates two cases: `eq == -1` (no `=` at
all) and `eq == 0` (`=` at position 0 — an empty key). The line-level `Trim()`
means `" = value"` becomes `"= value"`, so `eq == 0` and the empty-key branch
never fires.

The code is not dead; its guard is over-broad by one value. You own `SIGLOC005`'s
message semantics, so you decide:

- **Narrow to `eq < 0`** — the branch becomes reachable and `" = value"` gets the
  better message (`"key is empty"` rather than `"expected 'key = value', got
  '= value'"`). Preferred, unless you find a reason against it.
- **Or delete the branch** if you judge one message sufficient.

Either way, the existing test `Parse_RecordsMalformed_ForLineStartingWithEquals`
pins today's behavior and must be updated to match your decision. State which
you chose and why in your report.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SigilBuild.Localization.Generator.Tests/CatalogDiagnosticsTests.cs
using System.Linq;
using FluentAssertions;
using SigilBuild.Localization.Generator;
using Xunit;

namespace SigilBuild.Localization.Generator.Tests;

public class CatalogDiagnosticsTests
{
    private static string[] Validate(string en, string uk) =>
        CatalogValidator.Validate(new[]
        {
            CatalogParser.Parse("Strings.en.txt", en),
            CatalogParser.Parse("Strings.uk.txt", uk),
        }).Select(p => p.Id).ToArray();

    [Fact]
    public void OrphanKeyInTranslation_IsError001()
    {
        Validate("a = A\n", "a = А\nb = Б\n").Should().Contain("SIGLOC001");
    }

    [Fact]
    public void KeyMissingFromTranslation_IsWarning002()
    {
        Validate("a = A\nb = B\n", "a = А\n").Should().Contain("SIGLOC002");
    }

    [Fact]
    public void DroppedPlaceholder_IsError003()
    {
        // The translator lost {toVersion} — the exact defect this catches.
        Validate(
            "upgrading = Upgrading {appName} to {toVersion}\n",
            "upgrading = Оновлення {appName}\n").Should().Contain("SIGLOC003");
    }

    [Fact]
    public void ReorderedPlaceholders_AreNotAnError()
    {
        // Set equality, not sequence equality: word order is the translator's business.
        Validate(
            "greet = Hello {first} {last}\n",
            "greet = Вітаю {last} {first}\n").Should().NotContain("SIGLOC003");
    }

    [Fact]
    public void DuplicateKey_IsError004()
    {
        Validate("a = A\na = B\n", "a = А\n").Should().Contain("SIGLOC004");
    }

    [Fact]
    public void MalformedLine_IsError005()
    {
        Validate("this line has no equals sign\n", "a = А\n").Should().Contain("SIGLOC005");
    }

    [Fact]
    public void MatchingCatalogs_ProduceNoProblems()
    {
        Validate("a = A\nb = B {x}\n", "a = А\nb = Б {x}\n").Should().BeEmpty();
    }

    // SIGLOC006 — MethodName splits on '.' AND '_', so these two distinct keys
    // both become LocationErrorNotAbsolute. The emitter would write two
    // identical signatures -> CS0111 pointing at generated source.
    [Fact]
    public void KeysCollidingOnMethodName_IsError006()
    {
        Validate(
            "location.error.notAbsolute = A\nlocation.error.not_absolute = B\n",
            "location.error.notAbsolute = А\nlocation.error.not_absolute = Б\n")
            .Should().Contain("SIGLOC006");
    }

    [Fact]
    public void DistinctMethodNames_AreNotACollision()
    {
        Validate("nav.back = Back\nnav.next = Next\n", "nav.back = Назад\nnav.next = Далі\n")
            .Should().NotContain("SIGLOC006");
    }

    // SIGLOC007 — {class} passes the placeholder regex but emits `string class` -> CS1041.
    [Fact]
    public void PlaceholderNamedForCSharpKeyword_IsError007()
    {
        Validate("x = a {class} b\n", "x = а {class} б\n").Should().Contain("SIGLOC007");
    }

    [Fact]
    public void PlaceholderNamedLikeAKeywordButNotOne_IsFine()
    {
        Validate("x = a {className} b\n", "x = а {className} б\n").Should().NotContain("SIGLOC007");
    }
}
```

For `SIGLOC007`, use Roslyn's own keyword table rather than hand-listing:
`Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None`
(and consider `GetContextualKeywordKind`). Hand-maintained keyword lists rot.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SigilBuild.Localization.Generator.Tests -c Release --filter CatalogDiagnosticsTests`
Expected: FAIL — `CatalogValidator` does not exist.

- [ ] **Step 3: Write the validator**

```csharp
// src/SigilBuild.Localization.Generator/CatalogDiagnostics.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace SigilBuild.Localization.Generator;

internal sealed class CatalogProblem
{
    public string Id { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
    public int Line { get; set; }
}

internal static class CatalogValidator
{
    public static IReadOnlyList<CatalogProblem> Validate(IReadOnlyList<CatalogFile> files)
    {
        var problems = new List<CatalogProblem>();
        var en = files.FirstOrDefault(f => f.Lang == "en");

        foreach (var file in files)
        {
            var name = $"Strings.{file.Lang}.txt";

            foreach (var (message, line) in file.Malformed)
            {
                problems.Add(new CatalogProblem { Id = "SIGLOC005", Message = message, File = name, Line = line });
            }

            foreach (var group in file.Entries.GroupBy(e => e.Key, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                problems.Add(new CatalogProblem
                {
                    Id = "SIGLOC004",
                    Message = $"duplicate key '{group.Key}'",
                    File = name,
                    Line = group.Last().Line,
                });
            }
        }

        if (en is null)
        {
            return problems;
        }

        var enKeys = en.Entries.ToDictionary(e => e.Key, e => e, StringComparer.Ordinal);

        foreach (var file in files.Where(f => f.Lang != "en"))
        {
            var name = $"Strings.{file.Lang}.txt";

            foreach (var entry in file.Entries)
            {
                if (!enKeys.TryGetValue(entry.Key, out var enEntry))
                {
                    problems.Add(new CatalogProblem
                    {
                        Id = "SIGLOC001",
                        Message = $"key '{entry.Key}' has no counterpart in Strings.en.txt",
                        File = name,
                        Line = entry.Line,
                    });
                    continue;
                }

                // Set comparison, deliberately: a translation may reorder placeholders,
                // but must not drop or invent one.
                var enSet = new HashSet<string>(enEntry.Placeholders, StringComparer.Ordinal);
                var trSet = new HashSet<string>(entry.Placeholders, StringComparer.Ordinal);
                if (!enSet.SetEquals(trSet))
                {
                    problems.Add(new CatalogProblem
                    {
                        Id = "SIGLOC003",
                        Message =
                            $"key '{entry.Key}' placeholders {{{string.Join(",", trSet.OrderBy(x => x, StringComparer.Ordinal))}}} " +
                            $"do not match en {{{string.Join(",", enSet.OrderBy(x => x, StringComparer.Ordinal))}}}",
                        File = name,
                        Line = entry.Line,
                    });
                }
            }

            var trKeys = new HashSet<string>(file.Entries.Select(e => e.Key), StringComparer.Ordinal);
            foreach (var missing in en.Entries.Where(e => !trKeys.Contains(e.Key)))
            {
                problems.Add(new CatalogProblem
                {
                    Id = "SIGLOC002",
                    Message = $"key '{missing.Key}' is missing; falls back to English",
                    File = name,
                    Line = 1,
                });
            }
        }

        return problems;
    }

    public static readonly DiagnosticDescriptor Orphan = Descriptor("SIGLOC001", DiagnosticSeverity.Error);
    public static readonly DiagnosticDescriptor Missing = Descriptor("SIGLOC002", DiagnosticSeverity.Warning);
    public static readonly DiagnosticDescriptor Placeholders = Descriptor("SIGLOC003", DiagnosticSeverity.Error);
    public static readonly DiagnosticDescriptor Duplicate = Descriptor("SIGLOC004", DiagnosticSeverity.Error);
    public static readonly DiagnosticDescriptor Malformed = Descriptor("SIGLOC005", DiagnosticSeverity.Error);
    public static readonly DiagnosticDescriptor Collision = Descriptor("SIGLOC006", DiagnosticSeverity.Error);
    public static readonly DiagnosticDescriptor KeywordPlaceholder = Descriptor("SIGLOC007", DiagnosticSeverity.Error);

    public static DiagnosticDescriptor For(string id) => id switch
    {
        "SIGLOC001" => Orphan,
        "SIGLOC002" => Missing,
        "SIGLOC003" => Placeholders,
        "SIGLOC004" => Duplicate,
        "SIGLOC005" => Malformed,
        "SIGLOC006" => Collision,
        "SIGLOC007" => KeywordPlaceholder,
        _ => throw new System.ArgumentOutOfRangeException(nameof(id), id, "unknown catalog diagnostic id"),
    };

    private static DiagnosticDescriptor Descriptor(string id, DiagnosticSeverity severity) =>
        new(id, "Localization catalog", "{0}", "Localization", severity, isEnabledByDefault: true);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SigilBuild.Localization.Generator.Tests -c Release --filter CatalogDiagnosticsTests`
Expected: PASS, 7 tests.

- [ ] **Step 5: Wire the incremental generator**

```csharp
// src/SigilBuild.Localization.Generator/StringsGenerator.cs
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace SigilBuild.Localization.Generator;

[Generator]
public sealed class StringsGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var catalogs = context.AdditionalTextsProvider
            .Where(static t => Path.GetFileName(t.Path).StartsWith("Strings.", System.StringComparison.Ordinal)
                            && Path.GetExtension(t.Path) == ".txt")
            .Select(static (t, ct) => (Name: Path.GetFileName(t.Path), Text: t.GetText(ct)?.ToString() ?? string.Empty))
            .Collect();

        context.RegisterSourceOutput(catalogs, static (spc, items) =>
        {
            if (items.IsDefaultOrEmpty)
            {
                return;
            }

            var files = items
                .Select(i => CatalogParser.Parse(i.Name, i.Text))
                .Where(f => f.Lang.Length > 0)
                .OrderBy(f => f.Lang, System.StringComparer.Ordinal)
                .ToList();

            var problems = CatalogValidator.Validate(files);
            var hasError = false;

            foreach (var problem in problems)
            {
                var descriptor = CatalogValidator.For(problem.Id);
                hasError |= descriptor.DefaultSeverity == DiagnosticSeverity.Error;

                spc.ReportDiagnostic(Diagnostic.Create(
                    descriptor,
                    Location.None,
                    $"{problem.File}({problem.Line}): {problem.Message}"));
            }

            // Emission is SUPPRESSED on any Error. Every Error-severity problem
            // corresponds to generated code that would also fail to compile with an
            // opaque error pointing at generated source (SIGLOC004/006 -> CS0111,
            // SIGLOC007 -> CS1041). Emitting anyway would show the author both, and
            // the opaque one is louder. Warnings (SIGLOC002 — a partial translation,
            // which is legal) must NOT suppress emission.
            if (hasError)
            {
                return;
            }

            spc.AddSource("Strings.g.cs", SourceText.From(StringsEmitter.Emit(files), System.Text.Encoding.UTF8));
        });
    }
}
```

- [ ] **Step 6: Commit**

```bash
git add src/SigilBuild.Localization.Generator tests/SigilBuild.Localization.Generator.Tests
git commit -m "feat(p9): catalog diagnostics SIGLOC001-005 + incremental generator wiring"
```

---

### Task 4: Wire the generator into Wrapper.Core + session language guard

**Files:**
- Modify: `src/SigilBuild.Wrapper.Core/SigilBuild.Wrapper.Core.csproj`
- Create: `src/SigilBuild.Wrapper.Core/Localization/Strings.en.txt`
- Create: `src/SigilBuild.Wrapper.Core/Localization/Strings.uk.txt`
- Create: `src/SigilBuild.Wrapper.Core/Localization/SessionLanguage.cs`
- Create: `tests/SigilBuild.Wrapper.Tests/Localization/SessionLanguageTests.cs`

**Interfaces:**
- Consumes: generated `Lang`, `Strings` (Task 2).
- Produces: `SessionLanguage.Set(Lang)`, `SessionLanguage.Current` (throws in Debug if unset), `SessionLanguage.SetForTesting(Lang)` (internal), and `S` — a static class with one property per key resolved against `SessionLanguage.Current`, for XAML `{x:Static}`.

Per spec §3.2, `Lang` is public (host ViewModels need it) so `Lang.Pseudo` is a public member. What is internal is the **path to select it**.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SigilBuild.Wrapper.Tests/Localization/SessionLanguageTests.cs
using System;
using FluentAssertions;
using SigilBuild.Wrapper.Core.Localization;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Localization;

[Collection("SessionLanguage")] // static state: must not run in parallel
public class SessionLanguageTests : IDisposable
{
    public void Dispose() => SessionLanguage.ResetForTesting();

    [Fact]
    public void Current_BeforeSet_ThrowsInDebug()
    {
        SessionLanguage.ResetForTesting();
#if DEBUG
        var act = () => _ = SessionLanguage.Current;
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*before*language*resolved*");
#else
        SessionLanguage.Current.Should().Be(Lang.En);
#endif
    }

    [Fact]
    public void Set_ThenCurrent_ReturnsIt()
    {
        SessionLanguage.Set(Lang.Uk);
        SessionLanguage.Current.Should().Be(Lang.Uk);
    }

    [Fact]
    public void Strings_ResolveAgainstLang()
    {
        Strings.NavBack(Lang.En).Should().Be("Back");
        Strings.NavBack(Lang.Uk).Should().Be("Назад");
    }

    [Fact]
    public void Pseudo_IsBracketed()
    {
        Strings.NavBack(Lang.Pseudo).Should().StartWith("[").And.EndWith("]");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SigilBuild.Wrapper.Tests -c Release --filter SessionLanguageTests`
Expected: FAIL — `SessionLanguage` does not exist; `Strings` not generated yet.

- [ ] **Step 3: Wire the generator + seed catalogs**

Add to `src/SigilBuild.Wrapper.Core/SigilBuild.Wrapper.Core.csproj`:

```xml
  <ItemGroup>
    <ProjectReference Include="..\SigilBuild.Localization.Generator\SigilBuild.Localization.Generator.csproj"
                      OutputItemType="Analyzer"
                      ReferenceOutputAssembly="false" />
  </ItemGroup>

  <ItemGroup>
    <AdditionalFiles Include="Localization\Strings.*.txt" />
  </ItemGroup>
```

```text
# src/SigilBuild.Wrapper.Core/Localization/Strings.en.txt
# Baseline catalog. Every user-facing chrome string lives here; see
# docs/plan/feature-parity/P9-DESIGN-localization.md §3.
# Rule (§3.1): a count may appear in a value, but must never inflect the
# sentence around it. "Applications to close: {count}" is allowed;
# "{count} applications must be closed" is not.
# Reviewer: n/a (source language)

nav.back = Back
```

```text
# src/SigilBuild.Wrapper.Core/Localization/Strings.uk.txt
# Ukrainian (uk).
# Reviewer: Yevhen Khudoliiv — native speaker, reviewed 2026-07-15.
# ADR-008 §4: a language ships only with a named reviewer recorded here.

nav.back = Назад
```

- [ ] **Step 4: Write SessionLanguage**

```csharp
// src/SigilBuild.Wrapper.Core/Localization/SessionLanguage.cs
using System;
using System.Diagnostics.CodeAnalysis;

namespace SigilBuild.Wrapper.Core.Localization;

/// <summary>
/// The resolved chrome language for this install session. Set exactly once at
/// session start (see the resolver) before any UI is constructed, and immutable
/// thereafter — which is what makes the generated <c>S</c> static accessor legal
/// in XAML <c>{x:Static}</c>.
/// </summary>
/// <remarks>
/// The guard is deliberately asymmetric (design §3.2). A read before
/// initialization means someone reordered startup, and the natural consequence
/// would be a silent wrong-language render — invisible to tests and users alike.
/// Debug throws so the test suite fails loudly; Release falls back to English and
/// logs, so a shipped installer degrades rather than dies.
/// </remarks>
public static class SessionLanguage
{
    private static Lang? _current;

    public static Lang Current
    {
        get
        {
            if (_current is { } value)
            {
                return value;
            }
#if DEBUG
            throw new InvalidOperationException(
                "SessionLanguage.Current read before the session language was resolved. " +
                "Resolution must run at session start, before any UI is constructed.");
#else
            OnUninitializedRead?.Invoke();
            return Lang.En;
#endif
        }
    }

    /// <summary>Raised on a Release-mode read before <see cref="Set"/>. Wired to the install log.</summary>
    public static Action? OnUninitializedRead { get; set; }

    public static bool IsSet => _current is not null;

    public static void Set(Lang lang) => _current = lang;

    [SuppressMessage("Usage", "CA2255", Justification = "Test-only reset of static session state.")]
    internal static void SetForTesting(Lang lang) => _current = lang;

    internal static void ResetForTesting() => _current = null;
}
```

Add to `src/SigilBuild.Wrapper.Core/SigilBuild.Wrapper.Core.csproj` (or the existing `InternalsVisibleTo` block):

```xml
    <InternalsVisibleTo Include="SigilBuild.Wrapper.Tests" />
    <InternalsVisibleTo Include="SigilBuild.Installer.Host" />
    <InternalsVisibleTo Include="SigilBuild.Installer.Host.Tests" />
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/SigilBuild.Wrapper.Tests -c Release --filter SessionLanguageTests`
Expected: PASS, 6 tests.

- [ ] **Step 6: Verify the generator actually ran**

Run: `dotnet build src/SigilBuild.Wrapper.Core -c Release -p:EmitCompilerGeneratedFiles=true`
Expected: `obj/Release/net10.0/generated/SigilBuild.Localization.Generator/SigilBuild.Localization.Generator.StringsGenerator/Strings.g.cs` exists and contains `public static string NavBack(Lang lang)`.

If the generator does not run, the usual cause is a missing `OutputItemType="Analyzer"` — do not work around it by checking in generated code.

- [ ] **Step 7: Commit**

```bash
git add src/SigilBuild.Wrapper.Core tests/SigilBuild.Wrapper.Tests
git commit -m "feat(p9): wire generator into Wrapper.Core + session language guard"
```

---

### Task 5: Language tag validator (one rule, two call sites)

**Files:**
- Create: `src/SigilBuild.Core/Manifest/LanguageTag.cs`
- Create: `tests/SigilBuild.Core.Tests/Manifest/LanguageTagTests.cs`

**Interfaces:**
- Produces: `LanguageTag.IsValid(string? tag) -> bool`. Grammar per spec §6.2: `ALPHA{2,3} ( "-" ALPHANUM{1,8} )*`, matched `OrdinalIgnoreCase`.

This lives in `SigilBuild.Core` because **both** `SIG0291` (pack time, Task 8) and `/lang` (`CommandLineParser`, Task 10) must use it. `SigilBuild.Wrapper.Core` already references `SigilBuild.Core` (`SigilBuild.Wrapper.Core.csproj:22`), so one implementation serves both. Do not duplicate this logic — pack time and parse time drifting apart is the exact failure this prevents.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SigilBuild.Core.Tests/Manifest/LanguageTagTests.cs
using FluentAssertions;
using SigilBuild.Core.Manifest;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

public class LanguageTagTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("uk")]
    [InlineData("de")]
    [InlineData("pt-BR")]
    [InlineData("zh-Hans")]
    [InlineData("de-AT")]
    [InlineData("qps")]
    [InlineData("EN")]       // ordinal-ignore-case
    [InlineData("pt-br")]
    public void Valid(string tag) => LanguageTag.IsValid(tag).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("e")]            // primary subtag too short
    [InlineData("engl")]         // primary subtag too long
    [InlineData("pseudo")]       // 6 alpha: rejected, which is why /lang=pseudo cannot reach Lang.Pseudo
    [InlineData("!!")]
    [InlineData("en-")]          // empty subtag
    [InlineData("en--US")]
    [InlineData("en-toolongsubtag")] // subtag > 8
    [InlineData("en US")]
    public void Invalid(string? tag) => LanguageTag.IsValid(tag).Should().BeFalse();
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SigilBuild.Core.Tests -c Release --filter LanguageTagTests`
Expected: FAIL — `LanguageTag` does not exist.

- [ ] **Step 3: Write the validator**

```csharp
// src/SigilBuild.Core/Manifest/LanguageTag.cs
namespace SigilBuild.Core.Manifest;

/// <summary>
/// The one language-tag rule, shared by pack-time validation (SIG0291) and the
/// installer's <c>/lang</c> flag. Two call sites, one implementation — see
/// docs/plan/feature-parity/P9-DESIGN-localization.md §6.2.
/// </summary>
/// <remarks>
/// A deliberate ordinal subset of BCP-47: <c>ALPHA{2,3} ( "-" ALPHANUM{1,8} )*</c>.
/// This accepts everything Sigil realistically needs (en, uk, pt-BR, zh-Hans,
/// de-AT) and rejects the malformed. Full BCP-47 — grandfathered tags,
/// extensions, private-use sequences — buys nothing here and would need a parser
/// the AOT constraints would rather not carry. No CultureInfo: constructing one
/// throws under InvariantGlobalization.
/// </remarks>
public static class LanguageTag
{
    public static bool IsValid(string? tag)
    {
        if (string.IsNullOrEmpty(tag))
        {
            return false;
        }

        var segments = tag!.Split('-');

        var primary = segments[0];
        if (primary.Length is < 2 or > 3 || !AllAlpha(primary))
        {
            return false;
        }

        for (var i = 1; i < segments.Length; i++)
        {
            var s = segments[i];
            if (s.Length is < 1 or > 8 || !AllAlphaNum(s))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AllAlpha(string s)
    {
        foreach (var c in s)
        {
            if (!IsAlpha(c)) return false;
        }
        return true;
    }

    private static bool AllAlphaNum(string s)
    {
        foreach (var c in s)
        {
            if (!IsAlpha(c) && !(c is >= '0' and <= '9')) return false;
        }
        return true;
    }

    // Ordinal ASCII checks only — char.IsLetter would drag in culture data.
    private static bool IsAlpha(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SigilBuild.Core.Tests -c Release --filter LanguageTagTests`
Expected: PASS, 19 cases.

- [ ] **Step 5: Commit**

```bash
git add src/SigilBuild.Core/Manifest/LanguageTag.cs tests/SigilBuild.Core.Tests/Manifest/LanguageTagTests.cs
git commit -m "feat(p9): shared language-tag validator (ordinal BCP-47 subset)"
```

---

### Task 6: Re-point locale() at the OS UI language

**Files:**
- Create: `src/SigilBuild.Wrapper.Core/Localization/OsUiLanguage.cs`
- Modify: `src/SigilBuild.Wrapper.Core/Expressions/Functions.cs:47-50`
- Create: `tests/SigilBuild.Wrapper.Tests/Localization/OsUiLanguageTests.cs`

**Interfaces:**
- Produces: `OsUiLanguage.Preferences() -> IReadOnlyList<string>` (ordered, possibly empty) and `OsUiLanguage.Primary() -> string` (`""` when unavailable).

Per spec §4.1: `locale()` returns the **first** entry (scalar semantics — it answers "where is this machine"). The full ordered list is for the resolver (Task 7). Every failure path yields `""`, keeping the function total per ADR-008 §1.2.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SigilBuild.Wrapper.Tests/Localization/OsUiLanguageTests.cs
using System.Runtime.InteropServices;
using FluentAssertions;
using SigilBuild.Wrapper.Core.Localization;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Localization;

public class OsUiLanguageTests
{
    [Fact]
    public void Preferences_OnWindows_AreWellFormedTags()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            OsUiLanguage.Preferences().Should().BeEmpty();
            return;
        }

        var prefs = OsUiLanguage.Preferences();
        prefs.Should().NotBeEmpty("a Windows machine always reports at least one UI language");
        prefs.Should().OnlyContain(t => SigilBuild.Core.Manifest.LanguageTag.IsValid(t));
    }

    [Fact]
    public void Primary_IsFirstPreference_OrEmpty()
    {
        var prefs = OsUiLanguage.Preferences();
        OsUiLanguage.Primary().Should().Be(prefs.Count > 0 ? prefs[0] : string.Empty);
    }

    [Fact]
    public void Primary_IsTotal_NeverNullNeverThrows()
    {
        var act = () => OsUiLanguage.Primary();
        act.Should().NotThrow("ADR-008 §1.2 requires locale() to be total");
        OsUiLanguage.Primary().Should().NotBeNull();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SigilBuild.Wrapper.Tests -c Release --filter OsUiLanguageTests`
Expected: FAIL — `OsUiLanguage` does not exist.

- [ ] **Step 3: Write the Win32 probe**

```csharp
// src/SigilBuild.Wrapper.Core/Localization/OsUiLanguage.cs
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SigilBuild.Core.Manifest;

namespace SigilBuild.Wrapper.Core.Localization;

/// <summary>
/// Reads the user's ordered UI-language preferences from Win32.
/// </summary>
/// <remarks>
/// CultureInfo cannot be used: InvariantGlobalization=true makes
/// CurrentUICulture.Name always "" and makes `new CultureInfo("uk-UA")` throw.
/// GetUserPreferredUILanguages is a bounded, read-only, deterministic probe, so
/// locale() stays inside ADR-008 §1.2 — only its source changes, not its
/// contract. Every failure path returns empty, keeping the function total.
/// </remarks>
public static partial class OsUiLanguage   // partial is required by [LibraryImport]
{
    private const uint MuiLanguageName = 0x8;

    [LibraryImport("kernel32.dll", EntryPoint = "GetUserPreferredUILanguages", SetLastError = true,
                   StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetUserPreferredUILanguages(
        uint dwFlags, out uint pulNumLanguages, Span<char> pwszLanguagesBuffer, ref uint pcchLanguagesBuffer);

    public static IReadOnlyList<string> Preferences()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Array.Empty<string>();
        }

        try
        {
            uint count = 0;
            uint chars = 0;

            // First call sizes the buffer.
            if (!GetUserPreferredUILanguages(MuiLanguageName, out count, Span<char>.Empty, ref chars) || chars == 0)
            {
                return Array.Empty<string>();
            }

            var buffer = new char[chars];
            if (!GetUserPreferredUILanguages(MuiLanguageName, out count, buffer, ref chars))
            {
                return Array.Empty<string>();
            }

            // Double-null-terminated, null-separated list.
            var result = new List<string>((int)count);
            var start = 0;
            for (var i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] != '\0')
                {
                    continue;
                }

                if (i > start)
                {
                    var tag = new string(buffer, start, i - start);
                    if (LanguageTag.IsValid(tag))
                    {
                        result.Add(tag);
                    }
                }

                start = i + 1;
                if (start < buffer.Length && buffer[start] == '\0')
                {
                    break; // double null: end of list
                }
            }

            return result;
        }
        catch (Exception)
        {
            // Total by contract (ADR-008 §1.2): an absent/denied path yields "".
            return Array.Empty<string>();
        }
    }

    public static string Primary()
    {
        var prefs = Preferences();
        return prefs.Count > 0 ? prefs[0] : string.Empty;
    }
}
```

Mark the containing class `partial` for `[LibraryImport]`: change the declaration to `public static partial class OsUiLanguage`.

- [ ] **Step 4: Re-point locale()**

Replace `src/SigilBuild.Wrapper.Core/Expressions/Functions.cs:47-50`:

```csharp
        // locale() reads the OS UI language (Win32), NOT CurrentUICulture — which is
        // always "" under InvariantGlobalization=true. Returns the user's top
        // preference; the full ordered list drives language resolution
        // (Localization/LanguageResolver). Total: "" when unavailable.
        // ADR-008 §1.1 amended 2026-07-15 — this is a behavior change, not only a
        // source change: a `When` using locale() moves from always-"" to a real tag.
        ["locale"] = _ => OsUiLanguage.Primary(),
```

Add `using SigilBuild.Wrapper.Core.Localization;` to the file's usings.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SigilBuild.Wrapper.Tests -c Release --filter "OsUiLanguageTests|Functions"`
Expected: PASS. Any existing test asserting `locale()` returns `""` must be **updated, not deleted** — it now asserts `""` only off-Windows. If you find such a test, report it in your summary.

- [ ] **Step 6: Verify AOT stays clean**

Run: `dotnet publish src/SigilBuild.Installer.Host -c Release -r win-x64 -p:PublishAot=true`
Expected: no `IL2xxx`/`IL3xxx` warnings. `[LibraryImport]` is source-generated and AOT-safe; `[DllImport]` would not be.

- [ ] **Step 7: Commit**

```bash
git add src/SigilBuild.Wrapper.Core tests/SigilBuild.Wrapper.Tests
git commit -m "feat(p9): locale() reads the OS UI language via GetUserPreferredUILanguages"
```

---

### Task 7: LanguageResolver — chain, list-walk, matching

**Files:**
- Create: `src/SigilBuild.Wrapper.Core/Localization/LanguageResolver.cs`
- Create: `tests/SigilBuild.Wrapper.Tests/Localization/LanguageResolverTests.cs`

**Interfaces:**
- Consumes: `OsUiLanguage.Preferences()` (Task 6), `LanguageTag.IsValid` (Task 5), generated `Lang` (Task 2).
- Produces:
  - `LanguageResolver.Preferences(string? manifestLanguage, string? langFlag, IReadOnlyList<string> osPreferences) -> IReadOnlyList<string>`
  - `LanguageResolver.Match(IReadOnlyList<string> preferences, IReadOnlyCollection<string> available) -> string` (returns `"en"` when nothing matches)
  - `LanguageResolver.MatchChrome(IReadOnlyList<string> preferences) -> Lang`

Per spec §4.2/§4.5. `Lang.Pseudo` is **never** returned by `MatchChrome` — no catalog declares that tag and `pseudo` fails `LanguageTag.IsValid`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SigilBuild.Wrapper.Tests/Localization/LanguageResolverTests.cs
using System;
using System.Collections.Generic;
using FluentAssertions;
using SigilBuild.Wrapper.Core.Localization;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Localization;

public class LanguageResolverTests
{
    private static readonly string[] Os = { "de-DE", "uk-UA" };

    [Fact]
    public void ManifestLanguage_Wins_OverFlagAndOs()
    {
        LanguageResolver.Preferences("en", "uk", Os).Should().Equal("en");
    }

    [Fact]
    public void Flag_Wins_OverOs()
    {
        LanguageResolver.Preferences(null, "uk", Os).Should().Equal("uk");
    }

    [Fact]
    public void Os_UsedWhenNoManifestOrFlag_AsFullOrderedList()
    {
        LanguageResolver.Preferences(null, null, Os).Should().Equal("de-DE", "uk-UA");
    }

    [Fact]
    public void NothingAvailable_FallsBackToEn()
    {
        LanguageResolver.Preferences(null, null, Array.Empty<string>()).Should().Equal("en");
    }

    [Fact]
    public void Match_ExactBeatsEverything_CaseInsensitive()
    {
        LanguageResolver.Match(new[] { "pt-br" }, new[] { "en", "pt-BR" }).Should().Be("pt-BR");
    }

    [Fact]
    public void Match_FallsBackToPrimarySubtag()
    {
        LanguageResolver.Match(new[] { "de-AT" }, new[] { "en", "de" }).Should().Be("de");
    }

    [Fact]
    public void Match_PrimarySubtagPicksOrdinalFirst_Deterministically()
    {
        LanguageResolver.Match(new[] { "de" }, new[] { "de-CH", "de-AT", "en" }).Should().Be("de-AT");
    }

    [Fact]
    public void Match_NoHit_FallsBackToEn()
    {
        LanguageResolver.Match(new[] { "zz" }, new[] { "en", "uk" }).Should().Be("en");
    }

    // The reason list-walk exists (design §4.2). This test fails under first-only.
    [Fact]
    public void Match_WalksPastUnavailableTopPreference()
    {
        LanguageResolver.Match(Os, new[] { "en", "uk" }).Should().Be("uk");
    }

    [Fact]
    public void MatchChrome_ForThatSameList_IsUk_NotEn()
    {
        LanguageResolver.MatchChrome(Os).Should().Be(Lang.Uk);
    }

    [Fact]
    public void MatchChrome_NeverReturnsPseudo()
    {
        LanguageResolver.MatchChrome(new[] { "pseudo" }).Should().Be(Lang.En);
        LanguageResolver.MatchChrome(new[] { "qps" }).Should().Be(Lang.En);
    }

    [Fact]
    public void InvalidManifestLanguage_IsIgnored_NotCrashed()
    {
        // SIG0291 rejects it at pack time; a blob that predates the check must not crash.
        LanguageResolver.Preferences("!!", null, Os).Should().Equal("de-DE", "uk-UA");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SigilBuild.Wrapper.Tests -c Release --filter LanguageResolverTests`
Expected: FAIL — `LanguageResolver` does not exist.

- [ ] **Step 3: Write the resolver**

```csharp
// src/SigilBuild.Wrapper.Core/Localization/LanguageResolver.cs
using System;
using System.Collections.Generic;
using SigilBuild.Core.Manifest;

namespace SigilBuild.Wrapper.Core.Localization;

/// <summary>
/// Resolves the session's language preferences and matches surfaces against them.
/// See docs/plan/feature-parity/P9-DESIGN-localization.md §4.
/// </summary>
public static class LanguageResolver
{
    private static readonly string[] EnOnly = { "en" };

    /// <summary>
    /// The chain: installer.language (fixed) -> /lang -> OS list -> en.
    /// Returns an ORDERED list, not a single tag: the OS reports preferences in
    /// order, and a user whose list is [de-DE, uk-UA] has said they read Ukrainian
    /// better than English. Taking only the first entry would discard that.
    /// </summary>
    public static IReadOnlyList<string> Preferences(
        string? manifestLanguage, string? langFlag, IReadOnlyList<string> osPreferences)
    {
        if (LanguageTag.IsValid(manifestLanguage))
        {
            return new[] { manifestLanguage! };
        }

        if (LanguageTag.IsValid(langFlag))
        {
            return new[] { langFlag! };
        }

        return osPreferences.Count > 0 ? osPreferences : EnOnly;
    }

    /// <summary>
    /// Ordinal-only best match. No ICU, no CultureInfo. Returns "en" when nothing
    /// matches — total for manifest maps because SIG0290 makes an en-less map a
    /// pack-time error.
    /// </summary>
    public static string Match(IReadOnlyList<string> preferences, IReadOnlyCollection<string> available)
    {
        foreach (var pref in preferences)
        {
            foreach (var a in available)
            {
                if (string.Equals(a, pref, StringComparison.OrdinalIgnoreCase))
                {
                    return a;
                }
            }

            var primary = PrimarySubtag(pref);

            foreach (var a in available)
            {
                if (string.Equals(a, primary, StringComparison.OrdinalIgnoreCase))
                {
                    return a;
                }
            }

            // Ordinal-first among same-primary candidates, purely for determinism:
            // de -> {de-CH, de-AT} must resolve identically on every machine.
            string? best = null;
            foreach (var a in available)
            {
                if (!string.Equals(PrimarySubtag(a), primary, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (best is null || string.CompareOrdinal(a, best) < 0)
                {
                    best = a;
                }
            }

            if (best is not null)
            {
                return best;
            }
        }

        return "en";
    }

    /// <summary>
    /// Matches against the chrome catalog's language set. Never returns Lang.Pseudo:
    /// no catalog declares that tag and "pseudo" fails LanguageTag.IsValid.
    /// </summary>
    /// <remarks>
    /// Both the tag list and the mapping are GENERATED from Localization/Strings.*.txt
    /// (ChromeCatalog). Nothing here is hand-maintained, so adding Strings.de.txt wires
    /// the language end-to-end with no code edit — which is what ADR-008 §4's
    /// "languages ship as content contributions" rule requires. A hardcoded list here
    /// would make a new catalog file compile but stay unreachable, with no failing test.
    /// </remarks>
    public static Lang MatchChrome(IReadOnlyList<string> preferences) =>
        ChromeCatalog.FromTag(Match(preferences, ChromeCatalog.Tags));

    private static string PrimarySubtag(string tag)
    {
        var dash = tag.IndexOf('-');
        return dash < 0 ? tag : tag.Substring(0, dash);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SigilBuild.Wrapper.Tests -c Release --filter LanguageResolverTests`
Expected: PASS, 12 tests.

- [ ] **Step 5: Commit**

```bash
git add src/SigilBuild.Wrapper.Core/Localization/LanguageResolver.cs tests/SigilBuild.Wrapper.Tests/Localization/LanguageResolverTests.cs
git commit -m "feat(p9): language resolver with ordered-preference list-walk"
```

---

### Task 8: LocalizedText + records + schema + blob (single M0 pass)

**Files:**
- Create: `src/SigilBuild.Core/Manifest/LocalizedText.cs`
- Modify: `src/SigilBuild.Core/Manifest/InstallerScreen.cs:16-34`
- Modify: `src/SigilBuild.Core/Manifest/ParameterDefinition.cs:11-22`
- Modify: `src/SigilBuild.Core/Manifest/InstallerSection.cs:53+` (add `Language`)
- Modify: `src/SigilBuild.Core/Diagnostics/DiagnosticCodes.cs` (append `SIG029x`)
- Modify: `schemas/sigil-schema.json` (add `LocalizedText`; retype `title`/`subtitle`/`description`/`license`; add `language`)
- Modify: `src/SigilBuild.Wrapper.Core/Json/SerializableWrapperBlob.cs:407-478` + `ToWrapperBlob`/`FromWrapperBlob`
- Create: `tests/SigilBuild.Core.Tests/Manifest/LocalizedTextTests.cs`

**Interfaces:**
- Consumes: `LanguageTag.IsValid` (Task 5).
- Produces: `LocalizedText` record with `IReadOnlyDictionary<string,string> Values` and `static LocalizedText Plain(string)`. `InstallerScreen.Title: LocalizedText`, `InstallerScreen.Subtitle: LocalizedText?`, `ParameterDefinition.Description: LocalizedText?`, `InstallerSection.Language: string?`. Blob carries `Dictionary<string,string>`.

Per spec §5. Plain strings **normalize at parse time** to `{"en": value}`, so the map is the only runtime shape and no consumer branches on "string or map".

Diagnostics to append to `DiagnosticCodes.cs`:

```csharp
    // SIG029x — localization (P9, gap G10)
    // SIG0290 is FATAL: every runtime fallback bottoms out at `en`, so a map
    // without it has no defined rendering. Pack diagnostics reach manifest
    // authors, who do not build under this repo's TreatWarningsAsErrors — a
    // warning here would genuinely ship blank strings.
    public const string LocalizedTextMissingEnglish = "SIG0290";
    public const string InvalidLanguageTag = "SIG0291";
```

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SigilBuild.Core.Tests/Manifest/LocalizedTextTests.cs
using System.Collections.Generic;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

public class LocalizedTextTests
{
    [Fact]
    public void Plain_NormalizesToEnglishKeyedMap()
    {
        var text = LocalizedText.Plain("Configure");

        text.Values.Should().HaveCount(1);
        text.Values["en"].Should().Be("Configure");
    }

    [Fact]
    public void Map_IsCarriedVerbatim()
    {
        var text = new LocalizedText(new Dictionary<string, string> { ["en"] = "Configure", ["uk"] = "Налаштування" });

        text.Values.Should().HaveCount(2);
        text.Values["uk"].Should().Be("Налаштування");
    }

    [Fact]
    public void HasEnglish_IsFalse_WhenMapOmitsIt()
    {
        var text = new LocalizedText(new Dictionary<string, string> { ["uk"] = "Налаштування" });

        text.HasEnglish.Should().BeFalse("SIG0290 keys off this");
    }

    [Fact]
    public void HasEnglish_IsCaseInsensitive()
    {
        new LocalizedText(new Dictionary<string, string> { ["EN"] = "x" }).HasEnglish.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SigilBuild.Core.Tests -c Release --filter LocalizedTextTests`
Expected: FAIL — `LocalizedText` does not exist.

- [ ] **Step 3: Write LocalizedText**

```csharp
// src/SigilBuild.Core/Manifest/LocalizedText.cs
using System;
using System.Collections.Generic;
using System.Linq;

namespace SigilBuild.Core.Manifest;

/// <summary>
/// Manifest text that may be authored either as a plain string or as a
/// <c>{ en: ..., uk: ... }</c> map. A plain string normalizes to <c>{"en": value}</c>
/// at parse time, so the map is the only shape that exists at runtime and no
/// consumer branches on "string or map".
/// </summary>
/// <remarks>
/// Picking a language is deliberately NOT a method here: this record is manifest
/// data shared with pack time, while matching belongs next to the resolver in
/// SigilBuild.Wrapper.Core/Localization. Core carries the map; Wrapper.Core
/// resolves it. See design §5.1.
/// </remarks>
public sealed record LocalizedText(IReadOnlyDictionary<string, string> Values)
{
    public static LocalizedText Plain(string value) =>
        new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = value });

    /// <summary>Backs SIG0290. Every runtime fallback bottoms out at `en`.</summary>
    public bool HasEnglish => Values.Keys.Any(k => string.Equals(k, "en", StringComparison.OrdinalIgnoreCase));

    /// <summary>English text, for pack-time diagnostics and tests. Empty when absent.</summary>
    public string English =>
        Values.FirstOrDefault(kv => string.Equals(kv.Key, "en", StringComparison.OrdinalIgnoreCase)).Value
        ?? string.Empty;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SigilBuild.Core.Tests -c Release --filter LocalizedTextTests`
Expected: PASS, 6 tests.

- [ ] **Step 5: Retype the records**

```csharp
// src/SigilBuild.Core/Manifest/InstallerScreen.cs — Title/Subtitle become LocalizedText
public sealed record InstallerScreen(
    string Id,
    LocalizedText Title,
    LocalizedText? Subtitle,
    string? When,
    IReadOnlyList<ScreenField> Fields);
```

```csharp
// src/SigilBuild.Core/Manifest/ParameterDefinition.cs — Description becomes LocalizedText
public sealed record ParameterDefinition(
    string Name,
    ParameterType Type,
    object? Default,
    IReadOnlyList<string>? EnumValues,
    bool InstallTime,
    LocalizedText? Description,
    string? Pattern,
    int? Min,
    int? Max,
    ParameterSource? Source = null,
    string? Screen = null);
```

Add `Language` to `InstallerSection` (follow the existing optional-parameter style; do **not** reorder existing parameters):

```csharp
    string? Language = null,
```

Fix every resulting compile error by wrapping plain strings in `LocalizedText.Plain(...)`. Compile errors are the worklist — do not suppress them.

- [ ] **Step 6: Schema, blob, parser diagnostics**

`schemas/sigil-schema.json` — add the definition:

```json
    "LocalizedText": {
      "description": "Either a plain string (treated as English) or a { \"en\": ..., \"uk\": ... } map. An `en` entry is required (SIG0290).",
      "oneOf": [
        { "type": "string", "minLength": 1 },
        {
          "type": "object",
          "minProperties": 1,
          "additionalProperties": { "type": "string" },
          "propertyNames": { "pattern": "^[A-Za-z]{2,3}(-[A-Za-z0-9]{1,8})*$" }
        }
      ]
    },
```

Retype `InstallerScreen.title`/`subtitle` and the parameter `description` to `{ "$ref": "#/definitions/LocalizedText" }`; retype `installer.license` the same way; add `installer.language` as `{ "type": "string" }`.

Leave the schema **permissive about `en`** — the parser diagnostic owns that rule, because it produces a better message than a `oneOf` "matches no subschema" error, and two enforcement points would drift.

Blob (`SerializableWrapperBlob.cs`): `SerializableInstallerScreen.Title`/`Subtitle` and `SerializableParameterDefinition.Description` become `Dictionary<string, string>`; add `Language` (`string?`). `Dictionary<string,string>` is **already registered** in `WrapperBlobJsonContext.cs:45`, so **no new `[JsonSerializable]` entries are needed**. Update `ToWrapperBlob` **and** `FromWrapperBlob` symmetrically.

Parser: emit `SIG0290` (Error) when a map lacks `en`; emit `SIG0291` (Error) when `installer.language` or any map key fails `LanguageTag.IsValid`.

- [ ] **Step 7: Test schema + diagnostics + round-trip**

```csharp
// Add to tests/SigilBuild.Core.Tests (screens/parser test file) and the blob round-trip suite.
[Fact]
public void PlainStringTitle_NormalizesToEnglish()
{
    var manifest = ParseManifest("installer:\n  screens:\n    - id: cfg\n      title: Configure\n      fields: []\n");
    manifest.Installer!.Screens![0].Title.Values["en"].Should().Be("Configure");
}

[Fact]
public void MapTitle_WithoutEnglish_EmitsSig0290_AsError()
{
    var diagnostics = ParseManifestDiagnostics(
        "installer:\n  screens:\n    - id: cfg\n      title:\n        uk: Налаштування\n      fields: []\n");

    diagnostics.Should().Contain(d =>
        d.Code == DiagnosticCodes.LocalizedTextMissingEnglish && d.Severity == DiagnosticSeverity.Error);
}

[Fact]
public void MapTitle_WithEnglish_IsAccepted()
{
    var manifest = ParseManifest(
        "installer:\n  screens:\n    - id: cfg\n      title:\n        en: Configure\n        uk: Налаштування\n      fields: []\n");

    manifest.Installer!.Screens![0].Title.Values.Should().HaveCount(2);
}

[Fact]
public void InvalidLanguageTag_EmitsSig0291()
{
    var diagnostics = ParseManifestDiagnostics("installer:\n  language: \"!!\"\n");
    diagnostics.Should().Contain(d => d.Code == DiagnosticCodes.InvalidLanguageTag);
}

[Fact]
public void Blob_RoundTrips_LocalizedFields()
{
    var blob = new SerializableWrapperBlob
    {
        Screens = new[]
        {
            new SerializableInstallerScreen
            {
                Id = "cfg",
                Title = new Dictionary<string, string> { ["en"] = "Configure", ["uk"] = "Налаштування" },
                Fields = Array.Empty<SerializableScreenField>(),
            },
        },
        Language = "uk",
    };

    var json = JsonSerializer.Serialize(blob, WrapperBlobJsonContext.Default.SerializableWrapperBlob);
    var back = JsonSerializer.Deserialize(json, WrapperBlobJsonContext.Default.SerializableWrapperBlob)!;

    back.Screens[0].Title["uk"].Should().Be("Налаштування");
    back.Language.Should().Be("uk");
}
```

Adapt `ParseManifest`/`ParseManifestDiagnostics` to the existing test helpers in that file — do not invent new harnesses.

- [ ] **Step 8: Run the full suite**

Run: `dotnet build Sigil.slnx -c Release && dotnet test Sigil.slnx -c Release`
Expected: green. The schema fixture tests (`SigilBuild.Schema.Tests`) must still validate the reference manifest.

- [ ] **Step 9: Commit**

```bash
git add src/SigilBuild.Core src/SigilBuild.Wrapper.Core/Json schemas/sigil-schema.json tests/
git commit -m "feat(p9): LocalizedText across records, schema and blob (SIG0290/SIG0291)"
```

---

### Task 9: License map at pack time (SIG0250 → SIG0290 ordering)

**Files:**
- Modify: `src/SigilBuild.Packaging/ExeWrapper/ExeWrapperPackager.cs:246,286-340` (`ReadLicenseText`)
- Modify: `src/SigilBuild.Wrapper.Core/Json/SerializableWrapperBlob.cs:79` (`LicenseText` → map)
- Modify: `src/SigilBuild.Wrapper.Core/Engine/WrapperBlob.cs:211`
- Modify: `tests/SigilBuild.Packaging.Tests/ExeWrapper/` (license tests)

**Interfaces:**
- Consumes: `LocalizedText` (Task 8).
- Produces: `SerializableWrapperBlob.LicenseText` becomes `Dictionary<string,string>?` — tag → **file contents** (not paths; files are read at pack time).

Per spec §5.3, ownership splits by **failure kind**, and the `en` invariant is asserted on the **post-read** map:

| Failure | Owner | Severity |
|---|---|---|
| An entry's file is missing/unreadable/empty | `SIG0250` | Non-fatal — that entry drops |
| The **resulting** map is non-empty but lacks `en` | `SIG0290` | **Fatal** |
| The resulting map is empty (all dropped) | *neither* | Screen omitted — existing T14 behavior |

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void License_PlainPath_ReadsAsEnglish()
{
    var dir = CreateFixture(("LICENSE.txt", "Example EULA."));
    var blob = Pack(dir, "installer:\n  license: LICENSE.txt\n", out var diagnostics);

    blob.LicenseText!["en"].Should().Be("Example EULA.");
    diagnostics.Should().BeEmpty();
}

[Fact]
public void License_Map_ReadsEachFile()
{
    var dir = CreateFixture(("LICENSE.txt", "Example EULA."), ("LICENSE.uk.txt", "Приклад ліцензії."));
    var blob = Pack(dir, "installer:\n  license:\n    en: LICENSE.txt\n    uk: LICENSE.uk.txt\n", out var diagnostics);

    blob.LicenseText!["en"].Should().Be("Example EULA.");
    blob.LicenseText!["uk"].Should().Be("Приклад ліцензії.");
    diagnostics.Should().BeEmpty();
}

// The composite case §5.3 exists to catch: without ordering, this packs a
// uk-only license and renders blank for everyone else.
[Fact]
public void License_UnreadableEnglish_Drops250_ThenFails290()
{
    var dir = CreateFixture(("LICENSE.uk.txt", "Приклад ліцензії."));
    Pack(dir, "installer:\n  license:\n    en: missing.txt\n    uk: LICENSE.uk.txt\n", out var diagnostics);

    diagnostics.Should().Contain(d => d.Code == DiagnosticCodes.LicenseFileUnreadable);
    diagnostics.Should().Contain(d =>
        d.Code == DiagnosticCodes.LocalizedTextMissingEnglish && d.Severity == DiagnosticSeverity.Error);
}

// T14's behavior must survive: nothing readable => screen omitted, no SIG0290.
[Fact]
public void License_AllEntriesUnreadable_OmitsScreen_WithoutSig0290()
{
    var dir = CreateFixture();
    var blob = Pack(dir, "installer:\n  license:\n    en: missing.txt\n    uk: also-missing.txt\n", out var diagnostics);

    blob.LicenseText.Should().BeNull();
    diagnostics.Should().Contain(d => d.Code == DiagnosticCodes.LicenseFileUnreadable);
    diagnostics.Should().NotContain(d => d.Code == DiagnosticCodes.LocalizedTextMissingEnglish);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SigilBuild.Packaging.Tests -c Release --filter License`
Expected: FAIL — `LicenseText` is still `string?`.

- [ ] **Step 3: Rewrite ReadLicenseText**

```csharp
    /// <summary>
    /// Reads each declared license file at pack time into a tag -> text map.
    /// Ownership (design §5.3): SIG0250 owns per-file readability and is non-fatal
    /// (the entry drops); SIG0290 owns the `en` invariant and is fatal. The
    /// invariant is asserted on the POST-READ map, so {en: missing, uk: ok} fails
    /// rather than silently packing a license only Ukrainian users can read.
    /// An empty result omits the screen — T14's original behavior, unchanged.
    /// </summary>
    private static Dictionary<string, string>? ReadLicenseText(
        LocalizedText? license,
        string sourceDirectory,
        IList<Diagnostic> diagnostics)
    {
        if (license is null)
        {
            return null;
        }

        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (tag, pathOrText) in license.Values)
        {
            var text = ReadOneLicense(pathOrText, sourceDirectory, tag, diagnostics);
            if (!string.IsNullOrWhiteSpace(text))
            {
                texts[tag] = text!;
            }
        }

        if (texts.Count == 0)
        {
            return null; // T14: no text -> no License screen. Not a SIG0290 case.
        }

        if (!texts.ContainsKey("en"))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticCodes.LocalizedTextMissingEnglish,
                DiagnosticSeverity.Error,
                $"installer.license has no readable 'en' entry (found: {string.Join(", ", texts.Keys)}). " +
                "Every localized value needs an English fallback — without it there is no defined " +
                "rendering for users whose language you do not ship."));
        }

        return texts;
    }
```

Keep the existing single-file read logic as `ReadOneLicense`, preserving its `SIG0250` emission verbatim. Update the call site at `:246` to pass the `LocalizedText?`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SigilBuild.Packaging.Tests -c Release --filter License`
Expected: PASS, 4 tests. Existing T14 license tests must also stay green.

- [ ] **Step 5: Commit**

```bash
git add src/SigilBuild.Packaging src/SigilBuild.Wrapper.Core tests/SigilBuild.Packaging.Tests
git commit -m "feat(p9): license map read at pack time with SIG0250/SIG0290 ownership split"
```

---

### Task 10: /lang flag + /? help

**Files:**
- Modify: `src/SigilBuild.Wrapper.Core/Cli/CommandLineParser.cs` (5 sites + 2 flag enumerations)
- Create: `src/SigilBuild.Wrapper.Core/Cli/HelpText.cs`
- Modify: `src/SigilBuild.Wrapper/Program.cs:12-16`
- Modify: `src/SigilBuild.Installer.Host/Program.cs`
- Create: `tests/SigilBuild.Wrapper.Tests/Cli/LangFlagTests.cs`

**Interfaces:**
- Consumes: `LanguageTag.IsValid` (Task 5).
- Produces: `ParsedCommandLine.Lang { get; init; }` (`string?`), `HelpText.Render() -> string`.

`ParsedCommandLine` is a **`sealed class` with `init` properties — not a record.** Five edit sites: the property (after `CloseApps`, ~`:133`), `AuditSafeRendering()` (emit `/lang=` between `/closeapps` at `:222-226` and the `Values` loop), the local `var` (~`:329`), a prefix-form parse branch modelled on `/D=` (`:396-408`), and the object initializer (`:470-485`).

No collision risk: `/launch` is a bare `string.Equals` and the `/LOG` branch tests `body[1] == 'O' || 'o'`, so `lang=` falls through.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SigilBuild.Wrapper.Tests/Cli/LangFlagTests.cs
using FluentAssertions;
using SigilBuild.Wrapper.Core.Cli;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Cli;

public class LangFlagTests
{
    [Fact]
    public void Lang_ParsesTag()
    {
        Parse("/lang=uk").Lang.Should().Be("uk");
    }

    [Fact]
    public void Lang_AcceptsRegionSubtag()
    {
        Parse("/lang=pt-BR").Lang.Should().Be("pt-BR");
    }

    [Fact]
    public void Lang_WellFormedButUnknown_IsAccepted_NotAnError()
    {
        // Sigil ships no `de` chrome, but a manifest may supply `de` screens.
        // Rejecting this would break design §4.4.
        Parse("/lang=de").Lang.Should().Be("de");
    }

    [Fact]
    public void Lang_Empty_IsUsageError()
    {
        var act = () => Parse("/lang=");
        act.Should().Throw<UsageException>().WithMessage("*requires a language tag*");
    }

    [Fact]
    public void Lang_Malformed_IsUsageError()
    {
        var act = () => Parse("/lang=!!");
        act.Should().Throw<UsageException>();
    }

    [Fact]
    public void Lang_Pseudo_IsUsageError_SoPseudoIsUnreachable()
    {
        var act = () => Parse("/lang=pseudo");
        act.Should().Throw<UsageException>("'pseudo' is 6 alpha chars; the grammar allows 2-3");
    }

    [Fact]
    public void Launch_StillParses_NoCollisionWithLang()
    {
        Parse("/launch").Launch.Should().BeTrue();
    }

    [Fact]
    public void AuditSafeRendering_IncludesLang()
    {
        Parse("/lang=uk").AuditSafeRendering().Should().Contain("/lang=uk");
    }

    // Design §6.2: SIG0291 and /lang are the same rule. This pins the two call
    // sites to one implementation — if someone re-implements either side, the
    // shared truth table below diverges and this fails.
    [Theory]
    [InlineData("uk", true)]
    [InlineData("pt-BR", true)]
    [InlineData("de", true)]
    [InlineData("!!", false)]
    [InlineData("pseudo", false)]
    [InlineData("e", false)]
    public void LangFlag_AcceptsExactlyWhatLanguageTagAccepts(string tag, bool valid)
    {
        LanguageTag.IsValid(tag).Should().Be(valid, "the validator is the shared source of truth");

        var act = () => Parse($"/lang={tag}");
        if (valid)
        {
            act.Should().NotThrow();
        }
        else
        {
            act.Should().Throw<UsageException>();
        }
    }

    [Fact]
    public void Help_ListsLang_AndDoesNotImplyManifestLanguagesAreLimited()
    {
        var help = HelpText.Render();
        help.Should().Contain("/lang=");
        help.Should().Contain("chrome ships in: en, uk");
        help.Should().Contain("manifest screens may supply any tag");
    }

    private static ParsedCommandLine Parse(params string[] args) =>
        CommandLineParser.Parse(args, TestBlob.WithNoRequiredParameters());
}
```

Use the existing test helper for building a blob in that suite — mirror how the current `CommandLineParser` tests construct one; do not invent `TestBlob` if an equivalent already exists.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SigilBuild.Wrapper.Tests -c Release --filter LangFlagTests`
Expected: FAIL — `Lang` property does not exist.

- [ ] **Step 3: Add the parse branch**

Insert after the `/LOG` branch (~`:435`), modelled on `/D=`:

```csharp
            // /lang=<tag> — prefix form, like /D=. No collision: /launch is matched by
            // string.Equals above, and the /LOG branch tests body[1] == 'O'/'o'.
            if (body.Length >= 4
                && (body[0] is 'l' or 'L')
                && (body[1] is 'a' or 'A')
                && (body[2] is 'n' or 'N')
                && (body[3] is 'g' or 'G'))
            {
                if (body.Length < 6 || body[4] != '=')
                {
                    throw new UsageException(
                        $"'/lang=' requires a language tag (offending token: '{rawArg}')");
                }

                var tag = body.Substring(5);
                if (!LanguageTag.IsValid(tag))
                {
                    throw new UsageException(
                        $"'{tag}' is not a valid language tag (offending token: '{rawArg}'). " +
                        "Expected a tag like en, uk, or pt-BR.");
                }

                lang = tag;
                continue;
            }
```

Declare `string? lang = null;` with the other locals (~`:329`), add `Lang = lang,` to the object initializer (`:470-485`), add the property:

```csharp
    /// <summary>
    /// Requested wizard language from /lang=&lt;tag&gt;. A fixed installer.language
    /// overrides this (design §2.1) — language is a display preference, so a
    /// conflict is logged and ignored rather than being a usage error like
    /// T12's fixed-scope vs /allusers.
    /// </summary>
    public string? Lang { get; init; }
```

Add to `AuditSafeRendering()` between `/closeapps` and the `Values` loop:

```csharp
        if (Lang is not null)
        {
            sb.Append(" /lang=").Append(Lang);
        }
```

Update **both** inline flag enumerations (`:341` and `:445`) to include `/lang=tag` — they each list the accepted flags and both are user-visible.

- [ ] **Step 4: Add the help text**

```csharp
// src/SigilBuild.Wrapper.Core/Cli/HelpText.cs
namespace SigilBuild.Wrapper.Core.Cli;

/// <summary>
/// The /? screen. Deliberately English (design D3): console output is the support
/// surface, and an admin grepping docs for "/lang=" should not get a translated
/// page. This is why CLI help does NOT flow through the localization catalog.
/// </summary>
public static class HelpText
{
    public static string Render() =>
        """
        Usage: Setup.exe [options]

          /silent, /S        install without the wizard
          /verysilent        install with no UI and no progress
          /Uninstall         uninstall
          /allusers          install for all users (elevates)
          /currentuser       install for the current user only
          /D=<path>          install directory
          /LOG[=<path>]      write an install log
          /lang=<tag>        force the wizard language
                             chrome ships in: en, uk
                             manifest screens may supply any tag
          /launch            launch the app when finished
          /closeapps         close blocking applications automatically
          /force-downgrade   allow installing over a newer version
          /PName=Value       set a declared parameter
          /?, /help          show this help

        Exit codes: 0 ok, 1 failed (rolled back), 2 cancelled, 3 downgrade blocked,
        4 files in use, 5 already running, 64 usage error, 3010 reboot required.
        """;
}
```

Route it in `src/SigilBuild.Wrapper/Program.cs` **before** the parser (the closed grammar would otherwise reject it), beside the existing `--version` bypass at `:12-16`:

```csharp
        if (args.Length == 1 && (args[0] == "/?" || args[0].Equals("/help", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine(HelpText.Render());
            return 0;
        }
```

Do the same in `src/SigilBuild.Installer.Host/Program.cs`, before Avalonia starts.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/SigilBuild.Wrapper.Tests -c Release --filter LangFlagTests`
Expected: PASS, 9 tests.

- [ ] **Step 6: Commit**

```bash
git add src/SigilBuild.Wrapper.Core/Cli src/SigilBuild.Wrapper/Program.cs src/SigilBuild.Installer.Host/Program.cs tests/SigilBuild.Wrapper.Tests/Cli
git commit -m "feat(p9): /lang=<tag> flag + English /? help screen"
```

---

### Task 11: Author the full catalog (en + uk)

**Files:**
- Modify: `src/SigilBuild.Wrapper.Core/Localization/Strings.en.txt`
- Modify: `src/SigilBuild.Wrapper.Core/Localization/Strings.uk.txt`

This task adds **only** catalog content — no call sites change. It is separated so a reviewer can gate translation quality independently of the mechanical migration in Tasks 12–14.

The key table below is the complete inventory of user-facing chrome, derived from a full sweep of `src/SigilBuild.Installer.Host` and the engine's prose messages. Duplicates in the source have been collapsed to single keys (`browse`, `choose_install_location`, `options_load_failed`, `launch_app`, brand fallbacks).

- [ ] **Step 1: Write Strings.en.txt**

```text
# src/SigilBuild.Wrapper.Core/Localization/Strings.en.txt
# Baseline catalog — the source of truth for every user-facing chrome string.
# See docs/plan/feature-parity/P9-DESIGN-localization.md §3.
#
# RULE (§3.1): a count may appear in a value, but must never inflect the
# sentence around it. "Applications to close: {count}" is allowed;
# "{count} applications must be closed" is not — uk has three plural forms and
# concatenation cannot select between them.
#
# NOT in this catalog: log output, journal lines, developer exception messages,
# per-step failure detail (design D2), and CLI help (design D3).
#
# Reviewer: n/a (source language)

# --- navigation / chrome ---
nav.back = Back
nav.next = Next
nav.cancel = Cancel
nav.close = Close
nav.retry = Retry
nav.browse = Browse…

# --- rail labels ---
rail.welcome = Welcome
rail.license = License
rail.location = Location
rail.options = Options
rail.install = Install
rail.configure = Configure
rail.finish = Finish
rail.failed = Failed
rail.close_apps = Close apps
rail.downgrade_blocked = Blocked

# --- welcome ---
welcome.title = Welcome to {appName} setup
welcome.body = This wizard will guide you through installation. Click Next to continue.

# --- license ---
license.title = License agreement
license.accept = I accept the terms

# --- install location ---
location.title = Choose install location
location.body = Where should the application be installed?
location.scope.user = Just for me (recommended)
location.scope.user_hint = No administrator permission needed
location.scope.machine = All users of this computer
location.scope.machine_hint = Installs to Program Files — Windows will ask for permission

# --- install location validation ---
location.error.empty = Enter an install location.
location.error.not_absolute = Enter an absolute path (for example C:\Program Files\App).
location.error.is_file = That location is a file. Choose a folder.
location.error.denied = You don't have permission to install there. Choose another folder.

# --- options ---
options.title = Options
options.body = Choose what to set up during installation.
options.desktop_shortcut = Create a desktop shortcut
options.start_menu = Add a Start menu shortcut
options.add_to_path = Add to PATH
options.file_associations = Register file associations

# --- installing / finish ---
installing.title = Installing…
finish.title = {appName} is installed
finish.body = You can now launch the application from the Start menu.
finish.launch_app = Launch {appName}
finish.reboot_notice = A restart is required to finish setting up a prerequisite. Please restart your computer.

# --- failed ---
failed.title = Installation failed
failed.body = Setup could not complete. No changes were kept.
failed.open_log = Open log

# --- cancel dialog ---
cancel.title = Cancel Installation
cancel.body = Cancel now? Files already copied will be removed during rollback.
cancel.confirm = Yes, cancel
cancel.resume = Continue installing

# --- close apps ---
close_apps.title = Close these applications to continue
close_apps.body = Setup needs to update files that these applications are using. Close them and choose Retry, or let Setup close them for you.
close_apps.close_for_me = Close for me
close_apps.close_for_me_hint = Close for me asks each application to shut down normally — unsaved work is not discarded. Applications are not restarted afterwards.

# --- upgrade / downgrade ---
upgrade.reinstall_notice = {appName} is already installed. Continuing will reinstall it — the current version is removed first.
upgrade.upgrading = Upgrading {appName} from {fromVersion} to {toVersion}.
upgrade.replacing_newer = Replacing {appName} {fromVersion} with the older version {toVersion}.
downgrade.title = A newer version is already installed
downgrade.body = A newer version ({installedVersion}) of {appName} is already installed. Setup will not replace it with the older version {incomingVersion}. Close this window, then uninstall the current version first if you really want to downgrade.

# --- uninstall ---
uninstall.version = Version {version}
uninstall.title = Uninstall {appName}
uninstall.body = This removes {appName}, its Start-menu entry, desktop shortcut, and PATH entry. Your documents are not affected.
uninstall.action = Uninstall
uninstall.progress = Uninstalling…
uninstall.done = {appName} was removed
uninstall.failed = Uninstall failed
uninstall.failed_body = Some components may not have been removed. You can try again from Add or Remove Programs.

# --- fields ---
field.show = Show
field.hide = Hide
field.options_loading = Loading options…
field.options_load_failed = Couldn't load options.
field.error.required = {label} is required.
field.error.choose = Choose a {label}.
field.error.invalid_choice = '{value}' is not a valid choice.
field.error.not_integer = {label} must be a whole number.
field.error.min = {label} must be at least {min}.
field.error.max = {label} must be at most {max}.
field.error.pattern = {label} is not in the expected format.

# --- single instance (pre-Avalonia MessageBox) ---
already_running.caption = Setup
already_running.body = Setup is already running.\n\nAnother copy of this installer is in progress. Finish or close it, then try again.

# --- brand fallbacks (un-stamped build) ---
brand.app_fallback = Application
brand.publisher_fallback = Publisher

# --- engine prose (design D2 — prose only; step failure detail stays English) ---
engine.removing_previous = Removing previous version
engine.removing_newer = Removing newer version
engine.installing_prerequisite = Installing {name}…
engine.update_unsupported = /Update is not supported by this installer: update_steps run via the delta-update SDK, not the setup runtime.
```

- [ ] **Step 2: Write Strings.uk.txt**

```text
# src/SigilBuild.Wrapper.Core/Localization/Strings.uk.txt
# Ukrainian (uk).
# Reviewer: Yevhen Khudoliiv — native speaker, reviewed 2026-07-15.
# ADR-008 §4: a language ships only with a named reviewer recorded here.

# --- navigation / chrome ---
nav.back = Назад
nav.next = Далі
nav.cancel = Скасувати
nav.close = Закрити
nav.retry = Повторити
nav.browse = Огляд…

# --- rail labels ---
rail.welcome = Вітаємо
rail.license = Ліцензія
rail.location = Розташування
rail.options = Параметри
rail.install = Встановлення
rail.configure = Налаштування
rail.finish = Готово
rail.failed = Помилка
rail.close_apps = Закрити програми
rail.downgrade_blocked = Заблоковано

# --- welcome ---
welcome.title = Ласкаво просимо до встановлення {appName}
welcome.body = Цей майстер допоможе вам виконати встановлення. Натисніть «Далі», щоб продовжити.

# --- license ---
license.title = Ліцензійна угода
license.accept = Я приймаю умови

# --- install location ---
location.title = Виберіть розташування для встановлення
location.body = Куди слід встановити програму?
location.scope.user = Лише для мене (рекомендовано)
location.scope.user_hint = Права адміністратора не потрібні
location.scope.machine = Для всіх користувачів цього комп'ютера
location.scope.machine_hint = Встановлюється до Program Files — Windows запитає дозвіл

# --- install location validation ---
location.error.empty = Укажіть розташування для встановлення.
location.error.not_absolute = Укажіть абсолютний шлях (наприклад, C:\Program Files\App).
location.error.is_file = Це розташування є файлом. Виберіть папку.
location.error.denied = У вас немає дозволу на встановлення в цю папку. Виберіть іншу.

# --- options ---
options.title = Параметри
options.body = Виберіть, що налаштувати під час встановлення.
options.desktop_shortcut = Створити ярлик на робочому столі
options.start_menu = Додати ярлик у меню «Пуск»
options.add_to_path = Додати до PATH
options.file_associations = Зареєструвати асоціації файлів

# --- installing / finish ---
installing.title = Встановлення…
finish.title = {appName} встановлено
finish.body = Тепер ви можете запустити програму з меню «Пуск».
finish.launch_app = Запустити {appName}
finish.reboot_notice = Щоб завершити налаштування необхідного компонента, потрібно перезавантажити комп'ютер.

# --- failed ---
failed.title = Помилка встановлення
failed.body = Не вдалося завершити встановлення. Жодних змін не збережено.
failed.open_log = Відкрити журнал

# --- cancel dialog ---
cancel.title = Скасувати встановлення
cancel.body = Скасувати зараз? Уже скопійовані файли буде вилучено під час відкату.
cancel.confirm = Так, скасувати
cancel.resume = Продовжити встановлення

# --- close apps ---
close_apps.title = Закрийте ці програми, щоб продовжити
close_apps.body = Програмі встановлення потрібно оновити файли, які використовують ці програми. Закрийте їх і натисніть «Повторити» або дозвольте закрити їх автоматично.
close_apps.close_for_me = Закрити за мене
close_apps.close_for_me_hint = «Закрити за мене» просить кожну програму завершити роботу штатно — незбережені дані не втрачаються. Програми не запускаються повторно.

# --- upgrade / downgrade ---
upgrade.reinstall_notice = {appName} уже встановлено. Продовження перевстановить програму — поточну версію буде вилучено першою.
upgrade.upgrading = Оновлення {appName} з версії {fromVersion} до {toVersion}.
upgrade.replacing_newer = Заміна {appName} {fromVersion} на старішу версію {toVersion}.
downgrade.title = Уже встановлено новішу версію
downgrade.body = Уже встановлено новішу версію ({installedVersion}) програми {appName}. Програма встановлення не замінить її старішою версією {incomingVersion}. Закрийте це вікно, а потім спочатку вилучіть поточну версію, якщо ви справді хочете перейти на старішу.

# --- uninstall ---
uninstall.version = Версія {version}
uninstall.title = Вилучити {appName}
uninstall.body = Це вилучить {appName}, її запис у меню «Пуск», ярлик на робочому столі та запис у PATH. Ваші документи не буде змінено.
uninstall.action = Вилучити
uninstall.progress = Вилучення…
uninstall.done = {appName} вилучено
uninstall.failed = Помилка вилучення
uninstall.failed_body = Деякі компоненти могло бути не вилучено. Ви можете спробувати ще раз через «Програми та засоби».

# --- fields ---
field.show = Показати
field.hide = Приховати
field.options_loading = Завантаження параметрів…
field.options_load_failed = Не вдалося завантажити параметри.
field.error.required = Поле «{label}» є обов'язковим.
field.error.choose = Виберіть значення для «{label}».
field.error.invalid_choice = «{value}» не є припустимим варіантом.
field.error.not_integer = Поле «{label}» має бути цілим числом.
field.error.min = Значення поля «{label}» має бути не менше {min}.
field.error.max = Значення поля «{label}» має бути не більше {max}.
field.error.pattern = Поле «{label}» має неправильний формат.

# --- single instance (pre-Avalonia MessageBox) ---
already_running.caption = Програма встановлення
already_running.body = Програму встановлення вже запущено.\n\nІнша копія цієї програми встановлення вже виконується. Завершіть або закрийте її, а потім спробуйте ще раз.

# --- brand fallbacks (un-stamped build) ---
brand.app_fallback = Програма
brand.publisher_fallback = Видавець

# --- engine prose ---
engine.removing_previous = Вилучення попередньої версії
engine.removing_newer = Вилучення новішої версії
engine.installing_prerequisite = Встановлення {name}…
engine.update_unsupported = /Update не підтримується цією програмою встановлення: update_steps виконуються через SDK дельта-оновлень, а не через середовище встановлення.
```

- [ ] **Step 3: Verify the generator accepts both catalogs**

Run: `dotnet build src/SigilBuild.Wrapper.Core -c Release`
Expected: **clean**. Any `SIGLOC001`–`SIGLOC005` is a real defect in the catalogs — fix the catalog, never suppress the diagnostic. `SIGLOC003` means a Ukrainian value dropped or invented a placeholder relative to English.

- [ ] **Step 4: Commit**

```bash
git add src/SigilBuild.Wrapper.Core/Localization
git commit -m "feat(p9): full en + uk chrome catalog"
```

---

### Task 12: Migrate XAML literals

**Files:**
- Modify: `src/SigilBuild.Installer.Host/Views/InstallerWindow.axaml:56-58`
- Modify: `src/SigilBuild.Installer.Host/Views/CancelConfirmDialog.axaml:4,11,13,16,17`
- Modify: `src/SigilBuild.Installer.Host/Views/UninstallWindow.axaml:24,40,80,82,91,92,95`
- Modify: `src/SigilBuild.Installer.Host/Views/Screens/WelcomeView.axaml:8,10`
- Modify: `src/SigilBuild.Installer.Host/Views/Screens/LicenseView.axaml:7,11`
- Modify: `src/SigilBuild.Installer.Host/Views/Screens/InstallOptionsView.axaml:11,13,24,38,40,45,46`
- Modify: `src/SigilBuild.Installer.Host/Views/Screens/InstallingView.axaml:7`
- Modify: `src/SigilBuild.Installer.Host/Views/Screens/FinishView.axaml:8,9`
- Modify: `src/SigilBuild.Installer.Host/Views/Screens/FailedView.axaml:7,8,10`
- Modify: `src/SigilBuild.Installer.Host/Views/Screens/CloseAppsView.axaml:7,9,25,26,30`
- Modify: `src/SigilBuild.Installer.Host/Views/Screens/DowngradeBlockedView.axaml:12`
- Modify: `src/SigilBuild.Installer.Host/Views/Screens/OptionsView.axaml:11,13`

**Interfaces:**
- Consumes: generated `S` static accessor (Task 4), catalog keys (Task 11).

Two mechanisms, by whether the string takes an argument:

- **Static literals → `{x:Static}`.** Compile-time resolved by Avalonia's XAML compiler; no binding, no `INotifyPropertyChanged`, AOT-clean. Legal because the session language is immutable (design §3.2).
- **The 3 `StringFormat` compositions** (`WelcomeView:8`, `FinishView:8`, `UninstallWindow:24`) cannot use `{x:Static}` — they need an argument — so they move to VM properties in Task 13. Leave them for now and note them.

**Do not touch** glyphs: `🔒` (`InstallerWindow:25`), `✓` (`UninstallWindow:71`), `•` (`WizardField:323`), `••••••••` (`CustomView:268`). They are not catalog entries.

- [ ] **Step 1: Add the namespace + convert one file**

In each `.axaml` root element add:

```xml
xmlns:loc="clr-namespace:SigilBuild.Wrapper.Core.Localization;assembly=SigilBuild.Wrapper.Core"
```

`InstallerWindow.axaml:56-58`:

```xml
        <Button Content="{x:Static loc:S.NavBack}" ... />
        <Button Content="{x:Static loc:S.NavNext}" ... />
        <Button Content="{x:Static loc:S.NavCancel}" ... />
```

- [ ] **Step 2: Verify it compiles and renders**

Run: `dotnet build src/SigilBuild.Installer.Host -c Release`
Expected: clean. If Avalonia's XAML compiler cannot resolve `S`, the cause is a missing/incorrect `assembly=` in the `xmlns:loc` — do not fall back to a binding.

- [ ] **Step 3: Convert the remaining 11 files**

Apply the same pattern. Mapping (source literal → key):

| File:line | Literal | Key |
|---|---|---|
| `CancelConfirmDialog:4,11` | Cancel Installation | `S.CancelTitle` |
| `CancelConfirmDialog:13` | Cancel now? … | `S.CancelBody` |
| `CancelConfirmDialog:16` | Yes, cancel | `S.CancelConfirm` |
| `CancelConfirmDialog:17` | Continue installing | `S.CancelResume` |
| `UninstallWindow:40` | Uninstalling… | `S.UninstallProgress` |
| `UninstallWindow:80` | Uninstall failed | `S.UninstallFailed` |
| `UninstallWindow:82` | Some components may not… | `S.UninstallFailedBody` |
| `UninstallWindow:91` | Cancel | `S.NavCancel` |
| `UninstallWindow:92` | Uninstall | `S.UninstallAction` |
| `UninstallWindow:95` | Close | `S.NavClose` |
| `WelcomeView:10` | This wizard will guide… | `S.WelcomeBody` |
| `LicenseView:7` | License agreement | `S.LicenseTitle` |
| `LicenseView:11` | I accept the terms | `S.LicenseAccept` |
| `InstallOptionsView:11` | Choose install location | `S.LocationTitle` |
| `InstallOptionsView:13` | Where should the application… | `S.LocationBody` |
| `InstallOptionsView:24` | Browse… | `S.NavBrowse` |
| `InstallOptionsView:38` | Just for me (recommended) | `S.LocationScopeUser` |
| `InstallOptionsView:40` | No administrator permission needed | `S.LocationScopeUserHint` |
| `InstallOptionsView:45` | All users of this computer | `S.LocationScopeMachine` |
| `InstallOptionsView:46` | Installs to Program Files… | `S.LocationScopeMachineHint` |
| `InstallingView:7` | Installing… | `S.InstallingTitle` |
| `FinishView:9` | You can now launch… | `S.FinishBody` |
| `FailedView:7` | Installation failed | `S.FailedTitle` |
| `FailedView:8` | Setup could not complete… | `S.FailedBody` |
| `FailedView:10` | Open log | `S.FailedOpenLog` |
| `CloseAppsView:7` | Close these applications… | `S.CloseAppsTitle` |
| `CloseAppsView:9` | Setup needs to update files… | `S.CloseAppsBody` |
| `CloseAppsView:25` | Retry | `S.NavRetry` |
| `CloseAppsView:26` | Close for me | `S.CloseAppsCloseForMe` |
| `CloseAppsView:30` | Close for me asks each… | `S.CloseAppsCloseForMeHint` |
| `DowngradeBlockedView:12` | A newer version is already installed | `S.DowngradeTitle` |
| `OptionsView:11` | Options | `S.OptionsTitle` |
| `OptionsView:13` | Choose what to set up… | `S.OptionsBody` |

- [ ] **Step 4: Run the host suite**

Run: `dotnet test tests/SigilBuild.Installer.Host.Tests -c Release`
Expected: green. Snapshot tests may need re-baselining — inspect each diff and confirm it is a text-source change only, not a layout change.

- [ ] **Step 5: Commit**

```bash
git add src/SigilBuild.Installer.Host/Views
git commit -m "feat(p9): route XAML chrome through the catalog via {x:Static}"
```

---

### Task 13: Migrate ViewModels and code-behind

**Files:**
- Modify: `src/SigilBuild.Installer.Host/ViewModels/InstallerViewModel.cs` (142, 236, 238, 250-252, 476, 505, 535, 674, 679, 684, 689, 1021-1030, 1143)
- Modify: `src/SigilBuild.Installer.Host/ViewModels/UninstallViewModel.cs:73,75,77`
- Modify: `src/SigilBuild.Installer.Host/ViewModels/WizardField.cs` (272, 284, 324, 364, 370, 393, 403, 408, 413, 434, 531-538)
- Modify: `src/SigilBuild.Installer.Host/Views/Screens/CustomView.axaml.cs:236,286`
- Modify: `src/SigilBuild.Installer.Host/Views/Screens/InstallOptionsView.axaml.cs:37`
- Modify: `src/SigilBuild.Installer.Host/App.axaml.cs:167-168`
- Modify: `src/SigilBuild.Installer.Host/Branding/BrandTokens.cs:14-16`
- Modify: `src/SigilBuild.Installer.Host/Views/Screens/WelcomeView.axaml:8`, `FinishView.axaml:8`, `UninstallWindow.axaml:24`

**Interfaces:**
- Consumes: `Strings.X(lang, ...)` (Task 2), `SessionLanguage.Current` (Task 4).

Three defects get folded in here, because they are the same defect as a hardcoded string:

1. **Enum/id leaks.** `InstallerViewModel:1028-1029` renders `node.Screen?.Id` and `node.Step.ToString()` into the rail. Replace with real keys — every `InstallerStep` maps to a `rail.*` key.
2. **Duplicate pairs collapse.** `"Browse…"`, `"Choose install location"`, `"Couldn't load options."`, `"Launch application"`, `"Application"`/`"Publisher"` each become one key.
3. **The 3-part concatenated downgrade notice** (`:250-252`) becomes **one** key (`downgrade.body`), not three. Sentence fragments are not independently translatable — word order differs per language.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SigilBuild.Installer.Host.Tests/Localization/ViewModelLocalizationTests.cs
using FluentAssertions;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Wrapper.Core.Localization;
using Xunit;

namespace SigilBuild.Installer.Host.Tests.Localization;

[Collection("SessionLanguage")]
public class ViewModelLocalizationTests
{
    [Fact]
    public void InstallPathError_IsLocalized()
    {
        SessionLanguage.SetForTesting(Lang.Uk);
        var vm = new InstallerViewModel(new BrandTokens()) { InstallPath = string.Empty };

        vm.ValidateDestination().Should().BeFalse();

        vm.InstallPathError.Should().Be("Укажіть розташування для встановлення.");
    }

    [Fact]
    public void RailLabels_NeverLeakEnumNamesOrScreenIds()
    {
        SessionLanguage.SetForTesting(Lang.En);
        var vm = new InstallerViewModel(new BrandTokens());

        vm.RailSteps.Should().NotContain(s => s.Label == nameof(InstallerStep.Finish));
        vm.RailSteps.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.Label));
    }

    [Fact]
    public void DowngradeNotice_IsOneKey_NotThreeFragments()
    {
        SessionLanguage.SetForTesting(Lang.Uk);
        var text = Strings.DowngradeBody(Lang.Uk, "2.1.0", "Acme", "1.0.0");

        text.Should().StartWith("Уже встановлено новішу версію");
        text.Should().Contain("Acme");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SigilBuild.Installer.Host.Tests -c Release --filter ViewModelLocalizationTests`
Expected: FAIL — `InstallPathError` is still the English literal.

- [ ] **Step 3: Migrate the ViewModels**

Verified member names — use these exactly, they are **not** what you might guess:
`ValidateDestination()` (not `ValidateInstallPath`), `RailSteps` of `RailStep`
with a `Label` property (not `RailItems`), `InstallPathError`, `UpgradeNotice`,
`LoadLicense`, `CurrentStep`, `InstallPath`. There is **no** `NextLabel` — the
Next button's caption lives in XAML and is handled by Task 12.

Pattern — add a `Lang` field and route every literal:

```csharp
    private readonly Lang _lang = SessionLanguage.Current;
```

```csharp
    // InstallerViewModel.cs:674-689, inside ValidateDestination()
    InstallPathError = Strings.LocationErrorEmpty(_lang);
    InstallPathError = Strings.LocationErrorNotAbsolute(_lang);
    InstallPathError = Strings.LocationErrorIsFile(_lang);
    InstallPathError = Strings.LocationErrorDenied(_lang);
```

```csharp
    // InstallerViewModel.cs:250-252 — three fragments collapse to one key
    UpgradeNotice = Strings.DowngradeBody(_lang, _installedVersion, Brand.AppName, Brand.AppVersion);
```

```csharp
    // InstallerViewModel.cs RebuildRail() ~:1021-1032 — rail labels.
    // Replaces both leaks: `node.Screen?.Id ?? "Configure"` and `_ => node.Step.ToString()`.
    var label = node.Step switch
    {
        InstallerStep.Welcome => Strings.RailWelcome(_lang),
        InstallerStep.License => Strings.RailLicense(_lang),
        InstallerStep.InstallOptions => Strings.RailLocation(_lang),
        InstallerStep.Options => Strings.RailOptions(_lang),
        InstallerStep.Installing => Strings.RailInstall(_lang),
        InstallerStep.Custom => Strings.RailConfigure(_lang),
        InstallerStep.Finish => Strings.RailFinish(_lang),
        InstallerStep.Failed => Strings.RailFailed(_lang),
        InstallerStep.CloseApps => Strings.RailCloseApps(_lang),
        InstallerStep.DowngradeBlocked => Strings.RailDowngradeBlocked(_lang),
        _ => Strings.RailConfigure(_lang),
    };
```

A **declared** screen's rail label should prefer the manifest's resolved title over `rail.configure`; resolve it with `LanguageResolver.Match` against the screen's `Title` map (Task 7) rather than rendering the raw `Screen.Id`.

The 3 XAML `StringFormat` cases become VM properties:

```csharp
    // InstallerViewModel — replaces WelcomeView.axaml:8's StringFormat
    public string WelcomeTitle => Strings.WelcomeTitle(_lang, Brand.AppName);

    // replaces FinishView.axaml:8's StringFormat
    public string FinishTitle => Strings.FinishTitle(_lang, Brand.AppName);
```

```xml
<!-- WelcomeView.axaml:8 -->
<TextBlock Text="{Binding WelcomeTitle}" ... />
<!-- FinishView.axaml:8 -->
<TextBlock Text="{Binding FinishTitle}" ... />
<!-- UninstallWindow.axaml:24 -->
<TextBlock Text="{Binding VersionLine}" ... />
```

Apply the same treatment to `UninstallViewModel:73,75,77`, `WizardField` (272/284 → one `field.options_load_failed`; 324 → `field.show`/`field.hide`; 364-434 → the `field.error.*` family; 531-538 → the `options.*` component captions, with the unknown-component `_ => name` fallback **kept** — author-supplied component labels are P10's job), `CustomView.axaml.cs:236,286`, `InstallOptionsView.axaml.cs:37`, and the brand fallbacks at `App.axaml.cs:167-168` + `BrandTokens.cs:14-16` (both sites, one key each).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SigilBuild.Installer.Host.Tests -c Release`
Expected: green.

- [ ] **Step 5: Commit**

```bash
git add src/SigilBuild.Installer.Host
git commit -m "feat(p9): route ViewModel + code-behind chrome through the catalog"
```

---

### Task 14: Migrate engine prose + wire resolution at session start

**Files:**
- Modify: `src/SigilBuild.Wrapper.Core/Engine/InstallSession.cs` (329-330, 490-496, 742, 1044-1055, 1066)
- Modify: `src/SigilBuild.Wrapper.Core/Engine/PrerequisiteRunner.cs:107`
- Modify: `src/SigilBuild.Installer.Host/Program.cs`
- Modify: `src/SigilBuild.Wrapper/Program.cs`
- Modify: `src/SigilBuild.Wrapper.Core/Expressions/` (expression context seeding — `system.language`)
- Create: `tests/SigilBuild.Wrapper.Tests/Localization/SessionResolutionTests.cs`

**Interfaces:**
- Consumes: `LanguageResolver` (Task 7), `SessionLanguage` (Task 4), `ParsedCommandLine.Lang` (Task 10), `SerializableWrapperBlob.Language` (Task 8).

Per spec §4.6, resolution runs **once, at session start**, after the blob loads and **before any UI is constructed** — including the pre-Avalonia single-instance `MessageBoxW`, which is itself a catalog string. The resolver depends only on the blob and Win32, so this ordering is available.

**Only the 6 prose messages move** (design D2). Everything in `Steps/` and every `_log?.WriteLine` stays English. The tell is already consistent in the codebase: lowercase-prefixed `"noun: detail"` is log convention; sentence-cased is user-facing.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SigilBuild.Wrapper.Tests/Localization/SessionResolutionTests.cs
using FluentAssertions;
using SigilBuild.Wrapper.Core.Localization;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Localization;

[Collection("SessionLanguage")]
public class SessionResolutionTests
{
    [Fact]
    public void FixedManifestLanguage_OverridesLangFlag_WithoutFailing()
    {
        // Design §2.1: language is a display preference, not a trust boundary,
        // so this does NOT mirror T12's fixed-scope vs /allusers exit 64.
        var prefs = LanguageResolver.Preferences(manifestLanguage: "en", langFlag: "uk", osPreferences: new[] { "uk-UA" });

        LanguageResolver.MatchChrome(prefs).Should().Be(Lang.En);
    }

    [Fact]
    public void SystemLanguage_IsChromeLanguage_NotTopOsPreference()
    {
        // Design §4.3: with [de-DE, uk-UA] and en+uk chrome, locale() says de-DE
        // but system.language says uk, because uk is what the UI renders.
        var prefs = LanguageResolver.Preferences(null, null, new[] { "de-DE", "uk-UA" });

        LanguageResolver.MatchChrome(prefs).Should().Be(Lang.Uk);
    }

    [Fact]
    public void EngineProse_IsLocalized()
    {
        Strings.EngineRemovingPrevious(Lang.Uk).Should().Be("Вилучення попередньої версії");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SigilBuild.Wrapper.Tests -c Release --filter SessionResolutionTests`
Expected: FAIL.

- [ ] **Step 3: Wire resolution at session start**

In both `Program.cs` entries, immediately after the blob loads and **before** any UI (including the single-instance `MessageBoxW`):

```csharp
        var preferences = LanguageResolver.Preferences(
            blob.Language, parsed.Lang, OsUiLanguage.Preferences());
        var chrome = LanguageResolver.MatchChrome(preferences);
        SessionLanguage.Set(chrome);

        if (blob.Language is not null && parsed.Lang is not null
            && !string.Equals(blob.Language, parsed.Lang, StringComparison.OrdinalIgnoreCase))
        {
            // Design §2.1: the manifest pin wins; the flag is ignored, not fatal.
            log.Info($"language: manifest pin '{blob.Language}' overrides /lang={parsed.Lang}");
        }
```

Seed `system.language` into the expression context wherever `system.*` is seeded today (`StepContext`), using the resolved chrome language's tag.

- [ ] **Step 4: Migrate the 6 prose messages**

```csharp
    // InstallSession.cs:1066 — was composed in the engine; now a catalog key.
    public string LaunchLabel =>
        Strings.FinishLaunchApp(SessionLanguage.Current,
            _blob.AppName ?? _blob.DisplayName ?? Strings.BrandAppFallback(SessionLanguage.Current));
```

Same for `:490-496` (`downgrade.body`), `:1044-1055` (close-apps blocker), `:742` (`engine.removing_previous` / `engine.removing_newer`), `:329-330` (`engine.update_unsupported`), and `PrerequisiteRunner.cs:107` (`engine.installing_prerequisite`).

Delete the now-dead `"Launch application"` fallback at `InstallerViewModel.cs:505` — `finish.launch_app` is the single source.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test Sigil.slnx -c Release`
Expected: green.

- [ ] **Step 6: Commit**

```bash
git add src/SigilBuild.Wrapper.Core src/SigilBuild.Wrapper src/SigilBuild.Installer.Host tests/
git commit -m "feat(p9): resolve language at session start + localize engine prose"
```

---

### Task 15: The zero-hardcoded-strings tests

**Files:**
- Create: `tests/SigilBuild.Installer.Host.Tests/Localization/NoHardcodedStringsTests.cs`
- Create: `tests/SigilBuild.Installer.Host.Tests/Localization/PseudoLocRenderTests.cs`

Per spec §8.1, **two** mechanisms, because neither alone is honest: the render pass can only assert screens it can *reach* (`Failed`, `CloseApps`, `DowngradeBlocked` each need specific state), and the static scan cannot see composed VM strings.

- [ ] **Step 1: Write the static scan**

```csharp
// tests/SigilBuild.Installer.Host.Tests/Localization/NoHardcodedStringsTests.cs
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace SigilBuild.Installer.Host.Tests.Localization;

public class NoHardcodedStringsTests
{
    // ToolTip.Tip and AutomationProperties.* are here preventively: zero exist
    // today, and they are user-facing chrome the pseudo-loc render walk would
    // NOT catch, because a visual-tree text sweep never reads them.
    private static readonly string[] Attributes =
    {
        "Text", "Content", "Watermark", "ToolTip.Tip",
        "AutomationProperties.Name", "AutomationProperties.HelpText",
    };

    // Kept deliberately tiny; the size assertion below stops it becoming a loophole.
    private static readonly string[] Allowlist =
    {
        "\U0001F512", // 🔒 trust glyph
        "✓",
        "•",
        "••••••••",
        "(no view)", // ScreenSelector unreachable default
    };

    [Fact]
    public void NoXamlAttribute_ContainsALiteralWithLetters()
    {
        var offenders = EnumerateXaml()
            .SelectMany(file => Regex
                .Matches(File.ReadAllText(file), $"({string.Join("|", Attributes.Select(Regex.Escape))})=\"([^\"]*)\"")
                .Select(m => (File: Path.GetFileName(file), Value: m.Groups[2].Value)))
            .Where(x => Regex.IsMatch(x.Value, "[A-Za-z]"))
            .Where(x => !x.Value.StartsWith("{", StringComparison.Ordinal)) // bindings / x:Static
            .Where(x => !Allowlist.Contains(x.Value))
            .ToArray();

        offenders.Should().BeEmpty(
            "every user-facing string must flow through the catalog; found: " +
            string.Join(", ", offenders.Select(o => $"{o.File}: \"{o.Value}\"")));
    }

    [Fact]
    public void Allowlist_HasNotGrown()
    {
        // If this fails, someone widened the escape hatch. Justify it in review;
        // do not bump the number reflexively.
        Allowlist.Should().HaveCount(5);
    }

    private static string[] EnumerateXaml()
    {
        var root = FindHostProject();
        return Directory.GetFiles(Path.Combine(root, "Views"), "*.axaml", SearchOption.AllDirectories);
    }

    private static string FindHostProject()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "SigilBuild.Installer.Host")))
        {
            dir = dir.Parent;
        }
        dir.Should().NotBeNull("the test must be able to locate the repo root");
        return Path.Combine(dir!.FullName, "src", "SigilBuild.Installer.Host");
    }
}
```

- [ ] **Step 2: Run it — expect real failures**

Run: `dotnet test tests/SigilBuild.Installer.Host.Tests -c Release --filter NoHardcodedStringsTests`
Expected: PASS if Task 12 is complete. **If it fails, the listed offenders are genuine misses** — fix them in the XAML, do not extend the allowlist.

- [ ] **Step 3: Write the pseudo-loc render test**

```csharp
// tests/SigilBuild.Installer.Host.Tests/Localization/PseudoLocRenderTests.cs
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Installer.Host.Views;
using SigilBuild.Wrapper.Core.Localization;
using Xunit;

namespace SigilBuild.Installer.Host.Tests.Localization;

[Collection("SessionLanguage")]
public class PseudoLocRenderTests
{
    [AvaloniaTheory]
    [InlineData(InstallerStep.Welcome)]
    [InlineData(InstallerStep.License)]
    [InlineData(InstallerStep.InstallOptions)]
    [InlineData(InstallerStep.Options)]
    [InlineData(InstallerStep.Installing)]
    [InlineData(InstallerStep.Finish)]
    [InlineData(InstallerStep.Failed)]
    [InlineData(InstallerStep.CloseApps)]
    [InlineData(InstallerStep.DowngradeBlocked)]
    public void EveryRenderedString_IsPseudoLocalized(InstallerStep step)
    {
        SessionLanguage.SetForTesting(Lang.Pseudo);

        var vm = new InstallerViewModel(new BrandTokens());
        vm.LoadLicense("EULA");
        vm.CurrentStep = step;

        var window = new InstallerWindow { DataContext = vm };
        window.Show();

        var texts = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Where(t => !IsAllowed(t!))
            .ToArray();

        texts.Should().OnlyContain(t => t!.StartsWith("[") && t.EndsWith("]"),
            "a plain-ASCII string on screen means it never went through the catalog");
    }

    // Brand data, user-entered values and English step detail are legitimately
    // un-pseudo — they are not catalog strings (design D2, §8.1).
    private static bool IsAllowed(string text) =>
        text is "🔒" or "✓" or "•" or "••••••••"
        || text.StartsWith("1.0.0", System.StringComparison.Ordinal);
}
```

- [ ] **Step 4: Run it**

Run: `dotnet test tests/SigilBuild.Installer.Host.Tests -c Release --filter PseudoLocRenderTests`
Expected: PASS across all 9 steps. A failure names the exact screen and string that bypassed the catalog.

If a step cannot be reached by setting `CurrentStep` alone, drive it the way the existing `AccessibilityTests` do rather than weakening the assertion.

- [ ] **Step 5: Commit**

```bash
git add tests/SigilBuild.Installer.Host.Tests/Localization
git commit -m "test(p9): pseudo-loc render + static XAML scan for hardcoded strings"
```

---

### Task 16: End-to-end fixtures

**Files:**
- Create: `tests/SigilBuild.Packaging.IntegrationTests/Fixtures/localized-uk/sigil.yaml`
- Create: `tests/SigilBuild.Packaging.IntegrationTests/Fixtures/localized-de/sigil.yaml`
- Create: `tests/SigilBuild.Packaging.IntegrationTests/LocalizationEndToEndTests.cs`

- [ ] **Step 1: Write the uk fixture**

```yaml
# tests/SigilBuild.Packaging.IntegrationTests/Fixtures/localized-uk/sigil.yaml
spec: "1.0"
app:
  id: com.example.localized
  name: Localized
  version: 1.0.0
  publisher: Example
build:
  architectures: [x64]
installer:
  license:
    en: LICENSE.txt
    uk: LICENSE.uk.txt
  screens:
    - id: configure
      title:
        en: Configure
        uk: Налаштування
      subtitle:
        en: Set up your install
        uk: Налаштуйте встановлення
      fields:
        - param: channel
parameters:
  channel:
    type: enum
    enum: [stable, beta]
    default: stable
    install_time: true
    description:
      en: Update channel
      uk: Канал оновлень
```

- [ ] **Step 2: Write the de fixture (no de chrome ships)**

```yaml
# tests/SigilBuild.Packaging.IntegrationTests/Fixtures/localized-de/sigil.yaml
# Proves design §4.4: chrome and manifest text match INDEPENDENTLY.
# /lang=de renders German declared screens with English chrome, because Sigil
# ships no `de` catalog but the author supplied `de` screen text.
spec: "1.0"
app:
  id: com.example.localizedde
  name: LocalizedDe
  version: 1.0.0
  publisher: Example
build:
  architectures: [x64]
installer:
  screens:
    - id: configure
      title:
        en: Configure
        de: Konfigurieren
      fields:
        - param: channel
parameters:
  channel:
    type: enum
    enum: [stable, beta]
    default: stable
    install_time: true
```

- [ ] **Step 3: Write the end-to-end tests**

```csharp
[Fact]
public void UkFixture_RendersUkrainianChromeAndDeclaredScreens()
{
    var exe = Pack("localized-uk");
    var vm = LaunchHeadless(exe, "/lang=uk");

    // Declared screen text, from the manifest's uk map.
    vm.RailSteps.Should().Contain(s => s.Label == "Налаштування");
    // Chrome, from the shipped uk catalog. There is no NextLabel on the VM —
    // the button caption is XAML {x:Static}, so assert the resolved chrome instead.
    SessionLanguage.Current.Should().Be(Lang.Uk);
    Strings.NavNext(SessionLanguage.Current).Should().Be("Далі");
}

// The asymmetry test (design §4.4). Chrome has no `de`; the manifest does.
[Fact]
public void DeFixture_RendersGermanScreens_WithEnglishChrome()
{
    var exe = Pack("localized-de");
    var vm = LaunchHeadless(exe, "/lang=de");

    vm.RailSteps.Should().Contain(s => s.Label == "Konfigurieren", "the manifest supplies de");
    SessionLanguage.Current.Should().Be(Lang.En, "Sigil ships no de chrome; it falls back to en");
    Strings.NavNext(SessionLanguage.Current).Should().Be("Next");
}

[Fact]
public void SilentInstall_IsUnaffectedByLang()
{
    var exe = Pack("localized-uk");

    var en = RunSilent(exe, "/silent", "/D=" + NewTempDir());
    var uk = RunSilent(exe, "/silent", "/lang=uk", "/D=" + NewTempDir());

    uk.ExitCode.Should().Be(en.ExitCode).And.Be(0);
    uk.InstalledFiles.Should().BeEquivalentTo(en.InstalledFiles);
    uk.LogText.Should().NotContain("Вилучення", "the log stays English for supportability");
}

[Fact]
public void FixedManifestLanguage_LogsAndIgnoresLangFlag()
{
    var exe = Pack("localized-uk-fixed"); // installer.language: en
    var run = RunSilent(exe, "/silent", "/lang=uk", "/LOG");

    run.ExitCode.Should().Be(0, "a language conflict is not a usage error (design §2.1)");
    run.LogText.Should().Contain("manifest pin 'en' overrides /lang=uk");
}
```

Reuse the existing integration harness helpers (`Pack`, `RunSilent`) — mirror the T5/T12 integration tests rather than inventing a new harness. Add a `localized-uk-fixed` fixture that is `localized-uk` plus `installer: language: en`.

- [ ] **Step 4: Run**

Run: `dotnet test tests/SigilBuild.Packaging.IntegrationTests -c Release --filter Localization`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/SigilBuild.Packaging.IntegrationTests
git commit -m "test(p9): uk/de end-to-end fixtures + silent-path invariance"
```

---

### Task 17: ADR-008 amendments, docs, size gates

**Files:**
- Modify: `docs/architecture/adr-008-expression-policy.md` (§1.1, §4, amendment log)
- Modify: `src/SigilBuild.Wrapper/SigilBuild.Wrapper.csproj:19-27` (the comment)
- Modify: `docs/manifest-reference.md`, `docs/cli-reference.md`
- Modify: `docs/plan/feature-parity/00-GAP_ANALYSIS.md` (G10 status), `01-IMPLEMENTATION_PLAN.md` (P9 checklist row)

- [ ] **Step 1: Amend ADR-008 §1.1**

Change `locale()`'s Nature cell from `reads CurrentUICulture.Name ("" under InvariantGlobalization)` to:

```text
reads the OS UI language via GetUserPreferredUILanguages (top preference; "" when unavailable)
```

- [ ] **Step 2: Amend ADR-008 §4**

Replace "Ship English plus a small seed set (5–10 languages)" with the **standing rule**:

```markdown
- **A language ships only with a named reviewer** recorded in its catalog file's
  provenance header. The initial set is English plus Ukrainian; further languages
  are admitted under this rule as ordinary content contributions, not as
  amendments. What protects users is review, not count — a machine-translated
  language nobody has read is worse than an honest English fallback.
```

- [ ] **Step 3: Append the amendment log row**

```markdown
| 2026-07-15 | §1.1: `locale()` re-pointed from `CurrentUICulture.Name` to `GetUserPreferredUILanguages`. §4: the "5–10 languages" seed count replaced by a standing named-reviewer rule; initial set en + uk. | P9/G10 — `locale()` returned `""` under `InvariantGlobalization`, so the documented language-resolution chain could not work. **This is a behavior change, not only a source change:** a `When` using `locale()` moves from an always-`""` result to a real tag, which can flip conditions. Practical risk is ~zero precisely because the function was useless, but it is recorded here rather than assumed. `InvariantGlobalization` stays on; no satellite assemblies; no `CultureInfo` is constructed. |
```

Never rewrite prior rows.

- [ ] **Step 4: Update the csproj comment**

Replace the "If real localization is needed, revisit this together with ADR-008" sentence at `SigilBuild.Wrapper.csproj:19-27` with a statement that localization **has landed** (P9) via source-generated culture-neutral string tables, `InvariantGlobalization` intact — which is exactly what that comment anticipated. Keep the ordinal/invariant-comparison warning.

- [ ] **Step 5: Document the limitations**

In `docs/manifest-reference.md`: `installer.language`, the `LocalizedText` shape for `title`/`subtitle`/`description`/`license`, and the `en`-required rule (`SIG0290`).

In `docs/cli-reference.md`: `/lang=<tag>` and `/?`, plus the known limitations verbatim from design §10 — **no RTL layout** (a manifest may supply `ar`/`he` maps; text renders, layout does not mirror), **log stays English**, **no language-selection dialog**, **number/date rendering stays invariant**.

- [ ] **Step 6: Re-measure the size gates**

Run: `pwsh scripts/publish-installer-runtime.ps1`
Expected: host ≤ 40 MB, CLI ≤ 15 MB. Record the **actual** numbers in your summary. P13 anticipates globalization adding weight — if a gate is exceeded, **report it; do not re-pin silently.**

- [ ] **Step 7: Full verification**

```bash
dotnet build Sigil.slnx -c Release
dotnet test Sigil.slnx -c Release
dotnet publish src/SigilBuild.Installer.Host -c Release -r win-x64 -p:PublishAot=true
```

Expected: build clean (no `IL2xxx`/`IL3xxx`), all tests green, zero skipped localization tests.

- [ ] **Step 8: Commit**

```bash
git add docs src/SigilBuild.Wrapper/SigilBuild.Wrapper.csproj
git commit -m "docs(p9): ADR-008 amendments, manifest/CLI reference, G10 status"
```

---

## Done criteria

- [ ] `dotnet build Sigil.slnx -c Release` clean under `TreatWarningsAsErrors`
- [ ] `dotnet test Sigil.slnx -c Release` green, zero skipped localization tests
- [ ] AOT publish warning-free
- [ ] Pseudo-loc render + static scan both green — **zero hardcoded UI strings**
- [ ] uk fixture renders Ukrainian chrome *and* declared screens
- [ ] de fixture renders German screens with English chrome (independent match)
- [ ] `/lang=uk /silent` is byte-identical to `/silent`; log still English
- [ ] `SIG0290` fatal on an `en`-less map, including the license composite case
- [ ] ADR-008 amended: §1.1, §4, and one dated log row
- [ ] Size gates measured and reported (not silently re-pinned)
- [ ] Branch `task/p9-localization` pushed; **not** merged
