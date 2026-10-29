using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OllamaRouter.Endpoints;
using OllamaRouter.Options;
using OllamaRouter.Services.Hardware;

namespace OllamaRouter.Services;

/// <summary>
/// Background service that continuously renders a lightweight Terminal UI activity monitor
/// directly to the console when OllamaRouter is running interactively.
/// </summary>
public sealed class TerminalMonitoringHostedService : BackgroundService
{
    private readonly IActivityMonitorService _activityMonitor;
    private readonly IActivityStatisticsService _activityStatistics;
    private readonly ITargetAvailabilityService _targetAvailability;
    private readonly IModelCatalogCacheService _modelCatalogCache;
    private readonly ITargetLivenessService _targetLiveness;
    private readonly ITargetExecutionTracker _targetExecutionTracker;
    private readonly IContextAutoShrinkManager _contextAutoShrinkManager;
    private readonly IOptions<OllamaRouterOptions> _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IFleetHardwareService? _fleetHardware;

    public TerminalMonitoringHostedService(
        IActivityMonitorService activityMonitor,
        IActivityStatisticsService activityStatistics,
        ITargetAvailabilityService targetAvailability,
        IModelCatalogCacheService modelCatalogCache,
        ITargetLivenessService targetLiveness,
        ITargetExecutionTracker targetExecutionTracker,
        IContextAutoShrinkManager contextAutoShrinkManager,
        IOptions<OllamaRouterOptions> options,
        IHostApplicationLifetime lifetime,
        IFleetHardwareService? fleetHardware = null)
    {
        _activityMonitor = activityMonitor;
        _activityStatistics = activityStatistics;
        _targetAvailability = targetAvailability;
        _modelCatalogCache = modelCatalogCache;
        _targetLiveness = targetLiveness;
        _targetExecutionTracker = targetExecutionTracker;
        _contextAutoShrinkManager = contextAutoShrinkManager;
        _options = options;
        _lifetime = lifetime;
        _fleetHardware = fleetHardware;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = _options.Value.TerminalMonitoring;
        if (!settings.Enabled || Console.IsOutputRedirected)
        {
            return;
        }

        // Wait for the application to be fully started and listening
        var startTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = _lifetime.ApplicationStarted.Register(() => startTcs.TrySetResult());
        using var cancelReg = stoppingToken.Register(() => startTcs.TrySetCanceled());

        try
        {
            await startTcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        ConsoleHelper.TryEnableVirtualTerminalProcessing();

        var enableAnsi = Environment.GetEnvironmentVariable("NO_COLOR") == null;

        void CleanupConsole(object? sender, EventArgs e)
        {
            try
            {
                if (enableAnsi)
                {
                    // Show cursor, reset styling, and exit alternate screen buffer
                    Console.Write("\x1b[?25h\x1b[0m\x1b[?1049l\n");
                }
            }
            catch
            {
                // Ignored on process shutdown
            }
        }

        AppDomain.CurrentDomain.ProcessExit += CleanupConsole;
        try
        {
            Console.CancelKeyPress += (s, e) => CleanupConsole(s, e);
        }
        catch
        {
            // May fail in non-interactive or redirected hosts
        }

        try
        {
            if (enableAnsi)
            {
                // Switch to alternate screen buffer, hide cursor, clear screen and home cursor
                Console.Write("\x1b[?1049h\x1b[?25l\x1b[2J\x1b[H");
            }

            var intervalSeconds = settings.RefreshIntervalSeconds > 0 ? settings.RefreshIntervalSeconds : 2.0;
            var interval = TimeSpan.FromSeconds(intervalSeconds);

            string? lastRendered = null;
            int lastWidth = -1;
            int lastHeight = -1;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var state = await MonitorEndpointsExtensions.BuildMonitorStateResponseAsync(
                        _activityMonitor,
                        _activityStatistics,
                        _options,
                        _targetAvailability,
                        _modelCatalogCache,
                        stoppingToken,
                        includeOverhead: false,
                        liveness: _targetLiveness,
                        executionTracker: _targetExecutionTracker,
                        contextShrinkManager: _contextAutoShrinkManager,
                        fleetHardware: _fleetHardware).ConfigureAwait(false);

                    var monitorUrl = _options.Value.BindAddress.TrimEnd('/').Replace("+", "localhost") + "/monitor";

                    int width = 80;
                    int height = 24;
                    try
                    {
                        if (Console.WindowWidth > 0) width = Console.WindowWidth;
                        if (Console.WindowHeight > 0) height = Console.WindowHeight;
                    }
                    catch
                    {
                        // Fall back to default dimensions
                    }

                    var rendered = TerminalMonitorRenderer.Render(state, monitorUrl, width, height, enableAnsi);

                    // Skip repainting if the rendered frame and terminal dimensions have not changed
                    if (string.Equals(rendered, lastRendered, StringComparison.Ordinal) &&
                        width == lastWidth &&
                        height == lastHeight)
                    {
                        await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    lastRendered = rendered;
                    lastWidth = width;
                    lastHeight = height;

                    if (enableAnsi)
                    {
                        try
                        {
                            Console.SetCursorPosition(0, 0);
                        }
                        catch
                        {
                            // Ignore if unsupported
                        }

                        // Move cursor to home, print rendered frame, and clear remaining lines below buffer
                        Console.Write("\x1b[H" + rendered + "\x1b[J");
                    }
                    else
                    {
                        Console.WriteLine(rendered);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // Prevent any rendering or snapshot exception from crashing the background loop
                }

                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            AppDomain.CurrentDomain.ProcessExit -= CleanupConsole;
            CleanupConsole(null, EventArgs.Empty);
        }
    }
}
