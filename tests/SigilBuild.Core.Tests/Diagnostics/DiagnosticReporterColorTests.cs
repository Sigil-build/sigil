// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System.IO;
using FluentAssertions;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using Xunit;

namespace SigilBuild.Core.Tests.Diagnostics;

public class DiagnosticReporterColorTests
{
    [Fact]
    public void Format_WithColorEnabled_StillPrintsLocationAndCode()
    {
        var diag = new Diagnostic(
            DiagnosticSeverity.Info,
            "SIG9999",
            "informational note",
            new SourceLocation("a.yaml", 10, 5),
            "https://docs.sigil.build/diagnostics/SIG9999");

        var sw = new StringWriter();
        DiagnosticReporter.Write(sw, new[] { diag }, useColor: true);

        var output = sw.ToString();
        output.Should().Contain("a.yaml:10:5");
        output.Should().Contain("info");
        output.Should().Contain("SIG9999");
    }
}
