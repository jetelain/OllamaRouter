using OllamaRouter.Services.Hardware;

namespace OllamaRouter.Tests.Services.Hardware;

/// <summary>
/// Unit tests of the single C# digest engine (task #143): the pure frame →
/// <see cref="NodeHardwareProfile"/> parser that serves BOTH the local bash probe and the
/// remote ssh probe (one code path, one place for all classification/derivation). Canned
/// probe outputs cover the full happy path (NVIDIA), AMD rocm-smi, the lspci-only fallback
/// (incl. Intel Arc + Unknown vendor), CPU-only nodes, missing/empty sections, corrupt
/// frames, dmidecode channel parsing and arch normalization. No I/O, no processes.
/// </summary>
public class LinuxHardwareDigestTests
{
    // ------------------------------------------------------------------
    //  Canned-frame helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds a probe frame in the exact wire format of
    /// <c>Services/Hardware/linux-hardware-probe.sh</c> (defaults: empty RAW sections).
    /// </summary>
    private static string BuildFrame(
        string hostname = "probe-node",
        string arch = "x86_64",
        string cpu = "Intel(R) Xeon(R) CPU E5-2680 v4 @ 2.40GHz",
        long? ramMB = 65536,
        string dmidecode = "",
        string nvidia = "",
        string rocm = "",
        string lspci = "")
    {
        return $"===BEGIN_HW===\n" +
               $"HOSTNAME={hostname}\n" +
               $"ARCH={arch}\n" +
               $"CPU={cpu}\n" +
               $"RAM_MB={ramMB?.ToString() ?? ""}\n" +
               $"---DMIDECODE_MEMORY---\n{dmidecode}" +
               (dmidecode.Length > 0 && !dmidecode.EndsWith('\n') ? "\n" : "") +
               $"---NVIDIA_GPUS---\n{nvidia}" +
               (nvidia.Length > 0 && !nvidia.EndsWith('\n') ? "\n" : "") +
               $"---ROCM_GPUS---\n{rocm}" +
               (rocm.Length > 0 && !rocm.EndsWith('\n') ? "\n" : "") +
               $"---LSPCI_GPUS---\n{lspci}" +
               (lspci.Length > 0 && !lspci.EndsWith('\n') ? "\n" : "") +
               $"===END_HW===\n";
    }

    private const string DmidecodeDdr5TwoChannel =
        "Handle 0x0101, DMI type 17, 40 bytes\n" +
        "\tMemory Device\n" +
        "\t\tLocator: DIMM_A1\n" +
        "\t\tBank Locator: BANK 0\n" +
        "\t\tType: DDR5\n" +
        "\t\tSpeed: 4800 MT/s\n" +
        "\t\tSize: 32 GB\n" +
        "Handle 0x0102, DMI type 17, 40 bytes\n" +
        "\tMemory Device\n" +
        "\t\tLocator: DIMM_B1\n" +
        "\t\tBank Locator: BANK 1\n" +
        "\t\tType: DDR5\n" +
        "\t\tSpeed: 4800 MT/s\n" +
        "\t\tSize: 32 GB\n" +
        "Handle 0x0103, DMI type 17, 40 bytes\n" +
        "\tMemory Device\n" +
        "\t\tLocator: DIMM_A2\n" +
        "\t\tSize: No Module Installed\n";

    private const string RocmOneGpu16Gb =
        "{\n" +
        "    \"card0\": {\n" +
        "        \"vram_total\": 16777216,\n" +
        "        \"vram_used\": 1024,\n" +
        "        \"vram_free\": 16776192\n" +
        "    }\n" +
        "}\n";

    private const string LspciMultiVendor =
        "01:00.0 VGA compatible controller: NVIDIA Corporation GA102 [GeForce RTX 4090] (rev a1)\n" +
        "01:00.1 3D controller: Advanced Micro Devices, Inc. [AMD/ATI] Navi 31 [Radeon RX 7900 XTX]\n" +
        "02:00.0 VGA compatible controller: Intel Corporation Arc A770\n" +
        "03:00.0 Display controller: Unknown Corporation Weird Display Adapter\n";

    // ------------------------------------------------------------------
    //  Full happy path (NVIDIA) + derivation semantics
    // ------------------------------------------------------------------

    [Fact]
    public void TryBuildProfile_NvidiaHappyPath_ParsesAllSectionsAndDerivesMetrics()
    {
        var profile = LinuxHardwareDigest.TryBuildProfile(BuildFrame(
            hostname: "gpu-box",
            arch: "x86_64",
            cpu: "AMD Ryzen 9 7950X",
            ramMB: 65536,
            dmidecode: DmidecodeDdr5TwoChannel,
            nvidia: "NVIDIA GeForce RTX 4090, 24564\n",
            lspci: "01:00.0 VGA compatible controller: NVIDIA Corporation GA102 [GeForce RTX 4090] (rev a1)\n"));

        Assert.NotNull(profile);
        Assert.Equal("gpu-box", profile.NodeName);
        Assert.Equal("linux", profile.Os);
        Assert.Equal("x64", profile.Architecture);
        Assert.False(profile.IsUnifiedMemory);
        Assert.Equal(65536, profile.SystemRamMB);
        Assert.Equal(SystemRamType.Ddr5, profile.SystemRamType);

        // The NVIDIA section WINS over the lspci fallback (first non-empty section).
        var gpu = Assert.Single(profile.Gpus);
        Assert.Equal("Nvidia", gpu.Vendor);
        Assert.Equal("GeForce RTX 4090", gpu.ModelName);
        Assert.Equal(24564, gpu.VramMB);
        Assert.Equal("GDDR6X", gpu.VramType);
        // GDDR6X with unknown bus width → 936 * 0.5 = 468.
        Assert.Equal(468.0, gpu.BandwidthGBs, precision: 1);

        // Derivation (the exact HardwareMetrics calls of the pre-task local provider):
        // discrete → total fast memory = GPU VRAM; bandwidth = max GPU bandwidth.
        Assert.Equal(24564, profile.TotalFastMemoryMB);
        Assert.Equal(468.0, profile.EffectiveBandwidthGBs, precision: 1);
    }

    [Fact]
    public void TryBuildProfile_MultiGpuNvidia_ParsesEveryRow()
    {
        var profile = LinuxHardwareDigest.TryBuildProfile(BuildFrame(
            nvidia: "NVIDIA A100-SXM4-80GB, 81920\nNVIDIA A100-SXM4-80GB, 81920\n"));

        Assert.NotNull(profile);
        Assert.Equal(2, profile.Gpus.Count);
        var gpus = profile.Gpus;
        Assert.Equal("A100-SXM4-80GB", gpus[0].ModelName);
        Assert.Equal(81920, gpus[0].VramMB);
        Assert.Equal("HBM2E", gpus[0].VramType);
        Assert.Equal(81920 * 2, profile.TotalFastMemoryMB);
    }

    [Fact]
    public void TryBuildProfile_NvidiaSectionEmpty_FallsThroughToRocm()
    {
        // nvidia-smi present but no parseable row (e.g. error text) → the digest falls
        // through to the ROCM section, exactly like the pre-task local probe chain.
        var profile = LinuxHardwareDigest.TryBuildProfile(BuildFrame(
            nvidia: "NVIDIA-SMI has failed because it couldn't communicate with the NVIDIA driver.\n",
            rocm: RocmOneGpu16Gb));

        Assert.NotNull(profile);
        var gpu = Assert.Single(profile.Gpus);
        Assert.Equal("AMD", gpu.Vendor);
        Assert.Equal("AMD GPU (rocm-smi)", gpu.ModelName);
    }

    // ------------------------------------------------------------------
    //  AMD rocm-smi
    // ------------------------------------------------------------------

    [Fact]
    public void TryBuildProfile_AmdRocmSmi_ParsesVramTotalInKilobytes()
    {
        var profile = LinuxHardwareDigest.TryBuildProfile(BuildFrame(
            ramMB: 32768,
            rocm: RocmOneGpu16Gb));

        Assert.NotNull(profile);
        var gpu = Assert.Single(profile.Gpus);
        Assert.Equal("AMD", gpu.Vendor);
        Assert.Equal("AMD GPU (rocm-smi)", gpu.ModelName);
        Assert.Equal(16384, gpu.VramMB); // 16777216 kB / 1024
        Assert.Equal("GDDR6", gpu.VramType);
        Assert.Equal(256.0, gpu.BandwidthGBs, precision: 1); // 512 * 0.5
        Assert.Equal(16384, profile.TotalFastMemoryMB);
        Assert.Equal(256.0, profile.EffectiveBandwidthGBs, precision: 1);
    }

    [Fact]
    public void TryBuildProfile_AmdRocmSmi_MultiCard_ParsesEveryVramTotal()
    {
        var profile = LinuxHardwareDigest.TryBuildProfile(BuildFrame(
            rocm:
                "{\n" +
                "    \"card0\": { \"vram_total\": 16777216 },\n" +
                "    \"card1\": { \"vram_total\": 16777216 }\n" +
                "}\n"));

        Assert.NotNull(profile);
        Assert.Equal(2, profile.Gpus.Count);
        Assert.Equal(32768, profile.TotalFastMemoryMB);
    }

    // ------------------------------------------------------------------
    //  AMD rocm-smi NEW schema (task #154: quoted byte strings per card)
    // ------------------------------------------------------------------

    /// <summary>The VERBATIM compact rocm-smi dump captured on aiserver (Strix Halo, 2026-11-21).</summary>
    private const string RocmNewSchemaStrixHalo =
        "{\"card0\": {\"VRAM Total Memory (B)\": \"536870912\", \"VRAM Total Used Memory (B)\": \"165527552\"}}\n";

    /// <summary>Strix Halo lspci row, verbatim (vendor token + rev suffix intact).</summary>
    private const string LspciStrixHalo =
        "03:00.0 VGA compatible controller: Advanced Micro Devices, Inc. [AMD/ATI] Strix Halo " +
        "[Radeon Graphics / Radeon 8050S Graphics / Radeon 8060S Graphics] (rev c5)\n";

    private const string StrixHaloExpectedModel =
        "Strix Halo [Radeon Graphics / Radeon 8050S Graphics / Radeon 8060S Graphics]";

    private const string DmidecodeLpDdr5Single =
        "Handle 0x0101, DMI type 17, 40 bytes\n" +
        "\tMemory Device\n" +
        "\t\tLocator: Channel0-DIMM0\n" +
        "\t\tType: LPDDR5\n" +
        "\t\tSize: 32 GB\n";

    [Fact]
    public void TryBuildProfile_AmdRocmNewSchema_StrixHaloRealDump_SingleCrossReferencedEntry_UnifiedDerivation()
    {
        // The real aiserver frame (task #154): NEW rocm schema + exactly ONE AMD lspci row.
        var profile = LinuxHardwareDigest.TryBuildProfile(BuildFrame(
            hostname: "aiserver",
            cpu: "AMD Ryzen AI Max+ 395",
            ramMB: 93379,
            dmidecode: DmidecodeLpDdr5Single,
            rocm: RocmNewSchemaStrixHalo,
            lspci: LspciStrixHalo));

        Assert.NotNull(profile);
        var gpu = Assert.Single(profile.Gpus); // NO lspci-fallback duplicate entry
        Assert.Equal("AMD", gpu.Vendor);
        Assert.Equal(StrixHaloExpectedModel, gpu.ModelName); // vendor token + (rev …) stripped
        Assert.Equal(512, gpu.VramMB); // 536870912 B / 1048576 floor
        Assert.Equal("Unknown", gpu.VramType); // 512 MB + no table hit
        Assert.Equal(0, gpu.BandwidthGBs, precision: 1);

        // Derivation untouched: 512 MB ≤ 2048 → still unified → RAM-derived profile
        // identical to the pre-fix aiserver persisted profile.
        Assert.True(profile.IsUnifiedMemory);
        Assert.Equal(93379, profile.TotalFastMemoryMB);
        Assert.Equal(341.2, profile.EffectiveBandwidthGBs, precision: 1); // LPDDR5X 4-channel (AMD Max rule)
    }

    [Fact]
    public void TryBuildProfile_AmdRocmNewSchema_TwoAmdLspciRows_FallsBackToGenericName()
    {
        var profile = LinuxHardwareDigest.TryBuildProfile(BuildFrame(
            rocm: RocmNewSchemaStrixHalo,
            lspci: LspciStrixHalo +
                   "04:00.0 3D controller: Advanced Micro Devices, Inc. [AMD/ATI] Navi 31 [Radeon RX 7900 XTX]\n"));

        Assert.NotNull(profile);
        var gpu = Assert.Single(profile.Gpus);
        Assert.Equal("AMD GPU (rocm-smi)", gpu.ModelName); // ≥2 AMD rows → generic
        Assert.Equal(512, gpu.VramMB); // the byte→MB value is unaffected
    }

    [Fact]
    public void TryBuildProfile_AmdRocmNewSchema_ZeroAmdLspciRows_FallsBackToGenericName()
    {
        // Intel-only lspci rows → generic name …
        var intelOnly = LinuxHardwareDigest.TryBuildProfile(BuildFrame(
            rocm: RocmNewSchemaStrixHalo,
            lspci: "02:00.0 VGA compatible controller: Intel Corporation Arc A770\n"));
        Assert.NotNull(intelOnly);
        var gpu = Assert.Single(intelOnly.Gpus);
        Assert.Equal("AMD GPU (rocm-smi)", gpu.ModelName);
        Assert.Equal(512, gpu.VramMB);

        // … and an EMPTY lspci section → generic name as well.
        var noLspci = LinuxHardwareDigest.TryBuildProfile(BuildFrame(rocm: RocmNewSchemaStrixHalo));
        Assert.NotNull(noLspci);
        gpu = Assert.Single(noLspci.Gpus);
        Assert.Equal("AMD GPU (rocm-smi)", gpu.ModelName);
        Assert.Equal(512, gpu.VramMB);
    }

    [Fact]
    public void TryBuildProfile_AmdRocmNewSchema_MultiCard_OneEntryPerCardInJsonOrder()
    {
        var profile = LinuxHardwareDigest.TryBuildProfile(BuildFrame(
            rocm:
                "{\n" +
                "    \"card0\": { \"VRAM Total Memory (B)\": \"536870912\" },\n" +
                "    \"card1\": { \"VRAM Total Memory (B)\": \"25769803776\" }\n" +
                "}\n"));

        Assert.NotNull(profile);
        Assert.Equal(2, profile.Gpus.Count);

        var gpus = profile.Gpus;
        Assert.Equal("AMD", gpus[0].Vendor);
        Assert.Equal("AMD GPU (rocm-smi)", gpus[0].ModelName);
        Assert.Equal(512, gpus[0].VramMB); // 536870912 B
        Assert.Equal("Unknown", gpus[0].VramType);
        Assert.Equal(0, gpus[0].BandwidthGBs, precision: 1);

        Assert.Equal("AMD", gpus[1].Vendor);
        Assert.Equal(24576, gpus[1].VramMB); // 25769803776 B
        Assert.Equal("GDDR6", gpus[1].VramType); // ≥ 8192 MB → GDDR6 in the shared table
        Assert.Equal(256.0, gpus[1].BandwidthGBs, precision: 1); // 512 * 0.5

        // 24 GB card > 2048 MB → discrete → total fast memory = the per-GPU sum.
        Assert.False(profile.IsUnifiedMemory);
        Assert.Equal(512 + 24576, profile.TotalFastMemoryMB);
    }

    [Fact]
    public void TryBuildProfile_AmdRocmNewSchema_NonRoundByteTotal_FloorsToMB()
    {
        var profile = LinuxHardwareDigest.TryBuildProfile(BuildFrame(
            rocm: "{\"card0\": {\"VRAM Total Memory (B)\": \"500000000\"}}\n"));

        Assert.NotNull(profile);
        var gpu = Assert.Single(profile.Gpus);
        Assert.Equal(476, gpu.VramMB); // 500000000 / 1048576 floor
    }

    [Fact]
    public void TryBuildProfile_AmdRocmNewSchema_TruncatedJson_DegradesToLspciFallbackWithoutThrowing()
    {
        // Malformed / truncated NEW-schema JSON (no closing quote/braces) yields zero
        // parseable cards → legacy regex yields nothing → the lspci fallback wins.
        var profile = LinuxHardwareDigest.TryBuildProfile(BuildFrame(
            ramMB: 32768,
            rocm: "{\"card0\": {\"VRAM Total Memory (B)\": \"5368",
            lspci: LspciStrixHalo));

        Assert.NotNull(profile);
        var gpu = Assert.Single(profile.Gpus);
        Assert.Equal("AMD", gpu.Vendor);
        // The lspci FALLBACK cleaner is untouched: raw model substring, vendor token kept.
        Assert.Equal(
            "Advanced Micro Devices, Inc. [AMD/ATI] Strix Halo [Radeon Graphics / Radeon 8050S Graphics / Radeon 8060S Graphics] (rev c5)",
            gpu.ModelName);
        Assert.Equal(0, gpu.VramMB); // lspci carries no VRAM — never fabricated
    }

    [Fact]
    public void TryBuildProfile_AmdRocmNewSchema_TrailingCommasAndExtraKeys_Parse()
    {
        var profile = LinuxHardwareDigest.TryBuildProfile(BuildFrame(
            rocm:
                "{\n" +
                "    \"card0\": {\n" +
                "        \"VRAM Total Memory (B)\": \"536870912\",\n" +
                "        \"VRAM Total Used Memory (B)\": \"165527552\",\n" +
                "        \"Extra Key\": \"irrelevant\"\n" +
                "    },\n" +
                "},\n"));

        Assert.NotNull(profile);
        var gpu = Assert.Single(profile.Gpus);
        Assert.Equal(512, gpu.VramMB);
    }

    [Fact]
    public void TryBuildProfile_NvidiaRowsPresent_BeatsNewSchemaRocm()
    {
        // The chain order is unchanged: nvidia-smi rows win before the ROCM stage is consulted.
        var profile = LinuxHardwareDigest.TryBuildProfile(BuildFrame(
            nvidia: "NVIDIA GeForce RTX 4090, 24564\n",
            rocm: RocmNewSchemaStrixHalo,
            lspci: LspciStrixHalo));

        Assert.NotNull(profile);
        var gpu = Assert.Single(profile.Gpus);
        Assert.Equal("Nvidia", gpu.Vendor);
        Assert.Equal("GeForce RTX 4090", gpu.ModelName);
        Assert.Equal(24564, gpu.VramMB);
    }

    // ------------------------------------------------------------------
    //  lspci-only fallback (GPU identity when no vendor CLI is installed)
    // ------------------------------------------------------------------

    [Fact]
    public void TryBuildProfile_LspciOnly_SniffsVendor_ModelAndClassification()
    {
        var profile = LinuxHardwareDigest.TryBuildProfile(BuildFrame(
            ramMB: 32768,
            dmidecode: DmidecodeDdr5TwoChannel,
            lspci: LspciMultiVendor));

        Assert.NotNull(profile);
        var gpus = profile.Gpus;
        Assert.Equal(4, gpus.Count);

        Assert.Equal("Nvidia", gpus[0].Vendor);
        Assert.Equal("Corporation GA102 [GeForce RTX 4090] (rev a1)", gpus[0].ModelName);
        Assert.Equal("GDDR6X", gpus[0].VramType);
        Assert.Equal(0, gpus[0].VramMB); // lspci carries no VRAM — never fabricated

        Assert.Equal("AMD", gpus[1].Vendor);
        Assert.Contains("Radeon RX 7900 XTX", gpus[1].ModelName);
        Assert.Equal("GDDR6", gpus[1].VramType); // "7900" → GDDR6 in the shared table

        Assert.Equal("Intel", gpus[2].Vendor);
        Assert.Equal("Intel Corporation Arc A770", gpus[2].ModelName);
        Assert.Equal("GDDR6", gpus[2].VramType); // "ARC" / "A770" → GDDR6 in the shared table

        Assert.Equal("Unknown", gpus[3].Vendor);
        Assert.Equal("Unknown", gpus[3].VramType);
        Assert.Equal(0, gpus[3].BandwidthGBs, precision: 1);
    }

    // ------------------------------------------------------------------
    //  CPU-only (no GPU sections) + RAM tier
    // ------------------------------------------------------------------

    [Fact]
    public void TryBuildProfile_CpuOnly_RamTierDrivesTotalFastMemoryAndBandwidth()
    {
        var profile = LinuxHardwareDigest.TryBuildProfile(BuildFrame(
            ramMB: 32768,
            dmidecode: DmidecodeDdr5TwoChannel));

        Assert.NotNull(profile);
        Assert.Empty(profile.Gpus);
        Assert.False(profile.IsUnifiedMemory);
        Assert.Equal(SystemRamType.Ddr5, profile.SystemRamType);
        Assert.Equal(32768, profile.TotalFastMemoryMB);
        Assert.Equal(38.4, profile.EffectiveBandwidthGBs, precision: 1); // DDR5 dual-channel
    }

    [Fact]
    public void TryBuildProfile_AmdApuLpDdr5X_NoDiscreteGpu_IsUnifiedMemory()
    {
        var profile = LinuxHardwareDigest.TryBuildProfile(BuildFrame(
            hostname: "strix-halo",
            cpu: "AMD Ryzen AI Max+ 395",
            ramMB: 98304,
            dmidecode: "Handle 0x0101, DMI type 17, 40 bytes\n\tMemory Device\n\t\tLocator: Channel0-DIMM0\n\t\tType: LPDDR5\n\t\tSize: 32 GB\n"));

        // No GPU sections at all (integrated graphics only): LPDDR5X + AMD brand → the
        // existing unified-memory rule (the same one the pre-task local path applied).
        Assert.NotNull(profile);
        Assert.True(profile.IsUnifiedMemory);
        Assert.Equal(SystemRamType.LpDdr5X, profile.SystemRamType);
        Assert.Equal(98304, profile.TotalFastMemoryMB); // unified → system RAM
        Assert.Equal(341.2, profile.EffectiveBandwidthGBs, precision: 1); // LPDDR5X 4-channel default
    }

    // ------------------------------------------------------------------
    //  Corrupt / absent frames (never throw)
    // ------------------------------------------------------------------

    [Fact]
    public void TryBuildProfile_MissingMarkers_ReturnsNull()
    {
        Assert.Null(LinuxHardwareDigest.TryBuildProfile(string.Empty));
        Assert.Null(LinuxHardwareDigest.TryBuildProfile("no markers at all\n"));
        Assert.Null(LinuxHardwareDigest.TryBuildProfile("===BEGIN_HW===\nHOSTNAME=x\n")); // no END
        Assert.Null(LinuxHardwareDigest.TryBuildProfile("===END_HW===\n")); // no BEGIN
    }

    [Fact]
    public void TryBuildProfile_EndMarkerWithoutBeginMarker_IsCorrupt_StrayEndBeforeFrameIsIgnored()
    {
        // An END marker with no BEGIN before it (and no later END) → corrupt → null.
        Assert.Null(LinuxHardwareDigest.TryBuildProfile("===END_HW===\nHOSTNAME=x\n"));

        // A stray END before the frame does NOT invalidate a later valid frame: the digest
        // scans the whole output for the first BEGIN and then the first END after it.
        var profile = LinuxHardwareDigest.TryBuildProfile(
            "===END_HW===\n" + BuildFrame(hostname: "after-noise", ramMB: 4096));
        Assert.NotNull(profile);
        Assert.Equal("after-noise", profile.NodeName);
        Assert.Equal(4096, profile.SystemRamMB);
    }

    [Fact]
    public void TryBuildProfile_SshBannerNoiseBeforeFrame_StillParses()
    {
        // ssh/sshd pre-banners and MOTD noise before the markers must not break the frame.
        var noisy = "Warning: Permanently added 'gpu-box' (ED25519) to the list of known hosts.\n" +
                    "Last login: Wed Nov 19 12:00:00 2026 from 192.168.1.10\n" +
                    BuildFrame(hostname: "gpu-box", ramMB: 16384);

        var profile = LinuxHardwareDigest.TryBuildProfile(noisy);
        Assert.NotNull(profile);
        Assert.Equal("gpu-box", profile.NodeName);
        Assert.Equal(16384, profile.SystemRamMB);
    }

    // ------------------------------------------------------------------
    //  Header edge cases
    // ------------------------------------------------------------------

    [Fact]
    public void TryBuildProfile_EmptyHostname_FallsBackToLinuxNode()
    {
        var profile = LinuxHardwareDigest.TryBuildProfile(BuildFrame(hostname: ""));
        Assert.NotNull(profile);
        Assert.Equal("linux-node", profile.NodeName);
    }

    [Fact]
    public void TryBuildProfile_NonNumericRamMb_FallsBackToZero()
    {
        var profile = LinuxHardwareDigest.TryBuildProfile(BuildFrame(ramMB: null));
        Assert.NotNull(profile);
        Assert.Equal(0, profile.SystemRamMB);

        var garbage = LinuxHardwareDigest.TryBuildProfile(
            BuildFrame(ramMB: 42).Replace("RAM_MB=42", "RAM_MB=garbage", StringComparison.Ordinal));
        Assert.NotNull(garbage);
        Assert.Equal(0, garbage!.SystemRamMB);
    }

    [Fact]
    public void TryBuildProfile_NegativeRamMb_ClampsToZero()
    {
        var profile = LinuxHardwareDigest.TryBuildProfile(
            BuildFrame(ramMB: -1).Replace("RAM_MB=-1", "RAM_MB=-42", StringComparison.Ordinal));
        Assert.NotNull(profile);
        Assert.Equal(0, profile!.SystemRamMB);
    }

    // ------------------------------------------------------------------
    //  Architecture normalization (uname -m → profile shape)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("x86_64", "x64")]
    [InlineData("amd64", "x64")]
    [InlineData("aarch64", "arm64")]
    [InlineData("arm64", "arm64")]
    [InlineData("riscv64", "riscv64")] // unknown passes through lower-cased
    [InlineData("X86_64", "x64")] // case-insensitive input
    public void TryBuildProfile_ArchitectureNormalization_MatchesRuntimeInformationShapes(string rawArch, string expected)
    {
        var profile = LinuxHardwareDigest.TryBuildProfile(BuildFrame(arch: rawArch));
        Assert.NotNull(profile);
        Assert.Equal(expected, profile.Architecture);
    }

    // ------------------------------------------------------------------
    //  dmidecode channel parsing through the digest
    // ------------------------------------------------------------------

    [Fact]
    public void TryBuildProfile_DmidecodeSection_DerivesRamTypeAndChannelBanks()
    {
        var profile = LinuxHardwareDigest.TryBuildProfile(BuildFrame(
            ramMB: 65536,
            dmidecode: DmidecodeDdr5TwoChannel));

        Assert.NotNull(profile);
        Assert.Equal(SystemRamType.Ddr5, profile.SystemRamType);
        // Two populated banks (DIMM_A + DIMM_B; the "No Module Installed" bank counts
        // its locator before the size line, matching the pre-task local parser).
        Assert.Equal(38.4, profile.EffectiveBandwidthGBs, precision: 1);
    }

    [Fact]
    public void TryBuildProfile_DmidecodeSectionAbsent_RamTypeStaysUnknown()
    {
        var profile = LinuxHardwareDigest.TryBuildProfile(BuildFrame(ramMB: 32768));
        Assert.NotNull(profile);
        Assert.Equal(SystemRamType.Unknown, profile.SystemRamType);
        Assert.Equal(0, profile.EffectiveBandwidthGBs, precision: 1); // unknown tier → 0
        Assert.Equal(32768, profile.TotalFastMemoryMB);
    }

    [Fact]
    public void TryBuildProfile_MissingSections_AreTreatedAsEmpty()
    {
        // A frame that ends right after the header (no RAW sections at all).
        var frame = "===BEGIN_HW===\nHOSTNAME=mini\nARCH=x86_64\nCPU=\nRAM_MB=8192\n===END_HW===\n";
        var profile = LinuxHardwareDigest.TryBuildProfile(frame);
        Assert.NotNull(profile);
        Assert.Equal("mini", profile.NodeName);
        Assert.Equal(8192, profile.SystemRamMB);
        Assert.Empty(profile.Gpus);
    }

    [Fact]
    public void TryBuildProfile_CrlfLineEndings_Parse()
    {
        var profile = LinuxHardwareDigest.TryBuildProfile(
            BuildFrame(hostname: "crlf-node", ramMB: 16384).Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.NotNull(profile);
        Assert.Equal("crlf-node", profile.NodeName);
        Assert.Equal(16384, profile.SystemRamMB);
    }
}
