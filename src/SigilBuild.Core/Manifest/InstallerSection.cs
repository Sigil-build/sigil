using System.Collections.Generic;

namespace SigilBuild.Core.Manifest;

public sealed record InstallerBrand(
    string? Logo,
    string? Hero,
    string? PrimaryColor,
    string? AccentColor,
    string? GradientStart,
    string? GradientMid,
    string? GradientEnd);

/// <summary>
/// Branded Windows installer configuration parsed from the manifest's
/// <c>installer:</c> block.
/// </summary>
/// <remarks>
/// Every member beyond <see cref="Brand"/> is additive and optional: existing
/// construction sites (<c>new InstallerSection(brand)</c>) keep compiling
/// unchanged. Parsing/validation of the new members is owned by the wave-2
/// feature tasks (T8 options, T9 screens, T12 scope, T13 install dir,
/// T14 license); this record is the data surface only.
/// </remarks>
/// <param name="Brand">Brand colors + assets (T7).</param>
/// <param name="Options">Built-in configurable components (T8).</param>
/// <param name="Screens">Declared custom wizard screens (T9).</param>
/// <param name="License">License file path or inline text; shows the License
/// screen when present (T14).</param>
/// <param name="Scope">Install scope; defaults to <see cref="InstallScope.Auto"/> (T12).</param>
/// <param name="InstallDir">Optional install-dir override; may reference
/// <c>{app.*}</c> / <c>{scope_root}</c> tokens (T13).</param>
public sealed record InstallerSection(
    InstallerBrand? Brand,
    InstallerOptions? Options = null,
    IReadOnlyList<InstallerScreen>? Screens = null,
    string? License = null,
    InstallScope Scope = InstallScope.Auto,
    string? InstallDir = null);
