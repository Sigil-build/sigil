using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;
using SigilBuild.Installer.Host.Services;
using SigilBuild.Wrapper.Core.Localization;

namespace SigilBuild.Installer.Host.ViewModels;

/// <summary>
/// A selectable dropdown entry — <see cref="Label"/> is shown to the user,
/// <see cref="Value"/> is bound into <c>param.*</c>. For a static manifest enum
/// the two are identical; for an HTTP <c>source</c>-backed dropdown they differ
/// (e.g. a friendly name vs an opaque id). <see cref="ToString"/> returns the
/// label so the imperatively-built ComboBox shows it without a template.
/// </summary>
public sealed record DropdownOption(string Label, string Value)
{
    public override string ToString() => Label;
}

/// <summary>
/// Injectable seam for fetching a dynamic dropdown's options. The production
/// default is <see cref="HttpOptionsLoader.LoadAsync"/>; tests substitute a
/// canned delegate so unit tests never touch the network. The parameter order
/// mirrors <see cref="HttpOptionsLoader.LoadAsync"/> exactly.
/// </summary>
public delegate Task<IReadOnlyList<HttpOption>> OptionsFetcher(
    string url, string itemsPath, string labelProperty, string valueProperty, CancellationToken ct);

/// <summary>
/// The rendered widget for a declared screen field (T9). Inferred from the
/// parameter's <see cref="ParameterType"/> and optionally overridden by the
/// field's <c>widget</c> key. See <see cref="WidgetFactory.Infer"/>.
/// </summary>
public enum WizardWidget
{
    Checkbox,
    Switch,
    Radio,
    Dropdown,
    SecretInput,
    PathInput,
    TextInput,
    TextArea,
    NumberInput,
    Slider,
}

/// <summary>
/// Maps a declared parameter (its <see cref="ParameterType"/> and an optional
/// <c>widget</c> override) to the concrete wizard widget, per the T9 inference
/// table. The single source of truth shared by the view factory and tests.
/// </summary>
public static class WidgetFactory
{
    public static WizardWidget Infer(ParameterType type, string? widgetOverride, int enumCount, bool hasSource = false)
    {
        var w = widgetOverride?.Trim().ToLowerInvariant();
        return type switch
        {
            ParameterType.Bool => w == "switch" ? WizardWidget.Switch : WizardWidget.Checkbox,
            // A source-backed enum has no static values to count and always renders
            // as a ComboBox (per ParameterSource's contract) — never a radio group.
            ParameterType.Enum => hasSource ? WizardWidget.Dropdown
                : w == "radio" ? WizardWidget.Radio
                : w == "dropdown" ? WizardWidget.Dropdown
                : enumCount <= 4 ? WizardWidget.Radio : WizardWidget.Dropdown,
            ParameterType.Secret => WizardWidget.SecretInput,
            ParameterType.Path => WizardWidget.PathInput,
            ParameterType.String => w == "textarea" ? WizardWidget.TextArea : WizardWidget.TextInput,
            ParameterType.Int => w == "slider" ? WizardWidget.Slider : WizardWidget.NumberInput,
            _ => WizardWidget.TextInput,
        };
    }
}

/// <summary>
/// View-model for a single field on a declared custom screen. Holds the current
/// value across every widget shape, validates it against the parameter's
/// <c>pattern</c>/<c>min</c>/<c>max</c>/<c>enum</c> constraints before the wizard
/// advances, and exposes the collected value as a string for the engine.
/// </summary>
public sealed class FieldViewModel : INotifyPropertyChanged
{
    // P9: the resolved chrome language for this session, captured once per field
    // (Task 4 sets SessionLanguage before any UI is built).
    private readonly Lang _lang = SessionLanguage.Current;

    public FieldViewModel(ParameterDefinition def, string? widgetOverride)
    {
        Definition = def ?? throw new ArgumentNullException(nameof(def));
        EnumOptions = def.EnumValues ?? Array.Empty<string>();
        Widget = WidgetFactory.Infer(def.Type, widgetOverride, EnumOptions.Count, def.Source is not null);
        Label = string.IsNullOrWhiteSpace(def.Description?.English) ? def.Name : def.Description!.English;

        // Seed the dropdown's items. A static enum's items are (label == value ==
        // option); a source-backed dropdown starts empty and is populated on
        // navigation by LoadDynamicOptionsAsync.
        if (Widget == WizardWidget.Dropdown && def.Source is null)
        {
            foreach (var opt in EnumOptions)
            {
                DropdownOptions.Add(new DropdownOption(opt, opt));
            }
        }

        // Seed the initial value from the schema default.
        switch (Widget)
        {
            case WizardWidget.Checkbox:
            case WizardWidget.Switch:
                _boolValue = def.Default switch
                {
                    bool b => b,
                    string s => bool.TryParse(s, out var pb) && pb,
                    _ => false,
                };
                break;
            case WizardWidget.Radio:
            case WizardWidget.Dropdown:
                _selectedOption = def.Default?.ToString();
                break;
            default:
                _textValue = def.Default?.ToString() ?? string.Empty;
                break;
        }
    }

    public ParameterDefinition Definition { get; }
    public string ParamName => Definition.Name;
    public WizardWidget Widget { get; }
    public string Label { get; }
    public IReadOnlyList<string> EnumOptions { get; }

    // --- Widget-shape flags for the view factory / bindings ---
    public bool IsCheckable => Widget is WizardWidget.Checkbox or WizardWidget.Switch;
    public bool IsRadio => Widget == WizardWidget.Radio;
    public bool IsDropdown => Widget == WizardWidget.Dropdown;
    public bool IsSecret => Widget == WizardWidget.SecretInput;
    public bool IsPath => Widget == WizardWidget.PathInput;
    public bool IsTextArea => Widget == WizardWidget.TextArea;
    public bool IsSlider => Widget == WizardWidget.Slider;
    public bool IsTextLike => Widget is WizardWidget.TextInput or WizardWidget.TextArea
        or WizardWidget.PathInput or WizardWidget.SecretInput or WizardWidget.NumberInput;

    private string _textValue = string.Empty;
    public string TextValue
    {
        get => _textValue;
        set { if (_textValue != value) { _textValue = value; OnPropertyChanged(); } }
    }

    private bool _boolValue;
    public bool BoolValue
    {
        get => _boolValue;
        set { if (_boolValue != value) { _boolValue = value; OnPropertyChanged(); } }
    }

    private string? _selectedOption;
    public string? SelectedOption
    {
        get => _selectedOption;
        set { if (_selectedOption != value) { _selectedOption = value; OnPropertyChanged(); } }
    }

    // --- Dynamic (source-backed) dropdown options ---

    /// <summary>
    /// The dropdown's items. For a static enum these are (label == value) pairs
    /// seeded in the constructor; for a <c>source</c>-backed dropdown they are
    /// filled by <see cref="LoadDynamicOptionsAsync"/> when the screen is shown.
    /// </summary>
    public ObservableCollection<DropdownOption> DropdownOptions { get; } = new();

    /// <summary>True when this field pulls its options from an HTTP <c>source</c>.</summary>
    public bool HasDynamicOptions => Definition.Source is not null;

    private bool _isLoadingOptions;
    /// <summary>True while a dynamic option fetch is in flight (drives a "Loading…" hint).</summary>
    public bool IsLoadingOptions
    {
        get => _isLoadingOptions;
        private set { if (_isLoadingOptions != value) { _isLoadingOptions = value; OnPropertyChanged(); } }
    }

    private string? _optionsError;
    /// <summary>
    /// Set to an inline "couldn't load options" message when the fetch fails or
    /// returns nothing; null on success. Never blocks the wizard — the user can
    /// go back or proceed (subject to the field's required/enum validation).
    /// </summary>
    public string? OptionsError
    {
        get => _optionsError;
        private set
        {
            if (_optionsError != value)
            {
                _optionsError = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasOptionsError));
            }
        }
    }

    public bool HasOptionsError => !string.IsNullOrEmpty(_optionsError);

    private static readonly Regex _paramPlaceholder =
        new(@"\$\{parameters\.([A-Za-z0-9_]+)\}", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Substitute <c>${parameters.name}</c> placeholders in a source URL from the
    /// currently-collected parameter values. An unknown name resolves to empty so
    /// a stale placeholder never leaks the literal token into the request.
    /// </summary>
    public static string SubstituteParameters(string url, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(url) || url.IndexOf("${", StringComparison.Ordinal) < 0)
        {
            return url;
        }
        return _paramPlaceholder.Replace(url, m =>
            values is not null && values.TryGetValue(m.Groups[1].Value, out var v) ? v : string.Empty);
    }

    /// <summary>
    /// Fetch this field's dropdown options from its <c>source</c> endpoint and
    /// populate <see cref="DropdownOptions"/>. The URL's <c>${parameters.*}</c>
    /// placeholders are substituted from <paramref name="collectedValues"/> before
    /// the fetch. No-op for a static (source-less) field. A failed/empty fetch is
    /// caught, logged, and surfaced via <see cref="OptionsError"/> — it never
    /// throws, so the wizard keeps running.
    /// </summary>
    /// <param name="fetcher">The option source (production: <see cref="HttpOptionsLoader.LoadAsync"/>).</param>
    public async Task LoadDynamicOptionsAsync(
        OptionsFetcher fetcher,
        IReadOnlyDictionary<string, string> collectedValues,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        var source = Definition.Source;
        if (source is null)
        {
            return; // static field: nothing to fetch.
        }

        var url = SubstituteParameters(source.Url, collectedValues ?? new Dictionary<string, string>());
        IsLoadingOptions = true;
        OptionsError = null;
        try
        {
            var options = await fetcher(url, source.ItemsPath, source.LabelProperty, source.ValueProperty, ct)
                .ConfigureAwait(true);

            DropdownOptions.Clear();
            foreach (var o in options)
            {
                DropdownOptions.Add(new DropdownOption(o.Label, o.Value));
            }

            // Preserve a prior selection only if it still exists among the new items.
            if (_selectedOption is { } sel && !DropdownOptionsContainValue(sel))
            {
                SelectedOption = null;
            }

            if (DropdownOptions.Count == 0)
            {
                OptionsError = Strings.FieldOptionsLoadFailed(_lang);
            }
        }
        catch (OperationCanceledException)
        {
            throw; // navigating away / disposal — let the caller observe cancellation.
        }
#pragma warning disable CA1031 // A network/HTTP/parse failure must not crash the wizard — surface inline.
        catch (Exception ex)
        {
            InstallerLog.Error($"LoadDynamicOptions '{ParamName}' failed", ex);
            DropdownOptions.Clear();
            OptionsError = Strings.FieldOptionsLoadFailed(_lang);
        }
#pragma warning restore CA1031
        finally
        {
            IsLoadingOptions = false;
        }
    }

    private bool DropdownOptionsContainValue(string value)
    {
        foreach (var o in DropdownOptions)
        {
            if (string.Equals(o.Value, value, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private bool _revealSecret;
    /// <summary>Show/hide toggle for a secret input (masked by default).</summary>
    public bool RevealSecret
    {
        get => _revealSecret;
        set
        {
            if (_revealSecret != value)
            {
                _revealSecret = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PasswordChar));
                OnPropertyChanged(nameof(RevealLabel));
            }
        }
    }

    /// <summary>The mask char for a secret TextBox — empty string reveals the value.</summary>
    public char PasswordChar => IsSecret && !RevealSecret ? '•' : '\0';
    public string RevealLabel => RevealSecret ? Strings.FieldHide(_lang) : Strings.FieldShow(_lang);

    private string? _validationError;
    public string? ValidationError
    {
        get => _validationError;
        private set
        {
            if (_validationError != value)
            {
                _validationError = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_validationError);

    /// <summary>The current field value rendered as the string the engine consumes.</summary>
    public string GetStringValue() => Widget switch
    {
        WizardWidget.Checkbox or WizardWidget.Switch => BoolValue ? "true" : "false",
        WizardWidget.Radio or WizardWidget.Dropdown => SelectedOption ?? string.Empty,
        _ => TextValue ?? string.Empty,
    };

    /// <summary>
    /// Validate the current value against the parameter's declared constraints.
    /// Sets <see cref="ValidationError"/> (surfaced inline) and returns whether the
    /// wizard may advance past this field.
    /// </summary>
    public bool Validate()
    {
        var value = GetStringValue();

        // Required: an install-time parameter with no default must be supplied.
        var required = Definition.InstallTime && Definition.Default is null;
        if (IsTextLike && required && string.IsNullOrWhiteSpace(value))
        {
            ValidationError = Strings.FieldErrorRequired(_lang, Label);
            return false;
        }

        if ((IsRadio || IsDropdown) && required && string.IsNullOrEmpty(value))
        {
            ValidationError = Strings.FieldErrorChoose(_lang, Label);
            return false;
        }

        // Enum membership. A dropdown validates against its live items (which for a
        // source-backed field are the fetched values); a radio against its static
        // enum set.
        if ((IsRadio || IsDropdown) && !string.IsNullOrEmpty(value))
        {
            var member = false;
            if (IsDropdown)
            {
                member = DropdownOptionsContainValue(value);
            }
            else
            {
                foreach (var opt in EnumOptions)
                {
                    if (string.Equals(opt, value, StringComparison.Ordinal)) { member = true; break; }
                }
            }
            if (!member)
            {
                ValidationError = Strings.FieldErrorInvalidChoice(_lang, value);
                return false;
            }
        }

        // Numeric range.
        if (Definition.Type == ParameterType.Int && !string.IsNullOrWhiteSpace(value))
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                ValidationError = Strings.FieldErrorNotInteger(_lang, Label);
                return false;
            }
            if (Definition.Min is { } min && n < min)
            {
                ValidationError = Strings.FieldErrorMin(_lang, Label, min.ToString(CultureInfo.InvariantCulture));
                return false;
            }
            if (Definition.Max is { } max && n > max)
            {
                ValidationError = Strings.FieldErrorMax(_lang, Label, max.ToString(CultureInfo.InvariantCulture));
                return false;
            }
        }

        // Pattern.
        if (Definition.Pattern is { } pattern && !string.IsNullOrEmpty(value))
        {
            bool ok;
            try
            {
                ok = Regex.IsMatch(value, pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            }
#pragma warning disable CA1031 // A malformed pattern must not crash the wizard — treat as a validation failure.
            catch (Exception)
            {
                ok = false;
            }
#pragma warning restore CA1031
            if (!ok)
            {
                ValidationError = Strings.FieldErrorPattern(_lang, Label);
                return false;
            }
        }

        ValidationError = null;
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? string.Empty));
}

/// <summary>
/// View-model for one declared custom screen (T9): its interpolated title +
/// subtitle and the ordered field view-models rendered on it.
/// </summary>
public sealed class CustomScreenViewModel
{
    public CustomScreenViewModel(
        string id, string title, string? subtitle, string? when, IReadOnlyList<FieldViewModel> fields,
        LocalizedText titleMap)
    {
        Id = id;
        Title = title;
        Subtitle = subtitle;
        When = when;
        Fields = fields;
        TitleMap = titleMap ?? throw new ArgumentNullException(nameof(titleMap));
    }

    public string Id { get; }
    public string Title { get; }
    public string? Subtitle { get; }
    public string? When { get; }
    public IReadOnlyList<FieldViewModel> Fields { get; }

    /// <summary>
    /// The manifest's raw <c>{tag -&gt; text}</c> screen title (P9), kept alongside
    /// the already-interpolated <see cref="Title"/> so the rail can resolve the
    /// declared screen's title against the session language instead of falling
    /// back to <c>rail.configure</c> — see <c>InstallerViewModel.RebuildRail</c>.
    /// </summary>
    public LocalizedText TitleMap { get; }

    /// <summary>Validate every field; returns true only when all pass (inline errors set on failures).</summary>
    public bool Validate()
    {
        var ok = true;
        foreach (var f in Fields)
        {
            if (!f.Validate())
            {
                ok = false;
            }
        }
        return ok;
    }
}

/// <summary>
/// View-model for one built-in option component checkbox on the Options screen
/// (T8). Seeded from the component's resolved default; a <c>locked</c> component
/// renders disabled (<see cref="IsEnabled"/> is false) and cannot be toggled —
/// it is always applied at its default.
/// </summary>
public sealed class OptionItemViewModel : INotifyPropertyChanged
{
    public OptionItemViewModel(InstallerOptionComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        Name = component.Name;
        IsLocked = component.Locked;
        Label = LabelFor(component.Name, SessionLanguage.Current);
        _isChecked = component.Default;
    }

    /// <summary>The canonical component key (e.g. <c>desktop_shortcut</c>) — the key the engine binds into <c>option.*</c>.</summary>
    public string Name { get; }

    /// <summary>The human-readable checkbox caption.</summary>
    public string Label { get; }

    /// <summary>True for a <c>locked</c> component: rendered disabled, always applied at its default.</summary>
    public bool IsLocked { get; }

    /// <summary>Bindable enabled-state for the checkbox — a locked component is disabled.</summary>
    public bool IsEnabled => !IsLocked;

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            // A locked component cannot be toggled by the user.
            if (IsLocked || _isChecked == value)
            {
                return;
            }
            _isChecked = value;
            OnPropertyChanged();
        }
    }

    // The four known built-in components get catalog keys. An unknown,
    // author-supplied component name falls back to itself (P10's job to
    // localize, not P9's) — deliberately kept, not a gap.
    private static string LabelFor(string name, Lang lang) => name switch
    {
        "desktop_shortcut" => Strings.OptionsDesktopShortcut(lang),
        "start_menu" => Strings.OptionsStartMenu(lang),
        "add_to_path" => Strings.OptionsAddToPath(lang),
        "file_associations" => Strings.OptionsFileAssociations(lang),
        _ => name,
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? string.Empty));
}

/// <summary>A single entry in the wizard's rail step indicator.</summary>
public sealed class RailStep
{
    public RailStep(string label, bool isCurrent, bool isDone)
    {
        Label = label;
        IsCurrent = isCurrent;
        IsDone = isDone;
    }

    public string Label { get; }
    public bool IsCurrent { get; }
    public bool IsDone { get; }

    /// <summary>Emphasise the current step; dim upcoming ones. Theme-neutral (opacity only).</summary>
    public double LabelOpacity => IsCurrent ? 1.0 : IsDone ? 0.8 : 0.5;

    public Avalonia.Media.FontWeight LabelWeight =>
        IsCurrent ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal;
}
