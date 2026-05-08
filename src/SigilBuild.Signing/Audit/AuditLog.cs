using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SigilBuild.Signing.Audit;

public sealed class AuditLog
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _path;

    public AuditLog(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public static AuditLog Default()
    {
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        return new AuditLog(Path.Combine(home, ".sigil", "audit.log"));
    }

    public async Task AppendAsync(AuditEntry entry, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(entry, AuditEntryJsonContext.Default.AuditEntry);
        await using var stream = new FileStream(
            _path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read | FileShare.Write,
            bufferSize: 4096,
            useAsync: true);
        await using var writer = new StreamWriter(stream, Utf8NoBom) { NewLine = "\n" };
        await writer.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
    }
}
