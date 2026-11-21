using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OllamaRouter.Options;
using OllamaRouter.Services.Process;

namespace OllamaRouter.Services.Hardware;

/// <summary>
/// Accessor for the single embedded Linux hardware probe script (task #143) plus the
/// marker-frame constants shared with the <see cref="LinuxHardwareDigest"/> and the tests.
/// The script is an <c>EmbeddedResource</c> of this assembly — read ONCE lazily via
/// <see cref="Assembly.GetManifestResourceStream"/> (AOT-safe; never a build-output file read).
/// It is the ONLY Linux collector: local execution feeds it to <c>bash</c> over stdin, remote
/// execution ships it base64-encoded over ssh.
/// </summary>
internal static class LinuxHardwareProbeScript
{
    /// <summary>The frame's opening marker (the digest scans the WHOLE output for it).</summary>
    public const string BeginMarker = "===BEGIN_HW===";

    /// <summary>The frame's closing marker (must appear AFTER <see cref="BeginMarker"/>).</summary>
    public const string EndMarker = "===END_HW===";

    /// <summary>Raw <c>dmidecode -t memory</c> section (RAM type + channel banks).</summary>
    public const string SectionDmidecodeMemory = "---DMIDECODE_MEMORY---";

    /// <summary>Raw <c>nvidia-smi --query-gpu=name,memory.total --format=csv,noheader,nounits</c> section.</summary>
    public const string SectionNvidiaGpus = "---NVIDIA_GPUS---";

    /// <summary>Raw <c>rocm-smi --showmeminfo vram --json</c> section.</summary>
    public const string SectionRocmGpus = "---ROCM_GPUS---";

    /// <summary>Raw <c>lspci | grep -iE 'vga|3d|display'</c> section (GPU-identity fallback).</summary>
    public const string SectionLspciGpus = "---LSPCI_GPUS---";

    private static readonly string[] SectionMarkers =
    [
        SectionDmidecodeMemory,
        SectionNvidiaGpus,
        SectionRocmGpus,
        SectionLspciGpus
    ];

    private static string? _scriptText;

    /// <summary>
    /// The embedded script text (loaded once, cached for the process lifetime).
    /// </summary>
    public static string ScriptText
    {
        get
        {
            var text = _scriptText;
            if (text is null)
            {
                text = LoadScriptText();
                _scriptText = text;
            }

            return text;
        }
    }

    internal static bool IsSectionMarker(string line)
    {
        foreach (var marker in SectionMarkers)
        {
            if (string.Equals(line, marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string LoadScriptText()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(".linux-hardware-probe.sh", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                "The embedded Linux hardware probe script resource is missing from the OllamaRouter assembly.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// The single C# digest engine of the Linux hardware probe (task #143): parses the
/// <c>===BEGIN_HW===</c> marker frame emitted by <see cref="LinuxHardwareProbeScript"/> and
/// produces a <see cref="NodeHardwareProfile"/>. ONE code path serves local and remote probe
/// output — all classification tables and derivation live in exactly one place (this type +
/// the existing <see cref="LocalHardwareProfileProvider"/> parsers + <see cref="HardwareMetrics"/>).
/// Pure and never throws: absent/corrupt frames (either marker missing, or <c>END</c> not after
/// <c>BEGIN</c>) return <c>null</c>.
/// </summary>
internal static class LinuxHardwareDigest
{
    /// <summary>
    /// Digests one raw probe output (local <c>bash</c> stdout or ssh stdout) into a
    /// <see cref="NodeHardwareProfile"/> (os <c>"linux"</c>, nodeName = the probed hostname,
    /// fallback <c>linux-node</c>); returns <c>null</c> when the marker frame is absent or corrupt.
    /// Never throws.
    /// </summary>
    public static NodeHardwareProfile? TryBuildProfile(string rawOutput)
    {
        if (string.IsNullOrEmpty(rawOutput))
        {
            return null;
        }

        // The digest scans the WHOLE output: ssh/sshd pre-banners and noise BEFORE the markers
        // must not break the frame.
        var begin = rawOutput.IndexOf(LinuxHardwareProbeScript.BeginMarker, StringComparison.Ordinal);
        if (begin < 0)
        {
            return null;
        }

        var beginContent = begin + LinuxHardwareProbeScript.BeginMarker.Length;
        var end = rawOutput.IndexOf(LinuxHardwareProbeScript.EndMarker, beginContent, StringComparison.Ordinal);
        if (end < 0)
        {
            return null;
        }

        var frame = rawOutput[beginContent..end].Split('\n');

        var hostname = string.Empty;
        var arch = string.Empty;
        var cpu = string.Empty;
        var ramMB = 0L;

        // The KEY=VALUE header occupies the frame lines before the first RAW section marker
        // (HOSTNAME / ARCH / CPU / RAM_MB).
        foreach (var rawLine in frame)
        {
            var line = rawLine.TrimEnd('\r');
            if (LinuxHardwareProbeScript.IsSectionMarker(line))
            {
                break;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            switch (key)
            {
                case "HOSTNAME" when value.Length > 0:
                    hostname = value;
                    break;
                case "ARCH" when value.Length > 0:
                    arch = value;
                    break;
                case "CPU":
                    cpu = value;
                    break;
                case "RAM_MB":
                    ramMB = long.TryParse(value, out var parsedRam) ? Math.Max(0, parsedRam) : 0;
                    break;
            }
        }

        var architecture = NormalizeArchitecture(arch);

        // RAM type + channels: the EXISTING dmidecode parser (the same one the pre-task local
        // C# path used — this is what keeps local and remote probes byte-compatible).
        var (ramType, channels) = LocalHardwareProfileProvider.ParseDmidecodeMemory(
            ExtractSection(frame, LinuxHardwareProbeScript.SectionDmidecodeMemory));

        // GPU chain, first non-empty section WINS (same order as the pre-task local probe +
        // the lspci fallback of the retired PowerShell digest, now in C#).
        var gpus = ParseGpuSections(frame);

        // Derivation: the EXACT same three HardwareMetrics calls the local provider makes, so
        // identical hardware yields identical values.
        const string os = "linux";
        var isUnified = HardwareMetrics.DetectUnifiedMemory(os, architecture, cpu, ramType, gpus);
        var totalFastMemoryMB = HardwareMetrics.ComputeTotalFastMemoryMB(isUnified, ramMB, gpus);
        var bandwidth = HardwareMetrics.ComputeEffectiveBandwidthGBs(isUnified, ramType, cpu, channels, gpus);

        return new NodeHardwareProfile(
            string.IsNullOrWhiteSpace(hostname) ? "linux-node" : hostname,
            os,
            architecture,
            isUnified,
            ramMB,
            ramType,
            gpus,
            totalFastMemoryMB,
            bandwidth);
    }

    /// <summary>
    /// Normalizes the raw <c>uname -m</c> value to the profile's architecture shape:
    /// <c>x86_64</c>/<c>amd64</c> → <c>x64</c>, <c>aarch64</c>/<c>arm64</c> → <c>arm64</c>,
    /// anything else passes through lower-cased (matches <c>RuntimeInformation</c> shapes).
    /// </summary>
    private static string NormalizeArchitecture(string raw)
    {
        var arch = raw.Trim().ToLowerInvariant();
        return arch switch
        {
            "x86_64" or "amd64" => "x64",
            "aarch64" or "arm64" => "arm64",
            _ => arch
        };
    }

    /// <summary>
    /// Returns the RAW content of one section (the lines after its marker up to the next
    /// section marker), or an empty string when the marker is absent.
    /// </summary>
    private static string ExtractSection(string[] frameLines, string marker)
    {
        for (var i = 0; i < frameLines.Length; i++)
        {
            if (string.Equals(frameLines[i].TrimEnd('\r'), marker, StringComparison.Ordinal))
            {
                var section = new StringBuilder();
                for (var j = i + 1; j < frameLines.Length; j++)
                {
                    var line = frameLines[j].TrimEnd('\r');
                    if (LinuxHardwareProbeScript.IsSectionMarker(line))
                    {
                        break;
                    }

                    section.Append(line).Append('\n');
                }

                return section.ToString();
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// The GPU chain: NVIDIA rows (nvidia-smi CSV) first, then ROCM (rocm-smi JSON), then the
    /// LSPCI fallback — the first section that YIELDS a GPU list wins (the pre-task local probe
    /// fell through to the next tool when a tool ran but produced no parseable GPU row).
    /// </summary>
    private static IReadOnlyList<GpuDeviceProfile> ParseGpuSections(string[] frameLines)
    {
        var gpus = new List<GpuDeviceProfile>();

        var nvidiaSection = ExtractSection(frameLines, LinuxHardwareProbeScript.SectionNvidiaGpus);
        if (nvidiaSection.Trim().Length > 0 && ParseNvidiaSection(nvidiaSection, gpus))
        {
            return gpus;
        }

        var rocmSection = ExtractSection(frameLines, LinuxHardwareProbeScript.SectionRocmGpus);
        if (rocmSection.Trim().Length > 0 && ParseRocmSection(rocmSection, gpus))
        {
            return gpus;
        }

        var lspciSection = ExtractSection(frameLines, LinuxHardwareProbeScript.SectionLspciGpus);
        if (lspciSection.Trim().Length > 0)
        {
            ParseLspciSection(lspciSection, gpus);
        }

        return gpus;
    }

    /// <summary>
    /// Parses nvidia-smi <c>csv,noheader,nounits</c> rows (<c>name, vramMB</c>): the model name
    /// is cleaned via the shared <see cref="LocalHardwareProfileProvider.CleanGpuModelName"/>,
    /// the VRAM via <see cref="LocalHardwareProfileProvider.ParseLeadingInt"/> and the tier via
    /// <see cref="LocalHardwareProfileProvider.ClassifyNvidiaGpu"/> — the pre-task local path,
    /// verbatim. Returns whether at least one GPU row parsed.
    /// </summary>
    private static bool ParseNvidiaSection(string section, List<GpuDeviceProfile> gpus)
    {
        var any = false;
        foreach (var rawLine in section.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var name = LocalHardwareProfileProvider.CleanGpuModelName("Nvidia", parts[0]);
            var vram = LocalHardwareProfileProvider.ParseLeadingInt(parts[1]);
            var (vramType, bandwidth) = LocalHardwareProfileProvider.ClassifyNvidiaGpu(name, 0);
            gpus.Add(new GpuDeviceProfile("Nvidia", name, vram, vramType, 0, bandwidth));
            any = true;
        }

        return any;
    }

    /// <summary>
    /// Parses rocm-smi <c>--showmeminfo vram --json</c> output: one GPU entry per
    /// <c>"vram_total": &lt;KB&gt;</c> match (KB → MB floor), named <c>AMD GPU (rocm-smi)</c> and
    /// classified via <see cref="LocalHardwareProfileProvider.ClassifyAmdGpu"/> — the pre-task
    /// local path, verbatim. Returns whether at least one GPU entry parsed.
    /// </summary>
    private static bool ParseRocmSection(string section, List<GpuDeviceProfile> gpus)
    {
        var any = false;
        foreach (Match match in Regex.Matches(section, "\"vram_total\"\\s*:\\s*(\\d+)"))
        {
            if (!long.TryParse(match.Groups[1].Value, out var totalKb))
            {
                continue;
            }

            var vramMB = (int)Math.Max(0, totalKb / 1024);
            var (vramType, bandwidth) = LocalHardwareProfileProvider.ClassifyAmdGpu("AMD GPU (rocm-smi)", vramMB);
            gpus.Add(new GpuDeviceProfile("AMD", "AMD GPU (rocm-smi)", vramMB, vramType, 0, bandwidth));
            any = true;
        }

        return any;
    }

    /// <summary>
    /// Parses the lspci GPU fallback rows: vendor sniff (NVIDIA → <c>Nvidia</c>,
    /// Advanced Micro Devices/AMD/Radeon → <c>AMD</c>, Intel → <c>Intel</c>, else
    /// <c>Unknown</c>), the model name = the substring after the first <c>": "</c> (or the whole
    /// line) cleaned via <see cref="LocalHardwareProfileProvider.CleanGpuModelName"/>, and the
    /// tier via the shared <c>Classify*Gpu</c> tables (strictly better than the retired
    /// PowerShell digest, which emitted all-Unknown for lspci rows).
    /// </summary>
    private static void ParseLspciSection(string section, List<GpuDeviceProfile> gpus)
    {
        foreach (var rawLine in section.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var vendor = SniffLspciVendor(line);
            var model = line;
            var separator = line.IndexOf(": ", StringComparison.Ordinal);
            if (separator >= 0)
            {
                model = line[(separator + 2)..];
            }

            var name = LocalHardwareProfileProvider.CleanGpuModelName(vendor, model);
            var (vramType, bandwidth) = vendor switch
            {
                "Nvidia" => LocalHardwareProfileProvider.ClassifyNvidiaGpu(name, 0),
                "AMD" => LocalHardwareProfileProvider.ClassifyAmdGpu(name, 0),
                "Intel" => LocalHardwareProfileProvider.ClassifyIntelGpu(name),
                _ => ("Unknown", 0.0)
            };

            gpus.Add(new GpuDeviceProfile(vendor, name, 0, vramType, 0, bandwidth));
        }
    }

    /// <summary>Vendor sniff of one lspci row (NVIDIA / AMD-family / Intel / Unknown).</summary>
    private static string SniffLspciVendor(string line)
    {
        if (line.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            return "Nvidia";
        }

        if (line.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
        {
            return "AMD";
        }

        if (line.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            return "Intel";
        }

        return "Unknown";
    }
}

/// <summary>
/// The outcome of one hardware probe run (local <c>bash</c> or remote <c>ssh</c>): exactly ONE
/// of <see cref="Profile"/> (success) or <see cref="Error"/> (a human-readable failure
/// description — the ssh exit code + trimmed stderr, the timeout flag, or the missing-marker
/// message) is non-null.
/// </summary>
internal readonly record struct LinuxProbeResult(NodeHardwareProfile? Profile, string? Error)
{
    /// <summary>Creates a successful outcome.</summary>
    public static LinuxProbeResult Success(NodeHardwareProfile profile) => new(profile, null);

    /// <summary>Creates a failed outcome.</summary>
    public static LinuxProbeResult Failure(string error) => new(null, error);
}

/// <summary>
/// The daemon-side executor of the single embedded Linux probe script (task #143): local runs
/// feed <see cref="LinuxHardwareProbeScript.ScriptText"/> to <c>bash</c> over stdin (no temp
/// file), remote runs ship it base64-encoded over ssh to <c>{sshUser}@{url-host}</c> on
/// <c>sshPort</c> (key-based auth from the ssh user's profile — NO password support, NO
/// identity-file handling; BatchMode + no-prompt hardening, 30 s overall bound). Fail-soft and
/// NEVER throws: every failure (launch, non-zero exit, timeout, missing markers) yields a
/// <see cref="LinuxProbeResult"/> with a readable <see cref="LinuxProbeResult.Error"/>.
/// </summary>
internal interface ILinuxHardwareProbeExecutor
{
    /// <summary>Runs the embedded script locally via <c>bash</c> (stdin) and digests its output.</summary>
    Task<LinuxProbeResult> ProbeLocalAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the embedded script on the remote host behind <paramref name="target"/> via ssh
    /// (requires a non-blank <see cref="OllamaTargetOptions.SshUser"/>; the host is the target
    /// URL host, the port <see cref="OllamaTargetOptions.GetEffectiveSshPort"/>) and digests it.
    /// </summary>
    Task<LinuxProbeResult> ProbeRemoteAsync(OllamaTargetOptions target, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ILinuxHardwareProbeExecutor" />
internal sealed class LinuxHardwareProbeExecutor : ILinuxHardwareProbeExecutor
{
    /// <summary>The overall probe bound (connect + script execution): 30 seconds.</summary>
    public const int ProbeTimeoutMs = 30_000;

    /// <summary>The ssh <c>ConnectTimeout</c> hardening flag value (seconds).</summary>
    public const int SshConnectTimeoutSeconds = 10;

    private const int MaxErrorChars = 512;

    private readonly IProcessRunner _processRunner;
    private readonly ILogger<LinuxHardwareProbeExecutor> _logger;

    public LinuxHardwareProbeExecutor(
        IProcessRunner processRunner,
        ILogger<LinuxHardwareProbeExecutor> logger)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<LinuxProbeResult> ProbeLocalAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(LinuxProbeResult.Failure("the hardware probe was canceled before it started"));
        }

        // The script is fed over STDIN (no temp file): bash reads it from stdin and runs it.
        var result = _processRunner.Run("bash", string.Empty, ProbeTimeoutMs, LinuxHardwareProbeScript.ScriptText);
        return Task.FromResult(ToProbeResult(result));
    }

    /// <inheritdoc />
    public Task<LinuxProbeResult> ProbeRemoteAsync(OllamaTargetOptions target, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(LinuxProbeResult.Failure("the hardware probe was canceled before it started"));
        }

        if (target is null)
        {
            return Task.FromResult(LinuxProbeResult.Failure("no target to probe"));
        }

        var sshUser = target.SshUser?.Trim();
        if (string.IsNullOrWhiteSpace(sshUser))
        {
            return Task.FromResult(LinuxProbeResult.Failure("the target has no sshUser configured"));
        }

        if (string.IsNullOrWhiteSpace(target.Url) ||
            !Uri.TryCreate(target.Url, UriKind.Absolute, out var uri) ||
            string.IsNullOrEmpty(uri.Host))
        {
            return Task.FromResult(LinuxProbeResult.Failure(
                $"the target url '{target.Url}' does not resolve to an ssh host"));
        }

        var base64Script = Convert.ToBase64String(Encoding.UTF8.GetBytes(LinuxHardwareProbeScript.ScriptText));
        var arguments = BuildSshArguments(sshUser, uri.Host, target.GetEffectiveSshPort(), base64Script);
        var result = _processRunner.Run("ssh", arguments, ProbeTimeoutMs);
        return Task.FromResult(ToProbeResult(result));
    }

    /// <summary>
    /// Builds the single bounded ssh argument string: <c>-p {port}</c> + the non-interactive
    /// hardening flags (BatchMode=yes makes ANY prompt — password or host key — fail instantly
    /// instead of hanging) + <c>{user}@{host}</c> + the base64-over-ssh remote command (the
    /// proven form of the retired PowerShell probe: quoting-proof; the remote host only needs
    /// <c>base64</c> + <c>bash</c>). One method so the unit tests assert it VERBATIM.
    /// </summary>
    internal static string BuildSshArguments(string user, string host, int port, string base64Script)
    {
        return $"-p {port} " +
            $"-o BatchMode=yes " +
            $"-o ConnectTimeout={SshConnectTimeoutSeconds} " +
            $"-o StrictHostKeyChecking=accept-new " +
            $"-o PasswordAuthentication=no " +
            $"-o KbdInteractiveAuthentication=no " +
            $"-o NumberOfPasswordPrompts=0 " +
            $"{user}@{host} " +
            $"\"echo {base64Script} | base64 -d | bash\"";
    }

    /// <summary>
    /// Maps one process outcome to a <see cref="LinuxProbeResult"/>: timeout / launch failure /
    /// non-zero exit → a readable error; otherwise the digest of stdout (null frame → error).
    /// Never throws.
    /// </summary>
    private LinuxProbeResult ToProbeResult(ProcessResult result)
    {
        try
        {
            if (result.TimedOut)
            {
                return LinuxProbeResult.Failure(
                    $"the hardware probe timed out after {ProbeTimeoutMs / 1000} seconds");
            }

            if (result.ExitCode < 0)
            {
                // Launch failure (e.g. `bash` missing on a non-Linux daemon host, `ssh` absent).
                return LinuxProbeResult.Failure(
                    string.IsNullOrWhiteSpace(result.StdErr)
                        ? "failed to launch the hardware probe process"
                        : $"failed to launch the hardware probe process: {Tail(result.StdErr)}");
            }

            if (!result.Succeeded)
            {
                // Non-zero exit (unreachable host, ssh auth failure, ...).
                return LinuxProbeResult.Failure(
                    string.IsNullOrWhiteSpace(result.StdErr)
                        ? $"the hardware probe exited with code {result.ExitCode}"
                        : $"the hardware probe exited with code {result.ExitCode}: {Tail(result.StdErr)}");
            }

            var profile = LinuxHardwareDigest.TryBuildProfile(result.StdOut);
            return profile is null
                ? LinuxProbeResult.Failure(
                    "the probe output did not contain the hardware marker frame (===BEGIN_HW=== / ===END_HW===)")
                : LinuxProbeResult.Success(profile);
        }
        catch (Exception ex)
        {
            // Fail-soft by contract: an unexpected digest/runner bug never escapes as a throw.
            _logger.LogWarning(ex, "The Linux hardware probe executor hit an unexpected error.");
            return LinuxProbeResult.Failure("the hardware probe hit an unexpected internal error");
        }
    }

    /// <summary>Trims the error text and keeps only its last ~512 characters (the tail).</summary>
    private static string Tail(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= MaxErrorChars ? trimmed : trimmed[^MaxErrorChars..];
    }
}
