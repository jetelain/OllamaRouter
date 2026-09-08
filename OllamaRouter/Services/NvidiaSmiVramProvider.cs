using System.Diagnostics;

namespace OllamaRouter.Services;

/// <summary>
/// Queries <c>nvidia-smi</c> to get the free VRAM. Returns 0 if the tool is not available
/// (for example on an environment without an NVIDIA GPU).
/// </summary>
public sealed class NvidiaSmiVramProvider : IGpuVramProvider
{
    public int GetFreeVramMB()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=memory.free --format=csv,noheader,nounits",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

            var lines = output.Split('\n');
            return int.TryParse(lines[0], out int vram) ? vram : 0;
        }
        catch
        {
            return 0;
        }
    }
}
