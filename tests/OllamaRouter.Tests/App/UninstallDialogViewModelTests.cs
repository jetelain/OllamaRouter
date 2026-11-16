using System.Net;
using OllamaRouter.App.ViewModels;
using OllamaRouter.Endpoints.RouterApi.Models;

namespace OllamaRouter.Tests.App;

/// <summary>
/// <see cref="UninstallDialogViewModel"/> tests (task #126): the all-rows-preselected
/// default, the zero-selection validation (NO client call), the exact payload (model name +
/// ONLY the checked targets, unchecked targets absent), the per-target result mapping (mixed
/// success with error text surfaced, <see cref="UninstallDialogViewModel.HasResults"/>), the
/// 404 error surface (status + message, no partial per-target list), the busy guard and the
/// constructor guards.
/// </summary>
public sealed class UninstallDialogViewModelTests
{
    private static (UninstallDialogViewModel Vm, FakeRouterApiClient Client) CreateVm(params string[] presentTargets)
    {
        var client = new FakeRouterApiClient();
        var vm = new UninstallDialogViewModel("llama3:8b", presentTargets, () => client);
        return (vm, client);
    }

    [Fact]
    public void Constructor_ListsExactlyThePresentTargets_AllPreselected()
    {
        var (vm, _) = CreateVm("Local", "Remote");

        Assert.Equal("llama3:8b", vm.ModelName);
        Assert.Equal(["Local", "Remote"], vm.Targets.Select(t => t.Target).ToArray());
        Assert.All(vm.Targets, t => Assert.True(t.IsSelected));
        Assert.All(vm.Targets, t => Assert.Null(t.Succeeded));
        Assert.All(vm.Targets, t => Assert.Null(t.ResultText));
        Assert.False(vm.HasResults);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public void Constructor_EmptyPresentTargets_Throws()
    {
        Assert.Throws<ArgumentException>(() => new UninstallDialogViewModel("m", [], () => new FakeRouterApiClient()));
        Assert.Throws<ArgumentNullException>(() => new UninstallDialogViewModel("m", null!, () => new FakeRouterApiClient()));
    }

    [Fact]
    public async Task UninstallCommand_NoTargetsChecked_ValidationError_WithoutCallingTheClient()
    {
        var (vm, client) = CreateVm("Local", "Remote");
        vm.Targets[0].IsSelected = false;
        vm.Targets[1].IsSelected = false;

        await vm.UninstallCommand.ExecuteAsync(null);

        Assert.Equal("Select at least one target to uninstall.", vm.ErrorMessage);
        Assert.Empty(client.UninstallCalls);
        Assert.False(vm.HasResults);
    }

    [Fact]
    public async Task UninstallCommand_SendsExactlyTheCheckedTargets_WithTheModelName()
    {
        var (vm, client) = CreateVm("Local", "Remote");
        vm.Targets[1].IsSelected = false; // unchecked targets must be ABSENT from the payload

        await vm.UninstallCommand.ExecuteAsync(null);

        var call = Assert.Single(client.UninstallCalls);
        Assert.Equal("llama3:8b", call.Model);
        Assert.Equal(["Local"], call.Targets.ToArray());
        Assert.Null(vm.ErrorMessage);
        Assert.True(vm.HasResults);
    }

    [Fact]
    public async Task UninstallCommand_MixedResults_MapPerTarget_WithSuccessAndErrorText()
    {
        var (vm, client) = CreateVm("Local", "Remote");
        client.UninstallResponse = new RouterModelUninstallResponse("llama3:8b",
        [
            new RouterModelUninstallTargetResult("Local", true),
            new RouterModelUninstallTargetResult("Remote", false, "http 404: model 'llama3:8b' not found"),
        ]);

        await vm.UninstallCommand.ExecuteAsync(null);

        var local = vm.Targets[0];
        var remote = vm.Targets[1];
        Assert.True(local.Succeeded);
        Assert.Equal("deleted", local.ResultText);
        Assert.False(remote.Succeeded);
        Assert.Equal("http 404: model 'llama3:8b' not found", remote.ResultText);
        Assert.True(vm.HasResults);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task UninstallCommand_On404_SurfacesStatusAndMessage_WithoutPartialRows()
    {
        var (vm, client) = CreateVm("Local");
        client.UninstallError = (HttpStatusCode.NotFound, "model not found: nope");

        await vm.UninstallCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("404", vm.ErrorMessage!);
        Assert.Contains("model not found: nope", vm.ErrorMessage!);
        Assert.False(vm.HasResults); // no partial per-target list on the error path
        Assert.All(vm.Targets, t => Assert.Null(t.Succeeded));
        Assert.Single(client.UninstallCalls);
    }

    [Fact]
    public async Task UninstallCommand_BusyGuard_SecondConcurrentCommandIgnored()
    {
        var client = new FakeRouterApiClient();
        var vm = new UninstallDialogViewModel("m", ["Local"], () => client);
        var block = new TaskCompletionSource();
        client.UninstallBlock = block.Task;

        var first = vm.UninstallAsync();
        while (!vm.IsBusy)
        {
            await Task.Yield();
        }

        // The second command hits the busy guard and completes without a client call.
        var second = vm.UninstallAsync();
        Assert.True(second.IsCompleted);

        block.SetResult();
        await first;

        Assert.Single(client.UninstallCalls);
        Assert.True(vm.HasResults);
    }
}
