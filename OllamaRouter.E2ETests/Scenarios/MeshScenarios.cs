using System.Text;
using System.Text.Json.Nodes;

namespace OllamaRouter.E2ETests.Scenarios;

/// <summary>
/// Opt-in live dual-router mesh scenarios (plan §5): validate a REAL 3-node fleet — two
/// physical OllamaRouter instances on :11434, each routing to both daemons on :11435.
/// Registered by Program.cs only when E2EConfig.MeshEnabled (OLLAMAROUTER_E2E_MESH=1) and a
/// second router URL (OLLAMAROUTER_E2E_MESH_URL_B) are supplied; the default single-router
/// run is unaffected.
/// </summary>
internal static class MeshScenarios
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    public static IReadOnlyList<Scenario> All(E2EConfig config) =>
    [
        CatalogUnion_BothRouters(),
        Chat_FromBothRouters(),
        Pinning_FromEachRouter(config)
    ];

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() ?? string.Empty : string.Empty;

    private static Scenario CatalogUnion_BothRouters() =>
        new("Mesh: /api/tags union identical on both routers", async config =>
        {
            using var httpA = new HttpClient { Timeout = RequestTimeout };
            using var httpB = new HttpClient { Timeout = RequestTimeout };

            var (tagsA, statusA) = await GetTagsAsync(httpA, config.RouterUrl);
            var (tagsB, statusB) = await GetTagsAsync(httpB, config.RouterUrlB);

            if (statusA != System.Net.HttpStatusCode.OK || statusB != System.Net.HttpStatusCode.OK)
            {
                return $"Expected 200 from both routers' /api/tags (A={statusA}, B={statusB}).";
            }

            if (!string.Equals(tagsA, tagsB, StringComparison.Ordinal))
            {
                return "The /api/tags model union differs between the two routers: " +
                    $"A=[{string.Join(",", ModelNames(tagsA))}] B=[{string.Join(",", ModelNames(tagsB))}]";
            }

            return null;
        });

    private static Scenario Chat_FromBothRouters() =>
        new("Mesh: /api/chat completes from both routers (bounded time, telemetry target present)", async config =>
        {
            using var httpA = new HttpClient { Timeout = RequestTimeout };
            using var httpB = new HttpClient { Timeout = RequestTimeout };

            var (bodyA, statusA, targetA, elapsedA) = await PostChatAsync(httpA, config.RouterUrl, config.ModelName);
            var (bodyB, statusB, targetB, elapsedB) = await PostChatAsync(httpB, config.RouterUrlB, config.ModelName);

            if (statusA != System.Net.HttpStatusCode.OK || statusB != System.Net.HttpStatusCode.OK)
            {
                return $"Expected 200 chat from both routers (A={statusA}, B={statusB}).";
            }

            if (!bodyA.Contains("\"done\":true") || !bodyB.Contains("\"done\":true"))
            {
                return $"Expected completed NDJSON (done:true) from both routers (A={bodyA.Length} chars, B={bodyB.Length} chars).";
            }

            if (string.IsNullOrEmpty(targetA) || string.IsNullOrEmpty(targetB))
            {
                return "Expected X-OllamaRouter-Target telemetry on both routers' chat responses (no hop/loop: each request completed).";
            }

            if (elapsedA > 30 || elapsedB > 30)
            {
                return "A chat request exceeded the 30 s loop bound (A=" + elapsedA + " s, B=" + elapsedB + " s).";
            }

            return null;
        });

    private static Scenario Pinning_FromEachRouter(E2EConfig config) =>
        new("Mesh: X-OllamaRouter-Target pin honored from each router", async cfg =>
        {
            if (cfg.ExpectedTargets.Length == 0)
            {
                return "OLLAMAROUTER_E2E_MESH_TARGETS must list the fleet target names for the pin scenario.";
            }

            using var httpA = new HttpClient { Timeout = RequestTimeout };
            using var httpB = new HttpClient { Timeout = RequestTimeout };

            foreach (var pin in cfg.ExpectedTargets)
            {
                var (bodyA, statusA, servedA, _) = await PostChatAsync(httpA, cfg.RouterUrl, cfg.ModelName, pin: pin);
                var (bodyB, statusB, servedB, _) = await PostChatAsync(httpB, cfg.RouterUrlB, cfg.ModelName, pin: pin);

                if (statusA != System.Net.HttpStatusCode.OK || statusB != System.Net.HttpStatusCode.OK)
                {
                    return $"Pinned chat to '{pin}' did not complete on both routers (A={statusA}, B={statusB}).";
                }

                if (servedA is null || !servedA.Equals(pin, StringComparison.OrdinalIgnoreCase) ||
                    servedB is null || !servedB.Equals(pin, StringComparison.OrdinalIgnoreCase))
                {
                    return $"Pinned chat to '{pin}' was not served by the pinned target (A served '{servedA}', B served '{servedB}').";
                }
            }

            return null;
        });

    private static async Task<(string Body, System.Net.HttpStatusCode Status, string? Target, double ElapsedSeconds)>
        PostChatAsync(HttpClient http, string baseUrl, string model, string? pin = null)
    {
        var payload = new JsonObject
        {
            ["model"] = model,
            ["stream"] = true,
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "Reply with exactly one word: OK" } }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/chat")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        if (pin is not null)
        {
            request.Headers.TryAddWithoutValidation("X-OllamaRouter-Target", pin);
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead);
        stopwatch.Stop();

        var body = await response.Content.ReadAsStringAsync();
        string? target = null;
        if (response.Headers.TryGetValues("X-OllamaRouter-Target", out var values))
        {
            target = values.FirstOrDefault();
        }

        return (body, response.StatusCode, target, stopwatch.Elapsed.TotalSeconds);
    }

    private static async Task<(string Body, System.Net.HttpStatusCode Status)> GetTagsAsync(HttpClient http, string baseUrl)
    {
        using var response = await http.GetAsync($"{baseUrl.TrimEnd('/')}/api/tags");
        return (await response.Content.ReadAsStringAsync(), response.StatusCode);
    }

    private static string[] ModelNames(string tagsBody)
    {
        var root = JsonNode.Parse(tagsBody);
        if (root is null)
        {
            return [];
        }

        var names = new List<string>();
        if (root["models"] is JsonArray models)
        {
            foreach (var entry in models)
            {
                var name = entry?["name"]?.GetValue<string>();
                if (name is not null)
                {
                    names.Add(name);
                }
            }
        }

        return names.OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }
}
