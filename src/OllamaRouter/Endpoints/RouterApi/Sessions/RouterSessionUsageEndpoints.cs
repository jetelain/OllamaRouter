using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OllamaRouter.Middleware;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Routing;

namespace OllamaRouter.Endpoints.RouterApi.Sessions;

/// <summary>
/// The per-session usage &amp; savings endpoints of the OllamaRouter programmatic management
/// API (task #85):
/// <list type="bullet">
/// <item><c>GET /api/router/monitor/sessions</c> — the stored per-session aggregates
/// (token totals, cost/savings, last-seen timestamp), most-recently-active first.</item>
/// <item><c>GET /api/router/monitor/sessions/{sessionId}</c> — one session's totals plus its
/// per-target breakdown (unknown session → 404).</item>
/// </list>
/// The store holds RAW COUNTERS only; cost figures are computed at READ time from the
/// currently configured <c>Pricing</c> (cached-token pricing + electricity subtraction, the
/// monitor's exact cost math), so a pricing change re-prices every stored session immediately.
/// </summary>
public static class RouterSessionUsageEndpoints
{
    /// <summary>
    /// Maps the session usage &amp; savings endpoints
    /// (<c>GET /api/router/monitor/sessions</c>, <c>GET /api/router/monitor/sessions/{*sessionId}</c>).
    /// </summary>
    public static WebApplication MapRouterSessionUsageEndpoints(this WebApplication app)
    {
        app.MapGet("/api/router/monitor/sessions", (ISessionUsageStore store, IOptionsMonitor<OllamaRouterOptions> options) =>
        {
            var opt = options.CurrentValue;
            var currency = CurrencyOf(opt);
            var sessions = store.GetSessions().Select(snapshot => ToSessionItem(snapshot, opt, currency)).ToList();
            return Results.Json(
                new RouterSessionUsageListResponse(currency, sessions.Count, sessions),
                OllamaRouterJsonSerializerContext.Default.RouterSessionUsageListResponse);
        })
        .WithName("GetRouterSessionUsageList")
        .WithTags("Session Usage & Savings")
        .WithSummary("Per-session token usage, actual price and savings (most-recently-active first).")
        .WithDescription("Read-only list of the in-memory per-session aggregates keyed by the public "
            + "X-OllamaRouter-Session-Id header, most-recently-active first (reads never refresh recency — "
            + "eviction is driven by request traffic only). Each row carries the session's accumulated "
            + "token totals (inputTokens are cached-INCLUSIVE; cachedInputTokens are reported separately "
            + "as a subset) and cumulative GPU time (elapsedMs) plus cost figures (cloudValue, "
            + "electricityCost, savings, actualCloudCost, cost) computed at READ time from the CURRENTLY configured "
            + "Pricing (cached-token pricing + electricity subtraction; Cloud targets contribute their token value "
            + "to cloudValue but no electricity/savings; 4-decimal rounding, never clamped). cloudValue is the "
            + "all-cloud BASELINE (every token — local and cloud — valued at cloud rates); actualCloudCost is "
            + "the Cloud target's OWN token value — the actual money paid to cloud (0.0 when the Cloud target "
            + "was never used); cost = actualCloudCost + electricityCost — the session's total actual spend "
            + "(cloud spend + local power), a distinct figure from the savings-vs-baseline savings. A pricing "
            + "change re-prices every stored session immediately. When no token price rate is configured "
            + "the currency/cloudValue/electricityCost/savings/actualCloudCost/cost fields are omitted (token "
            + "totals are always "
            + "present). The store is a bounded LRU (OllamaRouter:SessionUsageMaxSessions, default 1000, "
            + "live) that is best-effort persisted to session-usage.json in the state directory (throttled ~30 s "
            + "background write plus a flush on graceful shutdown); raw counters only — the cost figures "
            + "stay computed at read time, so pricing changes re-price persisted sessions immediately. "
            + "Requests without a session ID are never stored; failed requests "
            + "contribute nothing; successful completions without daemon-reported usage contribute elapsed "
            + "time only. Read-only — no key required in default mode.");
        app.MapGet("/api/router/monitor/sessions/{*sessionId}", (ISessionUsageStore store, IOptionsMonitor<OllamaRouterOptions> options, string sessionId) =>
        {
            sessionId = Uri.UnescapeDataString(sessionId).TrimStart('/');
            var opt = options.CurrentValue;
            var snapshot = store.TryGetSession(sessionId);
            if (snapshot is null)
            {
                return Results.Json(
                    new OllamaErrorResponse($"session not found: {sessionId}"),
                    OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                    statusCode: StatusCodes.Status404NotFound);
            }

            var detail = BuildSessionDetail(snapshot, opt);
            return Results.Json(detail, OllamaRouterJsonSerializerContext.Default.RouterSessionUsageDetailResponse);
        })
        .WithName("GetRouterSessionUsageDetail")
        .WithTags("Session Usage & Savings")
        .WithSummary("One session's usage totals plus per-target price/savings breakdown.")
        .WithDescription("Registered as the catch-all GET /api/router/monitor/sessions/{*sessionId} route "
            + "so session IDs containing slashes work (the segment is unescaped and trimmed of the leading "
            + "'/'; lookup is case-insensitive). Returns the session's accumulated token totals and cost "
            + "figures (computed at read time from the currently configured Pricing — see "
            + "GetRouterSessionUsageList for the cost semantics, including the top-level session-level "
            + "actualCloudCost — the Cloud target's own token value / actual cloud spend, 0.0 when the Cloud "
            + "target was never used — and cost = actualCloudCost + electricityCost, the total actual spend) "
            + "at the top level (actualCloudCost/cost are NOT on the per-target rows) plus the per-target "
            + "breakdown (per-row tokens, elapsedMs, cloudValue, electricityCost, savings; configured "
            + "fleet order, Cloud last, historical since-renamed/removed targets appended after; the Cloud "
            + "row carries electricityCost 0 and savings 0 when present). Unknown/absent session → 404 "
            + "OllamaErrorResponse. Read-only — no key required in default mode.");
        return app;
    }

    // The configured currency symbol, or null when no token price rate is configured (Q3 = A:
    // the cost fields — and the currency alongside them — are omitted on the wire).
    private static string? CurrencyOf(OllamaRouterOptions opt) =>
        opt.Pricing is { IsConfigured: true } pricing ? pricing.Currency : null;

    // Builds the list-row / top-level fields of one snapshot from its raw per-target
    // counters. The session totals are ALWAYS the sums over the per-target map (single source
    // of truth) and the cost math reuses the monitor's exact helpers (TokenCost +
    // ElectricityCost + EffectiveRates + ResolvePowerWatts): per-target values rounded first,
    // totals rounded last, never clamped.
    private static RouterSessionUsageItem ToSessionItem(
        SessionUsageSnapshot snapshot,
        OllamaRouterOptions opt,
        string? currency)
    {
        var perTarget = ComputePerTarget(snapshot.ByTarget, opt, currency);

        int requests = 0, input = 0, cached = 0, output = 0;
        var elapsedMs = 0.0;
        double cloudValue = 0.0, electricity = 0.0, savings = 0.0;
        double actualCloudCost = 0.0;
        foreach (var (target, cost) in perTarget)
        {
            requests += cost.Totals.Requests;
            input += cost.Totals.InputTokens;
            cached += cost.Totals.CachedInputTokens;
            output += cost.Totals.OutputTokens;
            elapsedMs += cost.Totals.ElapsedMilliseconds;
            if (cost.CloudValue is { } value && cost.ElectricityCost is { } powerCost && cost.Savings is { } saved)
            {
                // Q1 = A (mirror the monitor): cloudValue includes the Cloud token value; the
                // electricity/savings sums include only non-Cloud rows (Cloud carries 0/0).
                cloudValue += value;
                electricity += powerCost;
                savings += saved;
                // The Cloud row's per-target cloudValue is its OWN token value (already 4-dec
                // rounded upstream) — the actual money paid to cloud (actualCloudCost = 0.0 when
                // the session never used the Cloud target).
                if (target == RoutingTarget.Cloud)
                {
                    actualCloudCost = value;
                }
            }
        }

        return new RouterSessionUsageItem(
            snapshot.SessionId,
            requests,
            input,
            cached,
            output,
            elapsedMs,
            snapshot.LastSeenUtc,
            currency is null ? null : Math.Round(cloudValue, 4),
            currency is null ? null : Math.Round(electricity, 4),
            currency is null ? null : Math.Round(savings, 4),
            currency is null ? null : actualCloudCost,
            currency is null ? null : Math.Round(actualCloudCost + electricity, 4));
    }

    private static RouterSessionUsageDetailResponse BuildSessionDetail(
        SessionUsageSnapshot snapshot,
        OllamaRouterOptions opt)
    {
        var currency = CurrencyOf(opt);
        var perTarget = ComputePerTarget(snapshot.ByTarget, opt, currency);

        // Row order: configured fleet order (options.GetEffectiveTargets()), Cloud last, any
        // historical (since-renamed/removed) targets appended after.
        var effectiveTargets = opt.GetEffectiveTargets();
        var remaining = new HashSet<RoutingTarget>(perTarget.Keys);
        var ordered = new List<(RoutingTarget Target, SessionTargetCost Cost)>();
        foreach (var target in effectiveTargets)
        {
            var routingTarget = (RoutingTarget)target.Name!;
            if (remaining.Remove(routingTarget))
            {
                ordered.Add((routingTarget, perTarget[routingTarget]));
            }
        }

        if (remaining.Remove(RoutingTarget.Cloud))
        {
            ordered.Add((RoutingTarget.Cloud, perTarget[RoutingTarget.Cloud]));
        }

        foreach (var target in remaining)
        {
            ordered.Add((target, perTarget[target]));
        }

        var item = ToSessionItem(snapshot, opt, currency);
        var rows = ordered.Select(p => new RouterSessionUsageTargetItem(
            p.Target.Value,
            p.Cost.Totals.Requests,
            p.Cost.Totals.InputTokens,
            p.Cost.Totals.CachedInputTokens,
            p.Cost.Totals.OutputTokens,
            p.Cost.Totals.ElapsedMilliseconds,
            p.Cost.CloudValue,
            p.Cost.ElectricityCost,
            p.Cost.Savings)).ToList();

        return new RouterSessionUsageDetailResponse(
            currency,
            snapshot.SessionId,
            item.Requests,
            item.InputTokens,
            item.CachedInputTokens,
            item.OutputTokens,
            item.ElapsedMs,
            snapshot.LastSeenUtc,
            item.CloudValue,
            item.ElectricityCost,
            item.Savings,
            item.ActualCloudCost,
            item.Cost,
            rows);
    }

    // Computes the per-target cost figures for one snapshot with the CURRENT effective
    // pricing (all cost fields null when no token price rate is configured — Q3 = A).
    private static Dictionary<RoutingTarget, SessionTargetCost> ComputePerTarget(
        IReadOnlyDictionary<RoutingTarget, SessionUsageTotals> byTarget,
        OllamaRouterOptions opt,
        string? currency)
    {
        var result = new Dictionary<RoutingTarget, SessionTargetCost>(byTarget.Count);
        if (currency is null)
        {
            foreach (var (target, totals) in byTarget)
            {
                result[target] = new SessionTargetCost(totals, null, null, null);
            }
            return result;
        }

        var rates = MonitorEndpointsExtensions.EffectiveRates(opt.Pricing!);
        var effectiveTargets = opt.GetEffectiveTargets();
        foreach (var (target, totals) in byTarget)
        {
            // Reuse the monitor math on an equivalently-shaped totals record (the same raw
            // counters) so the session cost figures equal the monitor's for identical inputs
            // by construction (no second implementation).
            var stats = new ActivityStatisticsTotals(
                totals.Requests, totals.InputTokens, totals.OutputTokens, totals.CachedInputTokens,
                totals.ElapsedMilliseconds);
            var cloudValue = Math.Round(
                MonitorEndpointsExtensions.TokenCost(stats, rates.Input, rates.Cached, rates.Output), 4);

            double? electricityCost, savings;
            if (target == RoutingTarget.Cloud)
            {
                // Q1 = A (mirror the monitor): the Cloud pseudo-target has no node
                // electricity and its token value is a cost against the all-cloud baseline,
                // not a saving.
                electricityCost = 0;
                savings = 0;
            }
            else
            {
                electricityCost = MonitorEndpointsExtensions.ElectricityCost(
                    totals.ElapsedMilliseconds,
                    MonitorEndpointsExtensions.ResolvePowerWatts(target, effectiveTargets, opt.Pricing!),
                    rates.ElectricityKwh);
                // Never clamped: negative savings are reported as-is.
                savings = Math.Round(cloudValue - electricityCost.Value, 4);
            }

            result[target] = new SessionTargetCost(totals, cloudValue, electricityCost, savings);
        }
        return result;
    }

    /// <summary>One per-target row: the raw counters plus its (nullable) cost figures.</summary>
    private readonly record struct SessionTargetCost(
        SessionUsageTotals Totals, double? CloudValue, double? ElectricityCost, double? Savings);
}
