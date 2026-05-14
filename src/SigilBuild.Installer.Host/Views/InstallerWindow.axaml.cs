using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Installer.Host.Views.Screens;

namespace SigilBuild.Installer.Host.Views;

public partial class InstallerWindow : Window
{
    private ContentControl? _screenHost;
    private InstallerViewModel? _attachedVm;

    public InstallerWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _screenHost = this.FindControl<ContentControl>("ScreenHost");
        DataContextChanged += OnDataContextChanged;
        TryLoadWindowIcon();
    }

    /// <summary>
    /// Load <c>installer-icon.ico</c> from the wizard's extract directory and
    /// assign it to <see cref="Window.Icon"/>. The InstallerHostBundle packager
    /// stamps the bundled exe's PE icon AND drops the .ico bytes next to it as
    /// <c>installer-icon.ico</c>; the latter is what Avalonia's Window.Icon
    /// needs because PE-stamped icons don't propagate to the title bar / Alt+Tab
    /// thumbnail at runtime without an explicit assignment.
    /// </summary>
    private void TryLoadWindowIcon()
    {
        try
        {
            var baseDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath ?? "")
                ?? Environment.CurrentDirectory;
            var iconPath = System.IO.Path.Combine(baseDir, "installer-icon.ico");
            if (!System.IO.File.Exists(iconPath))
            {
                InstallerLog.Info($"InstallerWindow: no installer-icon.ico at '{iconPath}' — using stock window icon");
                return;
            }
            using var stream = System.IO.File.OpenRead(iconPath);
            Icon = new WindowIcon(stream);
            InstallerLog.Info($"InstallerWindow: loaded Window.Icon from '{iconPath}'");
        }
        catch (Exception ex)
        {
            InstallerLog.Error("InstallerWindow: failed to load installer-icon.ico", ex);
        }
    }

    /// <summary>
    /// Subscribe to <see cref="InstallerViewModel.PropertyChanged"/> so the
    /// ContentControl swaps to a freshly-constructed screen view whenever
    /// <see cref="InstallerViewModel.CurrentStep"/> changes. The previous
    /// XAML-only data-template approach failed because <c>Content="{Binding}"</c>
    /// never invalidated — the VM instance stayed identical step-to-step.
    /// </summary>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_attachedVm is not null)
        {
            _attachedVm.PropertyChanged -= OnVmPropertyChanged;
            _attachedVm = null;
        }
        if (DataContext is InstallerViewModel vm)
        {
            _attachedVm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
            RebuildScreen(vm);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // CurrentStepIndex is the canonical source of truth — CurrentStepDef
        // and the legacy CurrentStep enum both follow from it. We listen for
        // index changes (not the enum) so rebuilds fire correctly when we
        // page across multiple consecutive ParameterGroup steps that all map
        // back to the same InstallerStep.InstallOptions enum value.
        if (e.PropertyName == nameof(InstallerViewModel.CurrentStepIndex) && sender is InstallerViewModel vm)
        {
            InstallerLog.Info($"InstallerWindow: CurrentStepIndex changed to {vm.CurrentStepIndex} ({vm.CurrentStepDef.Id}) — rebuilding screen");
            RebuildScreen(vm);
        }
    }

    private void RebuildScreen(InstallerViewModel vm)
    {
        if (_screenHost is null)
        {
            InstallerLog.Error("InstallerWindow.RebuildScreen: ScreenHost ContentControl was not found in XAML");
            return;
        }
        // Dispatch on the discriminated step-def directly rather than the
        // legacy enum: the enum collapses InstallDir + ParameterGroup into a
        // single InstallOptions value, but they need distinct views (a
        // disk-space readout vs the parameter ItemsControl).
        Control screen = vm.CurrentStepDef switch
        {
            InstallerStepDef.Welcome => new WelcomeView(),
            InstallerStepDef.License => new LicenseView(),
            InstallerStepDef.InstallDir => new InstallDirView(),
            InstallerStepDef.ParameterGroup => new InstallOptionsView(),
            InstallerStepDef.Installing => new InstallingView(),
            InstallerStepDef.Finish => new FinishView(),
            InstallerStepDef.Custom => new CustomView(),
            _ => new TextBlock { Text = "(no view)" },
        };
        // Force the child to inherit the VM's DataContext explicitly. Without
        // this, controls instantiated in code default to DataContext=null and
        // their bindings (Brand.AppName, LicenseText, etc.) silently fail.
        screen.DataContext = vm;
        _screenHost.Content = screen;
    }

    private void OnNext(object? _, RoutedEventArgs __)
    {
        try
        {
            if (DataContext is InstallerViewModel vm)
            {
                InstallerLog.Info($"OnNext clicked, current step={vm.CurrentStep}, LicenseAccepted={vm.LicenseAccepted}");
                vm.Next();
                InstallerLog.Info($"OnNext after vm.Next(), new step={vm.CurrentStep}");
            }
            else
            {
                InstallerLog.Error($"OnNext: DataContext is {DataContext?.GetType().Name ?? "<null>"}, expected InstallerViewModel");
            }
        }
        catch (Exception ex)
        {
            InstallerLog.Error("OnNext threw", ex);
        }
    }

    private void OnBack(object? _, RoutedEventArgs __)
    {
        try
        {
            if (DataContext is InstallerViewModel vm)
            {
                InstallerLog.Info($"OnBack clicked, current step={vm.CurrentStep}");
                vm.Back();
                InstallerLog.Info($"OnBack after vm.Back(), new step={vm.CurrentStep}");
            }
        }
        catch (Exception ex)
        {
            InstallerLog.Error("OnBack threw", ex);
        }
    }

    private async void OnCancel(object? _, RoutedEventArgs __)
    {
        try
        {
            InstallerLog.Info("OnCancel clicked");
            await TryCloseWithCancelAsync();
        }
        catch (Exception ex)
        {
            InstallerLog.Error("OnCancel threw", ex);
        }
    }

    /// <summary>Called by the Cancel button and by the window-close gesture.</summary>
    private async Task TryCloseWithCancelAsync()
    {
        if (DataContext is not InstallerViewModel vm)
        {
            Close();
            return;
        }

        var confirmed = await vm.CancelAsync(() => ShowConfirmDialogAsync());
        if (confirmed)
            Close();
    }

    private async Task<bool> ShowConfirmDialogAsync()
    {
        var dialog = new CancelConfirmDialog();
        var result = await dialog.ShowDialog<bool?>(this);
        return result is true;
    }

    /// <summary>
    /// Drag the chromeless window when the user presses the sidebar. The
    /// installer window is borderless (no OS title bar), so without an explicit
    /// drag hook the user cannot reposition it.
    /// </summary>
    private void OnDragHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
