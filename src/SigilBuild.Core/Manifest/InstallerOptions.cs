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
    FileAssociationOption? FileAssociations = null,
    // P10 (gap G11): app-defined custom components — the Inno [Tasks] equivalent.
    // Declared under `installer.options.components[]`, in declaration order. Each
    // generates NO install step of its own; it exists only as `option.<name>` in
    // the expression engine, gating arbitrary steps / step groups via their
    // `when`. Null/empty when the manifest declares no custom component.
    IReadOnlyList<CustomComponent>? Components = null);

/// <summary>
/// An app-defined custom option component (P10, gap G11) — the declarative
/// equivalent of an Inno Setup <c>[Tasks]</c> entry. Unlike the four built-in
/// components it generates <em>no</em> install step of its own: it surfaces as a
/// checkbox on the Options screen and seeds <c>option.&lt;Name&gt;</c> in the
/// expression engine, which the manifest author references from the <c>when</c>
/// of any step (or step group) they want the checkbox to gate.
/// </summary>
/// <param name="Name">The component key — a bare identifier
/// (<c>[A-Za-z_][A-Za-z0-9_]*</c>), unique among components and not colliding
/// with a built-in component or a declared parameter. Referenced as
/// <c>option.&lt;Name&gt;</c> in step <c>when</c> clauses and overridden on the CLI
/// via the namespaced <c>/Poption.&lt;Name&gt;=true|false</c> form.</param>
/// <param name="Label">The checkbox caption (localizable — a plain string or a
/// <c>{ en: ..., de: ... }</c> map).</param>
/// <param name="Default">The checkbox's initial (checked) state.</param>
/// <param name="Locked">When <c>true</c> the row renders disabled and is always
/// applied at its <see cref="Default"/>; a CLI override is ignored (logged).</param>
/// <param name="Description">Optional secondary caption (localizable).</param>
/// <param name="When">Optional applicability gate: when it evaluates false the
/// row is hidden and <c>option.&lt;Name&gt;</c> resolves to <c>false</c>.</param>
public sealed record CustomComponent(
    string Name,
    LocalizedText Label,
    bool Default = false,
    bool Locked = false,
    LocalizedText? Description = null,
    string? When = null);

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

/// <summary>
/// A single <em>enabled</em> built-in option component, resolved for the runtime
/// and the wizard (T8). Disabled components are omitted entirely — they generate
/// no install step and never appear on the Options screen — so every element of
/// this list is a component the user can see (and, unless <see cref="Locked"/>,
/// toggle). Carried in the wrapper blob so the engine can seed
/// <c>option.&lt;Name&gt;</c> for step gating and the host can render one checkbox
/// per component.
/// </summary>
/// <param name="Name">The canonical component key (<c>desktop_shortcut</c>,
/// <c>start_menu</c>, <c>add_to_path</c>, <c>file_associations</c>) — the same
/// token the generated step's <c>when: option.&lt;Name&gt;</c> gate references and
/// the CLI <c>/P&lt;Name&gt;=value</c> override uses.</param>
/// <param name="Default">The checkbox's resolved initial (checked) state.</param>
/// <param name="Locked">When <c>true</c> the component is rendered disabled and
/// always applied at its <see cref="Default"/> — the user cannot change it.</param>
/// <param name="Custom">P10 (gap G11): <c>true</c> for an app-defined custom
/// component (generates no step of its own; its CLI override is namespaced under
/// <c>/Poption.&lt;Name&gt;</c>). <c>false</c> for the four built-ins.</param>
/// <param name="Label">P10: the custom component's localizable checkbox caption.
/// <c>null</c> for a built-in, whose caption comes from the wizard string catalog.</param>
/// <param name="Description">P10: optional localizable secondary caption (custom only).</param>
/// <param name="When">P10: optional applicability gate — when it evaluates false the
/// row is hidden and <c>option.&lt;Name&gt;</c> resolves to <c>false</c> (custom only).</param>
public sealed record InstallerOptionComponent(
    string Name,
    bool Default,
    bool Locked,
    bool Custom = false,
    LocalizedText? Label = null,
    LocalizedText? Description = null,
    string? When = null);
