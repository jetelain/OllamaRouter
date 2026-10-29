using OllamaRouter.Services;
using OllamaRouter.Services.Hardware;

namespace OllamaRouter.Extensions;

/// <summary>
/// Registers the multi-vendor GPU VRAM providers and the composite <see cref="MultiVendorVramProvider"/>
/// as the <see cref="IGpuVramProvider"/> singleton.
/// </summary>
public static class GpuVramServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IProcessRunner"/> (internal, via the concrete <c>ProcessRunner</c>), the vendor
    /// providers (NVIDIA always; AMD/Intel on Linux; the degraded Windows WMI provider on Windows), the
    /// composite <see cref="MultiVendorVramProvider"/> as the <see cref="IGpuVramProvider"/> singleton, and the
    /// local workstation hardware profiler (<see cref="LocalHardwareProfileProvider"/>).
    /// Probe order is fixed here: NVIDIA, AMD, Intel, Windows WMI (first non-zero value wins).
    /// </summary>
    public static IServiceCollection AddGpuVramProviders(this IServiceCollection services)
    {
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton(sp => new NvidiaSmiVramProvider(sp.GetRequiredService<IProcessRunner>()));
        if (OperatingSystem.IsLinux())
        {
            services.AddSingleton<RocmSmiVramProvider>();
            services.AddSingleton<IntelXpuVramProvider>();
        }
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<WindowsVramProvider>();
        }
        services.AddSingleton<IGpuVramProvider>(sp =>
        {
            var list = new List<IGpuVramProvider> { sp.GetRequiredService<NvidiaSmiVramProvider>() };
            if (OperatingSystem.IsLinux())
            {
                list.Add(sp.GetRequiredService<RocmSmiVramProvider>());
                list.Add(sp.GetRequiredService<IntelXpuVramProvider>());
            }
            if (OperatingSystem.IsWindows())
            {
                list.Add(sp.GetRequiredService<WindowsVramProvider>());
            }
            return new MultiVendorVramProvider(list, sp.GetRequiredService<ILogger<MultiVendorVramProvider>>());
        });

        // Local workstation hardware profiler (fail-soft OS probes; lazy single-flight cache).
        // Explicit factory: the optional test-injection Func<...> constructor parameters must NOT
        // be resolved from DI (NodeHardwareProfile is not a registered service).
        services.AddSingleton<IHardwareProfileProvider>(sp => new LocalHardwareProfileProvider(
            sp.GetRequiredService<IProcessRunner>(),
            sp.GetRequiredService<IGpuVramProvider>(),
            sp.GetRequiredService<ILogger<LocalHardwareProfileProvider>>()));
        return services;
    }
}
