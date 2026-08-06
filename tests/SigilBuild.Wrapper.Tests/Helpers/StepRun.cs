namespace SigilBuild.Wrapper.Tests.Helpers;

using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Steps;

/// <summary>
/// Runs a single step against a fresh rollback journal and enforces the
/// lane-wide safety invariant centrally, so it cannot be forgotten by the next
/// test someone adds.
/// </summary>
/// <remarks>
/// <para>
/// <b>The invariant: a step that refuses must journal nothing.</b> It matters
/// because a rollback record is not inert — the engine replays the journal when a
/// step fails under <c>OnFailure.Fail</c>, and several record types are
/// destructive: <c>DeleteShortcut</c> unlinks a <c>.lnk</c>,
/// <c>DeleteScheduledTask</c> / <c>RemoveService</c> / <c>DeleteFirewallRule</c>
/// tear down system objects, <c>UnregisterCom</c> calls
/// <c>DllUnregisterServer</c>. Each undoes its target <em>unconditionally</em>,
/// without checking that this installer created it. So a step that journals
/// before it validates, and then refuses, deletes somebody else's same-named
/// object — on the machine running the test suite as much as on a user's.
/// </para>
/// <para>
/// This was found twice by review, in two different steps
/// (<c>ScheduledTaskCreateStep</c>, then <c>ShortcutCreateStep</c>), each time
/// because a test asserted the failure but not the journal. Asserting it inside
/// the runner rather than in each test is what stops there being a third.
/// </para>
/// </remarks>
internal static class StepRun
{
    /// <summary>
    /// Run <paramref name="step"/> expecting a refusal. Asserts the step failed
    /// AND that it journaled nothing, then hands back the result so the caller can
    /// assert on the message.
    /// </summary>
    public static async Task<StepResult> RefusalAsync(IStep step, StepContext ctx)
    {
        var journal = new RollbackJournal();

        var result = await step.RunAsync(ctx, journal, CancellationToken.None);

        result.Success.Should().BeFalse("this arrangement is expected to be refused");
        journal.Records.Should().BeEmpty(
            "a refused step attempted nothing, so it must have journaled nothing — a rollback " +
            "record here would be replayed and would undo an object this installer never created");
        return result;
    }

    /// <summary>
    /// Run <paramref name="step"/> and return both the result and the journal, for
    /// the accept-direction cases that legitimately record an undo.
    /// </summary>
    public static async Task<(StepResult Result, RollbackJournal Journal)> Async(IStep step, StepContext ctx)
    {
        var journal = new RollbackJournal();
        var result = await step.RunAsync(ctx, journal, CancellationToken.None);
        return (result, journal);
    }
}
