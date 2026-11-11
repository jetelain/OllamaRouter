using System.Net;
using OllamaRouter.App.ViewModels;
using OllamaRouter.Client;
using OllamaRouter.Endpoints.RouterApi.Cartography;
using OllamaRouter.Endpoints.RouterApi.Preload;

namespace OllamaRouter.Tests.App;

/// <summary>
/// <see cref="CartographyViewModel"/> / <see cref="PreloadDialogViewModel"/> tests
/// (task #102, spec §7): the model × target matrix cells (present / capable / ceiling /
/// resident footprint / veto reason), the loaded-process flattening, and the
/// preview-then-preload sequence with the exact request payloads.
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
    public async Task RefreshAsync_BuildsTheMatrix_WithCellStatesAndFootprint()
    {
        _client.CartographyResponse = BuildCartography();

        await _viewModel.RefreshAsync();

        Assert.Equal(2, _viewModel.Nodes.Count);
        Assert.Equal("Local", _viewModel.Nodes[0].Name);
        Assert.Equal("Cloud", _viewModel.Nodes[1].Name); // Cloud pseudo-target last
        Assert.Equal(2, _viewModel.Models.Count);

        var llama = _viewModel.Models[0];
        Assert.Equal("llama3:8b", llama.Name);
        Assert.Equal("llama", llama.Architecture);
        Assert.Equal(4096, llama.RecommendedContextWindow);
        Assert.True(llama.CloudOverflowAvailable);
        Assert.Equal(2, llama.Cells.Count);

        var localCell = Assert.Single(llama.Cells, c => c.Target == "Local");
        Assert.True(localCell.Present);
        Assert.True(localCell.Capable);
        Assert.True(localCell.IsResident);
        Assert.Equal(8192, localCell.ContextCeiling);
        Assert.Equal(1, localCell.InFlightRequests);
        Assert.Null(localCell.VetoReason);
        Assert.NotNull(localCell.LoadedSizeHuman); // the resident footprint in humanized bytes
        Assert.Equal("Q4_K_M", localCell.Quantization);
        Assert.Contains("resident", localCell.Summary);

        var cloudCell = Assert.Single(llama.Cells, c => c.Target == "Cloud");
        Assert.False(cloudCell.Present);
        Assert.Equal("not present", cloudCell.Summary);
    }

    [Fact]
    public async Task RefreshAsync_SurfacesVetoReasons_AndFlattensLoadedProcesses()
    {
        _client.CartographyResponse = BuildCartography();

        await _viewModel.RefreshAsync();

        var qwen = _viewModel.Models[1];
        var vetoed = Assert.Single(qwen.Cells, c => c.Target == "Local");
        Assert.True(vetoed.Present);
        Assert.False(vetoed.Capable);
        Assert.Equal("vram too small for qwen2:72b", vetoed.VetoReason);
        Assert.Contains("vram too small", vetoed.Summary);

        var process = Assert.Single(_viewModel.LoadedProcesses);
        Assert.Equal("llama3:8b", process.Name);
        Assert.Equal("Local", process.Target);
        Assert.Equal(5L * 1024 * 1024, process.SizeVramBytes);
        Assert.Equal("Online", process.Liveness);
        Assert.False(process.Stale);
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

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task DisposeAsync() => await _viewModel.DisposeAsync();
}
