using System.Collections.Generic;

namespace SigilBuild.Core.Manifest;

/// <summary>
/// Built-in-but-configurable installer components (decision 5). Each component
/// ships built in, is individually configurable, and can be disabled. Parsed
/// from the manifest's <c>installer.options</c> block (T8). A <c>null</c>
/// component means "not declared" and is treated as its built-in default by the
/// wave-2 options feature.
/// </summary>
public sealed record InstallerOptions(
    InstallerOption? DesktopShortcut = null,
    InstallerOption? StartMenu = null,
    InstallerOption? AddToPath = null,
    FileAssociationOption? FileAssociations = null);

/// <summary>
/// Per-component configuration for a boolean installer option
/// (<c>desktop_shortcut</c>, <c>start_menu</c>, <c>add_to_path</c>).
/// </summary>
/// <remarks>
/// In YAML each component accepts either a shorthand boolean or an object.
/// The wave-2 parser (T8) maps the shorthand forms onto this record:
/// <c>true</c> → <c>{ Enabled = true, Default = true }</c>;
/// <c>false</c> → <c>{ Enabled = false }</c>. The object form supplies
/// <c>enabled</c>, <c>default</c>, and <c>locked</c> directly.
/// </remarks>
public sealed record InstallerOption(
    bool Enabled = true,
    bool Default = true,
    bool Locked = false);

/// <summary>
/// Configuration for the <c>file_associations</c> component. Extends the shared
/// <see cref="InstallerOption"/> shape with the file extensions to register
/// (each including the leading dot, e.g. <c>.acme</c>).
/// </summary>
/// <remarks>
/// Shorthand mapping matches <see cref="InstallerOption"/>: a bare boolean sets
/// <see cref="Enabled"/> (with no extensions), the object form additionally
/// carries <see cref="Extensions"/>.
/// </remarks>
public sealed record FileAssociationOption(
    bool Enabled = true,
    bool Default = true,
    bool Locked = false,
    IReadOnlyList<string>? Extensions = null);
