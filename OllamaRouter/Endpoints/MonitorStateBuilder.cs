using Microsoft.Extensions.Options;
using OllamaRouter.Options;
using OllamaRouter.Services;
using OllamaRouter.Services.Hardware;

namespace OllamaRouter.Endpoints;

public static partial class MonitorEndpointsExtensions
{
    public static async Task<MonitorStateResponse> BuildMonitorStateResponseAsync(
        IActivityMonitorService activityMonitor,
        IActivityStatisticsService activityStatistics,
        IOptions<OllamaRouterOptions> options,
        ITargetAvailabilityService targetAvailability,
        IModelCatalogCacheService modelCatalogCache,
        CancellationToken cancellationToken = default,
        bool includeOverhead = true,
        ITargetLivenessService? liveness = null,
        ITargetExecutionTracker? executionTracker = null,
        IContextAutoShrinkManager? contextShrinkManager = null,
        IFleetHardwareService? fleetHardware = null)
    {
        var snapshot = activityMonitor.GetSnapshot();
        var statistics = activityStatistics.GetSnapshot();
        var now = DateTimeOffset.UtcNow;
        var cloudEnabled = options.Value.IsCloudOverflowConfigured();
        var targets = targetAvailability.GetSnapshot();

        var effectiveTargets = options.Value.GetEffectiveTargets();
        var livenessEntries = liveness?.GetEntries();
        var leaseSnapshot = contextShrinkManager?.GetLeaseSnapshot();

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
            var activeModel = executionState?.LoadedModels.FirstOrDefault()?.Name;
            var inFlight = executionState?.InFlightRequests ?? 0;

            var effectiveVram = target.GetEffectiveMaxVramMB(fleetHardware);
            var hardwareProfile = effectiveVram.HasValue
                ? (effectiveVram.Value % 1024 == 0 ? $"{effectiveVram.Value / 1024}gb" : $"{effectiveVram.Value} MB")
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
                ContextAllocation: FormatContextAllocation((RoutingTarget)name, leaseSnapshot),
                Location: TargetLocationClassifier.ToWireValue(TargetLocationClassifier.Classify(target))));

            periodByName[name] = statistics.Last7Days.GetValueOrDefault((RoutingTarget)name) ?? new ActivityStatisticsTotals(0, 0, 0);
        }

        // The Cloud pseudo-target is never probed/tracked: it is "Online" iff cloud is enabled,
        // never has a loaded model or in-flight requests, and never carries a context lease.
        var cloudFlag = targets.Flags.TryGetValue(RoutingTarget.Cloud, out var cloudEnabledFlag) ? cloudEnabledFlag : cloudEnabled;
        fleet.Add(new MonitorFleetTargetItem(
            Name: RoutingTarget.Cloud.Value,
            Url: null,
            Enabled: cloudFlag,
            Liveness: cloudEnabled ? "Online" : "Offline",
            ActiveModel: null,
            InFlightRequests: 0,
            HardwareProfile: null,
            ContextAllocation: "baseline",
            Location: TargetLocationClassifier.ToWireValue(TargetLocation.Cloud)));
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
                (now - r.StartedAt).TotalMilliseconds)).ToList(),
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
                r.RawPromptTokens)).ToList(),
            CalculateCostStatus(statistics.Last7Days, options.Value.Pricing),
            includeOverhead
                ? CalculateOverheadStatus(snapshot.RecentRequests, options.Value.TokenEstimationOverheadFactor, options.Value.TokenEstimator)
                : null);
    }

    // Renders the current context allocation for one fleet target from the lease snapshot:
    // any expanded lease -> "<maxBucket>k (expanded)"; a single lease at baseline ->
    // "<bucket>k (baseline)"; no leases at all -> "-" (baseline implied). Cloud is "baseline".
    private static string FormatContextAllocation(
        RoutingTarget target,
        IReadOnlyDictionary<RoutingTarget, IReadOnlyList<ContextLeaseView>>? leaseSnapshot)
    {
        if (target == RoutingTarget.Cloud || leaseSnapshot is null ||
            !leaseSnapshot.TryGetValue(target, out var leases) || leases.Count == 0)
        {
            return target == RoutingTarget.Cloud ? "baseline" : "-";
        }

        if (leases.Any(lease => lease.IsExpanded))
        {
            return $"{FormatBucketTokens(leases.Max(lease => lease.ActiveBucket))} (expanded)";
        }

        return $"{FormatBucketTokens(leases[0].ActiveBucket)} (baseline)";
    }

    private static string FormatBucketTokens(int tokens)
    {
        if (tokens >= 1_000_000)
        {
            return (tokens / 1_000_000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "M";
        }
        return tokens >= 1_000
            ? (tokens / 1_000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "k"
            : tokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static MonitorCostStatus? CalculateCostStatus(
        IReadOnlyDictionary<RoutingTarget, ActivityStatisticsTotals> last7Days,
        PricingOptions? pricing)
    {
        if (pricing == null || !pricing.IsConfigured)
        {
            return null;
        }

        var effectiveInputPrice = pricing.PromptPricePerMillion ?? pricing.PricePerMillion ?? 0.0;
        var effectiveOutputPrice = pricing.CompletionPricePerMillion ?? pricing.PricePerMillion ?? 0.0;

        // Per-target savings: every NON-Cloud target's tokens valued at the effective cloud
        // prices (the same 4-decimal rounding as the totals); the Cloud overflow is excluded
        // (it is a cost, not a saving).
        var perTargetSavings = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var estimatedSavings = 0.0;
        foreach (var (target, stats) in last7Days)
        {
            if (target == RoutingTarget.Cloud)
            {
                continue;
            }

            var targetSavings = (stats.InputTokens / 1_000_000.0 * effectiveInputPrice) +
                                (stats.OutputTokens / 1_000_000.0 * effectiveOutputPrice);
            targetSavings = Math.Round(targetSavings, 4);
            perTargetSavings[target.Value] = targetSavings;
            estimatedSavings += targetSavings;
        }

        var cloudStats = last7Days.GetValueOrDefault(RoutingTarget.Cloud) ?? new ActivityStatisticsTotals(0, 0, 0);
        var cloudOverflowCost = (cloudStats.InputTokens / 1_000_000.0 * effectiveInputPrice) +
                                (cloudStats.OutputTokens / 1_000_000.0 * effectiveOutputPrice);

        return new MonitorCostStatus(
            pricing.Currency,
            Math.Round(estimatedSavings, 4),
            Math.Round(cloudOverflowCost, 4),
            perTargetSavings);
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
