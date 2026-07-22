using System;
using FluentAssertions;
using SigilBuild.Wrapper.Core.Localization;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Localization;

[Collection("SessionLanguage")] // static state: must not run in parallel
public sealed class SessionLanguageTests : IDisposable
{
    // Restore the assembly-wide English default (set once by
    // TestAssemblySetup's ModuleInitializer) rather than nulling it out — a
    // bare ResetForTesting() here would leave SessionLanguage unset for
    // whichever test class the runner happens to run next, reintroducing the
    // Debug-mode throw for production code (PrerequisiteRunner, InstallSession)
    // that now reads SessionLanguage.Current without itself calling
    // ResolveSessionLanguage first.
    public void Dispose() => SessionLanguage.SetForTesting(Lang.En);

    [Fact]
    public void Current_BeforeSet_ThrowsInDebug()
    {
        SessionLanguage.ResetForTesting();
#if DEBUG
        var act = () => _ = SessionLanguage.Current;
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*before*language*resolved*");
#else
        SessionLanguage.Current.Should().Be(Lang.En);
#endif
    }

    [Fact]
    public void Set_ThenCurrent_ReturnsIt()
    {
        SessionLanguage.Set(Lang.Uk);
        SessionLanguage.Current.Should().Be(Lang.Uk);
    }

    [Fact]
    public void Strings_ResolveAgainstLang()
    {
        Strings.NavBack(Lang.En).Should().Be("Back");
        Strings.NavBack(Lang.Uk).Should().Be("Назад");
    }

    [Fact]
    public void Pseudo_IsBracketed()
    {
        Strings.NavBack(Lang.Pseudo).Should().StartWith("[").And.EndWith("]");
    }
}
