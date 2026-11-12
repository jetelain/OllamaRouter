using System.Globalization;
using Avalonia.Data.Converters;

namespace OllamaRouter.App.Converters;

/// <summary>
/// Compares an <see cref="int"/> value to an <see cref="int"/> parameter and maps equality to
/// <c>true</c> (visible) / inequality to <c>false</c> (collapsed) (task #103). Avalonia exposes
/// visibility as the <c>IsVisible</c> BOOL, so this converter drives that property — used for
/// the wizard step panel:
/// <c>IsVisible="{Binding SelectedStep, Converter={x:Static IntEqualityToVisibilityConverter.Instance}, ConverterParameter=0}"</c>.
/// </summary>
public sealed class IntEqualityToVisibilityConverter : IValueConverter
{
    /// <summary>The shared instance (XAML <c>{x:Static}</c>).</summary>
    public static readonly IntEqualityToVisibilityConverter Instance = new();

    private IntEqualityToVisibilityConverter()
    {
    }

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int valueInt
        && IntEqualityConverterSupport.TryGetIntParameter(parameter, culture, out var parameterInt)
        && valueInt == parameterInt;

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Parameter handling shared by the wizard's int-equality converters (task #103): compiled
/// Avalonia XAML materializes <c>ConverterParameter="0"</c> as the STRING <c>"0"</c> (the
/// runtime XAML loader does the same — only direct C# calls pass a real <c>int</c>), so both
/// string and int parameters must be accepted. A check for <c>int</c> alone would silently
/// never match and hide every wizard step pane.
/// </summary>
internal static class IntEqualityConverterSupport
{
    /// <summary>Parses the converter parameter as an int (accepts int or string forms).</summary>
    internal static bool TryGetIntParameter(object? parameter, CultureInfo culture, out int value)
    {
        switch (parameter)
        {
            case int intParameter:
                value = intParameter;
                return true;
            case string stringParameter:
                return int.TryParse(stringParameter, NumberStyles.Integer, culture, out value);
            default:
                value = 0;
                return false;
        }
    }
}
