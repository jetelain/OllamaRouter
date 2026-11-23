using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace OllamaRouter.App.Views;

/// <summary>
/// Maps the matrix cell's "present" flag to a background brush (present = a visible accent,
/// absent = transparent) for a glanceable model × target matrix. Task #170: the accent is the
/// NightCodeForge accent indigo (#6366F1 at ~25% alpha), immutable — the static is shared
/// process-wide and must not capture a thread owner.
/// </summary>
public sealed class CellBackgroundConverter : IValueConverter
{
    /// <summary>The shared instance (XAML <c>{x:Static}</c>).</summary>
    public static readonly CellBackgroundConverter Instance = new();

    private static readonly IBrush PresentBrush = new ImmutableSolidColorBrush(Color.FromArgb(64, 99, 102, 241));

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
