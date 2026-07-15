using System.Collections.Generic;

namespace SigilBuild.Core.Manifest;

/// <summary>
/// Declarative description of an install-time / pack-time parameter, parsed from
/// the manifest's <c>parameters:</c> block. Consumed by the wrapper installer
/// (Sprint 5c) to render UI inputs and to validate values before they reach
/// install steps.
/// </summary>
public sealed record ParameterDefinition(
    string Name,
    ParameterType Type,
    object? Default,
    IReadOnlyList<string>? EnumValues,
    bool InstallTime,
    LocalizedText? Description,
    string? Pattern,
    int? Min,
    int? Max,
    ParameterSource? Source = null,
    string? Screen = null);

/// <summary>
/// Supported parameter scalar types. Mirrors the schema enum exactly:
/// keep these two surfaces in sync.
/// </summary>
#pragma warning disable CA1720 // Identifier contains type name — these are intentionally
                              // shaped to mirror the YAML schema's `type:` enum surface.
public enum ParameterType { String, Path, Bool, Int, Enum, Secret }
#pragma warning restore CA1720
