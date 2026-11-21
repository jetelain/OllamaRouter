using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace OllamaRouter.App.Converters;

/// <summary>
/// Highlights the active preset chip of the "Add External Provider" dialog (task #149):
/// the bound value is the form's <c>ActivePresetId</c> (null = custom/none) and the
/// <c>ConverterParameter</c> is THIS chip's preset id — an exact (case-insensitive) match
/// paints the chip with a soft accent, anything else stays transparent (the themed default
/// shows through). One-way only: <see cref="ConvertBack"/> throws, per the repo convention
/// (Avalonia 12 has no <c>Binding.DoNothing</c>).
/// </summary>
public sealed class PresetChipConverter : IValueConverter
{
    /// <summary>The shared instance (XAML <c>{x:Static}</c>).</summary>
    public static readonly PresetChipConverter Instance = new();

    // IMMUTABLE brushes: the statics are shared process-wide (converter + layout tests
    // run on different threads), so they must not capture a thread owner — the
    // TargetVisualConverters convention.
    private static readonly IBrush SelectedBrush = new ImmutableSolidColorBrush(Color.FromArgb(90, 90, 160, 255));
    private static readonly IBrush UnselectedBrush = new ImmutableSolidColorBrush(Colors.Transparent);

    private PresetChipConverter()
    {
    }

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string activeId &&
        parameter is string chipId &&
        string.Equals(activeId, chipId, StringComparison.OrdinalIgnoreCase)
            ? SelectedBrush
            : UnselectedBrush;

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
