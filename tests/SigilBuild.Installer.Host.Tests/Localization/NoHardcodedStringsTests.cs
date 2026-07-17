using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace SigilBuild.Installer.Host.Tests.Localization;

/// <summary>
/// Task 15 (P9): the static half of the "zero hardcoded strings" guarantee. Scans
/// every <c>Views/*.axaml</c> file for a user-facing attribute literal that still
/// contains a letter — anything found bypassed the S/Strings catalog (Tasks
/// 12-14). Complements <see cref="PseudoLocRenderTests"/>, whose runtime render
/// walk can only prove screens it can actually reach; this scan sees every XAML
/// file regardless of reachability (design §8.1).
/// </summary>
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
                // XAML numeric character references (e.g. "&#x1F512;" for 🔒) must be
                // decoded before the letter check, or the escape's own hex digits
                // ("x1F512") falsely read as a hardcoded English word.
                .Select(m => (File: Path.GetFileName(file), Value: System.Net.WebUtility.HtmlDecode(m.Groups[2].Value))))
            .Where(x => Regex.IsMatch(x.Value, "[A-Za-z]"))
            .Where(x => !x.Value.StartsWith('{')) // bindings / x:Static
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
