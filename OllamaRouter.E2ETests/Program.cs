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
    OpenAiCompatibleScenarios.ChatCompletion()
];

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
