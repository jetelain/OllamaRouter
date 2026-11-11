using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace OllamaRouter.App.Views;

/// <summary>
/// Maps the matrix cell's "present" flag to a background brush (present = a visible accent,
/// absent = transparent) for a glanceable model × target matrix.
/// </summary>
public sealed class CellBackgroundConverter : IValueConverter
{
    /// <summary>The shared instance (XAML <c>{x:Static}</c>).</summary>
    public static readonly CellBackgroundConverter Instance = new();

    private static readonly IBrush PresentBrush = new SolidColorBrush(Color.FromArgb(80, 90, 160, 255));

    private CellBackgroundConverter()
    {
    }

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? PresentBrush : null;

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
