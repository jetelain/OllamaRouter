using System.Globalization;
using Avalonia.Data.Converters;

namespace OllamaRouter.App.Converters;

/// <summary>
/// Negates a <see cref="bool"/> value (task #114): <c>true</c> → <c>false</c> and vice versa,
/// with pass-through for null and non-bool values. Used only for the Monitor tab's usage-ratio
/// empty-state <c>IsVisible</c> so the "No token activity in the last 7 days" text is shown
/// exactly when <c>HasUsageActivity</c> is false:
/// <c>IsVisible="{Binding HasUsageActivity, Converter={x:Static BoolNegateConverter.Instance}}"</c>.
/// </summary>
public sealed class BoolNegateConverter : IValueConverter
{
    /// <summary>The shared instance (XAML <c>{x:Static}</c>).</summary>
    public static readonly BoolNegateConverter Instance = new();

    private BoolNegateConverter()
    {
    }

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : value;

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
