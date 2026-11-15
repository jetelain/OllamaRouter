using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OllamaRouter.Client;
using OllamaRouter.Endpoints.RouterApi.Sessions;

namespace OllamaRouter.App.ViewModels;

/// <summary>
/// One agent session row of the Agents &amp; Sessions tab (task #124, wire:
/// <see cref="RouterSessionUsageItem"/>). The agent identifier is the client-provided
/// <c>X-OllamaRouter-Session-Id</c> (or <c>X-OllamaRouter-Agent-Id</c>) request header; the
/// token counters are RAW and input is CACHED INCLUSIVE (cached reported separately as a
/// subset). The cost figures are the SERVER's read-time computation from the live
/// <c>Pricing</c> config (single source of truth — the VM only formats, never recomputes).
/// </summary>
public sealed record AgentSessionRow(
    string AgentId,
    int Requests,
    int InputTokens,
    int CachedInputTokens,
    int OutputTokens,
    double ElapsedMs,
    DateTimeOffset LastSeenAt,
    string? Currency,
    double? CloudValue,
    double? ElectricityCost,
    double? Savings,
    double? ActualCloudCost,
    double? Cost)
{
    /// <summary>The session's total tokens (prompt incl. cached + response).</summary>
    public long TotalTokens => (long)InputTokens + OutputTokens;

    /// <summary>The session's total actual spend formatted (e.g. "$ 1.23"; "-" when absent).</summary>
    public string FormattedCost => AgentSessionsViewModel.FormatCost(Cost, Currency);

    /// <summary>The all-cloud token value formatted (e.g. "$ 8.40"; "-" when absent).</summary>
    public string FormattedCloudValue => AgentSessionsViewModel.FormatCost(CloudValue, Currency);

    /// <summary>The session's savings formatted (e.g. "$ 8.00"; "-" when absent).</summary>
    public string FormattedSavings => AgentSessionsViewModel.FormatCost(Savings, Currency);
}

/// <summary>
/// The fleet-wide aggregate over the listed session rows of the Agents &amp; Sessions tab
/// (task #124): sums of the wire counters; the cost totals are sums of the non-null
/// SERVER-computed cost figures (null when no row carries one).
/// </summary>
public sealed record AgentSessionsSummary(
    int Sessions,
    int Requests,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    double ElapsedMs,
    string? Currency,
    double? TotalCost,
    double? TotalCloudValue)
{
    /// <summary>Total tokens across all sessions (prompt incl. cached + response).</summary>
    public long TotalTokens => InputTokens + OutputTokens;

    /// <summary>The total actual spend formatted (e.g. "$ 1.23"; "-" when absent).</summary>
    public string FormattedTotalCost => AgentSessionsViewModel.FormatCost(TotalCost, Currency);

    /// <summary>The total all-cloud token value formatted (e.g. "$ 8.40"; "-" when absent).</summary>
    public string FormattedTotalCloudValue =>
        AgentSessionsViewModel.FormatCost(TotalCloudValue, Currency);

    /// <summary>The total tokens in human-readable form (e.g. "2.25M").</summary>
    public string FormattedTotalTokens => Views.HumanReadableFormatters.FormatTokens(TotalTokens);

    /// <summary>The cumulative GPU time in human-readable form (e.g. "1.2 min").</summary>
    public string FormattedElapsed => Views.HumanReadableFormatters.FormatDurationMs(ElapsedMs);
}

/// <summary>
/// One per-target row of the session-detail breakdown of the Agents &amp; Sessions tab
/// (task #124, wire: <see cref="RouterSessionUsageTargetItem"/> — fleet order, Cloud last).
/// </summary>
public sealed record AgentSessionTargetRow(
    string Target,
    int Requests,
    int InputTokens,
    int CachedInputTokens,
    int OutputTokens,
    double ElapsedMs,
    string? Currency,
    double? CloudValue,
    double? ElectricityCost,
    double? Savings)
{
    /// <summary>The target's all-cloud token value formatted ("-" when absent).</summary>
    public string FormattedCloudValue => AgentSessionsViewModel.FormatCost(CloudValue, Currency);

    /// <summary>The target's electricity cost formatted ("-" when absent).</summary>
    public string FormattedElectricity => AgentSessionsViewModel.FormatCost(ElectricityCost, Currency);

    /// <summary>The target's savings formatted ("-" when absent).</summary>
    public string FormattedSavings => AgentSessionsViewModel.FormatCost(Savings, Currency);
}

/// <summary>
/// The Agents &amp; Sessions tab view model (task #124): the per-agent session list (agent /
/// session id, requests, prompt incl. cached, cached, response tokens, cumulative GPU time,
/// last seen, estimated token cost, total actual cost) sourced from
/// <c>GET /api/router/monitor/sessions</c> (5 s poll), the fleet-wide summary aggregate, and —
/// on row selection — the per-target breakdown of
/// <c>GET /api/router/monitor/sessions/{id}</c>. Cost figures are displayed VERBATIM as the
/// server computed them at read time from the live <c>Pricing</c> config (the VM never
/// re-implements the token-cost math). Empty state, the inherited error banner and last-good
/// data semantics come from <see cref="RouterTabViewModel"/>.
/// </summary>
public sealed partial class AgentSessionsViewModel : RouterTabViewModel
{
    /// <summary>The Agents &amp; Sessions tab polling interval (sessions are slow-changing
    /// aggregates; 5 s, matching the Fleet &amp; Cartography tabs).</summary>
    internal static readonly TimeSpan SessionsInterval = TimeSpan.FromSeconds(5);

    public AgentSessionsViewModel(
        Func<IOllamaRouterApiClient> clientAccessor,
        Func<TimeSpan, CancellationToken, Task>? pollDelay = null)
        : base(clientAccessor, pollDelay)
    {
    }

    /// <inheritdoc />
    protected override TimeSpan PollInterval => SessionsInterval;

    /// <summary>The session rows in wire order (most-recently-active first).</summary>
    public ObservableCollection<AgentSessionRow> Sessions { get; } = [];

    /// <summary>Whether any session is recorded (false ⇒ the empty state is shown).</summary>
    [ObservableProperty]
    private bool _hasSessions;

    /// <summary>The fleet-wide aggregate over the listed rows (null before the first load).</summary>
    [ObservableProperty]
    private AgentSessionsSummary? _summary;

    /// <summary>The selected session row (drives the per-target detail panel; cleared when
    /// the session is evicted from the store).</summary>
    [ObservableProperty]
    private AgentSessionRow? _selectedRow;

    /// <summary>The per-target breakdown of the selected session (fleet order, Cloud last).</summary>
    public ObservableCollection<AgentSessionTargetRow> SelectedTargetRows { get; } = [];

    /// <summary>The inline error of the selected session's detail fetch (list refreshes are
    /// unaffected; null while healthy or nothing is selected).</summary>
    [ObservableProperty]
    private string? _selectedDetailError;

    /// <summary>
    /// Formats a SERVER-computed cost figure for display: <c>"{currency} {value:F2}"</c>
    /// (invariant culture; a missing currency symbol falls back to "$" like the Monitor tab);
    /// a null (absent) value renders as <c>"-"</c> — never <c>"$ 0.00"</c>. Pure.
    /// </summary>
    internal static string FormatCost(double? value, string? currency)
    {
        if (value is not { } v)
        {
            return "-";
        }

        var symbol = string.IsNullOrEmpty(currency) ? "$" : currency;
        return $"{symbol} {v.ToString("F2", CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// The pure fleet-wide aggregate over the session rows: request / token / elapsed sums and
    /// the sums of the non-null SERVER-computed <c>cost</c> / <c>cloudValue</c> figures
    /// (null when no row carries one — i.e. no pricing is configured). The values are never
    /// re-derived from the token counters. Pure.
    /// </summary>
    internal static AgentSessionsSummary BuildSummary(
        IReadOnlyList<RouterSessionUsageItem> rows, string? currency)
    {
        double? totalCost = null;
        double? totalCloudValue = null;
        foreach (var row in rows)
        {
            if (row.Cost is { } cost)
            {
                totalCost = totalCost is { } costTotal ? costTotal + cost : cost;
            }

            if (row.CloudValue is { } cloudValue)
            {
                totalCloudValue = totalCloudValue is { } cloudTotal
                    ? cloudTotal + cloudValue
                    : cloudValue;
            }
        }

        return new AgentSessionsSummary(
            rows.Count,
            rows.Sum(r => r.Requests),
            rows.Sum(r => (long)r.InputTokens),
            rows.Sum(r => (long)r.CachedInputTokens),
            rows.Sum(r => (long)r.OutputTokens),
            rows.Sum(r => r.ElapsedMs),
            currency,
            totalCost,
            totalCloudValue);
    }

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
        var response = await ClientAccessor().GetSessionsAsync(ct).ConfigureAwait(false);
        var previousAgentId = SelectedRow?.AgentId;

        await RunOnUiAsync(() =>
        {
            Sessions.Clear();
            foreach (var item in response.Sessions)
            {
                Sessions.Add(new AgentSessionRow(
                    item.SessionId, item.Requests, item.InputTokens, item.CachedInputTokens,
                    item.OutputTokens, item.ElapsedMs, item.LastSeenAt, response.Currency,
                    item.CloudValue, item.ElectricityCost, item.Savings,
                    item.ActualCloudCost, item.Cost));
            }

            HasSessions = Sessions.Count > 0;
            Summary = BuildSummary(response.Sessions, response.Currency);

            if (previousAgentId is null)
            {
                return;
            }

            var stillPresent = Sessions.FirstOrDefault(r => r.AgentId == previousAgentId);
            if (stillPresent is null)
            {
                // The selected session was evicted from the (LRU) store: clear the selection.
                SelectedRow = null;
            }
            else if (!ReferenceEquals(stillPresent, SelectedRow))
            {
                // Re-point the selection at the refreshed row (fires the detail re-fetch).
                SelectedRow = stillPresent;
            }
        });
    }

    partial void OnSelectedRowChanged(AgentSessionRow? value)
    {
        SelectedDetailError = null;
        if (value is null)
        {
            SelectedTargetRows.Clear();
            return;
        }

        _ = LoadDetailAsync(value);
    }

    /// <summary>
    /// Fire-and-track fetch of the selected session's per-target breakdown. A 404 (evicted
    /// mid-flight) or any other failure is surfaced in <see cref="SelectedDetailError"/>
    /// WITHOUT touching the list's last-good state; a selection that moved on before the
    /// fetch completes is dropped.
    /// </summary>
    private async Task LoadDetailAsync(AgentSessionRow row)
    {
        try
        {
            var detail = await ClientAccessor()
                .GetSessionDetailAsync(row.AgentId, CancellationToken.None)
                .ConfigureAwait(false);

            await RunOnUiAsync(() =>
            {
                if (SelectedRow?.AgentId != row.AgentId)
                {
                    return; // the selection moved on while the fetch was in flight
                }

                SelectedTargetRows.Clear();
                foreach (var item in detail.PerTarget)
                {
                    SelectedTargetRows.Add(new AgentSessionTargetRow(
                        item.Target, item.Requests, item.InputTokens, item.CachedInputTokens,
                        item.OutputTokens, item.ElapsedMs, detail.Currency,
                        item.CloudValue, item.ElectricityCost, item.Savings));
                }

                SelectedDetailError = null;
            });
        }
        catch (OllamaRouterApiException ex)
        {
            await SetDetailErrorAsync(row,
                $"Session detail unavailable (Router error {(int)ex.Status} {ex.Status}): " +
                $"{ex.Error ?? "(no error body)"}");
        }
        catch (Exception ex)
        {
            await SetDetailErrorAsync(row, $"Session detail unavailable: {ex.Message}");
        }
    }

    private async Task SetDetailErrorAsync(AgentSessionRow row, string message)
    {
        await RunOnUiAsync(() =>
        {
            if (SelectedRow?.AgentId != row.AgentId)
            {
                return; // the selection moved on while the fetch was in flight
            }

            SelectedTargetRows.Clear();
            SelectedDetailError = message;
        });
    }
}
