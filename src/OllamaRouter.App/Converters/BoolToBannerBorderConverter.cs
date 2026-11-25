using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace OllamaRouter.App.Converters;

/// <summary>
/// Maps a boolean error flag to a NightCodeForge status banner border brush
/// (<c>SemiColorDanger</c> for error, <c>SemiColorSuccess</c> otherwise).
/// </summary>
public sealed class BoolToBannerBorderConverter : IValueConverter
{
    public static readonly BoolToBannerBorderConverter Instance = new();

    private static readonly IBrush OkBorder = new ImmutableSolidColorBrush(Color.Parse("#10B981"));
    private static readonly IBrush ErrorBorder = new ImmutableSolidColorBrush(Color.Parse("#EF4444"));

    private BoolToBannerBorderConverter()
    {
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? ErrorBorder : OkBorder;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
