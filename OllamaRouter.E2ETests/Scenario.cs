namespace OllamaRouter.E2ETests;

/// <summary>
/// Result of a single end-to-end scenario run.
/// </summary>
internal sealed record ScenarioResult(string Name, bool Success, string Message);

/// <summary>
/// A named end-to-end scenario that exercises a running OllamaRouter instance.
/// </summary>
internal sealed class Scenario(string name, Func<E2EConfig, Task<string?>> run)
{
    public string Name { get; } = name;

    /// <summary>
    /// Runs the scenario. The delegate should throw on unexpected failures, or return a
    /// non-null string describing why the scenario's expectations were not met.
    /// </summary>
    public async Task<ScenarioResult> RunAsync(E2EConfig config)
    {
        try
        {
            var failureReason = await run(config);
            return failureReason is null
                ? new ScenarioResult(Name, true, "OK")
                : new ScenarioResult(Name, false, failureReason);
        }
        catch (Exception ex)
        {
            return new ScenarioResult(Name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
