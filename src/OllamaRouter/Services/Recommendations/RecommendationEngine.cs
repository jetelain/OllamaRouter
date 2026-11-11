using OllamaRouter.Options;
using OllamaRouter.Services.Benchmarking;
using OllamaRouter.Services.Hardware;

namespace OllamaRouter.Services.Recommendations;

/// <summary>
/// The native role recommendation engine (task #66). Pure and deterministic (no I/O):
/// <list type="number">
/// <item><b>Input gate:</b> only targets with a TERMINAL benchmark status contribute
/// (<c>Succeeded</c>, <c>PartiallySucceeded</c>, <c>Failed</c>, <c>Cancelled</c>);
/// <c>Running</c> targets are excluded (their ladder is incomplete).</item>
/// <item><b>Per (catalog model × target):</b> the benchmark rows where <c>row.Model ==
/// model.Name</c> (Ordinal) and <c>row.Viable == true</c> (the runner pre-gates on the
/// frozen ≥ 20 t/s viability floor; exactly 20.0 is viable). No viable rows → the pair is
/// dropped.</item>
/// <item><b>Per-node ceiling:</b> dense → <c>ContextCeiling = max viable row NumCtx</c>
/// (evidence-driven — the ladder stops at the first non-viable row, so the largest viable
/// row IS the max safe context, and it may exceed the catalog native length),
/// <c>BaselineContext = null</c>; MoE → <c>ContextCeiling = model.NativeContext</c> (the
/// single extensible bucket, task #26), <c>BaselineContext = max viable row NumCtx</c>
/// (the optimal baseline). <c>TokensPerSecond</c> is that row's measured decode tps.</item>
/// <item><b>Per role:</b> candidates are catalog models tagged with the role that have ≥ 1
/// node. The winner: (1) highest max-node ContextCeiling, (2) highest best
/// TokensPerSecond, (3) catalog declaration order. A role with no viable candidate anywhere
/// is ABSENT from <see cref="FleetRecommendations.Roles"/>.</item>
/// <item><b>Models index:</b> one <see cref="ModelNodeProfile"/> per distinct native model
/// that won at least one role (union node rows, catalog order).</item>
/// </list>
/// With the default seed catalog the multi-role unified hot cache falls out for free:
/// <c>agentic_chat</c> / <c>software_architect</c> / <c>senior_developer</c> all resolve to
/// the identical native <c>qwen3.8:27b</c> (one resident model, zero evictions).
/// </summary>
public sealed class RecommendationEngine : IRecommendationEngine
{
    /// <inheritdoc/>
    public FleetRecommendations Generate(
        IReadOnlyList<BenchmarkTargetResult> benchmarkResults,
        IReadOnlyList<CandidateModel> catalog,
        Func<string, NodeHardwareProfile?>? hardwareProfileLookup = null,
        DateTimeOffset? generatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(benchmarkResults);
        ArgumentNullException.ThrowIfNull(catalog);

        // 1. Input gate: terminal-status targets only, in result (queue) order, deduplicated.
        var orderedTargets = new List<string>();
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targetResults = new Dictionary<string, BenchmarkTargetResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in benchmarkResults)
        {
            if (result is null ||
                result.Status is BenchmarkRunStatus.Running or BenchmarkRunStatus.NotStarted)
            {
                continue;
            }

            if (seenTargets.Add(result.Target))
            {
                orderedTargets.Add(result.Target);
                targetResults[result.Target] = result;
            }
        }

        // 2. Per (model × target) node profiles: the largest viable row is the ceiling row.
        // Dictionary<string, IReadOnlyList<NodeRoleProfile>> per model, nodes in fleet order.
        var modelNodes = new Dictionary<string, List<NodeRoleProfile>>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in orderedTargets)
        {
            var vramMB = LookupVramMB(target, hardwareProfileLookup);
            foreach (var model in catalog)
            {
                BenchmarkContextResult? ceilingRow = null;
                foreach (var row in targetResults[target].Results)
                {
                    if (!row.Viable || !string.Equals(row.Model, model.Name, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // The ladder is ascending, so the LAST viable row is the max-NumCtx row.
                    if (ceilingRow is null || row.NumCtx > ceilingRow.NumCtx)
                    {
                        ceilingRow = row;
                    }
                }

                if (ceilingRow is null)
                {
                    continue; // No viable rows for this (model, node) pair → dropped.
                }

                var isMoe = string.Equals(model.Architecture, "MoE", StringComparison.OrdinalIgnoreCase);
                var profile = new NodeRoleProfile(
                    Target: target,
                    ContextCeiling: isMoe ? model.NativeContext : ceilingRow.NumCtx,
                    BaselineContext: isMoe ? ceilingRow.NumCtx : null,
                    TokensPerSecond: ceilingRow.DecodeTokensPerSecond,
                    VramMB: vramMB);

                if (!modelNodes.TryGetValue(model.Name, out var nodes))
                {
                    nodes = new List<NodeRoleProfile>();
                    modelNodes[model.Name] = nodes;
                }

                nodes.Add(profile);
            }
        }

        // 3. Per role: winner by (1) max-node ceiling, (2) best tps, (3) catalog order.
        var roles = new Dictionary<string, RoleRecommendation>(StringComparer.OrdinalIgnoreCase);
        var models = new Dictionary<string, ModelNodeProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var usageRole in new[]
        {
            UsageRole.BasicModel,
            UsageRole.AgenticChat,
            UsageRole.SoftwareArchitect,
            UsageRole.SeniorDeveloper,
            UsageRole.JuniorDeveloper
        })
        {
            CandidateModel? winner = null;
            IReadOnlyList<NodeRoleProfile> winnerNodes = [];
            foreach (var model in catalog)
            {
                if (!model.Roles.Contains(usageRole) ||
                    !modelNodes.TryGetValue(model.Name, out var nodes) ||
                    nodes.Count == 0)
                {
                    continue;
                }

                if (winner is null)
                {
                    winner = model;
                    winnerNodes = nodes;
                    continue;
                }

                var candidateMax = nodes.Max(n => n.ContextCeiling);
                var winnerMax = winnerNodes.Max(n => n.ContextCeiling);
                if (candidateMax > winnerMax)
                {
                    (winner, winnerNodes) = (model, nodes);
                    continue;
                }

                if (candidateMax == winnerMax && nodes.Max(n => n.TokensPerSecond) > winnerNodes.Max(n => n.TokensPerSecond))
                {
                    (winner, winnerNodes) = (model, nodes);
                }
            }

            if (winner is null)
            {
                continue; // No viable candidate anywhere → role absent from the matrix.
            }

            roles[RoleAlias.ToAlias(usageRole)] = new RoleRecommendation(
                Role: RoleAlias.ToAlias(usageRole),
                Model: winner.Name,
                RecommendedContextWindow: winnerNodes.Max(n => n.ContextCeiling),
                Description: RoleDescription(usageRole),
                Nodes: winnerNodes);

            // Models index: catalog declaration order (first role win registers the model).
            if (!models.ContainsKey(winner.Name))
            {
                models[winner.Name] = new ModelNodeProfile(
                    Model: winner.Name,
                    Architecture: winner.Architecture,
                    NativeContext: winner.NativeContext,
                    Quantization: winner.Quantization,
                    Nodes: winnerNodes);
            }
        }

        return new FleetRecommendations(
            GeneratedAt: generatedAt ?? DateTimeOffset.UtcNow,
            Roles: roles,
            Models: models);
    }

    /// <summary>
    /// The node's fast memory (MB) from the hardware profile; null when no profile is
    /// known (fail-open — the ceiling evidence is the benchmark ladder, not VRAM).
    /// </summary>
    private static int? LookupVramMB(string target, Func<string, NodeHardwareProfile?>? hardwareProfileLookup)
    {
        if (hardwareProfileLookup is null)
        {
            return null;
        }

        try
        {
            var profile = hardwareProfileLookup(target);
            return profile is { TotalFastMemoryMB: > 0 } ? (int)profile.TotalFastMemoryMB : null;
        }
        catch
        {
            return null; // Fail-open: a broken lookup never breaks generation.
        }
    }

    private static string RoleDescription(UsageRole role) => role switch
    {
        UsageRole.BasicModel => "General-purpose everyday assistant: fast, low-cost chat for casual Q&A and light tasks.",
        UsageRole.AgenticChat => "Agentic chat: multi-turn, tool-calling and reasoning-heavy interactive sessions.",
        UsageRole.SoftwareArchitect => "Software architect: system design, API design, trade-off analysis and codebase-wide reasoning.",
        UsageRole.SeniorDeveloper => "Senior developer: complex feature implementation, refactoring and debugging across large codebases.",
        UsageRole.JuniorDeveloper => "Junior developer: small, well-scoped coding tasks and quick question answering.",
        _ => "General-purpose assistant."
    };
}
