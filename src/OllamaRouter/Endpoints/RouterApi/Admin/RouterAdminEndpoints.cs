using Microsoft.AspNetCore.Http;
using OllamaRouter.Endpoints.RouterApi.Info;
using OllamaRouter.Serialization;
using Microsoft.Extensions.Hosting;

namespace OllamaRouter.Endpoints.RouterApi.Admin;

/// <summary>
/// The router daemon lifecycle area of the OllamaRouter management API (task #99):
/// <list type="bullet">
/// <item><c>POST /api/router/admin/shutdown</c> (202 Accepted) — triggers a GRACEFUL host
/// stop via <see cref="IHostApplicationLifetime.StopApplication"/>: Kestrel stops the
/// listener and drains in-flight requests, so <c>ActivityStatisticsService</c> /
/// <c>SessionUsageStore</c> (both registered on <c>ApplicationStopped</c>) flush their
/// in-memory statistics to disk. <see cref="Environment.Exit"/> is deliberately NOT used
/// (it skips the hosted-service shutdown + <c>ApplicationStopped</c> callbacks).
/// The stop signal is raised from <c>HttpContext.Response.OnStarting</c> — i.e. exactly
/// when the 202 response headers begin flowing — so the caller ALWAYS receives the
/// complete <c>202</c> body before the host tears down. Key-protected by default
/// (see <c>ApiSecurityPolicy</c>).</item>
/// </list>
/// </summary>
public static class RouterAdminEndpoints
{
    /// <summary>
    /// Maps <c>POST /api/router/admin/shutdown</c>.
    /// </summary>
    public static WebApplication MapRouterAdminEndpoints(this WebApplication app)
    {
        app.MapPost("/api/router/admin/shutdown", (HttpContext context, IHostApplicationLifetime lifetime) =>
        {
            // Response-first guarantee: fire the (non-blocking, idempotent) stop signal
            // exactly when the 202 response starts flowing to the client. Kestrel's
            // graceful shutdown then stops the listener and WAITS for this in-flight
            // request, so the client receives the full body before the host exits.
            context.Response.OnStarting(() =>
            {
                lifetime.StopApplication();
                return Task.CompletedTask;
            });
            return Results.Json(new RouterShutdownResponse(Stopping: true, ProcessId: Environment.ProcessId),
                OllamaRouterJsonSerializerContext.Default.RouterShutdownResponse,
                statusCode: StatusCodes.Status202Accepted);
        })
        .WithName("PostRouterAdminShutdown")
        .WithTags("Admin & Lifecycle")
        .WithSummary("Gracefully shut down the router daemon (202; the host stops after the response).")
        .WithDescription("Key-protected. Returns 202 Accepted with {\"stopping\":true,\"processId\":...} BEFORE the host "
            + "actually stops: the IHostApplicationLifetime.StopApplication() signal is raised when the response starts "
            + "flowing, Kestrel then stops the listener and drains this in-flight request, so the complete 202 body is "
            + "always delivered. The graceful stop lets the hosted statistics persistence (ActivityStatisticsService and "
            + "SessionUsageStore, both registered on ApplicationStopped) flush their in-memory state to disk. Re-invoking "
            + "the endpoint (e.g. a concurrent Ctrl+C) is harmless: StopApplication is idempotent.");
        return app;
    }
}
