using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaRouter.Options;
using OllamaRouter.Services.Hardware;
using OllamaRouter.Tests.Services;

namespace OllamaRouter.Tests.Services.Hardware;

/// <summary>
/// Tests of the daemon-side Linux probe executor (task #143): the script-content regression
/// (the embedded script is the single Linux collector and carries NO classification logic)
/// and the local/ssh invocation construction via <see cref="FakeProcessRunner"/> (no real
/// ssh/bash is ever launched): stdin delivery for local runs, the exact ssh argument string
/// (user@host, port, BatchMode/no-prompt hardening, 30 s bound, base64 payload round-trip)
/// and the fail-soft outcome matrix (launch failure / non-zero exit / timeout / missing
/// markers / missing sshUser / unresolvable url / cancellation — never a throw).
/// </summary>
public class LinuxHardwareProbeExecutorTests
{
    private const string CannedFrame =
        "===BEGIN_HW===\n" +
        "HOSTNAME=fake-node\n" +
        "ARCH=x86_64\n" +
        "CPU=Test CPU\n" +
        "RAM_MB=16384\n" +
        "---DMIDECODE_MEMORY---\n" +
        "---NVIDIA_GPUS---\n" +
        "NVIDIA GeForce RTX 4090, 24564\n" +
        "---ROCM_GPUS---\n" +
        "---LSPCI_GPUS---\n" +
        "===END_HW===\n";

    private static LinuxHardwareProbeExecutor CreateExecutor(FakeProcessRunner runner) =>
        new(runner, NullLogger<LinuxHardwareProbeExecutor>.Instance);

    // ------------------------------------------------------------------
    //  Script-content regression (the embedded script is the ONLY collector)
    // ------------------------------------------------------------------

    [Fact]
    public void EmbeddedScript_IsNonEmpty_AndCarriesTheFullMarkerFrameContract()
    {
        var script = LinuxHardwareProbeScript.ScriptText;
        Assert.False(string.IsNullOrWhiteSpace(script));
        Assert.StartsWith("#!/usr/bin/env bash", script, StringComparison.Ordinal);

        // The marker frame + every RAW section marker.
        Assert.Contains(LinuxHardwareProbeScript.BeginMarker, script, StringComparison.Ordinal);
        Assert.Contains(LinuxHardwareProbeScript.EndMarker, script, StringComparison.Ordinal);
        Assert.Contains(LinuxHardwareProbeScript.SectionDmidecodeMemory, script, StringComparison.Ordinal);
        Assert.Contains(LinuxHardwareProbeScript.SectionNvidiaGpus, script, StringComparison.Ordinal);
        Assert.Contains(LinuxHardwareProbeScript.SectionRocmGpus, script, StringComparison.Ordinal);
        Assert.Contains(LinuxHardwareProbeScript.SectionLspciGpus, script, StringComparison.Ordinal);

        // The four tool invocations (dmidecode via non-interactive sudo first, then bare).
        Assert.Contains("sudo -n dmidecode -t memory", script, StringComparison.Ordinal);
        Assert.Contains("dmidecode -t memory", script, StringComparison.Ordinal);
        Assert.Contains("nvidia-smi --query-gpu=name,memory.total --format=csv,noheader,nounits", script, StringComparison.Ordinal);
        Assert.Contains("rocm-smi --showmeminfo vram --json", script, StringComparison.Ordinal);
        Assert.Contains("lspci | grep -iE 'vga|3d|display'", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gddr")]
    [InlineData("hbm")]
    [InlineData("bandwidth")]
    [InlineData("unified")]
    public void EmbeddedScript_ContainsNoClassificationKeywords(string keyword)
    {
        // ALL parsing/classification/bandwidth/unified-memory logic lives in the C# digest
        // (LinuxHardwareDigest) — the script only collects raw values.
        Assert.DoesNotContain(keyword, LinuxHardwareProbeScript.ScriptText,
            StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    //  Local probe (bash over stdin — no temp file)
    // ------------------------------------------------------------------

    [Fact]
    public async Task ProbeLocalAsync_Success_DigestsStdout_AndFeedsTheScriptOverStdin()
    {
        var runner = new FakeProcessRunner().Succeeds("bash", string.Empty, CannedFrame);
        var executor = CreateExecutor(runner);

        var result = await executor.ProbeLocalAsync();

        Assert.NotNull(result.Profile);
        Assert.Null(result.Error);
        Assert.Equal("fake-node", result.Profile!.NodeName);
        Assert.Equal("linux", result.Profile.Os);
        Assert.Single(result.Profile.Gpus);

        // Exactly ONE bash invocation, the script delivered as STDIN (no temp file),
        // bounded at 30 s.
        var invocations = runner.GetInvocations();
        var invocation = Assert.Single(invocations);
        Assert.Equal("bash", invocation.FileName);
        Assert.Equal(string.Empty, invocation.Arguments);
        Assert.Equal(LinuxHardwareProbeExecutor.ProbeTimeoutMs, invocation.TimeoutMs);
        Assert.Equal(30_000, invocation.TimeoutMs);
        Assert.Equal(LinuxHardwareProbeScript.ScriptText, invocation.StandardInput);
    }

    [Fact]
    public async Task ProbeLocalAsync_LaunchFailure_ReturnsReadableError()
    {
        var runner = new FakeProcessRunner().FailsToStart("bash", string.Empty, "bash: command not found");
        var executor = CreateExecutor(runner);

        var result = await executor.ProbeLocalAsync();

        Assert.Null(result.Profile);
        Assert.NotNull(result.Error);
        Assert.Contains("failed to launch", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bash: command not found", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeLocalAsync_NonZeroExit_ReturnsExitCodeAndStderrTail()
    {
        var runner = new FakeProcessRunner().FailsWithStdErr("bash", string.Empty, 1, "bash: /x: line 3: syntax error");
        var executor = CreateExecutor(runner);

        var result = await executor.ProbeLocalAsync();

        Assert.Null(result.Profile);
        Assert.Contains("code 1", result.Error!, StringComparison.Ordinal);
        Assert.Contains("syntax error", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeLocalAsync_Timeout_ReturnsReadableTimeoutError()
    {
        var runner = new FakeProcessRunner().TimesOut("bash", string.Empty, "partial output");
        var executor = CreateExecutor(runner);

        var result = await executor.ProbeLocalAsync();

        Assert.Null(result.Profile);
        Assert.Contains("timed out", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("30", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeLocalAsync_MissingMarkerFrame_ReturnsReadableError()
    {
        var runner = new FakeProcessRunner().Succeeds("bash", string.Empty, "no hardware frame in this output\n");
        var executor = CreateExecutor(runner);

        var result = await executor.ProbeLocalAsync();

        Assert.Null(result.Profile);
        Assert.Contains("marker frame", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeLocalAsync_CancelledToken_FailsSoftWithoutAnyProcessLaunch()
    {
        var runner = new FakeProcessRunner().Succeeds("bash", string.Empty, CannedFrame);
        var executor = CreateExecutor(runner);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await executor.ProbeLocalAsync(cts.Token);

        Assert.Null(result.Profile);
        Assert.Contains("canceled", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.GetInvocations());
    }

    // ------------------------------------------------------------------
    //  Remote probe (ssh — the argument string is testable verbatim)
    // ------------------------------------------------------------------

    [Fact]
    public void BuildSshArguments_ProducesTheExactHardenedCommand()
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("SCRIPT"));
        var args = LinuxHardwareProbeExecutor.BuildSshArguments("ops", "gpu-box", 2222, base64);

        Assert.Equal(
            "-p 2222 " +
            "-o BatchMode=yes " +
            "-o ConnectTimeout=10 " +
            "-o StrictHostKeyChecking=accept-new " +
            "-o PasswordAuthentication=no " +
            "-o KbdInteractiveAuthentication=no " +
            "-o NumberOfPasswordPrompts=0 " +
            "ops@gpu-box " +
            $"'echo {base64} | base64 -d | bash'",
            args);

        // Hardening invariants: no prompts, bounded connect, no password auth.
        Assert.Contains("-o BatchMode=yes", args, StringComparison.Ordinal);
        Assert.Contains("-o NumberOfPasswordPrompts=0", args, StringComparison.Ordinal);
        Assert.DoesNotContain("-i ", args, StringComparison.Ordinal); // no identity-file handling
        Assert.DoesNotContain("sshpass", args, StringComparison.Ordinal); // no password support
    }

    [Fact]
    public async Task ProbeRemoteAsync_Success_InvokesSshWithUserHostPortAndBase64Payload()
    {
        var target = new OllamaTargetOptions
        {
            Name = "Remote",
            Url = "http://gpu-box:11434",
            SshUser = "ops",
            SshPort = 2222
        };
        var expectedArgs = LinuxHardwareProbeExecutor.BuildSshArguments(
            "ops", "gpu-box", 2222,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(LinuxHardwareProbeScript.ScriptText)));
        var runner = new FakeProcessRunner().Succeeds("ssh", expectedArgs, CannedFrame);
        var executor = CreateExecutor(runner);

        var result = await executor.ProbeRemoteAsync(target);

        Assert.NotNull(result.Profile);
        Assert.Equal("fake-node", result.Profile!.NodeName);
        var invocation = Assert.Single(runner.GetInvocations());
        Assert.Equal("ssh", invocation.FileName);
        Assert.Equal(expectedArgs, invocation.Arguments);
        Assert.Equal(LinuxHardwareProbeExecutor.ProbeTimeoutMs, invocation.TimeoutMs);
        Assert.Null(invocation.StandardInput); // the script travels inside the remote command
    }

    [Fact]
    public async Task ProbeRemoteAsync_DefaultPortIs22_WhenSshPortIsNullOrInvalid()
    {
        var withNull = new OllamaTargetOptions { Name = "R", Url = "http://gpu-box:11434", SshUser = "ops" };
        var expectedDefault = LinuxHardwareProbeExecutor.BuildSshArguments(
            "ops", "gpu-box", 22,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(LinuxHardwareProbeScript.ScriptText)));
        var runner = new FakeProcessRunner().Succeeds("ssh", expectedDefault, CannedFrame);
        var executor = CreateExecutor(runner);
        var result = await executor.ProbeRemoteAsync(withNull);
        Assert.NotNull(result.Profile);
        Assert.Equal(expectedDefault, runner.GetInvocations()[0].Arguments);

        // Out-of-range port → GetEffectiveSshPort() falls back to 22 (defensive read).
        var withBadPort = new OllamaTargetOptions { Name = "R", Url = "http://gpu-box:11434", SshUser = "ops", SshPort = 0 };
        var runner2 = new FakeProcessRunner().Succeeds("ssh", expectedDefault, CannedFrame);
        var result2 = await CreateExecutor(runner2).ProbeRemoteAsync(withBadPort);
        Assert.NotNull(result2.Profile);
        Assert.Equal(expectedDefault, runner2.GetInvocations()[0].Arguments);
    }

    [Fact]
    public async Task ProbeRemoteAsync_Base64PayloadRoundTripsToTheEmbeddedScript()
    {
        var target = new OllamaTargetOptions { Name = "R", Url = "http://gpu-box:11434", SshUser = "ops" };
        var runner = new FakeProcessRunner();
        var executor = CreateExecutor(runner);
        await executor.ProbeRemoteAsync(target);

        var args = runner.GetInvocations()[0].Arguments;
        var marker = "'echo ";
        var start = args.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = args.IndexOf(" | base64 -d | bash'", start, StringComparison.Ordinal);
        var base64 = args[start..end];
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        Assert.Equal(LinuxHardwareProbeScript.ScriptText, decoded);
    }

    [Fact]
    public async Task ProbeRemoteAsync_MissingSshUser_FailsSoftWithoutAnyProcessLaunch()
    {
        var target = new OllamaTargetOptions { Name = "R", Url = "http://gpu-box:11434" };
        var runner = new FakeProcessRunner();
        var executor = CreateExecutor(runner);

        var result = await executor.ProbeRemoteAsync(target);

        Assert.Null(result.Profile);
        Assert.Contains("sshUser", result.Error!, StringComparison.Ordinal);
        Assert.Empty(runner.GetInvocations());
    }

    [Fact]
    public async Task ProbeRemoteAsync_WhitespaceSshUser_FailsSoftWithoutAnyProcessLaunch()
    {
        var target = new OllamaTargetOptions { Name = "R", Url = "http://gpu-box:11434", SshUser = "   " };
        var runner = new FakeProcessRunner();

        var result = await CreateExecutor(runner).ProbeRemoteAsync(target);

        Assert.Null(result.Profile);
        Assert.Contains("sshUser", result.Error!, StringComparison.Ordinal);
        Assert.Empty(runner.GetInvocations());
    }

    [Fact]
    public async Task ProbeRemoteAsync_UnresolvableUrl_FailsSoftWithoutAnyProcessLaunch()
    {
        var target = new OllamaTargetOptions { Name = "R", Url = "not-a-url", SshUser = "ops" };
        var runner = new FakeProcessRunner();

        var result = await CreateExecutor(runner).ProbeRemoteAsync(target);

        Assert.Null(result.Profile);
        Assert.Contains("does not resolve", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.GetInvocations());
    }

    [Fact]
    public async Task ProbeRemoteAsync_SshAuthFailure_ReturnsReadableErrorWithExitCode()
    {
        var target = new OllamaTargetOptions { Name = "R", Url = "http://gpu-box:11434", SshUser = "ops" };
        var args = LinuxHardwareProbeExecutor.BuildSshArguments(
            "ops", "gpu-box", 22,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(LinuxHardwareProbeScript.ScriptText)));
        var runner = new FakeProcessRunner().FailsWithStdErr(
            "ssh", args, 255, "ops@gpu-box: Permission denied (publickey).");
        var executor = CreateExecutor(runner);

        var result = await executor.ProbeRemoteAsync(target);

        Assert.Null(result.Profile);
        Assert.Contains("code 255", result.Error!, StringComparison.Ordinal);
        Assert.Contains("Permission denied (publickey)", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeRemoteAsync_SshUnreachable_ReturnsReadableError()
    {
        var target = new OllamaTargetOptions { Name = "R", Url = "http://192.168.5.99:11434", SshUser = "ops" };
        var args = LinuxHardwareProbeExecutor.BuildSshArguments(
            "ops", "192.168.5.99", 22,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(LinuxHardwareProbeScript.ScriptText)));
        var runner = new FakeProcessRunner().FailsWithStdErr(
            "ssh", args, 255, "ssh: connect to host 192.168.5.99 port 22: No route to host");
        var executor = CreateExecutor(runner);

        var result = await executor.ProbeRemoteAsync(target);

        Assert.Null(result.Profile);
        Assert.Contains("No route to host", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeRemoteAsync_SshMissingMarkers_ReturnsReadableError()
    {
        var target = new OllamaTargetOptions { Name = "R", Url = "http://winbox:11434", SshUser = "ops" };
        var args = LinuxHardwareProbeExecutor.BuildSshArguments(
            "ops", "winbox", 22,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(LinuxHardwareProbeScript.ScriptText)));
        // A non-Linux remote (e.g. Windows) runs the script where bash does not exist or
        // emits no frame — either way the digest must fail softly with a readable error.
        var runner = new FakeProcessRunner().Succeeds("ssh", args, "bash: not found\n");
        var executor = CreateExecutor(runner);

        var result = await executor.ProbeRemoteAsync(target);

        Assert.Null(result.Profile);
        Assert.Contains("marker frame", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeRemoteAsync_SshTimeout_ReturnsReadableTimeoutError()
    {
        var target = new OllamaTargetOptions { Name = "R", Url = "http://slowbox:11434", SshUser = "ops" };
        var args = LinuxHardwareProbeExecutor.BuildSshArguments(
            "ops", "slowbox", 22,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(LinuxHardwareProbeScript.ScriptText)));
        var runner = new FakeProcessRunner().TimesOut("ssh", args, "partial");
        var executor = CreateExecutor(runner);

        var result = await executor.ProbeRemoteAsync(target);

        Assert.Null(result.Profile);
        Assert.Contains("timed out", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeRemoteAsync_CancelledToken_FailsSoftWithoutAnyProcessLaunch()
    {
        var target = new OllamaTargetOptions { Name = "R", Url = "http://gpu-box:11434", SshUser = "ops" };
        var runner = new FakeProcessRunner();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await CreateExecutor(runner).ProbeRemoteAsync(target, cts.Token);

        Assert.Null(result.Profile);
        Assert.Contains("canceled", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.GetInvocations());
    }

    [Fact]
    public async Task ProbeRemoteAsync_LaunchFailure_ReturnsReadableError()
    {
        var target = new OllamaTargetOptions { Name = "R", Url = "http://gpu-box:11434", SshUser = "ops" };
        var runner = new FakeProcessRunner().FailsToStart("ssh", string.Empty, "ssh: command not found");
        var executor = CreateExecutor(runner);

        var result = await executor.ProbeRemoteAsync(target);

        Assert.Null(result.Profile);
        Assert.Contains("failed to launch", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ToProbeResult_Errors_TruncatedTo512Characters()
    {
        var longStderr = new string('x', 600) + " root-cause-at-the-tail";
        var runner = new FakeProcessRunner().FailsWithStdErr("bash", string.Empty, 1, longStderr);
        var executor = CreateExecutor(runner);

        var result = await executor.ProbeLocalAsync();

        Assert.Null(result.Profile);
        Assert.EndsWith("root-cause-at-the-tail", result.Error!, StringComparison.Ordinal);
        // The stderr tail is capped at ~512 chars (plus the short error prefix).
        var xCount = result.Error.Count(c => c == 'x');
        Assert.True(xCount <= 512, $"stderr tail must be capped at 512 chars, got {xCount} leading 'x' chars");
        Assert.True(result.Error.Length <= 512 + 100, $"error too long: {result.Error.Length}");
    }
}
