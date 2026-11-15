using System.Net;
using OllamaRouter.App.ViewModels;
using OllamaRouter.Client;
using OllamaRouter.Endpoints;
using OllamaRouter.Endpoints.RouterApi.Vram;
using OllamaRouter.Services.Monitoring;

namespace OllamaRouter.Tests.App;

/// <summary>
/// <see cref="MonitorViewModel"/> tests (task #102, spec §7): refresh population (active /
/// recent / 7-day breakdown incl. <c>total</c> / savings card / fleet rows), the runtime
/// toggle (exact <c>POST /targets</c> flag map + revert on failure), the Reclaim Local VRAM
/// payload (<c>location: "local"</c>, <c>disableTarget: true</c>) and last-good data on API
/// errors.
/// </summary>
public sealed class MonitorViewModelTests : IAsyncLifetime
{
    private readonly FakeRouterApiClient _client = new();
    private readonly MonitorViewModel _viewModel;

    public MonitorViewModelTests()
    {
        _viewModel = new MonitorViewModel(() => _client);
    }

    internal static MonitorStateResponse BuildMonitorState()
    {
        var fleet = new[]
        {
            new MonitorFleetTargetItem(
                "Local", true, "Online", 2, "4k", "local", "http://127.0.0.1:11434/",
                "llama3:8b", "NVIDIA GeForce RTX 4090",
                ["llama3:8b", "qwen2:1.5b"], 900, "class", 600),
            new MonitorFleetTargetItem("Cloud", true, "Online", 0, "-", "cloud"),
        };
        var last7Days = new Dictionary<string, ActivityStatisticsTotals>
        {
            ["Local"] = new(41, 12000, 3000, 800, 25000),
            ["total"] = new(55, 15000, 4000, 800, 30000),
        };
        return new MonitorStateResponse(
            true,
            fleet,
            new MonitorStatisticsStatus(new Dictionary<string, ActivityStatisticsTotals>(), last7Days),
            [new MonitorInProgressItem("Local", "llama3:8b", 1200, 250.5, "high", "sess-1", "agent-9")],
            [
                new MonitorRecentRequestItem(
                    DateTimeOffset.UtcNow, "Local", "llama3:8b", 1000, 1010, 250, 150.0, 200, true,
                    null, null, null, null, 90),
                new MonitorRecentRequestItem(
                    DateTimeOffset.UtcNow.AddMinutes(-1), "Cloud", "gpt-4o-mini", 500, null, null, 900.0, 429, false),
            ],
            new MonitorCostStatus(
                "$", 3.5, 1.25,
                new Dictionary<string, double> { ["Local"] = 3.92 },
                0.42,
                new Dictionary<string, double> { ["Local"] = 0.42 }));
    }

    [Fact]
    public async Task RefreshAsync_PopulatesAllSections_FromTheFakeMonitorState()
    {
        _client.MonitorState = BuildMonitorState();

        await _viewModel.RefreshAsync();

        Assert.False(_viewModel.IsErrorVisible);
        var active = Assert.Single(_viewModel.ActiveRequests);
        Assert.Equal("Local", active.Target);
        Assert.Equal("llama3:8b", active.Model);
        Assert.Equal(1200, active.EstimatedPromptTokens);
        Assert.Equal("high", active.ReasoningEffort);
        Assert.Equal("sess-1", active.SessionId);
        Assert.Equal("agent-9", active.AgentId);

        Assert.Equal(2, _viewModel.RecentRequests.Count);
        var recent = _viewModel.RecentRequests[0];
        Assert.Equal(1010, recent.ActualPromptTokens);
        Assert.Equal(250, recent.ActualResponseTokens);
        Assert.Equal(90, recent.CachedInputTokens);
        Assert.Equal(200, recent.StatusCode);
        Assert.True(recent.Success);

        Assert.Equal(2, _viewModel.TargetBreakdown.Count);
        var total = _viewModel.TargetBreakdown.Single(r => r.IsTotal);
        Assert.Equal(55, total.Requests);
        Assert.Equal(15000, total.InputTokens);
        Assert.Equal(4000, total.OutputTokens);
        Assert.Equal(800, total.CachedInputTokens);
        Assert.Equal(30.0, total.ElapsedSeconds, 3);
        var local = Assert.Single(_viewModel.TargetBreakdown, r => r.Target == "Local");
        Assert.Equal(25.0, local.ElapsedSeconds, 3);

        var card = Assert.IsType<MonitorSavingsCard>(_viewModel.SavingsCard);
        Assert.Equal("$", card.Currency);
        Assert.Equal(3.5, card.EstimatedSavings);
        Assert.Equal(1.25, card.CloudOverflowCost);
        Assert.Equal(0.42, card.ElectricityCost);
        Assert.Equal(3.92, card.PerTargetSavings["Local"]);
        Assert.Equal(0.42, card.PerTargetElectricityCost["Local"]);

        Assert.Equal(2, _viewModel.FleetRows.Count);
        var fleetRow = Assert.Single(_viewModel.FleetRows, r => r.Name == "Local");
        Assert.True(fleetRow.Enabled);
        Assert.Equal("Online", fleetRow.Liveness);
        Assert.Equal(2, fleetRow.InFlightRequests);
        Assert.Equal("llama3:8b", fleetRow.ActiveModel);
        Assert.Equal(new[] { "llama3:8b", "qwen2:1.5b" }, fleetRow.ResidentModels);
        Assert.Equal("local", fleetRow.Location);
    }

    [Fact]
    public async Task ToggleTarget_SendsTheComplementFlagMap_AndFlipsTheRow()
    {
        _client.MonitorState = BuildMonitorState();
        await _viewModel.RefreshAsync();
        var row = Assert.Single(_viewModel.FleetRows, r => r.Name == "Local");
        Assert.True(row.Enabled);

        await _viewModel.ToggleTargetCommand.ExecuteAsync(row);

        var flags = Assert.Single(_client.AvailabilityCalls);
        Assert.Equal("Local", Assert.Single(flags.Keys));
        Assert.False(flags["Local"]);
        Assert.False(row.Enabled);
        Assert.False(_viewModel.IsErrorVisible);
    }

    [Fact]
    public async Task ToggleTarget_OnFailure_RevertsTheFlag_AndSetsLastError()
    {
        _client.MonitorState = BuildMonitorState();
        await _viewModel.RefreshAsync();
        var row = Assert.Single(_viewModel.FleetRows, r => r.Name == "Local");

        _client.NextCallException = new OllamaRouterApiException(HttpStatusCode.InternalServerError, "boom");
        await _viewModel.ToggleTargetCommand.ExecuteAsync(row);

        Assert.True(row.Enabled); // reverted
        Assert.True(_viewModel.IsErrorVisible);
        Assert.Contains("boom", _viewModel.LastError);
    }

    [Fact]
    public async Task ReclaimLocalVram_SendsLocalDisablePayload_AndSurfacesTotalUnloaded()
    {
        _client.ReclaimResponse = new RouterVramReclaimResponse(
            true, 2,
            [new RouterVramReclaimTargetResult("Local", ["llama3:8b", "qwen2:1.5b"], true)],
            null);

        await _viewModel.ReclaimLocalVramCommand.ExecuteAsync(null);

        var request = Assert.Single(_client.ReclaimCalls);
        Assert.NotNull(request);
        Assert.Equal("local", request!.Location);
        Assert.True(request.DisableTarget);
        Assert.Null(request.Target);
        Assert.Contains("2 model(s) unloaded", _viewModel.ReclaimResultText);
        Assert.Contains("llama3:8b", _viewModel.ReclaimResultText);
        Assert.Contains("[disabled]", _viewModel.ReclaimResultText);
    }

    [Fact]
    public async Task RefreshAsync_OnApiError_KeepsLastGoodData_AndSetsLastError()
    {
        _client.MonitorState = BuildMonitorState();
        await _viewModel.RefreshAsync();
        var rowsBefore = _viewModel.ActiveRequests.Count;

        _client.NextCallException = new OllamaRouterApiException(HttpStatusCode.NotFound, "route gone");
        await _viewModel.RefreshAsync();

        Assert.True(_viewModel.IsErrorVisible);
        Assert.Contains("route gone", _viewModel.LastError);
        Assert.Equal(rowsBefore, _viewModel.ActiveRequests.Count); // last-good preserved
        Assert.Equal(2, _viewModel.FleetRows.Count);
    }

    [Fact]
    public async Task RefreshAsync_With7DayActivity_SetsHasUsageActivity_AndNonZeroSummary()
    {
        _client.MonitorState = BuildMonitorState();

        await _viewModel.RefreshAsync();

        Assert.True(_viewModel.HasUsageActivity);
        Assert.NotEqual("0 tokens", _viewModel.UsageRatioSummary);
    }

    [Fact]
    public async Task RefreshAsync_WithZero7DayActivity_ClearsHasUsageActivity_AndShowsZeroSummary()
    {
        _client.MonitorState = BuildMonitorState();
        await _viewModel.RefreshAsync();
        Assert.True(_viewModel.HasUsageActivity);

        _client.MonitorState = new MonitorStateResponse(
            true,
            [new MonitorFleetTargetItem("Local", true, "Online", 0, "-", "local", "http://127.0.0.1:11434/")],
            new MonitorStatisticsStatus(new Dictionary<string, ActivityStatisticsTotals>(), new Dictionary<string, ActivityStatisticsTotals>()),
            [],
            [],
            null);

        await _viewModel.RefreshAsync();

        Assert.False(_viewModel.HasUsageActivity);
        Assert.Equal("0 tokens", _viewModel.UsageRatioSummary);
    }

    [Fact]
    public async Task RefreshAsync_UsageRatios_OrderAndPercentages_MatchTheTokenShares()
    {
        _client.MonitorState = BuildMonitorState();

        await _viewModel.RefreshAsync();

        // BuildMonitorState: Local = 12000 in + 3000 out = 15000 tokens, only non-total entry.
        var ratio = Assert.Single(_viewModel.UsageRatios);
        Assert.Equal("Local", ratio.Name);
        Assert.Equal(100.0, ratio.Percentage, 10);
        Assert.Equal(15000, ratio.Tokens);
        Assert.Equal(41, ratio.Requests);
    }

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task DisposeAsync() => await _viewModel.DisposeAsync();
}
