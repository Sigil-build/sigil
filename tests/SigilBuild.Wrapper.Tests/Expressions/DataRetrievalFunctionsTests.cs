using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Win32;
using SigilBuild.Wrapper.Expressions;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Expressions;

/// <summary>
/// P1 (gap G1) data-retrieval functions: <c>registry_read</c>, <c>env</c>,
/// <c>file_version</c>, <c>installed_version</c>. All are read-only, AOT-safe, and
/// total — they return <c>""</c> on the absent / denied / bad-input path rather
/// than throwing (ADR-008 §1.2/§1.3).
/// </summary>
public class DataRetrievalFunctionsTests
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyCtx =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    private static string EvalStr(string expr)
    {
        var v = new Evaluator().EvaluateValue(expr, EmptyCtx);
        return v as string ?? v?.ToString() ?? string.Empty;
    }

    [Fact]
    public void Env_returns_value_when_set_and_empty_when_unset()
    {
        var name = "SIGIL_P1_TEST_" + Guid.NewGuid().ToString("N");
        try
        {
            Environment.SetEnvironmentVariable(name, "hello-env");
            EvalStr($"env('{name}')").Should().Be("hello-env");
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        EvalStr($"env('{name}')").Should().BeEmpty("an unset variable reads as \"\"");
    }

    [Theory]
    // bad hive → "" (unrecognized hive is caught, not thrown)
    [InlineData("registry_read('BOGUS_HIVE', 'Some\\\\Key', 'V')")]
    // access-denied → "" (HKLM\SECURITY is ACL-restricted for non-admins; on
    // non-Windows the OS guard short-circuits to "" too)
    [InlineData("registry_read('HKLM', 'SECURITY', 'anything')")]
    // absent file → "" (never loads/executes the file)
    [InlineData("file_version('/no/such/file-xyz-p1.dll')")]
    // not-installed app → ""
    [InlineData("installed_version('com.acme.not-installed-xyz-p1')")]
    public void Absent_or_invalid_reads_return_empty_string(string expr)
        => EvalStr(expr).Should().BeEmpty();

    [Fact]
    public void Functions_are_usable_inside_boolean_when_expressions()
    {
        // installed_version()=="" drives the common "is this a fresh install?" guard.
        new Evaluator()
            .EvaluateBool("installed_version('com.acme.not-installed-xyz-p1') == ''", EmptyCtx)
            .Should().BeTrue();
    }
}

/// <summary>
/// Windows-only legs of the P1 data-retrieval functions — they touch the real
/// registry (HKCU scratch keys, no admin needed) and a versioned system file.
/// </summary>
[SupportedOSPlatform("windows")]
public class DataRetrievalFunctionsWindowsTests
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyCtx =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    private static string EvalStr(string expr)
    {
        var v = new Evaluator().EvaluateValue(expr, EmptyCtx);
        return v as string ?? v?.ToString() ?? string.Empty;
    }

    [Fact]
    public void RegistryRead_returns_value_when_present_and_empty_when_absent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var k = TestRegistry.CreateScratchKey();
        k.SetValue("Path", @"C:\Apps\Acme");

        // present
        EvalStr($"registry_read('HKCU', '{k.Path}', 'Path')").Should().Be(@"C:\Apps\Acme");
        // absent value under an existing key
        EvalStr($"registry_read('HKCU', '{k.Path}', 'Missing')").Should().BeEmpty();
        // absent key entirely
        EvalStr($"registry_read('HKCU', 'Software\\Sigil-test\\nope-{Guid.NewGuid():N}', 'X')")
            .Should().BeEmpty();
    }

    [Fact]
    public void InstalledVersion_reads_DisplayVersion_from_own_ARP_entry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var appId = "sigil-p1-test-" + Guid.NewGuid().ToString("N");
        var keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + appId;
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                k!.SetValue("DisplayVersion", "2.3.4");
            }

            EvalStr($"installed_version('{appId}')").Should().Be("2.3.4");
        }
        finally
        {
#pragma warning disable CA1031 // best-effort scratch cleanup
            try { Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false); }
            catch { /* best-effort */ }
#pragma warning restore CA1031
        }
    }

    [Fact]
    public void FileVersion_reads_version_of_a_versioned_system_file()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        if (!File.Exists(path))
        {
            return;
        }

        // kernel32.dll always carries a file version resource.
        EvalStr($"file_version('{path}')").Should().NotBeEmpty();
    }
}
