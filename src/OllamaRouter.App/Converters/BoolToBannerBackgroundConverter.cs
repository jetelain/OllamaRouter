using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace OllamaRouter.App.Converters;

/// <summary>
/// Maps a boolean error flag to a NightCodeForge status banner background brush
/// (<c>SemiColorDangerLight</c> for error, <c>SemiColorSuccessLight</c> otherwise).
/// </summary>
public sealed class BoolToBannerBackgroundConverter : IValueConverter
{
    public static readonly BoolToBannerBackgroundConverter Instance = new();

    private static readonly IBrush OkBackground = new ImmutableSolidColorBrush(Color.Parse("#2610B981"));
    private static readonly IBrush ErrorBackground = new ImmutableSolidColorBrush(Color.Parse("#26EF4444"));

    private BoolToBannerBackgroundConverter()
    {
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? ErrorBackground : OkBackground;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
