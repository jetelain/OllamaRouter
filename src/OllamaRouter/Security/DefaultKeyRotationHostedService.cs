using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;
using Pmad.LocalApi.Security;

namespace OllamaRouter.Security;

/// <summary>
/// Background auto-rotation of the default API key (task #50). On start it runs one
/// <see cref="IApiKeyStore.RotateDefaultKeyIfStaleAsync"/> check (covers "stale across a
/// process restart"), then ticks every 5 minutes: each tick re-reads the LIVE
/// <c>ApiSecurity.DefaultKeyRotationHours</c> option and rotates when the stored key's
/// <c>generatedAt</c> has elapsed that interval (no grace period — the old key stops working
/// immediately; callers re-read the key after a 401). A failing tick NEVER stops the loop or
/// the host (every tick is swallowed + logged); <see cref="StopAsync"/> cancels the loop.
/// <para>
/// Task #115: the store (Pmad.LocalApi.Security) no longer owns the options — this service
/// re-reads the LIVE <c>ApiSecurity.DefaultKeyRotationHours</c> option on every tick and
/// computes <c>TimeSpan.FromHours(...)</c> passed to
/// <see cref="IApiKeyStore.RotateDefaultKeyIfStaleAsync(TimeSpan)"/>.
/// </para>
/// </summary>
public sealed class DefaultKeyRotationHostedService : IHostedService, IDisposable
{
    /// <summary>
    /// Internal check interval (5 minutes): the rotation decision re-reads the LIVE option on
    /// every tick, so a shorter check only bounds the max rotation delay (≤ interval + hours).
    /// </summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    private readonly IApiKeyStore _keyStore;
    private readonly IOptionsMonitor<OllamaRouterOptions> _options;
    private readonly ILogger<DefaultKeyRotationHostedService> _logger;
    private readonly CancellationTokenSource _stop = new();
    private Task? _runTask;

    /// <summary>Creates the rotation service.</summary>
    /// <param name="keyStore">The API key store (rotation semantics live there).</param>
    /// <param name="options">The live options monitor (supplies the rotation interval).</param>
    /// <param name="logger">The logger.</param>
    public DefaultKeyRotationHostedService(
        IApiKeyStore keyStore,
        IOptionsMonitor<OllamaRouterOptions> options,
        ILogger<DefaultKeyRotationHostedService> logger)
    {
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _runTask = Task.Run(() => RunAsync(_stop.Token));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // DisposeAsync already cancelled: expected on double-stop.
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Startup check + periodic tick loop. The whole loop is guarded: an unexpected failure
    /// is logged and the host keeps running.
    /// </summary>
    private async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            await TickAsync("startup", stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(CheckInterval, stoppingToken).ConfigureAwait(false);
                await TickAsync("periodic", stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown: expected.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The default API key rotation loop terminated unexpectedly.");
        }
    }

    /// <summary>One rotation check; a failure is logged and NEVER propagates.</summary>
    private async Task TickAsync(string phase, CancellationToken cancellationToken)
    {
        try
        {
            var maxAge = TimeSpan.FromHours(_options.CurrentValue.ApiSecurity.DefaultKeyRotationHours);
            if (await _keyStore.RotateDefaultKeyIfStaleAsync(maxAge).ConfigureAwait(false))
            {
                _logger.LogInformation(
                    "Default API key auto-rotation ({Phase}): the previous key no longer works (no grace period).",
                    phase);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Default API key rotation tick ({Phase}) failed; the loop keeps running.", phase);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Dispose();
    }
}
