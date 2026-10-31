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
        Pinning_FromEachRouter(config),
        LocationScopeRemote_Pin(),
        LocationScopeCloud_NoCloudTarget()
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

    // C1 — location-scope pins (task #40): X-OllamaRouter-Target: remote is served only by a
    // daemon whose configured URL classifies as Remote (LAN), never by the loopback one;
    // X-OllamaRouter-Target: local is served only by a Local-classified (loopback) daemon.
    // The fleet's per-target location comes from the router's own fleet API (the same
    // TargetLocationClassifier the scope pin uses). Skipped with a printed notice when the
    // fleet has no target of the requested class (harness-dependent).
    private static Scenario LocationScopeRemote_Pin() =>
        new("Mesh: location-scope pins (remote → LAN-only, local → loopback-only)", async config =>
        {
            var fleet = await GetFleetAsync(new HttpClient { Timeout = RequestTimeout }, config.RouterUrl);
            if (fleet is null)
            {
                return "Could not fetch GET /api/router/fleet from router A (required for the location-scope scenario).";
            }

            var byName = fleet.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
            var remoteTargets = fleet.Where(t => t.Location == "remote").Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var localTargets = fleet.Where(t => t.Location == "local").Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // A target literally named "remote"/"local" would exact-pin (name precedence) —
            // the scope path is only observable when NO such target exists.
            if (byName.ContainsKey("remote") || byName.ContainsKey("local"))
            {
                Console.WriteLine("[skip] location-scope scenario: a target is literally named 'remote' or 'local' (exact-name precedence masks the scope path).");
                return null;
            }

            using var http = new HttpClient { Timeout = RequestTimeout };

            if (remoteTargets.Count == 0)
            {
                Console.WriteLine("[skip] location-scope 'remote' half: fleet has no Remote-classified target.");
            }
            else
            {
                var (_, status, served, _) = await PostChatAsync(http, config.RouterUrl, config.ModelName, pin: "remote");
                if (status != System.Net.HttpStatusCode.OK)
                {
                    return $"Scope pin 'remote' did not complete (status {status}).";
                }

                if (served is null || !remoteTargets.Contains(served))
                {
                    return $"Scope pin 'remote' was served by '{served}' which is not Remote-classified (LAN targets: {string.Join(",", remoteTargets)}).";
                }
            }

            if (localTargets.Count == 0)
            {
                Console.WriteLine("[skip] location-scope 'local' half: fleet has no Local-classified (loopback) target.");
            }
            else
            {
                var (_, status, served, _) = await PostChatAsync(http, config.RouterUrl, config.ModelName, pin: "local");
                if (status != System.Net.HttpStatusCode.OK)
                {
                    return $"Scope pin 'local' did not complete (status {status}).";
                }

                if (served is null || !localTargets.Contains(served))
                {
                    return $"Scope pin 'local' was served by '{served}' which is not Local-classified (loopback targets: {string.Join(",", localTargets)}).";
                }
            }

            return null;
        });

    // C2 (harness-dependent) — a {cloud}-classified fleet with no Cloud Overflow cannot leak a
    // LAN-only fleet into a cloud scope: the scope pin 'cloud' (which exact-pins the Cloud
    // pseudo-target) must NOT fall through to a LAN/loopback fleet target. When the fleet has
    // no Cloud-classified target AND Cloud Overflow is unavailable, the request must fail
    // (503-ish), never succeed on a non-Cloud target. Skipped with a notice when a
    // Cloud-classified target exists (the relay path is then legitimately available).
    private static Scenario LocationScopeCloud_NoCloudTarget() =>
        new("Mesh: cloud scope never falls through to a LAN-only fleet", async config =>
        {
            var http = new HttpClient { Timeout = RequestTimeout };
            var fleet = await GetFleetAsync(http, config.RouterUrl);
            if (fleet is null)
            {
                return "Could not fetch GET /api/router/fleet from router A (required for the cloud-scope scenario).";
            }

            var cloudTargets = fleet.Where(t => t.Location == "cloud").ToList();
            if (cloudTargets.Count > 0)
            {
                Console.WriteLine("[skip] cloud-scope scenario: fleet has Cloud-classified target(s); the relay path is available.");
                return null;
            }

            // No cloud-classified fleet target: a 'cloud' pin cannot be served by any fleet
            // daemon. Whatever the outcome (503 or Cloud-relay 200), it must NOT be a LAN or
            // loopback target masquerading as the served target.
            var (_, status, served, _) = await PostChatAsync(http, config.RouterUrl, config.ModelName, pin: "cloud");
            if (status == System.Net.HttpStatusCode.OK)
            {
                if (served is null || !served.Equals("Cloud", StringComparison.OrdinalIgnoreCase))
                {
                    return $"Cloud scope was served by non-Cloud target '{served}' (no cloud-classified target exists — LAN fall-through leak).";
                }
            }

            // A 503 (no in-scope/relay candidate) is the correct no-leak outcome here.
            return null;
        });

    private static async Task<IReadOnlyList<(string Name, string Location)>?> GetFleetAsync(HttpClient http, string baseUrl)
    {
        using var response = await http.GetAsync($"{baseUrl.TrimEnd('/')}/api/router/fleet");
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync();
        var root = JsonNode.Parse(body);
        if (root?["targets"] is not JsonArray targets)
        {
            return null;
        }

        var fleet = new List<(string, string)>();
        foreach (var entry in targets)
        {
            var name = entry?["name"]?.GetValue<string>();
            var location = entry?["location"]?.GetValue<string>();
            if (name is not null && location is not null)
            {
                fleet.Add((name, location.ToLowerInvariant()));
            }
        }

        return fleet;
    }

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
