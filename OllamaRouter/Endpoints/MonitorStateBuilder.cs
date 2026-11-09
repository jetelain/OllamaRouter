using Microsoft.Extensions.Options;
using OllamaRouter.Options;
using OllamaRouter.Services.Catalog;
using OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Preload;
using OllamaRouter.Services.Routing;
using OllamaRouter.Services.Hardware;

namespace OllamaRouter.Endpoints;

public static partial class MonitorEndpointsExtensions
{
    public static async Task<MonitorStateResponse> BuildMonitorStateResponseAsync(
        IActivityMonitorService activityMonitor,
        IActivityStatisticsService activityStatistics,
        IOptionsMonitor<OllamaRouterOptions> options,
        ITargetAvailabilityService targetAvailability,
        IModelCatalogCacheService modelCatalogCache,
        CancellationToken cancellationToken = default,
        bool includeOverhead = true,
        ITargetLivenessService? liveness = null,
        ITargetExecutionTracker? executionTracker = null,
        IFleetHardwareService? fleetHardware = null)
    {
        var snapshot = activityMonitor.GetSnapshot();
        var statistics = activityStatistics.GetSnapshot();
        var now = DateTimeOffset.UtcNow;
        var cloudEnabled = options.CurrentValue.IsCloudOverflowConfigured();
        var targets = targetAvailability.GetSnapshot();

        var effectiveTargets = options.CurrentValue.GetEffectiveTargets();
        var livenessEntries = liveness?.GetEntries();

        // Ordered fleet list: configured targets first (configuration order), Cloud always last.
        // (Invalid Targets entries were already warned about at startup; this read-only path
        // uses the default no-logger overload to avoid repeated warnings on every request.)
        var fleet = new List<MonitorFleetTargetItem>(effectiveTargets.Count + 1);
        var periodByName = new Dictionary<string, ActivityStatisticsTotals>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in effectiveTargets)
        {
            var name = target.Name!;
            var enabled = targets.Flags.TryGetValue((RoutingTarget)name, out var targetEnabled) ? targetEnabled : true;

            // Liveness: prefer the raw (non-TTL) liveness state machine; fall back to the
            // catalog-based check when the liveness service is not registered.
            string livenessText;
            if (livenessEntries is { } entries && entries.TryGetValue((RoutingTarget)name, out var entry))
            {
                livenessText = entry.State switch
                {
                    TargetLivenessState.Online => "Online",
                    TargetLivenessState.Offline => "Offline",
                    _ => "Probing"
                };
            }
            else
            {
                livenessText = !string.IsNullOrWhiteSpace(target.Url) &&
                               await modelCatalogCache.HasAnyModelsAsync(target.Url, cancellationToken)
                    ? "Online"
                    : "Offline";
            }

            var executionState = executionTracker?.GetState((RoutingTarget)name);
            // The ACTIVE model entry is the FIRST loaded model; it drives BOTH the ActiveModel
            // name and the actual context value so the two columns can never disagree rows.
            var activeModelEntry = executionState?.LoadedModels.FirstOrDefault();
            var activeModel = activeModelEntry?.Name;
            var inFlight = executionState?.InFlightRequests ?? 0;

            // Task #45 (residency visibility): the FULL resident model list (tracker order,
            // [] when idle) and the best-effort daemon-observed keep-alive of the FIRST resident
            // model (null → omitted); snapshot-only reads, no new HTTP calls. Plus the
            // CONFIGURED (router-owned) effective preload policy + source (shared resolver).
            var residentModels = executionState?.LoadedModels is { } loaded
                ? loaded.Select(m => m.Name).ToArray()
                : [];
            var preloadResolution = PreloadKeepAlivePolicy.ResolvePreloadKeepAlive(target, options.CurrentValue, fleetHardware);

            var effectiveVram = target.GetEffectiveMaxVramMB(fleetHardware);
            var hardwareProfile = effectiveVram.HasValue
                ? (effectiveVram.Value > 1024 ? $"{Math.Round(effectiveVram.Value / 1024.0)} GB" : $"{effectiveVram.Value} MB")
                : "-";

            // Append the configured speed factor to the HW cell (e.g. "16gb,2.0x") only when it
            // is above the fleet baseline (1.0); the TUI HW column is 9-wide to hold "16gb,2.0x".
            var effectiveSpeedFactor = target.GetEffectiveSpeedFactor();
            if (effectiveSpeedFactor > 1.0)
            {
                hardwareProfile = $"{hardwareProfile},{effectiveSpeedFactor.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture)}x";
            }

            fleet.Add(new MonitorFleetTargetItem(
                Name: name,
                Url: target.Url,
                Enabled: enabled,
                Liveness: livenessText,
                ActiveModel: activeModel,
                InFlightRequests: inFlight,
                HardwareProfile: hardwareProfile,
                ContextAllocation: FormatActualContext(activeModelEntry?.ContextLength),
                Location: TargetLocationClassifier.ToWireValue(TargetLocationClassifier.Classify(target)),
                ResidentModels: residentModels,
                KeepAliveSeconds: preloadResolution.Seconds,
                KeepAliveSource: preloadResolution.Source,
                ActiveKeepAliveSeconds: activeModelEntry?.KeepAliveSeconds));

            periodByName[name] = statistics.Last7Days.GetValueOrDefault((RoutingTarget)name) ?? new ActivityStatisticsTotals(0, 0, 0);
        }

        // The Cloud pseudo-target is never probed/tracked: it is "Online" iff cloud is enabled,
        // never has a loaded model or in-flight requests, and never has a daemon-reported context.
        var cloudFlag = targets.Flags.TryGetValue(RoutingTarget.Cloud, out var cloudEnabledFlag) ? cloudEnabledFlag : cloudEnabled;
        fleet.Add(new MonitorFleetTargetItem(
            Name: RoutingTarget.Cloud.Value,
            Url: null,
            Enabled: cloudFlag,
            Liveness: cloudEnabled ? "Online" : "Offline",
            ActiveModel: null,
            InFlightRequests: 0,
            HardwareProfile: null,
            ContextAllocation: "-",
            Location: TargetLocationClassifier.ToWireValue(TargetLocation.Cloud),
            ResidentModels: [], // Cloud never hosts models (no policy, no observed keep-alive).
            KeepAliveSeconds: null,
            KeepAliveSource: null,
            ActiveKeepAliveSeconds: null));
        periodByName[RoutingTarget.Cloud.Value] = statistics.Last7Days.GetValueOrDefault(RoutingTarget.Cloud) ?? new ActivityStatisticsTotals(0, 0, 0);

        var todayByName = new Dictionary<string, ActivityStatisticsTotals>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in periodByName.Keys)
        {
            todayByName[name] = statistics.Today.GetValueOrDefault((RoutingTarget)name) ?? new ActivityStatisticsTotals(0, 0, 0);
        }
        todayByName["total"] = statistics.TodayTotal;
        periodByName["total"] = statistics.Last7DaysTotal;

        return new MonitorStateResponse(
            cloudEnabled,
            fleet,
            new MonitorStatisticsStatus(todayByName, periodByName),
            snapshot.InProgressRequests.Select(r => new MonitorInProgressItem(
                r.Target.ToString(),
                r.Model,
                r.EstimatedPromptTokens,
                (now - r.StartedAt).TotalMilliseconds,
                r.ReasoningEffort,
                r.SessionId,
                r.AgentId)).ToList(),
            snapshot.RecentRequests.Select(r => new MonitorRecentRequestItem(
                r.Timestamp,
                r.Target.ToString(),
                r.Model,
                r.EstimatedPromptTokens,
                r.ActualPromptTokens,
                r.ActualResponseTokens,
                r.ElapsedMilliseconds,
                r.StatusCode,
                r.Success,
                r.RawPromptTokens,
                r.ReasoningEffort,
                r.SessionId,
                r.AgentId,
                r.CachedInputTokens)).ToList(),
            CalculateCostStatus(statistics.Last7Days, options.CurrentValue.Pricing, effectiveTargets),
            includeOverhead
                ? CalculateOverheadStatus(snapshot.RecentRequests, options.CurrentValue.TokenEstimationOverheadFactor, options.CurrentValue.TokenEstimator)
                : null);
    }

    /// <summary>
    /// Renders the active model's daemon-reported context size in compact token format:
    /// < 1,000 → raw integer (e.g. "512"); < 1,000,000 → nearest whole k (e.g. 4,096 → "4k",
    /// 32,768 → "33k"); ≥ 1,000,000 → 1-decimal M (e.g. 1,310,720 → "1.3M"). InvariantCulture.
    /// Null (no model loaded, or the daemon did not report context_length) → "-" — the lease
    /// allocation is intentionally NOT used as a fallback.
    /// </summary>
    private static string FormatActualContext(int? tokens)
    {
        if (tokens is null or <= 0)
        {
            return "-";
        }

        var v = tokens.Value;

        if (v >= 1_000_000)
        {
            return (v / 1_000_000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "M";
        }
        if (v >= 1_000)
        {
            return Math.Round(v / 1000.0, 0, MidpointRounding.AwayFromZero).ToString(System.Globalization.CultureInfo.InvariantCulture) + "k";
        }
        return v.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Calculates the monitor cost status (ACTUAL savings since task #84): per non-Cloud target,
    /// the token cloud value (cached-token pricing included, <see cref="TokenCost"/>) MINUS the
    /// target's electricity cost
    /// <c>(elapsedMs / 3_600_000) × (watts / 1000) × ElectricityPricePerKwh</c>, with the
    /// per-target wattage resolution chain (target <c>PowerWatts</c> override →
    /// <c>Pricing:DefaultNodePowerWatts</c> → 150 W) and the effective electricity price
    /// (explicit <c>ElectricityPricePerKwh</c> → 0.20). Rounding contract (4-decimal
    /// everywhere, totals rounded LAST):
    /// <c>electricity4 = round(raw, 4)</c>, <c>cloudValue4 = round(TokenCost, 4)</c>,
    /// <c>savings4 = round(cloudValue4 − electricity4, 4)</c> (NEVER clamped — negative
    /// savings are reported as-is); <c>EstimatedSavings = round(Σ savings4, 4)</c> and
    /// <c>ElectricityCost = round(Σ electricity4, 4)</c> (Cloud excluded from both sums;
    /// <c>CloudOverflowCost</c> is untouched).
    /// Returns null (cost section hidden) unless at least one token price rate is configured
    /// (<see cref="PricingOptions.IsConfigured"/> — the electricity price alone never surfaces
    /// it).
    /// </summary>
    /// <param name="last7Days">Per-target token + GPU-time statistics of the last 7 days.</param>
    /// <param name="pricing">The live pricing options (rates, currency, electricity price,
    /// default node power).</param>
    /// <param name="targets">
    /// Optional effective fleet target list (name-matched case-insensitively for the
    /// per-target <c>PowerWatts</c> override). Null (unit-test callers) ⇒ the electricity term
    /// resolves against the pricing default / the 150 W fallback only (0 when there is no
    /// elapsed time).
    /// </param>
    public static MonitorCostStatus? CalculateCostStatus(
        IReadOnlyDictionary<RoutingTarget, ActivityStatisticsTotals> last7Days,
        PricingOptions? pricing,
        IReadOnlyList<OllamaTargetOptions>? targets = null)
    {
        if (pricing == null || !pricing.IsConfigured)
        {
            return null;
        }

        var effectiveRates = EffectiveRates(pricing);

        // Per-target ACTUAL savings: every NON-Cloud target's tokens valued at the effective
        // cloud prices (4-decimal rounding) minus its electricity cost (4-decimal rounding);
        // the Cloud overflow is excluded (it is a cost, not a saving). Cached input tokens (a
        // subset of the total input) are valued at the cached rate, so a prompt-cache hit
        // saves LESS against the cloud baseline.
        var perTargetSavings = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var perTargetElectricityCost = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var actualSavings = 0.0;
        var electricityCost = 0.0;
        foreach (var (target, stats) in last7Days)
        {
            if (target == RoutingTarget.Cloud)
            {
                continue;
            }

            var cloudValue = Math.Round(TokenCost(stats, effectiveRates.Input, effectiveRates.Cached, effectiveRates.Output), 4);
            var electricity = ElectricityCost(stats.ElapsedMilliseconds, ResolvePowerWatts(target, targets, pricing), effectiveRates.ElectricityKwh);
            // Never clamped: negative savings (electricity exceeding the cloud value) are
            // reported as-is.
            var savings = Math.Round(cloudValue - electricity, 4);
            perTargetSavings[target.Value] = savings;
            perTargetElectricityCost[target.Value] = electricity;
            actualSavings += savings;
            electricityCost += electricity;
        }

        var cloudStats = last7Days.GetValueOrDefault(RoutingTarget.Cloud) ?? new ActivityStatisticsTotals(0, 0, 0);
        var cloudOverflowCost = TokenCost(cloudStats, effectiveRates.Input, effectiveRates.Cached, effectiveRates.Output);

        return new MonitorCostStatus(
            pricing.Currency,
            Math.Round(actualSavings, 4),
            Math.Round(cloudOverflowCost, 4),
            perTargetSavings,
            Math.Round(electricityCost, 4),
            perTargetElectricityCost);
    }

    /// <summary>
    /// Resolves the four effective rate values from the live pricing options (extracted from
    /// <see cref="CalculateCostStatus"/> so the per-session endpoints reuse the EXACT same
    /// resolution chain): <c>input = PromptPricePerMillion ?? PricePerMillion ?? 0</c>,
    /// <c>output = CompletionPricePerMillion ?? PricePerMillion ?? 0</c>, <c>cached =
    /// CachedPromptPricePerMillion ?? input</c> (an unset cached rate values cached tokens at
    /// the regular input rate) and <c>electricityKwh = GetEffectiveElectricityPricePerKwh()</c>.
    /// </summary>
    internal static (double Input, double Cached, double Output, double ElectricityKwh) EffectiveRates(PricingOptions pricing)
    {
        var input = pricing.PromptPricePerMillion ?? pricing.PricePerMillion ?? 0.0;
        var output = pricing.CompletionPricePerMillion ?? pricing.PricePerMillion ?? 0.0;
        var cached = pricing.CachedPromptPricePerMillion ?? input;
        return (input, cached, output, pricing.GetEffectiveElectricityPricePerKwh());
    }

    /// <summary>
    /// Electricity cost of one target's cumulative GPU time (task #84 formula, 4-decimal
    /// rounding): <c>round((elapsedMs / 3_600_000) × (watts / 1000) × electricityPrice, 4)</c>.
    /// </summary>
    internal static double ElectricityCost(double elapsedMs, double watts, double electricityKwh) =>
        Math.Round(elapsedMs / 3_600_000.0 * watts / 1000.0 * electricityKwh, 4);

    /// <summary>
    /// Wattage resolution chain of one NON-Cloud statistics target (task #84, decision 6):
    /// the <see cref="OllamaTargetOptions.PowerWatts"/> of the name-matched (case-insensitive)
    /// configured target via its full chain (override → pricing default → 150 W); a statistics
    /// target ABSENT from the configured fleet (e.g. a removed target with 7-day history) —
    /// or a null fleet list — uses the default wattage (<see cref="PricingOptions.DefaultPowerWatts"/>
    /// / the pricing default when no fleet list is available).
    /// </summary>
    internal static double ResolvePowerWatts(
        RoutingTarget target,
        IReadOnlyList<OllamaTargetOptions>? targets,
        PricingOptions pricing)
    {
        if (targets is not null)
        {
            var match = targets.FirstOrDefault(t => string.Equals(t.Name, target.Value, StringComparison.OrdinalIgnoreCase));
            return match is not null ? match.GetEffectivePowerWatts(pricing) : PricingOptions.DefaultPowerWatts;
        }

        return pricing.GetEffectiveDefaultNodePowerWatts();
    }

    /// <summary>
    /// Values one target's token usage against the effective per-1M cloud rates:
    /// <c>(input − cached) × inputPrice + cached × cachedPrice + output × outputPrice</c>, with
    /// cached defensively clamped to the total input (never negative, never more than the input).
    /// Rounding is left to the caller (per-target 4-dec, totals rounded last).
    /// </summary>
    internal static double TokenCost(
        ActivityStatisticsTotals stats,
        double effectiveInputPrice,
        double effectiveCachedPrice,
        double effectiveOutputPrice)
    {
        var cached = Math.Clamp(stats.CachedInputTokens, 0, stats.InputTokens);
        return (stats.InputTokens - cached) / 1_000_000.0 * effectiveInputPrice
            + cached / 1_000_000.0 * effectiveCachedPrice
            + stats.OutputTokens / 1_000_000.0 * effectiveOutputPrice;
    }

    public static MonitorOverheadStatus CalculateOverheadStatus(
        IReadOnlyList<ActivityLogEntry> recentRequests,
        double configuredFactor,
        string estimator)
    {
        var validSamples = recentRequests
            .Where(r => r.Success && r.ActualPromptTokens.HasValue && r.ActualPromptTokens.Value > 0)
            .Select(r =>
            {
                int raw = r.RawPromptTokens.GetValueOrDefault(0);
                if (raw <= 0)
                {
                    raw = (int)Math.Round(r.EstimatedPromptTokens / (configuredFactor > 0 ? configuredFactor : 1.0));
                }
                return (r.Model, Raw: raw, Actual: r.ActualPromptTokens!.Value);
            })
            .Where(x => x.Raw > 0)
            .ToList();

        if (validSamples.Count == 0)
        {
            return new MonitorOverheadStatus(configuredFactor, estimator, null, 0, Array.Empty<MonitorOverheadRecommendation>());
        }

        var modelGroups = validSamples
            .GroupBy(x => x.Model)
            .Select(g =>
            {
                long sumRaw = g.Sum(x => (long)x.Raw);
                long sumActual = g.Sum(x => (long)x.Actual);
                double factor = sumRaw > 0 ? Math.Round((double)sumActual / sumRaw, 2) : 1.0;
                return new MonitorOverheadRecommendation(g.Key, factor, g.Count());
            })
            .OrderByDescending(m => m.SampleCount)
            .ToList();

        long totalRaw = validSamples.Sum(x => (long)x.Raw);
        long totalActual = validSamples.Sum(x => (long)x.Actual);
        double overallFactor = totalRaw > 0 ? Math.Round((double)totalActual / totalRaw, 2) : 1.0;

        return new MonitorOverheadStatus(configuredFactor, estimator, overallFactor, validSamples.Count, modelGroups);
    }
}
