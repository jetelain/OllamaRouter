using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OllamaRouter.Client;
using OllamaRouter.Endpoints.RouterApi.Vram;

namespace OllamaRouter.App.ViewModels;

/// <summary>
/// One in-progress request row of the Monitor tab (spec §5.1, wire:
/// <see cref="MonitorInProgressItem"/>).
/// </summary>
public sealed record MonitorActiveRequestRow(
    string Target,
    string Model,
    int EstimatedPromptTokens,
    double ElapsedMs,
    string? ReasoningEffort,
    string? SessionId,
    string? AgentId);

/// <summary>
/// One recent-request row of the Monitor tab (spec §5.1, wire:
/// <see cref="MonitorRecentRequestItem"/>).
/// </summary>
public sealed record MonitorRecentRequestRow(
    DateTimeOffset Timestamp,
    string Target,
    string Model,
    int EstimatedPromptTokens,
    int? ActualPromptTokens,
    int? ActualResponseTokens,
    int? CachedInputTokens,
    double ElapsedMs,
    int? StatusCode,
    bool Success);

/// <summary>
/// One 7-day per-target statistics row (wire:
/// <see cref="OllamaRouter.Endpoints.MonitorStatisticsStatus.Last7Days"/>, incl. the
/// <c>total</c> row).
/// </summary>
public sealed record MonitorTargetStatRow(
    string Target,
    int Requests,
    int InputTokens,
    int OutputTokens,
    int CachedInputTokens,
    double ElapsedSeconds)
{
    /// <summary>The cumulative GPU seconds of the row (the wire <c>elapsedMs</c> ÷ 1000).</summary>
    public double ElapsedMs => ElapsedSeconds * 1000.0;

    /// <summary>True for the reserved <c>total</c> row.</summary>
    public bool IsTotal => Target == "total";
}

/// <summary>
/// The cloud-vs-electricity savings/cost card (wire: <see cref="MonitorCostStatus"/>).
/// <see cref="EstimatedSavings"/> is the ACTUAL 7-day savings (cloud value − electricity; may
/// be negative).
/// </summary>
public sealed partial class MonitorSavingsCard
{
    public required string Currency { get; init; }
    public required double EstimatedSavings { get; init; }
    public required double CloudOverflowCost { get; init; }
    public required double ElectricityCost { get; init; }
    public required IReadOnlyDictionary<string, double> PerTargetSavings { get; init; }
    public required IReadOnlyDictionary<string, double> PerTargetElectricityCost { get; init; }
}

/// <summary>
/// One fleet toggle row of the Monitor tab (wire: <see cref="MonitorFleetTargetItem"/>).
/// <see cref="Enabled"/> is a RUNTIME availability flag: the per-row toggle posts
/// <c>POST /targets</c> (optimistic update, reverted on failure).
/// </summary>
public sealed partial class MonitorFleetRow : ObservableObject
{
    private bool _enabled;

    public MonitorFleetRow(
        string name,
        bool enabled,
        string liveness,
        int inFlightRequests,
        string? activeModel,
        IReadOnlyList<string> residentModels,
        string location,
        string? url,
        string? hardwareProfile = null,
        string? contextAllocation = null,
        int? keepAliveSeconds = null)
    {
        Name = name;
        _enabled = enabled;
        Liveness = liveness;
        InFlightRequests = inFlightRequests;
        ActiveModel = activeModel;
        ResidentModels = residentModels;
        Location = location;
        Url = url;
        HardwareProfile = hardwareProfile;
        ContextAllocation = contextAllocation;
        KeepAliveSeconds = keepAliveSeconds;
    }

    /// <summary>The view-model toggle command (wired by the owner; keeps the row self-
    /// contained for the XAML row template).</summary>
    public ICommand? ToggleCommand { get; set; }

    /// <summary>The fleet target name.</summary>
    public string Name { get; }

    /// <summary>Runtime availability (optimistically toggled by the UI).</summary>
    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    /// <summary>The liveness state ("Online" / "Offline" / "Probing").</summary>
    public string Liveness { get; }

    /// <summary>The number of in-flight requests on the target.</summary>
    public int InFlightRequests { get; }

    /// <summary>The first resident (active) model, if any.</summary>
    public string? ActiveModel { get; }

    /// <summary>The full resident model list (always an array; empty when idle).</summary>
    public IReadOnlyList<string> ResidentModels { get; }

    /// <summary>The lowercase location classification ("local" / "remote" / "cloud").</summary>
    public string Location { get; }

    /// <summary>The target base URL (null for Cloud).</summary>
    public string? Url { get; }

    /// <summary>The hardware profile summary (e.g. "16 GB").</summary>
    public string? HardwareProfile { get; }

    /// <summary>Context length allocation string (e.g. "262k").</summary>
    public string? ContextAllocation { get; }

    /// <summary>Effective keep-alive in seconds (-1 for indefinite).</summary>
    public int? KeepAliveSeconds { get; }

    /// <summary>Formatted keep-alive display (e.g. "15m", "∞", "-").</summary>
    public string KeepAliveDisplay => KeepAliveSeconds switch
    {
        -1 => "\u221E",
        null => "-",
        >= 60 => $"{KeepAliveSeconds.Value / 60}m",
        _ => $"{KeepAliveSeconds.Value}s"
    };
}

public sealed class MonitorCombinedStatRow
{
    public string Target { get; init; } = "";
    public int TodayRequests { get; init; }
    public long TodayInputTokens { get; init; }
    public long TodayOutputTokens { get; init; }
    public int Last7DaysRequests { get; init; }
    public long Last7DaysInputTokens { get; init; }
    public long Last7DaysOutputTokens { get; init; }
    public double Last7DaysElapsedSeconds { get; init; }
    public bool IsTotal => Target.Equals("total", StringComparison.OrdinalIgnoreCase);
}

public sealed class MonitorUsageRatioItem
{
    public string Name { get; init; } = "";
    public string Location { get; init; } = "";
    public double Percentage { get; init; }
    public long Tokens { get; init; }
    public int Requests { get; init; }
    public string FormattedSummary => $"{Views.HumanReadableFormatters.FormatTokens(Tokens)} ({Percentage:0.#}%)";

    /// <summary>
    /// Hover tooltip for a usage-ratio bar segment, matching the web monitor:
    /// <c>{Name}: {pct:0.#}% ({formatted tokens} tokens, {requests} req)</c>.
    /// </summary>
    public string FormattedTooltip =>
        $"{Name}: {Percentage:0.#}% ({Views.HumanReadableFormatters.FormatTokens(Tokens)} tokens, {Requests} req)";
}

public sealed class MonitorTargetSavingsItem
{
    public string Name { get; init; } = "";
    public string Location { get; init; } = "";
    public double Savings { get; init; }
    public string Currency { get; init; } = "$";
    public string FormattedText => $"{Name}: {Currency} {Savings:F2}";
}

/// <summary>
/// The Monitor &amp; Savings tab view model (task #102, spec §5.1): live active requests,
/// recent request history, the 7-day per-target token breakdown (incl. <c>total</c>), the
/// cloud-vs-electricity savings card, per-target runtime Enable/Disable toggles and the
/// "Reclaim Local VRAM (Gaming Mode)" action. Data source: <c>GET /monitor/api</c> (3 s poll).
/// </summary>
public sealed partial class MonitorViewModel : RouterTabViewModel
{
    /// <summary>The Monitor tab polling interval (spec §4).</summary>
    internal static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(3);

    public MonitorViewModel(
        Func<IOllamaRouterApiClient> clientAccessor,
        Func<TimeSpan, CancellationToken, Task>? pollDelay = null)
        : base(clientAccessor, pollDelay)
    {
    }

    /// <inheritdoc />
    protected override TimeSpan PollInterval => MonitorInterval;

    /// <summary>The live in-progress requests.</summary>
    public ObservableCollection<MonitorActiveRequestRow> ActiveRequests { get; } = [];

    /// <summary>The recent request history (most recent first).</summary>
    public ObservableCollection<MonitorRecentRequestRow> RecentRequests { get; } = [];

    /// <summary>The 7-day per-target token breakdown (incl. the <c>total</c> row).</summary>
    public ObservableCollection<MonitorTargetStatRow> TargetBreakdown { get; } = [];

    /// <summary>Combined today &amp; 7-day statistics table matching the web monitor.</summary>
    public ObservableCollection<MonitorCombinedStatRow> StatisticsRows { get; } = [];

    /// <summary>Usage ratio segments for the 7-day breakdown.</summary>
    public ObservableCollection<MonitorUsageRatioItem> UsageRatios { get; } = [];

    /// <summary>Summary text for the usage ratio progress bar (e.g. "9.7M tokens").</summary>
    [ObservableProperty]
    private string _usageRatioSummary = "0 tokens";

    /// <summary>
    /// Whether there is any token activity in the last 7 days (task #114): gates the
    /// segmented usage-ratio bar and its legend — at zero activity the panel shows the
    /// "No token activity in the last 7 days" empty state instead. False before the first poll.
    /// </summary>
    [ObservableProperty]
    private bool _hasUsageActivity;

    /// <summary>Whether cost &amp; savings data is available.</summary>
    [ObservableProperty]
    private bool _hasCost;

    /// <summary>Formatted estimated savings string (e.g. "$ 70.78").</summary>
    [ObservableProperty]
    private string _formattedEstimatedSavings = "-";

    /// <summary>Formatted cloud overflow cost string (e.g. "$ 1.22").</summary>
    [ObservableProperty]
    private string _formattedCloudOverflow = "-";

    /// <summary>Formatted electricity cost line (e.g. "⚡ Electricity (last 7 days): $ 1.64").</summary>
    [ObservableProperty]
    private string _formattedElectricity = "-";

    /// <summary>Per-target savings list for the savings card footer.</summary>
    public ObservableCollection<MonitorTargetSavingsItem> PerTargetSavingsList { get; } = [];

    /// <summary>The cloud-vs-electricity savings/cost card (null before the first load).</summary>
    [ObservableProperty]
    private MonitorSavingsCard? _savingsCard;

    /// <summary>The savings card's display currency (leaf for the compiled XAML bindings;
    /// null before the first cost data).</summary>
    [ObservableProperty]
    private string? _savingsCurrency;

    /// <summary>The ACTUAL 7-day savings in the card's currency (leaf for the compiled XAML
    /// bindings; null before the first cost data).</summary>
    [ObservableProperty]
    private double? _savingsEstimated;

    /// <summary>The 7-day cloud overflow cost (leaf for the compiled XAML bindings).</summary>
    [ObservableProperty]
    private double? _savingsCloudOverflow;

    /// <summary>The 7-day electricity cost (leaf for the compiled XAML bindings).</summary>
    [ObservableProperty]
    private double? _savingsElectricity;

    /// <summary>The fleet rows with their runtime Enable/Disable toggles.</summary>
    public ObservableCollection<MonitorFleetRow> FleetRows { get; } = [];

    /// <summary>The last VRAM-reclaim result summary (null before the first reclaim).</summary>
    [ObservableProperty]
    private string? _reclaimResultText;

    private static async Task RunOnUiAsync(Action action)
    {
        try
        {
            if (Avalonia.Application.Current is null || Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                action();
            }
            else
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(action);
            }
        }
        catch
        {
            action();
        }
    }

    /// <inheritdoc />
    protected override async Task RefreshCoreAsync(CancellationToken ct)
    {
        var state = await ClientAccessor().GetMonitorStateAsync(ct).ConfigureAwait(false);

        await RunOnUiAsync(() =>
        {
            ActiveRequests.Clear();
            foreach (var item in state.InProgress)
            {
                ActiveRequests.Add(new MonitorActiveRequestRow(
                    item.Target, item.Model, item.EstimatedPromptTokens, item.ElapsedMs,
                    item.ReasoningEffort, item.SessionId, item.AgentId));
            }

            RecentRequests.Clear();
            foreach (var item in state.Requests)
            {
                RecentRequests.Add(new MonitorRecentRequestRow(
                    item.Timestamp, item.Target, item.Model, item.EstimatedPromptTokens,
                    item.ActualPromptTokens, item.ActualResponseTokens, item.CachedInputTokens,
                    item.ElapsedMs, item.StatusCode, item.Success));
            }

            TargetBreakdown.Clear();
            foreach (var (target, totals) in state.Statistics.Last7Days)
            {
                TargetBreakdown.Add(new MonitorTargetStatRow(
                    target, totals.Requests, totals.InputTokens, totals.OutputTokens,
                    totals.CachedInputTokens, totals.ElapsedMilliseconds / 1000.0));
            }

            StatisticsRows.Clear();
            var allTargets = state.Statistics.Today.Keys
                .Union(state.Statistics.Last7Days.Keys)
                .Where(k => !k.Equals("total", StringComparison.OrdinalIgnoreCase))
                .ToList();
            allTargets.Add("total");

            foreach (var t in allTargets)
            {
                state.Statistics.Today.TryGetValue(t, out var today);
                state.Statistics.Last7Days.TryGetValue(t, out var last7);
                StatisticsRows.Add(new MonitorCombinedStatRow
                {
                    Target = t,
                    TodayRequests = today?.Requests ?? 0,
                    TodayInputTokens = today?.InputTokens ?? 0,
                    TodayOutputTokens = today?.OutputTokens ?? 0,
                    Last7DaysRequests = last7?.Requests ?? 0,
                    Last7DaysInputTokens = last7?.InputTokens ?? 0,
                    Last7DaysOutputTokens = last7?.OutputTokens ?? 0,
                    Last7DaysElapsedSeconds = (last7?.ElapsedMilliseconds ?? 0) / 1000.0
                });
            }

            // Usage ratios
            UsageRatios.Clear();
            var nonTotalEntries = state.Statistics.Last7Days
                .Where(kv => !kv.Key.Equals("total", StringComparison.OrdinalIgnoreCase))
                .Select(kv => new { Name = kv.Key, Tokens = (long)kv.Value.InputTokens + kv.Value.OutputTokens, Requests = kv.Value.Requests })
                .ToList();
            long totalTokens = nonTotalEntries.Sum(e => e.Tokens);
            UsageRatioSummary = totalTokens > 0
                ? $"{Views.HumanReadableFormatters.FormatTokens(totalTokens)} tokens"
                : "0 tokens";
            HasUsageActivity = totalTokens > 0;

            foreach (var e in nonTotalEntries)
            {
                var pct = totalTokens > 0 ? (e.Tokens * 100.0 / totalTokens) : 0;
                UsageRatios.Add(new MonitorUsageRatioItem
                {
                    Name = e.Name,
                    Location = Views.TargetLocationVisuals.ClassifyLocation(e.Name),
                    Percentage = pct,
                    Tokens = e.Tokens,
                    Requests = e.Requests
                });
            }

            // Savings & cost
            HasCost = state.Cost != null;
            if (state.Cost is { } cost)
            {
                var curr = cost.Currency ?? "$";
                FormattedEstimatedSavings = $"{curr} {cost.EstimatedSavings:F2}";
                FormattedCloudOverflow = $"{curr} {cost.CloudOverflowCost:F2}";
                FormattedElectricity = $"⚡ Electricity (last 7 days): {curr} {cost.ElectricityCost:F2}";

                PerTargetSavingsList.Clear();
                if (cost.PerTargetSavings != null)
                {
                    foreach (var (tName, val) in cost.PerTargetSavings)
                    {
                        PerTargetSavingsList.Add(new MonitorTargetSavingsItem
                        {
                            Name = tName,
                            Location = Views.TargetLocationVisuals.ClassifyLocation(tName),
                            Savings = val,
                            Currency = curr
                        });
                    }
                }
            }
            else
            {
                FormattedEstimatedSavings = "-";
                FormattedCloudOverflow = "-";
                FormattedElectricity = "-";
                PerTargetSavingsList.Clear();
            }

            SavingsCard = state.Cost is { } c
                ? new MonitorSavingsCard
                {
                    Currency = c.Currency,
                    EstimatedSavings = c.EstimatedSavings,
                    CloudOverflowCost = c.CloudOverflowCost,
                    ElectricityCost = c.ElectricityCost,
                    PerTargetSavings = c.PerTargetSavings,
                    PerTargetElectricityCost = c.PerTargetElectricityCost,
                }
                : null;
            SavingsCurrency = state.Cost?.Currency;
            SavingsEstimated = state.Cost?.EstimatedSavings;
            SavingsCloudOverflow = state.Cost?.CloudOverflowCost;
            SavingsElectricity = state.Cost?.ElectricityCost;

            FleetRows.Clear();
            foreach (var item in state.Fleet)
            {
                Views.TargetLocationVisuals.Register(item.Name, item.Location);
                var row = new MonitorFleetRow(
                    item.Name, item.Enabled, item.Liveness, item.InFlightRequests, item.ActiveModel,
                    item.ResidentModels ?? (IReadOnlyList<string>)Array.Empty<string>(), item.Location, item.Url,
                    item.HardwareProfile, item.ContextAllocation, item.KeepAliveSeconds);
                row.ToggleCommand = ToggleTargetCommand;
                FleetRows.Add(row);
            }
        });
    }

    /// <summary>
    /// Toggles the row's RUNTIME availability via <c>POST /targets</c> (single-entry flag map,
    /// the complement of the current flag). Optimistic: the flag flips immediately and reverts
    /// (with <see cref="RouterTabViewModel.LastError"/>) on failure.
    /// </summary>
    [RelayCommand]
    public async Task ToggleTargetAsync(MonitorFleetRow row, CancellationToken ct = default)
    {
        if (row is null)
        {
            throw new ArgumentNullException(nameof(row));
        }

        var previous = row.Enabled;
        row.Enabled = !previous;
        try
        {
            await ClientAccessor()
                .UpdateTargetAvailabilityAsync(new Dictionary<string, bool> { [row.Name] = !previous }, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            row.Enabled = previous;
            SetError(ex.Message);
        }
    }

    /// <summary>
    /// The "Reclaim Local VRAM (Gaming Mode)" action:
    /// <c>POST /api/router/vram/reclaim</c> with <c>location: "local"</c>,
    /// <c>disableTarget: true</c>. Surfaces the result summary in
    /// <see cref="ReclaimResultText"/>. (The confirmation dialog is shown by the view; the VM
    /// never modal-blocks.)
    /// </summary>
    [RelayCommand]
    public async Task ReclaimLocalVramAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await ClientAccessor()
                .ReclaimVramAsync(new RouterVramReclaimRequest(null, "local", true), ct)
                .ConfigureAwait(false);
            ReclaimResultText = FormatReclaimResult(response);
            SetError(null);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    private static string FormatReclaimResult(RouterVramReclaimResponse response)
    {
        var parts = new List<string> { $"{response.TotalUnloaded} model(s) unloaded" };
        foreach (var target in response.Targets)
        {
            var models = target.UnloadedModels.Count > 0
                ? string.Join(", ", target.UnloadedModels)
                : "(none)";
            parts.Add($"{target.Target}: {models}{(target.Disabled ? " [disabled]" : string.Empty)}");
        }

        if (response.Message is not null)
        {
            parts.Add(response.Message);
        }

        return string.Join(" · ", parts);
    }
}
