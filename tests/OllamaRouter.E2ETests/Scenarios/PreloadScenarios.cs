using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OllamaRouter.E2ETests.Scenarios;

/// <summary>
/// End-to-end scenarios for the dedicated model pre-load API (task #44). The default-suite
/// <see cref="PreloadModel"/> runs on any live single-router deployment (a real daemon behind
/// the router): POST preload on the configured model must 200 with a selected target and an
/// accepted load, and the model must then appear resident in the router's merged /api/ps. The
/// <see cref="Mesh"/> group is opt-in (MeshEnabled) against the real fleet: a large
/// context-intent preload must land on the server-class daemon, and a BUSY server-class daemon
/// (a pinned background chat driving real in-flight) must be bypassed in favor of the idle
/// capable node.
/// </summary>
internal static class PreloadScenarios
{
    // The server-class VRAM threshold (MB), mirroring DynamicContextSizingEngine.ServerClassVramThresholdMB.
    private const int ServerClassVramThresholdMB = 32_768;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    public static Scenario PreloadModel() => new("Model preload POST /api/router/models/{model}/preload returns 200 and the model becomes resident", async config =>
    {
        using var http = new HttpClient { Timeout = RequestTimeout };

        var response = await http.PostAsync(
            $"{config.RouterUrl.TrimEnd('/')}/api/router/models/{config.ModelName}/preload",
            content: null);

        var body = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return $"Expected 200 OK from POST /api/router/models/{config.ModelName}/preload, got {(int)response.StatusCode}: {body}";
        }

        var json = JsonNode.Parse(body);
        if (json?["selectedTarget"]?.GetValue<string>() is not { Length: > 0 } selected)
        {
            return $"Expected a non-empty 'selectedTarget' in the preload response, got: {body}";
        }

        if (json?["loadAccepted"]?.GetValue<bool>() != true)
        {
            return $"Expected 'loadAccepted' to be true in the preload response, got: {body}";
        }

        // The bounded /api/ps residency confirmation must reflect the load.
        if (json?["residencyConfirmed"]?.GetValue<bool>() != true)
        {
            return $"Expected 'residencyConfirmed' to be true in the preload response, got: {body}";
        }

        // The router's merged /api/ps must now list the preloaded model.
        var psResponse = await http.GetAsync($"{config.RouterUrl.TrimEnd('/')}/api/ps");
        var psBody = await psResponse.Content.ReadAsStringAsync();
        if (psResponse.StatusCode != HttpStatusCode.OK)
        {
            return $"Expected 200 OK from GET /api/ps, got {(int)psResponse.StatusCode}.";
        }

        var models = JsonNode.Parse(psBody)?["models"] as JsonArray;
        var resident = models?.Any(m =>
            string.Equals(m?["name"]?.GetValue<string>(), config.ModelName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(m?["model"]?.GetValue<string>(), config.ModelName, StringComparison.OrdinalIgnoreCase)) == true;
        return resident
            ? null
            : $"Expected the preloaded model '{config.ModelName}' to appear resident in GET /api/ps, got: {psBody}";
    });

    /// <summary>
    /// Opt-in live-mesh preload scenarios (only when MeshEnabled): registered by Program.cs
    /// alongside the other mesh scenarios; every failure string is diagnostic (the live fleet
    /// is not guaranteed to satisfy the scenario preconditions).
    /// </summary>
    public static IReadOnlyList<Scenario> Mesh(E2EConfig config) =>
    [
        LargeContextIntent_LandsOnServerClassTarget(),
        BusyServerClassTarget_DefersToIdleCapableNode()
    ];

    private static Scenario LargeContextIntent_LandsOnServerClassTarget() =>
        new("Mesh: 260k contextIntentTokens preload lands on the server-class daemon", async config =>
        {
            using var http = new HttpClient { Timeout = RequestTimeout };

            var (fleet, fleetStatus) = await GetFleetAsync(http, config.RouterUrl);
            if (fleetStatus != HttpStatusCode.OK || fleet is null)
            {
                return $"Expected 200 OK from GET /api/router/fleet, got {(int)fleetStatus}.";
            }

            var targets = fleet["targets"] as JsonArray;
            if (targets is null || targets.Count == 0)
            {
                return "The fleet response carried no targets.";
            }

            var server = targets.AsEnumerable()
                .Select(t => (Name: t?["name"]?.GetValue<string>(), Vram: GetInt(t?["hardware"]?["maxVramMB"]), Enabled: GetBool(t?["enabled"]), Liveness: t?["liveness"]?.GetValue<string>()))
                .FirstOrDefault(t => t.Name is { Length: > 0 } && t.Enabled && t.Vram is >= ServerClassVramThresholdMB);
            if (server.Name is null)
            {
                return "No server-class (VRAM >= 32768 MB) enabled target found in the fleet — cannot verify the large-intent landing node.";
            }

            var response = await http.PostAsync(
                $"{config.RouterUrl.TrimEnd('/')}/api/router/models/{config.ModelName}/preload?contextIntentTokens=260000",
                content: null);
            var body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return $"Expected 200 OK for the 260k preload (fleet may not support that context), got {(int)response.StatusCode}: {body}";
            }

            var selected = JsonNode.Parse(body)?["selectedTarget"]?.GetValue<string>();
            return string.Equals(selected, server.Name, StringComparison.OrdinalIgnoreCase)
                ? null
                : $"Expected the 260k preload to select the server-class target '{server.Name}', got '{selected}'. Body: {body}";
        });

    private static Scenario BusyServerClassTarget_DefersToIdleCapableNode() =>
        new("Mesh: busy server-class target (in-flight > 0) is bypassed by a small-intent preload", async config =>
        {
            using var http = new HttpClient { Timeout = RequestTimeout };

            var (fleet, fleetStatus) = await GetFleetAsync(http, config.RouterUrl);
            if (fleetStatus != HttpStatusCode.OK || fleet is null)
            {
                return $"Expected 200 OK from GET /api/router/fleet, got {(int)fleetStatus}.";
            }

            var targets = fleet["targets"] as JsonArray;
            if (targets is null || targets.Count == 0)
            {
                return "The fleet response carried no targets.";
            }

            var server = targets.AsEnumerable()
                .Select(t => (Name: t?["name"]?.GetValue<string>(), Vram: t?["hardware"]?["maxVramMB"]?.GetValue<int?>(), Enabled: t?["enabled"]?.GetValue<bool?>() == true, Liveness: t?["liveness"]?.GetValue<string>()))
                .FirstOrDefault(t => t.Name is { Length: > 0 } && t.Enabled && t.Vram is >= ServerClassVramThresholdMB);
            var other = targets.AsEnumerable()
                .Select(t => (Name: t?["name"]?.GetValue<string>(), Enabled: GetBool(t?["enabled"]), Liveness: t?["liveness"]?.GetValue<string>()))
                .FirstOrDefault(t => t.Name is { Length: > 0 } && t.Enabled && t.Liveness == "Online"
                    && !string.Equals(t.Name, server.Name, StringComparison.OrdinalIgnoreCase));
            if (server.Name is null || other.Name is null)
            {
                return "The fleet needs one server-class (VRAM >= 32768 MB) and one other enabled target for the busy-bypass scenario.";
            }

            // Pin a long background chat to the server-class daemon (the PUBLIC pin header;
            // the internal X-Ollama-Target is sanitized on inbound by design) so the real
            // execution tracker keeps the server in-flight > 0 across the preload request.
            var chatPayload = JsonSerializer.Serialize(new
            {
                model = config.ModelName,
                stream = true,
                messages = new[] { new { role = "user", content = "Write a detailed 1500-word essay about the history of distributed systems." } },
                options = new { num_predict = 2048 }
            });
            using var chatContent = new StringContent(chatPayload, Encoding.UTF8);
            chatContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            var chatRequest = new HttpRequestMessage(HttpMethod.Post, $"{config.RouterUrl.TrimEnd('/')}/api/chat")
            {
                Content = chatContent
            };
            chatRequest.Headers.Add(OllamaRouterE2E_PinningHeader, server.Name);

            using var cts = new CancellationTokenSource();
            var background = http.SendAsync(chatRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            try
            {
                // Bounded wait: the fleet endpoint must observe the server in-flight > 0.
                var observedBusy = false;
                for (var i = 0; i < 60 && !observedBusy; i++)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500));
                    var (probe, probeStatus) = await GetFleetAsync(http, config.RouterUrl);
                    if (probeStatus != HttpStatusCode.OK || probe is null)
                    {
                        continue;
                    }

                    var entry = (probe["targets"] as JsonArray)?.AsEnumerable()
                        .FirstOrDefault(t => string.Equals(t?["name"]?.GetValue<string>(), server.Name, StringComparison.OrdinalIgnoreCase));
                    observedBusy = entry?["execution"]?["inFlightRequests"]?.GetValue<int>() is > 0;
                }

                if (!observedBusy)
                {
                    return $"The pinned background chat never drove the server-class target '{server.Name}' to inFlightRequests > 0 within the 30 s bound.";
                }

                var response = await http.PostAsync(
                    $"{config.RouterUrl.TrimEnd('/')}/api/router/models/{config.ModelName}/preload?contextIntentTokens=8192",
                    content: null);
                var body = await response.Content.ReadAsStringAsync();
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return $"Expected 200 OK for the busy-bypass preload, got {(int)response.StatusCode}: {body}";
                }

                var selected = JsonNode.Parse(body)?["selectedTarget"]?.GetValue<string>();
                return string.Equals(selected, server.Name, StringComparison.OrdinalIgnoreCase)
                    ? $"The busy server-class target '{server.Name}' (in-flight > 0) must be bypassed in favor of an idle capable node; the preload selected it anyway. Body: {body}"
                    : null;
            }
            finally
            {
                cts.Cancel();
                try
                {
                    var chatResponse = await background;
                    chatResponse.Dispose();
                }
                catch
                {
                    // The background chat is aborted by the timeout/cancellation; its transport
                    // failure must not mask the scenario outcome.
                }
            }
        });

    // The public pin header name (OllamaRouter.ReverseProxy.OllamaReverseProxyConfig.PublicTargetHeader) —
    // the E2E project has no reference to the router assembly, so the wire name is inlined.
    private const string OllamaRouterE2E_PinningHeader = "X-OllamaRouter-Target";

    private static int? GetInt(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out int? value) ? value : null;

    private static bool GetBool(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out bool? value) && value == true;

    private static async Task<(JsonNode? Fleet, HttpStatusCode Status)> GetFleetAsync(HttpClient http, string routerUrl)
    {
        using var response = await http.GetAsync($"{routerUrl.TrimEnd('/')}/api/router/fleet");
        var body = await response.Content.ReadAsStringAsync();
        JsonNode? fleet = null;
        if (response.IsSuccessStatusCode)
        {
            try
            {
                fleet = JsonNode.Parse(body);
            }
            catch
            {
                fleet = null;
            }
        }

        return (fleet, response.StatusCode);
    }
}
