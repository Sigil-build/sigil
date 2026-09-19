// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;
using SigilBuild.Core.Manifest;
using SigilBuild.Packaging.ExeWrapper;
using Xunit;

namespace SigilBuild.Packaging.Tests.ExeWrapper;

/// <summary>
/// SIG0123 — packing a manifest that resolves <c>payload://</c> out of a source
/// directory that holds no files.
/// </summary>
/// <remarks>
/// Both directions matter, and the second is why the check asks about
/// <c>payload://</c> rather than simply refusing an empty payload. An installer
/// that legitimately carries none — one that only writes registry values, or a
/// <c>--payload web</c> stub that downloads the real package at install time —
/// must still pack.
/// </remarks>
public sealed class ExeWrapperEmptyPayloadTests
{
    [RuntimeStagedFact]
    public async Task Pack_refuses_when_the_manifest_resolves_payload_but_nothing_was_packed()
    {
        using var work = new TempDir();
        var payloadDir = Path.Combine(work.Path, "payload");
        Directory.CreateDirectory(payloadDir);        // exists, and is empty — the defect

        var manifestPath = Path.Combine(work.Path, "sigil.yaml");
        await File.WriteAllTextAsync(manifestPath, """
            spec: v1.0
            app: { id: com.example.Empty, name: Empty, version: 1.0.0, publisher: Example Inc. }
            build: { source: ./payload }
            package: { formats: [exe], architectures: [x64] }
            install_steps:
              - id: deploy
                type: file_copy
                from: payload://**
                to: "{install_dir}"
            """);

        var load = await ManifestLoader.LoadAsync(manifestPath, new ProcessEnvironmentReader());
        load.Manifest.Should().NotBeNull();

        var outDir = Path.Combine(work.Path, "dist");
        Directory.CreateDirectory(outDir);

        var result = await new ExeWrapperPackager().PackAsync(
            load.Manifest!,
            new PackOptions(payloadDir, outDir, PackageFormat.Exe, TargetArchitecture.X64),
            CancellationToken.None);

        result.Artifact.Should().BeNull("an installer that would install nothing must not be produced");

        var refusal = result.Diagnostics.SingleOrDefault(
            d => d.Code == DiagnosticCodes.PayloadReferencedButEmpty);
        refusal.Should().NotBeNull();
        refusal!.Severity.Should().Be(DiagnosticSeverity.Error);
        refusal.Message.Should().Contain("payload://");
        refusal.Message.Should().Contain("contains no files");

        // And nothing may be left behind. The runtime copy used to happen before
        // this check, so a refusal dropped a 25 MB unstamped host into dist/ under
        // the installer's own name — a file that looks like the artifact, runs, and
        // installs nothing.
        Directory.EnumerateFileSystemEntries(outDir).Should().BeEmpty(
            "a refused pack must not leave something behind for someone to ship");
    }

    [RuntimeStagedFact]
    public async Task Pack_allows_an_empty_payload_when_no_step_resolves_one()
    {
        using var work = new TempDir();
        var payloadDir = Path.Combine(work.Path, "payload");
        Directory.CreateDirectory(payloadDir);

        // Same empty directory, but nothing reads from it: a registry-only
        // installer is a legitimate shape and must keep packing.
        var manifestPath = Path.Combine(work.Path, "sigil.yaml");
        await File.WriteAllTextAsync(manifestPath, """
            spec: v1.0
            app: { id: com.example.RegOnly, name: RegOnly, version: 1.0.0, publisher: Example Inc. }
            build: { source: ./payload }
            package: { formats: [exe], architectures: [x64] }
            install_steps:
              - id: mark
                type: registry_write
                hive: HKCU
                key: Software\Example\RegOnly
                name: Installed
                value: "1"
                value_type: string
            """);

        var load = await ManifestLoader.LoadAsync(manifestPath, new ProcessEnvironmentReader());
        load.Manifest.Should().NotBeNull();

        var outDir = Path.Combine(work.Path, "dist");
        Directory.CreateDirectory(outDir);

        var result = await new ExeWrapperPackager().PackAsync(
            load.Manifest!,
            new PackOptions(payloadDir, outDir, PackageFormat.Exe, TargetArchitecture.X64),
            CancellationToken.None);

        result.Diagnostics.Should().NotContain(
            d => d.Code == DiagnosticCodes.PayloadReferencedButEmpty,
            "nothing in this manifest reads from the payload, so its emptiness is not a defect");
        result.Artifact.Should().NotBeNull();
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sigil-empty-payload-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
#pragma warning disable CA1031 // Best-effort cleanup; a leftover temp dir must not fail a test.
            try { Directory.Delete(Path, recursive: true); }
            catch { }
#pragma warning restore CA1031
        }
    }
}
