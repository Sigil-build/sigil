namespace SigilBuild.Wrapper.IntegrationTests;

using System;
using System.IO;
using System.Runtime.Versioning;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Cli;
using SigilBuild.Wrapper.Engine;

/// <summary>
/// A real, resolved <c>install_dir</c> for the elevated system-step legs
/// (<c>scheduled_task_create</c>, <c>com_register</c>) plus the
/// <see cref="StepContext"/> anchored on it — built through the same
/// <see cref="StepContext.From"/> / <see cref="InstallDirResolver"/> path
/// <c>InstallSession</c> uses on the silent (<c>/D=</c>) install route.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists (R67).</b> These two legs were originally written against
/// <see cref="StepContext.Empty"/> and system binaries
/// (<c>%SystemRoot%\System32\cmd.exe</c>, <c>kernel32.dll</c>) chosen because they
/// are "present on every Windows host". Anchoring every SYSTEM-level step target to
/// the run's resolved <c>install_dir</c> (R3, R9, R16) made both legs fail at
/// <see cref="PrivilegedTargetGuard"/> — no resolved <c>install_dir</c> to anchor
/// against. Both halves are fixed by giving the run a genuine anchor and putting the
/// target inside it — the shape <c>docs/guides/install-steps.md</c> prescribes
/// ("sequence a <c>file_copy</c> into <c>install_dir</c> first, then point the
/// privileged step at the copied location").
/// </para>
/// <para>
/// <b>Why not <c>allow_outside_install_dir</c>.</b> That opt-out is a
/// <em>destination</em>-containment opt-out for <c>file_copy</c> and the config
/// editors. <c>docs/manifest-reference.md</c> and the guide both state it does not
/// relax the privileged-target rule on <c>service_install</c> /
/// <c>scheduled_task_create</c> / <c>com_register</c> / <c>firewall_rule</c> — on
/// those step types it is an unrecognized field (<c>SIG0231</c>). The
/// <em>uninstall replay</em> anchor was separately widened with the signed blob's
/// declared roots (<see cref="SignedDeclarations"/>, R44, R51); that did not touch
/// this guard either. There is therefore no opt-out to mirror, and no fixture here
/// uses one.
/// </para>
/// <para>
/// <b>Why <c>%ProgramFiles%</c> and not <c>%TEMP%</c>.</b> The guard has two arms,
/// and a temp directory fails both: <see cref="InstallDirResolver"/> refuses a
/// machine-scope <c>install_dir</c> outside the scope root (R3), and
/// <see cref="StateDirectorySecurity.IsAdminOnlyWritable"/> refuses a target whose
/// directory an unprivileged user can rewrite — which the invoking user's temp
/// directory always is. A per-run GUID directory under <c>%ProgramFiles%</c> is the
/// one sandbox that is also a faithful machine-scope install root, so that is what
/// this creates and deletes. It needs elevation, which
/// <see cref="VmSystemStepsFactAttribute"/> already requires for these legs.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class SystemStepInstallDir : IDisposable
{
    private const string AppId = "com.example.sigil-integration";
    private const string AppName = "SigilIntegration";

    private SystemStepInstallDir(string dir, StepContext context)
    {
        Dir = dir;
        Context = context;
    }

    /// <summary>The created, admin-only-writable install directory.</summary>
    public string Dir { get; }

    /// <summary>A <see cref="StepContext"/> whose <c>InstallDir</c> is <see cref="Dir"/>.</summary>
    public StepContext Context { get; }

    /// <summary>
    /// Resolve a fresh per-run machine-scope <c>install_dir</c> the way the silent
    /// install route does — <c>/D=&lt;dir&gt;</c> through
    /// <see cref="CommandLineParser.Parse"/> and <see cref="StepContext.From"/>,
    /// which calls <see cref="InstallDirResolver"/> including its R3 scope-root
    /// containment check. Pure: nothing is created on disk.
    /// </summary>
    /// <remarks>
    /// Exposed separately from <see cref="CreateElevated"/> so the unelevated pin
    /// test (<see cref="SystemStepAnchorTests"/>) can prove the anchor this fixture
    /// hands the guard is one the guard accepts, without needing admin rights or the
    /// VM matrix.
    /// </remarks>
    public static (string Dir, StepContext Context) ResolveAnchor()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "SigilIt_" + Guid.NewGuid().ToString("N"));

        var blob = Blob();
        var context = StepContext.From(
            blob,
            CommandLineParser.Parse(new[] { "/silent", "/D=" + dir }, blob.Parameters),
            scope: InstallScope.Machine);

        return (dir, context);
    }

    /// <summary>
    /// <see cref="ResolveAnchor"/> plus the directory itself, created and verified
    /// admin-only writable. Requires an elevated process.
    /// </summary>
    public static SystemStepInstallDir CreateElevated()
    {
        var (dir, context) = ResolveAnchor();

        // A real install creates its install_dir with a plain CreateDirectory and
        // inherits %ProgramFiles%' admin-only DACL; an elevated process's default
        // owner is BUILTIN\Administrators, so that alone normally satisfies the
        // guard's ACL arm. CreateHardened is the repair, not the mechanism: it
        // returns immediately when the inherited result already passes
        // (StateDirectorySecurity.IsTrusted) and otherwise stamps the same
        // admin-only DACL the engine uses for its own directories. Without it, a
        // runner with an unusual %ProgramFiles% ACL would surface as a step refusal
        // that reads like a product bug.
        Directory.CreateDirectory(dir);
        var fixture = new SystemStepInstallDir(dir, context);

        try
        {
            StateDirectorySecurity.CreateHardened(dir);

            StateDirectorySecurity.IsAdminOnlyWritable(dir).Should().BeTrue(
                $"'{dir}' must be admin-only writable for a privileged step target inside it to be " +
                "accepted — if this fails the host's %ProgramFiles% permissions are unusual, " +
                "not the guard");
        }
        catch
        {
            // Do not leave a directory in %ProgramFiles% behind when the
            // precondition itself is what failed.
            fixture.Dispose();
            throw;
        }

        return fixture;
    }

    /// <summary>
    /// Copy <paramref name="sourceFile"/> into the install directory as
    /// <paramref name="targetName"/> and return the full path — the
    /// <c>file_copy</c>-into-<c>install_dir</c> step these legs would be sequenced
    /// after in a real manifest.
    /// </summary>
    public string CopyIn(string sourceFile, string targetName)
    {
        File.Exists(sourceFile).Should().BeTrue($"'{sourceFile}' must exist to be copied into install_dir");

        var target = Path.Combine(Dir, targetName);
        File.Copy(sourceFile, target, overwrite: true);
        return target;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch
        {
            // Best-effort: a per-run GUID directory under %ProgramFiles% that the
            // next run never reuses. Leaving it behind must not fail a green test.
        }
    }

    private static WrapperBlob Blob() =>
        new(
            AppId: AppId,
            Parameters: Array.Empty<ParameterDefinition>(),
            InstallSteps: Array.Empty<InstallStep>(),
            PreInstall: Array.Empty<InstallStep>(),
            PostInstall: Array.Empty<InstallStep>(),
            UpdateSteps: Array.Empty<InstallStep>(),
            Scope: InstallScope.Machine,
            Options: null,
            AppName: AppName,
            InstallDir: null);
}
