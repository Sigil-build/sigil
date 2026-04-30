using System.IO;
using FluentAssertions;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using Xunit;

namespace SigilBuild.Core.Tests.Diagnostics;

public class DiagnosticReporterTests
{
    [Fact]
    public void Format_ProducesGccStyleSourceLineWithCode()
    {
        var diag = new Diagnostic(
            DiagnosticSeverity.Error,
            DiagnosticCodes.SchemaViolation,
            "property 'app.version' must match SemVer pattern",
            new SourceLocation("sigil.yaml", 4, 12),
            "https://docs.sigil.build/diagnostics/SIG0010");

        var sw = new StringWriter();
        DiagnosticReporter.Write(sw, new[] { diag }, useColor: false);

        var output = sw.ToString();
        output.Should().Contain("sigil.yaml:4:12");
        output.Should().Contain("error");
        output.Should().Contain("SIG0010");
        output.Should().Contain("property 'app.version' must match SemVer pattern");
        output.Should().Contain("https://docs.sigil.build/diagnostics/SIG0010");
    }

    [Fact]
    public void Format_OmitsLocationWhenUnknown()
    {
        var diag = new Diagnostic(
            DiagnosticSeverity.Warning,
            DiagnosticCodes.MissingOptionalField,
            "consider adding 'app.description'",
            SourceLocation.Unknown,
            "https://docs.sigil.build/diagnostics/SIG0050");

        var sw = new StringWriter();
        DiagnosticReporter.Write(sw, new[] { diag }, useColor: false);

        sw.ToString().Should().NotContain("0:0").And.Contain("warning");
    }
}
