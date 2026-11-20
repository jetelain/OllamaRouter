using OllamaRouter.Services.Process;

namespace OllamaRouter.Tests.Services;

/// <summary>
/// Test double for <see cref="IProcessRunner"/> that returns canned <see cref="ProcessResult"/>
/// values keyed by (fileName, arguments). Supports per-key scripts (multiple results returned in
/// order for consecutive calls with the same key) and records every invocation, including the
/// stdin payload of the 4-arg <c>Run</c> overload (null when the 3-arg overload was used).
/// </summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Dictionary<(string FileName, string Arguments), Queue<ProcessResult>> _scripts = new();
    private readonly object _lock = new();

    public List<(string FileName, string Arguments, int TimeoutMs, string? StandardInput)> Invocations { get; } = new();

    public FakeProcessRunner When(string fileName, string arguments, ProcessResult result)
    {
        lock (_lock)
        {
            _scripts.TryGetValue((fileName, arguments), out var queue);
            if (queue is null)
            {
                queue = new Queue<ProcessResult>();
                _scripts[(fileName, arguments)] = queue;
            }

            queue.Enqueue(result);
        }

        return this;
    }

    public FakeProcessRunner Succeeds(string fileName, string arguments, string stdOut)
        => When(fileName, arguments, new ProcessResult(0, stdOut, string.Empty, false));

    public FakeProcessRunner Fails(string fileName, string arguments, int exitCode, string stdOut = "")
        => When(fileName, arguments, new ProcessResult(exitCode, stdOut, string.Empty, false));

    public FakeProcessRunner FailsWithStdErr(string fileName, string arguments, int exitCode, string stdErr, string stdOut = "")
        => When(fileName, arguments, new ProcessResult(exitCode, stdOut, stdErr, false));

    public FakeProcessRunner FailsToStart(string fileName, string arguments, string error = "binary not found")
        => When(fileName, arguments, ProcessResult.FailedToStart(error));

    public FakeProcessRunner TimesOut(string fileName, string arguments, string stdOut)
        => When(fileName, arguments, new ProcessResult(-1, stdOut, string.Empty, true));

    public IReadOnlyList<(string FileName, string Arguments, int TimeoutMs, string? StandardInput)> GetInvocations()
    {
        lock (_lock)
        {
            return Invocations.ToList();
        }
    }

    public int InvocationCount(string fileName, string arguments)
    {
        lock (_lock)
        {
            return Invocations.Count(i => i.FileName == fileName && i.Arguments == arguments);
        }
    }

    /// <summary>
    /// The last recorded stdin payload for (fileName, arguments) (null when the invocation used
    /// the 3-arg overload or no invocation was recorded).
    /// </summary>
    public string? LastStandardInput(string fileName, string arguments)
    {
        lock (_lock)
        {
            return Invocations.LastOrDefault(i => i.FileName == fileName && i.Arguments == arguments).StandardInput;
        }
    }

    /// <inheritdoc />
    public ProcessResult Run(string fileName, string arguments, int timeoutMs)
        => Run(fileName, arguments, timeoutMs, standardInput: null);

    /// <inheritdoc />
    public ProcessResult Run(string fileName, string arguments, int timeoutMs, string? standardInput)
    {
        lock (_lock)
        {
            Invocations.Add((fileName, arguments, timeoutMs, standardInput));
            if (_scripts.TryGetValue((fileName, arguments), out var queue) && queue.Count > 0)
            {
                return queue.Dequeue();
            }

            return ProcessResult.FailedToStart($"No canned result registered for {fileName} {arguments}");
        }
    }
}
