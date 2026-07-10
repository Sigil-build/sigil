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

/// <summary>
/// Coarse wizard step enum. Retained as the legacy public surface; the
/// underlying step machine is now a list of <see cref="InstallerStepDef"/>
/// entries so the InstallOptions surface can fan out across an arbitrary
/// number of NSIS-style themed pages plus a dedicated Install Directory page.
/// Setting <see cref="InstallerViewModel.CurrentStep"/> to an enum value maps
/// onto the first list entry of that kind.
/// </summary>
public enum InstallerStep { Welcome, License, InstallOptions, Installing, Finish, Custom }

/// <summary>Windows MSI-convention exit codes surfaced by the installer process.</summary>
public enum InstallerOutcomeCode
{
    Completed    = 0,
    UserCancelled = 1602,
    Failed       = 1603,
}

/// <summary>
/// Discriminated union of installer wizard steps. The step machine is a flat
/// <see cref="IReadOnlyList{T}"/> of these; the wizard renders one screen per
/// entry in order. Welcome / License / Installing / Finish are singletons;
/// <see cref="InstallDir"/> appears only when the manifest declared an
/// install-time <c>install_dir</c> parameter; <see cref="ParameterGroup"/>
/// repeats once per distinct <c>screen:</c> value declared on install-time
/// parameters (plus one synthetic "Install Options" group at the end for
/// parameters that omitted the field).
/// </summary>
public abstract record InstallerStepDef(string Id, string Title)
{
    public sealed record Welcome() : InstallerStepDef("welcome", "Welcome");
    public sealed record License() : InstallerStepDef("license", "License");
    public sealed record InstallDir() : InstallerStepDef("install_dir", "Choose Install Location");
    public sealed record ParameterGroup(string ScreenName, IReadOnlyList<ParameterFieldVm> Fields)
        : InstallerStepDef($"group:{ScreenName}", ScreenName);
    public sealed record Installing() : InstallerStepDef("installing", "Installing");
    public sealed record Finish() : InstallerStepDef("finish", "Finish");
    public sealed record Custom() : InstallerStepDef("custom", "Custom");
}

public sealed class InstallerViewModel : INotifyPropertyChanged
{
    private const string DefaultParameterGroupName = "Install Options";

    private int _currentStepIndex;
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

        // install_dir is special: it's the value the InstallDirView's TextBox
        // binds to. Preselect the user's manifest default if it declared an
        // install-time parameter named "install_dir"; otherwise fall back to
        // the conventional Program Files\AppName.
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
                Screen = p.Screen,
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

        Steps = BuildSteps(InstallTimeParameters, fields);

        LogoImage = TryLoadLogo(tokens.LogoFile);
    }

    /// <summary>
    /// Builds the ordered step list from the manifest's install-time
    /// parameters. Always includes Welcome / License / Installing / Finish.
    /// Inserts an InstallDir page when an <c>install_dir</c> parameter was
    /// declared. Inserts one ParameterGroup per unique <c>screen:</c> value,
    /// in first-appearance order. Parameters without a <c>screen:</c> value
    /// land in a trailing synthetic "Install Options" group. The
    /// <c>install_dir</c> parameter is excluded from every group — it lives
    /// exclusively on the InstallDir page.
    /// </summary>
    private static List<InstallerStepDef> BuildSteps(
        IReadOnlyList<InstallTimeParameter> parameters,
        IReadOnlyList<ParameterFieldVm> fields)
    {
        var steps = new List<InstallerStepDef>(8)
        {
            new InstallerStepDef.Welcome(),
            new InstallerStepDef.License(),
        };

        var hasInstallDir = parameters.Any(p =>
            string.Equals(p.Name, "install_dir", StringComparison.OrdinalIgnoreCase));
        if (hasInstallDir)
            steps.Add(new InstallerStepDef.InstallDir());

        // Group parameter fields by manifest-declared screen value. The
        // install_dir parameter never appears in any group (it gets its own
        // page above). Parameters without a screen value go into a trailing
        // synthetic "Install Options" group so single-page installers keep
        // their NSIS-equivalent look-and-feel.
        var grouped = new Dictionary<string, List<ParameterFieldVm>>(StringComparer.Ordinal);
        var orderedKeys = new List<string>();
        var defaultGroup = new List<ParameterFieldVm>();
        foreach (var f in fields)
        {
            if (string.Equals(f.Name, "install_dir", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrEmpty(f.Screen))
            {
                defaultGroup.Add(f);
                continue;
            }
            if (!grouped.TryGetValue(f.Screen, out var bucket))
            {
                bucket = new List<ParameterFieldVm>();
                grouped[f.Screen] = bucket;
                orderedKeys.Add(f.Screen);
            }
            bucket.Add(f);
        }

        foreach (var key in orderedKeys)
        {
            steps.Add(new InstallerStepDef.ParameterGroup(key, grouped[key]));
        }
        if (defaultGroup.Count > 0)
        {
            steps.Add(new InstallerStepDef.ParameterGroup(DefaultParameterGroupName, defaultGroup));
        }

        // If neither install_dir nor any parameters at all are declared, keep
        // the Install Options page visible so the wizard still has a screen
        // between License and Installing — the page just renders empty fields.
        if (!hasInstallDir && grouped.Count == 0 && defaultGroup.Count == 0)
        {
            steps.Add(new InstallerStepDef.ParameterGroup(
                DefaultParameterGroupName,
                Array.Empty<ParameterFieldVm>()));
        }

        steps.Add(new InstallerStepDef.Installing());
        steps.Add(new InstallerStepDef.Finish());
        return steps;
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
    /// Flat list of every install-time parameter as a per-field view-model.
    /// The Install Options page binds to the slice for the current
    /// <see cref="InstallerStepDef.ParameterGroup"/>; this flat list survives
    /// so existing wiring (HTTPS option fetch, install-subprocess launcher)
    /// can iterate every parameter regardless of which page hosts it.
    /// </summary>
    public IReadOnlyList<ParameterFieldVm> ParameterFields { get; } = Array.Empty<ParameterFieldVm>();

    /// <summary>
    /// Ordered list of wizard step definitions. Always begins with Welcome
    /// + License and ends with Installing + Finish; InstallDir / one or more
    /// ParameterGroup entries sit in between depending on the manifest.
    /// </summary>
    public IReadOnlyList<InstallerStepDef> Steps { get; }

    /// <summary>Zero-based index into <see cref="Steps"/>.</summary>
    public int CurrentStepIndex
    {
        get => _currentStepIndex;
        private set
        {
            if (_currentStepIndex == value) return;
            _currentStepIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentStepDef));
            OnPropertyChanged(nameof(CurrentStep));
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoNext));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CancelButtonText));
            OnPropertyChanged(nameof(CurrentGroupFields));
            OnPropertyChanged(nameof(CurrentGroupTitle));
        }
    }

    /// <summary>Current step definition; one entry from <see cref="Steps"/>.</summary>
    public InstallerStepDef CurrentStepDef => Steps[_currentStepIndex];

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

    /// <summary>
    /// Legacy coarse-grained step accessor for tests and back-compat callers.
    /// Setting this jumps to the FIRST step of the matching kind:
    /// <c>InstallOptions</c> picks the InstallDir page if one exists,
    /// otherwise the first ParameterGroup.
    /// </summary>
    public InstallerStep CurrentStep
    {
        get => MapToEnum(CurrentStepDef);
        set
        {
            var targetIndex = FindFirstIndexFor(value);
            if (targetIndex >= 0)
                CurrentStepIndex = targetIndex;
        }
    }

    private static InstallerStep MapToEnum(InstallerStepDef def) => def switch
    {
        InstallerStepDef.Welcome => InstallerStep.Welcome,
        InstallerStepDef.License => InstallerStep.License,
        InstallerStepDef.InstallDir => InstallerStep.InstallOptions,
        InstallerStepDef.ParameterGroup => InstallerStep.InstallOptions,
        InstallerStepDef.Installing => InstallerStep.Installing,
        InstallerStepDef.Finish => InstallerStep.Finish,
        InstallerStepDef.Custom => InstallerStep.Custom,
        _ => InstallerStep.Welcome,
    };

    private int FindFirstIndexFor(InstallerStep step)
    {
        for (var i = 0; i < Steps.Count; i++)
        {
            if (MapToEnum(Steps[i]) == step)
                return i;
        }
        return -1;
    }

    public bool CanGoBack =>
        CurrentStepIndex > 0
        && CurrentStepDef is not InstallerStepDef.Installing
        && CurrentStepDef is not InstallerStepDef.Finish;

    public bool CanGoNext =>
        CurrentStepIndex < Steps.Count - 1
        && CurrentStepDef is not InstallerStepDef.Installing
        && CurrentStepDef is not InstallerStepDef.Finish;

    /// <summary>
    /// Cancel/Close button enable state.
    /// False ONLY on Installing — the child setup.exe /S subprocess is in
    /// flight (sc.exe / registry / file_copy operations); killing the wizard
    /// would orphan the child and leave the system half-installed. On Finish
    /// the same button text changes to "Close" (see <see cref="CancelButtonText"/>)
    /// and the click handler closes with <see cref="InstallerOutcomeCode.Completed"/>.
    /// </summary>
    public bool CanCancel => CurrentStepDef is not InstallerStepDef.Installing;

    /// <summary>
    /// Dynamic label for the bottom-row button. "Close" on the Finish screen
    /// (install is done — clicking closes the wizard with Completed exit code),
    /// "Cancel" everywhere else (user is abandoning; closes with UserCancelled).
    /// </summary>
    public string CancelButtonText => CurrentStepDef is InstallerStepDef.Finish ? "Close" : "Cancel";

    /// <summary>
    /// Fields displayed on the current parameter-group page. Empty for any
    /// non-ParameterGroup step.
    /// </summary>
    public IReadOnlyList<ParameterFieldVm> CurrentGroupFields =>
        CurrentStepDef is InstallerStepDef.ParameterGroup group
            ? group.Fields
            : Array.Empty<ParameterFieldVm>();

    /// <summary>
    /// Title text displayed at the top of the current step. Used by the
    /// Install Options view to show the manifest-declared screen name (e.g.
    /// "Server Settings", "Kiosk Settings") instead of a static heading.
    /// </summary>
    public string CurrentGroupTitle => CurrentStepDef.Title;

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
                OnPropertyChanged(nameof(InstallDirDriveLabel));
                OnPropertyChanged(nameof(InstallDirSpaceLabel));
            }
        }
    }

    /// <summary>
    /// Drive name + filesystem label for the disk the chosen install path
    /// lives on. Recomputed whenever <see cref="InstallPath"/> changes; falls
    /// back to a friendly placeholder when the path is invalid or the drive
    /// can't be queried (network drives, removable media not present, ...).
    /// </summary>
    public string InstallDirDriveLabel =>
        TryGetDriveInfo() is { } d
            ? $"Drive: {d.Name.TrimEnd('\\')}  ({d.DriveFormat})"
            : "Drive: (unavailable)";

    /// <summary>
    /// Human-readable free / total space readout for the destination drive
    /// (e.g. "Free space: 152.31 GB  /  Total: 931.51 GB"). NSIS-style
    /// disk-space indicator on the Install Directory page.
    /// </summary>
    public string InstallDirSpaceLabel =>
        TryGetDriveInfo() is { IsReady: true } d
            ? $"Free space: {FormatBytes(d.AvailableFreeSpace)}  /  Total: {FormatBytes(d.TotalSize)}"
            : "Free space: (drive not ready)";

    private DriveInfo? TryGetDriveInfo()
    {
        try
        {
            var root = Path.GetPathRoot(_installPath);
            if (string.IsNullOrEmpty(root)) return null;
            return new DriveInfo(root);
        }
#pragma warning disable CA1031 // disk-info readout must never crash the wizard
        catch
        {
            return null;
        }
#pragma warning restore CA1031
    }

    private static string FormatBytes(long bytes)
    {
        const double KB = 1024d;
        const double MB = KB * 1024d;
        const double GB = MB * 1024d;
        const double TB = GB * 1024d;
        return bytes switch
        {
            >= (long)TB => $"{bytes / TB:F2} TB",
            >= (long)GB => $"{bytes / GB:F2} GB",
            >= (long)MB => $"{bytes / MB:F2} MB",
            >= (long)KB => $"{bytes / KB:F2} KB",
            _ => $"{bytes} B",
        };
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

    /// <summary>
    /// Advances one step forward. Honours the License-acceptance gate: when
    /// the current step is License and the user hasn't ticked the agreement,
    /// the call is a no-op so the Next button feels disabled even though it
    /// stays clickable.
    /// </summary>
    public void Next()
    {
        if (CurrentStepDef is InstallerStepDef.License && !LicenseAccepted)
            return;
        if (CurrentStepIndex < Steps.Count - 1)
            CurrentStepIndex = CurrentStepIndex + 1;
    }

    /// <summary>
    /// Walks one step back. Locked on Installing (can't rewind a running
    /// install) and Finish (no behind to go to).
    /// </summary>
    public void Back()
    {
        if (CurrentStepDef is InstallerStepDef.Installing || CurrentStepDef is InstallerStepDef.Finish)
            return;
        if (CurrentStepIndex > 0)
            CurrentStepIndex = CurrentStepIndex - 1;
    }

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
        if (CurrentStepDef is InstallerStepDef.Finish)
        {
            OutcomeCode = InstallerOutcomeCode.Completed;
            return true;
        }

        if (CurrentStepDef is InstallerStepDef.Installing && _engineCts is not null)
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

    /// <summary>
    /// Manifest-declared <c>screen:</c> value for this parameter. Drives the
    /// wizard's multi-page grouping — fields with the same screen value land
    /// on the same Install Options page. Null/empty means "Install Options"
    /// (the synthetic default group).
    /// </summary>
    public string? Screen { get; init; }

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
    /// True when the manifest declared <c>type: bool</c> — drives CheckBox
    /// visibility. Bool defaults arrive as the strings "True"/"False" from
    /// <see cref="InstallTimeParameter.DefaultAsString"/>; <see cref="BoolValue"/>
    /// is the two-way bound property the CheckBox binds to.
    /// </summary>
    public bool IsBool =>
        string.Equals(Type, "bool", StringComparison.OrdinalIgnoreCase) && !IsStaticEnum && !IsDynamicEnum;

    /// <summary>
    /// True when none of {static enum, dynamic enum, bool} — drives TextBox
    /// visibility (string / path / int / secret typed in by hand).
    /// </summary>
    public bool IsTextual => !IsStaticEnum && !IsDynamicEnum && !IsBool;

    /// <summary>
    /// Boolean projection of <see cref="CurrentValue"/> for CheckBox.IsChecked
    /// two-way binding. Parses "True"/"False"/"true"/"false" case-insensitively;
    /// anything else maps to false. Writes back the canonical "True"/"False"
    /// form so the install-launcher's <c>/Name=Value</c> argv stays consistent
    /// with the manifest's bool defaults.
    /// </summary>
    public bool BoolValue
    {
        get => bool.TryParse(CurrentValue, out var b) && b;
        set
        {
            var s = value ? "True" : "False";
            if (CurrentValue != s) CurrentValue = s;
        }
    }

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
                // BoolValue is a derived view of CurrentValue; notify so the
                // CheckBox.IsChecked binding refreshes when CurrentValue is
                // mutated through any path (write-back, programmatic set,
                // initial seed).
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BoolValue)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
