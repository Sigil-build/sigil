using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// Register row R18: a <see cref="ParameterType.Secret"/> value must not appear on
/// the elevated (UAC) relaunch command line, where Sysmon / EDR / WMI
/// process-creation auditing records it verbatim. The relaunch vector carries only
/// <c>/SecretHandoff=&lt;path&gt;</c>; the values cross the elevation boundary inside
/// a DPAPI-protected, ACL-restricted, delete-after-read envelope.
/// </summary>
public sealed class ElevationSecretHandoffTests
{
    private const string HandoffSwitch = "/SecretHandoff=";

    /// <summary>
    /// A real <see cref="ParsedCommandLine"/> over a two-parameter schema —
    /// <c>apikey</c> and <c>mode</c> — with the named subset declared
    /// <see cref="ParameterType.Secret"/>. Parsing (rather than hand-building the
    /// record) is deliberate: <see cref="ParsedCommandLine.SecretKeys"/> and the
    /// canonical-cased value keys must come from the same code path production uses.
    /// </summary>
    private static ParsedCommandLine ParseWithSchema(IReadOnlyList<string> args, IReadOnlyList<string> secret)
    {
        var schema = new[] { "apikey", "mode" }
            .Select(name => new ParameterDefinition(
                name,
                secret.Contains(name, StringComparer.OrdinalIgnoreCase)
                    ? ParameterType.Secret
                    : ParameterType.String,
                Default: null,
                EnumValues: null,
                // Not install-time: the silent-mode "required parameter has no
                // default" gate is a different contract and would fire on the
                // identity case below, which supplies only /Pmode.
                InstallTime: false,
                Description: null,
                Pattern: null,
                Min: null,
                Max: null))
            .ToArray();

        return CommandLineParser.Parse(args, schema);
    }

    private static string HandoffPathOf(IReadOnlyList<string> relaunch) =>
        relaunch.Single(a => a.StartsWith(HandoffSwitch, StringComparison.OrdinalIgnoreCase))
            .Substring(HandoffSwitch.Length);

    [WindowsFact("DPAPI (crypt32) and Windows file ACLs")]
    public void PrepareRelaunchArgs_removes_secret_parameter_tokens_from_the_relaunch_vector()
    {
        // Arrange — a parse whose schema declares "apikey" secret and "mode" not.
        var args = new[] { "/silent", "/Papikey=hunter2", "/Pmode=full" };
        var parsed = ParseWithSchema(args, secret: new[] { "apikey" });

        // Act
        var relaunch = ElevationSecretHandoff.PrepareRelaunchArgs(args, parsed);

        try
        {
            // Assert — the secret value appears nowhere in the vector; the plain one survives.
            relaunch.Should().NotContain(a => a.Contains("hunter2", StringComparison.Ordinal));
            relaunch.Should().Contain("/Pmode=full");
            relaunch.Should().ContainSingle(a => a.StartsWith(HandoffSwitch, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            ElevationSecretHandoff.CleanUp(relaunch);
        }
    }

    [WindowsFact("DPAPI (crypt32) and Windows file ACLs")]
    public void Handoff_roundtrips_and_deletes_the_envelope_file()
    {
        // Arrange
        var args = new[] { "/Papikey=hunter2" };
        var parsed = ParseWithSchema(args, secret: new[] { "apikey" });

        // Act
        var relaunch = ElevationSecretHandoff.PrepareRelaunchArgs(args, parsed);
        var path = HandoffPathOf(relaunch);
        var recovered = ElevationSecretHandoff.TryConsumeHandoff(path);

        // Assert
        recovered.Should().NotBeNull();
        recovered!.Should().ContainKey("apikey").WhoseValue.Should().Be("hunter2");
        File.Exists(path).Should().BeFalse("the envelope must not outlive its single read");
    }

    [Fact]
    public void PrepareRelaunchArgs_is_the_identity_when_no_secret_parameter_is_present()
    {
        // Arrange
        var args = new[] { "/silent", "/Pmode=full" };
        var parsed = ParseWithSchema(args, secret: Array.Empty<string>());

        // Act + Assert
        ElevationSecretHandoff.PrepareRelaunchArgs(args, parsed).Should().Equal(args);
    }

    [WindowsFact("DPAPI (crypt32) and Windows file ACLs")]
    public void CommandLineParser_binds_the_handoff_pairs_as_redacted_secret_parameters()
    {
        // Arrange — the un-elevated parent builds the relaunch vector.
        var args = new[] { "/Papikey=hunter2", "/Pmode=full" };
        var parent = ParseWithSchema(args, secret: new[] { "apikey" });
        var relaunch = ElevationSecretHandoff.PrepareRelaunchArgs(args, parent);

        // Act — the elevated child parses that vector, consuming the envelope.
        var child = ParseWithSchema(relaunch, secret: new[] { "apikey" });

        // Assert — the value is bound under its canonical name and still redacted
        // by the existing audit re-render.
        child.Values.Should().ContainKey("apikey").WhoseValue.Should().Be("hunter2");
        child.Values.Should().ContainKey("mode").WhoseValue.Should().Be("full");
        child.AuditSafeRendering().Should().Contain("/Papikey=***").And
            .NotContain("hunter2", "the audit rendering must never carry a secret value");
        File.Exists(HandoffPathOf(relaunch)).Should().BeFalse("the child consumes the envelope");
    }

    [Fact]
    public void CommandLineParser_refuses_an_unreadable_handoff_without_naming_a_value()
    {
        // Arrange — a path that does not exist (child crashed, envelope already read,
        // or an attacker pointing the switch at something else).
        var missing = Path.Combine(Path.GetTempPath(), "sigil-elevate-" + Guid.NewGuid().ToString("N") + ".dpapi");

        // Act
        var act = () => ParseWithSchema(new[] { HandoffSwitch + missing }, secret: new[] { "apikey" });

        // Assert
        act.Should().Throw<UsageException>()
            .WithMessage("*secret handoff could not be read*");
    }

    [WindowsFact("DPAPI (crypt32) and Windows file ACLs")]
    [SupportedOSPlatform("windows")]
    public void The_envelope_grants_only_this_user_and_administrators()
    {
        // Arrange — DPAPI machine scope means any local process that can READ the
        // blob can decrypt it, so the create-time DACL is R18's load-bearing
        // compensating control, not decoration.
        var args = new[] { "/Papikey=hunter2" };
        var parsed = ParseWithSchema(args, secret: new[] { "apikey" });
        var relaunch = ElevationSecretHandoff.PrepareRelaunchArgs(args, parsed);
        var path = HandoffPathOf(relaunch);

        try
        {
            // Act
            var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
            using var identity = WindowsIdentity.GetCurrent();
            var self = identity.User;
            self.Should().NotBeNull("the test host must have a user SID to compare against");
            var granted = security
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Where(rule => rule.AccessControlType == AccessControlType.Allow)
                .Select(rule => (SecurityIdentifier)rule.IdentityReference)
                .ToList();

            // Assert
            security.AreAccessRulesProtected.Should()
                .BeTrue("no inherited %TEMP% ACE may survive onto a file holding a secret");
            granted.Should().NotBeEmpty();
            granted.Should().OnlyContain(
                sid => sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
                       || sid.Equals(self!),
                "only this user and BUILTIN\\Administrators (the elevated child may be a "
                + "different admin account) may read the envelope");
        }
        finally
        {
            ElevationSecretHandoff.CleanUp(relaunch);
        }
    }

    [WindowsFact("DPAPI (crypt32) and Windows file ACLs")]
    public void TryConsumeHandoff_returns_null_for_a_file_that_is_not_an_envelope()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), "sigil-elevate-" + Guid.NewGuid().ToString("N") + ".dpapi");
        File.WriteAllText(path, "not a DPAPI blob");

        try
        {
            // Act + Assert
            ElevationSecretHandoff.TryConsumeHandoff(path).Should().BeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
