// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using Xunit;

namespace SigilBuild.Installer.Host.Tests;

/// <summary>
/// The wizard log is UTF-8; a BOM on creation lets Windows PowerShell 5.1 read it as
/// UTF-8 instead of ANSI (which renders an arrow as mojibake). (R89)
/// </summary>
public sealed class InstallerLogEncodingTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "sigil-logenc-" + Path.GetRandomFileName());

    public InstallerLogEncodingTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void New_log_file_starts_with_a_single_utf8_bom()
    {
        // Arrange
        var path = Path.Combine(_dir, "new.log");

        // Act
        InstallerLog.AppendLineTo(path, "first → line");
        InstallerLog.AppendLineTo(path, "second line");

        // Assert
        var bytes = File.ReadAllBytes(path);
        bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        var text = Encoding.UTF8.GetString(bytes);
        text.Count(c => c == '﻿').Should().Be(1, "a BOM is written once, at creation");
        text.Should().Contain("first → line").And.Contain("second line");
    }

    [Fact]
    public void Appending_to_an_existing_file_does_not_add_a_bom()
    {
        // Arrange
        var path = Path.Combine(_dir, "existing.log");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("from the wrapper\n"));

        // Act
        InstallerLog.AppendLineTo(path, "wizard line");

        // Assert
        var bytes = File.ReadAllBytes(path);
        bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF });
        Encoding.UTF8.GetString(bytes).Should().Be("from the wrapper\nwizard line\n");
    }
}
