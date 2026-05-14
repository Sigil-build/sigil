using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace SigilBuild.Installer.Host.ViewModels;

/// <summary>
/// True when the bound string value matches any token in the
/// comma-separated <c>ConverterParameter</c> list. Used by the Install
/// Options screen's per-parameter DataTemplate to toggle ComboBox vs
/// TextBox visibility based on <see cref="ParameterFieldVm.Type"/>:
/// <c>IsVisible="{Binding Type, Converter=..., ConverterParameter=enum}"</c>
/// renders a ComboBox only for enum-typed parameters; the sibling TextBox
/// uses <c>ConverterParameter=string,path,int,bool,secret</c> to render
/// everything else.
/// </summary>
public sealed class EqualsConverter : IValueConverter
{
    public static readonly EqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || parameter is not string p) return false;
        foreach (var tok in p.Split(','))
            if (s.Equals(tok, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
