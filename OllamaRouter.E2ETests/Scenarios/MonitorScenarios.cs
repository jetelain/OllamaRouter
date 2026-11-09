using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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

        string[] requiredProperties = ["cloudEnabled", "fleet", "statistics", "inProgress", "requests"];
        foreach (var prop in requiredProperties)
        {
            if (node[prop] is null)
            {
                return $"Missing required property '{prop}' in /monitor/api response.";
            }
        }

        var fleet = node["fleet"];
        // The fleet is an ordered array (configured targets first, Cloud last); look up the
        // legacy fleet entries case-insensitively.
        if (FindFleetEntry(fleet, "local") is null || FindFleetEntry(fleet, "remote") is null || FindFleetEntry(fleet, "cloud") is null)
        {
            return "Missing fleet entries (local, remote, cloud) in /monitor/api response.";
        }

        // Cloud must always be the last fleet entry.
        var fleetArray = fleet as JsonArray;
        if (fleetArray is null || fleetArray.Count == 0 ||
            !string.Equals(fleetArray[^1]?["name"]?.GetValue<string>(), "Cloud", StringComparison.OrdinalIgnoreCase))
        {
            return "Expected the 'Cloud' pseudo-target to be the last entry of the fleet array.";
        }

        // The fleet context column now carries the daemon-REPORTED context size (task #73):
        // either "-" (no model loaded / daemon does not report it / Cloud row) or a compact
        // token value ("512", "4k", "33k", "1.3M"). Holds whether or not the live daemon
        // emits context_length; the old lease text (e.g. "262.1k (expanded)") must be gone.
        var contextShape = new Regex("^(\\d+)(\\.\\d+)?[kM]?$", RegexOptions.Compiled);
        foreach (var entry in fleetArray)
        {
            var value = entry?["contextAllocation"]?.GetValue<string>();
            if (value != "-" && !contextShape.IsMatch(value))
            {
                return $"Fleet entry '{entry?["name"]?.GetValue<string>()}' has unexpected contextAllocation shape '{value}' (expected '-' or a compact token value like '512', '4k', '1.3M').";
            }
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

    /// <summary>
    /// Task #83: cached prompt-token capture + pricing + cost math, best-effort end-to-end.
    /// Repeats a long IDENTICAL prefix twice over native <c>/api/chat</c> (streaming) to attempt a
    /// prompt-cache hit, then reads <c>GET /monitor/api</c>. When the live daemon actually reported
    /// a cache hit (target stat <c>cachedInputTokens > 0</c>), the count is a subset of
    /// <c>inputTokens</c>. When no hit was observed (non-flaky: KV-cache dependent) a notice is
    /// printed and the read-only portion passes. When <c>OLLAMAROUTER_E2E_API_KEY</c> is set the
    /// scenario additionally <c>PUT</c>s a cached prompt-token rate (snapshot/restore), and verifies
    /// the <c>cost</c> status applies the cached split to the observed per-target statistics.
    /// </summary>
    public static Scenario CachedTokenPricing() => new("Monitor cost math applies cached prompt-token pricing (task #83)", async config =>
    {
        using var http = new HttpClient { BaseAddress = new Uri(config.RouterUrl) };

        // 1. Attempt a prompt-cache hit: repeat the SAME long prefix twice over native /api/chat.
        var prefix = new string('x', 400) + " Explain cached prompt tokens in Ollama. ";
        for (var i = 0; i < 2; i++)
        {
            var warmPayload = JsonSerializer.Serialize(new
            {
                model = config.ModelName,
                stream = true,
                messages = new[] { new { role = "user", content = prefix } }
            });
            using var warmContent = new StringContent(warmPayload, Encoding.UTF8, "application/json");
            var warm = await http.PostAsync("/api/chat", warmContent);
            if (!warm.IsSuccessStatusCode)
            {
                return $"Expected 200 from POST /api/chat (warm-up {i + 1}), got {(int)warm.StatusCode} {warm.ReasonPhrase}.";
            }

            await warm.Content.ReadAsStringAsync();
        }

        // 2. GET /monitor/api: read the target statistics.
        var monitor = await GetMonitorJsonAsync(http);
        if (monitor is null)
        {
            return "Failed to read /monitor/api after warm-up chats.";
        }

        // 3. Consistency: whenever the daemon reported a cache hit, cachedInputTokens is a subset
        //    of inputTokens (never exceeds it). Non-flaky: no hit observed ⇒ notice + pass.
        var hitObserved = false;
        foreach (var (name, stat) in PeriodEntries(monitor, "last7Days"))
        {
            var cached = stat?["cachedInputTokens"]?.GetValue<int>() ?? 0;
            var input = stat?["inputTokens"]?.GetValue<int>() ?? 0;
            if (cached > input)
            {
                return $"Target '{name}' reports cachedInputTokens ({cached}) > inputTokens ({input}).";
            }

            if (cached > 0)
            {
                hitObserved = true;
            }
        }

        Console.WriteLine(hitObserved
            ? "  [cache] live daemon reported a prompt-cache hit (cachedInputTokens > 0)."
            : "  [cache] no prompt-cache hit observed this run (KV-cache dependent); skipping hit assertion.");

        // 4. When an API key is supplied: PUT a cached prompt-token rate (snapshot/restore) and
        //    verify the cost status applies the cached split to the observed statistics.
        if (config.ApiKey.Length == 0)
        {
            return null; // read-only portion satisfied; config manipulation is opt-in.
        }

        var (snapshotOk, snapshot) = await GetConfigAsync(http, config.ApiKey);
        if (!snapshotOk || snapshot is null)
        {
            return "Could not snapshot /api/router/config (API key) before the cached-pricing PUT.";
        }

        var pricingModified = false;
        try
        {
            // Task #84: the savings are ACTUAL (cloud value − electricity). Read the electricity
            // knobs from the config snapshot (null ⇒ 0.20 / 150 W) and ECHO them into the PUT
            // body (full-section-replace would otherwise clear them) so the expected formula and
            // the live options can never drift.
            var snapshotPricing = snapshot?["pricing"] as JsonObject;
            var snapshotElectricityPrice = snapshotPricing?["electricityPricePerKwh"];
            var snapshotDefaultWatts = snapshotPricing?["defaultNodePowerWatts"];

            var pricingSection = new JsonObject
            {
                ["promptPricePerMillion"] = 0.20,
                ["completionPricePerMillion"] = 0.80,
                ["cachedPromptPricePerMillion"] = 0.05,
                ["currency"] = "$"
            };
            if (snapshotElectricityPrice is not null)
            {
                pricingSection["electricityPricePerKwh"] = snapshotElectricityPrice;
            }
            if (snapshotDefaultWatts is not null)
            {
                pricingSection["defaultNodePowerWatts"] = snapshotDefaultWatts;
            }
            var putBody = new JsonObject
            {
                ["pricing"] = pricingSection
            };
            var (putOk, putDoc, putStatus) = await ConfigRequestAsync(http, "PUT", "/api/router/config", putBody, config.ApiKey);
            if (!putOk)
            {
                return $"PUT /api/router/config (cached pricing) failed with HTTP {(int)putStatus}.";
            }

            // From here on the live config differs from the snapshot: restore it in the
            // finally block even when a later assertion fails (snapshot/restore contract).
            pricingModified = true;

            if (putDoc?["config"]?["pricing"]?["cachedPromptPricePerMillion"]?.GetValue<double>() != 0.05)
            {
                return "PUT /api/router/config did not echo cachedPromptPricePerMillion = 0.05.";
            }

            // Re-read the monitor: the cost object must now be present (pricing configured) and
            // its per-target ACTUAL savings (task #84) must match the cached-split token value
            // MINUS the target's electricity cost (observed elapsedMs × resolved wattage ×
            // electricity price, 4-decimal rounding contract) on the observed statistics.
            var monitor2 = await GetMonitorJsonAsync(http);
            if (monitor2?["cost"] is null)
            {
                return "Expected a 'cost' object in /monitor/api after configuring pricing.";
            }

            var electricityPrice = snapshotElectricityPrice?.GetValue<double>() ?? 0.20;
            var defaultWatts = snapshotDefaultWatts?.GetValue<double>() ?? 150.0;
            var snapshotTargets = (snapshot?["targets"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];

            foreach (var (name, stat) in PeriodEntries(monitor2, "last7Days"))
            {
                if (string.Equals(name, "total", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "Cloud", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var input = stat?["inputTokens"]?.GetValue<int>() ?? 0;
                var cached = stat?["cachedInputTokens"]?.GetValue<int>() ?? 0;
                var output = stat?["outputTokens"]?.GetValue<int>() ?? 0;
                var tokenValue = Math.Round((input - cached) / 1_000_000.0 * 0.20 +
                                            cached / 1_000_000.0 * 0.05 +
                                            output / 1_000_000.0 * 0.80, 4);

                // Wattage resolution chain (target override → pricing default → 150 W) from the
                // same config snapshot the live options resolve against.
                var snapshotTarget = snapshotTargets.FirstOrDefault(t =>
                    string.Equals(t?["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));
                var watts = snapshotTarget?["powerWatts"] is { } targetWatts && targetWatts.GetValue<double>() >= 0
                    ? targetWatts.GetValue<double>()
                    : defaultWatts;

                var elapsedMs = stat?["elapsedMs"]?.GetValue<double>() ?? 0.0;
                var electricity = Math.Round(elapsedMs / 3_600_000.0 * watts / 1000.0 * electricityPrice, 4);
                var expected = Math.Round(tokenValue - electricity, 4);
                var actual = monitor2["cost"]!["perTargetSavings"]![name]?.GetValue<double>();
                if (actual is null || Math.Abs(actual.Value - expected) > 1e-9)
                {
                    return $"cost.perTargetSavings['{name}'] = {actual} does not match the actual-savings formula tokenValue({tokenValue}) − electricity({electricity}) = {expected} (input {input}, cached {cached}, output {output}, elapsedMs {elapsedMs}, watts {watts}, electricityPrice {electricityPrice}).";
                }

                var actualElectricity = monitor2["cost"]!["perTargetElectricityCost"]?[name]?.GetValue<double>();
                if (actualElectricity is null || Math.Abs(actualElectricity.Value - electricity) > 1e-9)
                {
                    return $"cost.perTargetElectricityCost['{name}'] = {actualElectricity} does not match the observed-elapsed electricity term ({electricity}).";
                }
            }

            return null;
        }
        finally
        {
            if (pricingModified)
            {
                // Restore the original pricing section (or clear it when it was unset).
                var originalPricing = snapshot?["pricing"];
                var restoreBody = new JsonObject
                {
                    ["pricing"] = originalPricing ?? (JsonNode)new JsonObject()
                };
                _ = await ConfigRequestAsync(http, "PUT", "/api/router/config", restoreBody, config.ApiKey);
            }
        }
    });

    private static async Task<JsonNode?> GetMonitorJsonAsync(HttpClient http)
    {
        var response = await http.GetAsync("/monitor/api");
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return JsonNode.Parse(await response.Content.ReadAsStringAsync());
    }

    private static IEnumerable<(string Name, JsonNode? Stat)> PeriodEntries(JsonNode monitor, string period)
    {
        var bucket = monitor["statistics"]![period] as JsonObject;
        if (bucket is null)
        {
            yield break;
        }

        foreach (var pair in bucket)
        {
            yield return (pair.Key, pair.Value);
        }
    }

    private static (bool Ok, JsonNode? Body) GetConfigResult(HttpStatusCode status, JsonNode? body) =>
        (status == HttpStatusCode.OK, body);

    private static async Task<(bool Ok, JsonNode? Body)> GetConfigAsync(HttpClient http, string apiKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/router/config");
        request.Headers.Add("X-Api-Key", apiKey);
        using var response = await http.SendAsync(request);
        return GetConfigResult(response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync()));
    }

    private static async Task<(bool Ok, JsonNode? Body, HttpStatusCode Status)> ConfigRequestAsync(
        HttpClient http, string method, string path, JsonNode? body, string apiKey)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("X-Api-Key", apiKey);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request);
        var parsed = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        return (response.IsSuccessStatusCode, parsed, response.StatusCode);
    }

    private static async Task<(bool Local, bool Remote, bool Cloud)> GetCurrentTargetsAsync(HttpClient http)
    {
        var response = await http.GetAsync("/monitor/api");
        response.EnsureSuccessStatusCode();

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var fleet = json?["fleet"];

        // Case-insensitive lookup of the fleet entries: the legacy fleet exposes
        // "Local"/"Remote"/"Cloud".
        return (
            FindFleetEnabled(fleet, "local") ?? true,
            FindFleetEnabled(fleet, "remote") ?? true,
            FindFleetEnabled(fleet, "cloud") ?? false
        );
    }

    private static JsonNode? FindFleetEntry(JsonNode? fleet, string name)
    {
        return FleetEntries(fleet)
            .FirstOrDefault(entry => string.Equals(entry?["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool? FindFleetEnabled(JsonNode? fleet, string name)
    {
        return FindFleetEntry(fleet, name)?["enabled"]?.GetValue<bool>();
    }

    private static IEnumerable<JsonNode?> FleetEntries(JsonNode? fleet) =>
        fleet is JsonArray array
            ? array
            : Enumerable.Empty<JsonNode?>();

    private static async Task SetTargetsAsync(HttpClient http, bool local, bool remote, bool cloud)
    {
        var payload = JsonSerializer.Serialize(new { local, remote, cloud });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await http.PostAsync("/targets", content);
        response.EnsureSuccessStatusCode();
    }
}
