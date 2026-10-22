using System.Globalization;
using System.Text;
using Microsoft.Extensions.Primitives;
using OllamaRouter.Endpoints;

namespace OllamaRouter.Services;

/// <summary>
/// Renders a lightweight, flicker-free Terminal UI (TUI) dashboard displaying the same
/// monitoring information as the /monitor web page, but without actions (no interactive toggles
/// or buttons) and without token estimation overhead recommendations.
/// </summary>
public static class TerminalMonitorRenderer
{
    // ANSI color definitions
    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";
    private const string Underline = "\x1b[4m";

    private const string BrightCyan = "\x1b[96m";
    private const string BrightGreen = "\x1b[92m";
    private const string BrightYellow = "\x1b[93m";
    private const string BrightRed = "\x1b[91m";
    private const string BrightMagenta = "\x1b[95m";
    private const string Gray = "\x1b[90m";
    private const string Orange = "\x1b[38;5;208m";
    private const string ClearLine = "\x1b[K";

    public static string Render(
        MonitorStateResponse state,
        string monitorUrl,
        int consoleWidth = 80,
        int consoleHeight = 24,
        bool enableAnsi = true)
    {
        var width = Math.Clamp(consoleWidth > 0 ? consoleWidth : 80, 60, 120);
        var height = Math.Clamp(consoleHeight > 0 ? consoleHeight : 24, 15, 60);
        var addSpacing = height > 40;
        var lines = new List<string>(height + 5);

        // Header Banner (link is in footer to avoid duplicate)
        AppendHeader(lines, width, enableAnsi);

        // Instances Status
        AppendInstances(lines, state, enableAnsi);

        // Statistics Table
        if (height > 30)
        {
            if (addSpacing)
            {
                lines.Add(string.Empty);
            }
            AppendStatistics(lines, state, enableAnsi);
        }

        // Usage Ratio Progress Bar
        if (addSpacing)
        {
            lines.Add(string.Empty);
        }
        AppendUsageRatio(lines, state, width, enableAnsi);

        // Cost & Savings (if configured)
        if (state.Cost != null)
        {
            if (addSpacing)
            {
                lines.Add(string.Empty);
            }
            AppendCostAndSavings(lines, state, enableAnsi);
        }

        if (addSpacing)
        {
            lines.Add(string.Empty);
        }
        // In Progress Requests
        AppendInProgress(lines, state, enableAnsi);

        if (addSpacing)
        {
            lines.Add(string.Empty);
        }
        // Recent Requests: calculate available rows so total output fits strictly within height - 2
        var usedLines = lines.Count + 6; // including recent header and footer
        var maxRecentItems = Math.Max(1, height - usedLines);
        AppendRecentRequests(lines, state, maxRecentItems, enableAnsi);

        // Footer (contains single, full-featured monitor link)
        AppendFooter(lines, monitorUrl, width, enableAnsi);

        var sb = new StringBuilder(lines.Count * 80);
        foreach (var line in lines)
        {
            sb.Append(line);
            if (enableAnsi) sb.Append(ClearLine);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void AppendHeader(List<string> lines, int width, bool enableAnsi)
    {
        int boxInnerWidth = 76;
        var bar = new string('═', boxInnerWidth);

        lines.Add($" ╔{bar}╗");

        var titleSb = new StringBuilder();
        titleSb.Append(" ║");
        if (enableAnsi) titleSb.Append(Bold).Append(BrightCyan);
        titleSb.Append(PadRightDisplay("🔀 Ollama Router - Activity Monitor", boxInnerWidth));
        if (enableAnsi) titleSb.Append(Reset);
        titleSb.Append("║");
        lines.Add(titleSb.ToString());

        lines.Add($" ╚{bar}╝");
    }

    private static void AppendInstances(List<string> lines, MonitorStateResponse state, bool enableAnsi)
    {
        var sb = new StringBuilder();
        sb.Append(" ");
        if (enableAnsi) sb.Append(Bold);
        sb.Append("INSTANCES: ");
        if (enableAnsi) sb.Append(Reset);

        var first = true;
        foreach (var (name, enabled) in state.Targets)
        {
            if (!first)
            {
                sb.Append("   ");
            }
            first = false;
            AppendInstanceStatus(sb, name, TargetLabel(name), enabled, state.Online.GetValueOrDefault(name, true), state.Busy.GetValueOrDefault(name), enableAnsi);
        }

        lines.Add(sb.ToString());
    }

    // Display label (icon + name) with Local/Remote/Cloud icons and a plain-name fallback for
    // fleet targets.
    public static string TargetLabel(string target)
    {
        return target.ToLowerInvariant() switch
        {
            "local" => "🖥️ Local",
            "remote" => "📡 Remote",
            "cloud" => "☁️ Cloud",
            _ => target
        };
    }

    private static string TargetColor(string targetKey)
    {
        return targetKey.ToLowerInvariant() switch
        {
            "local" => BrightCyan,
            "remote" => BrightGreen,
            "cloud" => BrightMagenta,
            _ => ""
        };
    }

    private static void AppendInstanceStatus(
        StringBuilder sb,
        string targetKey,
        string displayName,
        bool enabled,
        bool online,
        bool busy,
        bool enableAnsi)
    {
        var targetColor = TargetColor(targetKey);

        if (enableAnsi) sb.Append(Bold).Append(targetColor);
        sb.Append(displayName).Append(": ");
        if (enableAnsi) sb.Append(Reset);

        if (!enabled)
        {
            if (enableAnsi) sb.Append(BrightRed);
            sb.Append("● Disabled");
        }
        else if (!online)
        {
            if (enableAnsi) sb.Append(Orange);
            sb.Append("● Offline");
        }
        else if (busy)
        {
            if (enableAnsi) sb.Append(BrightYellow);
            sb.Append("● Busy");
        }
        else
        {
            if (enableAnsi) sb.Append(BrightGreen);
            sb.Append("● Idle");
        }

        if (enableAnsi) sb.Append(Reset);
    }

    private static void AppendStatistics(List<string> lines, MonitorStateResponse state, bool enableAnsi)
    {
        var header = new StringBuilder();
        header.Append(" ");
        if (enableAnsi) header.Append(Bold);
        header.Append("STATISTICS");
        if (enableAnsi) header.Append(Reset);
        lines.Add(header.ToString());

        lines.Add("   Target       │          Today           │       Last 7 days        ");
        lines.Add("                │    Req       In      Out │    Req       In      Out ");
        lines.Add("   ─────────────┼──────────────────────────┼──────────────────────────");

        var today = state.Statistics.Today;
        var last7Days = state.Statistics.Last7Days;

        foreach (var name in today.Keys.Where(key => key != "total"))
        {
            lines.Add(FormatStatsRow(TargetLabel(name), TargetColor(name), today[name], last7Days.GetValueOrDefault(name, new ActivityStatisticsTotals(0, 0, 0)), enableAnsi));
        }

        lines.Add("   ─────────────┼──────────────────────────┼──────────────────────────");
        lines.Add(FormatStatsRow("Total", Bold, today["total"], last7Days["total"], enableAnsi, isTotal: true));
    }

    private static string FormatStatsRow(
        string label,
        string labelColor,
        ActivityStatisticsTotals today,
        ActivityStatisticsTotals last7,
        bool enableAnsi,
        bool isTotal = false)
    {
        var sb = new StringBuilder();
        sb.Append("   ");
        if (enableAnsi) sb.Append(labelColor);
        sb.Append(PadRightDisplay(label, 13));
        if (enableAnsi) sb.Append(Reset);
        sb.Append("│ ");

        sb.Append(FormatNumber(today.Requests).PadLeft(6)).Append(' ');
        sb.Append(FormatTokens(today.InputTokens).PadLeft(8)).Append(' ');
        sb.Append(FormatTokens(today.OutputTokens).PadLeft(8)).Append(" │ ");

        sb.Append(FormatNumber(last7.Requests).PadLeft(6)).Append(' ');
        sb.Append(FormatTokens(last7.InputTokens).PadLeft(8)).Append(' ');
        sb.Append(FormatTokens(last7.OutputTokens).PadLeft(8)).Append(' ');

        return sb.ToString();
    }

    private static void AppendUsageRatio(List<string> lines, MonitorStateResponse state, int width, bool enableAnsi)
    {
        var last7 = state.Statistics.Last7Days;
        var entries = last7.Keys
            .Where(name => name != "total")
            .Select(name => (Name: name, Stats: last7.GetValueOrDefault(name, new ActivityStatisticsTotals(0, 0, 0))))
            .ToList();
        long totalTokens = entries.Sum(e => (long)e.Stats.InputTokens + e.Stats.OutputTokens);

        var header = new StringBuilder();
        header.Append(" ");
        if (enableAnsi) header.Append(Bold);
        header.Append("USAGE RATIO (last 7 days)");
        if (enableAnsi) header.Append(Reset);

        if (totalTokens == 0)
        {
            lines.Add(header.ToString());
            lines.Add("   " + (enableAnsi ? Gray : "") + "No token activity in the last 7 days" + (enableAnsi ? Reset : ""));
            return;
        }

        header.Append(" - Total tokens: ");
        if (enableAnsi) header.Append(Bold);
        header.Append(FormatTokens(totalTokens));
        if (enableAnsi) header.Append(Reset);
        lines.Add(header.ToString());

        int barLength = Math.Clamp(width - 40, 24, 40);
        var barSb = new StringBuilder();
        barSb.Append("   [");

        var legendSb = new StringBuilder();
        legendSb.Append("   ");
        var firstLegend = true;

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var entryTokens = (long)entry.Stats.InputTokens + entry.Stats.OutputTokens;
            int chars = i < entries.Count - 1
                ? (int)Math.Round((double)entryTokens / totalTokens * barLength)
                : Math.Max(0, barLength - entries.Take(i).Sum(prev => (int)Math.Round((double)((long)prev.Stats.InputTokens + prev.Stats.OutputTokens) / totalTokens * barLength)));
            chars = Math.Clamp(chars, 0, barLength);

            if (chars > 0)
            {
                if (enableAnsi) barSb.Append(TargetColor(entry.Name));
                barSb.Append(new string('█', chars));
            }

            var pct = entryTokens * 100.0 / totalTokens;
            if (!firstLegend)
            {
                legendSb.Append(" • ");
            }
            firstLegend = false;
            if (enableAnsi) legendSb.Append(TargetColor(entry.Name));
            legendSb.Append(TargetLabel(entry.Name)).Append(": ").Append(pct.ToString("0.0", CultureInfo.InvariantCulture)).Append("%");
            if (enableAnsi) legendSb.Append(Reset);
        }

        var emptyChars = barLength - CountBarChars(barSb);
        if (emptyChars > 0)
        {
            if (enableAnsi) barSb.Append(Gray);
            barSb.Append(new string('░', emptyChars));
        }
        if (enableAnsi) barSb.Append(Reset);
        barSb.Append(']');

        lines.Add(barSb.ToString());
        lines.Add(legendSb.ToString());
    }

    // Counts the filled '█' characters appended to the bar so far (the ANSI codes are not counted).
    private static int CountBarChars(StringBuilder barSb)
    {
        var text = barSb.ToString();
        int count = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '█')
            {
                count++;
            }
            else if (text[i] == '\u001b')
            {
                // Skip ANSI escape sequences.
                while (i < text.Length && text[i] != 'm')
                {
                    i++;
                }
            }
        }
        return count;
    }

    private static void AppendCostAndSavings(List<string> lines, MonitorStateResponse state, bool enableAnsi)
    {
        if (state.Cost == null) return;

        var sb = new StringBuilder();
        sb.Append(" ");
        if (enableAnsi) sb.Append(Bold);
        sb.Append("COST & SAVINGS (last 7 days): ");
        if (enableAnsi) sb.Append(Reset);

        sb.Append("💰 Savings: ~");
        if (enableAnsi) sb.Append(Bold).Append(BrightGreen);
        sb.Append(FormatCost(state.Cost.EstimatedSavings, state.Cost.Currency));
        if (enableAnsi) sb.Append(Reset);

        if (state.CloudEnabled)
        {
            sb.Append("   ☁️ Cloud: ~");
            if (enableAnsi) sb.Append(Bold).Append(BrightMagenta);
            sb.Append(FormatCost(state.Cost.CloudOverflowCost, state.Cost.Currency));
            if (enableAnsi) sb.Append(Reset);
        }

        lines.Add(sb.ToString());
    }

    private static void AppendInProgress(List<string> lines, MonitorStateResponse state, bool enableAnsi)
    {
        var header = new StringBuilder();
        header.Append(" ");
        if (enableAnsi) header.Append(Bold);
        header.Append("IN PROGRESS");
        if (enableAnsi) header.Append(Reset);
        lines.Add(header.ToString());

        if (state.InProgress.Count == 0)
        {
            lines.Add("   " + (enableAnsi ? Gray : "") + "No request in progress" + (enableAnsi ? Reset : ""));
            return;
        }

        lines.Add("   Target      Model                                      In (est.) Running for");
        lines.Add("   ─────────── ──────────────────────────────────────── ─────────── ───────────");

        foreach (var req in state.InProgress)
        {
            var sb = new StringBuilder();
            sb.Append("   ");
            AppendTargetBadge(sb, req.Target, enableAnsi, 11);
            sb.Append(' ');
            sb.Append(PadRightDisplay(Truncate(req.Model, 40), 40));
            sb.Append(' ');
            if (enableAnsi) sb.Append(BrightYellow);
            sb.Append(FormatTokens(req.EstimatedPromptTokens).PadLeft(11));
            sb.Append(' ');
            var seconds = (req.ElapsedMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s";
            sb.Append(seconds.PadLeft(11));
            if (enableAnsi) sb.Append(Reset);
            lines.Add(sb.ToString());
        }
    }

    private static void AppendRecentRequests(List<string> lines, MonitorStateResponse state, int maxCount, bool enableAnsi)
    {
        var header = new StringBuilder();
        header.Append(" ");
        if (enableAnsi) header.Append(Bold);
        header.Append("RECENT REQUESTS");
        if (enableAnsi) header.Append(Reset);
        lines.Add(header.ToString());

        if (state.Requests.Count == 0)
        {
            lines.Add("   " + (enableAnsi ? Gray : "") + "No recent requests" + (enableAnsi ? Reset : ""));
            return;
        }

        lines.Add("   Time     Target      Model               In (est/act)    Out  Elapsed Status");
        lines.Add("   ──────── ─────────── ───────────────── ────────────── ────── ──────── ──────");

        var items = state.Requests.Take(maxCount);
        foreach (var req in items)
        {
            var isFail = !req.Success || (req.StatusCode.HasValue && req.StatusCode.Value >= 400);
            var sb = new StringBuilder();

            sb.Append("   ");
            if (enableAnsi && isFail) sb.Append(BrightRed);

            var timeStr = req.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            sb.Append(timeStr.PadRight(8)).Append(' ');

            AppendTargetBadge(sb, req.Target, enableAnsi, 11);
            sb.Append(' ');

            sb.Append(PadRightDisplay(Truncate(req.Model, 17), 17)).Append(' ');

            var estTokens = FormatTokens(req.EstimatedPromptTokens);
            var actTokens = req.ActualPromptTokens.HasValue ? FormatTokens(req.ActualPromptTokens.Value) : "-";
            var inStr = $"{estTokens} / {actTokens}";
            sb.Append(inStr.PadLeft(14)).Append(' ');

            var outStr = req.ActualResponseTokens.HasValue ? FormatTokens(req.ActualResponseTokens.Value) : "-";
            sb.Append(outStr.PadLeft(6)).Append(' ');

            var elapsedStr = (req.ElapsedMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s";
            sb.Append(elapsedStr.PadLeft(8)).Append(' ');

            var statusStr = req.StatusCode.HasValue ? req.StatusCode.Value.ToString(CultureInfo.InvariantCulture) : "-";
            if (enableAnsi && isFail)
            {
                sb.Append(BrightRed).Append(statusStr.PadLeft(6)).Append(Reset);
            }
            else
            {
                sb.Append(statusStr.PadLeft(6));
            }

            if (enableAnsi && isFail) sb.Append(Reset);
            lines.Add(sb.ToString());
        }
    }

    private static void AppendFooter(List<string> lines, string monitorUrl, int width, bool enableAnsi)
    {
        lines.Add("   " + new string('─', 76));

        var sb = new StringBuilder();
        var prefix = "   Full monitor: ";
        var suffix = " (2s) • Ctrl+C to quit";
        int availableUrlWidth = Math.Max(10, 79 - prefix.Length - suffix.Length);
        var displayUrl = monitorUrl.Length <= availableUrlWidth
            ? monitorUrl
            : monitorUrl[..(availableUrlWidth - 1)] + "…";

        sb.Append(prefix);
        if (enableAnsi)
        {
            sb.Append("\x1b]8;;").Append(monitorUrl).Append("\x1b\\")
              .Append(BrightCyan).Append(displayUrl).Append(Reset)
              .Append("\x1b]8;;\x1b\\");
        }
        else
        {
            sb.Append(displayUrl);
        }
        sb.Append(suffix);
        lines.Add(sb.ToString());
    }

    private static void AppendTargetBadge(StringBuilder sb, string target, bool enableAnsi, int padLength)
    {
        var color = TargetColor(target);
        var icon = TargetLabel(target);

        if (enableAnsi) sb.Append(color);
        sb.Append(PadRightDisplay(icon, padLength));
        if (enableAnsi) sb.Append(Reset);
    }

    public static int GetDisplayWidth(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int width = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\x1b' && i + 1 < text.Length)
            {
                char next = text[i + 1];
                if (next == ']') // OSC sequence (e.g. \x1b]8;;URL\x1b\)
                {
                    i += 2;
                    while (i < text.Length)
                    {
                        if (text[i] == '\x07')
                        {
                            break;
                        }
                        if (text[i] == '\x1b' && i + 1 < text.Length && text[i + 1] == '\\')
                        {
                            i++;
                            break;
                        }
                        i++;
                    }
                    continue;
                }
                if (next == '[') // CSI sequence (e.g. \x1b[96m, \x1b[K)
                {
                    i += 2;
                    while (i < text.Length && !char.IsLetter(text[i]))
                    {
                        i++;
                    }
                    continue;
                }
            }

            // Zero-width characters (variation selector-16, zero-width joiner)
            if (c == '\uFE0F' || c == '\u200D')
            {
                continue;
            }

            // Surrogate pairs
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                int codePoint = char.ConvertToUtf32(text, i);
                i++;
                width += (codePoint >= 0x1F000 && codePoint <= 0x1FFFF) ||
                         (codePoint >= 0x20000 && codePoint <= 0x2FFFF) ? 2 : 1;
                continue;
            }

            // BMP wide characters / symbols
            if (c == '☁' || (c >= 0x2E80 && c <= 0x9FFF))
            {
                width += 2;
            }
            else
            {
                width += 1;
            }
        }

        return width;
    }

    public static string PadRightDisplay(string text, int targetWidth)
    {
        int currentWidth = GetDisplayWidth(text);
        if (currentWidth >= targetWidth) return text;
        return text + new string(' ', targetWidth - currentWidth);
    }

    public static string FormatNumber(long value)
    {
        if (value == 0) return "0";
        return value.ToString("N0", CultureInfo.InvariantCulture).Replace(",", " ");
    }

    public static string FormatTokens(long? value)
    {
        if (value == null) return "-";
        if (value == 0) return "0";
        if (value >= 1_000_000)
        {
            return (value.Value / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M";
        }
        if (value >= 1_000)
        {
            return (value.Value / 1_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k";
        }
        return value.Value.ToString(CultureInfo.InvariantCulture);
    }

    public static string FormatTokens(int? value) => FormatTokens((long?)value);

    public static string FormatCost(double amount, string currency)
    {
        var curr = string.IsNullOrEmpty(currency) ? "$" : currency;
        if (amount == 0.0) return curr + "0.00";
        if (amount < 0.01) return curr + amount.ToString("0.0000", CultureInfo.InvariantCulture);
        return curr + amount.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
        int cut = maxLength - 1;
        if (cut > 0 && char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }
        return value[..cut] + "…";
    }
}
