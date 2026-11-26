using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using OllamaRouter.App.Converters;
using OllamaRouter.App.ViewModels;

namespace OllamaRouter.Tests.App;

/// <summary>
/// <see cref="DaemonStatusToBrushConverter"/> unit tests (task #173): every connection-bar
/// state maps to the exact NightCodeForge semantic brush (green #10B981 Running/Remote,
/// amber #F59E0B Connecting/Port Conflict, crimson #EF4444 Stopped/Disconnected), and
/// non-enum/null input falls back to crimson. Plain unit tests — no Avalonia session.
/// </summary>
public sealed class DaemonStatusToBrushConverterTests
{
    private static readonly DaemonStatusToBrushConverter Converter = DaemonStatusToBrushConverter.Instance;

    [Theory]
    [InlineData(DaemonStatus.Running, "#10B981")]
    [InlineData(DaemonStatus.Remote, "#10B981")]
    [InlineData(DaemonStatus.Connecting, "#F59E0B")]
    [InlineData(DaemonStatus.PortConflict, "#F59E0B")]
    [InlineData(DaemonStatus.Stopped, "#EF4444")]
    [InlineData(DaemonStatus.Disconnected, "#EF4444")]
    public void Convert_MapsEveryStatusToItsSemanticBrush(DaemonStatus status, string expectedHex)
    {
        var brush = AssertBrush(Converter.Convert(status, typeof(IBrush), null, CultureInfo.InvariantCulture), expectedHex);
        Assert.True(brush is ImmutableSolidColorBrush,
            "shared static converter brushes must be ImmutableSolidColorBrush (mutable shared brushes corrupt parallel headless sessions)");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-status")]
    [InlineData(42)]
    public void Convert_NonEnumOrNull_FallsBackToCrimson(object? value)
    {
        AssertBrush(Converter.Convert(value, typeof(IBrush), null, CultureInfo.InvariantCulture), "#EF4444");
    }

    [Fact]
    public void ConvertBack_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(() =>
            Converter.ConvertBack(null, typeof(DaemonStatus), null, CultureInfo.InvariantCulture));
    }

    private static ISolidColorBrush AssertBrush(object? value, string expectedHex)
    {
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(value);
        Assert.Equal(Color.Parse(expectedHex), brush.Color);
        return brush;
    }
}
