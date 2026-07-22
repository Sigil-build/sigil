using System.Collections.Generic;

namespace SigilBuild.Core.Manifest;

/// <summary>
/// A declared custom wizard screen (decision 6 / T9). Custom screens are forms
/// over already-declared top-level <c>parameters:</c> — no arbitrary markup.
/// Parsed from the manifest's <c>installer.screens</c> block.
/// </summary>
/// <param name="Id">Screen identifier, used for the rail step indicator.</param>
/// <param name="Title">Heading; may interpolate <c>{app.name}</c>-style tokens.</param>
/// <param name="Subtitle">Optional supporting line under the title.</param>
/// <param name="When">Optional visibility expression (the <c>when</c> engine);
/// the screen is skipped at runtime when it evaluates false.</param>
/// <param name="Fields">Ordered fields rendered on the screen.</param>
public sealed record InstallerScreen(
    string Id,
    LocalizedText Title,
    LocalizedText? Subtitle,
    string? When,
    IReadOnlyList<ScreenField> Fields);

/// <summary>
/// A single field on an <see cref="InstallerScreen"/>. References a declared
/// <see cref="ParameterDefinition"/> by name; the widget is inferred from the
/// parameter's <see cref="ParameterType"/> unless <see cref="Widget"/> overrides
/// it (T9 widget-inference table).
/// </summary>
/// <param name="Param">Name of the declared parameter this field edits.</param>
/// <param name="Widget">Optional widget override (e.g. <c>radio</c>,
/// <c>dropdown</c>, <c>switch</c>, <c>textarea</c>, <c>slider</c>).</param>
public sealed record ScreenField(
    string Param,
    string? Widget);
