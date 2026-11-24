using OllamaRouter.Options;
using OllamaRouter.Services.Benchmarking;
using OllamaRouter.Services.Hardware;

namespace OllamaRouter.Services.Recommendations;

/// <summary>
/// The native role recommendation engine (task #66, hardware-capability-aware since
/// task #168). Pure and deterministic (no I/O):
/// <list type="number">
/// <item><b>Input gate:</b> only targets with a TERMINAL benchmark status contribute
/// (<c>Succeeded</c>, <c>PartiallySucceeded</c>, <c>Failed</c>, <c>Cancelled</c>);
/// <c>Running</c> targets are excluded (their ladder is incomplete).</item>
/// <item><b>Per (catalog model × target):</b> the benchmark rows where <c>row.Model ==
/// model.Name</c> (Ordinal) and <c>row.Viable == true</c> (the runner pre-gates on the
/// frozen ≥ 20 t/s viability floor; exactly 20.0 is viable). No viable rows → the pair is
/// dropped. The largest viable row is the <i>ceiling row</i>, and the 128k role-eligibility
/// floor (task #168, <c>MinEligibleContextTokens</c> = 131 072) applies PER (model, node) to
/// <c>ceilingRow.NumCtx</c> regardless of architecture: a model can be eligible on one fleet
/// node and dropped on another, so a winner's <c>Nodes</c> list contains only its eligible
/// nodes. For MoE the ceiling row's NumCtx is exactly the <c>BaselineContext</c>, so the
/// "MoE baseline ≥ 131 072" invariant holds by construction.</item>
/// <item><b>Per-node ceiling:</b> dense → <c>ContextCeiling = max viable row NumCtx</c>
/// (evidence-driven — the ladder stops at the first non-viable row, so the largest viable
/// row IS the max safe context, and it may exceed the catalog native length),
/// <c>BaselineContext = null</c>; MoE → <c>ContextCeiling = model.NativeContext</c> (the
/// single extensible bucket, task #26), <c>BaselineContext = max viable row NumCtx</c>
/// (the optimal baseline). <c>TokensPerSecond</c> is that row's measured decode tps.</item>
/// <item><b>Role pools (task #168):</b> candidates are catalog models tagged with the role
/// that have ≥ 1 eligible node. The catalog <c>Roles</c> already encode per-model eligibility
/// (<c>Ornith-1.5-122b</c> ∉ <c>agentic_chat</c>; Expert models ∉ <c>basic</c> /
/// <c>junior_developer</c>; every MoE model carries all five roles as the universal
/// fallback). The engine additionally stages by model class: <c>basic</c> /
/// <c>junior_developer</c> → MoE candidates only (never a dense/122B Expert model); the
/// expert roles (<c>agentic_chat</c> / <c>software_architect</c> / <c>senior_developer</c>) →
/// the highest-tier eligible Expert model first, gracefully falling back to the MoE stage of
/// the same pool when no Expert model clears the 128k floor on any fleet node.</item>
/// <item><b>Winner comparator</b> (first non-tie wins): (1) <c>QualityRank</c> ascending —
/// the curated catalog encodes both the Expert order (Ornith 1 &gt; Qwen3.8-27B:latest 2
/// &gt; Qwen3.8-27B:iq3 3) and the MoE quant tier (Q8 = 4 &gt; Q4 = 5); (2) role-specific MoE
/// family preference (<c>Qwen3.6-35B-A3B</c> first for <c>basic</c> / <c>agentic_chat</c>;
/// <c>Flint</c> first for <c>junior_developer</c> / <c>software_architect</c> /
/// <c>senior_developer</c>; unknown families sort after both, deterministically);
/// (3) max-node <c>ContextCeiling</c> descending; (4) max-node <c>TokensPerSecond</c>
/// descending; (5) catalog declaration order (the pool is scanned in catalog order and the
/// winner is replaced only on a strict improvement).</item>
/// <item><b>Models index:</b> one <see cref="ModelNodeProfile"/> per distinct native model
/// that won at least one role (union node rows, catalog declaration order). A role with no
/// eligible candidate anywhere is ABSENT from <see cref="FleetRecommendations.Roles"/>.</item>
/// </list>
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

        // 2. Per (model × target) node profiles: the largest viable row is the ceiling row,
        // and it must clear the 128k role-eligibility floor (task #168, per (model, node)).
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

                if (ceilingRow is null ||
                    ceilingRow.NumCtx < CandidateModelCatalogOptions.MinEligibleContextTokens)
                {
                    // No viable rows, or the viable ceiling is below the 128k role floor →
                    // the (model, node) pair is dropped (task #168).
                    continue;
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

        // 3. Per role (task #168): class-staged candidate pools + the hardware-aware
        // comparator; the pool is scanned in catalog declaration order, so the final
        // catalog-order tie-break falls out of the stable winner scan.
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
            // Pool: catalog declaration order. The catalog Roles already encode the per-role
            // model eligibility (Ornith ∉ agentic_chat; Expert models ∉ basic/junior; every
            // MoE model carries all five roles as the universal fallback) — the class
            // staging below is the engine's addition (task #168).
            var pool = catalog.Where(m => m.Roles.Contains(usageRole)
                                          && modelNodes.TryGetValue(m.Name, out var n) && n.Count > 0)
                              .ToList();

            if (usageRole is UsageRole.BasicModel or UsageRole.JuniorDeveloper)
            {
                // basic / junior_developer NEVER take a dense/122B Expert model (safety net
                // over the catalog Roles).
                pool = pool.Where(m => m.ModelClass == CandidateModelClass.MoE).ToList();
            }
            else
            {
                // Expert roles: highest-tier eligible Expert model first; when no Expert
                // model has an eligible node anywhere, gracefully fall back to the MoE stage
                // of the same pool.
                var experts = pool.Where(m => m.ModelClass == CandidateModelClass.Expert).ToList();
                pool = experts.Count > 0
                    ? experts
                    : pool.Where(m => m.ModelClass == CandidateModelClass.MoE).ToList();
            }

            CandidateModel? winner = null;
            IReadOnlyList<NodeRoleProfile> winnerNodes = [];
            foreach (var model in pool)
            {
                if (winner is null ||
                    CompareForRole(model, modelNodes[model.Name], usageRole, winner!, winnerNodes) < 0)
                {
                    (winner, winnerNodes) = (model, modelNodes[model.Name]);
                }
            }

            if (winner is null)
            {
                continue; // No eligible candidate anywhere → role absent from the matrix.
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
    /// Role-specific MoE family preference (Ordinal match on <see cref="CandidateModel.Family"/>);
    /// first entry = preferred. Families not in the list get position = list length (they sort
    /// after the listed families, their relative order falling through to the ceiling / tps /
    /// catalog-order keys — deterministic).
    /// </summary>
    private static readonly IReadOnlyDictionary<UsageRole, string[]> MoEFamilyPreference =
        new Dictionary<UsageRole, string[]>
        {
            [UsageRole.BasicModel] = ["Qwen3.6-35B-A3B", "Flint"],
            [UsageRole.AgenticChat] = ["Qwen3.6-35B-A3B", "Flint"],
            [UsageRole.JuniorDeveloper] = ["Flint", "Qwen3.6-35B-A3B"],
            [UsageRole.SoftwareArchitect] = ["Flint", "Qwen3.6-35B-A3B"],
            [UsageRole.SeniorDeveloper] = ["Flint", "Qwen3.6-35B-A3B"],
        };

    /// <summary>
    /// The role comparator (task #168), first non-tie decides: (1) <c>QualityRank</c>
    /// ascending (catalog: Ornith 1 > Qwen3.8-27B:latest 2 > iq3 3; Q8 MoE 4 > Q4 MoE 5),
    /// (2) role-specific MoE family preference, (3) max-node <c>ContextCeiling</c> descending,
    /// (4) max-node <c>TokensPerSecond</c> descending. Catalog declaration order is the final
    /// tie-break, realized by the caller's stable winner scan (pool in catalog order, replace
    /// only on a strict improvement). <returns>Negative when the candidate beats the winner
    /// (the ascending keys compare candidate − winner; the descending keys, winner −
    /// candidate, so that "higher wins" also yields a negative result).</returns>
    /// </summary>
    private static int CompareForRole(
        CandidateModel candidate, IReadOnlyList<NodeRoleProfile> candidateNodes,
        UsageRole usageRole, CandidateModel winner, IReadOnlyList<NodeRoleProfile> winnerNodes)
    {
        var byQuality = candidate.QualityRank - winner.QualityRank;
        if (byQuality != 0)
        {
            return byQuality;
        }

        var byFamily = FamilyPosition(usageRole, candidate.Family) - FamilyPosition(usageRole, winner.Family);
        if (byFamily != 0)
        {
            return byFamily;
        }

        var byCeiling = winnerNodes.Max(n => n.ContextCeiling) - candidateNodes.Max(n => n.ContextCeiling);
        if (byCeiling != 0)
        {
            return byCeiling;
        }

        return winnerNodes.Max(n => n.TokensPerSecond)
            .CompareTo(candidateNodes.Max(n => n.TokensPerSecond));
    }

    /// <summary>
    /// The family's index in the role's ordered preference list, or the list length for
    /// unknown families (sorts after the listed families).
    /// </summary>
    private static int FamilyPosition(UsageRole role, string family)
    {
        var list = MoEFamilyPreference[role];
        var index = list.AsSpan().IndexOf(family, StringComparer.Ordinal);
        return index < 0 ? list.Length : index;
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
