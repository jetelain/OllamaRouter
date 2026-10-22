using OllamaRouter.Services;

namespace OllamaRouter.Tests.Services;

/// <summary>
/// Test double for <see cref="IProcessRunner"/> that returns canned <see cref="ProcessResult"/>
/// values keyed by (fileName, arguments). Supports per-key scripts (multiple results returned in
/// order for consecutive calls with the same key) and records every invocation.
/// </summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Dictionary<(string FileName, string Arguments), Queue<ProcessResult>> _scripts = new();
    private readonly object _lock = new();

    public List<(string FileName, string Arguments, int TimeoutMs)> Invocations { get; } = new();

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

    public FakeProcessRunner FailsToStart(string fileName, string arguments, string error = "binary not found")
        => When(fileName, arguments, ProcessResult.FailedToStart(error));

    public FakeProcessRunner TimesOut(string fileName, string arguments, string stdOut)
        => When(fileName, arguments, new ProcessResult(-1, stdOut, string.Empty, true));

    public IReadOnlyList<(string FileName, string Arguments, int TimeoutMs)> GetInvocations()
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

    public ProcessResult Run(string fileName, string arguments, int timeoutMs)
    {
        lock (_lock)
        {
            Invocations.Add((fileName, arguments, timeoutMs));
            if (_scripts.TryGetValue((fileName, arguments), out var queue) && queue.Count > 0)
            {
                return queue.Dequeue();
            }

            return ProcessResult.FailedToStart($"No canned result registered for {fileName} {arguments}");
        }
    }
}
