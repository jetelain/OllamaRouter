using System.Globalization;
using Avalonia.Data.Converters;

namespace OllamaRouter.App.Views;

public static class HumanReadableFormatters
{
    public static string FormatTokens(long? tokens)
    {
        if (!tokens.HasValue) return "-";
        var val = tokens.Value;
        if (val < 0) return "-" + FormatTokens(-val);
        if (val >= 1_000_000_000) return (val / 1_000_000_000.0).ToString("0.##", CultureInfo.InvariantCulture) + "B";
        if (val >= 1_000_000) return (val / 1_000_000.0).ToString("0.##", CultureInfo.InvariantCulture) + "M";
        if (val >= 1_000) return (val / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        return val.ToString(CultureInfo.InvariantCulture);
    }

    public static string FormatBytes(long? bytes)
    {
        if (!bytes.HasValue) return "-";
        var val = bytes.Value;
        if (val < 0) return "-" + FormatBytes(-val);
        if (val >= 1024L * 1024 * 1024 * 1024) return (val / (1024.0 * 1024 * 1024 * 1024)).ToString("0.##", CultureInfo.InvariantCulture) + " TB";
        if (val >= 1024L * 1024 * 1024) return (val / (1024.0 * 1024 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " GB";
        if (val >= 1024L * 1024) return (val / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        if (val >= 1024L) return (val / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        return val + " B";
    }

    public static string FormatContextTokens(int? tokens)
    {
        if (!tokens.HasValue || tokens.Value <= 0) return "-";
        var val = tokens.Value;
        if (val >= 1_000_000) return (val / 1_000_000.0).ToString("0.##", CultureInfo.InvariantCulture) + "M";
        if (val >= 1_000) return (val / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        return val.ToString(CultureInfo.InvariantCulture);
    }

    public static string FormatDurationMs(double? ms)
    {
        if (!ms.HasValue) return "-";
        var val = ms.Value;
        if (val < 0) return "-" + FormatDurationMs(-val);
        if (val < 1000) return $"{val:0} ms";
        if (val < 60_000) return (val / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " s";
        if (val < 3_600_000) return (val / 60_000.0).ToString("0.#", CultureInfo.InvariantCulture) + " min";
        return (val / 3_600_000.0).ToString("0.#", CultureInfo.InvariantCulture) + " h";
    }

    public static string FormatDurationSeconds(double? seconds)
    {
        if (!seconds.HasValue) return "-";
        var val = seconds.Value;
        if (val < 0) return "-" + FormatDurationSeconds(-val);
        if (val < 60) return val.ToString("0.#", CultureInfo.InvariantCulture) + " s";
        if (val < 3600) return (val / 60.0).ToString("0.#", CultureInfo.InvariantCulture) + " min";
        return (val / 3600.0).ToString("0.#", CultureInfo.InvariantCulture) + " h";
    }
}

public sealed class TokenCountConverter : IValueConverter
{
    public static readonly TokenCountConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return "-";
        if (value is long l) return HumanReadableFormatters.FormatTokens(l);
        if (value is int i) return HumanReadableFormatters.FormatTokens(i);
        if (value is double d) return HumanReadableFormatters.FormatTokens((long)d);
        return value.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ByteSizeConverter : IValueConverter
{
    public static readonly ByteSizeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return "-";
        if (value is long l) return HumanReadableFormatters.FormatBytes(l);
        if (value is int i) return HumanReadableFormatters.FormatBytes(i);
        if (value is ulong ul) return HumanReadableFormatters.FormatBytes((long)ul);
        if (value is double d) return HumanReadableFormatters.FormatBytes((long)d);
        return value.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ContextSizeConverter : IValueConverter
{
    public static readonly ContextSizeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return "-";
        if (value is int i) return HumanReadableFormatters.FormatContextTokens(i);
        if (value is long l) return HumanReadableFormatters.FormatContextTokens((int)l);
        if (value is string s && int.TryParse(s, out var parsed)) return HumanReadableFormatters.FormatContextTokens(parsed);
        return value.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class DurationMsConverter : IValueConverter
{
    public static readonly DurationMsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return "-";
        if (value is double d) return HumanReadableFormatters.FormatDurationMs(d);
        if (value is long l) return HumanReadableFormatters.FormatDurationMs(l);
        if (value is int i) return HumanReadableFormatters.FormatDurationMs(i);
        return value.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class DurationSecondsConverter : IValueConverter
{
    public static readonly DurationSecondsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return "-";
        if (value is double d) return HumanReadableFormatters.FormatDurationSeconds(d);
        if (value is long l) return HumanReadableFormatters.FormatDurationSeconds(l);
        if (value is int i) return HumanReadableFormatters.FormatDurationSeconds(i);
        return value.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

