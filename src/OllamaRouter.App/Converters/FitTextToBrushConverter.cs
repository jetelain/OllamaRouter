using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace OllamaRouter.App.Converters;

/// <summary>
/// Maps pre-flight candidate fit verdict text to semantic NightCodeForge foreground brushes.
/// </summary>
public sealed class FitTextToBrushConverter : IValueConverter
{
    public static readonly FitTextToBrushConverter Instance = new();

    private static readonly IBrush FitsBrush = new ImmutableSolidColorBrush(Color.Parse("#10B981"));
    private static readonly IBrush PrunedBrush = new ImmutableSolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush AssumedBrush = new ImmutableSolidColorBrush(Color.Parse("#94A3B8"));

    private FitTextToBrushConverter()
    {
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s)
        {
            if (s.StartsWith("ctx", StringComparison.OrdinalIgnoreCase))
            {
                return FitsBrush;
            }

            if (s.StartsWith("won't fit", StringComparison.OrdinalIgnoreCase))
            {
                return PrunedBrush;
            }
        }

        return AssumedBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
