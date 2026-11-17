using OllamaRouter.App.ViewModels.InstallModelWizard;
using OllamaRouter.Client;
using OllamaRouter.Endpoints.RouterApi.Fleet;
using Xunit;

namespace OllamaRouter.Tests.App;

/// <summary>
/// Task #130 — Install Model wizard / Targets step RE-ENTRANCY + DEDUP regression tests:
/// the wizard fires the step lifecycle fire-and-forget, so a rapid Back→Next burst (or a
/// re-entry while a previous fetch is still in flight) can start a SECOND
/// <c>OnStepEnteredAsync</c> while the first is still awaiting the fleet endpoint —
/// the original bug rendered every fleet target TWICE (interleaved
/// <c>Rows.Clear/Add</c>). The step is now LATEST-WINS (a superseded fetch discards its
/// whole result — <c>Rows</c> always reflect the LATEST entry's response regardless of
/// completion order) plus case-insensitive first-occurrence name dedup as defense in
/// depth. All tests are in-memory (<see cref="FakeRouterApiClient"/> with the
/// <c>FleetResponses</c> / <c>FleetFetchBlocks</c> scripting knobs, task-gate delays,
/// <c>Task.Yield</c>-based settling) — NO network, NO real sleeps.
/// </summary>
public sealed class InstallTargetsStepViewModelTests
{
    private const string Model = "llama3.2:13b-q4_K_M";

    private static async Task WaitUntilAsync(Func<bool> condition, int maxBeats = 20000)
    {
        for (var i = 0; i < maxBeats && !condition(); i++)
        {
            await Task.Yield();
        }

        Assert.True(condition());
    }

    private static RouterTargetItem Target(string name, string liveness, string? gpu = "RTX 4090", int? vram = 24564) =>
        new(
            name,
            $"http://{name}:11435",
            true,
            liveness,
            null,
            "local",
            new RouterTargetHardware("NVIDIA", [gpu ?? string.Empty], vram, 1, null, false, null, null, 1.0, gpu),
            new RouterTargetExecution(0, null, "none"));

    private static InstallTargetsStepViewModel Step(FakeRouterApiClient fake, bool assisted = false) =>
        new(() => fake, () => Model, () => assisted);

    private static List<string> Names(InstallTargetsStepViewModel step) =>
        [.. step.Rows.Select(r => r.Name)];

    private static int FleetCalls(FakeRouterApiClient fake) =>
        fake.Calls.Count(c => string.Equals(c, nameof(IOllamaRouterApiClient.GetFleetAsync)));

    private static TaskCompletionSource Gate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Builds a FIFO <see cref="Queue{T}"/> (the <c>Queue{T}(IEnumerable{T})</c>
    /// constructor does not make <c>Queue{T}</c> a valid collection-expression target).</summary>
    private static Queue<T> AsQueue<T>(params T[] items) => new(items);

    [Fact]
    public async Task ConcurrentEntries_RowsNeverDuplicate_AndReflectLatestResponse()
    {
        // Both entries in flight CONCURRENTLY (both blocked on the same unsignaled gate) —
        // the exact interleave that produced 6 rows (rows 4–6 duplicating rows 1–3).
        var gate = Gate();
        var fake = new FakeRouterApiClient
        {
            FleetResponses = AsQueue(
                new RouterFleetResponse([Target("Alpha", "Online"), Target("Beta", "Online")]),
                new RouterFleetResponse(
                    [Target("Alpha", "Online"), Target("Beta", "Online"), Target("Gamma", "Online")])),
            FleetFetchBlocks = AsQueue(gate.Task, gate.Task)
        };
        var step = Step(fake);

        // Fire entry 1 and entry 2 back-to-back WITHOUT awaiting the first.
        var t1 = step.OnStepEnteredAsync();
        var t2 = step.OnStepEnteredAsync();
        await WaitUntilAsync(() => FleetCalls(fake) == 2); // both fetches now in flight
        gate.SetResult();
        await Task.WhenAll(t1, t2);
        await WaitUntilAsync(() => !step.IsFetching);

        // The LATEST response wins — 3 rows, each name exactly once, no error, fetch settled.
        Assert.Equal(3, step.Rows.Count);
        Assert.Equal(["Alpha", "Beta", "Gamma"], Names(step));
        Assert.Equal(
            3, step.Rows.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Null(step.Error);
        Assert.False(step.IsFetching);
    }

    [Fact]
    public async Task SlowOldFetch_FinishingAfterLatestFetch_DoesNotClobberRows()
    {
        // The FIRST (older) fetch is SLOWER: it finishes LAST. Its payload must be
        // discarded even though it settles after the latest entry already applied.
        var oldGate = Gate();
        var latestGate = Gate();
        var fake = new FakeRouterApiClient
        {
            FleetResponses = AsQueue(
                new RouterFleetResponse([Target("Alpha", "Online")]), // older (superseded) payload
                new RouterFleetResponse([Target("Beta", "Online"), Target("Gamma", "Online")])),
            FleetFetchBlocks = AsQueue(oldGate.Task, latestGate.Task)
        };
        var step = Step(fake);

        var t1 = step.OnStepEnteredAsync();
        var t2 = step.OnStepEnteredAsync();
        await WaitUntilAsync(() => FleetCalls(fake) == 2);

        // The LATEST entry settles first and applies...
        latestGate.SetResult();
        await WaitUntilAsync(() => step.Rows.Count == 2);
        // ...and is STAYED: the older fetch finishing last must not clobber it.
        oldGate.SetResult();
        await Task.WhenAll(t1, t2);
        await WaitUntilAsync(() => !step.IsFetching);

        Assert.Equal(["Beta", "Gamma"], Names(step));
        Assert.Equal(2, step.Rows.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Null(step.Error);
        Assert.False(step.IsFetching);
    }

    [Fact]
    public async Task BackToBackEntries_NoDuplicates()
    {
        var fake = new FakeRouterApiClient
        {
            FleetResponses = AsQueue(
                new RouterFleetResponse([Target("Alpha", "Online"), Target("Beta", "Online")]),
                new RouterFleetResponse([Target("Gamma", "Online")]))
        };
        var step = Step(fake);

        await step.OnStepEnteredAsync();
        Assert.Equal(["Alpha", "Beta"], Names(step));

        await step.OnStepEnteredAsync();
        Assert.Equal(["Gamma"], Names(step));
        Assert.Equal(1, step.Rows.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.False(step.IsFetching);
    }

    [Fact]
    public async Task DuplicatedFleetPayload_DeduplicatesByTargetName_KeepsFirst()
    {
        // A (malformed) wire payload repeating "Alpha" with DIFFERENT data: exactly one
        // row per name, carrying the FIRST occurrence's data; BuildPlan() then yields
        // exactly one plan entry per distinct selected target.
        var fake = new FakeRouterApiClient
        {
            FleetResponse = new RouterFleetResponse(
            [
                Target("Alpha", "Online"),
                Target("Beta", "Online"),
                Target("Alpha", "Offline", gpu: null, vram: null) // duplicate name, different data
            ])
        };
        var step = Step(fake);

        await step.OnStepEnteredAsync();
        Assert.Equal(2, step.Rows.Count);
        Assert.Equal(["Alpha", "Beta"], Names(step));
        Assert.Equal("Online", step.Rows[0]!.Liveness); // first-occurrence data kept
        Assert.Contains("VRAM 24564", step.Rows[0]!.HardwareText); // first-occurrence data kept

        step.Rows[0]!.Selected = true;
        step.Rows[1]!.Selected = true;
        step.Rows[0]!.Context = "4096";
        step.Rows[1]!.Context = "4096";
        var plan = step.BuildPlan();
        Assert.Equal(2, plan.Count);
        Assert.Equal(["Alpha", "Beta"], [.. plan.Select(p => p.Target)]);
        Assert.Equal(2, plan.Select(p => p.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal([4096, 4096], [.. plan.Select(p => p.Context!.Value)]);
    }

    [Fact]
    public async Task RapidBackNextNavigation_NoDuplicateRows()
    {
        // Wizard level: the fire-and-forget step lifecycle under a Back→Next burst —
        // fetch B (step 1 entry) is still in flight when Back→0→Next re-enters the step
        // and starts fetch C. After settling, the Targets step holds each target exactly
        // once (the latest response).
        var g1 = Gate();
        var g2 = Gate();
        var g3 = Gate();
        var fake = new FakeRouterApiClient
        {
            FleetResponse = new RouterFleetResponse([Target("Alpha", "Online"), Target("Beta", "Online")]),
            FleetFetchBlocks = AsQueue(g1.Task, g2.Task, g3.Task)
        };
        var vm = new InstallModelWizardViewModel(() => fake);

        // 0 → 1: fetch A (gated, settles first so the gate to the review step opens).
        vm.ModelStep.ModelReference = Model;
        vm.NextCommand.Execute(null);
        Assert.Equal(1, vm.SelectedStep);
        await WaitUntilAsync(() => FleetCalls(fake) == 1);
        g1.SetResult();
        await WaitUntilAsync(() => !vm.TargetsStep.IsFetching && vm.TargetsStep.Rows.Count == 2);

        // Manual selection so Next may advance 1 → 2.
        vm.TargetsStep.Rows[0]!.Selected = true;
        vm.TargetsStep.Rows[0]!.Context = "4096";
        vm.TargetsStep.RefreshGate();
        vm.NextCommand.Execute(null);
        Assert.Equal(2, vm.SelectedStep);

        // 2 → 1: fetch B starts (gated, stays IN FLIGHT)...
        vm.BackCommand.Execute(null);
        Assert.Equal(1, vm.SelectedStep);
        await WaitUntilAsync(() => FleetCalls(fake) == 2);
        // ...1 → 0 (B still in flight), then 0 → 1: fetch C re-enters the SAME step
        // while B is in flight (gated) — the re-entrancy burst from the bug report.
        vm.BackCommand.Execute(null);
        Assert.Equal(0, vm.SelectedStep);
        vm.NextCommand.Execute(null);
        Assert.Equal(1, vm.SelectedStep);
        await WaitUntilAsync(() => FleetCalls(fake) == 3);

        g2.SetResult();
        g3.SetResult();
        await WaitUntilAsync(() => !vm.TargetsStep.IsFetching);

        Assert.Equal(2, vm.TargetsStep.Rows.Count);
        Assert.Equal(["Alpha", "Beta"], Names(vm.TargetsStep));
        Assert.Equal(
            2, vm.TargetsStep.Rows.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Null(vm.TargetsStep.Error);
        await vm.DisposeAsync();
    }
}
