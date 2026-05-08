using System;
using System.IO;

namespace SigilBuild.Wrapper.Tests.Helpers;

internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "sigil-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
#pragma warning disable CA1031 // Best-effort cleanup of an OS temp directory.
        catch { /* best-effort */ }
#pragma warning restore CA1031
    }
}
