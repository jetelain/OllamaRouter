namespace OllamaRouter.Security;

/// <summary>
/// The default API-key-protected route set (task #50), resolved by PATH + HTTP method (no
/// endpoint metadata — deliberate: this uniformly covers mapped endpoints AND the YARP
/// catch-all before routing, which is why the auth middleware sits FIRST in the pipeline).
/// <para>
/// DEFAULT mode (config-affecting routes only): <c>GET/PUT /api/router/config</c>,
/// <c>POST /api/router/targets</c>, <c>PUT/DELETE /api/router/targets/{name}</c>,
/// <c>PUT /api/router/fleet/hardware/{targetName}</c>, <c>POST /api/router/benchmark</c>,
/// <c>DELETE /api/router/benchmark/results</c> and the key-management endpoints
/// (<c>GET/POST /api/router/api-keys</c>, <c>DELETE /api/router/api-keys/{name}</c>,
/// <c>POST /api/router/api-keys/default/rotate</c> — key-protected by definition).
/// </para>
/// <para>
/// Explicitly NOT in the default set: <c>POST /targets</c> (monitor availability toggle —
/// runtime state, not persistent config; the browser flow must keep working), all reads
/// (<c>/api/router/info|fleet|models|cartography|benchmark/results|preload/preview</c>,
/// <c>/api/tags|ps|show</c>, <c>/v1/models</c>, <c>/monitor*</c>), the runtime actions
/// (<c>/api/router/vram/reclaim</c>, <c>/api/router/fleet/unload</c>,
/// <c>/api/router/context/evaluate</c>, preload, <c>/monitor/reclaim-vram</c>,
/// <c>/api/models/unload</c>), the YARP proxy paths and unknown paths.
/// </para>
/// </summary>
public static class ApiSecurityPolicy
{
    /// <summary>
    /// Returns true when the route is in the DEFAULT protected set (case-insensitive path
    /// comparison; template routes = fixed prefix + exactly one non-empty trailing segment).
    /// Never throws.
    /// </summary>
    /// <param name="httpMethod">The HTTP method (e.g. "GET", "POST"; case-insensitive).</param>
    /// <param name="pathValue">The request path (e.g. <c>request.Path.Value</c>; query excluded).</param>
    public static bool IsProtectedRoute(string? httpMethod, string? pathValue)
    {
        if (string.IsNullOrEmpty(pathValue) || string.IsNullOrEmpty(httpMethod))
        {
            return false;
        }

        // Endpoint routing ignores trailing slashes; normalize so the path-based match
        // mirrors it ("/api/router/config/" matches the mapped "/api/router/config").
        var path = pathValue.TrimEnd('/');
        var method = httpMethod.ToUpperInvariant();

        // ---- Exact routes -------------------------------------------------
        if (string.Equals(path, "/api/router/config", StringComparison.OrdinalIgnoreCase))
        {
            return method is "GET" or "PUT";
        }

        if (string.Equals(path, "/api/router/targets", StringComparison.OrdinalIgnoreCase))
        {
            // POST only — the browser "monitor availability" toggle POST /targets is a
            // DIFFERENT path (no /api/router prefix) and stays unauthenticated.
            return method == "POST";
        }

        if (string.Equals(path, "/api/router/benchmark", StringComparison.OrdinalIgnoreCase))
        {
            return method == "POST";
        }

        if (string.Equals(path, "/api/router/benchmark/results", StringComparison.OrdinalIgnoreCase))
        {
            return method == "DELETE";
        }

        // ---- Key management (key-protected by definition) -----------------
        if (string.Equals(path, "/api/router/api-keys", StringComparison.OrdinalIgnoreCase))
        {
            return method is "GET" or "POST";
        }

        if (string.Equals(path, "/api/router/api-keys/default/rotate", StringComparison.OrdinalIgnoreCase))
        {
            return method == "POST";
        }

        // ---- Template routes (fixed prefix + exactly one non-empty segment) ----
        if (TryGetSingleSegment(path, "/api/router/targets/", out _))
        {
            return method is "PUT" or "DELETE";
        }

        if (TryGetSingleSegment(path, "/api/router/fleet/hardware/", out _))
        {
            return method == "PUT";
        }

        if (TryGetSingleSegment(path, "/api/router/api-keys/", out _))
        {
            // DELETE /api/router/api-keys/{name} — different method + segment count from
            // POST /api/router/api-keys/default/rotate (no route conflict).
            return method == "DELETE";
        }

        return false;
    }

    /// <summary>
    /// Returns true when <paramref name="path"/> starts with <paramref name="prefix"/>
    /// (case-insensitive) followed by EXACTLY one non-empty segment (no further <c>/</c>).
    /// </summary>
    private static bool TryGetSingleSegment(string path, string prefix, out string segment)
    {
        segment = string.Empty;
        if (path.Length <= prefix.Length || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        segment = path[prefix.Length..];
        return segment.Length > 0 && !segment.Contains('/');
    }
}
