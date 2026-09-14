// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using FluentAssertions;
using SigilBuild.Installer.BrandGenerator;
using Xunit;

namespace SigilBuild.Installer.BrandGenerator.Tests;

public class WcagContrastTests
{
    [Theory]
    [InlineData("#000000", "#FFFFFF", 21.0)]   // black on white
    [InlineData("#1F2937", "#FFFFFF", 14.68)]  // dark slate on white
    [InlineData("#FFFFFF", "#FFFFFF", 1.0)]    // identical = 1.0
    public void Ratio_KnownPairs(string fg, string bg, double expected)
    {
        var ratio = WcagContrast.Ratio(fg, bg);
        ratio.Should().BeApproximately(expected, 0.05);
    }

    [Theory]
    [InlineData("#1F2937", true)]
    [InlineData("#FFEE00", false)] // pale yellow on white fails
    public void PassesAA_AgainstWhite(string color, bool expected)
    {
        WcagContrast.PassesAaAgainstWhite(color).Should().Be(expected);
    }
}
