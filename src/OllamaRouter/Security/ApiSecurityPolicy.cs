namespace OllamaRouter.Security;

/// <summary>
/// The default API-key-protected route set (task #50), resolved by PATH + HTTP method (no
/// endpoint metadata — deliberate: this uniformly covers mapped endpoints AND the YARP
/// catch-all before routing, which is why the auth middleware sits FIRST in the pipeline).
/// <para>
/// DEFAULT mode (config-affecting routes only): <c>GET/PUT /api/router/config</c>,
/// <c>POST /api/router/targets</c>, <c>PUT/DELETE /api/router/targets/{name}</c>,
/// <c>PUT /api/router/fleet/hardware/{targetName}</c>, <c>POST /api/router/benchmark</c>,
/// <c>DELETE /api/router/benchmark/results</c>, the recommendation writes
/// (<c>POST /api/router/recommendations/generate</c>,
/// <c>POST /api/router/recommendations/apply</c>) and the key-management endpoints
/// (<c>GET/POST /api/router/api-keys</c>, <c>DELETE /api/router/api-keys/{name}</c>,
/// <c>POST /api/router/api-keys/default/rotate</c> — key-protected by definition), the
/// failure-streak reset admin actions (task #95: <c>POST /api/router/targets/{name}/reset-streak</c>
/// and <c>POST /api/router/fleet/reset-streaks</c> — they mutate live routing-reliability
/// state fleet-wide and re-admit failing nodes, so they are admin actions like the shutdown,
/// not browser runtime toggles) and the daemon lifecycle endpoint
/// <c>POST /api/router/admin/shutdown</c> (task #99: stopping the
/// host is config-lifetime-affecting and must never be reachable keyless), plus the model
/// uninstall action (task #126: <c>POST /api/router/models/uninstall</c> — DESTRUCTIVE AND
/// PERSISTENT, like the target-DELETE admin actions: it removes multi-GB model files from
/// fleet node disks permanently, so it must never be reachable keyless).
/// </para>
/// <para>
/// Explicitly NOT in the default set: <c>POST /targets</c> (monitor availability toggle —
/// runtime state, not persistent config; the browser flow must keep working), all reads
/// (<c>/api/router/info|fleet|models|cartography|benchmark/results|recommendations|preload/preview</c>,
/// <c>/api/tags|ps|show</c>, <c>/v1/models</c>, <c>/monitor*</c>), the runtime actions
/// (<c>/api/router/vram/reclaim</c>, <c>/api/router/fleet/unload</c>,
/// <c>/api/router/context/evaluate</c>, preload, <c>/monitor/reclaim-vram</c>,
/// <c>/api/models/unload</c> — unload/preload are re-provisionable runtime state; model
/// UNINSTALL deletes the files permanently and IS protected), the YARP proxy paths and unknown paths.
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

        // Role recommendations (task #66): the matrix read is open (like the benchmark
        // results read); both writes are config-affecting (they persist recommended.json /
        // activate live ceilings) and key-protected.
        if (string.Equals(path, "/api/router/recommendations/generate", StringComparison.OrdinalIgnoreCase))
        {
            return method == "POST";
        }

        if (string.Equals(path, "/api/router/recommendations/apply", StringComparison.OrdinalIgnoreCase))
        {
            return method == "POST";
        }

        if (string.Equals(path, "/api/router/admin/shutdown", StringComparison.OrdinalIgnoreCase))
        {
            // POST only (task #99): the graceful daemon shutdown is lifecycle-affecting.
            return method == "POST";
        }

        // Model uninstall (task #126): DESTRUCTIVE + PERSISTENT — permanently deletes model
        // files from fleet node disks (grouped with the target-DELETE admin actions).
        // POST only, literal route (the /api/router/models catch-alls stay open).
        if (string.Equals(path, "/api/router/models/uninstall", StringComparison.OrdinalIgnoreCase))
        {
            return method == "POST";
        }

        // Failure-streak reset fleet-wide (task #95): an admin action that re-admits failing
        // nodes across the fleet (runtime routing-reliability state; POST only).
        if (string.Equals(path, "/api/router/fleet/reset-streaks", StringComparison.OrdinalIgnoreCase))
        {
            return method == "POST";
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

        // Per-target failure-streak reset (task #95): fixed prefix + exactly TWO non-empty
        // segments whose LAST is 'reset-streak' (the single-segment rule above is NOT
        // loosened). Admin action, POST only.
        if (TryGetResetStreakSegment(path))
        {
            return method == "POST";
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

    /// <summary>
    /// True when <paramref name="path"/> is <c>/api/router/targets/{name}/reset-streak</c>
    /// (task #95): the fixed prefix (case-insensitive) followed by EXACTLY two non-empty
    /// segments whose LAST equals <c>reset-streak</c> (case-insensitive).
    /// </summary>
    private static bool TryGetResetStreakSegment(string path)
    {
        const string prefix = "/api/router/targets/";
        if (path.Length <= prefix.Length || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = path[prefix.Length..];
        var slashIndex = rest.IndexOf('/');
        return slashIndex > 0 &&
               slashIndex < rest.Length - 1 &&
               !rest[(slashIndex + 1)..].Contains('/') &&
               string.Equals(rest[(slashIndex + 1)..], "reset-streak", StringComparison.OrdinalIgnoreCase);
    }
}
