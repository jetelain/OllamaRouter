using OllamaRouter.Endpoints;
using OllamaRouter.Services;

namespace OllamaRouter.Tests.Services;

public class TerminalMonitorRendererTests
{
    private static MonitorFleetTargetItem FleetItem(
        string name,
        string? url = null,
        bool enabled = true,
        string liveness = "Online",
        string? activeModel = null,
        int inFlightRequests = 0,
        string? hardwareProfile = null,
        string contextAllocation = "-")
    {
        return new MonitorFleetTargetItem(name, enabled, liveness, inFlightRequests, contextAllocation, url, activeModel, hardwareProfile);
    }

    private static MonitorStateResponse CreateSampleState(
        bool cloudEnabled = false,
        bool localEnabled = true,
        bool remoteEnabled = true,
        bool cloudEnabledTarget = true,
        string localLiveness = "Online",
        string remoteLiveness = "Online",
        int localInFlight = 0,
        int remoteInFlight = 0,
        string? localActiveModel = null,
        string? remoteActiveModel = null,
        string localContext = "-",
        string remoteContext = "-",
        string cloudContext = "baseline",
        ActivityStatisticsTotals? todayLocal = null,
        ActivityStatisticsTotals? todayRemote = null,
        ActivityStatisticsTotals? todayCloud = null,
        ActivityStatisticsTotals? last7Local = null,
        ActivityStatisticsTotals? last7Remote = null,
        ActivityStatisticsTotals? last7Cloud = null,
        List<MonitorInProgressItem>? inProgress = null,
        List<MonitorRecentRequestItem>? recentRequests = null,
        MonitorCostStatus? cost = null,
        MonitorOverheadStatus? overhead = null)
    {
        var tl = todayLocal ?? new ActivityStatisticsTotals(0, 0, 0);
        var tr = todayRemote ?? new ActivityStatisticsTotals(0, 0, 0);
        var tc = todayCloud ?? new ActivityStatisticsTotals(0, 0, 0);
        var tTot = new ActivityStatisticsTotals(
            tl.Requests + tr.Requests + (cloudEnabled ? tc.Requests : 0),
            tl.InputTokens + tr.InputTokens + (cloudEnabled ? tc.InputTokens : 0),
            tl.OutputTokens + tr.OutputTokens + (cloudEnabled ? tc.OutputTokens : 0));

        var l7l = last7Local ?? new ActivityStatisticsTotals(0, 0, 0);
        var l7r = last7Remote ?? new ActivityStatisticsTotals(0, 0, 0);
        var l7c = last7Cloud ?? new ActivityStatisticsTotals(0, 0, 0);
        var l7Tot = new ActivityStatisticsTotals(
            l7l.Requests + l7r.Requests + (cloudEnabled ? l7c.Requests : 0),
            l7l.InputTokens + l7r.InputTokens + (cloudEnabled ? l7c.InputTokens : 0),
            l7l.OutputTokens + l7r.OutputTokens + (cloudEnabled ? l7c.OutputTokens : 0));

        var fleet = new List<MonitorFleetTargetItem>
        {
            FleetItem("Local", "http://localhost:11434", localEnabled, localLiveness, localActiveModel, localInFlight, "16gb", localContext),
            FleetItem("Remote", "http://192.168.1.2:11434", remoteEnabled, remoteLiveness, remoteActiveModel, remoteInFlight, "16gb", remoteContext)
        };
        // The Cloud pseudo-target is always present and always last.
        fleet.Add(FleetItem("Cloud", null, cloudEnabledTarget, cloudEnabled ? "Online" : "Offline", null, 0, null, cloudContext));

        return new MonitorStateResponse(
            cloudEnabled,
            fleet,
            new MonitorStatisticsStatus(
                new Dictionary<string, ActivityStatisticsTotals>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Local"] = tl,
                    ["Remote"] = tr,
                    ["Cloud"] = tc,
                    ["total"] = tTot
                },
                new Dictionary<string, ActivityStatisticsTotals>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Local"] = l7l,
                    ["Remote"] = l7r,
                    ["Cloud"] = l7c,
                    ["total"] = l7Tot
                }),
            inProgress ?? [],
            recentRequests ?? [],
            cost,
            overhead);
    }

    private static MonitorCostStatus Cost(double savings, double cloudCost, params (string Name, double Savings)[] perTarget)
    {
        return new MonitorCostStatus(
            "$",
            savings,
            cloudCost,
            perTarget.ToDictionary(pair => pair.Name, pair => pair.Savings, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Render_IdleState_ContainsTitleAndMonitorLinkAndTargetStatuses()
    {
        var state = CreateSampleState();
        var monitorUrl = "http://localhost:11434/monitor";

        var output = TerminalMonitorRenderer.Render(state, monitorUrl, enableAnsi: false);

        Assert.Contains("Ollama Router - Activity Monitor", output);
        Assert.Contains("Full monitor: http://localhost:11434/monitor", output);
        Assert.Contains("FLEET", output);
        Assert.Contains("\u25CF Online", output);
        Assert.Contains("No request in progress", output);
        Assert.Contains("No recent requests", output);
        Assert.Contains("No token activity in the last 7 days", output);

        // Verify NO actions or buttons
        Assert.DoesNotContain("Reclaim", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("checkbox", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("toggle", output, StringComparison.OrdinalIgnoreCase);

        // Verify NO overhead recommendation
        Assert.DoesNotContain("Overhead factor", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TokenEstimationOverheadFactor", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_FleetTable_RendersDisabledOfflineAndLivenessStates()
    {
        var state = CreateSampleState(
            cloudEnabled: true,
            localEnabled: true,
            remoteEnabled: true,
            remoteLiveness: "Offline",
            localLiveness: "Probing",
            cloudEnabledTarget: false);

        var output = TerminalMonitorRenderer.Render(state, "http://localhost:11434/monitor", enableAnsi: false);

        Assert.Contains("FLEET", output);
        Assert.Contains("\u25CF Disabled", output); // Cloud row is disabled
        Assert.Contains("\u25CF Offline", output);
        Assert.Contains("\u25CF Probing", output);
    }

    [Fact]
    public void Render_ThreeTargetFleet_WithOneOffline_ShowsAllRowsWithLivenessAndInFlight()
    {
        var fleet = new List<MonitorFleetTargetItem>
        {
            FleetItem("desktop", "http://192.168.1.10:11434", true, "Online", "llama3.2:latest", 2, "16gb", "65.5k (expanded)"),
            FleetItem("laptop", "http://192.168.1.11:11434", true, "Offline", null, 0, null, "-"),
            FleetItem("server1", "http://192.168.1.12:11434", true, "Probing", "qwen3:8b", 1, "96gb", "32.8k (baseline)"),
            FleetItem("Cloud", null, true, "Offline", null, 0, null, "baseline")
        };

        var state = new MonitorStateResponse(
            CloudEnabled: false,
            Fleet: fleet,
            Statistics: new MonitorStatisticsStatus(
                new Dictionary<string, ActivityStatisticsTotals>(StringComparer.OrdinalIgnoreCase)
                {
                    ["desktop"] = new ActivityStatisticsTotals(0, 0, 0),
                    ["laptop"] = new ActivityStatisticsTotals(0, 0, 0),
                    ["server1"] = new ActivityStatisticsTotals(0, 0, 0),
                    ["Cloud"] = new ActivityStatisticsTotals(0, 0, 0),
                    ["total"] = new ActivityStatisticsTotals(0, 0, 0)
                },
                new Dictionary<string, ActivityStatisticsTotals>(StringComparer.OrdinalIgnoreCase)
                {
                    ["desktop"] = new ActivityStatisticsTotals(0, 0, 0),
                    ["laptop"] = new ActivityStatisticsTotals(0, 0, 0),
                    ["server1"] = new ActivityStatisticsTotals(0, 0, 0),
                    ["Cloud"] = new ActivityStatisticsTotals(0, 0, 0),
                    ["total"] = new ActivityStatisticsTotals(0, 0, 0)
                }),
            InProgress: [],
            Requests: [],
            Cost: null,
            Overhead: null);

        var output = TerminalMonitorRenderer.Render(state, "http://localhost:11434/monitor", enableAnsi: false);

        // All three configured targets plus the Cloud row are rendered.
        Assert.Contains("desktop", output);
        Assert.Contains("laptop", output);
        Assert.Contains("server1", output);
        Assert.Contains("Cloud", output);

        // Liveness badges per row.
        Assert.Contains("\u25CF Online", output);
        Assert.Contains("\u25CF Offline", output);
        Assert.Contains("\u25CF Probing", output);

        // In-flight counts, hardware profiles, models and context allocations.
        Assert.Contains("llama3.2:latest", output);
        Assert.Contains("qwen3:8b", output);
        Assert.Contains("16gb", output);
        Assert.Contains("96gb", output);
        Assert.Contains("65.5k (expanded)", output);
        Assert.Contains("32.8k (baseline)", output);

        var lines = output.Split(["\r\n", "\n"], StringSplitOptions.None);
        var desktopRow = lines.Single(l => l.Contains("desktop"));
        var laptopRow = lines.Single(l => l.Contains("laptop"));
        var server1Row = lines.Single(l => l.Contains("server1"));
        var cloudRow = lines.Single(l => l.Contains("Cloud") && !l.Contains("Cloud:"));

        // desktop: online, 2 in-flight, 16gb, expanded context.
        Assert.Contains("\u25CF Online", desktopRow);
        Assert.Contains(" 2 ", " " + desktopRow.TrimEnd() + " ");
        Assert.Contains("16gb", desktopRow);
        Assert.Contains("65.5k (expanded)", desktopRow);

        // laptop: offline, 0 in-flight, no hardware profile, no lease.
        Assert.Contains("\u25CF Offline", laptopRow);
        Assert.EndsWith("0 -", laptopRow.TrimEnd());
        Assert.DoesNotContain("●", laptopRow.Replace("\u25CF Offline", ""));

        // server1: probing, 1 in-flight, 96gb, baseline lease.
        Assert.Contains("\u25CF Probing", server1Row);
        Assert.Contains("96gb", server1Row);
        Assert.Contains("32.8k (baseline)", server1Row);

        // Cloud row: offline (cloud disabled), no models, "baseline" context.
        Assert.Contains("\u25CF Offline", cloudRow);
        Assert.Contains("baseline", cloudRow);
    }

    [Fact]
    public void Render_StatisticsTotals_SumOfPerTargetRowsMatchesTotalRow()
    {
        var state = CreateSampleState(
            cloudEnabled: true,
            todayLocal: new ActivityStatisticsTotals(5, 12500, 3200),
            todayRemote: new ActivityStatisticsTotals(2, 45000, 15000),
            todayCloud: new ActivityStatisticsTotals(1, 100000, 5000),
            last7Local: new ActivityStatisticsTotals(50, 1250000, 320000),
            last7Remote: new ActivityStatisticsTotals(20, 4500000, 1500000),
            last7Cloud: new ActivityStatisticsTotals(5, 500000, 100000));

        var output = TerminalMonitorRenderer.Render(state, "http://localhost:11434/monitor", consoleHeight: 35, enableAnsi: false);

        Assert.Contains("STATISTICS", output);
        Assert.Contains("Total", output);

        // The "total" row values equal the sums of the per-target rows.
        var todayTotals = new ActivityStatisticsTotals(8, 157500, 23200);
        var last7Totals = new ActivityStatisticsTotals(75, 6250000, 1920000);
        Assert.Contains(TerminalMonitorRenderer.FormatNumber(todayTotals.Requests), output);
        Assert.Contains(TerminalMonitorRenderer.FormatTokens(todayTotals.InputTokens), output);
        Assert.Contains(TerminalMonitorRenderer.FormatTokens(todayTotals.OutputTokens), output);
        Assert.Contains(TerminalMonitorRenderer.FormatNumber(last7Totals.Requests), output);
        Assert.Contains(TerminalMonitorRenderer.FormatTokens(last7Totals.InputTokens), output);
        Assert.Contains(TerminalMonitorRenderer.FormatTokens(last7Totals.OutputTokens), output);

        Assert.Contains("USAGE RATIO (last 7 days)", output);
        Assert.Contains("Total tokens:", output);
        Assert.Contains("Local:", output);
        Assert.Contains("Remote:", output);
        Assert.Contains("Cloud:", output);
    }

    [Fact]
    public void Render_WithCost_RendersPerTargetSavingsLinesThatSumToTotal()
    {
        var cost = Cost(
            15.75,
            0.45,
            ("Local", 12.30),
            ("Remote", 3.45));
        var state = CreateSampleState(cloudEnabled: true, cost: cost);

        var output = TerminalMonitorRenderer.Render(state, "http://localhost:11434/monitor", enableAnsi: false);

        Assert.Contains("COST & SAVINGS (last 7 days)", output);
        Assert.Contains("Savings: ~$15.75", output);
        Assert.Contains("Cloud: ~$0.45", output);

        // Per-target savings lines render for each non-Cloud target.
        Assert.Contains("Local: ~$12.30", output);
        Assert.Contains("Remote: ~$3.45", output);

        // The per-target values sum to the rendered total.
        var perTargetSum = cost.PerTargetSavings.Values.Sum();
        Assert.Equal(cost.EstimatedSavings, perTargetSum, 4);
    }

    [Fact]
    public void Render_WithoutCost_OmitsCostSection()
    {
        var state = CreateSampleState(cost: null);

        var output = TerminalMonitorRenderer.Render(state, "http://localhost:11434/monitor", enableAnsi: false);

        Assert.DoesNotContain("COST & SAVINGS", output);
    }

    [Fact]
    public void Render_InProgressAndRecentRequests_RendersRowsProperly()
    {
        var now = DateTimeOffset.UtcNow;
        var inProgress = new List<MonitorInProgressItem>
        {
            new("Local", "Qwen3.8-27B", 3500, 2400.0)
        };

        var recent = new List<MonitorRecentRequestItem>
        {
            new(now.AddSeconds(-10), "Local", "Qwen3.8-27B", 3500, 3600, 250, 1850.0, 200, true),
            new(now.AddSeconds(-30), "Remote", "llama3", 15000, null, null, 5000.0, 500, false)
        };

        var state = CreateSampleState(inProgress: inProgress, recentRequests: recent);

        var output = TerminalMonitorRenderer.Render(state, "http://localhost:11434/monitor", consoleHeight: 30, enableAnsi: false);

        Assert.Contains("IN PROGRESS", output);
        Assert.Contains("Qwen3.8-27B", output);
        Assert.Contains("2.4s", output);

        Assert.Contains("RECENT REQUESTS", output);
        Assert.Contains("3.5k / 3.6k", output);
        Assert.Contains("1.9s", output);
        Assert.Contains("500", output);
    }

    [Theory]
    [InlineData(null, "-")]
    [InlineData(0, "0")]
    [InlineData(450, "450")]
    [InlineData(1500, "1.5k")]
    [InlineData(25000, "25.0k")]
    [InlineData(1250000, "1.3M")]
    public void FormatTokens_ReturnsExpectedRepresentation(int? tokens, string expected)
    {
        var actual = TerminalMonitorRenderer.FormatTokens(tokens);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(42, "42")]
    [InlineData(1234, "1 234")]
    [InlineData(1000000, "1 000 000")]
    public void FormatNumber_ReturnsExpectedRepresentation(long number, string expected)
    {
        var actual = TerminalMonitorRenderer.FormatNumber(number);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0.0, "$", "$0.00")]
    [InlineData(0.0052, "$", "$0.0052")]
    [InlineData(12.345, "$", "$12.35")]
    [InlineData(10.0, "€", "€10.00")]
    public void FormatCost_ReturnsExpectedRepresentation(double amount, string currency, string expected)
    {
        var actual = TerminalMonitorRenderer.FormatCost(amount, currency);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PadRightDisplay_WithEmojisAndAscii_AlignsToSameDisplayWidth()
    {
        Assert.Equal(2, TerminalMonitorRenderer.GetDisplayWidth("\uD83D\uDDA5️"));
        Assert.Equal(2, TerminalMonitorRenderer.GetDisplayWidth("\uD83D\uDCE1"));
        Assert.Equal(2, TerminalMonitorRenderer.GetDisplayWidth("☁️"));

        Assert.Equal(8, TerminalMonitorRenderer.GetDisplayWidth("\uD83D\uDDA5️ Local"));
        Assert.Equal(9, TerminalMonitorRenderer.GetDisplayWidth("\uD83D\uDCE1 Remote"));
        Assert.Equal(8, TerminalMonitorRenderer.GetDisplayWidth("☁️ Cloud"));

        var local = TerminalMonitorRenderer.PadRightDisplay("\uD83D\uDDA5️ Local", 13);
        var remote = TerminalMonitorRenderer.PadRightDisplay("\uD83D\uDCE1 Remote", 13);
        var cloud = TerminalMonitorRenderer.PadRightDisplay("☁️ Cloud", 13);
        var total = TerminalMonitorRenderer.PadRightDisplay("Total", 13);

        Assert.Equal(13, TerminalMonitorRenderer.GetDisplayWidth(local));
        Assert.Equal(13, TerminalMonitorRenderer.GetDisplayWidth(remote));
        Assert.Equal(13, TerminalMonitorRenderer.GetDisplayWidth(cloud));
        Assert.Equal(13, TerminalMonitorRenderer.GetDisplayWidth(total));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Render_AllLines_NeverExceed79DisplayColumns(bool enableAnsi)
    {
        var now = DateTimeOffset.UtcNow;
        var inProgress = new List<MonitorInProgressItem>
        {
            new("Local", "qwen2.5-coder:32b-instruct-q4_K_M", 3500, 2400.0),
            new("Remote", "deepseek-r1:70b-llama-distill", 12000, 45000.0),
            new("Cloud", "gpt-4o-mini", 150, 800.0)
        };

        var recent = new List<MonitorRecentRequestItem>
        {
            new(now.AddSeconds(-5), "Local", "qwen2.5-coder:32b-instruct-q4_K_M", 3500, 3600, 250, 1850.0, 200, true),
            new(now.AddSeconds(-15), "Remote", "deepseek-r1:70b", 12000, 12000, 1500, 12500.0, 200, true),
            new(now.AddSeconds(-30), "Cloud", "gpt-4o-mini", 500, 520, 80, 750.0, 500, false)
        };

        var cost = Cost(
            123.45,
            12.34,
            ("Local", 89.01),
            ("Remote", 34.44));

        var state = CreateSampleState(
            cloudEnabled: true,
            localEnabled: true,
            remoteEnabled: false,
            remoteLiveness: "Offline",
            localInFlight: 2,
            remoteInFlight: 0,
            localActiveModel: "qwen2.5-coder:32b-instruct-q4_K_M",
            localContext: "262.1k (expanded)",
            remoteContext: "32.8k (baseline)",
            todayLocal: new ActivityStatisticsTotals(120, 450000, 120000),
            todayRemote: new ActivityStatisticsTotals(80, 890000, 340000),
            todayCloud: new ActivityStatisticsTotals(15, 230000, 45000),
            last7Local: new ActivityStatisticsTotals(1200, 4500000, 1200000),
            last7Remote: new ActivityStatisticsTotals(800, 8900000, 3400000),
            last7Cloud: new ActivityStatisticsTotals(150, 2300000, 450000),
            inProgress: inProgress,
            recentRequests: recent,
            cost: cost);

        var monitorUrl = "http://localhost:11434/monitor";
        var rendered = TerminalMonitorRenderer.Render(state, monitorUrl, consoleWidth: 80, consoleHeight: 40, enableAnsi: enableAnsi);

        var lines = rendered.Split(["\r\n", "\n"], StringSplitOptions.None);
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line)) continue;
            var width = TerminalMonitorRenderer.GetDisplayWidth(line);
            Assert.True(width <= 79, $"Line exceeded 79 columns ({width} cols): '{line}'");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Render_LargeFleet_WithLongNames_NeverExceeds79Columns(bool enableAnsi)
    {
        // 5 configured targets (with long names and long models) + Cloud; long values truncate.
        var fleet = new List<MonitorFleetTargetItem>
        {
            FleetItem("workstation-primary", "http://192.168.1.10:11434", true, "Online", "llama-3.2-70b-instruct-fp8-tensorrt", 3, "16gb", "262.1k (expanded)"),
            FleetItem("laptop", "http://192.168.1.11:11434", false, "Probing", null, 0, null, "-"),
            FleetItem("server-rack-one", "http://192.168.1.12:11434", true, "Online", "qwen3:8b", 1, "96gb", "184.3k (expanded)"),
            FleetItem("mac-mini-studio", "http://192.168.1.13:11434", true, "Offline", null, 0, "16384 MB", "-"),
            FleetIndexItem(5),
            FleetItem("Cloud", null, true, "Online", null, 0, null, "baseline")
        };

        var state = new MonitorStateResponse(
            CloudEnabled: true,
            Fleet: fleet,
            Statistics: new MonitorStatisticsStatus(
                new Dictionary<string, ActivityStatisticsTotals>(StringComparer.OrdinalIgnoreCase)
                {
                    ["total"] = new ActivityStatisticsTotals(0, 0, 0)
                },
                new Dictionary<string, ActivityStatisticsTotals>(StringComparer.OrdinalIgnoreCase)
                {
                    ["total"] = new ActivityStatisticsTotals(0, 0, 0)
                }),
            InProgress: [],
            Requests: [],
            Cost: Cost(1.5, 0.25, ("workstation-primary", 1.25), ("server-rack-one", 0.25)),
            Overhead: null);

        var rendered = TerminalMonitorRenderer.Render(state, "http://localhost:11434/monitor", consoleWidth: 80, consoleHeight: 30, enableAnsi: enableAnsi);

        var lines = rendered.Split(["\r\n", "\n"], StringSplitOptions.None);
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line)) continue;
            var width = TerminalMonitorRenderer.GetDisplayWidth(line);
            Assert.True(width <= 79, $"Line exceeded 79 columns ({width} cols): '{line}'");
        }
    }

    private static MonitorFleetTargetItem FleetIndexItem(int index)
    {
        // A sixth-ish oddball name beyond the truncation budget.
        return FleetItem($"extra-target-node-{index}-very-long-name", "http://192.168.1.14:11434", true, "Online", null, 0, null, "-");
    }

    [Fact]
    public void Render_FleetNamedTarget_DisplaysNameWithoutWellKnownIcon()
    {
        var stats = new MonitorStatisticsStatus(
            new Dictionary<string, ActivityStatisticsTotals>(StringComparer.OrdinalIgnoreCase)
            {
                ["desktop"] = new ActivityStatisticsTotals(1, 100, 50),
                ["Cloud"] = new ActivityStatisticsTotals(0, 0, 0),
                ["total"] = new ActivityStatisticsTotals(1, 100, 50)
            },
            new Dictionary<string, ActivityStatisticsTotals>(StringComparer.OrdinalIgnoreCase)
            {
                ["desktop"] = new ActivityStatisticsTotals(1, 100, 50),
                ["Cloud"] = new ActivityStatisticsTotals(0, 0, 0),
                ["total"] = new ActivityStatisticsTotals(1, 100, 50)
            });

        var fleet = new List<MonitorFleetTargetItem>
        {
            FleetItem("desktop", "http://192.168.1.5:11434", true, "Online"),
            FleetItem("Cloud", null, true, "Online", null, 0, null, "baseline")
        };

        var state = new MonitorStateResponse(
            CloudEnabled: true,
            Fleet: fleet,
            Statistics: stats,
            InProgress: [],
            Requests: [],
            Cost: null,
            Overhead: null);

        var output = TerminalMonitorRenderer.Render(state, "http://localhost:11434/monitor", enableAnsi: false);

        // Fleet names are displayed as plain names (no well-known icon prefix) in the Target column.
        var desktopRow = output.Split(['\n'], StringSplitOptions.None).Single(l => l.Contains("desktop") && l.Contains("\u25CF"));
        Assert.Contains("\u25CF Online", desktopRow);
        // No icon prefixed onto the plain fleet name in the row.
        Assert.DoesNotContain("\uD83D\uDDA5\uFE0F desktop", desktopRow, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_RecentRequestsAndInProgress_ColumnsAlignWithDividers()
    {
        var now = DateTimeOffset.UtcNow;
        var inProgress = new List<MonitorInProgressItem>
        {
            new("Local", "model-local", 1000, 1000.0),
            new("Remote", "model-remote", 2000, 2000.0),
            new("Cloud", "model-cloud", 3000, 3000.0)
        };
        var recent = new List<MonitorRecentRequestItem>
        {
            new(now, "Local", "model-local", 1000, 1000, 100, 1000.0, 200, true),
            new(now, "Remote", "model-remote", 2000, 2000, 200, 2000.0, 200, true),
            new(now, "Cloud", "model-cloud", 3000, 3000, 300, 3000.0, 200, true)
        };
        var state = CreateSampleState(cloudEnabled: true, inProgress: inProgress, recentRequests: recent);
        var rendered = TerminalMonitorRenderer.Render(state, "http://localhost:11434/monitor", consoleHeight: 40, enableAnsi: false);
        var lines = rendered.Split(["\r\n", "\n"], StringSplitOptions.None);

        // Find IN PROGRESS divider and rows
        var inProgDividerIdx = Array.FindIndex(lines, l => l.Contains("─────────── ────────────────────────────────────────"));
        Assert.True(inProgDividerIdx >= 0);
        var inProgHeader = lines[inProgDividerIdx - 1];
        var inProgDivider = lines[inProgDividerIdx];
        Assert.Equal(79, TerminalMonitorRenderer.GetDisplayWidth(inProgHeader));
        Assert.Equal(79, TerminalMonitorRenderer.GetDisplayWidth(inProgDivider));
        for (int i = 1; i <= 3; i++)
        {
            Assert.Equal(79, TerminalMonitorRenderer.GetDisplayWidth(lines[inProgDividerIdx + i]));
        }

        // Find RECENT REQUESTS divider and rows
        var recentDividerIdx = Array.FindIndex(lines, l => l.Contains("──────── ─────────── ─────────────────"));
        Assert.True(recentDividerIdx >= 0);
        var recentHeader = lines[recentDividerIdx - 1];
        var recentDivider = lines[recentDividerIdx];
        Assert.Equal(79, TerminalMonitorRenderer.GetDisplayWidth(recentHeader));
        Assert.Equal(79, TerminalMonitorRenderer.GetDisplayWidth(recentDivider));
        for (int i = 1; i <= 3; i++)
        {
            Assert.Equal(79, TerminalMonitorRenderer.GetDisplayWidth(lines[recentDividerIdx + i]));
        }
    }

    [Theory]
    [InlineData("http://localhost:11430/monitor")]
    [InlineData("http://192.168.1.100:11430/monitor")]
    [InlineData("http://very-long-domain-name-for-testing-purposes.local:11430/monitor")]
    public void Render_VariousStatesAndUrls_NeverExceeds79Columns(string monitorUrl)
    {
        var emptyState = CreateSampleState();
        var emptyRendered = TerminalMonitorRenderer.Render(emptyState, monitorUrl, enableAnsi: false);
        foreach (var line in emptyRendered.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            if (string.IsNullOrEmpty(line)) continue;
            Assert.True(TerminalMonitorRenderer.GetDisplayWidth(line) <= 79, $"Line exceeded 79 cols: '{line}'");
        }

        var fullState = CreateSampleState(
            cloudEnabled: true,
            cost: Cost(999.99, 123.45, ("Local", 876.54), ("Remote", 123.45)),
            todayLocal: new ActivityStatisticsTotals(100, 1000000, 500000),
            last7Local: new ActivityStatisticsTotals(1000, 10000000, 5000000));
        var fullRendered = TerminalMonitorRenderer.Render(fullState, monitorUrl, enableAnsi: true);
        foreach (var line in fullRendered.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            if (string.IsNullOrEmpty(line)) continue;
            Assert.True(TerminalMonitorRenderer.GetDisplayWidth(line) <= 79, $"Line exceeded 79 cols: '{line}'");
        }
    }
}
