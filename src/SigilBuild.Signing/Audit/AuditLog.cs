using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SigilBuild.Signing.Audit;

public sealed class AuditLog
{
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

    public async Task AppendAsync(AuditEntry entry)
    {
        var json = JsonSerializer.Serialize(entry, AuditEntryJsonContext.Default.AuditEntry);
        await File.AppendAllTextAsync(_path, json + "\n", Encoding.UTF8);
    }
}
