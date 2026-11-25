using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace OllamaRouter.App.Converters;

/// <summary>
/// Maps target liveness strings to semantic NightCodeForge foreground brushes.
/// </summary>
public sealed class LivenessToBrushConverter : IValueConverter
{
    public static readonly LivenessToBrushConverter Instance = new();

    private static readonly IBrush OnlineBrush = new ImmutableSolidColorBrush(Color.Parse("#10B981"));
    private static readonly IBrush OfflineBrush = new ImmutableSolidColorBrush(Color.Parse("#F87171"));
    private static readonly IBrush OtherBrush = new ImmutableSolidColorBrush(Color.Parse("#F59E0B"));

    private LivenessToBrushConverter()
    {
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s)
        {
            return OtherBrush;
        }

        if (s.Equals("Online", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Healthy", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Ready", StringComparison.OrdinalIgnoreCase))
        {
            return OnlineBrush;
        }

        if (s.Equals("Offline", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Unhealthy", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Down", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Error", StringComparison.OrdinalIgnoreCase))
        {
            return OfflineBrush;
        }

        return OtherBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
