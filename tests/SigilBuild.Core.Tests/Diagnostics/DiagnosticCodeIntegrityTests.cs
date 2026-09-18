// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using SigilBuild.Core.Diagnostics;
using Xunit;

namespace SigilBuild.Core.Tests.Diagnostics;

/// <summary>
/// The guards that keep <see cref="DiagnosticCodes"/> the only place a code — or the
/// documentation host it resolves to — is spelled, and every code the name of exactly
/// one failure (R82).
/// </summary>
/// <remarks>
/// Both are needed, and neither substitutes for the other. The register asked only
/// for the first — "assert every emitted code resolves to a member" — but that alone
/// would have passed on the defect it was filed for: `SIG0270` resolved perfectly
/// well, to a different failure than the one printing it. Four numbers meant two
/// things each, so the second test is the one that actually catches the class.
/// </remarks>
public class DiagnosticCodeIntegrityTests
{
    private static readonly Regex CodeLiteral = new("\"SIG0[0-9]{3}\"", RegexOptions.Compiled);

    [Fact]
    public void No_source_file_spells_a_diagnostic_code_as_a_literal()
    {
        // Arrange — raw literals are the mechanism that allowed the collisions:
        // nothing checks a string against the table, so a second claim on a number
        // is invisible until someone reads both call sites. DiagnosticCodes.cs is
        // exempt because that is where the literals are supposed to live.
        var offenders = new List<string>();

        // Act
        foreach (var file in EnumerateSources())
        {
            if (Path.GetFileName(file) == "DiagnosticCodes.cs") continue;

            var text = File.ReadAllText(file);
            foreach (Match m in CodeLiteral.Matches(text))
            {
                offenders.Add($"{Path.GetFileName(file)}: {m.Value}");
            }
        }

        // Assert
        offenders.Should().BeEmpty(
            "a diagnostic code belongs in DiagnosticCodes and nowhere else — use the "
            + "constant, and DiagnosticCodes.DocsUrl for the documentation link");
    }

    [Fact]
    public void No_source_file_spells_the_documentation_host()
    {
        // Arrange — the sibling of the test above, and the one it could not do. Its
        // regex requires a quote immediately before SIG, so it never saw the code
        // embedded in a URL ("https://…/diagnostics/SIG0322"), and 40 such literals
        // across seven files had bypassed DocsUrl entirely — the one function that
        // exists to own this host. A host spelled in 40 places is a host that cannot
        // be changed, and these URLs are printed to users and outlive the binary that
        // printed them.
        var offenders = new List<string>();
        var scanned = 0;
        var sawTheTable = false;

        // Act
        foreach (var file in EnumerateSources())
        {
            scanned++;
            if (Path.GetFileName(file) == "DiagnosticCodes.cs")
            {
                sawTheTable = true;
                continue;
            }

            var text = File.ReadAllText(file);
            if (text.Contains(DocumentationHost, StringComparison.OrdinalIgnoreCase))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        // Assert — prove the scan reached the source tree first. A file walk that
        // silently finds nothing would make every assertion below vacuously true,
        // which is the failure mode of a guard nobody notices is asleep.
        scanned.Should().BeGreaterThan(50, "the scan must actually be walking src/");
        sawTheTable.Should().BeTrue(
            "DiagnosticCodes.cs is the one file allowed to spell the host, so not "
            + "encountering it means the walk is looking somewhere else entirely");

        offenders.Should().BeEmpty(
            "the documentation host belongs in DiagnosticCodes.DocsUrl and nowhere else, "
            + "so that moving it stays a one-line change instead of a repo-wide audit");
    }

    /// <summary>
    /// Spelled from its parts on purpose: the scan covers <c>src/</c> only, but if it
    /// is ever widened to the test tree this file must not become the offender it is
    /// looking for.
    /// </summary>
    private static readonly string DocumentationHost = "docs." + "sigil.build";

    [Fact]
    public void Every_diagnostic_code_value_is_distinct()
    {
        // Arrange
        var codes = typeof(DiagnosticCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => new { f.Name, Value = (string)f.GetRawConstantValue()! })
            .ToList();

        // Act
        var duplicates = codes
            .GroupBy(c => c.Value, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} claimed by {string.Join(" and ", g.Select(c => c.Name))}")
            .ToList();

        // Assert
        codes.Should().NotBeEmpty("the reflection query must actually find the table");
        duplicates.Should().BeEmpty(
            "one code names one failure — it is what a reader greps for and what a "
            + "documentation URL resolves to, so a second meaning does not make it "
            + "ambiguous, it makes it useless");
    }

    [Fact]
    public void DocsUrl_is_built_from_the_code_it_is_given()
    {
        // Arrange, Act, Assert — the property that makes a URL unable to drift from
        // its code, which is how the R82 renumber briefly left five URLs behind.
        // The target is an anchor on one page, and a URL fragment is case-sensitive,
        // so the assertion pins the lower-casing too: it is what makes the link land
        // on the entry rather than at the top of the page.
        DiagnosticCodes.DocsUrl(DiagnosticCodes.SigntoolFailed)
            .Should().EndWith("#" + DiagnosticCodes.SigntoolFailed.ToLowerInvariant());
    }

    [Fact]
    public void Every_diagnostic_code_has_an_entry_in_the_reference_page()
    {
        // Arrange — the codes are printed to users with a URL promising an
        // explanation, so a code with no entry is a broken promise that only the
        // reader discovers. The page carries an explicit {#sigxxxx} id per entry
        // precisely so this can be checked mechanically.
        var page = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "diagnostics.md"));

        var codes = typeof(DiagnosticCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(v => v.StartsWith("SIG", StringComparison.Ordinal))
            .ToList();

        var documented = Regex.Matches(page, @"\{#(sig[0-9]{4})\}")
            .Select(m => m.Groups[1].Value.ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);

        // Act
        var undocumented = codes.Where(c => !documented.Contains(c)).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var orphaned = documented.Where(d => !codes.Contains(d)).OrderBy(d => d, StringComparer.Ordinal).ToList();

        // Assert
        codes.Should().NotBeEmpty("the reflection query must actually find the table");
        documented.Should().NotBeEmpty("the anchor regex must actually match the page");

        undocumented.Should().BeEmpty(
            "every code Sigil can print links to this page, so one without an entry "
            + "sends the reader to a heading that is not there");
        orphaned.Should().BeEmpty(
            "an entry for a code that no longer exists outlives the failure it "
            + "described, and reads as though Sigil still emits it");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the test must be able to locate the repo root");
        return dir!.FullName;
    }

    private static IEnumerable<string> EnumerateSources()
    {
        return Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
