using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SigilBuild.Installer.Host.Services;
using SigilBuild.Installer.Host.ViewModels;

namespace SigilBuild.Installer.Host.Views.Screens;

public partial class InstallOptionsView : UserControl
{
    public InstallOptionsView() { AvaloniaXamlLoader.Load(this); }

    /// <summary>
    /// Fire the dynamic-options HTTPS fetch when the screen actually becomes
    /// visible, not at VM construction. Doing it here gives the user's earlier
    /// parameter edits (license_key, server, etc.) a chance to be substituted
    /// into the URL template via <c>${parameters.foo}</c>. Each fetch is
    /// wrapped in its own try/catch so one failing endpoint doesn't blank out
    /// the rest of the form.
    /// </summary>
    protected override async void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is not InstallerViewModel vm) return;
        foreach (var f in vm.ParameterFields)
        {
            if (f.Source is null) continue;
            try
            {
                var url = SubstituteTemplate(f.Source.Url, vm.ParameterValues);
                var options = await HttpOptionsLoader.LoadAsync(
                    url, f.Source.ItemsPath, f.Source.LabelProperty, f.Source.ValueProperty,
                    CancellationToken.None);
                f.DynamicOptions = options;
                // Seed CurrentValue with the first option's value if the user
                // hasn't already chosen something — keeps the ComboBox from
                // displaying empty selection on first paint.
                if (options.Count > 0 && string.IsNullOrEmpty(f.CurrentValue))
                    f.CurrentValue = options[0].Value;
            }
#pragma warning disable CA1031 // top-level UI handler — must not crash the wizard
            catch (Exception ex)
            {
                InstallerLog.Error($"dynamic options fetch failed for '{f.Name}'", ex);
            }
#pragma warning restore CA1031
        }
    }

    /// <summary>
    /// Minimal <c>${parameters.foo}</c> substitution for URL templates. Only
    /// the <c>parameters.</c> namespace is supported (matches the Core
    /// substitution surface for install-time parameters). Unknown tokens are
    /// dropped — better an empty segment than a literal <c>${…}</c> in the URL.
    /// </summary>
    private static string SubstituteTemplate(string template, IReadOnlyDictionary<string, string> values)
    {
        var sb = new StringBuilder(template.Length);
        int i = 0;
        while (i < template.Length)
        {
            if (i + 1 < template.Length && template[i] == '$' && template[i + 1] == '{')
            {
                var end = template.IndexOf('}', i + 2);
                if (end < 0) { sb.Append(template[i..]); break; }
                var path = template[(i + 2)..end];
                const string prefix = "parameters.";
                if (path.StartsWith(prefix, StringComparison.Ordinal) &&
                    values.TryGetValue(path[prefix.Length..], out var v))
                {
                    sb.Append(v);
                }
                i = end + 1;
            }
            else
            {
                sb.Append(template[i]);
                i++;
            }
        }
        return sb.ToString();
    }
}
