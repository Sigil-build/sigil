using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
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
            ElevationSecretHandoff.CleanUp(relaunch, childMayStillBeRunning: false);
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

    // ── An inbound /SecretHandoff= token is never forwarded ───────────────────
    //
    // The switch is caller-reachable, and this process's own parser has ALREADY
    // consumed and deleted whatever file it named. Copying it into the relaunch
    // vector hands the elevated child a switch pointing at a file that no longer
    // exists, which the child's parser correctly refuses — a caller-reachable way to
    // turn a legitimate per-machine install into exit 64. Both paths through
    // PrepareRelaunchArgs must drop it: the one that writes a fresh envelope and the
    // one that has nothing to protect.

    [WindowsFact("DPAPI (crypt32) and Windows file ACLs")]
    public void PrepareRelaunchArgs_drops_an_inbound_handoff_token_when_it_writes_its_own()
    {
        // Arrange — the faithful shape: a caller passes a handoff token that decrypts,
        // so the parent's own parse SUCCEEDS (and deletes the file) rather than
        // exit-64ing early. `inbound` is a spent path by the time argv is rewritten.
        var seed = ElevationSecretHandoff.PrepareRelaunchArgs(
            new[] { "/Papikey=hunter2" },
            ParseWithSchema(new[] { "/Papikey=hunter2" }, secret: new[] { "apikey" }));
        var inbound = seed.Single(a => a.StartsWith(HandoffSwitch, StringComparison.OrdinalIgnoreCase));

        var args = new[] { "/silent", inbound };
        var parsed = ParseWithSchema(args, secret: new[] { "apikey" });
        parsed.Values.Should().ContainKey("apikey").WhoseValue.Should().Be("hunter2");
        File.Exists(HandoffPathOf(seed)).Should().BeFalse("the parent's parse consumed it");

        // Act
        var relaunch = ElevationSecretHandoff.PrepareRelaunchArgs(args, parsed);

        try
        {
            // Assert — exactly one handoff token, and it is the fresh one, not the
            // spent path the caller supplied.
            relaunch.Should().NotContain(inbound, "a spent handoff path must never reach the child");
            relaunch.Should().ContainSingle(a => a.StartsWith(HandoffSwitch, StringComparison.OrdinalIgnoreCase));
            relaunch.Should().Contain("/silent");
            File.Exists(HandoffPathOf(relaunch)).Should().BeTrue("the forwarded envelope is the fresh one");
        }
        finally
        {
            ElevationSecretHandoff.CleanUp(relaunch, childMayStillBeRunning: false);
        }
    }

    [WindowsFact("DPAPI (crypt32) and Windows file ACLs")]
    public void PrepareRelaunchArgs_drops_an_inbound_handoff_token_when_there_is_no_secret()
    {
        // Arrange — the reachable no-secret case. An envelope's names only have to be
        // DECLARED parameters, not secret-typed ones, so a run whose schema declares
        // no secret at all can still consume a handoff and end up with
        // SecretKeys.Count == 0 plus a spent token in argv. Built by writing the
        // envelope against a schema that calls "mode" secret and then reading it back
        // against one that does not — which is exactly the shape a hand-crafted
        // envelope takes (DPAPI machine scope is writable by any local process).
        var seed = ElevationSecretHandoff.PrepareRelaunchArgs(
            new[] { "/Pmode=full" },
            ParseWithSchema(new[] { "/Pmode=full" }, secret: new[] { "mode" }));
        var inbound = seed.Single(a => a.StartsWith(HandoffSwitch, StringComparison.OrdinalIgnoreCase));

        var args = new[] { "/silent", inbound };
        var parsed = ParseWithSchema(args, secret: Array.Empty<string>());
        parsed.SecretKeys.Should().BeEmpty();
        parsed.Values.Should().ContainKey("mode").WhoseValue.Should().Be("full");

        // Act — nothing to protect, so no envelope is written; the spent token must
        // still be stripped rather than ridden along by the identity path.
        var relaunch = ElevationSecretHandoff.PrepareRelaunchArgs(args, parsed);

        // Assert
        relaunch.Should().Equal("/silent");
        relaunch.Should().NotContain(a => a.StartsWith(HandoffSwitch, StringComparison.OrdinalIgnoreCase));
    }

    [WindowsFact("DPAPI (crypt32) and Windows file ACLs")]
    public void CleanUp_leaves_the_envelope_alone_while_a_child_may_still_be_running()
    {
        // Arrange — Elevation.RelaunchElevatedAndWait reports childMayStillBeRunning
        // when ShellExecuteExW succeeded but handed back no handle to wait on. The
        // parent must not race a child that is still starting up to the envelope.
        var args = new[] { "/Papikey=hunter2" };
        var parsed = ParseWithSchema(args, secret: new[] { "apikey" });
        var relaunch = ElevationSecretHandoff.PrepareRelaunchArgs(args, parsed);
        var path = HandoffPathOf(relaunch);

        try
        {
            // Act
            ElevationSecretHandoff.CleanUp(relaunch, childMayStillBeRunning: true);

            // Assert
            File.Exists(path).Should()
                .BeTrue("a leaked DPAPI envelope beats deleting one the child has not read yet");

            // Act — and once the parent knows no child can read it, it does go.
            ElevationSecretHandoff.CleanUp(relaunch, childMayStillBeRunning: false);

            // Assert
            File.Exists(path).Should().BeFalse();
        }
        finally
        {
            ElevationSecretHandoff.CleanUp(relaunch, childMayStillBeRunning: false);
        }
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
            ElevationSecretHandoff.CleanUp(relaunch, childMayStillBeRunning: false);
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

    [WindowsFact("DPAPI (crypt32) and Windows file ACLs")]
    public void The_envelope_never_holds_the_secret_in_clear_text()
    {
        // Arrange — M6. The roundtrip test above proves the value SURVIVES the
        // envelope, which is equally true of a file that just writes it down. DPAPI
        // machine scope means any local process that can READ the blob can decrypt
        // it, so the ACL is the access control and the encryption is what stops a
        // stray %TEMP% backup, an AV quarantine copy or a crash dump from handing the
        // value over. Assert on the bytes, not on decoded text: a decode with
        // replacement characters can hide a match.

        // Named and sized to stay clear of gitleaks' generic-api-key rule, which
        // fires on an identifier carrying a key/secret/token word, an equals sign, and
        // then 10 or more entropic characters. Keep this identifier neutral and the
        // literal under ten characters; it only has to be distinctive enough for the
        // on-disk byte search below.
        const string PlaintextFixture = "hunter2fx";
        var args = new[] { "/Papikey=" + PlaintextFixture };
        var parsed = ParseWithSchema(args, secret: new[] { "apikey" });
        var relaunch = ElevationSecretHandoff.PrepareRelaunchArgs(args, parsed);
        var path = HandoffPathOf(relaunch);

        try
        {
            // Act
            var bytes = File.ReadAllBytes(path);

            // Assert
            bytes.Should().NotBeEmpty("the envelope the child is pointed at must exist");
            bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(PlaintextFixture)).Should()
                .Be(-1, "the UTF-8 bytes of a secret value must not appear in the envelope");
            bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(PlaintextFixture)).Should()
                .Be(-1, "nor the UTF-16LE bytes — that is how the string reaches crypt32 on Windows");
            bytes.AsSpan().IndexOf(Encoding.BigEndianUnicode.GetBytes(PlaintextFixture)).Should()
                .Be(-1, "nor the byte-swapped form, in case a future writer changes endianness");
        }
        finally
        {
            ElevationSecretHandoff.CleanUp(relaunch, childMayStillBeRunning: false);
        }
    }
}
