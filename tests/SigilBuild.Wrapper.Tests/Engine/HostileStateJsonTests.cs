namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

/// <summary>
/// R19: a planted <c>uninstall.json</c> must fail CLOSED, not FATALLY.
/// </summary>
/// <remarks>
/// <para>
/// Record rehydration sat outside <c>TryLoad</c>'s <c>catch</c>, which covered only
/// <c>JsonSerializer.Deserialize</c>. An unknown discriminator, a null array
/// element, or a missing required field threw out of <c>Load</c>, and nothing above
/// it — <c>UninstallEngine.RunAsync</c>, <c>InstallSession</c> — caught it either.
/// One planted line therefore killed every install and every uninstall of that
/// AppId: a persistent, per-app denial of service. The read was also unbounded.
/// </para>
/// <para>
/// All fixtures are <b>user</b>-scope, under <c>%LocalAppData%</c>, keyed by a
/// GUID-unique <c>sigil.test.*</c> AppId, and deleted in a <c>finally</c>. Nothing
/// here writes <c>%ProgramData%</c>, the registry, <c>PATH</c>, or any machine
/// state. <c>[SupportedOSPlatform("windows")]</c> satisfies CA1416 for the store's
/// Windows-attributed provenance calls; <c>[WindowsFact]</c> / <c>[WindowsTheory]</c>
/// make these report Skipped — never vacuously Passed — off Windows (register row R6).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class HostileStateJsonTests
{
    /// <summary>
    /// The three shapes register row R19 names, each throwing from a different place:
    /// an unknown discriminator and a missing required field throw out of
    /// <c>SerializableRollbackRecord.ToRollbackRecord</c>, a null element throws
    /// before it is even reached.
    /// </summary>
    [WindowsTheory("Windows-only state layout")]
    [InlineData("{\"records\":[{\"type\":\"no-such-type\"}]}")]
    [InlineData("{\"records\":[null]}")]
    [InlineData("{\"records\":[{\"type\":\"restore_file\"}]}")]   // required field missing
    [InlineData("{\"records\":null}")]
    [InlineData("this is not json at all")]
    public void Hostile_state_json_is_refused_without_an_unhandled_exception(string json)
    {
        var appId = NewAppId();
        Plant(appId, json);

        try
        {
            // Act
            var load = () => UninstallStateStore.Load(appId, InstallScope.User);

            // Assert
            load.Should().NotThrow(
                "rehydration happened OUTSIDE the try, so a one-line planted file made " +
                "every install and uninstall of this AppId die with an unhandled exception");

            var attempt = load();
            attempt.State.Should().BeNull("nothing may be replayed from a file that will not parse");
            attempt.RefusalReason.Should().NotBeNull(
                "the file is PRESENT — reporting an absence would print 'no uninstall state " +
                "found' for a file the operator can see on disk, and would hide the plant");
            UninstallStateStore.TryLoad(appId, InstallScope.User).Should().BeNull();
        }
        finally
        {
            Cleanup(appId);
        }
    }

    /// <summary>
    /// The refusal must reach the operator's log, not just the return value: the store
    /// has no logger of its own and the caller's progress sink is what feeds
    /// <c>/LOG</c>. Same channel R1's provenance refusal already uses.
    /// </summary>
    [WindowsFact("Windows-only state layout")]
    public void The_refusal_reason_is_reported_on_progress()
    {
        var appId = NewAppId();
        Plant(appId, "{\"records\":[{\"type\":\"no-such-type\"}]}");
        var progress = new CapturingProgress();

        try
        {
            var attempt = UninstallStateStore.Load(appId, InstallScope.User, progress);

            progress.Messages.Should().ContainSingle()
                .Which.Should().Contain("refusing state at").And.Contain(appId);
            attempt.RefusalReason.Should().Contain("refusing state at");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    /// <summary>
    /// The size ceiling, checked BEFORE the read. Proved by measuring the file rather
    /// than by trusting the constant: the fixture asserts it really did write more
    /// than the store will accept.
    /// </summary>
    [WindowsFact("Windows-only state layout")]
    public void An_oversized_state_file_is_refused_before_it_is_read()
    {
        var appId = NewAppId();
        var path = UninstallStateStore.PathFor(appId, InstallScope.User);
        Directory.CreateDirectory(UninstallStateStore.DirectoryFor(appId, InstallScope.User));

        try
        {
            // A syntactically valid journal padded past the ceiling, so the ONLY thing
            // that can reject it is the size check — not the parser.
            var padding = new string('x', 5 * 1024 * 1024);
            File.WriteAllText(path, "{\"appId\":\"" + padding + "\",\"records\":[]}");
            new FileInfo(path).Length.Should().BeGreaterThan(4L * 1024 * 1024,
                "the fixture must actually exceed the ceiling it is testing");

            var attempt = UninstallStateStore.Load(appId, InstallScope.User);

            attempt.State.Should().BeNull();
            attempt.RefusalReason.Should().Contain("ceiling");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    /// <summary>
    /// The record ceiling. The size cap alone does not bound the work — the records
    /// are tiny, so a file well under 4 MB can still declare a million of them.
    /// </summary>
    [WindowsFact("Windows-only state layout")]
    public void An_implausible_record_count_is_refused()
    {
        var appId = NewAppId();

        try
        {
            // 60,001 minimal records — over the 50,000 ceiling, comfortably under the
            // 4 MB one, so the record check is the only thing that can reject it.
            var sb = new StringBuilder("{\"records\":[");
            for (var i = 0; i <= 60_000; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                sb.Append("{\"type\":\"remove_directory\",\"path\":\"C:\\\\x\"}");
            }
            sb.Append("]}");
            Plant(appId, sb.ToString());

            var path = UninstallStateStore.PathFor(appId, InstallScope.User);
            new FileInfo(path).Length.Should().BeLessThan(4L * 1024 * 1024,
                "this fixture must be rejected by the RECORD ceiling, not the size one");

            var attempt = UninstallStateStore.Load(appId, InstallScope.User);

            attempt.State.Should().BeNull();
            attempt.RefusalReason.Should().Contain("record ceiling");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    /// <summary>
    /// The non-vacuity control for every test above: a well-formed journal of a
    /// realistic size still loads and still rehydrates its records. A "refuse
    /// everything" regression — the cheapest way to make hostile-input tests pass —
    /// fails here.
    /// </summary>
    [WindowsFact("Windows-only state layout")]
    public void A_well_formed_journal_still_loads()
    {
        var appId = NewAppId();

        try
        {
            var journal = new RollbackJournal();
            journal.Append(new RollbackRecord.RemoveDirectory(@"C:\Apps\Acme"));
            UninstallStateStore.Save(appId, journal, InstallScope.User, installDir: @"C:\Apps\Acme");

            var attempt = UninstallStateStore.Load(appId, InstallScope.User);

            attempt.RefusalReason.Should().BeNull();
            attempt.State.Should().NotBeNull();
            attempt.State!.Journal.Records.Should().HaveCount(1);
            attempt.State.InstallDir.Should().Be(@"C:\Apps\Acme");
        }
        finally
        {
            Cleanup(appId);
        }
    }

    /// <summary>
    /// An absence stays an absence. R19's refusal channel must not turn a first
    /// install — no state file at all — into "state was found but refused".
    /// </summary>
    [WindowsFact("Windows-only state layout")]
    public void A_missing_state_file_is_an_absence_not_a_refusal()
    {
        var attempt = UninstallStateStore.Load(NewAppId(), InstallScope.User);

        attempt.State.Should().BeNull();
        attempt.RefusalReason.Should().BeNull();
    }

    private static string NewAppId() => "sigil.test." + Guid.NewGuid().ToString("N");

    private static void Plant(string appId, string json)
    {
        Directory.CreateDirectory(UninstallStateStore.DirectoryFor(appId, InstallScope.User));
        File.WriteAllText(UninstallStateStore.PathFor(appId, InstallScope.User), json);
    }

    private static void Cleanup(string appId) =>
        UninstallStateStore.Delete(appId, InstallScope.User);

    /// <summary>Captures the messages reported on an <see cref="IProgress{T}"/> sink.</summary>
    private sealed class CapturingProgress : IProgress<StepProgress>
    {
        private readonly List<string> _messages = new();

        public IReadOnlyList<string> Messages => _messages;

        public void Report(StepProgress value)
        {
            if (value?.Message is not null)
            {
                _messages.Add(value.Message);
            }
        }
    }
}
