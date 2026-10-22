using OllamaRouter.Services;

namespace OllamaRouter.Extensions;

/// <summary>
/// Registers the multi-vendor GPU VRAM providers and the composite <see cref="MultiVendorVramProvider"/>
/// as the <see cref="IGpuVramProvider"/> singleton.
/// </summary>
public static class GpuVramServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IProcessRunner"/> (internal, via the concrete <c>ProcessRunner</c>), the vendor
    /// providers (NVIDIA always; AMD/Intel on Linux; the degraded Windows WMI provider on Windows), and the
    /// composite <see cref="MultiVendorVramProvider"/> as the <see cref="IGpuVramProvider"/> singleton.
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
        return services;
    }
}
