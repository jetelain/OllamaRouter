using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace OllamaRouter.App.Converters;

/// <summary>
/// Maps the Step 1 message severity to a foreground brush (task #150): <c>true</c> (error) →
/// soft red <c>#F2A0A0</c>; <c>false</c> or any other value → the existing green
/// <c>#8BC34A</c>. One-way (<see cref="ConvertBack"/> → <see cref="NotSupportedException"/>),
/// bound as:
/// <c>Foreground="{Binding MessageIsError, Converter={x:Static BoolToMessageForegroundConverter.Instance}}"</c>.
/// </summary>
public sealed class BoolToMessageForegroundConverter : IValueConverter
{
    /// <summary>The shared instance (XAML <c>{x:Static}</c>).</summary>
    public static readonly BoolToMessageForegroundConverter Instance = new();

    private static readonly IBrush OkBrush = new SolidColorBrush(Color.Parse("#8BC34A"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#F2A0A0"));

    private BoolToMessageForegroundConverter()
    {
    }

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? ErrorBrush : OkBrush;

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
