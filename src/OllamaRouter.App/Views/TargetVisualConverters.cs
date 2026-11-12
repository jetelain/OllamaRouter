using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace OllamaRouter.App.Views;

/// <summary>
/// Registry and visual converters for target locations, preserving the emojis and
/// color conventions from the OllamaRouter monitoring web page:
/// 🖥️ Local (#3F9EFF)
/// 📡 Remote (#34E0A1)
/// ☁️ Cloud (#C48BFF)
/// </summary>
public static class TargetLocationVisuals
{
    public static readonly IBrush LocalBrush = new ImmutableSolidColorBrush(Color.Parse("#3F9EFF"));
    public static readonly IBrush RemoteBrush = new ImmutableSolidColorBrush(Color.Parse("#34E0A1"));
    public static readonly IBrush CloudBrush = new ImmutableSolidColorBrush(Color.Parse("#C48BFF"));
    public static readonly IBrush NeutralBrush = new ImmutableSolidColorBrush(Color.Parse("#7A8AA0"));

    private static readonly ConcurrentDictionary<string, string> s_locations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Local"] = "local",
        ["Cloud"] = "cloud"
    };

    public static void Register(string? name, string? location)
    {
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(location))
        {
            s_locations[name.Trim()] = location.Trim().ToLowerInvariant();
        }
    }

    public static string ClassifyLocation(string? nameOrLocation)
    {
        if (string.IsNullOrWhiteSpace(nameOrLocation))
        {
            return "neutral";
        }

        var trimmed = nameOrLocation.Trim();
        var lower = trimmed.ToLowerInvariant();

        if (lower is "local" or "remote" or "cloud")
        {
            return lower;
        }

        if (s_locations.TryGetValue(trimmed, out var registered))
        {
            return registered;
        }

        if (lower.Contains("local") || lower.Contains("127.0.0.1") || lower.Contains("localhost"))
        {
            return "local";
        }

        if (lower.Contains("cloud") || lower.Contains("openai") || lower.Contains("anthropic") || lower.Contains("groq"))
        {
            return "cloud";
        }

        return "remote";
    }

    public static string GetEmoji(string? nameOrLocation)
    {
        return ClassifyLocation(nameOrLocation) switch
        {
            "local" => "\uD83D\uDDA5\uFE0F", // 🖥️
            "remote" => "\uD83D\uDCE1",        // 📡
            "cloud" => "\u2601\uFE0F",         // ☁️
            _ => string.Empty
        };
    }

    public static IBrush GetBrush(string? nameOrLocation)
    {
        return ClassifyLocation(nameOrLocation) switch
        {
            "local" => LocalBrush,
            "remote" => RemoteBrush,
            "cloud" => CloudBrush,
            _ => NeutralBrush
        };
    }

    public static string FormatLabel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var emoji = GetEmoji(name);
        return string.IsNullOrEmpty(emoji) ? name : $"{emoji} {name}";
    }
}

/// <summary>
/// Converts a target name or location into its branded emoji-prefixed label (e.g. "🖥️ Local").
/// </summary>
public sealed class TargetLabelConverter : IValueConverter
{
    public static readonly TargetLabelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string str ? TargetLocationVisuals.FormatLabel(str) : value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Converts a target name or location into its branded brush (#3F9EFF, #34E0A1, #C48BFF).
/// </summary>
public sealed class TargetBrushConverter : IValueConverter
{
    public static readonly TargetBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string str
            ? TargetLocationVisuals.GetBrush(str)
            : TargetLocationVisuals.NeutralBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Extracts just the emoji for the target ("🖥️", "📡", "☁️"), rendered in Segoe UI Emoji.
/// </summary>
public sealed class TargetEmojiConverter : IValueConverter
{
    public static readonly TargetEmojiConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string str ? TargetLocationVisuals.GetEmoji(str) : string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Strips any leading emoji from the target name so it can be cleanly styled with color.
/// </summary>
public sealed class TargetNameConverter : IValueConverter
{
    public static readonly TargetNameConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string str || string.IsNullOrWhiteSpace(str))
            return string.Empty;

        var trimmed = str.Trim();
        if (trimmed.StartsWith("\uD83D\uDDA5\uFE0F") || trimmed.StartsWith("🖥️") ||
            trimmed.StartsWith("\uD83D\uDCE1") || trimmed.StartsWith("📡") ||
            trimmed.StartsWith("\u2601\uFE0F") || trimmed.StartsWith("☁️"))
        {
            var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 ? parts[1] : trimmed;
        }

        return trimmed;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

