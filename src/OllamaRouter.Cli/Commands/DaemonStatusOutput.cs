using OllamaRouter.Client.Process;

namespace OllamaRouter.Cli.Commands;

/// <summary>
/// Shared human-readable formatting of a <see cref="RouterDaemonStatus"/> probe result (task
/// #100) used by the start/stop/restart/status commands.
/// </summary>
internal static class DaemonStatusOutput
{
    /// <summary>
    /// Prints the daemon status block: state (+ optional detail), version, PID and the bind
    /// address of the configured base URL.
    /// </summary>
    public static void Print(System.IO.TextWriter output, RouterDaemonStatus status, Uri baseUrl)
    {
        output.WriteLine($"state:  {status.State}");
        if (!string.IsNullOrEmpty(status.Detail))
        {
            output.WriteLine($"detail: {status.Detail}");
        }

        if (status.Info is { } info)
        {
            output.WriteLine($"version: {info.Version}");
            output.WriteLine($"pid:    {info.ProcessId}");
            output.WriteLine($"uptime: {FormatUptime(info.UptimeSeconds)}");
        }

        output.WriteLine($"bind:   {baseUrl.ToString().TrimEnd('/')}");
    }

    /// <summary>Formats a second count as a compact <c>1h 2m 3s</c> human uptime string.</summary>
    public static string FormatUptime(double totalSeconds)
    {
        if (totalSeconds < 0)
        {
            totalSeconds = 0;
        }

        var total = (int)Math.Round(totalSeconds);
        var hours = total / 3600;
        var minutes = (total % 3600) / 60;
        var seconds = total % 60;

        if (hours > 0)
        {
            return $"{hours}h {minutes}m {seconds}s";
        }

        return minutes > 0 ? $"{minutes}m {seconds}s" : $"{seconds}s";
    }
}
