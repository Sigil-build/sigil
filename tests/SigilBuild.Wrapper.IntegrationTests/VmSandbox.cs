using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace SigilBuild.Wrapper.IntegrationTests;

/// <summary>
/// Minimal "sandbox" for integration testing the wrapper exe. Currently a
/// temp-directory + process runner — NOT full Windows Sandbox isolation.
/// Tracks the install root so tests can assert filesystem state after install.
/// </summary>
/// <remarks>
/// FUTURE: hardening to a real Windows Sandbox (.wsb profile mounting the
/// SDK output dir + an empty <c>C:\AppDir</c> target) lands when the plan
/// calls for true VM isolation. For Task 13's scope, host-process invocation
/// against a temp directory is sufficient — the tests still drive the wrapper
/// exe end-to-end, they just don't get the OS-level sandboxing.
/// </remarks>
internal sealed class VmSandbox : IDisposable
{
    public string Root { get; }

    public string AppDir { get; }

    public VmSandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "sigil-vm-" + Guid.NewGuid().ToString("N"));
        AppDir = Path.Combine(Root, "AppDir");
        Directory.CreateDirectory(AppDir);
    }

    public async Task<int> RunAsync(string exePath, params string[] args)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Root,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync().ConfigureAwait(false);
        return p.ExitCode;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup — the OS will reap %TEMP% eventually.
        }
    }
}
