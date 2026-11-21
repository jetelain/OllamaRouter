using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OllamaRouter.Client;
using OllamaRouter.Endpoints.RouterApi.Recommendations;
using OllamaRouter.Services.Recommendations;

namespace OllamaRouter.App.ViewModels.Wizard;

/// <summary>
/// One node row of a role's recommendation of Step 5 (task #103): the per-target context
/// ceiling, the measured throughput and the VRAM.
/// </summary>
public sealed class RecommendationNodeRow
{
    public required string Target { get; init; }
    public int ContextCeiling { get; init; }
    public double TokensPerSecond { get; init; }
    public int? VramMib { get; init; }
}

/// <summary>
/// One role's recommendation row of Step 5 (task #103). <see cref="Benchmarked"/> is
/// <c>false</c> when the role is ABSENT from the matrix (no viable candidate benchmarked yet) —
/// the UI then shows the "run Step 4 benchmark" hint.
/// </summary>
public sealed class RecommendationRoleRow
{
    public required string Role { get; init; }
    public string? Model { get; init; }
    public int? RecommendedContextWindow { get; init; }
    public string? Description { get; init; }
    public bool Benchmarked { get; init; }
    public IReadOnlyList<RecommendationNodeRow> Nodes { get; init; } = [];

    public string ContextText => RecommendedContextWindow is { } c ? $"{c} ctx" : "\u2014";
    public string NodesText => Nodes.Count == 0
        ? string.Empty
        : string.Join(", ", Nodes.Select(n => $"{n.Target}: {n.ContextCeiling} ctx @ {n.TokensPerSecond:0.0} t/s"));
}

/// <summary>
/// One native-model row of the Step 5 models index (task #103): architecture, native context,
/// quantization and the node count.
/// </summary>
public sealed class RecommendationModelRow
{
    public required string Model { get; init; }
    public required string Architecture { get; init; }
    public int NativeContext { get; init; }
    public string Quantization { get; init; } = string.Empty;
    public int NodeCount { get; init; }

    public string NodesText => $"{NodeCount} node(s)";
}

/// <summary>
/// Step 5 — Role Recommendations (task #103, spec §3.5; renumbered by task #151): the live matrix read, the generate
/// (<c>POST .../generate</c>, 409 ⇒ "benchmark first" hint) and the idempotent apply
/// (<c>POST .../apply</c>) — all through the shared client SDK.
/// </summary>
public sealed partial class WizardRecommendationsStepViewModel : ObservableObject, IWizardStep
{
    /// <summary>The stable 5-alias key order (task #66 vocabulary).</summary>
    private static readonly string[] RoleOrder =
        ["basic", "agentic_chat", "software_architect", "senior_developer", "junior_developer"];

    private readonly Func<IOllamaRouterApiClient> _clientAccessor;
    private readonly Action? _goToBenchmark;
    private bool _canGoToBenchmark;
    private bool _disposed;

    public WizardRecommendationsStepViewModel(
        Func<IOllamaRouterApiClient> clientAccessor,
        Action? goToBenchmark = null)
    {
        _clientAccessor = clientAccessor ?? throw new ArgumentNullException(nameof(clientAccessor));
        _goToBenchmark = goToBenchmark;
    }

    /// <summary>The five role rows in the stable alias order (absent roles show the hint).</summary>
    public ObservableCollection<RecommendationRoleRow> RoleRows { get; } = [];

    /// <summary>The native-model index rows.</summary>
    public ObservableCollection<RecommendationModelRow> ModelRows { get; } = [];

    /// <summary>The per-target max ceilings reported by the last apply (empty until applied).</summary>
    public ObservableCollection<RouterRecommendationsAppliedTarget> AppliedTargets { get; } = [];

    [ObservableProperty]
    private DateTimeOffset? _generatedAt;

    [ObservableProperty]
    private int _rolesAssigned;

    [ObservableProperty]
    private int _nodesConfigured;

    [ObservableProperty]
    private int _totalContextCeilings;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>One-line generated-counts summary (raises <see cref="PropertyChanged"/> below).</summary>
    public string SummaryText => RolesAssigned > 0
        ? $"{RolesAssigned} role(s), {NodesConfigured} node(s), {TotalContextCeilings} context ceiling(s)"
        : string.Empty;

    /// <summary>Human timestamp of the last generation (raises <see cref="PropertyChanged"/> below).</summary>
    public string GeneratedAtText => GeneratedAt is { } t
        ? $"Generated {t:yyyy-MM-dd HH:mm:ss} UTC"
        : "Not generated yet";

    partial void OnRolesAssignedChanged(int value) => OnPropertyChanged(nameof(SummaryText));

    partial void OnNodesConfiguredChanged(int value) => OnPropertyChanged(nameof(SummaryText));

    partial void OnTotalContextCeilingsChanged(int value) => OnPropertyChanged(nameof(SummaryText));

    partial void OnGeneratedAtChanged(DateTimeOffset? value) => OnPropertyChanged(nameof(GeneratedAtText));

    /// <summary>
    /// Reads the live matrix (read-only; keyless in default mode) and renders it. The router
    /// may be DOWN (the canonical first-run state before Step 1 has started it): failures
    /// surface as a <see cref="Message"/> instead of throwing, because this refresh is awaited
    /// by the root navigation command (<c>OnStepEnteredAsync</c>) — an escaping exception would
    /// fault the async-void command bridge and crash the app.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var matrix = await _clientAccessor().GetRecommendationsAsync(ct).ConfigureAwait(false);
            LoadMatrix(matrix);
        }
        catch (OllamaRouterApiException ex)
        {
            Message = $"Router error {(int)ex.Status}: {ex.Error ?? "(no error body)"}";
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            Message = $"Connection failed: {ex.Message}";
        }
    }

    /// <summary>Recomputes + persists + activates the matrix from the benchmark results.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task Generate()
    {
        IsBusy = true;
        try
        {
            var response = await _clientAccessor().GenerateRecommendationsAsync().ConfigureAwait(false);
            GeneratedAt = response.GeneratedAt;
            RolesAssigned = response.Recommendations.Roles.Count;
            NodesConfigured = response.Targets.Count;
            TotalContextCeilings = response.Recommendations.Roles.Values.Sum(r => r.Nodes.Count);
            LoadMatrix(response.Recommendations);
            Message = $"Generated {RolesAssigned} role assignment(s) across {NodesConfigured} node(s) " +
                      $"({TotalContextCeilings} context ceiling(s)).";
        }
        catch (OllamaRouterApiException ex) when (ex.Status == HttpStatusCode.Conflict)
        {
            Message = "No benchmark results available — run the Step 4 micro-benchmark first";
            _canGoToBenchmark = true;
            GoToBenchmarkCommand.NotifyCanExecuteChanged();
        }
        catch (OllamaRouterApiException ex)
        {
            Message = $"Router error {(int)ex.Status}: {ex.Error ?? "(no error body)"}";
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            Message = $"Connection failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Idempotently re-applies the current matrix, reporting the enforced ceilings.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task Apply()
    {
        IsBusy = true;
        try
        {
            var response = await _clientAccessor().ApplyRecommendationsAsync().ConfigureAwait(false);
            GeneratedAt = response.GeneratedAt;
            AppliedTargets.Clear();
            foreach (var target in response.Targets)
            {
                AppliedTargets.Add(target);
            }

            Message = response.Targets is null || response.Targets.Count == 0
                ? "Applied (no per-target ceilings)."
                : "Applied: " + string.Join(", ", response.Targets.Select(t => $"{t.Target} → {t.MaxContextCeiling}"));
        }
        catch (OllamaRouterApiException ex) when (ex.Status == HttpStatusCode.Conflict)
        {
            Message = "No recommendations to apply — generate them first.";
        }
        catch (OllamaRouterApiException ex)
        {
            Message = $"Router error {(int)ex.Status}: {ex.Error ?? "(no error body)"}";
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            Message = $"Connection failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Jumps back to Step 4 (the micro-benchmark; renumbered by task #151) after a generate/apply
    /// 409 — bound to <c>SetupWizardViewModel.SelectedStep = 3</c> by the composition root.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGoToBenchmark))]
    private void GoToBenchmark() => _goToBenchmark?.Invoke();

    private bool CanAct() => !IsBusy;

    private bool CanGoToBenchmark() => _canGoToBenchmark;

    private void LoadMatrix(FleetRecommendations matrix)
    {
        GeneratedAt = matrix.GeneratedAt;

        RoleRows.Clear();
        foreach (var role in RoleOrder)
        {
            if (matrix.Roles.TryGetValue(role, out var rec))
            {
                RoleRows.Add(new RecommendationRoleRow
                {
                    Role = role,
                    Model = rec.Model,
                    RecommendedContextWindow = rec.RecommendedContextWindow,
                    Description = rec.Description,
                    Benchmarked = true,
                    Nodes = rec.Nodes.Select(n => new RecommendationNodeRow
                    {
                        Target = n.Target,
                        ContextCeiling = n.ContextCeiling,
                        TokensPerSecond = n.TokensPerSecond,
                        VramMib = n.VramMB,
                    }).ToList(),
                });
            }
            else
            {
                RoleRows.Add(new RecommendationRoleRow
                {
                    Role = role,
                    Model = null,
                    RecommendedContextWindow = null,
                    Description = "Not benchmarked yet — run the Step 4 micro-benchmark.",
                    Benchmarked = false,
                    Nodes = [],
                });
            }
        }

        ModelRows.Clear();
        foreach (var (model, profile) in matrix.Models.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            ModelRows.Add(new RecommendationModelRow
            {
                Model = profile.Model,
                Architecture = profile.Architecture,
                NativeContext = profile.NativeContext,
                Quantization = profile.Quantization,
                NodeCount = profile.Nodes.Count,
            });
        }
    }

    /// <summary>Step lifecycle: refresh the matrix on entry.</summary>
    public Task OnStepEnteredAsync(CancellationToken ct = default) => RefreshAsync(ct);

    /// <summary>Step lifecycle: nothing to stop (no poller).</summary>
    public Task OnStepLeftAsync() => Task.CompletedTask;

    partial void OnIsBusyChanged(bool value)
    {
        GenerateCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
