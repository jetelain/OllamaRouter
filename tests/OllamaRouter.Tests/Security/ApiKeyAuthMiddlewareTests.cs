using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Extensions;
using OllamaRouter.Options;
using OllamaRouter.Security;
using OllamaRouter.Tests.TestHelpers;

namespace OllamaRouter.Tests.Security;

/// <summary>
/// Full-pipeline tests for the API key enforcement middleware (task #50): the production
/// pipeline (AddOllamaRouter instance overload + UseOllamaRouterPipeline) on a temp content
/// root. Covers the 401/2xx matrix (no key / bad key / expired user key / default key / user
/// key) for the protected routes, both header forms (Authorization: Bearer and X-Api-Key),
/// the 401 shape (WWW-Authenticate: Bearer + JSON error body), the key-management CRUD over
/// HTTP, the manual default-rotation endpoint and the LIVE RequireKeyForAllEndpoints toggle
/// (every endpoint incl. /monitor, proxy paths, POST /targets requires a key; toggling back
/// re-opens them). All hosts are in-process loopback on ephemeral ports; no sleeps beyond the
/// immediate startup; the rotation is never waited on (the 5-min timer is out of scope here).
/// </summary>
public class ApiKeyAuthMiddlewareTests
{
    private sealed class Host : IAsyncDisposable
    {
        public Host(WebApplication app, HttpClient client, TestOptionsMonitor<OllamaRouterOptions> monitor, OllamaRouterOptions options, IServiceProvider services, string root)
        {
            App = app;
            Client = client;
            Monitor = monitor;
            Options = options;
            Services = services;
            Root = root;
        }

        public WebApplication App { get; }

        public HttpClient Client { get; }

        public TestOptionsMonitor<OllamaRouterOptions> Monitor { get; }

        public OllamaRouterOptions Options { get; }

        public IServiceProvider Services { get; }

        public string Root { get; }

        public IApiKeyStore Keys => Services.GetRequiredService<IApiKeyStore>();

        public async ValueTask DisposeAsync()
        {
            try
            {
                await App.StopAsync();
                await App.DisposeAsync();
            }
            finally
            {
                try
                {
                    if (Directory.Exists(Root))
                    {
                        Directory.Delete(Root, recursive: true);
                    }
                }
                catch
                {
                    // Temp cleanup must never mask the test outcome.
                }
            }
        }

        public static async Task<Host> StartAsync(Action<string>? preseedContentRoot = null)
        {
            var root = Path.Combine(Path.GetTempPath(), $"ollamarouter-keyauth-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            preseedContentRoot?.Invoke(root);

            var options = new OllamaRouterOptions
            {
                BindAddress = "http://127.0.0.1:0",
                Targets =
                [
                    new OllamaTargetOptions
                    {
                        Name = "Dummy",
                        Url = "http://127.0.0.1:59999", // never dialed by the tested paths
                        MaxParallel = 1
                    }
                ],
                TerminalMonitoring = new TerminalMonitoringOptions { Enabled = false }
            };

            var monitor = new TestOptionsMonitor<OllamaRouterOptions>(options);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = [],
                ContentRootPath = root
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            // The caller-registered monitor wins over the FixedOptionsMonitor fallback, so the
            // RequireKeyForAllEndpoints LIVE toggle is observable per request.
            builder.Services.AddSingleton<IOptionsMonitor<OllamaRouterOptions>>(monitor);
            builder.Services.AddOllamaRouter(
                options,
                stateDirectory: Path.Combine(root, "state"),
                enableTui: false,
                reprobeInterval: TimeSpan.FromSeconds(10),
                onlineCacheTtl: TimeSpan.FromSeconds(10));

            var app = builder.Build();
            app.UseOllamaRouterPipeline();
            await app.StartAsync();
            var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromSeconds(15) };
            return new Host(app, client, monitor, options, app.Services, root);
        }
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static HttpRequestMessage Request(string method, string path, string? body = null, string? key = null, bool useXApiKeyHeader = false)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        if (key is not null)
        {
            if (useXApiKeyHeader)
            {
                request.Headers.Add("X-Api-Key", key);
            }
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }
        }

        return request;
    }

    private static async Task<(int Status, string? WwwAuthenticate, JsonNode? Json)> SendAsync(HttpClient client, HttpRequestMessage request)
    {
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        string? wwwAuthenticate = null;
        if (response.Headers.TryGetValues("WWW-Authenticate", out var wwwValues))
        {
            wwwAuthenticate = wwwValues.FirstOrDefault();
        }

        JsonNode? json = null;
        if (text.Length > 0)
        {
            try
            {
                json = JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException)
            {
                // Non-JSON body (e.g. the HTML monitor page): callers that need JSON assert on it.
            }
        }

        return (
            (int)response.StatusCode,
            wwwAuthenticate,
            json);
    }

    // ------------------------------------------------------------------
    //  Default protected set: 401 without a key
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "/api/router/config", null)]
    [InlineData("PUT", "/api/router/config", """{"resilience": { "maxConsecutiveFailures": 2 }}""")]
    [InlineData("POST", "/api/router/targets", """{"name": "x", "url": "http://127.0.0.1:1"}""")]
    [InlineData("PUT", "/api/router/targets/Dummy", """{"enabled": false}""")]
    [InlineData("DELETE", "/api/router/targets/Dummy", null)]
    [InlineData("PUT", "/api/router/fleet/hardware/Dummy", """{"nodeName": "Dummy"}""")]
    [InlineData("POST", "/api/router/benchmark", "{}")]
    [InlineData("DELETE", "/api/router/benchmark/results", null)]
    [InlineData("GET", "/api/router/api-keys", null)]
    [InlineData("POST", "/api/router/api-keys", """{"name": "ci"}""")]
    [InlineData("DELETE", "/api/router/api-keys/ci", null)]
    [InlineData("POST", "/api/router/api-keys/default/rotate", null)]
    public async Task ProtectedRoute_WithoutKey_Returns401_WithWwwAuthenticateAndJsonError(string method, string path, string? body)
    {
        await using var host = await Host.StartAsync();

        var (status, wwwAuthenticate, json) = await SendAsync(
            host.Client, Request(method, path, body));

        Assert.Equal(401, status);
        Assert.Equal("Bearer", wwwAuthenticate);
        var error = (json! as JsonObject)!["error"];
        Assert.NotNull(error);
        Assert.False(error!.ToString().Contains(host.Keys.GetDefaultKey(), StringComparison.Ordinal),
            "the 401 body must never echo key material");
    }

    [Fact]
    public async Task ProtectedRoute_WithInvalidKey_Returns401()
    {
        await using var host = await Host.StartAsync();
        var wrongKey = ApiKeyCrypto.GenerateKey(); // well-formed, but not stored anywhere

        var (status1, _, _) = await SendAsync(host.Client, Request("GET", "/api/router/config", null, wrongKey));
        Assert.Equal(401, status1);

        var (status2, _, _) = await SendAsync(
            host.Client, Request("GET", "/api/router/config", null, wrongKey, useXApiKeyHeader: true));
        Assert.Equal(401, status2);

        // A structurally invalid value is rejected as well.
        var (status3, _, _) = await SendAsync(host.Client, Request("GET", "/api/router/config", null, "short"));
        Assert.Equal(401, status3);
    }

    [Fact]
    public async Task OpenRoutes_WithoutKey_RemainOpen_InDefaultMode()
    {
        await using var host = await Host.StartAsync();

        var (info, _, _) = await SendAsync(host.Client, Request("GET", "/api/router/info"));
        Assert.Equal(200, info);

        var (fleet, _, _) = await SendAsync(host.Client, Request("GET", "/api/router/fleet"));
        Assert.Equal(200, fleet);

        // POST /targets (monitor availability toggle) stays open in default mode: it must NOT
        // be a 401 (it may still 400/200 on the body itself).
        var (targets, _, _) = await SendAsync(host.Client, Request("POST", "/targets", """{"targets": []}"""));
        Assert.NotEqual(401, targets);

        var (monitor, _, _) = await SendAsync(host.Client, Request("GET", "/monitor"));
        Assert.Equal(200, monitor);
    }

    [Fact]
    public async Task ValidDefaultKey_BearerAndXApiKeyHeaders_AreBothAccepted()
    {
        await using var host = await Host.StartAsync();
        var key = host.Keys.GetDefaultKey();

        var (bearer, _, bearerJson) = await SendAsync(host.Client, Request("GET", "/api/router/config", null, key));
        Assert.Equal(200, bearer);
        Assert.NotNull(bearerJson);

        var (xApiKey, _, xApiKeyJson) = await SendAsync(
            host.Client, Request("GET", "/api/router/config", null, key, useXApiKeyHeader: true));
        Assert.Equal(200, xApiKey);
        Assert.NotNull(xApiKeyJson);

        // A write route as well (PUT /api/router/config is protected).
        var (put, _, _) = await SendAsync(
            host.Client, Request("PUT", "/api/router/config", """{"resilience": { "maxConsecutiveFailures": 4 }}""", key));
        Assert.Equal(200, put);
        Assert.Equal(4, host.Options.MaxConsecutiveFailures);
    }

    // ------------------------------------------------------------------
    //  User key lifecycle over HTTP
    // ------------------------------------------------------------------

    [Fact]
    public async Task UserKeyLifecycle_CreateAuthenticateListDelete()
    {
        await using var host = await Host.StartAsync();
        var defaultKey = host.Keys.GetDefaultKey();

        // Create: 201 + the raw key exactly once.
        var (createStatus, _, createJson) = await SendAsync(
            host.Client, Request("POST", "/api/router/api-keys", """{"name": "ci", "validFor": 168}""", defaultKey));
        Assert.Equal(201, createStatus);
        var created = (createJson! as JsonObject)!;
        Assert.True(created["success"]!.GetValue<bool>());
        var userKey = created["key"]!.GetValue<string>();
        Assert.True(ApiKeyCrypto.IsValidApiKeyFormat(userKey));
        Assert.NotNull(created["createdAt"]);
        Assert.NotNull(created["expiresAt"]);

        // The freshly created user key authenticates a protected route.
        var (getWithUser, _, _) = await SendAsync(host.Client, Request("GET", "/api/router/config", null, userKey));
        Assert.Equal(200, getWithUser);

        // List: masked — never the raw key, never a hash.
        var (listStatus, _, listJson) = await SendAsync(
            host.Client, Request("GET", "/api/router/api-keys", null, defaultKey));
        Assert.Equal(200, listStatus);
        var listText = (listJson! as JsonObject)!.ToJsonString();
        var row = ((JsonArray)((listJson as JsonObject)!["keys"]!))!;
        Assert.Single(row);
        var entry = (row[0]! as JsonObject)!;
        Assert.Equal("ci", entry["name"]!.GetValue<string>());
        Assert.Null(entry["hash"]);
        Assert.False(listText.Contains(userKey, StringComparison.Ordinal), "the list must never expose the raw key");

        // Delete: 200; the deleted key stops working; unknown delete → 404.
        var (deleteStatus, _, deleteJson) = await SendAsync(
            host.Client, Request("DELETE", "/api/router/api-keys/ci", null, defaultKey));
        Assert.Equal(200, deleteStatus);
        Assert.True((deleteJson! as JsonObject)!["success"]!.GetValue<bool>());

        var (afterDelete, _, _) = await SendAsync(host.Client, Request("GET", "/api/router/config", null, userKey));
        Assert.Equal(401, afterDelete);

        var (deleteUnknown, _, deleteUnknownJson) = await SendAsync(
            host.Client, Request("DELETE", "/api/router/api-keys/nope", null, defaultKey));
        Assert.Equal(404, deleteUnknown);
        Assert.False((deleteUnknownJson! as JsonObject)!["success"]!.GetValue<bool>());
    }

    [Fact]
    public async Task UserKeyCreate_DuplicateName_Returns409_InvalidInput_Returns400()
    {
        await using var host = await Host.StartAsync();
        var defaultKey = host.Keys.GetDefaultKey();

        var (first, _, _) = await SendAsync(
            host.Client, Request("POST", "/api/router/api-keys", """{"name": "dup"}""", defaultKey));
        Assert.Equal(201, first);

        var (duplicate, _, _) = await SendAsync(
            host.Client, Request("POST", "/api/router/api-keys", """{"name": "DUP"}""", defaultKey));
        Assert.Equal(409, duplicate);

        var (badValidFor, _, _) = await SendAsync(
            host.Client, Request("POST", "/api/router/api-keys", """{"name": "x", "validFor": 0}""", defaultKey));
        Assert.Equal(400, badValidFor);

        var (badName, _, _) = await SendAsync(
            host.Client, Request("POST", "/api/router/api-keys", """{"name": "bad name"}""", defaultKey));
        Assert.Equal(400, badName);

        var (badBody, _, _) = await SendAsync(
            host.Client, Request("POST", "/api/router/api-keys", "not json", defaultKey));
        Assert.Equal(400, badBody);
    }

    [Fact]
    public async Task UserKeyExpiry_PreseededPastKey_Returns401_FutureKey_Returns200()
    {
        // Pre-seed config/api-keys.json with two known keys (past expiry / future expiry)
        // BEFORE the host starts, so the store's lazy load picks them up.
        var pastKey = ApiKeyCrypto.GenerateKey();
        var futureKey = ApiKeyCrypto.GenerateKey();
        await using var host = await Host.StartAsync(root =>
        {
            var configDir = Path.Combine(root, "config");
            Directory.CreateDirectory(configDir);
            var keys = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "past",
                    ["hash"] = Convert.ToBase64String(ApiKeyCrypto.HashKey(pastKey)),
                    ["createdAt"] = DateTimeOffset.UtcNow.AddDays(-2).ToString("O"),
                    ["expiresAt"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O")
                },
                new JsonObject
                {
                    ["name"] = "future",
                    ["hash"] = Convert.ToBase64String(ApiKeyCrypto.HashKey(futureKey)),
                    ["createdAt"] = DateTimeOffset.UtcNow.AddHours(-1).ToString("O"),
                    ["expiresAt"] = DateTimeOffset.UtcNow.AddDays(1).ToString("O")
                }
            };
            File.WriteAllText(
                Path.Combine(configDir, "api-keys.json"),
                new JsonObject { ["version"] = 1, ["keys"] = keys }.ToJsonString());
        });

        var (past, _, _) = await SendAsync(host.Client, Request("GET", "/api/router/config", null, pastKey));
        Assert.Equal(401, past);

        var (future, _, _) = await SendAsync(host.Client, Request("GET", "/api/router/config", null, futureKey));
        Assert.Equal(200, future);
    }

    // ------------------------------------------------------------------
    //  Default key rotation endpoint
    // ------------------------------------------------------------------

    [Fact]
    public async Task DefaultRotateEndpoint_RotatesImmediately_OldKeyDead_NoGracePeriod()
    {
        await using var host = await Host.StartAsync();
        var oldKey = host.Keys.GetDefaultKey();

        var (rotateStatus, _, rotateJson) = await SendAsync(
            host.Client, Request("POST", "/api/router/api-keys/default/rotate", null, oldKey));
        Assert.Equal(200, rotateStatus);
        var rotated = (rotateJson! as JsonObject)!;
        Assert.True(rotated["success"]!.GetValue<bool>());
        var newKey = rotated["key"]!.GetValue<string>();
        Assert.True(ApiKeyCrypto.IsValidApiKeyFormat(newKey));
        Assert.NotEqual(oldKey, newKey);
        Assert.NotNull(rotated["generatedAt"]);

        var (oldNow, _, _) = await SendAsync(host.Client, Request("GET", "/api/router/config", null, oldKey));
        Assert.Equal(401, oldNow);

        var (newWorks, _, _) = await SendAsync(host.Client, Request("GET", "/api/router/config", null, newKey));
        Assert.Equal(200, newWorks);
        Assert.Equal(newKey, host.Keys.GetDefaultKey());
    }

    // ------------------------------------------------------------------
    //  RequireKeyForAllEndpoints LIVE toggle
    // ------------------------------------------------------------------

    [Fact]
    public async Task RequireKeyForAllEndpoints_LiveToggle_ProtectsEveryRoute_ThenReopens()
    {
        await using var host = await Host.StartAsync();
        var key = host.Keys.GetDefaultKey();

        // Default mode: reads / monitor / toggle / proxy paths are all open.
        var (openBefore, _, _) = await SendAsync(host.Client, Request("GET", "/api/router/fleet"));
        Assert.Equal(200, openBefore);
        var (monitorBefore, _, _) = await SendAsync(host.Client, Request("GET", "/monitor"));
        Assert.Equal(200, monitorBefore);

        // LIVE switch: mutate the shared options instance (the middleware reads
        // IOptionsMonitor.CurrentValue per request — no restart).
        host.Options.ApiSecurity.RequireKeyForAllEndpoints = true;

        var (fleetLocked, www, _) = await SendAsync(host.Client, Request("GET", "/api/router/fleet"));
        Assert.Equal(401, fleetLocked);
        Assert.Equal("Bearer", www);

        var (monitorLocked, _, _) = await SendAsync(host.Client, Request("GET", "/monitor"));
        Assert.Equal(401, monitorLocked);

        var (targetsLocked, _, _) = await SendAsync(host.Client, Request("POST", "/targets", """{"targets": []}"""));
        Assert.Equal(401, targetsLocked);

        var (proxyLocked, _, _) = await SendAsync(host.Client, Request("GET", "/api/tags"));
        Assert.Equal(401, proxyLocked);

        var (chatLocked, _, _) = await SendAsync(host.Client, Request("POST", "/api/chat", """{"model": "m", "prompt": "x"}"""));
        Assert.Equal(401, chatLocked);

        // The key opens everything again.
        var (fleetWithKey, _, _) = await SendAsync(host.Client, Request("GET", "/api/router/fleet", null, key));
        Assert.Equal(200, fleetWithKey);
        var (monitorWithKey, _, _) = await SendAsync(host.Client, Request("GET", "/monitor", null, key));
        Assert.Equal(200, monitorWithKey);

        // Toggle back: the routes re-open WITHOUT a key (live, no restart).
        host.Options.ApiSecurity.RequireKeyForAllEndpoints = false;
        var (openAfter, _, _) = await SendAsync(host.Client, Request("GET", "/api/router/fleet"));
        Assert.Equal(200, openAfter);
    }
}
