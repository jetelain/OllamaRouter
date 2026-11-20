using System.Net;
using OllamaRouter.App.ViewModels.InstallModelWizard;
using OllamaRouter.Client;
using OllamaRouter.Endpoints.RouterApi.Config;
using OllamaRouter.Endpoints.RouterApi.Fleet;
using OllamaRouter.Services.Hardware;
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

    /// <summary>A REMOTE fleet target (task #144 — the default <see cref="Target"/> helper
    /// produces "local" rows, which are never ssh-probed).</summary>
    private static RouterTargetItem RemoteTarget(string name, string liveness, string? gpu = null, int? vram = null) =>
        new(
            name,
            $"http://{name}:11435",
            true,
            liveness,
            null,
            "remote",
            new RouterTargetHardware("NVIDIA", [gpu ?? string.Empty], vram, 1, null, false, null, null, 1.0, gpu),
            new RouterTargetExecution(0, null, "none"));

    /// <summary>A config target row (task #144 — the wizard reads Type + SshUser from
    /// <c>GET /api/router/config</c>).</summary>
    private static RouterTargetConfigDto ConfigTarget(string name, string type = "ollama", string? sshUser = null, int? sshPort = null) =>
        new(name, $"http://{name}:11435", true, null, 1, type, null, null, false, null, sshUser, sshPort);

    /// <summary>The formatted line of the fake's default synthetic probe profile (RTX 4090 /
    /// 24576 MB fast memory / 1008 GB/s effective bandwidth).</summary>
    private const string DefaultProbeLine = "RTX 4090 · VRAM 24576 MB · BW 1008 GB/s";

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

    // -- Task #144: the daemon-side ssh hardware auto-population + re-probe ---------

    [Fact]
    public async Task AutoProbe_OnStepEntry_FiresExactlyForTheEligibleRow()
    {
        var fake = new FakeRouterApiClient
        {
            FleetResponse = new RouterFleetResponse(
            [
                RemoteTarget("Alpha", "Online", gpu: null), // eligible: remote ollama + sshUser + no profile
                Target("Beta", "Online"),                   // not eligible: LOCAL (the daemon probes itself)
                RemoteTarget("Gamma", "Online"),            // not eligible: no sshUser in the config
                RemoteTarget("Delta", "Online"),            // not eligible: openai (non-Ollama rows are never probed)
                RemoteTarget("Epsilon", "Online"),          // not eligible: already profiled (source "peer")
            ])
        };
        fake.SeedTarget(ConfigTarget("Alpha", "ollama", "gpuadmin"));
        fake.SeedTarget(ConfigTarget("Beta", "ollama", "gpuadmin"));
        fake.SeedTarget(ConfigTarget("Gamma", "ollama"));
        fake.SeedTarget(ConfigTarget("Delta", "openai", "gpuadmin"));
        fake.SeedTarget(ConfigTarget("Epsilon", "ollama", "gpuadmin"));
        fake.HardwareResponse = new RouterFleetHardwareResponse(
        [
            new FleetHardwareNode("Epsilon", "peer",
                new NodeHardwareProfile(
                    "Epsilon", "Linux", "x64", false, 16384, SystemRamType.Ddr5,
                    [new GpuDeviceProfile("NVIDIA", "RTX 3090", 24576, "GDDR6X", 384, 936)],
                    24576, 936),
                true),
        ]);
        var step = Step(fake);

        await step.OnStepEnteredAsync();

        // Fires EXACTLY for the eligible row (one auto-probe per step entry per row).
        Assert.Equal(["Alpha"], [.. fake.ProbeCalls]);
        var alpha = step.Rows.Single(r => r.Name == "Alpha");
        Assert.Equal(DefaultProbeLine, alpha.HardwareText); // the line was updated IN PLACE
        Assert.False(alpha.ProbingHardware);
        Assert.False(alpha.ShowReprobe); // a successful probe hides the affordance
        Assert.Null(step.Error);
        Assert.False(step.IsFetching);

        // The ineligible rows are unchanged (no probing state, no affordance, fleet line kept).
        foreach (var name in new[] { "Beta", "Gamma", "Delta", "Epsilon" })
        {
            var row = step.Rows.Single(r => r.Name == name);
            Assert.False(row.ProbingHardware);
            Assert.False(row.ShowReprobe);
            Assert.DoesNotContain("Probing", row.HardwareText);
        }

        // A SECOND step entry fires NO further probes (every row is known now).
        await step.OnStepEnteredAsync();
        Assert.Equal(1, fake.ProbeCalls.Count);
    }

    [Fact]
    public async Task AutoProbe_ShowsTheProbingText_AndSettlesInPlace_WithoutBlockingTheFetch()
    {
        var gate = Gate();
        var fake = new FakeRouterApiClient
        {
            FleetResponse = new RouterFleetResponse([RemoteTarget("Alpha", "Online", gpu: null)]),
            ProbeFetchBlocks = AsQueue(gate.Task)
        };
        fake.SeedTarget(ConfigTarget("Alpha", "ollama", "gpuadmin"));
        var step = Step(fake);

        await step.OnStepEnteredAsync();

        // The probe NEVER blocks the step entry: IsFetching settles with the probe still in
        // flight, and the row shows the transient "Probing hardware…" state.
        Assert.False(step.IsFetching);
        var alpha = step.Rows.Single(r => r.Name == "Alpha");
        Assert.Equal("Probing hardware…", alpha.HardwareText);
        Assert.True(alpha.ProbingHardware);
        Assert.True(alpha.ShowReprobe);
        Assert.False(alpha.CanReprobe); // the manual affordance is busy
        Assert.Equal("Probing…", alpha.ReprobeButtonContent);

        gate.SetResult();
        await WaitUntilAsync(() => !alpha.ProbingHardware);

        Assert.Equal(DefaultProbeLine, alpha.HardwareText); // updated in place
        Assert.False(alpha.ShowReprobe);
        Assert.Null(step.Error);
        Assert.Equal(1, fake.ProbeCalls.Count);
    }

    [Fact]
    public async Task AutoProbe_Failure_RevertsToTheUnknownText_SetsNoError_LeavesTheGateUnchanged()
    {
        var fake = new FakeRouterApiClient
        {
            FleetResponse = new RouterFleetResponse([RemoteTarget("Alpha", "Online", gpu: null)])
        };
        fake.SeedTarget(ConfigTarget("Alpha", "ollama", "gpuadmin"));
        fake.ProbeFailTargets.Add("Alpha");
        var step = Step(fake);

        await step.OnStepEnteredAsync();

        var alpha = step.Rows.Single(r => r.Name == "Alpha");
        Assert.Equal("CPU · BW 1 GB/s", alpha.HardwareText); // reverted to the ORIGINAL unknown text
        Assert.False(alpha.ProbingHardware);
        Assert.True(alpha.ShowReprobe); // the manual re-probe affordance remains
        Assert.Equal("Probe", alpha.ReprobeButtonContent);
        Assert.True(alpha.CanReprobe);
        Assert.Null(step.Error); // fail-soft: NO error banner

        // The Next gate is EXACTLY as before the failed probe.
        alpha.Selected = true;
        alpha.Context = "4096";
        step.RefreshGate();
        Assert.True(step.IsValid);
        Assert.Null(step.NextGateHint);
        Assert.False(step.IsFetching);
    }

    [Fact]
    public async Task ReProbe_OnAFailedRow_TriggersASecondProbe_AndHidesTheAffordanceOnSuccess()
    {
        var fake = new FakeRouterApiClient
        {
            FleetResponse = new RouterFleetResponse([RemoteTarget("Alpha", "Online", gpu: null)])
        };
        fake.SeedTarget(ConfigTarget("Alpha", "ollama", "gpuadmin"));
        fake.ProbeFailTargets.Add("Alpha");
        var step = Step(fake);

        await step.OnStepEnteredAsync();
        var alpha = step.Rows.Single(r => r.Name == "Alpha");
        Assert.True(alpha.ShowReprobe);
        Assert.Equal("CPU · BW 1 GB/s", alpha.HardwareText);
        Assert.Equal(1, fake.ProbeCalls.Count);

        // The manual re-probe (the row's affordance) triggers a SECOND probe call with the
        // busy state applied…
        fake.ProbeFailTargets.Clear();
        await step.ReProbeHardwareCommand.ExecuteAsync(alpha);
        await WaitUntilAsync(() => !alpha.ProbingHardware);

        Assert.Equal(2, fake.ProbeCalls.Count);
        Assert.Equal(DefaultProbeLine, alpha.HardwareText);
        Assert.False(alpha.ShowReprobe); // after success the affordance disappears
        Assert.Null(step.Error);

        // A stale row (not in Rows) is a no-op.
        var stale = new InstallTargetRowVm("Zeta", "Online", true, true, "CPU", false, false, Model);
        await step.ReProbeHardwareCommand.ExecuteAsync(stale);
        Assert.Equal(2, fake.ProbeCalls.Count);
    }

    [Fact]
    public async Task SupersededEntry_ItsProbeResult_NeverTouchesTheLatestEntryRows()
    {
        // Entry 1's auto-probe is still in flight when entry 2 re-enters and applies its own
        // rows; entry 1's probe result must then be DISCARDED (latest-wins, task #130) —
        // it settles long after, on a thread-pool thread, and must not touch the rows.
        var probeGate = Gate();
        var fake = new FakeRouterApiClient
        {
            FleetResponses = AsQueue(
                new RouterFleetResponse([RemoteTarget("Alpha", "Online", gpu: null)]),
                new RouterFleetResponse([RemoteTarget("Beta", "Online")])),
            ProbeFetchBlocks = AsQueue(probeGate.Task)
        };
        fake.SeedTarget(ConfigTarget("Alpha", "ollama", "gpuadmin"));
        fake.SeedTarget(ConfigTarget("Beta", "ollama")); // Beta has no sshUser → never probed
        var step = Step(fake);

        var t1 = step.OnStepEnteredAsync();
        await WaitUntilAsync(() => fake.ProbeCalls.Count == 1 && step.Rows.Count == 1);
        var alpha = step.Rows.Single();
        Assert.Equal("Probing hardware…", alpha.HardwareText);
        Assert.True(alpha.ProbingHardware);

        // Entry 2 (the LATEST) re-enters while entry 1's probe is gated and applies its rows.
        var t2 = step.OnStepEnteredAsync();
        await WaitUntilAsync(() => step.Rows.Count == 1 && step.Rows[0]!.Name == "Beta");
        var beta = step.Rows.Single();
        Assert.False(beta.ShowReprobe);
        Assert.False(beta.ProbingHardware);
        var betaHardwareText = beta.HardwareText;

        // Entry 1's probe now settles: the superseded result must leave the latest entry's
        // rows (and the error) untouched.
        probeGate.SetResult();
        await Task.WhenAll(t1, t2);
        await WaitUntilAsync(() => !step.IsFetching);

        Assert.Equal("Beta", step.Rows.Single().Name);
        Assert.Equal(betaHardwareText, beta.HardwareText); // no clobbered hardware line
        Assert.False(beta.ProbingHardware);
        Assert.Null(step.Error);
        Assert.Equal(["Alpha"], [.. fake.ProbeCalls]); // Beta was never probed
    }

    [Fact]
    public async Task HardwareFetchFailure_FailsSoft_NoAutoProbes_TheStepStillFetches()
    {
        var fake = new FakeRouterApiClient
        {
            FleetResponse = new RouterFleetResponse([RemoteTarget("Alpha", "Online", gpu: null)])
        };
        fake.SeedTarget(ConfigTarget("Alpha", "ollama", "gpuadmin"));
        fake.HardwareException = new OllamaRouterApiException(
            HttpStatusCode.InternalServerError, "hardware store unavailable");
        var step = Step(fake);

        await step.OnStepEnteredAsync();

        // The step fetch SUCCEEDS (a failed hardware GET must never fail the entry)…
        Assert.Null(step.Error);
        Assert.False(step.IsFetching);
        var alpha = step.Rows.Single(r => r.Name == "Alpha");
        // …but NO auto-probe fires ("unknown state unknown" → no probes this entry), the
        // fleet hardware line still renders, and no re-probe affordance is shown.
        Assert.Empty(fake.ProbeCalls);
        Assert.False(alpha.ShowReprobe);
        Assert.False(alpha.ProbingHardware);
        Assert.Equal("CPU · BW 1 GB/s", alpha.HardwareText);
    }
}
