namespace OllamaRouter.Services;

/// <summary>
/// Queries <c>nvidia-smi</c> to get the free VRAM. Returns 0 if the tool is not available
/// (for example on an environment without an NVIDIA GPU) or on any parsing failure.
/// </summary>
public sealed class NvidiaSmiVramProvider : IGpuVramProvider
{
    private const int TimeoutMs = 5000;

    private readonly IProcessRunner _processRunner;

    internal NvidiaSmiVramProvider(IProcessRunner processRunner)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public int GetFreeVramMB()
    {
        try
        {
            var result = _processRunner.Run("nvidia-smi", "--query-gpu=memory.free --format=csv,noheader,nounits", TimeoutMs);
            if (!result.Succeeded)
            {
                return 0;
            }

            foreach (var rawLine in result.StdOut.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                return int.TryParse(line, out int vram) ? vram : 0;
            }

            return 0;
        }
        catch
        {
            return 0;
        }
    }
}
