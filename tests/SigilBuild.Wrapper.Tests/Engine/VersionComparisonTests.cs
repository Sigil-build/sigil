using FluentAssertions;
using SigilBuild.Wrapper.Engine;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Engine;

/// <summary>
/// The single version-comparison used by <c>version_gte(...)</c> and the P3 upgrade
/// decision — numeric dotted ordering with an ordinal fallback, and the documented
/// (weak) pre-release handling.
/// </summary>
public sealed class VersionComparisonTests
{
    [Theory]
    [InlineData("1.0.0", "2.0.0")]
    [InlineData("1.2.3", "1.2.4")]
    [InlineData("1.9.0", "1.10.0")]  // numeric, not lexicographic (9 < 10)
    public void Numeric_ordering_is_used_when_both_parse(string lower, string higher)
    {
        VersionComparison.Compare(lower, higher).Should().BeNegative();
        VersionComparison.Compare(higher, lower).Should().BePositive();
        VersionComparison.Compare(lower, lower).Should().Be(0);
    }

    [Fact]
    public void Wellformed_recognizes_numeric_dotted_versions_only()
    {
        VersionComparison.IsWellFormed("1.0").Should().BeTrue();
        VersionComparison.IsWellFormed("1.2.3.4").Should().BeTrue();

        VersionComparison.IsWellFormed("1").Should().BeFalse();          // System.Version needs ≥ 2 components
        VersionComparison.IsWellFormed("1.2.0-rc1").Should().BeFalse();  // SemVer pre-release
        VersionComparison.IsWellFormed("v1.2").Should().BeFalse();
        VersionComparison.IsWellFormed("").Should().BeFalse();
        VersionComparison.IsWellFormed(null).Should().BeFalse();
    }

    [Fact]
    public void Prerelease_tag_falls_back_to_ordinal_compare_not_semver()
    {
        // Documented limitation: "1.2.0-rc1" is not understood as < "1.2.0";
        // it degrades to an ordinal compare rather than SemVer precedence.
        VersionComparison.IsWellFormed("1.2.0-rc1").Should().BeFalse();
        // Ordinal: "1.2.0-rc1" > "1.2.0" because '-' extends the equal prefix.
        VersionComparison.Compare("1.2.0-rc1", "1.2.0").Should().BePositive();
    }
}
