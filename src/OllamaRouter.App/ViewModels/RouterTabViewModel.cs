using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OllamaRouter.Client;

namespace OllamaRouter.App.ViewModels;

/// <summary>
/// The base class of the four dashboard tab view models (task #102, spec §4):
/// <list type="bullet">
/// <item>
/// A stable <c>Func&lt;IOllamaRouterApiClient&gt;</c> accessor so a reconnection swaps the
/// underlying client without rebuilding the tab tree (every call reads the CURRENT client).
/// </item>
/// <item>
/// A public, idempotent, catch-all <see cref="RefreshAsync"/> (a <see cref="SemaphoreSlim"/>
/// prevents overlapping refreshes; failures set <see cref="LastError"/> and KEEP the
/// last-good data).
/// </item>
/// <item>
/// A <see cref="DashboardPoller"/> with an injectable delay (tests never sleep or spin) and
/// clean <see cref="IAsyncDisposable"/> disposal.
/// </item>
/// </list>
/// </summary>
public abstract partial class RouterTabViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Func<TimeSpan, CancellationToken, Task> _pollDelay;
    private DashboardPoller? _poller;

    protected RouterTabViewModel(
        Func<IOllamaRouterApiClient> clientAccessor,
        Func<TimeSpan, CancellationToken, Task>? pollDelay = null)
    {
        ClientAccessor = clientAccessor ?? throw new ArgumentNullException(nameof(clientAccessor));
        _pollDelay = pollDelay ?? ((span, ct) => Task.Delay(span, ct));
        RefreshCommand = new RelayCommand(
            () =>
            {
                _ = RefreshAsync(CancellationToken.None);
            });
    }

    /// <summary>The accessor to the CURRENT router client (survives reconnections).</summary>
    protected Func<IOllamaRouterApiClient> ClientAccessor { get; }

    /// <summary>The on-demand refresh command (bound to the tab's Refresh button).</summary>
    public IRelayCommand RefreshCommand { get; }

    /// <summary>The polling interval of this tab (Monitor 3 s, Fleet 5 s, Cartography 5 s).</summary>
    protected abstract TimeSpan PollInterval { get; }

    /// <summary>The last error message (null while healthy).</summary>
    [ObservableProperty]
    private string? _lastError;

    /// <summary>Whether the error banner is visible.</summary>
    [ObservableProperty]
    private bool _isErrorVisible;

    /// <summary>
    /// The tab's data fetch (implemented per tab); throws are handled by
    /// <see cref="RefreshAsync"/>.
    /// </summary>
    protected abstract Task RefreshCoreAsync(CancellationToken ct);

    /// <summary>
    /// The public, idempotent refresh: single-writer (overlapping calls are skipped),
    /// catch-all (<see cref="OllamaRouterApiException"/> / <see cref="System.Net.Http.HttpRequestException"/>
    /// / timeout → <see cref="LastError"/> + <see cref="IsErrorVisible"/>; last-good data is
    /// preserved).
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!await _refreshGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await RefreshCoreAsync(ct).ConfigureAwait(false);
            SetError(null);
        }
        catch (OllamaRouterApiException ex)
        {
            SetError($"Router error {(int)ex.Status} {ex.Status}: {ex.Error ?? "(no error body)"}");
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            SetError($"Connection failed: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            SetError("The request timed out.");
        }
        catch (OperationCanceledException)
        {
            // A cancelled refresh is not an error condition.
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Starts the poller (the first tick runs immediately; a no-op when running).</summary>
    public Task StartPollingAsync(CancellationToken ct = default)
    {
        if (PollInterval <= TimeSpan.Zero)
        {
            return Task.CompletedTask;
        }

        _poller ??= new DashboardPoller(ct => RefreshAsync(ct), PollInterval, _pollDelay);
        return _poller.StartAsync();
    }

    /// <summary>Stops the poller (cancels the loop and awaits it — no orphan work).</summary>
    public Task StopPollingAsync() =>
        _poller is null ? Task.CompletedTask : _poller.StopAsync();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var poller = _poller;
        _poller = null;
        if (poller is not null)
        {
            await poller.DisposeAsync().ConfigureAwait(false);
        }

        _refreshGate.Dispose();
        GC.SuppressFinalize(this);
    }

    protected void SetError(string? message)
    {
        LastError = message;
        IsErrorVisible = !string.IsNullOrEmpty(message);
    }
}
