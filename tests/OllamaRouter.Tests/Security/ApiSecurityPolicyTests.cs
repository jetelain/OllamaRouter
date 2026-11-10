using OllamaRouter.Security;

namespace OllamaRouter.Tests.Security;

/// <summary>
/// Unit tests for the default API-key-protected route set (task #50): the full include matrix
/// (every template incl. {name} parameterization, case-insensitive paths, method sensitivity)
/// and the exclude matrix (POST /targets, reads, runtime actions, proxy / unknown paths, wrong
/// methods on protected paths).
/// </summary>
public class ApiSecurityPolicyTests
{
    // ------------------------------------------------------------------
    //  Default protected set (include matrix)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "/api/router/config")]
    [InlineData("PUT", "/api/router/config")]
    [InlineData("POST", "/api/router/targets")]
    [InlineData("PUT", "/api/router/targets/{alpha}")]
    [InlineData("DELETE", "/api/router/targets/{alpha}")]
    [InlineData("PUT", "/api/router/fleet/hardware/{targetName}")]
    [InlineData("POST", "/api/router/benchmark")]
    [InlineData("DELETE", "/api/router/benchmark/results")]
    [InlineData("GET", "/api/router/api-keys")]
    [InlineData("POST", "/api/router/api-keys")]
    [InlineData("DELETE", "/api/router/api-keys/{name}")]
    [InlineData("POST", "/api/router/api-keys/default/rotate")]
    public void IsProtectedRoute_DefaultSet_IncludesConfigAffectingRoutes(string method, string path)
    {
        Assert.True(ApiSecurityPolicy.IsProtectedRoute(method, path), $"{method} {path} must be protected");
    }

    [Theory]
    [InlineData("PUT", "/api/router/targets/Local")]
    [InlineData("DELETE", "/api/router/targets/remote-node")]
    [InlineData("PUT", "/api/router/fleet/hardware/Peer")]
    [InlineData("DELETE", "/api/router/api-keys/staging")]
    public void IsProtectedRoute_TemplateRoutes_MatchSingleSegmentParameterization(string method, string path)
    {
        Assert.True(ApiSecurityPolicy.IsProtectedRoute(method, path), $"{method} {path} must be protected");
    }

    [Theory]
    [InlineData("get", "/api/router/config")]
    [InlineData("post", "/Api/Router/Targets")]
    [InlineData("DeLeTe", "/api/router/benchmark/results")]
    public void IsProtectedRoute_MatchesCaseInsensitively(string method, string path)
    {
        Assert.True(ApiSecurityPolicy.IsProtectedRoute(method, path), $"{method} {path} must be protected (case-insensitive)");
    }

    [Fact]
    public void IsProtectedRoute_TrailingSlash_MatchesEndpointRoutingSemantics()
    {
        // Endpoint routing ignores trailing slashes; the path-based match mirrors it.
        Assert.True(ApiSecurityPolicy.IsProtectedRoute("GET", "/api/router/config/"));
        Assert.True(ApiSecurityPolicy.IsProtectedRoute("POST", "/api/router/targets/"));
    }

    // ------------------------------------------------------------------
    //  Excludes (default mode)
    // ------------------------------------------------------------------

    [Fact]
    public void IsProtectedRoute_MonitorAvailabilityToggle_StaysOpen()
    {
        // POST /targets (the monitor availability toggle) is runtime state, not persistent
        // config — it is NOT in the protected set even though POST /api/router/targets is.
        Assert.False(ApiSecurityPolicy.IsProtectedRoute("POST", "/targets"));
        Assert.False(ApiSecurityPolicy.IsProtectedRoute("PUT", "/targets"));
        Assert.False(ApiSecurityPolicy.IsProtectedRoute("GET", "/targets"));
    }

    [Theory]
    [InlineData("GET", "/api/router/info")]
    [InlineData("GET", "/api/router/fleet")]
    [InlineData("GET", "/api/router/fleet/hardware")]
    [InlineData("GET", "/api/router/hardware")]
    [InlineData("GET", "/api/router/models")]
    [InlineData("GET", "/api/router/models/llama3:8b")]
    [InlineData("GET", "/api/router/cartography")]
    [InlineData("GET", "/api/router/benchmark/results")]
    [InlineData("GET", "/api/router/preload/preview")]
    [InlineData("POST", "/api/router/models/llama3/preload")]
    [InlineData("POST", "/api/router/vram/reclaim")]
    [InlineData("POST", "/api/router/fleet/unload")]
    [InlineData("POST", "/api/router/context/evaluate")]
    [InlineData("GET", "/monitor")]
    [InlineData("GET", "/monitor/api/state")]
    [InlineData("POST", "/monitor/reclaim-vram")]
    [InlineData("GET", "/api/tags")]
    [InlineData("GET", "/api/ps")]
    [InlineData("POST", "/api/chat")]
    [InlineData("GET", "/v1/models")]
    [InlineData("DELETE", "/api/models/unload")]
    [InlineData("POST", "/targets")]
    public void IsProtectedRoute_DefaultSet_ExcludesReadsRuntimeAndProxyPaths(string method, string path)
    {
        Assert.False(ApiSecurityPolicy.IsProtectedRoute(method, path), $"{method} {path} must stay open in default mode");
    }

    [Theory]
    [InlineData("DELETE", "/api/router/config")]
    [InlineData("GET", "/api/router/targets")]
    [InlineData("PUT", "/api/router/targets")]
    [InlineData("DELETE", "/api/router/targets")]
    [InlineData("POST", "/api/router/targets/alpha")]
    [InlineData("GET", "/api/router/targets/alpha")]
    [InlineData("GET", "/api/router/fleet/hardware/Peer")]
    [InlineData("DELETE", "/api/router/fleet/hardware/Peer")]
    [InlineData("GET", "/api/router/benchmark")]
    [InlineData("POST", "/api/router/benchmark/results")]
    [InlineData("GET", "/api/router/api-keys/{name}")]
    [InlineData("PUT", "/api/router/api-keys/name")]
    [InlineData("DELETE", "/api/router/api-keys/default/rotate")]
    [InlineData("GET", "/api/router/api-keys/default/rotate")]
    [InlineData("POST", "/api/router/api-keys/name")]
    [InlineData("DELETE", "/api/router/api-keys/name/sub")] // two segments: no single-segment match
    [InlineData("GET", "/api/router/unknown")]
    [InlineData("POST", "/")]
    [InlineData("GET", "/")]
    public void IsProtectedRoute_WrongMethodOrUnknownPath_IsOpen(string method, string path)
    {
        Assert.False(ApiSecurityPolicy.IsProtectedRoute(method, path), $"{method} {path} must stay open");
    }

    [Theory]
    [InlineData(null, "/api/router/config")]
    [InlineData("GET", null)]
    [InlineData(null, null)]
    [InlineData("", "/api/router/config")]
    [InlineData("GET", "")]
    public void IsProtectedRoute_NullOrEmptyInputs_ReturnsFalse(string? method, string? path)
    {
        Assert.False(ApiSecurityPolicy.IsProtectedRoute(method, path));
    }
}
