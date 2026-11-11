using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Endpoints.RouterApi.Admin;
using OllamaRouter.Endpoints.RouterApi.Info;
using OllamaRouter.Middleware;
using OllamaRouter.Options;
using OllamaRouter.Security;
using OllamaRouter.Serialization;
using OllamaRouter.Tests.TestHelpers;

namespace OllamaRouter.Tests.Endpoints;

/// <summary>
/// Host tests for the key-protected <c>POST /api/router/admin/shutdown</c> endpoint (task #99):
/// a dedicated <see cref="WebApplication"/> host with the production <see cref="ApiKeyAuthMiddleware"/>,
/// a REAL <see cref="ApiKeyStore"/> on a temp content root and the REAL
/// <see cref="IHostApplicationLifetime"/> (replacing that registration is not supported by the
/// modern host — instead a <see cref="StopSignalRecorder"/> registers on the real
/// <c>ApplicationStopping</c> token: <c>StopApplication()</c> cancels it, so token firing IS
/// the recording). Verifies: unauthenticated → 401 + <c>WWW-Authenticate: Bearer</c> + JSON
/// body (stop NOT triggered); Bearer default-key and <c>X-Api-Key</c> forms → 202 with
/// <c>{"stopping":true,"processId":...}</c> AND the stop signal fires (response-first: it
/// fires on <c>Response.OnStarting</c>, i.e. after the complete 202 body was delivered); the
/// trailing-slash variant is protected + accepted identically.
/// </summary>
public class RouterAdminShutdownEndpointsTests
{
    /// <summary>
    /// Records whether the real host's <c>ApplicationStopping</c> token was cancelled — i.e.
    /// whether <c>IHostApplicationLifetime.StopApplication()</c> was invoked.
    /// </summary>
    private sealed class StopSignalRecorder
    {
        private readonly TaskCompletionSource _fired =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _registration;

        public bool HasFired => _fired.Task.IsCompleted;

        public void Attach(IHostApplicationLifetime lifetime) =>
            _registration = lifetime.ApplicationStopping.Register(static (object? state) =>
                ((StopSignalRecorder)state!)._fired.TrySetResult(), this);

        /// <summary>Waits until the stop signal fired (bounded — never hangs the suite).</summary>
        public async Task WaitFiredAsync()
        {
            await _fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public void Dispose() => _registration.Dispose();
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(WebApplication app, HttpClient client, IApiKeyStore keys, StopSignalRecorder recorder, string root)
        {
            App = app;
            Client = client;
            Keys = keys;
            Recorder = recorder;
            Root = root;
        }

        public WebApplication App { get; }

        public HttpClient Client { get; }

        public IApiKeyStore Keys { get; }

        public StopSignalRecorder Recorder { get; }

        public string Root { get; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                // Idempotent even after the endpoint already requested a stop.
                await App.StopAsync();
                await App.DisposeAsync();
            }
            finally
            {
                Recorder.Dispose();
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
    }

    private static async Task<Harness> CreateTestAppAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ollamarouter-admin-shutdown-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = root
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.ConfigureHttpJsonOptions(opt =>
            opt.SerializerOptions.TypeInfoResolverChain.Insert(0, OllamaRouterJsonSerializerContext.Default));

        var optionsMonitor = new TestOptionsMonitor<OllamaRouterOptions>(new OllamaRouterOptions());
        builder.Services.AddSingleton<IOptionsMonitor<OllamaRouterOptions>>(optionsMonitor);

        // The REAL key store on the temp content root (config/default-key.dat is generated
        // there; PlainKeyProtector keeps the envelope plain on any OS — no DPAPI). Resolved
        // lazily so the store's IServiceProvider (IHostEnvironment) is the built one.
        builder.Services.AddSingleton<IApiKeyStore>(sp =>
            new ApiKeyStore(optionsMonitor, sp, new PlainKeyProtector()));

        var app = builder.Build();
        var keys = app.Services.GetRequiredService<IApiKeyStore>();

        app.UseMiddleware<ApiKeyAuthMiddleware>();
        app.MapRouterInfoEndpoints();
        app.MapRouterAdminEndpoints();
        await app.StartAsync();

        var recorder = new StopSignalRecorder();
        recorder.Attach(app.Lifetime);

        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromSeconds(15) };
        return new Harness(app, client, keys, recorder, root);
    }

    // ------------------------------------------------------------------ tests

    [Fact]
    public async Task Shutdown_Unauthenticated_Returns401_WithBearerChallenge_AndDoesNotStop()
    {
        await using var harness = await CreateTestAppAsync();

        using var response = await harness.Client.PostAsync("/api/router/admin/shutdown", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate?.FirstOrDefault()?.Scheme);
        var body = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.False(string.IsNullOrWhiteSpace(body["error"]?.ToString()), "401 must carry a JSON error body");
        await Task.Delay(250); // give a (non-existent) stop signal time to fire — it must not
        Assert.False(harness.Recorder.HasFired, "a rejected request must not trigger the host stop");
    }

    [Fact]
    public async Task Shutdown_BearerDefaultKey_Returns202_WithStoppingAndProcessId_AndStopsTheHost()
    {
        await using var harness = await CreateTestAppAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/router/admin/shutdown");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", harness.Keys.GetDefaultKey());
        using (var response = await harness.Client.SendAsync(request))
        {
            // The COMPLETE 202 body is delivered BEFORE the host tears down.
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var body = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Assert.Equal("true", body["stopping"]!.ToString());
            Assert.Equal(Environment.ProcessId.ToString(), body["processId"]!.ToString());
        }

        // Response-first stop: the signal fires (on Response.OnStarting) after the 202.
        await harness.Recorder.WaitFiredAsync();
        Assert.True(harness.Recorder.HasFired);
    }

    [Fact]
    public async Task Shutdown_XApiKeyHeaderForm_Accepted_With202_AndStops()
    {
        await using var harness = await CreateTestAppAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/router/admin/shutdown");
        request.Headers.Add("X-Api-Key", harness.Keys.GetDefaultKey());
        using (var response = await harness.Client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var body = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Assert.Equal("true", body["stopping"]!.ToString());
        }

        await harness.Recorder.WaitFiredAsync();
    }

    [Fact]
    public async Task Shutdown_TrailingSlashVariant_IsProtectedAndAccepted_With202()
    {
        await using var harness = await CreateTestAppAsync();

        // Trailing slash: still protected (unauthenticated → 401)…
        using (var unauthorized = await harness.Client.PostAsync("/api/router/admin/shutdown/", content: null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        }

        // …and accepted with the key (202), matching endpoint-routing trailing-slash semantics.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/router/admin/shutdown/");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", harness.Keys.GetDefaultKey());
        using (var response = await harness.Client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        await harness.Recorder.WaitFiredAsync();
    }

    [Fact]
    public async Task Shutdown_InvalidKey_Returns401_AndDoesNotStop()
    {
        await using var harness = await CreateTestAppAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/router/admin/shutdown");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "wrong-key");
        using var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await Task.Delay(250);
        Assert.False(harness.Recorder.HasFired, "an invalid key must not trigger the host stop");
    }
}
