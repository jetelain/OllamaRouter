using System.Net;
using OllamaRouter.App.ViewModels;
using OllamaRouter.Client;
using OllamaRouter.Endpoints.RouterApi.Cartography;
using OllamaRouter.Endpoints.RouterApi.Preload;

namespace OllamaRouter.Tests.App;

/// <summary>
/// <see cref="CartographyViewModel"/> / <see cref="PreloadDialogViewModel"/> tests
/// (task #102, spec §7; task #119): the model × target matrix cells (present / capable /
/// ceiling / veto reason, no Cloud column, no resident / in-flight tokens), the
/// loaded-process flattening, and the preview-then-preload sequence with the exact request
/// payloads.
/// </summary>
public sealed class CartographyViewModelTests : IAsyncLifetime
{
    private readonly FakeRouterApiClient _client = new();
    private readonly CartographyViewModel _viewModel;

    public CartographyViewModelTests()
    {
        _viewModel = new CartographyViewModel(() => _client);
    }

    internal static RouterCartographyResponse BuildCartography()
    {
        var nodes = new[]
        {
            new CartographyNodeRow(
                "Local", "local", true, "Online", false, "4k", 1, 5L * 1024 * 1024,
                [new CartographyNodeLoadedModel("llama3:8b", 5L * 1024 * 1024, "Q4_K_M", 8192)],
                "http://127.0.0.1:11434/", null,
                new CartographyNodeHardware(false, "RTX 4090", "NVIDIA", 24576, 32768)),
            new CartographyNodeRow("Cloud", "cloud", true, "Online", false, "baseline", 0, 0, []),
        };
        var models = new[]
        {
            new CartographyModelRow(
                "llama3:8b", "llama", 49L * 1024 * 1024 * 1024 / 4, 8192, 4096, true,
                [
                    new CartographyDeploymentRow(
                        "Local", true, true, 8192, true, 1, null, 5L * 1024 * 1024, "Q4_K_M"),
                ],
                8000_000_000, "Q4_K_M"),
            new CartographyModelRow(
                "qwen2:72b", "llama", 42L * 1024 * 1024 * 1024, 32768, 32768, true,
                [
                    new CartographyDeploymentRow(
                        "Local", true, false, 0, false, 0, "vram too small for qwen2:72b", null, null),
                ],
                72_000_000_000, "Q4_K_M"),
        };
        var loaded = new[]
        {
            new CartographyLoadedModelRow(
                "llama3:8b", "Local", 5L * 1024 * 1024, "4k", "Online", false, "Q4_K_M", 8192),
        };
        return new RouterCartographyResponse(nodes, models, loaded);
    }

    [Fact]
    public async Task RefreshAsync_BuildsTheMatrix_WithCellStates()
    {
        _client.CartographyResponse = BuildCartography();

        await _viewModel.RefreshAsync();

        var localNode = Assert.Single(_viewModel.Nodes);
        Assert.Equal("Local", localNode.Name); // the Cloud pseudo-target is dropped
        Assert.Equal(2, _viewModel.Models.Count);

        var llama = _viewModel.Models[0];
        Assert.Equal("llama3:8b", llama.Name);
        Assert.Equal("llama", llama.Architecture);
        Assert.Equal(4096, llama.RecommendedContextWindow);
        Assert.True(llama.CloudOverflowAvailable);

        var localCell = Assert.Single(llama.Cells);
        Assert.Equal("Local", localCell.Target);
        Assert.True(localCell.Present);
        Assert.True(localCell.Capable);
        Assert.Equal(8192, localCell.ContextCeiling);
        Assert.Null(localCell.VetoReason);
        Assert.Equal("Q4_K_M", localCell.Quantization);
        Assert.Equal("capable · Q4_K_M", localCell.Summary);
        AssertNoFootprintTokens(localCell.Summary);
    }

    [Fact]
    public async Task RefreshAsync_DropsTheCloudPseudoTarget_ColumnsAndCellsStayInSync()
    {
        var response = BuildCartography();
        var local = response.Nodes[0];
        var remote = new CartographyNodeRow(
            "Remote", "remote", true, "Online", false, "8k", 0, 0,
            [], "http://remote:11434/", null,
            new CartographyNodeHardware(true, "A100", "NVIDIA", 32768, 65536));
        var cloud = response.Nodes[1];
        Assert.Equal("Cloud", cloud.Name);

        var models = response.Models.Select(model => new CartographyModelRow(
            model.Name, model.Architecture, model.WeightSizeBytes, model.NativeContextWindow,
            model.RecommendedContextWindow, model.CloudOverflowAvailable,
            [
                .. model.Deployments,
                new CartographyDeploymentRow("Remote", true, true, 16384, true, 3, null, 1L * 1024 * 1024, "Q8_0"),
            ],
            model.RecommendedContextWindow * 1000, model.Quantization)).ToArray();

        _client.CartographyResponse = new RouterCartographyResponse(
            [local, remote, cloud], models, response.LoadedModels);

        await _viewModel.RefreshAsync();

        Assert.Equal(["Local", "Remote"], _viewModel.Nodes.Select(n => n.Name).ToArray());
        Assert.DoesNotContain(_viewModel.Nodes, n => string.Equals(n.Name, "Cloud", StringComparison.OrdinalIgnoreCase));

        var remoteCell = _viewModel.Models[0].Cells.First(c => c.Target == "Remote");
        Assert.Equal("Q8_0", remoteCell.Quantization);
        Assert.Equal("capable · Q8_0", remoteCell.Summary); // capable flag + quant only

        foreach (var model in _viewModel.Models)
        {
            Assert.Equal(2, model.Cells.Count);
            Assert.DoesNotContain(model.Cells, c => c.Target == "Cloud");

            foreach (var cell in model.Cells)
            {
                AssertNoFootprintTokens(cell.Summary);
                if (cell.Quantization is not null)
                {
                    Assert.Contains(cell.Quantization, cell.Summary);
                }
                if (cell.Capable)
                {
                    Assert.Contains("capable", cell.Summary);
                }
            }
        }
    }

    [Fact]
    public async Task RefreshAsync_SurfacesVetoReasons_AndFlattensLoadedProcesses()
    {
        _client.CartographyResponse = BuildCartography();

        await _viewModel.RefreshAsync();

        var qwen = _viewModel.Models[1];
        var vetoed = Assert.Single(qwen.Cells);
        Assert.Equal("Local", vetoed.Target);
        Assert.True(vetoed.Present);
        Assert.False(vetoed.Capable);
        Assert.Equal("vram too small for qwen2:72b", vetoed.VetoReason);
        Assert.Contains("vram too small", vetoed.Summary);
        AssertNoFootprintTokens(vetoed.Summary);

        var process = Assert.Single(_viewModel.LoadedProcesses);
        Assert.Equal("llama3:8b", process.Name);
        Assert.Equal("Local", process.Target);
        Assert.Equal(5L * 1024 * 1024, process.SizeVramBytes);
        Assert.Equal("Online", process.Liveness);
        Assert.False(process.Stale);
    }

    private static void AssertNoFootprintTokens(string summary)
    {
        var lowered = summary.ToLowerInvariant();
        Assert.DoesNotContain("in flight", lowered);
        Assert.DoesNotContain("resident", lowered);
    }

    [Fact]
    public async Task PreloadDialog_PreviewsThenPreloads_WithTheExactPayloads()
    {
        _client.CartographyResponse = BuildCartography();
        await _viewModel.RefreshAsync();
        _client.PreloadResponse = new RouterModelPreloadResponse(
            "llama3:8b", "Local", 900, 4096, 4096, true, true, 1234,
            [new RouterModelPreloadCandidate("Local", true, 1, true, 8192, false, true, null)],
            "resident on Local");

        var dialog = _viewModel.CreatePreloadDialog("llama3:8b");
        dialog.KeepAliveSeconds = "1200";
        dialog.ContextIntentTokens = "2048";

        await dialog.PreviewCommand.ExecuteAsync(null);

        var preview = Assert.Single(_client.PreviewCalls);
        Assert.Equal("llama3:8b", preview.Model);
        Assert.Equal(1200, preview.KeepAliveSeconds);
        Assert.Equal(2048, preview.ContextIntentTokens);
        Assert.Null(preview.Target); // blank pin = auto
        var previewResult = Assert.IsType<RouterModelPreloadResponse>(dialog.PreviewResult);
        Assert.Equal("Local", previewResult.SelectedTarget);
        Assert.Single(previewResult.Candidates);
        Assert.Empty(_client.PreloadCalls); // preview is side-effect-free

        dialog.TargetPin = "Auto";
        await dialog.PreloadCommand.ExecuteAsync(null);

        var preload = Assert.Single(_client.PreloadCalls);
        Assert.Equal("llama3:8b", preload.Model);
        Assert.NotNull(preload.Request);
        Assert.Equal(1200, preload.Request!.KeepAliveSeconds);
        Assert.Equal(2048, preload.Request.ContextIntentTokens);
        Assert.Null(preload.Request.Target); // "Auto" = no pin
        var result = Assert.IsType<RouterModelPreloadResponse>(dialog.PreloadResult);
        Assert.Equal("Local", result.SelectedTarget);
        Assert.Equal(900, result.AppliedKeepAliveSeconds);
        Assert.Equal(4096, result.AppliedNumCtx);
        Assert.True(result.LoadAccepted);
        Assert.True(result.ResidencyConfirmed);
        Assert.Equal(1234, result.PreloadDurationMs);
        Assert.Equal("resident on Local", result.Reason);
        Assert.Null(dialog.ErrorMessage);
    }

    [Fact]
    public async Task PreloadDialog_TargetPin_IsSentWithThePreloadRequest()
    {
        _client.CartographyResponse = BuildCartography();
        await _viewModel.RefreshAsync();

        var dialog = _viewModel.CreatePreloadDialog("llama3:8b");
        dialog.TargetPin = "Local";

        await dialog.PreloadCommand.ExecuteAsync(null);

        var preload = Assert.Single(_client.PreloadCalls);
        Assert.Equal("Local", preload.Request!.Target);
    }

    [Fact]
    public async Task PreloadDialog_On404_SurfacesTheError()
    {
        _client.CartographyResponse = BuildCartography();
        await _viewModel.RefreshAsync();
        _client.NextCallException = new OllamaRouterApiException(HttpStatusCode.NotFound, "model unknown: nope");

        var dialog = _viewModel.CreatePreloadDialog("nope");
        await dialog.PreloadCommand.ExecuteAsync(null);

        Assert.Contains("404", dialog.ErrorMessage!);
        Assert.Contains("model unknown: nope", dialog.ErrorMessage!);
    }

    [Fact]
    public async Task PreloadDialog_InvalidInputs_ValidationError_WithoutCallingTheClient()
    {
        _client.CartographyResponse = BuildCartography();
        await _viewModel.RefreshAsync();

        var dialog = _viewModel.CreatePreloadDialog("llama3:8b");
        dialog.KeepAliveSeconds = "-5";
        await dialog.PreloadCommand.ExecuteAsync(null);
        Assert.NotNull(dialog.ErrorMessage);
        Assert.Empty(_client.PreloadCalls);
        Assert.Empty(_client.PreviewCalls);

        dialog.KeepAliveSeconds = "-1"; // indefinitely = valid
        dialog.ContextIntentTokens = "-3";
        await dialog.PreviewCommand.ExecuteAsync(null);
        Assert.NotNull(dialog.ErrorMessage);
        Assert.Empty(_client.PreviewCalls);
    }

    [Fact]
    public async Task UninstallDialog_SourcesExactlyThePresentTargets_NoCloudNoAbsentTargets()
    {
        // Extend the fixture: llama3:8b is present on Local AND a Remote node (Remote gets a
        // capable deployment); qwen2:72b is present on Local but vetoed (present = uninstallable);
        // a third model (mistral:7b) is absent everywhere (its row must not offer uninstall).
        var response = BuildCartography();
        var remote = new CartographyNodeRow(
            "Remote", "remote", true, "Online", false, "8k", 0, 0,
            [], "http://remote:11434/", null,
            new CartographyNodeHardware(true, "A100", "NVIDIA", 32768, 65536));
        var models = response.Models.Select(model => new CartographyModelRow(
            model.Name, model.Architecture, model.WeightSizeBytes, model.NativeContextWindow,
            model.RecommendedContextWindow, model.CloudOverflowAvailable,
            [
                .. model.Deployments,
                model.Name == "llama3:8b"
                    ? new CartographyDeploymentRow("Remote", true, true, 16384, true, 3, null, 1L * 1024 * 1024, "Q8_0")
                    : new CartographyDeploymentRow("Remote", false, false, 0, false, 0, null, null, null),
            ],
            model.RecommendedContextWindow * 1000, model.Quantization)).ToArray();
        models = [
            .. models,
            new CartographyModelRow(
                "mistral:7b", "llama", 4L * 1024 * 1024 * 1024, 8192, 8192, false,
                [new CartographyDeploymentRow("Local", false, true, 8192, true, 0, null, null, null)],
                7_000_000_000, null),
        ];
        _client.CartographyResponse = new RouterCartographyResponse(
            [.. response.Nodes.Where(n => n.Name != "Cloud"), remote], models, response.LoadedModels);

        await _viewModel.RefreshAsync();

        // AC1: the button flag mirrors presence.
        var llama = _viewModel.Models.First(m => m.Name == "llama3:8b");
        var qwen = _viewModel.Models.First(m => m.Name == "qwen2:72b");
        var mistral = _viewModel.Models.First(m => m.Name == "mistral:7b");
        Assert.True(llama.HasPresentTargets);
        Assert.True(qwen.HasPresentTargets); // present-but-vetoed still carries the files
        Assert.False(mistral.HasPresentTargets); // absent everywhere → button hidden

        // AC2: the dialog lists EXACTLY the present targets of the row (matrix order).
        var dialog = _viewModel.CreateUninstallDialog(llama);
        Assert.Equal("llama3:8b", dialog.ModelName);
        Assert.Equal(["Local", "Remote"], dialog.Targets.Select(t => t.Target).ToArray());
        Assert.DoesNotContain(dialog.Targets, t => t.Target == "Cloud");
        Assert.All(dialog.Targets, t => Assert.True(t.IsSelected)); // pre-checked

        var absentDialog = Assert.Throws<ArgumentException>(() => _viewModel.CreateUninstallDialog(mistral));
        Assert.NotNull(absentDialog); // the button hides this row in practice (defensive guard)
    }

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task DisposeAsync() => await _viewModel.DisposeAsync();
}
