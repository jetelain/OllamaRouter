using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OllamaRouter.Options;
using OllamaRouter.Services;

namespace OllamaRouter.Tests.Mesh;

/// <summary>
/// Shared helpers for the mesh test scenarios: deterministic prompt sizing (via the REAL
/// <see cref="HeuristicTokenEstimator"/> so sizing decisions land in the intended bucket), a
/// chat-dispatching client, and liveness/catalo warm-up + wait primitives.
/// </summary>
public static class MeshTestHelpers
{
    /// <summary>The exact ASCII word the heuristic estimator counts as one 1.25-token unit ("alpha" = 5 letters).</summary>
    public const string TokenWord = "alpha";

    private static readonly HeuristicTokenEstimator Estimator = new();

    /// <summary>
    /// Builds a prompt that the real <see cref="HeuristicTokenEstimator"/> sizes to EXACTLY
    /// <paramref name="tokenCount"/> (the harness computes the exact char count with the estimator —
    /// never assuming the char/token ratio). The estimator's char-floor branch (⌈len/3.8⌉) dominates
    /// for short words, so we build a run of <see cref="TokenWord"/> words whose trimmed length
    /// <c>len</c> satisfies ⌈len/3.8⌉ == tokenCount, then verify and fine-tune with the real
    /// estimator so the sizing decision lands in the intended bucket.
    /// </summary>
    public static string BuildPrompt(int tokenCount)
    {
        if (tokenCount <= 0)
        {
            return string.Empty;
        }

        // In the char-floor regime the estimator returns ⌈len/3.8⌉ (the word branch is far below
        // it for these dense short words). Pick len so ⌈len/3.8⌉ == tokenCount, then nudge.
        var len = (int)Math.Floor(tokenCount * 3.8);
        var prompt = BuildWordsOfLength(len);

        for (var i = 0; i < 12; i++)
        {
            var estimated = Estimator.EstimateTokens([prompt]);
            if (estimated == tokenCount)
            {
                return prompt;
            }

            // Move the trimmed length by the delta the floor implies (≈ delta × 3.8 chars).
            var delta = tokenCount - estimated;
            var newLen = Math.Max(TokenWord.Length, prompt.Length + (int)Math.Round(delta * 3.8));
            prompt = BuildWordsOfLength(newLen);
        }

        return prompt;
    }

    /// <summary>A prompt of exactly <paramref name="minLength"/>+ trimmed chars, composed of <see cref="TokenWord"/> words.</summary>
    private static string BuildWordsOfLength(int minLength)
    {
        var words = (minLength + (TokenWord.Length + 1) - 1) / (TokenWord.Length + 1);
        if (words < 1)
        {
            words = 1;
        }

        var sb = new StringBuilder(words * (TokenWord.Length + 1));
        for (var i = 0; i < words; i++)
        {
            sb.Append(TokenWord).Append(' ');
        }

        var text = sb.ToString().TrimEnd();
        if (text.Length > minLength)
        {
            text = text.Substring(0, minLength);
        }
        return text.TrimEnd();
    }

    /// <summary>The estimated token count the router will compute for <paramref name="prompt"/> (no overhead factor).</summary>
    public static int EstimateTokens(string prompt) => Estimator.EstimateTokens([prompt]);

    /// <summary>
    /// Sends a streaming <c>POST /api/chat</c> for <paramref name="model"/> with a prompt of
    /// <paramref name="tokenCount"/> tokens, an optional client <c>options.num_ctx</c> and an
    /// optional <c>X-OllamaRouter-Target</c> pin. Returns the (not yet consumed) response.
    /// </summary>
    public static Task<HttpResponseMessage> SendChatAsync(
        MeshRouter router,
        string model,
        int tokenCount,
        int? numCtx = null,
        string? pin = null,
        bool returnOnHeaders = false)
    {
        var options = new JsonObject();
        if (numCtx is { } value)
        {
            options["num_ctx"] = value;
        }

        var payload = new JsonObject
        {
            ["model"] = model,
            ["stream"] = true,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = BuildPrompt(tokenCount) }
            }
        };
        if (options.Count > 0)
        {
            payload["options"] = options;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        if (pin is not null)
        {
            request.Headers.Add(OllamaRouter.ReverseProxy.OllamaReverseProxyConfig.PublicTargetHeader, pin);
        }

        return router.Client.SendAsync(
            request,
            returnOnHeaders ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead);
    }

    /// <summary>Reads the full NDJSON/SSE body of <paramref name="response"/> and disposes it.</summary>
    public static async Task<string> DrainAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.Dispose();
        return body;
    }

    /// <summary>
    /// Tolerantly reads a streaming body that may be aborted MID-stream by the server (a socket
    /// reset after some NDJSON chunks were committed). Returns (text, interrupted) where
    /// interrupted is true when the transport surfaced a connection reset instead of a clean
    /// EOF. On a hard reset the committed prefix is not retrievable (SocketsHttpHandler surfaces
    /// the interruption as an exception, not partial content), so the caller combines this with
    /// the daemon-side observation plane (recorded chunks) to assert the no-corruption property.
    /// The response is always disposed.
    /// </summary>
    public static async Task<(string Text, bool Interrupted)> DrainTolerantAsync(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync();
            response.Dispose();
            return (body, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // Mid-stream abort: the socket was reset after the headers (and possibly some
            // chunks) were committed. Dispose so the connection is released; report the
            // interruption so the caller asserts the abort semantics.
            response.Dispose();
            return (string.Empty, true);
        }
    }

    /// <summary>
    /// Like <see cref="DrainTolerantAsync"/> but does NOT dispose the response: it leaves the
    /// response (and its socket) open so the caller can dispose it at a controlled time. Fault-
    /// injection scenarios use this to observe server-side §7 bookkeeping (the failure streak,
    /// recorded in the server's finally-block) BEFORE the client closes the socket — an early
    /// dispose cancels the router's RequestAborted token in a race that would misclassify a
    /// genuine server-side mid-stream abort as a client abort and suppress the streak.
    /// </summary>
    public static async Task<(string Text, bool Interrupted)> DrainTolerantKeepOpenAsync(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync();
            return (body, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return (string.Empty, true);
        }
    }

    /// <summary>Sends a chat, drains the response, and returns (status, body, served target header, context-size header, context-mode header).</summary>
    public static async Task<(HttpStatusCode Status, string Body, string? Served, int? ContextSize, string? ContextMode)> ChatAsync(
        MeshRouter router,
        string model,
        int tokenCount,
        int? numCtx = null,
        string? pin = null)
    {
        var response = await SendChatAsync(router, model, tokenCount, numCtx, pin);
        var status = response.StatusCode;
        string? served = null;
        string? contextSizeRaw = null;
        string? contextMode = null;
        if (response.Headers.TryGetValues(OllamaRouter.ReverseProxy.OllamaReverseProxyConfig.PublicTargetHeader, out var sv1)) served = sv1.FirstOrDefault();
        if (response.Headers.TryGetValues(OllamaRouter.ReverseProxy.OllamaReverseProxyConfig.TelemetryContextSizeHeader, out var sv2)) contextSizeRaw = sv2.FirstOrDefault();
        if (response.Headers.TryGetValues(OllamaRouter.ReverseProxy.OllamaReverseProxyConfig.TelemetryContextModeHeader, out var sv3)) contextMode = sv3.FirstOrDefault();
        var body = await DrainAsync(response);
        return (status, body, served, int.TryParse(contextSizeRaw, out var c) ? c : null, contextMode);
    }

    /// <summary>Warms the liveness prober + model catalog for <paramref name="router"/> so routing decisions see the full fleet.</summary>
    public static async Task WarmupAsync(MeshRouter router, string model)
    {
        foreach (var target in router.Liveness.GetFleetTargets())
        {
            _ = await router.Liveness.EnsureKnownAsync(target, CancellationToken.None);
        }
        await router.ExecutionTracker.RefreshAsync(CancellationToken.None);

        // A single small chat per target the daemon actually hosts: establishes the /api/show
        // capability metadata (digest-keyed) the sizing pipeline reads, without loading state
        // that would skew affinity scoring for the actual scenario.
        var response = await SendChatAsync(router, model, 8);
        await DrainAsync(response);
    }

    /// <summary>Waits (bounded) until every fleet target reports Online; returns whether all came up.</summary>
    public static async Task<bool> WaitAllOnlineAsync(MeshRouter router, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var all = router.Liveness.GetFleetTargets()
                .All(t => router.Liveness.GetEntries().TryGetValue(t, out var e) && e.State == TargetLivenessState.Online);
            if (all)
            {
                return true;
            }
            await Task.Delay(50, CancellationToken.None);
        }
        return false;
    }

    /// <summary>
    /// Waits (bounded) until the router's <see cref="ITargetExecutionTracker"/> snapshot for
    /// <paramref name="target"/> reports <paramref name="model"/> as loaded. The tracker refreshes
    /// out-of-band on a ~2 s background timer that only fetches <c>/api/ps</c> for targets it
    /// considers Online (a 2 s Online-verdict TTL). Under CI load that TTL can lapse between the
    /// preload chat and the timer tick, in which case the refresh would SKIP the target and the
    /// preloaded model never surfaces. To make the precondition deterministic we keep the target's
    /// liveness Online fresh by re-probing it on every poll (a cheap local <c>GET /</c>), so the
    /// next background tick is guaranteed to include it in its fan-out.
    /// </summary>
    public static async Task<bool> WaitForLoadedAsync(MeshRouter router, string target, string model, TimeSpan timeout)
    {
        var routingTarget = new RoutingTarget(target);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var state = router.ExecutionTracker.GetState(routingTarget);
            if (state is { } snapshot && snapshot.LoadedModels.Any(m =>
                string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            // Keep the target's Online TTL fresh and trigger a tracker refresh (which runs when
            // outside the 2 s TTL window, or coalesces with the background refresh).
            try
            {
                _ = await router.Liveness.EnsureKnownAsync(routingTarget, CancellationToken.None);
                await router.ExecutionTracker.RefreshAsync(CancellationToken.None);
            }
            catch
            {
                // Best-effort: a probe hiccup never blocks the wait.
            }

            state = router.ExecutionTracker.GetState(routingTarget);
            if (state is { } snapshotAfterRefresh && snapshotAfterRefresh.LoadedModels.Any(m =>
                string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            await Task.Delay(100, CancellationToken.None);
        }
        return false;
    }

    /// <summary>
    /// Polls (bounded) until the daemon's recorded <c>POST /api/chat</c> hits include one whose
    /// request body carries <paramref name="model"/> (case-insensitive). Used to assert that a
    /// rewritten model name (e.g. the Cloud relay's model rewrite) actually reached the daemon.
    /// </summary>
    public static async Task<bool> WaitForChatModelAsync(FakeOllamaDaemon daemon, string model, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (daemon.ChatHits().Any(h => h.Body.Contains(model, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            await Task.Delay(50, CancellationToken.None);
        }

        return daemon.ChatHits().Any(h => h.Body.Contains(model, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Polls (bounded) until the router's <see cref="ITargetFailureStreakService"/> reports a
    /// positive failure streak for <paramref name="target"/>. The middleware records the streak
    /// in its request finally-block, which runs asynchronously with the client's drain, so an
    /// immediate read right after the response is consumed can race the record (read 0 before the
    /// record). Polling with a short bound makes the §7 streak-feeding assertion deterministic.
    /// </summary>
    public static async Task<bool> WaitForPositiveStreakAsync(MeshRouter router, string target, TimeSpan timeout)
    {
        var routingTarget = new RoutingTarget(target);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (router.Streaks.GetStreak(routingTarget) > 0)
            {
                return true;
            }
            await Task.Delay(50, CancellationToken.None);
        }
        return router.Streaks.GetStreak(routingTarget) > 0;
    }
}
