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

    public Task<LocalGpuSnapshot> GetLocalGpuSnapshotAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = _processRunner.Run(
                "nvidia-smi",
                "--query-gpu=name,memory.total --format=csv,noheader",
                TimeoutMs);
            if (!result.Succeeded)
            {
                return Task.FromResult(LocalGpuSnapshot.Unknown);
            }

            var devices = new List<string>();
            var totalVramMB = 0;
            foreach (var rawLine in result.StdOut.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                var separatorIndex = line.IndexOf(',');
                if (separatorIndex <= 0 || separatorIndex == line.Length - 1)
                {
                    continue;
                }

                var name = line[..separatorIndex].Trim();
                var totalPart = line[(separatorIndex + 1)..].Trim();
                // nvidia-smi reports totals as e.g. "16384 MiB"; parse the leading number.
                var numberEnd = 0;
                while (numberEnd < totalPart.Length && char.IsDigit(totalPart[numberEnd]))
                {
                    numberEnd++;
                }
                if (numberEnd > 0 && int.TryParse(totalPart[..numberEnd], out var totalMB))
                {
                    var displayName = name.StartsWith("NVIDIA", StringComparison.OrdinalIgnoreCase) ? name : $"NVIDIA {name}";
                    devices.Add($"{displayName} ({totalMB} MB)");
                    totalVramMB += totalMB;
                }
            }

            return Task.FromResult(devices.Count > 0
                ? new LocalGpuSnapshot("Nvidia", devices, totalVramMB)
                : LocalGpuSnapshot.Unknown);
        }
        catch
        {
            return Task.FromResult(LocalGpuSnapshot.Unknown);
        }
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
