using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaRouter.Endpoints.RouterApi.SessionAffinity;
using OllamaRouter.Options;
using OllamaRouter.ReverseProxy;
using OllamaRouter.Services.Routing;
using OllamaRouter.Tests.TestHelpers;

namespace OllamaRouter.Tests.Endpoints;

/// <summary>
/// Minimal-WebApplication harness tests for the session-affinity lease release endpoint
/// (task #86, <c>POST /api/router/sessions/affinity/release</c>): live-lease release
/// (<c>released: true</c> + the lease is gone), the idempotent <c>released: false</c> paths
/// (unknown / TTL-expired-not-yet-swept via an injected fake clock), the case-insensitive
/// lookup, the trimmed-header echo, the 400 missing/blank-header rule with the exact error
/// body, the &gt;256-char header idempotent rule (never a 400), and the null-store (affinity
/// stack not wired) degradation to <c>released: false</c>.
/// </summary>
public class RouterSessionAffinityEndpointsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly DateTimeOffset[] _clock;

        public Harness(WebApplication app, HttpClient client, SessionAffinityService? affinity, DateTimeOffset[] clock)
        {
            App = app;
            Client = client;
            Affinity = affinity;
            _clock = clock;
        }

        public WebApplication App { get; }

        public HttpClient Client { get; }

        /// <summary>The affinity service the endpoint resolves (null when the stack is unwired).</summary>
        public SessionAffinityService? Affinity { get; }

        public void AdvanceClock(TimeSpan delta) => _clock[0] += delta;

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private static async Task<Harness> CreateAppAsync(bool registerAffinity)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var clock = new DateTimeOffset[] { T0 };
        SessionAffinityService? service = null;
        if (registerAffinity)
        {
            // Injectable fake clock (the harness advances it deterministically) so the
            // TTL-expired-not-yet-swept case is observable without real waiting.
            service = new SessionAffinityService(
                new TestOptionsMonitor<OllamaRouterOptions>(new OllamaRouterOptions()),
                NullLogger<SessionAffinityService>.Instance,
                () => clock[0]);
            builder.Services.AddSingleton<ISessionAffinityService>(service);
        }

        var app = builder.Build();
        app.MapRouterSessionAffinityEndpoints(); // ONLY the new area (harness isolation)
        await app.StartAsync();

        return new Harness(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) }, service, clock);
    }

    private static async Task<HttpResponseMessage> ReleaseAsync(HttpClient client, string? headerValue)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/router/sessions/affinity/release");
        if (headerValue is not null)
        {
            request.Headers.Add(OllamaReverseProxyConfig.PublicSessionIdHeader, headerValue);
        }

        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Release_LiveLease_Returns200ReleasedTrue_AndLeaseIsGone()
    {
        await using var harness = await CreateAppAsync(registerAffinity: true);
        harness.Affinity!.SetAffinity("sess-86", new RoutingTarget("Desktop"));

        using var response = await ReleaseAsync(harness.Client, "sess-86");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("sess-86", node["sessionId"]!.ToString());
        Assert.True(node["released"]!.GetValue<bool>());
        Assert.False(harness.Affinity.TryGetAffinity("sess-86", out _), "the lease must be removed");
    }

    [Fact]
    public async Task Release_UnknownSession_Returns200ReleasedFalse()
    {
        await using var harness = await CreateAppAsync(registerAffinity: true);

        using var response = await ReleaseAsync(harness.Client, "unknown");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("unknown", node["sessionId"]!.ToString());
        Assert.False(node["released"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Release_LookupIsCaseInsensitive()
    {
        await using var harness = await CreateAppAsync(registerAffinity: true);
        harness.Affinity!.SetAffinity("SESS-86", new RoutingTarget("Desktop"));

        using var response = await ReleaseAsync(harness.Client, "sess-86");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.True(node["released"]!.GetValue<bool>());
        Assert.False(harness.Affinity.TryGetAffinity("sess-86", out _));
    }

    [Fact]
    public async Task Release_TtlExpiredNotYetSwept_Returns200ReleasedFalse()
    {
        await using var harness = await CreateAppAsync(registerAffinity: true);
        harness.Affinity!.SetAffinity("sess-86", new RoutingTarget("Desktop"));
        harness.AdvanceClock(TimeSpan.FromMinutes(31)); // default 30-min TTL lapsed; table ≤ 100 ⇒ no sweep

        using var response = await ReleaseAsync(harness.Client, "sess-86");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.False(node["released"]!.GetValue<bool>());
        Assert.False(harness.Affinity.TryGetAffinity("sess-86", out _), "the expired entry must be evicted");
    }

    [Fact]
    public async Task Release_MissingHeader_Returns400_WithExactErrorMessage()
    {
        await using var harness = await CreateAppAsync(registerAffinity: true);

        using var response = await ReleaseAsync(harness.Client, headerValue: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("X-OllamaRouter-Session-Id header is required", node["error"]!.ToString());
    }

    [Fact]
    public async Task Release_WhitespaceOnlyHeader_Returns400_WithExactErrorMessage()
    {
        await using var harness = await CreateAppAsync(registerAffinity: true);

        using var response = await ReleaseAsync(harness.Client, "   ");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("X-OllamaRouter-Session-Id header is required", node["error"]!.ToString());
    }

    [Fact]
    public async Task Release_OversizedHeader_Returns200ReleasedFalse_Never400_AndEchoesFullValue()
    {
        await using var harness = await CreateAppAsync(registerAffinity: true);
        var oversized = new string('x', OllamaReverseProxyConfig.PublicSessionIdMaxLength + 1); // 257 chars

        using var response = await ReleaseAsync(harness.Client, oversized);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(oversized, node["sessionId"]!.ToString()); // the full trimmed value is echoed
        Assert.False(node["released"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Release_PaddedHeader_IsTrimmed_AndReleasesTheTrimmedLease()
    {
        await using var harness = await CreateAppAsync(registerAffinity: true);
        harness.Affinity!.SetAffinity("sess-86", new RoutingTarget("Desktop"));

        using var response = await ReleaseAsync(harness.Client, "  sess-86  ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("sess-86", node["sessionId"]!.ToString()); // trimmed echo
        Assert.True(node["released"]!.GetValue<bool>());
        Assert.False(harness.Affinity.TryGetAffinity("sess-86", out _));
    }

    [Fact]
    public async Task Release_AffinityStackNotWired_Returns200ReleasedFalse_MissingHeaderStill400()
    {
        await using var harness = await CreateAppAsync(registerAffinity: false);
        Assert.Null(harness.Affinity);

        using var released = await ReleaseAsync(harness.Client, "sess-86");
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        var node = JsonNode.Parse(await released.Content.ReadAsStringAsync())!;
        Assert.Equal("sess-86", node["sessionId"]!.ToString());
        Assert.False(node["released"]!.GetValue<bool>());

        using var missing = await ReleaseAsync(harness.Client, headerValue: null);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }
}
