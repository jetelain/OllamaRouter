namespace OllamaRouter.Services;

/// <summary>
/// Composite <see cref="IGpuVramProvider"/> that probes the provided vendor providers in the
/// exact order supplied at construction time and returns the first value greater than 0
/// (short-circuit: later providers are not probed). Returns 0 when no provider reports a
/// non-zero value, which preserves the "no usable GPU / fall through to Remote" contract.
/// </summary>
public sealed class MultiVendorVramProvider : IGpuVramProvider
{
    private readonly IReadOnlyList<IGpuVramProvider> _providers;
    private readonly ILogger<MultiVendorVramProvider>? _logger;

    public MultiVendorVramProvider(
        IReadOnlyList<IGpuVramProvider> providers,
        ILogger<MultiVendorVramProvider>? logger = null)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _logger = logger;
    }

    public int GetFreeVramMB()
    {
        foreach (var provider in _providers)
        {
            int freeMB = provider.GetFreeVramMB();
            _logger?.LogTrace("VRAM probe {Provider}: {Free}MB", provider.GetType().Name, freeMB);
            if (freeMB > 0)
            {
                return freeMB;
            }
        }

        return 0;
    }
}
