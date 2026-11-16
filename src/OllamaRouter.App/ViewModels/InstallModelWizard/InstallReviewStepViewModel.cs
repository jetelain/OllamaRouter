using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OllamaRouter.App.ViewModels.Wizard;
using OllamaRouter.Client;
using OllamaRouter.Endpoints.RouterApi.Models;

namespace OllamaRouter.App.ViewModels.InstallModelWizard;

/// <summary>
/// The lifecycle state of ONE target's install run row (task #125): the wizard runs the
/// selected targets SEQUENTIALLY (one target failing does NOT stop the remaining ones —
/// independent installs; the overall result is Succeeded / Partial / Failed / Cancelled).
/// </summary>
public enum InstallRunStatus
{
    Pending,
    Running,
    Present,
    Succeeded,
    Failed,
    Cancelled
}

/// <summary>
/// One target's run row of Step 2 — Review &amp; Install (task #125): the summary (model,
/// alias, context, liveness, already-present) + the live per-target state (status text +
/// the byte-based progress bar from <c>completedBytes/totalBytes</c>; indeterminate for
/// the alias stage and for daemons that report no totals).
/// </summary>
public sealed partial class InstallRunRowVm : ObservableObject
{
    public InstallRunRowVm(
        string target,
        string model,
        string? alias,
        int? context,
        bool present,
        string liveness)
    {
        Target = target;
        Model = model;
        Alias = alias;
        Context = context;
        Present = present;
        Liveness = liveness;
        Summary = BuildSummary(model, alias, context, present, liveness);
    }

    public string Target { get; }

    public string Model { get; }

    public string? Alias { get; private set; }

    public int? Context { get; }

    public bool Present { get; }

    public string Liveness { get; }

    /// <summary>The one-line review summary (model + alias + context + liveness + present).</summary>
    public string Summary { get; private set; }

    [ObservableProperty]
    private InstallRunStatus _status = InstallRunStatus.Pending;

    [ObservableProperty]
    private string _statusText = "pending";

    /// <summary>The progress bar value 0–100 (or &lt; 0 = indeterminate: no daemon byte
    /// totals, or the alias stage).</summary>
    [ObservableProperty]
    private double _progressValue = -1;

    [ObservableProperty]
    private string? _error;

    public bool ProgressIndeterminate => ProgressValue < 0;

    partial void OnProgressValueChanged(double value) => OnPropertyChanged(nameof(ProgressIndeterminate));

    public void SetRunning()
    {
        Status = InstallRunStatus.Running;
        StatusText = "pulling…";
        ProgressValue = -1;
        Error = null;
    }

    public void SetPresent()
    {
        Status = InstallRunStatus.Present;
        StatusText = "already present on the target";
        ProgressValue = 100;
    }

    public void SetSucceeded(string? alias)
    {
        if (alias is not null)
        {
            Alias = alias;
            Summary = BuildSummary(Model, alias, Context, Present, Liveness);
        }

        Status = InstallRunStatus.Succeeded;
        StatusText = alias is null ? "installed" : $"installed (alias {alias})";
        ProgressValue = 100;
    }

    public void SetFailed(string? error)
    {
        Status = InstallRunStatus.Failed;
        StatusText = error ?? "failed";
        Error = error;
        ProgressValue = -1;
    }

    public void SetCancelled()
    {
        Status = InstallRunStatus.Cancelled;
        StatusText = "cancelled";
        ProgressValue = -1;
    }

    /// <summary>
    /// Applies one streamed progress item: pull-stage lines update the status text + the
    /// byte-based percentage (a "present" status flips the row to Present), alias-stage
    /// lines show the alias lifecycle (indeterminate bar).
    /// </summary>
    public void ApplyProgress(OllamaRouterModelPullProgress progress)
    {
        if (progress.Stage == "alias")
        {
            StatusText = progress.Status == "created"
                ? $"alias {progress.Alias} created (ctx {progress.Context})"
                : $"creating alias {progress.Alias} (ctx {progress.Context})…";
            ProgressValue = -1;
            return;
        }

        if (progress.Status == "present")
        {
            SetPresent();
            return;
        }

        StatusText = progress.Status;
        ProgressValue = progress.TotalBytes is { } total and > 0 && progress.CompletedBytes is { } completed
            ? Math.Min(100.0, completed * 100.0 / total)
            : -1;
    }

    private static string BuildSummary(string model, string? alias, int? context, bool present, string liveness)
    {
        var parts = new List<string> { model };
        if (alias is not null)
        {
            parts.Add($"alias {alias}");
        }

        if (context is { } ctx)
        {
            parts.Add($"ctx {ctx}");
        }

        parts.Add(liveness);
        if (present)
        {
            parts.Add("already present");
        }

        return string.Join(" · ", parts);
    }
}

/// <summary>
/// Step 2 — Review &amp; Install of the Install Model wizard (task #125): the per-target
/// summary rows (built from the step 1 plan on entry), the SEQUENTIAL install loop over
/// <c>client.PullModelAsync</c> (one failing target does NOT stop the remaining ones;
/// cancellation marks the current + remaining rows Cancelled — no phantom success), the
/// live per-target progress (marshaled to the UI thread by the <c>Progress&lt;T&gt;</c>
/// wrapper — in headless tests there is no sync context and the handler runs inline) and
/// the overall result (Succeeded / Partial / Failed / Cancelled) gating the Done button.
/// </summary>
public sealed partial class InstallReviewStepViewModel : ObservableObject, IWizardStep
{
    private readonly Func<IOllamaRouterApiClient> _clientAccessor;
    private readonly Func<InstallTargetsStepViewModel> _targets;
    private readonly Func<string> _modelReference;
    private CancellationTokenSource? _runCts;

    public InstallReviewStepViewModel(
        Func<IOllamaRouterApiClient> clientAccessor,
        Func<InstallTargetsStepViewModel> targets,
        Func<string> modelReference)
    {
        _clientAccessor = clientAccessor ?? throw new ArgumentNullException(nameof(clientAccessor));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _modelReference = modelReference ?? throw new ArgumentNullException(nameof(modelReference));
    }

    /// <summary>The per-target run rows (review summary + live install state).</summary>
    public ObservableCollection<InstallRunRowVm> Rows { get; } = [];

    /// <summary>Whether an install run is in flight (the window + navigation gate on this).</summary>
    [ObservableProperty]
    private bool _isRunning;

    /// <summary>Whether a run COMPLETED (success / partial / failed / cancelled) — the Done
    /// button gate (before the first run the wizard is closed via Cancel).</summary>
    [ObservableProperty]
    private bool _runCompleted;

    /// <summary>The overall result text (null before the first run: "Succeeded" / "Partial" /
    /// "Failed" / "Cancelled").</summary>
    [ObservableProperty]
    private string? _runResultText;

    /// <summary>Starts the SEQUENTIAL install of every review row (one target at a time, in
    /// plan order; a failure does not stop the remaining targets).</summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task Install()
    {
        if (IsRunning || Rows.Count == 0)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _runCts = cts;
        IsRunning = true;
        RunCompleted = false;
        RunResultText = null;

        var client = _clientAccessor();
        var succeeded = 0;
        var failed = 0;
        var cancelled = false;

        foreach (var row in Rows)
        {
            // Cancellation between targets: the current target never started → Cancelled.
            if (cts.Token.IsCancellationRequested)
            {
                row.SetCancelled();
                cancelled = true;
                continue;
            }

            row.SetRunning();

            // Progress<T> posts to the capturing (UI) synchronization context; in headless
            // unit tests there is no sync context and the handler runs inline.
            IProgress<OllamaRouterModelPullProgress>? progress = new Progress<OllamaRouterModelPullProgress>(
                item => row.ApplyProgress(item));

            try
            {
                // ConfigureAwait(true) (default): the post-await continuations run back on
                // the UI synchronization context (no-op in headless tests).
                var result = await client.PullModelAsync(row.Model, row.Target, row.Context, progress, cts.Token);
                if (result.Succeeded)
                {
                    row.SetSucceeded(result.Alias);
                    succeeded++;
                }
                else
                {
                    row.SetFailed(result.Error ?? "pull failed");
                    failed++;
                }
            }
            catch (OperationCanceledException)
            {
                row.SetCancelled();
                cancelled = true;
            }
            catch (OllamaRouterApiException ex)
            {
                row.SetFailed($"Router error {(int)ex.Status}: {ex.Error ?? "(no error body)"}");
                failed++;
            }
            catch (Exception ex)
            {
                row.SetFailed(ex.Message);
                failed++;
            }
        }

        // Safety sweep: any row that never started after a cancellation is Cancelled too.
        if (cancelled)
        {
            foreach (var row in Rows)
            {
                if (row.Status == InstallRunStatus.Pending)
                {
                    row.SetCancelled();
                }
            }
        }

        IsRunning = false;
        RunCompleted = true;
        _runCts = null;
        RunResultText = cancelled
            ? "Cancelled"
            : failed == 0
                ? "Succeeded"
                : succeeded > 0
                    ? "Partial"
                    : "Failed";
        InstallCommand.NotifyCanExecuteChanged();
        CancelInstallCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Cancels the in-flight run (the current row → Cancelled, the remaining rows →
    /// Cancelled; the router request is aborted — no phantom success).</summary>
    [RelayCommand(CanExecute = nameof(CanCancelInstall))]
    private void CancelInstall()
    {
        _runCts?.Cancel();
    }

    private bool CanInstall() => !IsRunning && Rows.Count > 0;

    private bool CanCancelInstall() => IsRunning;

    /// <inheritdoc />
    public Task OnStepEnteredAsync(CancellationToken ct = default)
    {
        if (!IsRunning)
        {
            BuildRows();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnStepLeftAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _runCts?.Cancel();
        return ValueTask.CompletedTask;
    }

    private void BuildRows()
    {
        var model = _modelReference();
        Rows.Clear();
        foreach (var (target, context, alias, present) in _targets().BuildPlan())
        {
            Rows.Add(new InstallRunRowVm(target, model, alias, context, present, _targets().Rows
                .FirstOrDefault(r => r.Name == target)?.Liveness ?? "unknown"));
        }
    }
}
