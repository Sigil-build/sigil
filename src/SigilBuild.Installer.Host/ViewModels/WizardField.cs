using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using SigilBuild.Core.Manifest;

namespace SigilBuild.Installer.Host.ViewModels;

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
    public static WizardWidget Infer(ParameterType type, string? widgetOverride, int enumCount)
    {
        var w = widgetOverride?.Trim().ToLowerInvariant();
        return type switch
        {
            ParameterType.Bool => w == "switch" ? WizardWidget.Switch : WizardWidget.Checkbox,
            ParameterType.Enum => w == "radio" ? WizardWidget.Radio
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
    public FieldViewModel(ParameterDefinition def, string? widgetOverride)
    {
        Definition = def ?? throw new ArgumentNullException(nameof(def));
        EnumOptions = def.EnumValues ?? Array.Empty<string>();
        Widget = WidgetFactory.Infer(def.Type, widgetOverride, EnumOptions.Count);
        Label = string.IsNullOrWhiteSpace(def.Description) ? def.Name : def.Description!;

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
    public string RevealLabel => RevealSecret ? "Hide" : "Show";

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
            ValidationError = $"{Label} is required.";
            return false;
        }

        if ((IsRadio || IsDropdown) && required && string.IsNullOrEmpty(value))
        {
            ValidationError = $"Choose a {Label}.";
            return false;
        }

        // Enum membership.
        if ((IsRadio || IsDropdown) && !string.IsNullOrEmpty(value))
        {
            var member = false;
            foreach (var opt in EnumOptions)
            {
                if (string.Equals(opt, value, StringComparison.Ordinal)) { member = true; break; }
            }
            if (!member)
            {
                ValidationError = $"'{value}' is not a valid choice.";
                return false;
            }
        }

        // Numeric range.
        if (Definition.Type == ParameterType.Int && !string.IsNullOrWhiteSpace(value))
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                ValidationError = $"{Label} must be a whole number.";
                return false;
            }
            if (Definition.Min is { } min && n < min)
            {
                ValidationError = $"{Label} must be at least {min}.";
                return false;
            }
            if (Definition.Max is { } max && n > max)
            {
                ValidationError = $"{Label} must be at most {max}.";
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
                ValidationError = $"{Label} is not in the expected format.";
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
        string id, string title, string? subtitle, string? when, IReadOnlyList<FieldViewModel> fields)
    {
        Id = id;
        Title = title;
        Subtitle = subtitle;
        When = when;
        Fields = fields;
    }

    public string Id { get; }
    public string Title { get; }
    public string? Subtitle { get; }
    public string? When { get; }
    public IReadOnlyList<FieldViewModel> Fields { get; }

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
