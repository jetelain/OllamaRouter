using System.Globalization;
using OllamaRouter.Client;
using OllamaRouter.Client.Process;

namespace OllamaRouter.Cli.Commands;

/// <summary>
/// Shared daemon auto-start gate (task #101): runs <see cref="IOllamaRouterDaemonLauncher.EnsureRunningAsync"/>
/// (the SAME gate as <see cref="MonitorCommand"/> — the only start path) before ANY API call of the
/// operational subcommands (fleet/cartography/vram/preload/target/benchmark), and reports the
/// error with the exact MonitorCommand texts so the seven commands stay thin and cannot drift.
/// Also carries the small invariant number/size formatting helpers shared by the command set.
/// </summary>
internal static class DaemonGate
{
    /// <summary>
    /// Auto-starts the daemon. Returns <c>null</c> when the daemon is Running; writes
    /// <c>error: ...</c> to <c>context.Error</c> and returns <c>1</c> on a
    /// <see cref="RouterDaemonException"/> (failed auto-start) or a returned state other than
    /// Running (e.g. <see cref="RouterDaemonState.PortOccupiedByOtherService"/>).
    /// </summary>
    public static async Task<int?> EnsureRunningAsync(CliContext context, CancellationToken ct)
    {
        RouterDaemonStatus status;
        try
        {
            status = await context.Launcher.EnsureRunningAsync(ct);
        }
        catch (RouterDaemonException ex)
        {
            context.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }

        if (status.State != RouterDaemonState.Running)
        {
            context.Error.WriteLine($"error: {status.Detail ?? "the daemon is not running"}");
            return 1;
        }

        return null;
    }

    /// <summary>Round-up integer megabytes + <c>" MB"</c> (invariant; <c>0</c> → <c>"0 MB"</c>).</summary>
    public static string FormatMb(long bytes)
    {
        var mb = bytes <= 0 ? 0L : (bytes + 1024 * 1024 - 1) / (1024 * 1024);
        return mb.ToString(CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>Tokens/second with one decimal (invariant).</summary>
    public static string FormatTps(double tps) =>
        tps.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Keep-alive seconds: <c>-1</c> → <c>indefinite</c>, else <c>&lt;n&gt;s</c>.</summary>
    public static string FormatKeepAlive(int seconds) =>
        seconds < 0 ? "indefinite" : seconds.ToString(CultureInfo.InvariantCulture) + "s";
}
