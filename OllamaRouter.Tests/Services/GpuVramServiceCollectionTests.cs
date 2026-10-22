using Microsoft.Extensions.DependencyInjection;
using OllamaRouter.Extensions;
using OllamaRouter.Services;

namespace OllamaRouter.Tests.Services;

public class GpuVramServiceCollectionTests
{
    [Fact]
    public void AddGpuVramProviders_ResolvesCompositeAsIGpuVramProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGpuVramProviders();

        using var providerFactory = services.BuildServiceProvider();
        var gpuProvider = providerFactory.GetRequiredService<IGpuVramProvider>();

        Assert.IsType<MultiVendorVramProvider>(gpuProvider);
    }

    [Fact]
    public void AddGpuVramProviders_ResolvesProcessRunnerAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGpuVramProviders();

        using var providerFactory = services.BuildServiceProvider();
        var runner1 = providerFactory.GetRequiredService<IProcessRunner>();
        var runner2 = providerFactory.GetRequiredService<IProcessRunner>();

        Assert.IsType<ProcessRunner>(runner1);
        Assert.Same(runner1, runner2);
    }

    [Fact]
    public void AddGpuVramProviders_NvidiaProviderIsResolvableAndSharedSingleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGpuVramProviders();

        using var providerFactory = services.BuildServiceProvider();
        var nvidia1 = providerFactory.GetRequiredService<NvidiaSmiVramProvider>();
        var nvidia2 = providerFactory.GetRequiredService<NvidiaSmiVramProvider>();

        Assert.Same(nvidia1, nvidia2);
    }

    [Fact]
    public void AddGpuVramProviders_CompositeUsesSharedFakeRunner_ResolvedNvidiaIsInChain()
    {
        // Register a fake IProcessRunner first and drop the later concrete registration, so
        // every vendor inside the composite receives the same singleton fake (AC-18). A
        // canned non-zero nvidia-smi result proves the fake reached the NVIDIA provider via
        // the composite (short-circuits before AMD/Intel probes).
        const string nvidiaArgs = "--query-gpu=memory.free --format=csv,noheader,nounits";
        var fakeRunner = new FakeProcessRunner().Succeeds("nvidia-smi", nvidiaArgs, "12345\n");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProcessRunner>(fakeRunner);
        services.AddGpuVramProviders();
        var iProcessRunnerDescriptors = services.Where(
                d => d.ServiceType == typeof(IProcessRunner)).ToArray();
        if (iProcessRunnerDescriptors.Length > 1)
        {
            // The extension registers its own IProcessRunner; the last one wins, so remove the
            // original (registered after the fake) to keep the fake as the effective singleton.
            services.Remove(iProcessRunnerDescriptors[iProcessRunnerDescriptors.Length - 1]);
        }

        using var providerFactory = services.BuildServiceProvider();
        var nvidia = providerFactory.GetRequiredService<NvidiaSmiVramProvider>();
        var composite = (MultiVendorVramProvider)providerFactory.GetRequiredService<IGpuVramProvider>();

        Assert.Equal(12345, composite.GetFreeVramMB());

        // The fake (shared with the concrete nvidia singleton) saw exactly the nvidia-smi probe;
        // later vendors were short-circuited.
        Assert.Equal(1, fakeRunner.InvocationCount("nvidia-smi", nvidiaArgs));
        _ = nvidia;
    }
}
