// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace SigilBuild.Installer.Host.Tests;

/// <summary>
/// Every image shipped under <c>Assets/</c> must decode with the REAL Skia
/// codec — the one <c>Avalonia.Skia.ImmutableBitmap</c> uses in production.
/// </summary>
/// <remarks>
/// This test exists because the rest of this assembly cannot catch a corrupt
/// image. <c>TestAppBuilder</c> configures
/// <c>UseHeadless(new AvaloniaHeadlessPlatformOptions())</c>, whose
/// <c>UseHeadlessDrawing</c> defaults to <c>true</c>, so the headless render
/// interface's <c>LoadBitmap</c> hands back a stub <b>without parsing the
/// bytes</b>. Five test classes construct <c>InstallerWindow</c> and every one
/// of them stayed green while <c>default-logo.png</c> — referenced from
/// <c>InstallerWindow</c>, <c>UninstallWindow</c> and <c>UpdateWindow</c> — had
/// a corrupt IDAT CRC that made the real wizard throw
/// <c>ArgumentException: Unable to load bitmap from provided data</c> out of
/// <c>XamlIlPopulate</c> before its first window ever rendered. Found by R7 on a
/// clean machine; the release artifact's wizard had never opened on any machine.
/// <para>
/// So: resolve through <c>avares://</c> exactly as the XAML does (which also
/// pins the assembly name and the asset paths), then decode with SkiaSharp
/// directly rather than through Avalonia's platform abstraction — the stub is
/// precisely what must not be in the loop here.
/// </para>
/// </remarks>
public sealed class ShippedAssetDecodeTests
{
    /// <summary>
    /// The XAML binds <c>avares://installer/Assets/...</c>; <c>installer</c> is
    /// the host's <c>AssemblyName</c>. Probing the folder rather than naming the
    /// files keeps a newly added asset covered automatically.
    /// </summary>
    private static readonly Uri AssetsRoot = new("avares://installer/Assets");

    [AvaloniaFact]
    public void Every_shipped_asset_decodes_with_the_real_skia_codec()
    {
        // Arrange
        var assets = AssetLoader.GetAssets(AssetsRoot, null).ToList();
        assets.Should().NotBeEmpty(
            "the wizard windows bind images out of {0}; an empty enumeration means this probe " +
            "URI stopped matching the AvaloniaResource glob, not that the assets are sound",
            AssetsRoot);

        // Act
        var failures = new List<string>();
        foreach (var uri in assets)
        {
            using var asset = AssetLoader.Open(uri);
            using var buffer = new MemoryStream();
            asset.CopyTo(buffer);
            var bytes = buffer.ToArray();

            using var decoded = SKBitmap.Decode(bytes);
            if (decoded is null)
            {
                failures.Add(
                    $"{uri} ({bytes.Length} bytes) — SKBitmap.Decode returned null, so Avalonia " +
                    "will throw 'Unable to load bitmap from provided data' when XAML binds it");
            }
        }

        // Assert
        // Report every offender, not just the first: these assets are usually
        // generated in a batch, so one broken file almost always means siblings.
        failures.Should().BeEmpty(
            "a shipped asset that Skia refuses kills the wizard at window construction, " +
            "before any UI exists to report it.{0}Offenders:{0}  {1}{0}",
            Environment.NewLine,
            string.Join(Environment.NewLine + "  ", failures));
    }
}
