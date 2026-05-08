using System.Linq;
using FluentAssertions;
using SigilBuild.Signing.Local;
using Xunit;

namespace SigilBuild.Signing.Tests.Local;

public sealed class TimestampAuthorityTests
{
    [Fact]
    public void Candidates_PrefersConfiguredUrlFirst()
    {
        var candidates = TimestampAuthority.Candidates("http://my.tsa/").ToArray();

        candidates[0].Should().Be("http://my.tsa/");
        candidates.Skip(1).Should().Contain("http://timestamp.digicert.com");
        candidates.Skip(1).Should().Contain("http://timestamp.sectigo.com");
    }

    [Fact]
    public void Candidates_NullConfigured_StartsWithDigicert()
    {
        var candidates = TimestampAuthority.Candidates(null).ToArray();
        candidates[0].Should().Be("http://timestamp.digicert.com");
    }
}
