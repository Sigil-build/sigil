using System;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using SigilBuild.Installer.Host.ViewModels;

namespace SigilBuild.Installer.Host.Views.Screens;

/// <summary>
/// Renders a declared custom screen (T9): a title/subtitle plus one control per
/// field, built from the field's inferred <see cref="WizardWidget"/> via a widget
/// factory keyed on parameter type. No arbitrary markup — forms over parameters.
/// Controls are wired to the <see cref="FieldViewModel"/> with explicit event
/// handlers (not reflection bindings) so the host stays Native-AOT / trim clean.
/// </summary>
public partial class CustomView : UserControl
{
    public CustomView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Rebuild();
        Rebuild();
    }

    private void Rebuild()
    {
        var host = this.FindControl<StackPanel>("FieldsHost");
        var titleText = this.FindControl<TextBlock>("TitleText");
        var subtitleText = this.FindControl<TextBlock>("SubtitleText");
        if (host is null || titleText is null || subtitleText is null)
        {
            return;
        }

        host.Children.Clear();

        var screen = (DataContext as InstallerViewModel)?.CurrentCustomScreen;
        if (screen is null)
        {
            titleText.Text = string.Empty;
            subtitleText.IsVisible = false;
            return;
        }

        titleText.Text = screen.Title;
        subtitleText.Text = screen.Subtitle ?? string.Empty;
        subtitleText.IsVisible = !string.IsNullOrEmpty(screen.Subtitle);

        foreach (var f in screen.Fields)
        {
            host.Children.Add(BuildFieldRow(f));
        }
    }

    private static StackPanel BuildFieldRow(FieldViewModel field)
    {
        var container = new StackPanel { Spacing = 6 };

        // Label row (checkables carry their own inline label, so skip it there).
        if (!field.IsCheckable)
        {
            container.Children.Add(BuildLabelRow(field));
        }

        container.Children.Add(BuildInput(field));

        // Inline validation error, kept in sync with the field VM.
        var error = new TextBlock
        {
            Foreground = Brushes.Firebrick,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Text = field.ValidationError ?? string.Empty,
            IsVisible = field.HasError,
        };
        field.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(FieldViewModel.ValidationError) or nameof(FieldViewModel.HasError))
            {
                error.Text = field.ValidationError ?? string.Empty;
                error.IsVisible = field.HasError;
            }
        };
        container.Children.Add(error);

        return container;
    }

    private static Control BuildLabelRow(FieldViewModel field)
    {
        // Secret fields put a Show/Hide toggle on the right of the label row.
        if (field.IsSecret)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var label = new TextBlock { Text = field.Label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var toggle = new Button
            {
                Content = field.RevealLabel,
                FontSize = 12,
                Padding = new Thickness(4, 0, 4, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            toggle.Click += (_, _) => field.RevealSecret = !field.RevealSecret;
            field.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(FieldViewModel.RevealLabel))
                {
                    toggle.Content = field.RevealLabel;
                }
            };
            Grid.SetColumn(label, 0);
            Grid.SetColumn(toggle, 1);
            grid.Children.Add(label);
            grid.Children.Add(toggle);
            return grid;
        }

        return new TextBlock { Text = field.Label, FontSize = 12 };
    }

    private static Control BuildInput(FieldViewModel field) => field.Widget switch
    {
        WizardWidget.Checkbox => BuildCheckbox(field, asSwitch: false),
        WizardWidget.Switch => BuildCheckbox(field, asSwitch: true),
        WizardWidget.Radio => BuildRadio(field),
        WizardWidget.Dropdown => BuildDropdown(field),
        WizardWidget.SecretInput => BuildSecret(field),
        WizardWidget.PathInput => BuildPath(field),
        WizardWidget.TextArea => BuildTextBox(field, multiline: true),
        WizardWidget.NumberInput => BuildTextBox(field, multiline: false),
        WizardWidget.Slider => BuildSlider(field),
        _ => BuildTextBox(field, multiline: false),
    };

    private static ToggleButton BuildCheckbox(FieldViewModel field, bool asSwitch)
    {
        ToggleButton toggle = asSwitch
            ? new ToggleSwitch { Content = field.Label }
            : new CheckBox { Content = field.Label };
        toggle.IsChecked = field.BoolValue;
        toggle.IsCheckedChanged += (_, _) => field.BoolValue = toggle.IsChecked == true;
        return toggle;
    }

    private static StackPanel BuildRadio(FieldViewModel field)
    {
        var group = "grp_" + field.ParamName + "_" + Guid.NewGuid().ToString("N");
        var panel = new StackPanel { Spacing = 6 };
        foreach (var option in field.EnumOptions)
        {
            var radio = new RadioButton
            {
                GroupName = group,
                Content = option,
                IsChecked = string.Equals(option, field.SelectedOption, StringComparison.Ordinal),
            };
            var captured = option;
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true)
                {
                    field.SelectedOption = captured;
                }
            };
            panel.Children.Add(radio);
        }
        return panel;
    }

    private static ComboBox BuildDropdown(FieldViewModel field)
    {
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var option in field.EnumOptions)
        {
            combo.Items.Add(option);
        }
        combo.SelectedItem = field.SelectedOption;
        combo.SelectionChanged += (_, _) => field.SelectedOption = combo.SelectedItem as string;
        return combo;
    }

    private static TextBox BuildSecret(FieldViewModel field)
    {
        var box = new TextBox { PlaceholderText = "••••••••", PasswordChar = field.PasswordChar };
        box.Text = field.TextValue;
        box.TextChanged += (_, _) => field.TextValue = box.Text ?? string.Empty;
        field.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FieldViewModel.PasswordChar))
            {
                box.PasswordChar = field.PasswordChar;
            }
        };
        return box;
    }

    private static Grid BuildPath(FieldViewModel field)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var box = new TextBox { Text = field.TextValue };
        box.TextChanged += (_, _) => field.TextValue = box.Text ?? string.Empty;
        var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += async (s, _) =>
        {
            var top = TopLevel.GetTopLevel(s as Control);
            if (top is null)
            {
                return;
            }
#pragma warning disable CA1031 // A picker failure must not crash the wizard.
            try
            {
                var folders = await top.StorageProvider.OpenFolderPickerAsync(
                    new Avalonia.Platform.Storage.FolderPickerOpenOptions { AllowMultiple = false });
                if (folders.Count > 0)
                {
                    box.Text = folders[0].Path.LocalPath;
                }
            }
            catch (Exception)
            {
                // Best-effort; leave the typed value in place.
            }
#pragma warning restore CA1031
        };
        Grid.SetColumn(box, 0);
        Grid.SetColumn(browse, 1);
        grid.Children.Add(box);
        grid.Children.Add(browse);
        return grid;
    }

    private static TextBox BuildTextBox(FieldViewModel field, bool multiline)
    {
        var box = new TextBox { Text = field.TextValue };
        if (multiline)
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.MinHeight = 80;
        }
        box.TextChanged += (_, _) => field.TextValue = box.Text ?? string.Empty;
        return box;
    }

    private static Slider BuildSlider(FieldViewModel field)
    {
        var min = field.Definition.Min ?? 0;
        var max = field.Definition.Max ?? 100;
        var slider = new Slider { Minimum = min, Maximum = max };
        if (int.TryParse(field.TextValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed))
        {
            slider.Value = seed;
        }
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                field.TextValue = ((int)Math.Round(slider.Value)).ToString(CultureInfo.InvariantCulture);
            }
        };
        return slider;
    }
}
