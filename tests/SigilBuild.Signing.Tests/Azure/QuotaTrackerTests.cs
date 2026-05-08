using System;
using System.IO;
using FluentAssertions;
using SigilBuild.Signing.Azure;
using Xunit;

namespace SigilBuild.Signing.Tests.Azure;

public sealed class QuotaTrackerTests
{
    [Fact]
    public void RecordAndCount_Roundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var t = new QuotaTracker(path);
            t.RecordSign(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
            t.RecordSign(new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.Zero));
            t.RecordSign(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

            t.CountForMonth(2026, 5).Should().Be(2);
            t.CountForMonth(2026, 6).Should().Be(1);
        }
        finally { File.Delete(path); }
    }
}
