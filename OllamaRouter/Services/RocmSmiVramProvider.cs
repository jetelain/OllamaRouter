using System.Text.Json;

namespace OllamaRouter.Services;

/// <summary>
/// Queries <c>rocm-smi --showmeminfo vram --json</c> (Linux, ROCm) to get the free VRAM in MB,
/// summed across all detected AMD GPUs. Returns 0 if the tool is not available or on any
/// parsing failure. Values reported by rocm-smi are in KB and are converted to MB.
/// </summary>
internal sealed class RocmSmiVramProvider : IGpuVramProvider
{
    private const int TimeoutMs = 5000;

    private readonly IProcessRunner _processRunner;

    public RocmSmiVramProvider(IProcessRunner processRunner)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public int GetFreeVramMB()
    {
        try
        {
            var result = _processRunner.Run("rocm-smi", "--showmeminfo vram --json", TimeoutMs);
            if (!result.Succeeded)
            {
                return 0;
            }

            using var document = JsonDocument.Parse(result.StdOut);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return 0;
            }

            int totalFreeMB = 0;
            foreach (var card in root.EnumerateObject())
            {
                if (!card.Name.StartsWith("card", StringComparison.Ordinal) || card.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                long freeKB;
                if (TryGetKb(card.Value, "vram_free", out freeKB))
                {
                    totalFreeMB += (int)(Math.Max(0, freeKB) / 1024);
                }
                else if (TryGetKb(card.Value, "vram_total", out long totalKB) && TryGetKb(card.Value, "vram_used", out long usedKB))
                {
                    totalFreeMB += (int)(Math.Max(0, totalKB - usedKB) / 1024);
                }
            }

            return totalFreeMB;
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryGetKb(JsonElement card, string propertyName, out long valueKb)
    {
        valueKb = 0;
        if (!card.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        string raw = property.ValueKind switch
        {
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.String => property.GetString() ?? string.Empty,
            _ => string.Empty
        };

        return long.TryParse(raw.Trim(), out valueKb);
    }
}
