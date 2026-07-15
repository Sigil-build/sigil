using FluentAssertions;
using SigilBuild.Core.Manifest;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

public class LanguageTagTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("uk")]
    [InlineData("de")]
    [InlineData("pt-BR")]
    [InlineData("zh-Hans")]
    [InlineData("de-AT")]
    [InlineData("qps")]
    [InlineData("EN")]       // ordinal-ignore-case
    [InlineData("pt-br")]
    public void Valid(string tag) => LanguageTag.IsValid(tag).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("e")]            // primary subtag too short
    [InlineData("engl")]         // primary subtag too long
    [InlineData("pseudo")]       // 6 alpha: rejected, which is why /lang=pseudo cannot reach Lang.Pseudo
    [InlineData("!!")]
    [InlineData("en-")]          // empty subtag
    [InlineData("en--US")]
    [InlineData("en-toolongsubtag")] // subtag > 8
    [InlineData("en US")]
    public void Invalid(string? tag) => LanguageTag.IsValid(tag).Should().BeFalse();
}
