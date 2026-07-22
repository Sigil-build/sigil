using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

/// <summary>
/// P5: guarantees the two prerequisite recipes shipped in
/// <c>docs/guides/prerequisites.md</c> are copy-paste-valid — every fenced
/// <c>```yaml</c> manifest in the guide validates against the JSON schema AND parses
/// with no error diagnostics, and actually declares a prerequisite. Keeps the docs
/// from drifting out of sync with the schema / parser.
/// </summary>
public class PrerequisiteRecipeDocTests
{
    private static string DocPath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Sigil.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        dir.Should().NotBeNull("the repo root (Sigil.slnx) must be locatable from the test output");
        return Path.Combine(dir!, "docs", "guides", "prerequisites.md");
    }

    // Extract every ```yaml fenced block that is a full manifest (starts with "spec:").
    private static List<string> ManifestBlocks(string markdown)
    {
        var blocks = new List<string>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var inBlock = false;
        var current = new List<string>();
        foreach (var line in lines)
        {
            if (!inBlock && line.TrimStart().StartsWith("```yaml", StringComparison.Ordinal))
            {
                inBlock = true;
                current.Clear();
                continue;
            }
            if (inBlock && line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inBlock = false;
                var body = string.Join("\n", current);
                if (body.TrimStart().StartsWith("spec:", StringComparison.Ordinal))
                {
                    blocks.Add(body);
                }
                continue;
            }
            if (inBlock)
            {
                current.Add(line);
            }
        }
        return blocks;
    }

    [Fact]
    public async Task Both_recipes_validate_against_schema_and_parse_with_a_prerequisite()
    {
        var markdown = await File.ReadAllTextAsync(DocPath());
        var recipes = ManifestBlocks(markdown);

        recipes.Should().HaveCount(2, "the guide ships exactly the VC++ and .NET recipes as full manifests");

        foreach (var yaml in recipes)
        {
            // 1. JSON-schema valid.
            var schemaDiags = await SchemaValidator.ValidateAsync(yaml, "recipe.yaml");
            schemaDiags.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error,
                "a docs recipe must validate against sigil-schema.json");

            // 2. Typed parse clean, and it actually declares a prerequisite.
            var parsed = ManifestParser.Parse(yaml, "recipe.yaml");
            parsed.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error,
                "a docs recipe must parse with no error diagnostics (including SIG0280)");
            var prereqs = parsed.Manifest?.Installer?.Prerequisites;
            (prereqs is { Count: > 0 }).Should().BeTrue("each recipe declares a prerequisite");
        }
    }
}
