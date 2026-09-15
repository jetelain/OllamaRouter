using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OllamaRouter.E2ETests.Scenarios;

/// <summary>
/// Scenarios exercising the management, monitoring, and error-handling endpoints of OllamaRouter:
/// - GET  /monitor
/// - GET  /monitor/api
/// - POST /targets
/// - POST /monitor/reclaim-vram
/// - 503 error handling when no routing targets are available
/// </summary>
internal static class MonitorScenarios
{
    public static Scenario GetMonitorHtml() => new("Activity monitor /monitor returns HTML page", async config =>
    {
        using var http = new HttpClient { BaseAddress = new Uri(config.RouterUrl) };

        var response = await http.GetAsync("/monitor");
        if (!response.IsSuccessStatusCode)
        {
            return $"Expected 200 OK from /monitor, got {(int)response.StatusCode} {response.ReasonPhrase}.";
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType != "text/html")
        {
            return $"Expected Content-Type 'text/html', got '{contentType}'.";
        }

        var html = await response.Content.ReadAsStringAsync();
        if (!html.Contains("OllamaRouter - Activity Monitor"))
        {
            return "Expected HTML to contain 'OllamaRouter - Activity Monitor'.";
        }

        if (!html.Contains("reclaimLocalVram"))
        {
            return "Expected HTML to contain reclaim script function 'reclaimLocalVram'.";
        }

        return null;
    });

    public static Scenario GetMonitorApi() => new("Activity monitor /monitor/api returns valid state JSON", async config =>
    {
        using var http = new HttpClient { BaseAddress = new Uri(config.RouterUrl) };

        var response = await http.GetAsync("/monitor/api");
        if (!response.IsSuccessStatusCode)
        {
            return $"Expected 200 OK from /monitor/api, got {(int)response.StatusCode} {response.ReasonPhrase}.";
        }

        var json = await response.Content.ReadAsStringAsync();
        var node = JsonNode.Parse(json);
        if (node is null)
        {
            return "Failed to parse JSON response from /monitor/api.";
        }

        string[] requiredProperties = ["cloudEnabled", "targets", "online", "busy", "statistics", "inProgress", "requests"];
        foreach (var prop in requiredProperties)
        {
            if (node[prop] is null)
            {
                return $"Missing required property '{prop}' in /monitor/api response.";
            }
        }

        var targets = node["targets"];
        if (targets?["local"] is null || targets["remote"] is null || targets["cloud"] is null)
        {
            return "Missing target status flags (local, remote, cloud) in /monitor/api response.";
        }

        var stats = node["statistics"];
        if (stats?["today"] is null || stats["last7Days"] is null)
        {
            return "Missing statistics period buckets (today, last7Days) in /monitor/api response.";
        }

        return null;
    });

    public static Scenario UpdateTargets() => new("Target availability /targets updates and restores state", async config =>
    {
        using var http = new HttpClient { BaseAddress = new Uri(config.RouterUrl) };

        // 1. Snapshot original state so we can reliably restore it.
        var (origLocal, origRemote, origCloud) = await GetCurrentTargetsAsync(http);

        try
        {
            // 2. Update to a new test state (toggle cloud).
            var newCloud = !origCloud;
            var payload = JsonSerializer.Serialize(new { local = origLocal, remote = origRemote, cloud = newCloud });
            using var updateContent = new StringContent(payload, Encoding.UTF8, "application/json");

            var updateResponse = await http.PostAsync("/targets", updateContent);
            if (!updateResponse.IsSuccessStatusCode)
            {
                return $"Expected 200 OK from POST /targets, got {(int)updateResponse.StatusCode} {updateResponse.ReasonPhrase}.";
            }

            var updateJson = JsonNode.Parse(await updateResponse.Content.ReadAsStringAsync());
            if (updateJson?["cloud"]?.GetValue<bool>() != newCloud)
            {
                return $"POST /targets response did not reflect the requested cloud state '{newCloud}'.";
            }

            // 3. Verify via /monitor/api that the change took effect.
            var (currentLocal, currentRemote, currentCloud) = await GetCurrentTargetsAsync(http);
            if (currentCloud != newCloud)
            {
                return $"/monitor/api state after update did not reflect cloud='{newCloud}'.";
            }

            return null;
        }
        finally
        {
            // 4. Restore original state.
            await SetTargetsAsync(http, origLocal, origRemote, origCloud);
        }
    });

    public static Scenario ReclaimVram() => new("VRAM reclaim /monitor/reclaim-vram disables local and unloads models", async config =>
    {
        using var http = new HttpClient { BaseAddress = new Uri(config.RouterUrl) };

        // 1. Snapshot original state.
        var (origLocal, origRemote, origCloud) = await GetCurrentTargetsAsync(http);

        try
        {
            // 2. Invoke reclaim endpoint.
            var response = await http.PostAsync("/monitor/reclaim-vram", null);
            if (!response.IsSuccessStatusCode)
            {
                return $"Expected 200 OK from POST /monitor/reclaim-vram, got {(int)response.StatusCode} {response.ReasonPhrase}.";
            }

            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            if (json is null)
            {
                return "Failed to parse JSON response from /monitor/reclaim-vram.";
            }

            if (json["localDisabled"]?.GetValue<bool>() != true)
            {
                return "Expected 'localDisabled' to be true in /monitor/reclaim-vram response.";
            }

            if (json["unloadedModels"] is not JsonArray)
            {
                return "Expected 'unloadedModels' array in /monitor/reclaim-vram response.";
            }

            // 3. Verify that local target is now disabled.
            var (currentLocal, _, _) = await GetCurrentTargetsAsync(http);
            if (currentLocal)
            {
                return "Expected local target to be disabled after /monitor/reclaim-vram, but it is still enabled.";
            }

            return null;
        }
        finally
        {
            // 4. Restore original state.
            await SetTargetsAsync(http, origLocal, origRemote, origCloud);
        }
    });

    public static Scenario ServiceUnavailableWhenNoTargets() => new("Middleware returns 503 JSON error when all targets disabled", async config =>
    {
        using var http = new HttpClient { BaseAddress = new Uri(config.RouterUrl) };

        // 1. Snapshot original state.
        var (origLocal, origRemote, origCloud) = await GetCurrentTargetsAsync(http);

        try
        {
            // 2. Disable all targets.
            await SetTargetsAsync(http, local: false, remote: false, cloud: false);

            // 3. Send a chat completion request that requires routing inspection.
            var chatPayload = JsonSerializer.Serialize(new
            {
                model = config.ModelName,
                messages = new[] { new { role = "user", content = "ping" } },
                stream = false
            });
            using var chatContent = new StringContent(chatPayload, Encoding.UTF8, "application/json");

            var response = await http.PostAsync("/api/chat", chatContent);
            if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
            {
                return $"Expected 503 ServiceUnavailable when all targets are disabled, got {(int)response.StatusCode} {response.ReasonPhrase}.";
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType != "application/json")
            {
                return $"Expected Content-Type 'application/json', got '{contentType}'.";
            }

            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            var error = json?["error"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(error))
            {
                return "Expected non-empty 'error' field in 503 JSON response body.";
            }

            return null;
        }
        finally
        {
            // 4. Restore original state.
            await SetTargetsAsync(http, origLocal, origRemote, origCloud);
        }
    });

    private static async Task<(bool Local, bool Remote, bool Cloud)> GetCurrentTargetsAsync(HttpClient http)
    {
        var response = await http.GetAsync("/monitor/api");
        response.EnsureSuccessStatusCode();

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var targets = json?["targets"];

        return (
            targets?["local"]?.GetValue<bool>() ?? true,
            targets?["remote"]?.GetValue<bool>() ?? true,
            targets?["cloud"]?.GetValue<bool>() ?? false
        );
    }

    private static async Task SetTargetsAsync(HttpClient http, bool local, bool remote, bool cloud)
    {
        var payload = JsonSerializer.Serialize(new { local, remote, cloud });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await http.PostAsync("/targets", content);
        response.EnsureSuccessStatusCode();
    }
}

