using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using SigilBuild.Localization.Generator;
using Xunit;

namespace SigilBuild.Localization.Generator.Tests;

/// <summary>
/// Drives <see cref="StringsGenerator"/> itself through a real Roslyn <see cref="GeneratorDriver"/>
/// (unlike the other test files, which call <c>CatalogParser</c>/<c>CatalogValidator</c> directly).
/// This is the only place that proves the Error-suppresses-emission behavior described in the
/// comment above <c>StringsGenerator</c>'s <c>hasError</c> flag: that <c>AddSource</c> is actually
/// skipped on an Error-severity problem, and actually fires when there is none (or only a Warning).
/// </summary>
public class StringsGeneratorTests
{
    private sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        public InMemoryAdditionalText(string path, string content)
        {
            Path = path;
            _text = SourceText.From(content, System.Text.Encoding.UTF8);
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }

    private static GeneratorDriverRunResult Run(params (string Name, string Content)[] catalogs)
    {
        var additionalTexts = catalogs
            .Select(c => (AdditionalText)new InMemoryAdditionalText(c.Name, c.Content))
            .ToImmutableArray();

        var compilation = CSharpCompilation.Create(
            assemblyName: "SigilBuild.Localization.Generator.Tests.Generated",
            references: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });

        var driver = CSharpGeneratorDriver.Create(
            generators: new[] { new StringsGenerator().AsSourceGenerator() },
            additionalTexts: additionalTexts);

        var ranDriver = driver.RunGenerators(compilation);

        return ranDriver.GetRunResult();
    }

    [Fact]
    public void CleanCatalogs_SourceIsEmitted()
    {
        var result = Run(
            ("Strings.en.txt", "nav.back = Back\n"),
            ("Strings.uk.txt", "nav.back = Назад\n"));

        result.Diagnostics.Should().BeEmpty();

        var sources = result.Results.Single().GeneratedSources;
        sources.Should().ContainSingle(s => s.HintName == "Strings.g.cs");
        sources.Single(s => s.HintName == "Strings.g.cs").SourceText.ToString()
            .Should().Contain("public static string NavBack(Lang lang)");
    }

    [Fact]
    public void DuplicateKey_ErrorSuppressesEmission()
    {
        // SIGLOC004: "a" is duplicated in en. This is an Error, so no source must be emitted —
        // emitting anyway would additionally surface an opaque CS0111 pointing at generated code.
        var result = Run(
            ("Strings.en.txt", "a = A\na = B\n"),
            ("Strings.uk.txt", "a = А\n"));

        result.Diagnostics.Select(d => d.Id).Should().Contain("SIGLOC004");
        result.Results.Single().GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void MissingTranslation_WarningDoesNotSuppressEmission()
    {
        // SIGLOC002 is a Warning (a partial translation is legal and falls back to English) —
        // it must NOT suppress emission. This is the asymmetry that matters most: an Error and
        // a Warning both come out of CatalogValidator.Validate, but only one blocks AddSource.
        var result = Run(
            ("Strings.en.txt", "a = A\nb = B\n"),
            ("Strings.uk.txt", "a = А\n"));

        result.Diagnostics.Select(d => d.Id).Should().Contain("SIGLOC002");

        var sources = result.Results.Single().GeneratedSources;
        sources.Should().ContainSingle(s => s.HintName == "Strings.g.cs");
    }

    [Fact]
    public void MethodNameCollision_ErrorSuppressesEmission()
    {
        // SIGLOC006: both keys' '.' and '_' split to the same generated method name
        // (LocationErrorNotAbsolute), which would make the emitter write two identical
        // signatures -> CS0111. Error severity, so no source must be emitted.
        var result = Run(
            ("Strings.en.txt", "location.error.notAbsolute = A\nlocation.error.not_absolute = B\n"),
            ("Strings.uk.txt", "location.error.notAbsolute = А\nlocation.error.not_absolute = Б\n"));

        result.Diagnostics.Select(d => d.Id).Should().Contain("SIGLOC006");
        result.Results.Single().GeneratedSources.Should().BeEmpty();
    }
}
