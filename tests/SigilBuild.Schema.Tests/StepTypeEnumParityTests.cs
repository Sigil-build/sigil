// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace SigilBuild.Schema.Tests;

/// <summary>
/// The step-<c>type</c> enum is spelled in several places in the schema, and every
/// copy must list the same catalog (R81).
/// </summary>
/// <remarks>
/// This guard exists because the duplication has already drifted, twice over. The
/// lane that added <c>http_download</c>, <c>ini_write</c>, <c>json_edit</c> and
/// <c>xml_edit</c> updated the root enums and not <c>HookPhase</c>; the next lane to
/// add step types updated every copy including <c>HookPhase</c> — and, rewriting that
/// line, carried the earlier gap forward without noticing. The result read as a
/// deliberate narrowing of what hooks accept, and was argued about as one, when
/// nobody had ever decided it.
/// <para>
/// AGENTS.md warns to "update all of them". A warning that has been missed twice is
/// a test's job.
/// </para>
/// </remarks>
public class StepTypeEnumParityTests
{
    private const string SchemaFile = "sigil-schema.json";

    [Fact]
    public void Every_step_type_enum_in_the_schema_lists_the_same_catalog()
    {
        // Arrange
        using var doc = JsonDocument.Parse(File.ReadAllText(SchemaFile));
        var enums = CollectStepTypeEnums(doc.RootElement).ToList();

        // Act
        var distinctCatalogs = enums
            .Select(e => string.Join(",", e.Types.OrderBy(t => t, System.StringComparer.Ordinal)))
            .Distinct(System.StringComparer.Ordinal)
            .Count();

        // Assert
        enums.Should().HaveCountGreaterThan(1,
            "the point of this test is the duplication — if the schema ever holds one "
            + "copy, delete it rather than letting it pass vacuously");
        distinctCatalogs.Should().Be(1,
            "every copy of the step-type enum must list the same catalog; found: "
            + string.Join(" | ", enums.Select(e => $"{e.Path}={e.Types.Count}")));
    }

    /// <summary>
    /// Walks the whole schema for any <c>"type"</c> property whose <c>enum</c> holds
    /// step-type names, identified by a member every copy has.
    /// </summary>
    private static IEnumerable<(string Path, IReadOnlyList<string> Types)> CollectStepTypeEnums(
        JsonElement element, string path = "$")
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                var childPath = $"{path}.{prop.Name}";

                if (prop.Name == "type" &&
                    prop.Value.ValueKind == JsonValueKind.Object &&
                    prop.Value.TryGetProperty("enum", out var enumNode) &&
                    enumNode.ValueKind == JsonValueKind.Array)
                {
                    var members = enumNode.EnumerateArray()
                        .Where(v => v.ValueKind == JsonValueKind.String)
                        .Select(v => v.GetString()!)
                        .ToList();

                    if (members.Contains("file_copy", System.StringComparer.Ordinal))
                    {
                        yield return (childPath, members);
                    }
                }

                foreach (var found in CollectStepTypeEnums(prop.Value, childPath))
                {
                    yield return found;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var item in element.EnumerateArray())
            {
                foreach (var found in CollectStepTypeEnums(item, $"{path}[{i++}]"))
                {
                    yield return found;
                }
            }
        }
    }
}
