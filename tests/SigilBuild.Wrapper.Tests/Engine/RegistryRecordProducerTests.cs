namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using Xunit;

/// <summary>
/// R51's registry allowlist rests on one claim: <strong>the three registry steps are the
/// only producers of <c>restore_registry_value</c> / <c>restore_registry_key</c>
/// records</strong>, so collecting the keys those steps declare yields a COMPLETE
/// allowlist.
/// </summary>
/// <remarks>
/// <para>
/// The claim is true today and nothing in the type system keeps it true. The day a new
/// step journals a registry record without being added to
/// <c>SignedDeclarations.CollectFrom</c>, that step's key is undeclared, its record is
/// refused at uninstall, and the app it belongs to becomes unremovable — silently, and
/// only discovered when a user tries to remove it. Stage 1 closed four separate routes
/// into that end state; this file exists so a fifth cannot open by omission.
/// </para>
/// <para>
/// The source scan is deliberate rather than reflective: what matters is which code
/// APPENDS such a record, and that is a property of the source, not of a loaded type
/// graph. It follows the repo-root discovery pattern already used by
/// <c>PrerequisiteRecipeDocTests</c> and the localization end-to-end tests.
/// </para>
/// </remarks>
public class RegistryRecordProducerTests
{
    /// <summary>
    /// Files permitted to construct a registry rollback record, and why. Anything else
    /// is a new producer.
    /// </summary>
    private static readonly Dictionary<string, string> Expected = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RegistryWriteStep.cs"] = "registry_write — declares hive/key in the manifest",
        ["RegistryDeleteValueStep.cs"] = "registry_delete_value — declares hive/key in the manifest",
        ["RegistryDeleteKeyStep.cs"] = "registry_delete_key — declares hive/key in the manifest",
        ["SerializableRollbackRecord.cs"] = "wire round-trip: rebuilds a record that one of " +
            "the three steps already produced; it introduces no new coordinate",
    };

    [Fact]
    public void The_registry_allowlist_covers_every_producer_of_a_registry_rollback_record()
    {
        // Arrange
        var engineSources = Directory.EnumerateFiles(
            Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal));

        var producer = new Regex(
            @"new\s+(RollbackRecord\.)?RestoreRegistry(Value|Key)\s*\(",
            RegexOptions.CultureInvariant);

        // Act
        var found = engineSources
            .Where(f => producer.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Assert
        found.Should().NotBeEmpty("the scan must actually be finding the known producers");
        found.Should().BeEquivalentTo(
            Expected.Keys,
            "R51's allowlist is complete only while these are the only producers of a " +
            "registry rollback record. A new one means its keys must be collected in " +
            "SignedDeclarations.CollectFrom, or every install using that step becomes " +
            "unremovable: its records name keys no declaration covers, so the anchor " +
            "refuses them while the ARP row and the uninstall state are deleted anyway.");
    }

    [Fact]
    public void Every_registry_step_type_contributes_its_key_to_the_allowlist()
    {
        // Arrange — the behavioural half: each of the three step types, in one blob.
        var blob = new WrapperBlob(
            AppId: "sigil.acme",
            Parameters: Array.Empty<ParameterDefinition>(),
            InstallSteps: new InstallStep[]
            {
                new InstallStep.RegistryWrite(
                    "w", "HKCU", @"Software\Acme\Written", "V", "REG_SZ", "1", "default",
                    When: null, OnFailure: OnFailure.Fail),
                new InstallStep.RegistryDeleteValue(
                    "dv", "HKCU", @"Software\Acme\DeletedValue", "V", "default",
                    When: null, OnFailure: OnFailure.Fail),
                new InstallStep.RegistryDeleteKey(
                    "dk", "HKCU", @"Software\Acme\DeletedKey", Recursive: true, View: "default",
                    When: null, OnFailure: OnFailure.Fail),
            },
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>());

        var declarations = SignedDeclarations.FromBlob(
            blob, SigilBuild.Wrapper.Cli.CommandLineParser.Parse(Array.Empty<string>(), Array.Empty<ParameterDefinition>()),
            InstallScope.User);

        // Act
        var resolved = declarations.Resolve(Path.Combine(Path.GetTempPath(), "sigil-s7-install"));

        // Assert
        resolved.RegistryKeys.Select(k => k.Key).Should().BeEquivalentTo(
            new[]
            {
                @"Software\Acme\Written",
                @"Software\Acme\DeletedValue",
                @"Software\Acme\DeletedKey",
            },
            "a registry step whose key is not collected leaves its own rollback record " +
            "undeclared, and therefore refused at uninstall");
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Sigil.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        dir.Should().NotBeNull("the repo root (Sigil.slnx) must be locatable from the test output");
        return dir!;
    }
}
