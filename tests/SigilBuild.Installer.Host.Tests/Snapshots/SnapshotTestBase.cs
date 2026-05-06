using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using SigilBuild.Installer.Host.Branding;

namespace SigilBuild.Installer.Host.Tests.Snapshots;

/// <summary>
/// Base class for snapshot (golden-image) tests.
///
/// CAPTURE MODE — set env-var SIGIL_SNAPSHOT_CAPTURE=1 before running:
///   SIGIL_SNAPSHOT_CAPTURE=1 dotnet test --filter "ScreenSnapshotTests"
///
/// This renders each screen and saves PNG files under
///   tests/SigilBuild.Installer.Host.Tests/Snapshots/Baseline/
///
/// COMPARE MODE (default / CI):
///   dotnet test tests/SigilBuild.Installer.Host.Tests
///
/// If a baseline PNG is absent the individual test is silently skipped
/// (see ScreenSnapshotTests.CaptureOrSkip). Once baselines are committed
/// the comparison runs automatically on every CI build.
///
/// Rendering API: Avalonia.Headless 12.0.2 HeadlessWindowExtensions.CaptureRenderedFrame(TopLevel)
/// triggers one renderer tick and returns a WriteableBitmap (or null if the
/// window has not been shown).
/// </summary>
public abstract class SnapshotTestBase
{
    protected static readonly bool CaptureMode =
        string.Equals(
            Environment.GetEnvironmentVariable("SIGIL_SNAPSHOT_CAPTURE"),
            "1",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Absolute path to the Baseline folder, resolved relative to the test
    /// project root (three directories up from the build output).
    /// </summary>
    protected static string SnapshotDir =>
        Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Snapshots", "Baseline"));

    // ── Brand fixtures ────────────────────────────────────────────────────

    protected static BrandTokens DefaultBrand => new();

    protected static BrandTokens VerdantStudioBrand => new()
    {
        AppName = "Verdant Studio",
        Publisher = "Verdant Inc.",
        PrimaryColor = "#16A34A",
        AccentColor = "#15803D",
        GradientStart = "#022C22",
        GradientMid = "#064E3B",
        GradientEnd = "#16A34A",
    };

    protected static BrandTokens LumenComposeBrand => new()
    {
        AppName = "Lumen Compose",
        Publisher = "Lumen Inc.",
        PrimaryColor = "#DB2777",
        AccentColor = "#BE185D",
        GradientStart = "#1F0729",
        GradientMid = "#4A044E",
        GradientEnd = "#DB2777",
    };

    // ── Core assertion ────────────────────────────────────────────────────

    /// <summary>
    /// Shows <paramref name="window"/>, captures its rendered frame, then
    /// either saves the PNG (capture mode) or compares it to the stored
    /// baseline (compare mode).
    /// </summary>
    protected static void AssertSnapshot(Window window, string snapshotKey)
    {
        window.Show();

        // Trigger one renderer tick and capture the frame.
        // CaptureRenderedFrame is an extension method from Avalonia.Headless on TopLevel.
        var frame = window.CaptureRenderedFrame();
        if (frame is null)
            throw new InvalidOperationException(
                $"No rendered frame captured for snapshot '{snapshotKey}'. " +
                "Ensure the window was shown and the headless platform is active.");

        var snapshotPath = Path.Combine(SnapshotDir, $"{snapshotKey}.png");

        if (CaptureMode)
        {
            Directory.CreateDirectory(SnapshotDir);
            frame.Save(snapshotPath);
            return;
        }

        if (!File.Exists(snapshotPath))
            throw new InvalidOperationException(
                $"Baseline not found: {snapshotPath}\n" +
                $"Run with SIGIL_SNAPSHOT_CAPTURE=1 to generate baselines.");

        // Write the actual frame to a MemoryStream for byte comparison.
        using var actualMs = new MemoryStream();
        frame.Save(actualMs);
        var actualBytes = actualMs.ToArray();

        var baselineBytes = File.ReadAllBytes(snapshotPath);

        if (!baselineBytes.SequenceEqual(actualBytes))
        {
            // Persist the actual PNG next to the baseline so devs can diff it.
            var actualPath = Path.ChangeExtension(snapshotPath, ".actual.png");
            File.WriteAllBytes(actualPath, actualBytes);
            throw new InvalidOperationException(
                $"Snapshot mismatch for '{snapshotKey}'.\n" +
                $"Expected : {snapshotPath}\n" +
                $"Actual   : {actualPath}");
        }
    }
}
