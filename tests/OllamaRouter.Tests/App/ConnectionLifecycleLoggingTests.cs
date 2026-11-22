using System.Net;
using OllamaRouter.App.Connection;
using OllamaRouter.App.ViewModels;
using OllamaRouter.App.ViewModels.Wizard;
using OllamaRouter.Client;
using OllamaRouter.Client.Process;
using OllamaRouter.Endpoints.RouterApi.Fleet;
using OllamaRouter.Tests.TestHelpers;
using Serilog.Events;
using Xunit;

namespace OllamaRouter.Tests.App;

/// <summary>
/// Task #159 — daemon-connection-lifecycle logging tests (AC5/AC6/AC7): the real
/// <see cref="AppConnectionService"/> with the injectable factory ctor (NO network — the
/// client/launcher factories return the fakes), the <see cref="MainWindowViewModel"/>
/// daemon state gate, and the AC7 masking invariant (no key value may ever reach a log).
/// </summary>
public sealed class ConnectionLifecycleLoggingTests
{
    private static Task GatedDelay(TimeSpan span, CancellationToken ct) => new Task(static () => { }, ct);

    private static async Task WaitUntilAsync(Func<bool> condition, string what, int maxBeats = 200_000)
    {
        for (var i = 0; i < maxBeats; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Yield();
        }

        Assert.True(condition(), $"condition not met after {maxBeats} cooperative yields: {what}");
    }

    // ==================================================================
    //  AppConnectionService.SwitchConnectionAsync
    // ==================================================================

    [Fact]
    public async Task Switch_LocalToRemote_LogsTransitionWithMaskedKey()
    {
        // AC7: the key value flows to the log ONLY through SecretsMasker.Mask.
        var capturing = new CapturingLogger();
        var harness = new AppConnectionHarness(
            new FakeDefaultKeyReader(), () => new FakeRouterApiClient(),
            () => new FakeDaemonLauncher(), capturing.Logger);
        using (harness)
        {
            await harness.Service.SwitchConnectionAsync(
                new RouterConnectionProfile(
                    RouterConnectionMode.Remote, new Uri("https://router.lan:11434/"), "sk-super-secret-123"),
                CancellationToken.None);

            var event_ = capturing.Sink.Events
                .FirstOrDefault(e => Rendered(e).Contains("Connection profile switched:", StringComparison.Ordinal));
            Assert.NotNull(event_);
            Assert.Equal(LogEventLevel.Information, event_!.Level);
            var rendered = Rendered(event_);
            Assert.Contains("Local → Remote", rendered);
            Assert.Contains("https://router.lan:11434/", rendered);
            Assert.Contains("[REDACTED]", rendered);
            Assert.DoesNotContain("sk-super-secret-123", rendered); // AC7
        }
    }

    [Fact]
    public async Task Switch_RemoteToLocal_LogsTransitionWithNoKey()
    {
        var capturing = new CapturingLogger();
        var harness = new AppConnectionHarness(
            new FakeDefaultKeyReader(), () => new FakeRouterApiClient(),
            () => new FakeDaemonLauncher(), capturing.Logger);
        using (harness)
        {
            await harness.Service.SwitchConnectionAsync(
                new RouterConnectionProfile(
                    RouterConnectionMode.Remote, new Uri("https://router.lan:11434/"), "sk-key-1"),
                CancellationToken.None);
            await harness.Service.SwitchConnectionAsync(
                AppConnectionService.LocalProfile(), CancellationToken.None);

            var events = capturing.Sink.Events
                .Where(e => Rendered(e).Contains("Connection profile switched:", StringComparison.Ordinal))
                .ToList();
            Assert.Equal(2, events.Count);
            var backToLocal = Rendered(events[^1]);
            Assert.Contains("Remote → Local", backToLocal);
            Assert.Contains("(API key: (none))", backToLocal);
        }
    }

    [Fact]
    public async Task Switch_InvalidRemoteProfile_ThrowsAndLogsError()
    {
        var capturing = new CapturingLogger();
        var harness = new AppConnectionHarness(
            new FakeDefaultKeyReader(), () => new FakeRouterApiClient(),
            () => new FakeDaemonLauncher(), capturing.Logger);
        using (harness)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.SwitchConnectionAsync(
                new RouterConnectionProfile(RouterConnectionMode.Remote, new Uri("https://r.lan/"), ""),
                CancellationToken.None));

            var error = capturing.Sink.Events
                .FirstOrDefault(e => e.Level == LogEventLevel.Error
                    && Rendered(e).Contains("Connection profile switch rejected:", StringComparison.Ordinal));
            Assert.NotNull(error);
            Assert.IsType<ArgumentException>(error!.Exception);
        }
    }

    // ==================================================================
    //  MainWindowViewModel daemon state gate + request/outcome lines
    // ==================================================================

    private static (MainWindowViewModel View, AppConnectionHarness Harness, CapturingLogger Capturing,
        FakeRouterApiClient Client, FakeDaemonLauncher Launcher) BuildMain()
    {
        var capturing = new CapturingLogger();
        var client = new FakeRouterApiClient { FleetResponse = new RouterFleetResponse([]) };
        var launcher = new FakeDaemonLauncher();
        var harness = new AppConnectionHarness(
            new FakeDefaultKeyReader(), () => client, () => launcher, capturing.Logger);
        var view = new MainWindowViewModel(
            harness.Service,
            new MonitorViewModel(() => client, GatedDelay),
            new AgentSessionsViewModel(() => client, GatedDelay),
            new FleetTargetsViewModel(() => client, GatedDelay),
            new CartographyViewModel(() => client, GatedDelay),
            new SettingsViewModel(() => client, GatedDelay),
            logger: capturing.Logger);
        return (view, harness, capturing, client, launcher);
    }

    [Fact]
    public async Task StartDaemon_TransitionLogged_RefreshDoesNotRelog_SameState()
    {
        var (view, harness, capturing, _, launcher) = BuildMain();
        try
        {
            await view.StartAsync();
            Assert.Equal(RouterDaemonState.Running, launcher.Status.State); // auto-started
            var initialCount = capturing.Sink.Events
                .Count(e => Rendered(e).Contains("Router daemon state:", StringComparison.Ordinal));
            Assert.Equal(1, initialCount); // "unknown → Running" logged exactly once

            // A second probe (still Running) must NOT log a new transition (D8 anti-spam).
            await view.RefreshStatusCommand.ExecuteAsync(CancellationToken.None);
            Assert.Equal(
                1,
                capturing.Sink.Events.Count(e => Rendered(e).Contains("Router daemon state:", StringComparison.Ordinal)));

            // Stop → Stopped: exactly one more transition.
            await view.StopDaemonCommand.ExecuteAsync(CancellationToken.None);
            Assert.Equal(
                2,
                capturing.Sink.Events.Count(e => Rendered(e).Contains("Router daemon state:", StringComparison.Ordinal)));
            Assert.True(capturing.Sink.RendersContaining("Router daemon state: Running → Stopped"));
        }
        finally
        {
            await view.DisposeAsync();
            harness.Dispose();
        }
    }

    [Fact]
    public async Task StartDaemonCommand_LogsRequestAndOutcome()
    {
        var (view, harness, capturing, _, launcher) = BuildMain();
        try
        {
            launcher.Status = new RouterDaemonStatus(RouterDaemonState.Stopped, null, null);
            await view.StartAsync();
            // Bring the daemon down, then use the explicit Start command.
            launcher.Status = new RouterDaemonStatus(RouterDaemonState.Stopped, null, null);

            await view.StartDaemonCommand.ExecuteAsync(CancellationToken.None);

            Assert.True(capturing.Sink.RendersContaining("Daemon start requested"));
            Assert.True(capturing.Sink.RendersContaining("Daemon start → Running"));
        }
        finally
        {
            await view.DisposeAsync();
            harness.Dispose();
        }
    }

    [Fact]
    public async Task StartDaemonCommand_LauncherFailure_WarningWithException_AndStoppedState()
    {
        var (view, harness, capturing, _, launcher) = BuildMain();
        try
        {
            var boom = new RouterDaemonException("the daemon died on launch");
            launcher.Status = new RouterDaemonStatus(RouterDaemonState.Stopped, null, null);
            launcher.StartException = boom;
            await view.StartAsync();

            await view.StartDaemonCommand.ExecuteAsync(CancellationToken.None);

            var warning = capturing.Sink.EventsAt(LogEventLevel.Warning)
                .FirstOrDefault(e => Rendered(e).Contains("Daemon start failed:", StringComparison.Ordinal));
            Assert.NotNull(warning);
            Assert.Same(boom, warning!.Exception);
            Assert.Equal("Stopped", view.StatusText);
        }
        finally
        {
            await view.DisposeAsync();
            harness.Dispose();
        }
    }

    [Fact]
    public async Task ConnectRemote_MasksTheKeyInTheLog()
    {
        var (view, harness, capturing, _, _) = BuildMain();
        try
        {
            await view.StartAsync();
            view.RemoteUrl = "https://faraway.lan:11434/";
            view.RemoteApiKey = "sk-remote-super-secret";

            await view.ConnectRemoteCommand.ExecuteAsync(CancellationToken.None);

            var connect = capturing.Sink.Events
                .FirstOrDefault(e => Rendered(e).Contains("Connecting to remote router at", StringComparison.Ordinal));
            Assert.NotNull(connect);
            var rendered = Rendered(connect!);
            Assert.Contains("https://faraway.lan:11434/", rendered);
            Assert.Contains("[REDACTED]", rendered);
            Assert.DoesNotContain("sk-remote-super-secret", rendered); // AC7
        }
        finally
        {
            await view.DisposeAsync();
            harness.Dispose();
        }
    }

    [Fact]
    public async Task ConnectLocal_LogsTheConnectionLine()
    {
        var (view, harness, capturing, _, _) = BuildMain();
        try
        {
            await view.StartAsync();
            view.RemoteUrl = "https://faraway.lan:11434/";
            view.RemoteApiKey = "sk-remote-super-secret";
            await view.ConnectRemoteCommand.ExecuteAsync(CancellationToken.None);
            var before = capturing.Sink.Events
                .Count(e => Rendered(e).Contains("Connecting to local daemon", StringComparison.Ordinal));
            Assert.Equal(0, before);

            await view.ConnectLocalCommand.ExecuteAsync(CancellationToken.None);

            Assert.Equal(
                1,
                capturing.Sink.Events.Count(e => Rendered(e).Contains("Connecting to local daemon", StringComparison.Ordinal)));
        }
        finally
        {
            await view.DisposeAsync();
            harness.Dispose();
        }
    }

    /// <summary>Renders a LogEvent's message EXACTLY like the file sink's {Message:lj}
    /// token (literal, unquoted) + the exception — the assertion surface for the injected
    /// capturing logger.</summary>
    private static string Rendered(LogEvent e)
    {
        using var writer = new System.IO.StringWriter();
        SinkMessageFormatter.Format(e, writer);
        return new System.Text.StringBuilder(writer.ToString())
            .AppendLine(e.Exception?.ToString() ?? string.Empty).ToString();
    }

    private static readonly Serilog.Formatting.Display.MessageTemplateTextFormatter SinkMessageFormatter =
        new("{Message:lj}");
}
