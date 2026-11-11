namespace OllamaRouter.Endpoints.RouterApi.Info;

/// <summary>
/// Process telemetry of the running OllamaRouter daemon (task #99), reported by
/// <c>GET /api/router/info</c> as <c>processId</c> / <c>startedAtUtc</c> / <c>uptimeSeconds</c>.
/// The start instant is captured ONCE at module initialization (type initializer) — for a
/// long-lived single-process server that is the process start. No dependencies, no state
/// other than the one captured timestamp: AOT-safe.
/// </summary>
internal static class RouterProcessTelemetry
{
    /// <summary>The process start instant (captured once at module initialization).</summary>
    private static readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;

    /// <summary>The operating-system process id of the running daemon (<c>Environment.ProcessId</c>).</summary>
    public static int ProcessId => Environment.ProcessId;

    /// <summary>The process start instant (UTC).</summary>
    public static DateTimeOffset StartedAtUtc => _startedAtUtc;

    /// <summary>Seconds since the process start, clamped to a minimum of zero.</summary>
    public static double UptimeSeconds =>
        Math.Max(0.0, (DateTimeOffset.UtcNow - _startedAtUtc).TotalSeconds);
}
