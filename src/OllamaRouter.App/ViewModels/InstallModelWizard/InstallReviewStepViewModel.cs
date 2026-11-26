using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OllamaRouter.App.ViewModels.Wizard;
using OllamaRouter.Client;
using OllamaRouter.Endpoints.RouterApi.Benchmark;
using OllamaRouter.Endpoints.RouterApi.Models;
using OllamaRouter.Services.Benchmarking;

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

    /// <summary>
    /// Task #174 — the live post-install micro-benchmark feedback for this row: the in-flight
    /// phase message (e.g. "Downloading qwen3:8b (42% · 1.8 / 4.3 GB)" or the 409 / cancelled
    /// notes), the slim-bar percent (−1 = hidden / indeterminate) and the terminal summary
    /// ("max viable ctx 16384 @ 24.5 t/s" / "no viable context" / "benchmark failed").
    /// </summary>
    [ObservableProperty]
    private string? _benchmarkNote;

    [ObservableProperty]
    private double _benchmarkProgressValue = -1;

    [ObservableProperty]
    private string? _benchmarkSummaryText;

    public bool HasBenchmarkNote => BenchmarkNote is not null;

    public bool HasBenchmarkProgress => BenchmarkProgressValue >= 0;

    public bool HasBenchmarkSummary => BenchmarkSummaryText is not null;

    /// <summary>The benchmark slim-bar indeterminate gate (< 0 = indeterminate — the row is
    /// benchmarking but the phase has no byte/rung fraction yet, e.g. queued/sizing).</summary>
    public bool BenchmarkProgressIndeterminate => BenchmarkProgressValue < 0;

    partial void OnBenchmarkNoteChanged(string? value) => OnPropertyChanged(nameof(HasBenchmarkNote));

    partial void OnBenchmarkProgressValueChanged(double value)
    {
        OnPropertyChanged(nameof(HasBenchmarkProgress));
        OnPropertyChanged(nameof(BenchmarkProgressIndeterminate));
    }

    partial void OnBenchmarkSummaryTextChanged(string? value) => OnPropertyChanged(nameof(HasBenchmarkSummary));

    public void SetRunning()
    {
        Status = InstallRunStatus.Running;
        // Task #174: the model-named start text (the daemon's first progress line follows).
        StatusText = $"Checking {Model}…";
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

        // Task #174: model-named download status with human-readable byte progress
        // (1024-based, canonical BenchmarkProgressText formatting). The bar percent keeps
        // its pre-#174 semantics (−1 = indeterminate when the daemon reports no totals).
        var totalBytes = progress.TotalBytes;
        var completedBytes = progress.CompletedBytes;
        if (totalBytes is { } total and > 0 && completedBytes is { } completed)
        {
            StatusText = $"Downloading {Model}: {progress.Status} — {BenchmarkProgressText.FormatBytes(completed)} / {BenchmarkProgressText.FormatBytes(total)} ({Math.Min(100, (long)Math.Floor(completed * 100.0 / total))}%)";
            ProgressValue = Math.Min(100.0, completed * 100.0 / total);
        }
        else
        {
            StatusText = $"Downloading {Model}: {progress.Status}";
            ProgressValue = -1;
        }
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
    private CancellationTokenSource? _runCts;

    public InstallReviewStepViewModel(
        Func<IOllamaRouterApiClient> clientAccessor,
        Func<InstallTargetsStepViewModel> targets)
    {
        _clientAccessor = clientAccessor ?? throw new ArgumentNullException(nameof(clientAccessor));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
    }

    /// <summary>
    /// Task #174 test seam: replaces the post-install benchmark poll interval (default:
    /// 2 s — a plain <c>Task.Delay</c> loop, no <c>DashboardPoller</c>).
    /// </summary>
    internal TimeSpan? BenchmarkPollInterval { get; set; }

    /// <summary>The per-target run rows (review summary + live install state).</summary>
    public ObservableCollection<InstallRunRowVm> Rows { get; } = [];

    /// <summary>Whether an install run is in flight (the window + navigation gate on this).</summary>
    [ObservableProperty]
    private bool _isRunning;

    /// <summary>Whether a run COMPLETED (success / partial / failed / cancelled) — the Done
    /// button gate (before the first run the wizard is closed via Cancel).</summary>
    [ObservableProperty]
    private bool _runCompleted;

    /// <summary>Task #174 — opt-in (default OFF): after the install loop, every successfully
    /// installed target is micro-benchmarked (sequentially, live progress per row).</summary>
    [ObservableProperty]
    private bool _runBenchmarkAfterInstall;

    /// <summary>Task #174 — whether the opt-in post-install benchmark phase is in flight
    /// (IsRunning stays true throughout, so the window/nav gate keeps working).</summary>
    [ObservableProperty]
    private bool _isBenchmarking;

    /// <summary>The overall result text (null before the first run: "Succeeded" / "Partial" /
    /// "Failed" / "Cancelled").</summary>
    [ObservableProperty]
    private string? _runResultText;

    /// <summary>Whether a run result is displayed (the result banner's visibility gate).</summary>
    public bool HasRunResult => RunResultText is not null;

    /// <summary>Whether the run result renders at the DANGER severity: "Partial" / "Failed" /
    /// "Cancelled" are error states (only "Succeeded" renders the success severity).</summary>
    public bool RunResultIsError => RunResultText is not null && RunResultText != "Succeeded";

    partial void OnRunResultTextChanged(string? value)
    {
        OnPropertyChanged(nameof(HasRunResult));
        OnPropertyChanged(nameof(RunResultIsError));
    }

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

        // Task #174 — opt-in post-install micro-benchmark: AFTER the sequential install loop,
        // BEFORE RunCompleted. IsRunning stays true throughout the phase (the window/nav gate
        // and CancelInstallCommand keep working — _runCts.Cancel() aborts the phase too).
        string? benchmarkSuffix = null;
        var succeededRows = Rows.Where(r => r.Status == InstallRunStatus.Succeeded).ToList();
        if (RunBenchmarkAfterInstall && !cts.Token.IsCancellationRequested && succeededRows.Count > 0)
        {
            var benchmarkCancelled = await RunPostInstallBenchmarksAsync(client, succeededRows, cts.Token).ConfigureAwait(false);
            if (benchmarkCancelled)
            {
                benchmarkSuffix = " · benchmark cancelled";
            }
        }

        IsRunning = false;
        RunCompleted = true;
        _runCts = null;
        RunResultText = (cancelled
            ? "Cancelled"
            : failed == 0
                ? "Succeeded"
                : succeeded > 0
                    ? "Partial"
                    : "Failed") + (benchmarkSuffix ?? string.Empty);
        InstallCommand.NotifyCanExecuteChanged();
        CancelInstallCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Task #174 — the opt-in post-install micro-benchmark phase. For EACH succeeded row (in
    /// plan order): (1) <c>POST /api/router/benchmark</c> scoped to that target + model (the
    /// server run is SINGLE-FLIGHT, so the runs are strictly sequential — one target goes
    /// terminal before the next starts); (2) poll <c>GET /api/router/benchmark/results</c>
    /// every <see cref="BenchmarkPollInterval"/> until the row's target is terminal, mirroring
    /// the live progress (message + slim bar) into the row. A 409 (a run is already in
    /// progress elsewhere) notes every benchmarked row and skips the phase; transient poll
    /// errors are ignored (the next tick retries); cancellation stops the loop and leaves the
    /// install result intact plus the "· benchmark cancelled" suffix. Row writes are marshaled
    /// via <see cref="AppUiThread.InvokeOrPost"/> (headless tests run inline).
    /// </summary>
    /// <returns>Whether the phase was cancelled mid-flight (the caller appends the suffix).</returns>
    private async Task<bool> RunPostInstallBenchmarksAsync(
        IOllamaRouterApiClient client, IReadOnlyList<InstallRunRowVm> succeededRows, CancellationToken ct)
    {
        AppUiThread.InvokeOrPost(() => IsBenchmarking = true);
        try
        {
            var pollInterval = BenchmarkPollInterval ?? TimeSpan.FromSeconds(2);

            foreach (var row in succeededRows)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await client.StartBenchmarkAsync(
                        new RouterBenchmarkRequest(Target: row.Target, Model: row.Model), ct)
                        .ConfigureAwait(false);
                }
                catch (OllamaRouterApiException ex) when (ex.Status == HttpStatusCode.Conflict)
                {
                    // A benchmark run is already in progress (e.g. from the Setup Wizard or
                    // the CLI): note EVERY benchmarked row and skip the whole phase.
                    AppUiThread.InvokeOrPost(() =>
                    {
                        foreach (var r in succeededRows)
                        {
                            r.BenchmarkNote = "a benchmark run is already in progress — track it in the Setup Wizard or 'ollamarouter benchmark status'";
                        }
                    });
                    return false;
                }
                catch (OllamaRouterApiException)
                {
                    // Any other router error (e.g. the router went down between the install
                    // and the phase): skip the phase cleanly — the install result stays
                    // intact and the rows keep their current state (the phase is purely
                    // observational; it never fails the wizard).
                    return false;
                }

                // Poll until this row's target result is terminal.
                while (true)
                {
                    RouterBenchmarkResultsResponse? results;
                    try
                    {
                        results = await client.GetBenchmarkResultsAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (OllamaRouterApiException)
                    {
                        // Transient router error: ignore — the next tick retries (the wizard
                        // never fails over a poll blip).
                        results = null;
                    }

                    var target = results?.Targets
                        .FirstOrDefault(t => string.Equals(t.Target, row.Target, StringComparison.OrdinalIgnoreCase));

                    if (target is null || target.Status == BenchmarkRunStatus.Running)
                    {
                        if (target is not null && target.Progress is not null)
                        {
                            var progress = target.Progress;
                            var percent = BenchmarkLiveProgress.RowPercent(target.Status, progress);
                            AppUiThread.InvokeOrPost(() =>
                            {
                                row.BenchmarkNote = progress.Message;
                                row.BenchmarkProgressValue = percent;
                            });
                        }

                        await Task.Delay(pollInterval, ct).ConfigureAwait(false);
                        continue;
                    }

                    // Terminal: clear the live note + bar, render the summary.
                    AppUiThread.InvokeOrPost(() =>
                    {
                        row.BenchmarkNote = null;
                        row.BenchmarkProgressValue = -1;
                        row.BenchmarkSummaryText = BuildBenchmarkSummary(target);
                    });
                    break;
                }
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            // Cancellation mid-phase: the rows still awaiting a summary keep a note (the
            // server run may continue — track it in the Setup Wizard or CLI). The install
            // result itself stays intact (the caller appends the suffix).
            AppUiThread.InvokeOrPost(() =>
            {
                foreach (var row in succeededRows)
                {
                    if (row.BenchmarkSummaryText is null)
                    {
                        row.BenchmarkNote = "benchmark cancelled (the server run may continue — track it in the Setup Wizard or CLI)";
                    }
                }
            });
            return true;
        }
        finally
        {
            AppUiThread.InvokeOrPost(() => IsBenchmarking = false);
        }
    }

    /// <summary>
    /// Task #174 — the terminal benchmark summary of one row's target: any viable measured
    /// row ⇒ "max viable ctx {n} @ {tps:0.0} t/s" (the LARGEST viable context); measured rows
    /// but none viable ⇒ "no viable context"; no measured rows at all ⇒ "benchmark
    /// {status}" (e.g. a failed / cancelled run).
    /// </summary>
    private static string BuildBenchmarkSummary(BenchmarkTargetResult target)
    {
        var measured = target.Results;
        var viable = measured.Where(r => r.Viable && r.Reason is null).ToList();
        if (viable.Count > 0)
        {
            var best = viable.OrderByDescending(r => r.NumCtx).First();
            return $"max viable ctx {best.NumCtx} @ {best.DecodeTokensPerSecond:0.0} t/s";
        }

        if (measured.Any(r => r.Reason is null))
        {
            return "no viable context";
        }

        return $"benchmark {target.Status.ToString().ToLowerInvariant()}";
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
        Rows.Clear();
        foreach (var (target, model, context, alias, present) in _targets().BuildPlan())
        {
            Rows.Add(new InstallRunRowVm(target, model, alias, context, present, _targets().Rows
                .FirstOrDefault(r => r.Name == target)?.Liveness ?? "unknown"));
        }
    }
}
