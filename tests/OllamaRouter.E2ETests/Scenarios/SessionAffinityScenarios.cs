using System.Net;
using System.Text.Json.Nodes;

namespace OllamaRouter.E2ETests.Scenarios;

/// <summary>
/// Scenario exercising the session-affinity lease release endpoint (task #86):
/// - POST /api/router/sessions/affinity/release WITHOUT the X-OllamaRouter-Session-Id header
///   → 400 with the exact OllamaErrorResponse error body;
/// - POST WITH a fresh session header (no E2E scenario ever sends the sticky session header,
///   so no lease can exist) → 200 application/json with the { sessionId, released } shape and
///   released == false;
/// - the immediate repeat → 200 released == false (idempotent shape).
/// Public by default (no key required, like the read-only router routes).
/// </summary>
internal static class SessionAffinityScenarios
{
    public static Scenario ReleaseSessionAffinityLease() => new("Session affinity lease release /api/router/sessions/affinity/release 400-without-header + 200 released:false shape (task #86)", async config =>
    {
        using var http = new HttpClient { BaseAddress = new Uri(config.RouterUrl) };

        // 1. Missing header → 400 with the exact documented error body.
        using var missing = await http.PostAsync("/api/router/sessions/affinity/release", content: null);
        if (missing.StatusCode != HttpStatusCode.BadRequest)
        {
            return $"Expected 400 from POST /api/router/sessions/affinity/release without a session header, got {(int)missing.StatusCode} {missing.ReasonPhrase}.";
        }

        var missingNode = JsonNode.Parse(await missing.Content.ReadAsStringAsync());
        var error = missingNode?["error"]?.GetValue<string>();
        if (error != "X-OllamaRouter-Session-Id header is required")
        {
            return $"Expected the exact 400 error body 'X-OllamaRouter-Session-Id header is required', got '{error}'.";
        }

        // 2. Fresh session ID (no lease can exist in this E2E host) → 200 released:false shape.
        using var release = await ReleaseAsync(http, "e2e-affinity-release-86");
        if (release.StatusCode != HttpStatusCode.OK)
        {
            return $"Expected 200 from POST /api/router/sessions/affinity/release with a session header, got {(int)release.StatusCode} {release.ReasonPhrase}.";
        }

        var contentType = release.Content.Headers.ContentType?.MediaType;
        if (contentType != "application/json")
        {
            return $"Expected Content-Type 'application/json', got '{contentType}'.";
        }

        var body = JsonNode.Parse(await release.Content.ReadAsStringAsync());
        if (body?["sessionId"]?.GetValue<string>() != "e2e-affinity-release-86")
        {
            return $"Expected the echoed sessionId 'e2e-affinity-release-86', got '{body?["sessionId"]}'.";
        }

        if (body?["released"]?.GetValue<bool>() != false)
        {
            return $"Expected 'released': false for a session that never held a lease, got '{body?["released"]}'.";
        }

        // 3. Immediate repeat → 200 released:false (idempotent shape).
        using var repeat = await ReleaseAsync(http, "e2e-affinity-release-86");
        if (repeat.StatusCode != HttpStatusCode.OK)
        {
            return $"Expected 200 from the repeated release, got {(int)repeat.StatusCode} {repeat.ReasonPhrase}.";
        }

        var repeatBody = JsonNode.Parse(await repeat.Content.ReadAsStringAsync());
        if (repeatBody?["released"]?.GetValue<bool>() != false)
        {
            return $"Expected the repeat to be idempotent ('released': false), got '{repeatBody?["released"]}'.";
        }

        return null;
    });

    private static async Task<HttpResponseMessage> ReleaseAsync(HttpClient http, string sessionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/router/sessions/affinity/release");
        request.Headers.Add("X-OllamaRouter-Session-Id", sessionId);
        return await http.SendAsync(request);
    }
}
