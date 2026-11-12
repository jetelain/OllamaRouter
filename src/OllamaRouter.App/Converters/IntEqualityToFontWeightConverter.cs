using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace OllamaRouter.App.Converters;

/// <summary>
/// Maps an <see cref="int"/> value equal to the parameter to <see cref="FontWeight.Bold"/> and
/// everything else to <see cref="FontWeight.Normal"/> (task #103) — the "active step" styling of
/// the wizard progress header (all four step titles stay visible; the active one is bold):
/// <c>FontWeight="{Binding SelectedStep, Converter={x:Static IntEqualityToFontWeightConverter.Instance}, ConverterParameter=2}"</c>.
/// </summary>
public sealed class IntEqualityToFontWeightConverter : IValueConverter
{
    /// <summary>The shared instance (XAML <c>{x:Static}</c>).</summary>
    public static readonly IntEqualityToFontWeightConverter Instance = new();

    private IntEqualityToFontWeightConverter()
    {
    }

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int valueInt
        && IntEqualityConverterSupport.TryGetIntParameter(parameter, culture, out var parameterInt)
        && valueInt == parameterInt
            ? FontWeight.Bold
            : FontWeight.Normal;

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
