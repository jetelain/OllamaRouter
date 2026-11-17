using OllamaRouter.App.ViewModels.InstallModelWizard;
using OllamaRouter.Client;
using OllamaRouter.Endpoints.RouterApi.Benchmark;
using OllamaRouter.Endpoints.RouterApi.Cartography;
using OllamaRouter.Endpoints.RouterApi.Fleet;
using OllamaRouter.Endpoints.RouterApi.Models;
using OllamaRouter.Options;
using OllamaRouter.Services.Benchmarking;
using Xunit;

namespace OllamaRouter.Tests.App;

/// <summary>
/// Task #125 — Install Model wizard unit tests: the 3-step navigation gates (invalid model
/// reference / no selected target block Next; Back bounds), the MANUAL mode install loop
/// (one PullModelAsync per selected target in order; a failure does NOT stop the remaining
/// targets → Partial; cancellation marks current + remaining rows Cancelled — no phantom
/// success), the BENCHMARK-ASSISTED prefill (benchmark viable numCtx &gt; fleet-catalog
/// ceiling &gt; hint, overridable before confirming; online Ollama targets preselected), the
/// per-target run-row progress application, the pure <see cref="InstallAssistSuggester"/>
/// precedence and the Done/Cancel close signaling. All against the in-memory
/// <see cref="FakeRouterApiClient"/> — NO network.
/// </summary>
public sealed class InstallModelWizardViewModelTests
{
    private const string Model = "llama3.2:13b-q4_K_M";
    private const string ModelBase = "llama3.2";

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

    private static FakeRouterApiClient MakeFake()
    {
        var fake = new FakeRouterApiClient
        {
            FleetResponse = new RouterFleetResponse([Target("Alpha", "Online"), Target("Beta", "Online")])
        };
        return fake;
    }

    private static async Task<InstallModelWizardViewModel> OpenAtStepAsync(
        InstallModelWizardViewModel vm, InstallTargetsStepViewModel step, int stepIndex)
    {
        await WaitUntilAsync(() => !step.IsFetching && step.Rows.Count > 0);
        Assert.Equal(stepIndex, vm.SelectedStep);
        return vm;
    }

    // ------------------------------------------------------------------
    //  Navigation gates
    // ------------------------------------------------------------------

    [Fact]
    public async Task Navigation_InvalidModelReference_BlocksNext_BackBoundsAtZero()
    {
        var vm = new InstallModelWizardViewModel(() => MakeFake());

        Assert.Equal(0, vm.SelectedStep);
        vm.BackCommand.Execute(null); // bounds at 0
        Assert.Equal(0, vm.SelectedStep);

        vm.ModelStep.ModelReference = "bad name"; // invalid reference
        vm.NextCommand.Execute(null);
        Assert.Equal(0, vm.SelectedStep);

        vm.ModelStep.ModelReference = "a:b:c";
        vm.NextCommand.Execute(null);
        Assert.Equal(0, vm.SelectedStep);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Navigation_Step1WithoutTargets_BlocksNext()
    {
        var fake = new FakeRouterApiClient
        {
            // EMPTY fleet → no rows → the gate blocks.
            FleetResponse = new RouterFleetResponse([])
        };
        var vm = new InstallModelWizardViewModel(() => fake);

        vm.ModelStep.ModelReference = Model;
        vm.NextCommand.Execute(null);
        Assert.Equal(1, vm.SelectedStep);

        await WaitUntilAsync(() => !vm.TargetsStep.IsFetching);
        Assert.Empty(vm.TargetsStep.Rows);
        Assert.False(vm.TargetsStep.IsValid);
        Assert.Equal("No fleet targets to install on.", vm.TargetsStep.NextGateHint);

        vm.NextCommand.Execute(null);
        Assert.Equal(1, vm.SelectedStep); // blocked at step 1

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Navigation_ValidState_WalksAllThreeSteps()
    {
        var fake = MakeFake();
        var vm = new InstallModelWizardViewModel(() => fake);

        vm.ModelStep.ModelReference = Model;
        vm.NextCommand.Execute(null);
        Assert.Equal(1, vm.SelectedStep);
        await OpenAtStepAsync(vm, vm.TargetsStep, 1);

        // Manual mode: select a target + a per-row context (unified per-target inputs).
        vm.TargetsStep.Rows[0]!.Selected = true;
        vm.TargetsStep.Rows[0]!.Context = "8192";
        vm.TargetsStep.RefreshGate();
        Assert.True(vm.TargetsStep.IsValid);

        vm.NextCommand.Execute(null);
        Assert.Equal(2, vm.SelectedStep);
        await WaitUntilAsync(() => vm.ReviewStep.Rows.Count == 1);

        // Back walks home.
        vm.BackCommand.Execute(null);
        Assert.Equal(1, vm.SelectedStep);
        vm.BackCommand.Execute(null);
        Assert.Equal(0, vm.SelectedStep);

        await vm.DisposeAsync();
    }

    // ------------------------------------------------------------------
    //  Manual mode install loop
    // ------------------------------------------------------------------

    [Fact]
    public async Task ManualMode_Install_IssuesOnePullPerSelectedTarget_InOrder_WithAliasContext()
    {
        var fake = MakeFake();
        var vm = new InstallModelWizardViewModel(() => fake);

        vm.ModelStep.ModelReference = Model;
        vm.NextCommand.Execute(null);
        await OpenAtStepAsync(vm, vm.TargetsStep, 1);

        vm.TargetsStep.Rows[0]!.Selected = true; // Alpha
        vm.TargetsStep.Rows[1]!.Selected = true; // Beta
        vm.TargetsStep.Rows[0]!.Context = "8192"; // per-row contexts (alias ON, default)
        vm.TargetsStep.Rows[1]!.Context = "8192";
        vm.TargetsStep.RefreshGate();

        // The computed PER-TARGET alias hint (the row chip text, both rows own ref + ctx).
        Assert.Equal("llama3.2:13b-q4_K_M-8k", vm.TargetsStep.Rows[0]!.AliasHint);
        Assert.Equal("llama3.2:13b-q4_K_M-8k", vm.TargetsStep.Rows[1]!.AliasHint);

        vm.NextCommand.Execute(null);
        await WaitUntilAsync(() => vm.ReviewStep.Rows.Count == 2);
        Assert.Equal("llama3.2:13b-q4_K_M-8k", vm.ReviewStep.Rows[0]!.Alias);

        Assert.False(vm.ReviewStep.RunCompleted);
        Assert.False(vm.DoneCommand.CanExecute(null));

        await vm.ReviewStep.InstallCommand.ExecuteAsync(null);

        // EXACTLY ONE PullModelAsync per selected target, in selection order, with the alias
        // context (the router creates the context alias from it).
        Assert.Equal(
            [
                (Model, "Alpha", 8192),
                (Model, "Beta", 8192)
            ],
            fake.PullCalls);

        Assert.All(vm.ReviewStep.Rows, row =>
        {
            Assert.Equal(InstallRunStatus.Succeeded, row.Status);
            Assert.Equal("llama3.2:13b-q4_K_M-8k", row.Alias);
            Assert.Equal(100, row.ProgressValue);
        });
        Assert.True(vm.ReviewStep.RunCompleted);
        Assert.Equal("Succeeded", vm.ReviewStep.RunResultText);
        Assert.True(vm.DoneCommand.CanExecute(null));

        // Done closes the window (completed = true, the run result text).
        bool? signaled = null;
        string? signaledResult = null;
        vm.CloseRequestedSignal += (_, result) => { signaled = true; signaledResult = result; };
        vm.DoneCommand.Execute(null);
        Assert.True(signaled);
        Assert.Equal("Succeeded", signaledResult);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task ManualMode_AliasOff_OmitsTheContextFromThePullRequests()
    {
        var fake = MakeFake();
        var vm = new InstallModelWizardViewModel(() => fake);

        vm.ModelStep.ModelReference = Model;
        vm.NextCommand.Execute(null);
        await OpenAtStepAsync(vm, vm.TargetsStep, 1);

        vm.TargetsStep.Rows[0]!.Selected = true;
        vm.TargetsStep.CreateAlias = false; // the context is NOT required now
        vm.TargetsStep.RefreshGate();
        Assert.True(vm.TargetsStep.IsValid);
        Assert.Null(vm.TargetsStep.Rows[0]!.AliasHint); // the per-row alias chip is hidden

        vm.NextCommand.Execute(null);
        await WaitUntilAsync(() => vm.ReviewStep.Rows.Count == 1);
        await vm.ReviewStep.InstallCommand.ExecuteAsync(null);

        Assert.Equal([(Model, "Alpha", (int?)null)], fake.PullCalls);
        Assert.Equal(InstallRunStatus.Succeeded, vm.ReviewStep.Rows[0]!.Status);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task ManualMode_OneTargetFails_RemainingStillAttempted_ResultIsPartial()
    {
        var fake = MakeFake();
        fake.PullFailTargets.Add("Alpha");
        var vm = new InstallModelWizardViewModel(() => fake);

        vm.ModelStep.ModelReference = Model;
        vm.NextCommand.Execute(null);
        await OpenAtStepAsync(vm, vm.TargetsStep, 1);

        vm.TargetsStep.Rows[0]!.Selected = true;
        vm.TargetsStep.Rows[1]!.Selected = true;
        vm.TargetsStep.Rows[0]!.Context = "8192";
        vm.TargetsStep.Rows[1]!.Context = "8192";
        vm.TargetsStep.RefreshGate();
        vm.NextCommand.Execute(null);
        await WaitUntilAsync(() => vm.ReviewStep.Rows.Count == 2);

        await vm.ReviewStep.InstallCommand.ExecuteAsync(null);

        // BOTH targets were attempted (the failure did not stop the loop).
        Assert.Equal(
            [
                (Model, "Alpha", 8192),
                (Model, "Beta", 8192)
            ],
            fake.PullCalls);

        Assert.Equal(InstallRunStatus.Failed, vm.ReviewStep.Rows[0]!.Status);
        Assert.Equal("simulated pull failure", vm.ReviewStep.Rows[0]!.Error);
        Assert.Equal(InstallRunStatus.Succeeded, vm.ReviewStep.Rows[1]!.Status);
        Assert.Equal("Partial", vm.ReviewStep.RunResultText);
        Assert.True(vm.DoneCommand.CanExecute(null));

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task ManualMode_PreStreamRouterFailure_MarksTheRowFailed_AndContinues()
    {
        var fake = MakeFake();
        var vm = new InstallModelWizardViewModel(() => fake);

        vm.ModelStep.ModelReference = Model;
        vm.NextCommand.Execute(null);
        await OpenAtStepAsync(vm, vm.TargetsStep, 1);

        // A 409 (target offline) surfaces as OllamaRouterApiException BEFORE the stream —
        // the ONE-SHOT fake seam (set AFTER the step-1 fetch, so the FIRST PULL throws and
        // is cleared for the second).
        fake.NextCallException = new OllamaRouterApiException(System.Net.HttpStatusCode.Conflict, "target 'Alpha' is offline");

        vm.TargetsStep.Rows[0]!.Selected = true;
        vm.TargetsStep.Rows[1]!.Selected = true;
        vm.TargetsStep.Rows[0]!.Context = "8192";
        vm.TargetsStep.Rows[1]!.Context = "8192";
        vm.TargetsStep.RefreshGate();
        vm.NextCommand.Execute(null);
        await WaitUntilAsync(() => vm.ReviewStep.Rows.Count == 2);

        // The exception is one-shot: the FIRST pull throws, the second succeeds.
        await vm.ReviewStep.InstallCommand.ExecuteAsync(null);

        Assert.Equal(InstallRunStatus.Failed, vm.ReviewStep.Rows[0]!.Status);
        Assert.Contains("target 'Alpha' is offline", vm.ReviewStep.Rows[0]!.Error);
        Assert.Equal(InstallRunStatus.Succeeded, vm.ReviewStep.Rows[1]!.Status);
        Assert.Equal("Partial", vm.ReviewStep.RunResultText);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task ManualMode_Cancellation_MidRun_MarksCurrentAndRemainingRowsCancelled()
    {
        var fake = MakeFake();
        fake.PullDelayMs = 600; // the first pull is still "in flight" when we cancel
        var vm = new InstallModelWizardViewModel(() => fake);

        vm.ModelStep.ModelReference = Model;
        vm.NextCommand.Execute(null);
        await OpenAtStepAsync(vm, vm.TargetsStep, 1);

        vm.TargetsStep.Rows[0]!.Selected = true;
        vm.TargetsStep.Rows[1]!.Selected = true;
        vm.TargetsStep.Rows[0]!.Context = "8192";
        vm.TargetsStep.Rows[1]!.Context = "8192";
        vm.TargetsStep.RefreshGate();
        vm.NextCommand.Execute(null);
        await WaitUntilAsync(() => vm.ReviewStep.Rows.Count == 2);

        var installTask = vm.ReviewStep.InstallCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.ReviewStep.IsRunning);
        await Task.Delay(150);
        Assert.True(vm.ReviewStep.CancelInstallCommand.CanExecute(null));
        vm.ReviewStep.CancelInstallCommand.Execute(null);

        await installTask;

        // No phantom success: the in-flight row AND the never-started row are Cancelled.
        Assert.All(vm.ReviewStep.Rows, row => Assert.Equal(InstallRunStatus.Cancelled, row.Status));
        Assert.Equal("Cancelled", vm.ReviewStep.RunResultText);
        Assert.True(vm.ReviewStep.RunCompleted);
        Assert.True(vm.DoneCommand.CanExecute(null));
        Assert.Equal(1, fake.PullCalls.Count); // the second target never started

        await vm.DisposeAsync();
    }

    // ------------------------------------------------------------------
    //  Benchmark-assisted mode
    // ------------------------------------------------------------------

    [Fact]
    public async Task AssistedMode_Step0_FleetKnownModel_ShowsCatalogQuant_AndPrefillsTheTag()
    {
        var fake = MakeFake();
        fake.ModelDetailResponse = new RouterModelDetailResponse(
            Model, "Dense", 8_000_000_000L, 8192, 32768,
            [new RouterModelTargetCompatibility("Alpha", true, 49152, false, 0, null)]);
        fake.CartographyResponse = new RouterCartographyResponse(
            [],
            [new CartographyModelRow(Model, "llama", 4_000_000_000L, 8192, 32768, false, [], null, "13b-q4_K_M")],
            []);

        var vm = new InstallModelWizardViewModel(() => fake);
        vm.ModelStep.Assisted = true;
        vm.ModelStep.ModelReference = ModelBase; // the user left the tag blank

        await WaitUntilAsync(() => vm.ModelStep.FleetMessage is not null);
        Assert.Contains("Found on 1 node(s)", vm.ModelStep.FleetMessage!);
        Assert.Contains("32,768", vm.ModelStep.FleetMessage!);
        Assert.Contains("13b-q4_K_M", vm.ModelStep.FleetMessage!);
        Assert.Equal("13b-q4_K_M", vm.ModelStep.ParsedTag);
        Assert.Equal("fleet catalog", vm.ModelStep.TagProvenance);
        // The blank tag was PREFILLED from the fleet catalog (user-editable).
        Assert.Equal(Model, vm.ModelStep.ModelReference);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task AssistedMode_Step0_UnknownModel_ShowsTheNotInFleetHint()
    {
        var fake = MakeFake(); // ModelDetailResponse = null → the fake throws the 404.
        var vm = new InstallModelWizardViewModel(() => fake);
        vm.ModelStep.Assisted = true;
        vm.ModelStep.ModelReference = "ghostmodel";

        await WaitUntilAsync(() => vm.ModelStep.FleetMessage is not null);
        Assert.Equal("Not in the fleet yet — will be pulled from the Ollama registry.", vm.ModelStep.FleetMessage);
        Assert.Null(vm.ModelStep.FleetError);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task AssistedMode_Step1_PrefillsPerTargetContexts_BenchmarkBeatsCatalog_OnlineTargetsPreselected()
    {
        var fake = MakeFake();
        // Benchmark data for the EXACT model: Alpha has 32768 viable @ 24.3 t/s and 65536
        // NON-viable; Beta has none.
        fake.BenchmarkResults = new RouterBenchmarkResultsResponse(
        [
            new BenchmarkTargetResult("Alpha", BenchmarkRunStatus.Succeeded, null, null,
            [
                new BenchmarkContextResult(Model, UsageRole.AgenticChat, 65536, 512, 20_000_000_000L, 12.0, 500.0, false, "out of memory"),
                new BenchmarkContextResult(Model, UsageRole.AgenticChat, 32768, 512, 21_000_000_000L, 24.3, 900.0, true, null)
            ]),
            new BenchmarkTargetResult("Beta", BenchmarkRunStatus.Succeeded, null, null, [])
        ]);
        // Fleet catalog: Alpha capable (ceiling 49152 — below the benchmark win), Beta NOT
        // capable with a veto reason.
        fake.ModelDetailResponse = new RouterModelDetailResponse(
            Model, "Dense", 8_000_000_000L, 8192, 32768,
            [
                new RouterModelTargetCompatibility("Alpha", true, 49152, false, 0, null),
                new RouterModelTargetCompatibility("Beta", false, 0, false, 0, "insufficient VRAM for llama3.2")
            ]);

        var vm = new InstallModelWizardViewModel(() => fake);
        vm.ModelStep.Assisted = true;
        vm.ModelStep.ModelReference = Model; // an explicit tag (authoritative)
        vm.NextCommand.Execute(null);
        await OpenAtStepAsync(vm, vm.TargetsStep, 1);

        var alpha = vm.TargetsStep.Rows[0]!;
        var beta = vm.TargetsStep.Rows[1]!;

        // Online Ollama targets are PRESELECTED by default.
        Assert.True(alpha.Selected);
        Assert.True(beta.Selected);

        // The per-row quant/tag is PREFILLED with the step-0 reference's EXPLICIT tag in BOTH
        // modes (assisted here; manual asserted in the manual-mode tests).
        Assert.Equal("13b-q4_K_M", alpha.QuantTag);
        Assert.Equal("13b-q4_K_M", beta.QuantTag);

        // Alpha: the LARGEST VIABLE benchmark row wins over the catalog ceiling.
        Assert.Equal("32768", alpha.Context);
        Assert.Equal("Suggested: 32768", alpha.SuggestionBadge);
        Assert.Equal("32768 tok — benchmark: viable @ 24.3 t/s", alpha.SuggestionProvenance);

        // Beta: no benchmark + not capable → the veto reason, no suggested context.
        Assert.Equal(string.Empty, beta.Context);
        Assert.Null(beta.SuggestionBadge);
        Assert.Equal("insufficient VRAM for llama3.2", beta.SuggestionProvenance);

        // The gate blocks while Beta (selected) has no context.
        Assert.False(vm.TargetsStep.IsValid);
        Assert.Equal("Enter a context (tokens) of at least 1 for every selected target.", vm.TargetsStep.NextGateHint);

        // The user OVERRIDES the suggestions before confirming (the assistance never locks).
        alpha.Context = "2048";
        beta.Context = "4096";
        vm.TargetsStep.RefreshGate();
        Assert.True(vm.TargetsStep.IsValid);

        vm.NextCommand.Execute(null);
        await WaitUntilAsync(() => vm.ReviewStep.Rows.Count == 2);
        await vm.ReviewStep.InstallCommand.ExecuteAsync(null);

        // The OVERRIDDEN values (not the suggestions) went out on the wire.
        Assert.Equal(
            [
                (Model, "Alpha", 2048),
                (Model, "Beta", 4096)
            ],
            fake.PullCalls);

        await vm.DisposeAsync();
    }

    // ------------------------------------------------------------------
    //  The per-target run row's progress application
    // ------------------------------------------------------------------

    [Fact]
    public void RunRow_ApplyProgress_PullLines_UpdateStatusAndByteBasedPercentage()
    {
        var row = new InstallRunRowVm("Alpha", Model, "llama3.2:latest-8k", 8192, false, "Online");
        row.SetRunning();

        row.ApplyProgress(new OllamaRouterModelPullProgress("pull", "pulling 34%", 100, 34, null, null));
        Assert.Equal("pulling 34%", row.StatusText);
        Assert.Equal(34, row.ProgressValue);
        Assert.False(row.ProgressIndeterminate);

        row.ApplyProgress(new OllamaRouterModelPullProgress("pull", "verifying sha256 digest", null, null, null, null));
        Assert.True(row.ProgressIndeterminate); // no daemon totals → indeterminate bar

        row.ApplyProgress(new OllamaRouterModelPullProgress("alias", "started", null, null, "llama3.2:latest-8k", 8192));
        Assert.Contains("creating alias", row.StatusText);
        Assert.True(row.ProgressIndeterminate);

        row.ApplyProgress(new OllamaRouterModelPullProgress("alias", "created", null, null, "llama3.2:latest-8k", 8192));
        Assert.Contains("alias llama3.2:latest-8k created", row.StatusText);
    }

    [Fact]
    public void RunRow_ApplyProgress_Present_FlipsTheRowToPresent()
    {
        var row = new InstallRunRowVm("Alpha", Model, null, null, true, "Online");
        row.SetRunning();

        row.ApplyProgress(new OllamaRouterModelPullProgress("pull", "present", null, null, null, null));

        Assert.Equal(InstallRunStatus.Present, row.Status);
        Assert.Equal("already present on the target", row.StatusText);
        Assert.Equal(100, row.ProgressValue);
    }

    // ------------------------------------------------------------------
    //  The pure InstallAssistSuggester
    // ------------------------------------------------------------------

    [Fact]
    public void SuggestContext_BenchmarkBeatsCatalogBeatsNone()
    {
        var benchmark = new[]
        {
            new BenchmarkContextResult("m", UsageRole.AgenticChat, 4096, 100, 1_000L, 30.0, 800.0, true, null),
            new BenchmarkContextResult("m", UsageRole.AgenticChat, 32768, 100, 2_000L, 18.0, 700.0, true, null),
            new BenchmarkContextResult("m", UsageRole.AgenticChat, 65536, 100, 3_000L, 9.0, 600.0, false, "oom")
        };

        // (1) benchmark: the LARGEST VIABLE row (65536 is non-viable → excluded).
        var fromBenchmark = InstallAssistSuggester.SuggestContext("T", benchmark, true, 49152, null);
        Assert.Equal(32768, fromBenchmark.Context);
        Assert.Equal("32768 tok — benchmark: viable @ 18.0 t/s", fromBenchmark.Provenance);

        // (2) catalog ceiling (no benchmark data).
        var fromCatalog = InstallAssistSuggester.SuggestContext("T", null, true, 49152, null);
        Assert.Equal(49152, fromCatalog.Context);
        Assert.Equal("49152 tok — fleet catalog ceiling on T", fromCatalog.Provenance);

        // (3) nothing: hint (the user sets the context manually).
        var fromNothing = InstallAssistSuggester.SuggestContext("T", null, false, null, null);
        Assert.Null(fromNothing.Context);
        Assert.Equal("no benchmark/catalog data — set the context manually", fromNothing.Provenance);

        // A not-capable target surfaces its veto reason (instead of the generic hint).
        var vetoed = InstallAssistSuggester.SuggestContext("T", null, false, null, "insufficient VRAM");
        Assert.Null(vetoed.Context);
        Assert.Equal("insufficient VRAM", vetoed.Provenance);
    }

    [Fact]
    public void SuggestTag_UserTagIsAuthoritative_ThenCatalog_ThenRegistryDefault()
    {
        Assert.Equal(("q8_0", "user-specified"),
            (InstallAssistSuggester.SuggestTag("q8_0", "m:13b-q4_K_M", "m").Tag,
             InstallAssistSuggester.SuggestTag("q8_0", "m:13b-q4_K_M", "m").Provenance));
        Assert.Equal(("13b-q4_K_M", "fleet catalog"),
            (InstallAssistSuggester.SuggestTag(null, "m:13b-q4_K_M", "m").Tag,
             InstallAssistSuggester.SuggestTag(null, "m:13b-q4_K_M", "m").Provenance));
        Assert.Equal(("latest", "no data — registry default"),
            (InstallAssistSuggester.SuggestTag(null, null, "m").Tag,
             InstallAssistSuggester.SuggestTag(null, null, "m").Provenance));
    }

    [Fact]
    public void PlanTarget_ComputesTheAlias_AndPassesThroughPresenceAndModelReference()
    {
        var (target, model, context, alias, present) = InstallAssistSuggester.PlanTarget("Alpha", Model, 8192, true);
        Assert.Equal("Alpha", target);
        Assert.Equal(Model, model);
        Assert.Equal(8192, context);
        Assert.Equal("llama3.2:13b-q4_K_M-8k", alias);
        Assert.True(present);

        var (_, noAliasModel, noAliasContext, noAliasName, noAliasPresent) = InstallAssistSuggester.PlanTarget("Beta", Model, null, false);
        Assert.Equal(Model, noAliasModel);
        Assert.Null(noAliasName);
        Assert.False(noAliasPresent);
    }

    // ------------------------------------------------------------------
    //  Per-target quant/tag + per-target context (task #131, manual/assisted unified)
    // ------------------------------------------------------------------

    [Fact]
    public async Task TagPrefill_ExplicitTag_TheRowPrefillsAndComposesItsOwnReference()
    {
        var fake = MakeFake();
        var vm = new InstallModelWizardViewModel(() => fake);

        vm.ModelStep.ModelReference = Model; // llama3.2:13b-q4_K_M
        vm.NextCommand.Execute(null);
        await OpenAtStepAsync(vm, vm.TargetsStep, 1);

        Assert.All(vm.TargetsStep.Rows, row =>
        {
            Assert.Equal("13b-q4_K_M", row.QuantTag); // the EXPLICIT step-0 tag
            Assert.Equal("llama3.2", row.BaseName);
            Assert.Equal(Model, row.EffectiveModelRef);
            Assert.True(row.HasValidModelRef);
            Assert.Null(row.ModelRefError);
        });

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task TagPrefill_TaglessReference_PrefillsEmpty_AndTheAliasUsesTheRegistryDefault()
    {
        var fake = MakeFake();
        var vm = new InstallModelWizardViewModel(() => fake);

        vm.ModelStep.ModelReference = ModelBase; // no explicit tag
        vm.NextCommand.Execute(null);
        await OpenAtStepAsync(vm, vm.TargetsStep, 1);

        var alpha = vm.TargetsStep.Rows[0]!;
        alpha.Selected = true;
        Assert.Equal(string.Empty, alpha.QuantTag); // NEVER prefilled with "latest"
        Assert.Equal(ModelBase, alpha.EffectiveModelRef);
        Assert.True(alpha.HasValidModelRef);

        // Alias ON + a valid context → the alias derives from the tagless ref (→ latest).
        alpha.Context = "8192";
        Assert.Equal("llama3.2:latest-8k", alpha.ComputedAlias);
        Assert.Equal("llama3.2:latest-8k", alpha.AliasHint);

        vm.NextCommand.Execute(null);
        await WaitUntilAsync(() => vm.ReviewStep.Rows.Count == 1);
        Assert.Equal(ModelBase, vm.ReviewStep.Rows[0]!.Model);
        Assert.Equal("llama3.2:latest-8k", vm.ReviewStep.Rows[0]!.Alias);

        await vm.ReviewStep.InstallCommand.ExecuteAsync(null);
        Assert.Equal([(ModelBase, "Alpha", 8192)], fake.PullCalls);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task EmptyTagFallback_ClearingThePrefillPullsTheStepZeroReferenceAsIs()
    {
        var fake = MakeFake();
        var vm = new InstallModelWizardViewModel(() => fake);

        vm.ModelStep.ModelReference = Model;
        vm.NextCommand.Execute(null);
        await OpenAtStepAsync(vm, vm.TargetsStep, 1);

        var alpha = vm.TargetsStep.Rows[0]!;
        alpha.Selected = true;
        alpha.QuantTag = string.Empty; // the user clears the prefilled tag
        alpha.Context = "8192";
        Assert.True(alpha.HasValidModelRef); // the fallback is valid by the step-0 gate
        Assert.Equal(Model, alpha.EffectiveModelRef);
        Assert.True(vm.TargetsStep.IsValid);

        vm.NextCommand.Execute(null);
        await WaitUntilAsync(() => vm.ReviewStep.Rows.Count == 1);
        await vm.ReviewStep.InstallCommand.ExecuteAsync(null);

        Assert.Equal([(Model, "Alpha", 8192)], fake.PullCalls);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task PerTargetTagAndContext_EachTargetIsPulledWithItsOwnReferenceAndContext()
    {
        var fake = MakeFake();
        var vm = new InstallModelWizardViewModel(() => fake);

        vm.ModelStep.ModelReference = Model;
        vm.NextCommand.Execute(null);
        await OpenAtStepAsync(vm, vm.TargetsStep, 1);

        var alpha = vm.TargetsStep.Rows[0]!;
        var beta = vm.TargetsStep.Rows[1]!;
        alpha.Selected = true;
        beta.Selected = true;
        alpha.QuantTag = "13b-q8_0"; // a DIFFERENT quant on Alpha
        alpha.Context = "16384";      // and a DIFFERENT context
        beta.Context = "8192";        // Beta stays prefilled (13b-q4_K_M)

        Assert.True(vm.TargetsStep.IsValid);
        Assert.Equal("llama3.2:13b-q8_0-16k", alpha.AliasHint); // per-target alias
        Assert.Equal("llama3.2:13b-q4_K_M-8k", beta.AliasHint);

        vm.NextCommand.Execute(null);
        await WaitUntilAsync(() => vm.ReviewStep.Rows.Count == 2);

        // The review rows show the PER-TARGET reference, context and alias.
        Assert.Equal("llama3.2:13b-q8_0", vm.ReviewStep.Rows[0]!.Model);
        Assert.Equal(16384, vm.ReviewStep.Rows[0]!.Context);
        Assert.Equal("llama3.2:13b-q8_0-16k", vm.ReviewStep.Rows[0]!.Alias);
        Assert.Equal("llama3.2:13b-q4_K_M", vm.ReviewStep.Rows[1]!.Model);
        Assert.Equal(8192, vm.ReviewStep.Rows[1]!.Context);
        Assert.Equal("llama3.2:13b-q4_K_M-8k", vm.ReviewStep.Rows[1]!.Alias);

        await vm.ReviewStep.InstallCommand.ExecuteAsync(null);

        // ONE pull per target, in plan order, with THAT target's own ref + alias context.
        Assert.Equal(
            [
                ("llama3.2:13b-q8_0", "Alpha", 16384),
                ("llama3.2:13b-q4_K_M", "Beta", 8192)
            ],
            fake.PullCalls);
        Assert.All(vm.ReviewStep.Rows, row => Assert.Equal(InstallRunStatus.Succeeded, row.Status));
        Assert.Equal("Succeeded", vm.ReviewStep.RunResultText);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task DistinctContexts_OnEqualReferences_YieldDistinctPerRowAliases()
    {
        var fake = MakeFake();
        var vm = new InstallModelWizardViewModel(() => fake);

        vm.ModelStep.ModelReference = Model;
        vm.NextCommand.Execute(null);
        await OpenAtStepAsync(vm, vm.TargetsStep, 1);

        var alpha = vm.TargetsStep.Rows[0]!;
        var beta = vm.TargetsStep.Rows[1]!;
        alpha.Selected = true;
        beta.Selected = true;
        alpha.Context = "8192";
        beta.Context = "16384";

        // Each row's alias derives from its OWN ref + ctx (no cross-target clash).
        Assert.Equal("llama3.2:13b-q4_K_M-8k", alpha.ComputedAlias);
        Assert.Equal("llama3.2:13b-q4_K_M-16k", beta.ComputedAlias);

        // Every valid plan has non-null aliases (no false-positive collision hint).
        var plan = vm.TargetsStep.BuildPlan();
        Assert.All(plan, entry => Assert.NotNull(entry.Alias));
        Assert.Equal("llama3.2:13b-q4_K_M-8k", plan[0].Alias);
        Assert.Equal("llama3.2:13b-q4_K_M-16k", plan[1].Alias);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task InvalidTag_BlocksTheGate_WithThePerRowHint_AndFixingReenablesNext()
    {
        var fake = MakeFake();
        var vm = new InstallModelWizardViewModel(() => fake);

        vm.ModelStep.ModelReference = Model;
        vm.NextCommand.Execute(null);
        await OpenAtStepAsync(vm, vm.TargetsStep, 1);

        var alpha = vm.TargetsStep.Rows[0]!;
        alpha.Selected = true;
        alpha.Context = "8192";
        alpha.QuantTag = "q4_K M"; // a space: NOT a valid tag character

        Assert.False(alpha.HasValidModelRef);
        Assert.Contains("q4_K M", alpha.ModelRefError);
        Assert.False(vm.TargetsStep.IsValid);
        Assert.Equal("Enter a valid quant/tag (e.g. q4_K_M) for every selected target.", vm.TargetsStep.NextGateHint);

        vm.NextCommand.Execute(null);
        Assert.Equal(1, vm.SelectedStep); // blocked at step 1

        // A second invalid shape (a colon inside the tag).
        alpha.QuantTag = "a:b";
        Assert.False(vm.TargetsStep.IsValid);
        Assert.Equal("Enter a valid quant/tag (e.g. q4_K_M) for every selected target.", vm.TargetsStep.NextGateHint);

        // Fixing the tag re-enables Next.
        alpha.QuantTag = "13b-q8_0";
        Assert.True(vm.TargetsStep.IsValid);
        Assert.Null(vm.TargetsStep.NextGateHint);
        vm.NextCommand.Execute(null);
        Assert.Equal(2, vm.SelectedStep);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task BuildPlan_EmitsPerTargetFiveFieldTuples_InFleetOrder()
    {
        var fake = MakeFake();
        var vm = new InstallModelWizardViewModel(() => fake);

        vm.ModelStep.ModelReference = Model;
        vm.NextCommand.Execute(null);
        await OpenAtStepAsync(vm, vm.TargetsStep, 1);

        var alpha = vm.TargetsStep.Rows[0]!;
        var beta = vm.TargetsStep.Rows[1]!;
        alpha.Selected = true;
        beta.Selected = true;
        alpha.QuantTag = "13b-q8_0";
        alpha.Context = "16384";
        beta.Context = "8192";

        var plan = vm.TargetsStep.BuildPlan();
        Assert.Equal(
            [
                ("Alpha", "llama3.2:13b-q8_0", 16384, "llama3.2:13b-q8_0-16k", false),
                ("Beta", "llama3.2:13b-q4_K_M", 8192, "llama3.2:13b-q4_K_M-8k", false)
            ],
            plan);

        // Alias OFF: the context is NOT required and the tuple's context + alias are null.
        vm.TargetsStep.CreateAlias = false;
        alpha.Context = string.Empty;
        Assert.True(vm.TargetsStep.IsValid);
        Assert.Equal(
            [
                ("Alpha", "llama3.2:13b-q8_0", (int?)null, null, false),
                ("Beta", "llama3.2:13b-q4_K_M", (int?)null, null, false)
            ],
            vm.TargetsStep.BuildPlan());

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task ManualAndAssistedModes_ShareTheSamePerRowGateAndPlanBehavior()
    {
        // VM-level unification (task #131): the gate/plan behaves identically for manual and
        // assisted rows — every row carries its own tag + context, the shared field is gone.
        var fake = MakeFake();
        var vm = new InstallModelWizardViewModel(() => fake);

        vm.ModelStep.ModelReference = Model;
        vm.NextCommand.Execute(null);
        await OpenAtStepAsync(vm, vm.TargetsStep, 1);

        // MANUAL rows: the per-row inputs exist and drive the gate + alias hint.
        var alpha = vm.TargetsStep.Rows[0]!;
        Assert.False(alpha.Selected); // manual mode: no preselection
        alpha.Selected = true;
        Assert.False(vm.TargetsStep.IsValid); // alias ON, blank context
        Assert.Equal("Enter a context (tokens) of at least 1 for every selected target.", vm.TargetsStep.NextGateHint);
        alpha.Context = "8192";
        Assert.True(vm.TargetsStep.IsValid);
        Assert.Equal("llama3.2:13b-q4_K_M-8k", alpha.AliasHint);

        // Toggle the alias switch: the per-row hints flip with it (parent-driven).
        vm.TargetsStep.CreateAlias = false;
        Assert.Null(alpha.AliasHint);
        alpha.Context = string.Empty;
        Assert.True(vm.TargetsStep.IsValid); // the context is NOT required now
        vm.TargetsStep.CreateAlias = true;
        Assert.True(alpha.ComputedAlias is null); // valid ref, but no context
        Assert.False(vm.TargetsStep.IsValid);

        await vm.DisposeAsync();
    }

    // ------------------------------------------------------------------
    //  Close signaling (Cancel before any run / Done after)
    // ------------------------------------------------------------------

    [Fact]
    public async Task CancelCommand_SignalsClose_WithoutARunResult()
    {
        var vm = new InstallModelWizardViewModel(() => MakeFake());

        bool? signaled = null;
        string? result = null;
        vm.CloseRequestedSignal += (_, r) => { signaled = true; result = r; };
        vm.CancelCommand.Execute(null);

        Assert.True(signaled);
        Assert.Null(result);

        await vm.DisposeAsync();
    }
}
