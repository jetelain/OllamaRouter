using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using OllamaRouter.App.Connection;
using OllamaRouter.App.ViewModels;
using OllamaRouter.App.Views;
using OllamaRouter.Client;
using Pmad.LocalApi.Security;
using System.Net;
using System.Windows.Input;

namespace OllamaRouter.Tests.App;

/// <summary>
/// AC3 cross-thread regression tests (task #173): background-thread updates to
/// <see cref="MainWindowViewModel.IsBusy"/> / command state must NEVER throw Avalonia's
/// cross-thread <see cref="InvalidOperationException"/>. The hazard is documented by T1
/// (the unguarded primitive throws: Avalonia's Button CanExecuteChanged handler mutates
/// <c>IsEnabled</c> on the CALLING thread), and T2–T4 prove the app paths marshal through
/// <see cref="AppUiThread"/> / the binding engine. A LIVE headless session is REQUIRED —
/// without it the notifications run synchronously and the hazard cannot surface.
/// </summary>
[Collection("Avalonia")]
public sealed class MainWindowUiThreadDispatchTests :
    IClassFixture<PreloadDialogLayoutTests.HeadlessSessionFixture>, IAsyncLifetime
{
    private readonly PreloadDialogLayoutTests.HeadlessSessionFixture _fixture;
    private readonly FakeRouterApiClient _client = new();
    private readonly FakeDaemonLauncher _launcher = new();
    private readonly AppConnectionHarness _harness;

    private MainWindowViewModel? _viewModel;

    public MainWindowUiThreadDispatchTests(PreloadDialogLayoutTests.HeadlessSessionFixture fixture)
    {
        _fixture = fixture;
        _harness = new AppConnectionHarness(new FakeDefaultKeyReader(), () => _client, () => _launcher);
    }

    private static Task GatedDelay(TimeSpan span, CancellationToken ct) =>
        new Task(static () => { }, ct);

    /// <summary>
    /// T1 (documents the hazard): a UI-thread-constructed <see cref="Button"/> whose
    /// <c>CanExecuteChanged</c> handler mutates the control (the exact shape of Avalonia's
    /// Button handler) throws the cross-thread <see cref="InvalidOperationException"/>
    /// when an UNGUARDED <c>NotifyCanExecuteChanged</c> fires on a pool thread. If this ever
    /// stops throwing (Avalonia behavior change), delete this test — T2–T4 carry the contract.
    /// </summary>
    [Fact]
    public async Task T1_UnguardedCanExecuteChangedFromBackgroundThread_ThrowsCrossThreadException()
    {
        await _fixture.DispatchAsync(async () =>
        {
            var probe = new Button();
            var command = new ProbeCommand();
            command.CanExecuteChanged += (_, _) => probe.IsEnabled = command.CanExecute(null);

            var exception = await Record.ExceptionAsync(
                () => Task.Run(() => command.NotifyCanExecuteChanged()));

            Assert.True(
                exception is InvalidOperationException,
                "expected the unguarded pool-thread NotifyCanExecuteChanged to throw InvalidOperationException, got " +
                $"{exception?.GetType().Name ?? "null"}");
            Assert.Contains("calling thread", exception!.Message);
        });
    }

    /// <summary>
    /// T2: driving the main view model's full lifecycle (<c>StartAsync</c> — the IsBusy
    /// flips, every <c>RaiseCommandAvailability</c>, all property updates) from a POOL
    /// thread against a REAL <see cref="MainWindow"/> (with its header buttons logically
    /// attached) never throws, and the notifications are DEFERRED to the dispatcher (they
    /// never run inline on the raising thread). RED if the <see cref="AppUiThread"/> guard is
    /// removed (the header Button's CanExecuteChanged handler would mutate IsEnabled on the
    /// pool thread and throw the cross-thread exception inside StartAsync).
    /// </summary>
    [Fact]
    public async Task T2_BackgroundThreadStartAsync_NeverThrows_AndMarshalsToUiThread()
    {
        int? notificationThreadId = null;
        int? poolThreadId = null;

        await _fixture.DispatchAsync(async () =>
        {
            _launcher.Status = FakeDaemonLauncher.RunningStatus();
            _viewModel = BuildViewModel();
            _viewModel.RefreshStatusCommand.CanExecuteChanged += (_, _) =>
                notificationThreadId = Thread.CurrentThread.ManagedThreadId;

            var window = new MainWindow();
            window.DataContext = _viewModel;

            // The background work runs on a POOL thread INSIDE this active dispatch (the
            // headless session exposes Application.Current only while a dispatch is live).
            var backgroundException = await Record.ExceptionAsync(
                async () => await Task.Run(async () =>
                {
                    poolThreadId = Thread.CurrentThread.ManagedThreadId;
                    await _viewModel.StartAsync(CancellationToken.None);
                }));

            Assert.True(backgroundException is null,
                "background-thread startup must never throw cross-thread, got " + backgroundException);
        });

        // Pump (bounded — the posted notification is on the dispatcher queue and may land
        // just after this dispatch's job) until the notification has run.
        var observed = false;
        for (var i = 0; i < 100 && !observed; i++)
        {
            observed = await _fixture.DispatchAsync(() => notificationThreadId is not null);
        }

        await _fixture.DispatchAsync(() =>
        {
            // The notification ran on a dispatcher-side thread — NEVER on the raising pool
            // thread. (An unguarded notification would run inline on the pool thread and
            // throw VerifyAccess inside the window Button's handler → backgroundException.)
            Assert.True(observed,
                "the CanExecuteChanged notification must have run");
            Assert.True(
                notificationThreadId != poolThreadId,
                $"the notification ran on the raising pool thread {poolThreadId}, not a dispatcher thread");
            Assert.False(_viewModel!.IsBusy);
            Assert.Equal("Running", _viewModel.StatusText);
            Assert.Equal(DaemonStatus.Running, _viewModel.DaemonStatus);
        });
    }

    /// <summary>
    /// T3 (the direct AC3 probe): a subscriber that mutates a UI-thread-constructed control
    /// (exactly what Avalonia's Button does) observes the <c>RaiseCommandAvailability</c>
    /// notification raised by a CONNECTION SWITCH executed from a pool thread ON the pumping
    /// UI thread — the notification is marshaled by <see cref="AppUiThread"/>. The switch's
    /// fire-and-forget refresh (raw method — no CommunityToolkit wrapper) settles the same
    /// way. RED if the guard is removed (the handler would run on the pool thread and the
    /// pump would surface the exception).
    /// </summary>
    [Fact]
    public async Task T3_BackgroundThreadConnectionSwitch_MarshalsCommandNotificationToUiThread()
    {
        int? observedThreadId = null;
        int? poolThreadId = null;
        Button? probe = null;

        await _fixture.DispatchAsync(async () =>
        {
            _launcher.Status = FakeDaemonLauncher.RunningStatus();
            _viewModel = BuildViewModel();
            await _viewModel.StartAsync(CancellationToken.None);

            probe = new Button();
            _viewModel.RefreshStatusCommand.CanExecuteChanged += (_, _) =>
            {
                observedThreadId = Thread.CurrentThread.ManagedThreadId;
                probe!.IsEnabled = _viewModel.RefreshStatusCommand.CanExecute(null);
            };

            // The switch runs on a POOL thread INSIDE this active dispatch (the headless
            // session exposes Application.Current only while a dispatch is live, so the
            // AppUiThread guard takes its POST branch exactly as in production).
            var backgroundException = await Record.ExceptionAsync(
                async () => await Task.Run(async () =>
                {
                    poolThreadId = Thread.CurrentThread.ManagedThreadId;
                    await _harness.Service.SwitchConnectionAsync(new RouterConnectionProfile(
                        RouterConnectionMode.Remote, new Uri("http://192.168.1.50:11434/"), "key"));
                }));

            // If the AppUiThread guard were removed, the handler would run INLINE on the pool
            // thread here and probe.IsEnabled's VerifyAccess would throw inside the switch.
            Assert.True(backgroundException is null,
                "background-thread connection switch must never throw cross-thread, got " +
                backgroundException);
        });

        // Bounded pump: the posted notification is on the dispatcher queue and may land
        // just after this dispatch's job.
        var observed = false;
        for (var i = 0; i < 100 && !observed; i++)
        {
            observed = await _fixture.DispatchAsync(() => observedThreadId is not null);
        }

        await _fixture.DispatchAsync(() =>
        {
            // The posted notification ran on a dispatcher-side thread — never on the raising
            // pool thread.
            Assert.True(observed,
                "the posted CanExecuteChanged notification must have run while pumping");
            Assert.True(
                observedThreadId != poolThreadId,
                $"the notification ran on the raising pool thread {poolThreadId}, not a dispatcher thread");
        });

        // The switch's fire-and-forget refresh (raw RefreshStatusAsync, gated) settles the
        // busy flag; pump (bounded — the fake probe is immediate) until CanExecute re-evaluates true.
        var settled = false;
        for (var i = 0; i < 100 && !settled; i++)
        {
            settled = await _fixture.DispatchAsync(() => probe!.IsEnabled);
        }

        Assert.True(settled,
            "CanExecute(null) must be true (not busy) once the background refresh settles");
    }

    /// <summary>
    /// T4 (pins the tab poller path): a tab <c>RefreshAsync</c> driven from a pool thread
    /// surfaces its error into the VM state (<c>LastError</c> / <c>IsErrorVisible</c>) without
    /// throwing — the bound-property path is safe because <c>BindingEntryBase.SetValue</c>
    /// posts cross-thread writes to the UI dispatcher (no plain-.NET-event surface).
    /// </summary>
    [Fact]
    public async Task T4_TabPollerErrorFromBackgroundThread_DoesNotThrow()
    {
        MonitorViewModel? monitorViewModel = null;
        await _fixture.DispatchAsync(() =>
        {
            _client.NextCallException = new OllamaRouterApiException(
                HttpStatusCode.InternalServerError, "monitor boom");

            monitorViewModel = new MonitorViewModel(() => _client, GatedDelay);
            var view = new MonitorView { DataContext = monitorViewModel };
            HeadlessThemeHelper.Apply(view);
        });

        // The poller tick runs on a POOL thread INSIDE this active dispatch (production
        // shape: the DashboardPoller loop is a Task.Run, and the app is live).
        await _fixture.DispatchAsync(async () =>
        {
            var backgroundException = await Record.ExceptionAsync(
                async () => await Task.Run(
                    async () => await monitorViewModel!.RefreshAsync(CancellationToken.None)));

            Assert.True(backgroundException is null,
                "background-thread tab refresh must never throw, got " + backgroundException);
        });

        await _fixture.DispatchAsync(() =>
        {
            Assert.True(monitorViewModel!.IsErrorVisible, "the tab error banner must be visible");
            Assert.False(string.IsNullOrEmpty(monitorViewModel.LastError));
            Assert.Contains("500", monitorViewModel.LastError);
        });
    }

    private MainWindowViewModel BuildViewModel() => new(
        _harness.Service,
        new MonitorViewModel(() => _client, GatedDelay),
        new AgentSessionsViewModel(() => _client, GatedDelay),
        new FleetTargetsViewModel(() => _client, GatedDelay),
        new CartographyViewModel(() => _client, GatedDelay),
        new SettingsViewModel(() => _client, GatedDelay));

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_viewModel is not null)
        {
            await _fixture.DispatchAsync(async () =>
            {
                await _viewModel.DisposeAsync();
            });
        }

        _harness.Dispose();
    }

    /// <summary>
    /// The T1 primitive: a minimal <see cref="ICommand"/> with a plain .NET
    /// <c>CanExecuteChanged</c> event (NO UI-thread marshaling) — the exact shape of the
    /// unguarded <c>NotifyCanExecuteChanged</c> hazard that
    /// <see cref="AppUiThread"/> protects against.
    /// </summary>
    private sealed class ProbeCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) { }

        public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
