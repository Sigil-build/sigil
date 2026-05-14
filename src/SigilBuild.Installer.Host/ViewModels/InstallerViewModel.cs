using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.Services;

namespace SigilBuild.Installer.Host.ViewModels;

public enum InstallerStep { Welcome, License, InstallOptions, Installing, Finish, Custom }

/// <summary>Windows MSI-convention exit codes surfaced by the installer process.</summary>
public enum InstallerOutcomeCode
{
    Completed    = 0,
    UserCancelled = 1602,
    Failed       = 1603,
}

public sealed class InstallerViewModel : INotifyPropertyChanged
{
    private InstallerStep _step = InstallerStep.Welcome;
    private string _installPath;
    private CancellationTokenSource? _engineCts;

    public InstallerViewModel(BrandTokens tokens)
        : this(tokens, Array.Empty<InstallTimeParameter>())
    {
    }

    public InstallerViewModel(BrandTokens tokens, IReadOnlyList<InstallTimeParameter> installTimeParameters)
    {
        Brand = tokens;
        InstallTimeParameters = installTimeParameters ?? Array.Empty<InstallTimeParameter>();
        _parameterValues = SeedDefaults(InstallTimeParameters);

        // install_dir is special: it's the value the InstallOptionsView's
        // TextBox binds to. Preselect the user's manifest default if it
        // declared an install-time parameter named "install_dir"; otherwise
        // fall back to the conventional Program Files\AppName.
        if (_parameterValues.TryGetValue("install_dir", out var installDirDefault) &&
            !string.IsNullOrWhiteSpace(installDirDefault))
        {
            _installPath = installDirDefault;
        }
        else
        {
            _installPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                tokens.AppName);
        }

        // Build the per-parameter VMs that drive the InstallOptionsView's
        // ItemsControl. Each ParameterFieldVm carries the static metadata
        // (name, label, type, allowed values for enums) plus the current
        // user-edited value; CurrentValue changes flow back into
        // _parameterValues so the install subprocess launcher reads the
        // user's edits, not the defaults.
        var fields = new List<ParameterFieldVm>(InstallTimeParameters.Count);
        foreach (var p in InstallTimeParameters)
        {
            _parameterValues.TryGetValue(p.Name, out var current);
            var field = new ParameterFieldVm
            {
                Name = p.Name,
                Label = string.IsNullOrEmpty(p.Description) ? p.Name : p.Description!,
                Type = p.Type,
                Values = p.Values,
                Source = p.Source,
                CurrentValue = current ?? p.DefaultAsString,
            };
            fields.Add(field);
        }
        foreach (var f in fields)
        {
            var capture = f;
            capture.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ParameterFieldVm.CurrentValue))
                {
                    _parameterValues[capture.Name] = capture.CurrentValue;
                    if (capture.Name == "install_dir") InstallPath = capture.CurrentValue;
                }
            };
        }
        ParameterFields = fields;

        LogoImage = TryLoadLogo(tokens.LogoFile);
    }

    public BrandTokens Brand { get; }

    /// <summary>
    /// The declared install-time parameters from the manifest, as emitted by
    /// <c>BrandTokenEmitter.EmitInstallTimeParameters</c>. Each entry has a
    /// name, type, default, and (for enums) allowed values. Used by the
    /// install subprocess launcher to translate user overrides into the
    /// wrapper's <c>/Name=value</c> CLI form.
    /// </summary>
    public IReadOnlyList<InstallTimeParameter> InstallTimeParameters { get; }

    /// <summary>
    /// Per-parameter view-model entries the InstallOptionsView's ItemsControl
    /// binds to. One <see cref="ParameterFieldVm"/> per install-time parameter,
    /// in declaration order. Mutations to each entry's
    /// <see cref="ParameterFieldVm.CurrentValue"/> flow back into
    /// <see cref="ParameterValues"/>; for <c>install_dir</c> they also mirror
    /// into <see cref="InstallPath"/>.
    /// </summary>
    public IReadOnlyList<ParameterFieldVm> ParameterFields { get; } = Array.Empty<ParameterFieldVm>();

    private readonly Dictionary<string, string> _parameterValues;

    /// <summary>
    /// Current value (default or user-edited) for each install-time parameter,
    /// keyed by canonical name. Read by the install launcher when building the
    /// <c>setup.exe /S /Name=Value …</c> command line.
    /// </summary>
    public IReadOnlyDictionary<string, string> ParameterValues => _parameterValues;

    public void SetParameterValue(string name, string value)
    {
        _parameterValues[name] = value;
        if (name == "install_dir") InstallPath = value;
    }

    private static Dictionary<string, string> SeedDefaults(IReadOnlyList<InstallTimeParameter> parameters)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parameters)
        {
            dict[p.Name] = p.DefaultAsString;
        }
        return dict;
    }

    /// <summary>
    /// Branded logo loaded from disk at startup. Null when no logo is bundled
    /// or the file failed to decode — the sidebar Image binding tolerates null
    /// (renders nothing) so the wizard always shows even with a broken logo.
    /// Avoids the AOT-trimming landmine of loading bitmaps via
    /// <c>avares://</c> URIs at XAML-compile time (Avalonia's BitmapTypeConverter
    /// fails when assets aren't preserved through trimming).
    /// </summary>
    public IImage? LogoImage { get; }

    private static Bitmap? TryLoadLogo(string? logoFile)
    {
        if (string.IsNullOrWhiteSpace(logoFile))
        {
            InstallerLog.Info("LogoImage: BrandTokens.LogoFile is empty — sidebar renders without an image");
            return null;
        }
        // The brand-logo path is interpreted relative to the wizard exe's
        // directory (its extract location). The EXE-wrapper packager bundles
        // the logo as `brand-logo.<ext>` next to sigil-wizard.exe so this
        // Path.Combine resolves to an extracted file at runtime.
        var basePath = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? Environment.CurrentDirectory;
        var fullPath = Path.IsPathRooted(logoFile) ? logoFile : Path.Combine(basePath, logoFile);

        if (!File.Exists(fullPath))
        {
            InstallerLog.Error($"LogoImage: file not found at '{fullPath}' (relative '{logoFile}') — sidebar renders without an image");
            return null;
        }

        try
        {
            // SVG → rasterize via Svg.Skia (already referenced by the wizard
            // csproj for the FluentTheme bridge). Avalonia's Bitmap.ctor only
            // decodes raster formats; passing an SVG stream throws
            // "Unable to load bitmap from provided data".
            if (Path.GetExtension(fullPath).Equals(".svg", StringComparison.OrdinalIgnoreCase))
            {
                return RasterizeSvg(fullPath);
            }

            using var fs = File.OpenRead(fullPath);
            var bmp = new Bitmap(fs);
            InstallerLog.Info($"LogoImage: loaded '{fullPath}' ({bmp.PixelSize.Width}x{bmp.PixelSize.Height})");
            return bmp;
        }
        catch (Exception ex)
        {
            InstallerLog.Error($"LogoImage: failed to decode '{fullPath}'", ex);
            return null;
        }
    }

    /// <summary>
    /// Render an SVG file to an Avalonia <see cref="Bitmap"/> at the sidebar
    /// logo height (48 px), preserving aspect ratio. SkiaSharp is already in
    /// the wizard's dependency closure via <c>Svg.Skia</c>; we encode the
    /// rendered surface to PNG and re-decode into Avalonia so consumers stay
    /// in the IImage world without taking a hard dependency on Avalonia.Svg.Skia.
    /// </summary>
    private static Bitmap? RasterizeSvg(string svgPath)
    {
        const int targetHeight = 96;   // sidebar shows at Height=48; 2× for crisp HiDPI
        using var svg = new Svg.Skia.SKSvg();
        var picture = svg.Load(svgPath);
        if (picture is null)
        {
            InstallerLog.Error($"LogoImage: Svg.Skia returned null Picture for '{svgPath}'");
            return null;
        }

        var bounds = picture.CullRect;
        if (bounds.IsEmpty || bounds.Height <= 0 || bounds.Width <= 0)
        {
            InstallerLog.Error($"LogoImage: SVG '{svgPath}' has empty cull rect");
            return null;
        }

        var scale = targetHeight / bounds.Height;
        var width = (int)Math.Ceiling(bounds.Width * scale);
        var height = (int)Math.Ceiling(bounds.Height * scale);

        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(width, height));
        surface.Canvas.Clear(SkiaSharp.SKColors.Transparent);
        surface.Canvas.Scale(scale);
        surface.Canvas.DrawPicture(picture);
        using var img = surface.Snapshot();
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        using var ms = new MemoryStream(data.ToArray());
        var bmp = new Bitmap(ms);
        InstallerLog.Info($"LogoImage: rasterized SVG '{svgPath}' to {width}x{height}");
        return bmp;
    }

    public InstallerOutcomeCode OutcomeCode { get; private set; } = InstallerOutcomeCode.Completed;

    public InstallerStep CurrentStep
    {
        get => _step;
        set { if (_step != value) { _step = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanGoBack)); OnPropertyChanged(nameof(CanGoNext)); OnPropertyChanged(nameof(CanCancel)); OnPropertyChanged(nameof(CancelButtonText)); } }
    }

    public bool CanGoBack => _step is not InstallerStep.Welcome and not InstallerStep.Installing and not InstallerStep.Finish;
    public bool CanGoNext => _step is not InstallerStep.Installing and not InstallerStep.Finish;

    /// <summary>
    /// Cancel/Close button enable state.
    /// False ONLY on Installing — the child setup.exe /S subprocess is in
    /// flight (sc.exe / registry / file_copy operations); killing the wizard
    /// would orphan the child and leave the system half-installed. On Finish
    /// the same button text changes to "Close" (see <see cref="CancelButtonText"/>)
    /// and the click handler closes with <see cref="InstallerOutcomeCode.Completed"/>.
    /// </summary>
    public bool CanCancel => _step is not InstallerStep.Installing;

    /// <summary>
    /// Dynamic label for the bottom-row button. "Close" on the Finish screen
    /// (install is done — clicking closes the wizard with Completed exit code),
    /// "Cancel" everywhere else (user is abandoning; closes with UserCancelled).
    /// </summary>
    public string CancelButtonText => _step == InstallerStep.Finish ? "Close" : "Cancel";

    public string LicenseText { get; set; } = "MIT License (placeholder — replace with the app's actual EULA).";

    private bool _licenseAccepted;
    public bool LicenseAccepted
    {
        get => _licenseAccepted;
        set { if (_licenseAccepted != value) { _licenseAccepted = value; OnPropertyChanged(); } }
    }

    private bool _launchAfterInstall = true;
    public bool LaunchAfterInstall
    {
        get => _launchAfterInstall;
        set { if (_launchAfterInstall != value) { _launchAfterInstall = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// True when the manifest declares a launchable target (e.g. an
    /// <c>installer.launch.path</c> yaml field). Drives the FinishView's
    /// "Launch now" checkbox visibility — when false, the checkbox is hidden
    /// so the wizard doesn't promise a launch the manifest didn't configure.
    /// Until the yaml schema for launch lands, this stays false by default.
    /// </summary>
    public bool HasLaunchTarget { get; init; }

    /// <summary>Dynamic label for the "Launch now" checkbox.</summary>
    public string LaunchAfterInstallLabel => $"Launch {Brand.AppName} now";

    public string InstallPath
    {
        get => _installPath;
        set
        {
            if (_installPath != value)
            {
                _installPath = value;
                // Mirror back into the parameter values so the install
                // subprocess launcher reads the user's edit, not the default.
                _parameterValues["install_dir"] = value;
                OnPropertyChanged();
            }
        }
    }

    private double _installProgress;
    public double InstallProgress
    {
        get => _installProgress;
        set { _installProgress = value; OnPropertyChanged(); }
    }

    private string _installCurrentItem = "";
    public string InstallCurrentItem
    {
        get => _installCurrentItem;
        set { _installCurrentItem = value; OnPropertyChanged(); }
    }

    public void Next() => CurrentStep = _step switch
    {
        InstallerStep.Welcome => InstallerStep.License,
        InstallerStep.License => LicenseAccepted ? InstallerStep.InstallOptions : _step,
        InstallerStep.InstallOptions => InstallerStep.Installing,
        InstallerStep.Installing => InstallerStep.Finish,
        _ => _step,
    };

    public void Back() => CurrentStep = _step switch
    {
        InstallerStep.License => InstallerStep.Welcome,
        InstallerStep.InstallOptions => InstallerStep.License,
        InstallerStep.Installing => _step,
        _ => _step,
    };

    /// <summary>
    /// Registers the <see cref="CancellationTokenSource"/> owned by the install operation so
    /// <see cref="CancelAsync"/> can signal it when the user cancels during installation.
    /// </summary>
    public void SetEngineCts(CancellationTokenSource cts) => _engineCts = cts;

    /// <summary>
    /// Attempts to cancel the installation.  Returns <c>true</c> when the caller should
    /// close the window; <c>false</c> when the user dismissed the confirmation dialog.
    /// On the Finish screen this is always a no-op (returns <c>false</c>).
    /// </summary>
    /// <param name="confirmAsync">
    /// A delegate that, when the install is actively running, must show a confirmation dialog
    /// and return <c>true</c> if the user confirms cancellation.  Pass <c>null</c> to skip the
    /// modal (used in automated tests for pre-install screens).
    /// </param>
    public async Task<bool> CancelAsync(Func<Task<bool>>? confirmAsync = null)
    {
        // On Finish, the same UI button is labelled "Close" — the install
        // completed successfully, the user is just dismissing the wizard.
        // Surface that as Completed (exit 0), not UserCancelled (1602), so the
        // wrapper doesn't treat it as a cancel and skip its post-actions.
        if (_step == InstallerStep.Finish)
        {
            OutcomeCode = InstallerOutcomeCode.Completed;
            return true;
        }

        if (_step == InstallerStep.Installing && _engineCts is not null)
        {
            // Confirm with the user before interrupting a running install.
            if (confirmAsync is not null)
            {
                var confirmed = await confirmAsync().ConfigureAwait(true);
                if (!confirmed)
                    return false;
            }

            _engineCts.Cancel();
        }

        OutcomeCode = InstallerOutcomeCode.UserCancelled;
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? ""));
}

/// <summary>
/// Per-parameter view-model entry rendered by the Install Options screen's
/// ItemsControl. The view template chooses ComboBox vs TextBox based on
/// <see cref="Type"/>; binding writes flow back into the parent
/// <see cref="InstallerViewModel"/>'s parameter-value dictionary via the
/// <see cref="PropertyChanged"/> subscription wired in the constructor.
/// </summary>
public sealed class ParameterFieldVm : INotifyPropertyChanged
{
    /// <summary>Canonical parameter name from sigil.yaml.</summary>
    public string Name { get; init; } = "";

    /// <summary>Human-readable label (description text, falls back to <see cref="Name"/>).</summary>
    public string Label { get; init; } = "";

    /// <summary>Scalar type: <c>string</c>, <c>path</c>, <c>bool</c>, <c>int</c>, <c>enum</c>, <c>secret</c>.</summary>
    public string Type { get; init; } = "string";

    /// <summary>Allowed values when <see cref="Type"/> is <c>enum</c>; null otherwise.</summary>
    public IReadOnlyList<string>? Values { get; init; }

    /// <summary>
    /// When the manifest declares a <c>source</c> block on this parameter, the
    /// wizard fetches the dropdown options at install time and stuffs them into
    /// <see cref="DynamicOptions"/>. Null means "static" — the field renders as
    /// a TextBox or, if <see cref="Type"/> is <c>enum</c>, a ComboBox bound to
    /// <see cref="Values"/>.
    /// </summary>
    public InstallTimeParameterSource? Source { get; init; }

    private IReadOnlyList<HttpOption> _dynamicOptions = Array.Empty<HttpOption>();
    /// <summary>
    /// Options fetched from <see cref="Source"/> at attach time. Empty until the
    /// HTTPS fetch completes (or forever if it fails — the field still renders
    /// but the dropdown is empty so the user knows something is off).
    /// </summary>
    public IReadOnlyList<HttpOption> DynamicOptions
    {
        get => _dynamicOptions;
        set
        {
            _dynamicOptions = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DynamicOptions)));
        }
    }

    /// <summary>
    /// True when the manifest declared <c>type: enum</c> with a static
    /// <c>values:</c> list and no <c>source:</c> block — drives the static
    /// ComboBox's visibility in the view.
    /// </summary>
    public bool IsStaticEnum =>
        string.Equals(Type, "enum", StringComparison.OrdinalIgnoreCase) && Source is null && Values is not null;

    /// <summary>
    /// True when the manifest declared a <c>source:</c> block (dynamic options
    /// fetched over HTTPS at install time) — drives the dynamic ComboBox's
    /// visibility.
    /// </summary>
    public bool IsDynamicEnum => Source is not null;

    /// <summary>
    /// True when neither a static enum nor a dynamic source is in play —
    /// drives TextBox visibility (string / path / int / secret / bool typed
    /// in by hand).
    /// </summary>
    public bool IsTextual => !IsStaticEnum && !IsDynamicEnum;

    /// <summary>
    /// Legacy alias preserved for tests / callers that predated the
    /// static-vs-dynamic split. Equivalent to <see cref="IsStaticEnum"/>
    /// (a parameter with a <c>source</c> is no longer considered "enum"
    /// for view-binding purposes — it gets its own dynamic ComboBox).
    /// </summary>
    public bool IsEnum => IsStaticEnum;

    private string _current = "";
    /// <summary>The currently bound value (default at construction, mutated by ComboBox/TextBox edits).</summary>
    public string CurrentValue
    {
        get => _current;
        set
        {
            if (_current != value)
            {
                _current = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentValue)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
