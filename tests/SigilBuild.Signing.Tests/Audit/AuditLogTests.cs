using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Signing.Audit;
using Xunit;

namespace SigilBuild.Signing.Tests.Audit;

public sealed class AuditLogTests
{
    [Fact]
    public async Task AppendAsync_WritesOneJsonLinePerEntry()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ndjson");
        try
        {
            var log = new AuditLog(path);
            await log.AppendAsync(new AuditEntry(
                Timestamp: new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero),
                Provider: "local",
                Artifact: "/tmp/a.msix",
                FileHash: "deadbeef",
                Thumbprint: "ABCD",
                Outcome: "success",
                Message: null));
            await log.AppendAsync(new AuditEntry(
                Timestamp: new DateTimeOffset(2026, 4, 30, 12, 1, 0, TimeSpan.Zero),
                Provider: "azure-trusted-signing",
                Artifact: "/tmp/b.msix",
                FileHash: "cafebabe",
                Thumbprint: "EFGH",
                Outcome: "failure",
                Message: "quota exceeded"));

            var lines = await File.ReadAllLinesAsync(path);
            lines.Should().HaveCount(2);
            lines[0].Should().Contain("\"provider\":\"local\"");
            lines[0].Should().Contain("\"file_hash\":\"deadbeef\"");
            lines[0].Should().Contain("\"thumbprint\":\"ABCD\"");
            lines[1].Should().Contain("\"outcome\":\"failure\"");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task AppendAsync_DoesNotWriteUtf8Bom()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ndjson");
        try
        {
            var log = new AuditLog(path);
            await log.AppendAsync(new AuditEntry(
                Timestamp: new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero),
                Provider: "local",
                Artifact: "/tmp/a.msix",
                FileHash: "deadbeef",
                Thumbprint: null,
                Outcome: "success",
                Message: null));

            var bytes = await File.ReadAllBytesAsync(path);
            bytes.Length.Should().BeGreaterThan(3);
            (bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF).Should().BeFalse(
                "NDJSON files should not start with a UTF-8 BOM");
        }
        finally { File.Delete(path); }
    }
}
