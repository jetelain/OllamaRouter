using System.Text;
using Microsoft.Extensions.Options;
using OllamaRouter.Extensions;
using OllamaRouter.Options;
using OllamaRouter.ReverseProxy;
using OllamaRouter.Serialization;
using OllamaRouter.Services;
using Serilog;
using Serilog.Events;

Console.OutputEncoding = Encoding.UTF8;
Console.Title = "Ollama Router";

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog file & console logging
var rawLogsDir = builder.Configuration["Logging:LogsDirectory"] ?? "logs";
var logsDirectory = Path.IsPathRooted(rawLogsDir)
    ? rawLogsDir
    : Path.Combine(AppContext.BaseDirectory, rawLogsDir);
Directory.CreateDirectory(logsDirectory);
var logFilePath = Path.Combine(logsDirectory, "ollamarouter-.log");

builder.Services.AddSerilog((services, lc) =>
{
    lc.MinimumLevel.Warning()
      .MinimumLevel.Override("OllamaRouter", LogEventLevel.Information)
      .ReadFrom.Services(services)
      .Enrich.FromLogContext()
      .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Fatal)
      .WriteTo.File(
          path: logFilePath,
          rollingInterval: RollingInterval.Day,
          retainedFileCountLimit: 31,
          shared: true,
          outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");

    ApplyLogLevelsFromConfiguration(lc, builder.Configuration);
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, OllamaRouterJsonSerializerContext.Default);
});

// Resolved + normalized options (DynamicContext.Normalize() restores missing or zero-valued built-in
// profiles; .NET config binding would otherwise replace the whole Profiles dictionary on partial
// overrides). The exact same instance is registered by AddOllamaRouter.
var ollamaOptions = builder.Configuration.GetSection(OllamaRouterOptions.SectionName).Get<OllamaRouterOptions>()
    ?? new OllamaRouterOptions();
ollamaOptions.DynamicContext.Normalize();

// Full service registration + request pipeline (YARP from the effective fleet target list, the
// routing middleware before UseRouting, hybrid endpoints, monitor endpoints, YARP catch-all).
builder.Services.AddOllamaRouter(ollamaOptions, enableTui: !IsTuiDisabled(args));

var app = builder.Build();

var disableTui = IsTuiDisabled(args);
if (disableTui)
{
    ollamaOptions.TerminalMonitoring.Enabled = false;
}

static bool IsTuiDisabled(string[] args) => args.Any(a => string.Equals(a, "--no-tui", StringComparison.OrdinalIgnoreCase));

// Effective fleet targets plus the Cloud pseudo-target (deduplicated, case-insensitively).
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("OllamaRouter.Startup");
var fleetTargets = OllamaRouterServiceCollectionExtensions.BuildFleetTargets(ollamaOptions, startupLogger);

// Startup fleet log line + deprecation warnings.
{
    startupLogger.LogInformation("Effective fleet targets: {Targets}", string.Join(", ", fleetTargets.Select(t => t.Value ?? "")));

    if (ollamaOptions.Targets.Count > 0)
    {
        startupLogger.LogInformation(
            "OllamaRouter:Targets is configured with {Count} fleet target(s); routing decisions use the two-stage hot-model affinity pipeline over the configured fleet (catalog presence + hardware suitability + calibrated scoring).", ollamaOptions.Targets.Count);
    }

    var nonDefaultModels = ollamaOptions.Models
        .Where(pair => pair.Value.MaxLocalTokens != ModelThresholds.DefaultMaxLocalTokens ||
                    pair.Value.MinRequiredVramMB != ModelThresholds.DefaultMinRequiredVramMB)
        .Select(pair => pair.Key)
        .ToList();
    if (nonDefaultModels.Count > 0)
    {
        startupLogger.LogWarning(
            "OllamaRouter:Models[{Models}]:MaxLocalTokens/MinRequiredVramMB are deprecated and will be superseded by hardware suitability rules; they are slated for removal after the transition release (the keys keep working in this release).", string.Join(", ", nonDefaultModels));
    }
}

// Ensure local Ollama is running (Windows only).
app.Services.GetRequiredService<IOllamaProcessLauncher>().EnsureRunning(ollamaOptions);

app.UseOllamaRouterPipeline();

// Print a clickable link to the monitoring UI once the server has started (fallback when TUI is disabled).
app.Lifetime.ApplicationStarted.Register(() =>
{
    var monitorUrl = ollamaOptions.BindAddress.TrimEnd('/').Replace("+", "localhost") + "/monitor";
    if (!ollamaOptions.TerminalMonitoring.Enabled || Console.IsOutputRedirected)
    {
        Console.WriteLine("✅ Ollama Router is running");
        Console.WriteLine();
        Console.WriteLine($"\uD83D\uDCCA Activity monitor available at: {monitorUrl}");
    }
});

// Listen on the configured address (defaults to the standard Ollama port).
app.Run(ollamaOptions.BindAddress);

static void ApplyLogLevelsFromConfiguration(LoggerConfiguration lc, IConfiguration configuration)
{
    var logLevelSection = configuration.GetSection("Logging:LogLevel");
    foreach (var child in logLevelSection.GetChildren())
    {
        if (ParseLogLevel(child.Value) is { } level)
        {
            if (string.Equals(child.Key, "Default", StringComparison.OrdinalIgnoreCase))
            {
                lc.MinimumLevel.Is(level);
            }
            else
            {
                lc.MinimumLevel.Override(child.Key, level);
            }
        }
    }

    var serilogLevel = configuration["Serilog:MinimumLevel"];
    if (ParseLogLevel(serilogLevel) is { } sLevel)
    {
        lc.MinimumLevel.Is(sLevel);
    }
    foreach (var child in configuration.GetSection("Serilog:MinimumLevel:Override").GetChildren())
    {
        if (ParseLogLevel(child.Value) is { } sOverrideLevel)
        {
            lc.MinimumLevel.Override(child.Key, sOverrideLevel);
        }
    }
}

static LogEventLevel? ParseLogLevel(string? value) => value?.ToLowerInvariant() switch
{
    "trace" or "verbose" => LogEventLevel.Verbose,
    "debug" => LogEventLevel.Debug,
    "information" or "info" => LogEventLevel.Information,
    "warning" or "warn" => LogEventLevel.Warning,
    "error" => LogEventLevel.Error,
    "critical" or "fatal" => LogEventLevel.Fatal,
    _ => null
};

// Entry point exposed for integration tests (WebApplicationFactory).
public partial class Program;
