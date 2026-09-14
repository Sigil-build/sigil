// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using Xunit;

namespace SigilBuild.Core.Tests.Manifest;

public class EnvironmentReaderTests
{
    [Fact]
    public void ProcessEnvironmentReader_ReturnsValueForKnownVariable()
    {
        const string name = "SIGIL_TEST_VAR_123";
        Environment.SetEnvironmentVariable(name, "yes");
        try
        {
            var reader = new ProcessEnvironmentReader();
            reader.Get(name).Should().Be("yes");
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void ProcessEnvironmentReader_ReturnsNullForMissing()
    {
        var reader = new ProcessEnvironmentReader();
        reader.Get("SIGIL_DEFINITELY_NOT_SET_42").Should().BeNull();
    }
}
