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

        // Only fetch for fields visible on THIS screen (CurrentGroupFields),
        // not every install-time parameter. The wizard now paginates parameters
        // across multiple ParameterGroup screens, so re-attaching to a page
        // without a dynamic-source field shouldn't fire any GET.
        foreach (var f in vm.CurrentGroupFields)
        {
            if (f.Source is null) continue;

            // Skip if we already loaded options — InstallOptionsView is rebuilt
            // every time the user navigates Next/Back so DynamicOptions on the
            // VM survives across rebuilds and a re-attach should not refetch.
            if (f.DynamicOptions.Count > 0) continue;

            var (url, allResolved) = SubstituteTemplate(f.Source.Url, vm.ParameterValues);
            if (!allResolved)
            {
                // Dependency on a parameter the user hasn't filled in yet (e.g.
                // domain_name needed by the application_id endpoint but the
                // user hasn't visited Server Settings). Defer the fetch until
                // they reach the page that owns this field with the values set.
                InstallerLog.Info($"dynamic options fetch deferred for '{f.Name}' — url has unresolved/empty parameter references: {url}");
                continue;
            }

            try
            {
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
    /// substitution surface for install-time parameters).
    /// Returns the substituted URL plus a flag that's false when any
    /// <c>${parameters.X}</c> token referenced a missing or empty value — the
    /// caller skips the fetch in that case to avoid hitting bogus hosts like
    /// <c>https://sales./api/...</c> while the dependency is still unset.
    /// </summary>
    private static (string Url, bool AllResolved) SubstituteTemplate(string template, IReadOnlyDictionary<string, string> values)
    {
        var sb = new StringBuilder(template.Length);
        var allResolved = true;
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
                    values.TryGetValue(path[prefix.Length..], out var v) &&
                    !string.IsNullOrEmpty(v))
                {
                    sb.Append(v);
                }
                else
                {
                    allResolved = false;
                }
                i = end + 1;
            }
            else
            {
                sb.Append(template[i]);
                i++;
            }
        }
        return (sb.ToString(), allResolved);
    }
}
