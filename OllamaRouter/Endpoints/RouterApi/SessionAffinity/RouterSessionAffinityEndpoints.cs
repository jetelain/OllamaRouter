using System.Net;
using Microsoft.AspNetCore.Mvc;
using OllamaRouter.Middleware;
using OllamaRouter.ReverseProxy;
using OllamaRouter.Serialization;
using OllamaRouter.Services.Routing;

namespace OllamaRouter.Endpoints.RouterApi.SessionAffinity;

/// <summary>
/// The session-affinity lease endpoint of the OllamaRouter programmatic management API
/// (task #86):
/// <list type="bullet">
/// <item><c>POST /api/router/sessions/affinity/release</c> — releases the caller session's
/// sticky-routing lease so the router can repin the session's future traffic (the session
/// ends or was compacted and its KV-cache prefix on the pinned target is no longer needed).
/// The session is identified by the public <c>X-OllamaRouter-Session-Id</c> request header
/// (the same header clients already send on inference requests), never by a path parameter.
/// </item>
/// </list>
/// Idempotent by design: ALWAYS 200 (<c>{ "sessionId": …, "released": … }</c>) except 400 on
/// a missing/blank header — clients can fire-and-forget on session end.
/// </summary>
public static class RouterSessionAffinityEndpoints
{
    /// <summary>
    /// Maps the session-affinity lease release endpoint
    /// (<c>POST /api/router/sessions/affinity/release</c>).
    /// </summary>
    public static WebApplication MapRouterSessionAffinityEndpoints(this WebApplication app)
    {
        app.MapPost("/api/router/sessions/affinity/release", (HttpContext context, ISessionAffinityService? affinity) =>
        {
            var sessionId = context.Request.Headers[OllamaReverseProxyConfig.PublicSessionIdHeader].ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return Results.Json(
                    new OllamaErrorResponse("X-OllamaRouter-Session-Id header is required"),
                    OllamaRouterJsonSerializerContext.Default.OllamaErrorResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // Oversized IDs can never hold a lease (the routing middleware caps at
            // OllamaReverseProxyConfig.PublicSessionIdMaxLength chars), and a null store
            // (the affinity stack not wired, e.g. a minimal host) means no lease can exist —
            // both take the idempotent released:false path, never a 400/5xx.
            var released = false;
            if (affinity is not null &&
                sessionId!.Length <= OllamaReverseProxyConfig.PublicSessionIdMaxLength)
            {
                released = affinity.TryRemoveAffinity(sessionId);
            }

            return Results.Json(
                new RouterSessionAffinityReleaseResponse(sessionId, released),
                OllamaRouterJsonSerializerContext.Default.RouterSessionAffinityReleaseResponse);
        })
        .WithName("PostRouterSessionAffinityRelease")
        .WithTags("Session Affinity")
        .WithSummary("Release the session's sticky-routing affinity lease (idempotent).")
        .WithDescription("Releases the session's sticky-routing lease from the "
            + "ISessionAffinityService sliding-TTL store (default 30 min) so the session's NEXT "
            + "request routes fresh and re-pins as normal — call it when the session is done, was "
            + "compacted, or its KV-cache prefix is no longer needed, instead of waiting for the "
            + "lease's sliding TTL to lapse. The session is identified by the public "
            + "X-OllamaRouter-Session-Id REQUEST HEADER (the same header clients send on inference "
            + "requests; whitespace-trimmed, echoed back in sessionId; case-insensitive lookup), "
            + "never by a path parameter. Bodyless operation. Idempotent: ALWAYS 200 "
            + "RouterSessionAffinityReleaseResponse — released is true iff a LIVE lease was found "
            + "and removed; false for unknown session IDs, TTL-expired (not-yet-swept) leases and "
            + "already-released sessions; a header longer than "
            + OllamaReverseProxyConfig.PublicSessionIdMaxLength + " characters can never hold a lease "
            + "(the routing middleware ignores such IDs) and takes the same 200 released:false path. "
            + "Only 400 OllamaErrorResponse (error: 'X-OllamaRouter-"
            + "Session-Id header is required') for a missing/empty/whitespace-only header. Scope: "
            + "the session AFFINITY lease ONLY — the per-target/model dynamic-context auto-shrink "
            + "lease (shared across sessions, cooldown/idle hysteresis), the failure-streak state "
            + "and any per-target state are untouched; in-flight requests are unaffected. Public "
            + "in default mode (outside the API-key protected set, like the read-only router "
            + "routes); covered when ApiSecurity.RequireKeyForAllEndpoints: true, like every "
            + "route.");
        return app;
    }
}
