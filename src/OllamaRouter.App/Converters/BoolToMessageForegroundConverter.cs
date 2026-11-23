using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace OllamaRouter.App.Converters;

/// <summary>
/// Maps the Step 1 message severity to a foreground brush (task #150, re-colored by task #170
/// onto the NightCodeForge Obsidian & Indigo palette): <c>true</c> (error) → the accent crimson
/// light text <c>#F87171</c> (<c>NightCodeAccentCrimsonLightTextColor</c>);
/// <c>false</c> or any other value → the status success green <c>#10B981</c>
/// (<c>NightCodeStatusSuccessColor</c>). One-way (<see cref="ConvertBack"/> →
/// <see cref="NotSupportedException"/>), bound as:
/// <c>Foreground="{Binding MessageIsError, Converter={x:Static BoolToMessageForegroundConverter.Instance}}"</c>.
/// </summary>
public sealed class BoolToMessageForegroundConverter : IValueConverter
{
    /// <summary>The shared instance (XAML <c>{x:Static}</c>).</summary>
    public static readonly BoolToMessageForegroundConverter Instance = new();

    // IMMUTABLE brushes: the statics are shared process-wide (converter + headless layout tests
    // run on different threads), so they must not capture a thread owner.
    private static readonly IBrush OkBrush = new ImmutableSolidColorBrush(Color.Parse("#10B981"));
    private static readonly IBrush ErrorBrush = new ImmutableSolidColorBrush(Color.Parse("#F87171"));

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
