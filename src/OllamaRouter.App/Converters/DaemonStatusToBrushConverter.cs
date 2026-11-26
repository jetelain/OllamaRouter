using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using OllamaRouter.App.ViewModels;

namespace OllamaRouter.App.Converters;

/// <summary>
/// Maps the connection-bar <see cref="DaemonStatus"/> (task #173) to the status-dot brush:
/// green for Running/Remote, amber for Connecting/Port Conflict, crimson for
/// Stopped/Disconnected (the NightCodeForge semantic token values).
/// </summary>
public sealed class DaemonStatusToBrushConverter : IValueConverter
{
    public static readonly DaemonStatusToBrushConverter Instance = new();

    // NightCodeForge semantic tokens (values per Themes/NightCodeForgeTheme.axaml):
    // Success #10B981 · Warning #F59E0B · Danger (crimson) #EF4444.
    private static readonly IBrush GreenBrush = new ImmutableSolidColorBrush(Color.Parse("#10B981"));
    private static readonly IBrush AmberBrush = new ImmutableSolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush CrimsonBrush = new ImmutableSolidColorBrush(Color.Parse("#EF4444"));

    private DaemonStatusToBrushConverter()
    {
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DaemonStatus status ? status switch
        {
            DaemonStatus.Running or DaemonStatus.Remote => GreenBrush,
            DaemonStatus.Connecting or DaemonStatus.PortConflict => AmberBrush,
            _ => CrimsonBrush, // Stopped / Disconnected
        } : CrimsonBrush;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
