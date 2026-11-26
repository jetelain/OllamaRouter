using OllamaRouter.Services.Benchmarking;

namespace OllamaRouter.App.ViewModels.Wizard;

/// <summary>
/// The pure fractional overall-progress computation for the live benchmark run (task #174 —
/// shared by Setup Wizard Step 4 and the Install Model Wizard's post-install benchmark
/// phase). Given ONE target's status + optional live <see cref="BenchmarkTargetProgress"/>
/// it yields that target's 0..1 fraction; the overall percent is the 100-scaled mean of the
/// per-target fractions over the queued targets (or all rows when nothing is queued).
/// <para>
/// Pinned per-phase inner offsets (a single candidate, index 0):
/// <c>null</c>/<c>Queued</c> 0.0 · <c>Sizing</c> 0.1 · <c>CheckingModel</c> 0.2 ·
/// <c>DownloadingModel</c> 0.2 + 0.3·(completed/total) · <c>InspectingModel</c> 0.5 ·
/// <c>UnloadingModel</c> 0.6 · <c>Benchmarking</c> 0.7 + 0.3·(rungIndex/rungCount) ·
/// <c>Persisting</c> 0.95. A terminal target is always 1.0; a Running target with
/// <c>Progress == null</c> (older daemon) contributes 0.0 — so the pre-#174 assertions
/// (50.0 for 1-of-2 terminal with an un-progressed Running row; 100.0 all-terminal) hold.
/// </para>
/// </summary>
public static class BenchmarkLiveProgress
{
    /// <summary>
    /// The 0..1 completion fraction of ONE target. <paramref name="status"/>/
    /// <paramref name="progress"/> describe the target's current state (a terminal status
    /// always ⇒ 1.0 regardless of progress; a non-Running status with no progress ⇒ 0.0).
    /// </summary>
    public static double TargetFraction(BenchmarkRunStatus status, BenchmarkTargetProgress? progress)
    {
        if (status is BenchmarkRunStatus.Succeeded or BenchmarkRunStatus.PartiallySucceeded
            or BenchmarkRunStatus.Failed or BenchmarkRunStatus.Cancelled)
        {
            return 1.0;
        }

        if (status != BenchmarkRunStatus.Running || progress is null)
        {
            return 0.0;
        }

        var candidateCount = Math.Max(1, progress.CandidateCount);
        var candidateIndex = Math.Clamp(progress.CandidateIndex, 0, candidateCount - 1);

        double inner = progress.Phase switch
        {
            BenchmarkPhase.Queued => 0.0,
            BenchmarkPhase.Sizing => 0.1,
            BenchmarkPhase.CheckingModel => 0.2,
            BenchmarkPhase.DownloadingModel => DownloadingInner(progress),
            BenchmarkPhase.InspectingModel => 0.5,
            BenchmarkPhase.UnloadingModel => 0.6,
            BenchmarkPhase.Benchmarking => BenchmarkingInner(progress),
            BenchmarkPhase.Persisting => 0.95,
            _ => 0.0,
        };

        return (candidateIndex + inner) / candidateCount;
    }

    /// <summary>
    /// The 0..100 per-target percent (the <c>RowProgressPercent</c> bound in the row cards).
    /// </summary>
    public static double RowPercent(BenchmarkRunStatus status, BenchmarkTargetProgress? progress) =>
        TargetFraction(status, progress) * 100.0;

    private static double DownloadingInner(BenchmarkTargetProgress p) =>
        p.TotalBytes is { } total and > 0 && p.CompletedBytes is { } completed
            ? 0.2 + 0.3 * Math.Min(1.0, completed / (double)total)
            : 0.2;

    private static double BenchmarkingInner(BenchmarkTargetProgress p) =>
        p.RungCount > 0
            ? 0.7 + 0.3 * Math.Min(1.0, p.RungIndex / (double)p.RungCount)
            : 0.7;
}
