using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Installer.Host.Views;

namespace SigilBuild.Installer.Host;

public partial class App : Application
{
    private InstallerViewModel? _vm;

    /// <summary>
    /// The outcome chosen by the user during this session.
    /// Read by <see cref="Program.Main"/> after the Avalonia lifetime exits.
    /// </summary>
    public int OutcomeExitCode => (int)(_vm?.OutcomeCode ?? InstallerOutcomeCode.Completed);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        InstallerLog.Info($"App.OnFrameworkInitializationCompleted: ApplicationLifetime={ApplicationLifetime?.GetType().Name ?? "<null>"}");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var processDir = System.IO.Path.GetDirectoryName(System.Environment.ProcessPath ?? "") ?? "";

            BrandTokens tokens;
            var brandPath = System.IO.Path.Combine(processDir, "BrandTokens.g.json");
            try
            {
                tokens = BrandTokens.LoadOrDefault(brandPath);
                InstallerLog.Info($"BrandTokens loaded from '{brandPath}': AppName={tokens.AppName}, Primary={tokens.PrimaryColor}, Accent={tokens.AccentColor}");
            }
            catch (System.Text.Json.JsonException ex)
            {
                InstallerLog.Error($"BrandTokens parse failed for '{brandPath}', using defaults", ex);
                tokens = new BrandTokens();
            }

            var paramsPath = System.IO.Path.Combine(processDir, "InstallTimeParameters.g.json");
            var installTimeParams = InstallTimeParameterLoader.LoadOrEmpty(paramsPath);
            InstallerLog.Info($"InstallTimeParameters loaded from '{paramsPath}': {installTimeParams.Count} entries");
            foreach (var p in installTimeParams)
            {
                InstallerLog.Info($"  param: name={p.Name}, type={p.Type}, default={p.DefaultAsString}");
            }

            try
            {
                BrandPalette.Apply(this, tokens);
                InstallerLog.Info("BrandPalette applied");
                _vm = new InstallerViewModel(tokens, installTimeParams);
                InstallerLog.Info($"InstallerViewModel built, install path={_vm.InstallPath}");
                desktop.MainWindow = new InstallerWindow
                {
                    DataContext = _vm,
                };
                InstallerLog.Info("MainWindow created, handing control back to Avalonia");
            }
            catch (System.Exception ex)
            {
                InstallerLog.Error("OnFrameworkInitializationCompleted main-window construction failed", ex);
                throw;
            }
        }
        else
        {
            InstallerLog.Error($"ApplicationLifetime is NOT IClassicDesktopStyleApplicationLifetime — no main window will be created");
        }
        base.OnFrameworkInitializationCompleted();
    }
}
