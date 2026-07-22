using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SigilBuild.Core.Manifest;

namespace SigilBuild.Packaging.ExeWrapper;

/// <summary>
/// Pack-time generator for the built-in configurable Options (T8, decision 5).
/// Each <em>enabled</em> component (<c>desktop_shortcut</c>, <c>start_menu</c>,
/// <c>add_to_path</c>, <c>file_associations</c>) is turned into its install
/// step(s), gated on <c>option.&lt;component&gt;</c> so the runtime honours the
/// wizard checkbox (or a <c>/P&lt;component&gt;=value</c> override). A component
/// declared <c>false</c> (disabled) generates <em>nothing</em> and is absent from
/// the returned component list — so it neither runs nor appears on the Options
/// screen. A <c>locked</c> component still generates its step; the runtime fixes
/// its gate at the component default (the UI renders it disabled).
/// </summary>
/// <remarks>
/// The generated steps use <see cref="OnFailure.Continue"/>: a failed convenience
/// shortcut / PATH entry / file association must not roll back an otherwise good
/// install. Generation order is fixed (desktop_shortcut → start_menu →
/// add_to_path → file_associations, extensions sorted ordinal) so the blob — and
/// therefore the stamped Setup.exe — stays byte-identical across builds.
/// </remarks>
internal static class OptionStepGenerator
{
    // Component keys — the same tokens the CLI (`/P<name>`) and the expression
    // engine (`option.<name>`) use.
    internal const string DesktopShortcut = "desktop_shortcut";
    internal const string StartMenu = "start_menu";
    internal const string AddToPath = "add_to_path";
    internal const string FileAssociations = "file_associations";

    /// <summary>
    /// Generate the option-gated install steps plus the ENABLED component list
    /// (both empty when <paramref name="options"/> is <c>null</c> or declares no
    /// enabled component).
    /// </summary>
    public static (IReadOnlyList<InstallStep> Steps, IReadOnlyList<InstallerOptionComponent> Components)
        Generate(InstallerOptions? options, AppSection app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var steps = new List<InstallStep>();
        var components = new List<InstallerOptionComponent>();
        if (options is null)
        {
            return (steps, components);
        }

        // A best-effort default target for the generated shortcuts: the app's exe
        // under the install dir. `{install_dir}` is pinned by T13; a manifest author
        // who needs an exact target writes a hand-authored shortcut_create step.
        var target = "{install_dir}\\" + app.Name + ".exe";

        if (IsEnabled(options.DesktopShortcut))
        {
            components.Add(new InstallerOptionComponent(
                DesktopShortcut, options.DesktopShortcut!.Default, options.DesktopShortcut.Locked));
            steps.Add(new InstallStep.ShortcutCreate(
                Id: "option_" + DesktopShortcut,
                Target: target,
                Location: "desktop",
                Name: app.Name,
                Args: null,
                WorkingDir: "{install_dir}",
                Icon: null,
                Description: null,
                When: "option." + DesktopShortcut,
                OnFailure: OnFailure.Continue));
        }

        if (IsEnabled(options.StartMenu))
        {
            components.Add(new InstallerOptionComponent(
                StartMenu, options.StartMenu!.Default, options.StartMenu.Locked));
            steps.Add(new InstallStep.ShortcutCreate(
                Id: "option_" + StartMenu,
                Target: target,
                Location: "start_menu",
                Name: app.Name,
                Args: null,
                WorkingDir: "{install_dir}",
                Icon: null,
                Description: null,
                When: "option." + StartMenu,
                OnFailure: OnFailure.Continue));
        }

        if (IsEnabled(options.AddToPath))
        {
            components.Add(new InstallerOptionComponent(
                AddToPath, options.AddToPath!.Default, options.AddToPath.Locked));
            steps.Add(new InstallStep.EnvSet(
                Id: "option_" + AddToPath,
                Name: "PATH",
                Value: "{install_dir}",
                // "auto" defers to the resolved install scope (user vs machine PATH, T12).
                Scope: "auto",
                Action: "append",
                Separator: ";",
                When: "option." + AddToPath,
                OnFailure: OnFailure.Continue));
        }

        var fa = options.FileAssociations;
        if (fa is not null && fa.Enabled)
        {
            components.Add(new InstallerOptionComponent(
                FileAssociations, fa.Default, fa.Locked));

            var extensions = (fa.Extensions ?? Array.Empty<string>())
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(e => e, StringComparer.Ordinal);

            foreach (var ext in extensions)
            {
                var normalized = ext.StartsWith('.') ? ext : "." + ext;
                var bare = normalized.TrimStart('.');
                steps.Add(new InstallStep.RegistryWrite(
                    Id: "option_file_assoc_" + Sanitize(bare),
                    Hive: "HKCU",
                    Key: "Software\\Classes\\" + normalized,
                    Name: "",
                    Type: "REG_SZ",
                    // The ProgId this extension points at: "<appId><ext>", e.g.
                    // "com.acme.Studio.acme". Stable + collision-resistant per app.
                    Value: app.Id + normalized,
                    View: "native",
                    When: "option." + FileAssociations,
                    OnFailure: OnFailure.Continue));
            }
        }

        // P10 (gap G11): app-defined custom components. They generate NO step of
        // their own — a custom component exists only as `option.<name>` in the
        // expression engine, gating steps the author wrote (via their `when`). They
        // are appended AFTER the built-ins, in declared order, so the Options screen
        // renders them last and the blob stays deterministic.
        foreach (var custom in options.Components ?? Array.Empty<CustomComponent>())
        {
            components.Add(new InstallerOptionComponent(
                Name: custom.Name,
                Default: custom.Default,
                Locked: custom.Locked,
                Custom: true,
                Label: custom.Label,
                Description: custom.Description,
                When: custom.When));
        }

        return (steps, components);
    }

    private static bool IsEnabled(InstallerOption? option) => option is { Enabled: true };

    /// <summary>Reduce a file extension to a step-id-safe token (alphanumerics only).</summary>
    private static string Sanitize(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.Length == 0 ? "ext" : sb.ToString();
    }
}
