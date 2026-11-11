namespace OllamaRouter.Client.Process;

/// <summary>
/// Base exception of the <see cref="OllamaRouterDaemonLauncher"/> failure family (task #99).
/// </summary>
public class RouterDaemonException : Exception
{
    /// <summary>Creates the exception with the given message.</summary>
    public RouterDaemonException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with the given message and inner exception.</summary>
    public RouterDaemonException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when the daemon port is occupied by a NON-router service (e.g. an unmigrated raw
/// Ollama daemon on <c>:11434</c>) and the launcher must not launch against or kill it.
/// Carries the probe <see cref="Status"/> (detail + observed state).
/// </summary>
public sealed class RouterDaemonPortOccupiedException : RouterDaemonException
{
    /// <summary>Creates the exception from the probe status that classified the port.</summary>
    public RouterDaemonPortOccupiedException(RouterDaemonStatus status)
        : base(
            $"The daemon port is occupied by a non-router service; refusing to launch against or kill it "
            + $"({status.Detail ?? "port occupied"})")
    {
        Status = status;
    }

    /// <summary>The probe status that classified the port as foreign-owned.</summary>
    public RouterDaemonStatus Status { get; }
}

/// <summary>
/// Thrown by <see cref="OllamaRouterDaemonLauncher.StartAsync"/> when no daemon executable
/// can be located in ANY of the probed search roots. Carries the FULL list of probed paths.
/// </summary>
public sealed class RouterDaemonExecutableNotFoundException : RouterDaemonException
{
    /// <summary>Creates the exception with the full list of probed locations.</summary>
    public RouterDaemonExecutableNotFoundException(IReadOnlyList<string> searchedLocations)
        : base("No OllamaRouter daemon executable found. Searched: " + string.Join(", ", searchedLocations))
    {
        SearchedLocations = searchedLocations;
    }

    /// <summary>Every candidate path that was probed (all names × all roots).</summary>
    public IReadOnlyList<string> SearchedLocations { get; }
}

/// <summary>
/// Thrown when the launched daemon never reports a running identity probe within the start
/// timeout. Carries the timeout budget and the path of the launched executable (the child
/// was killed before the throw).
/// </summary>
public sealed class RouterDaemonStartTimeoutException : RouterDaemonException
{
    /// <summary>Creates the exception with the timeout and the launched executable path.</summary>
    public RouterDaemonStartTimeoutException(TimeSpan timeout, string executablePath)
        : base($"The OllamaRouter daemon ({executablePath}) did not report a running identity probe within {timeout.TotalSeconds:0.##} s.")
    {
        Timeout = timeout;
        ExecutablePath = executablePath;
    }

    /// <summary>The start-timeout budget that elapsed.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>The path of the launched (and killed) executable.</summary>
    public string ExecutablePath { get; }
}

/// <summary>
/// Thrown when the kill fallback of <see cref="OllamaRouterDaemonLauncher.StopAsync"/> fails
/// (e.g. access denied) and the tracked process is still alive. Carries the process id.
/// </summary>
public sealed class RouterDaemonStopFailedException : RouterDaemonException
{
    /// <summary>Creates the exception with the still-alive process id.</summary>
    public RouterDaemonStopFailedException(int processId)
        : base($"Failed to stop the OllamaRouter daemon (process {processId}): the kill fallback did not terminate it.")
    {
        ProcessId = processId;
    }

    /// <summary>The operating-system process id of the still-alive daemon.</summary>
    public int ProcessId { get; }
}
