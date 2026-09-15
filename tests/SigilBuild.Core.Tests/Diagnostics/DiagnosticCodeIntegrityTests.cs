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
/// The two guards that keep <see cref="DiagnosticCodes"/> the only place a code is
/// spelled, and every code the name of exactly one failure (R82).
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
        DiagnosticCodes.DocsUrl(DiagnosticCodes.SigntoolFailed)
            .Should().EndWith("/" + DiagnosticCodes.SigntoolFailed);
    }

    private static IEnumerable<string> EnumerateSources()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the test must be able to locate the repo root");

        return Directory
            .EnumerateFiles(Path.Combine(dir!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
