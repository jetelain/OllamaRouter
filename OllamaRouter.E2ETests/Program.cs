using OllamaRouter.E2ETests;
using OllamaRouter.E2ETests.Scenarios;

var config = E2EConfig.FromEnvironment();

Console.WriteLine("OllamaRouter end-to-end tests");
Console.WriteLine($"  Router URL: {config.RouterUrl}");
Console.WriteLine($"  Model name: {config.ModelName}");
Console.WriteLine();

Scenario[] scenarios =
[
    OllamaNativeScenarios.ListTags(),
    OllamaNativeScenarios.ListRunningModels(),
    OllamaNativeScenarios.Chat(),
    OpenAiCompatibleScenarios.ListModels(),
    OpenAiCompatibleScenarios.ChatCompletion(),
    MonitorScenarios.GetMonitorHtml(),
    MonitorScenarios.GetMonitorApi(),
    MonitorScenarios.UpdateTargets(),
    MonitorScenarios.ReclaimVram(),
    MonitorScenarios.ServiceUnavailableWhenNoTargets()
];

// Opt-in live dual-router mesh group (plan §5): runs only when OLLAMAROUTER_E2E_MESH=1 AND a
// second router URL (OLLAMAROUTER_E2E_MESH_URL_B) are supplied. The default single-router run
// is byte-for-byte unaffected — when the flag is off, the group is skipped with a printed notice.
if (config.MeshEnabled && config.RouterUrlB.Length > 0)
{
    scenarios = scenarios.Concat(MeshScenarios.All(config)).ToArray();
}
else if (config.MeshEnabled)
{
    Console.WriteLine();
    Console.WriteLine("[SKIP] Live dual-router mesh scenarios skipped: set OLLAMAROUTER_E2E_MESH_URL_B to the second router's base URL.");
}

var failureCount = 0;

foreach (var scenario in scenarios)
{
    Console.Write($"- {scenario.Name} ... ");

    var result = await scenario.RunAsync(config);

    if (result.Success)
    {
        Console.WriteLine("PASS");
    }
    else
    {
        Console.WriteLine("FAIL");
        Console.WriteLine($"    {result.Message}");
        failureCount++;
    }
}

Console.WriteLine();
Console.WriteLine(failureCount == 0
    ? $"All {scenarios.Length} scenario(s) passed."
    : $"{failureCount} of {scenarios.Length} scenario(s) failed.");

return failureCount == 0 ? 0 : 1;
