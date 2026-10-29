using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OllamaRouter.Tests.Mesh;

/// <summary>
/// The per-request chat behavior of a <see cref="FakeOllamaDaemon"/> (plan §3 daemon capability
/// knobs). <see cref="Ok"/> streams a small, complete NDJSON/SSE response; <see cref="AbortBeforeCommit"/>
/// resets the socket before any response byte (an uncommitted transport failure → failover);
/// <see cref="StreamNThenAbort"/> commits the headers + <c>N</c> NDJSON chunks and then resets the
/// socket (a mid-stream failure → streak fed, no retry); <see cref="Hang"/> delays the response body
/// (a latency spike against the probe/aggregation windows).
/// </summary>
public enum ChatMode
{
    Ok,
    AbortBeforeCommit,
    StreamNThenAbort,
    Hang
}

/// <summary>A model entry advertised by a <see cref="FakeOllamaDaemon"/> (catalog + capability fixture).</summary>
public sealed class DaemonModel
{
    public required string Name { get; init; }
    public long SizeBytes { get; init; } = 1_000_000_000;
    /// <summary>Parameter count surfaced in <c>model_info["general.parameter_count"]</c> (drives the dense/MoE split).</summary>
    public long ParameterCount { get; init; }
    /// <summary>Expert count surfaced as <c>model_info["&lt;family&gt;.expert_count"]</c> (MoE signal when &gt; 0).</summary>
    public long ExpertCount { get; init; }
    /// <summary>Optional explicit Modelfile <c>num_ctx</c> surfaced in the <c>parameters</c> block (caps C_max).</summary>
    public int? ModelfileNumCtx { get; init; }
    /// <summary>Optional native GGUF context length surfaced as <c>model_info["&lt;family&gt;.context_length"]</c>.</summary>
    public long? NativeContextLength { get; init; }
    public string Family { get; init; } = "llama";
}

/// <summary>A recorded request observed by a <see cref="FakeOllamaDaemon"/>.</summary>
public sealed record RecordedRequest(
    EndPoint? SourceEndpoint,
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Headers,
    string Body,
    DateTimeOffset TimestampUtc);

/// <summary>
/// A deterministic in-process fake Ollama daemon: a real Kestrel <see cref="WebApplication"/> on an
/// ephemeral loopback port implementing the production Ollama API surface the router actually
/// consumes (<c>GET /</c> liveness, <c>GET /api/tags</c>, <c>POST /api/show</c>, <c>GET /api/ps</c>,
/// <c>POST /api/chat</c> NDJSON streaming, <c>POST /v1/chat/completions</c> SSE). Every inbound
/// request is recorded with its <see cref="RecordedRequest.SourceEndpoint"/> (the remote/source
/// endpoint of the connection), method, path, headers and body — the observation plane for the
/// loop-free assertion (plan §5) and for asserting the exact rewritten <c>options.num_ctx</c> the
/// daemon received.
/// <para>
/// Fault-injection knobs: <see cref="StopAsync"/> (clean stop), <see cref="StartAsync"/> (restart on the
/// SAME port → crash+restart), <see cref="SetChatBehavior"/> (uncommitted / mid-stream aborts, latency
/// spikes). <c>GET /api/ps</c> reflects a "loaded models" set that the daemon grows after serving a
/// chat (simulating a model load) and, at capacity (single-slot daemons), evicts the oldest model.
/// </para>
/// </summary>
public sealed class FakeOllamaDaemon : IAsyncDisposable
    {
        /// <summary>
        /// Settle delay applied between the last committed <c>StreamNThenAbort</c> chunk and the
        /// socket reset. Loopback sockets can deliver the RST before the reader has drained the
        /// just-flushed bytes; the delay makes the "committed then aborted" ordering deterministic
        /// so the router's dispatch loop always observes a committed (mid-stream) failure rather
        /// than an uncommitted one. Overridable for tests that need a different race profile.
        /// </summary>
        public static TimeSpan StreamAbortSettleMs { get; set; } = TimeSpan.FromMilliseconds(150);

    private readonly string _name;
    private readonly IReadOnlyList<DaemonModel> _models;
    private readonly int _capacity;
    private readonly int _port;

    private WebApplication? _app;
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly List<string> _loaded = new();
    private readonly object _loadedLock = new();

    private volatile ChatMode _chatMode = ChatMode.Ok;
    private volatile int _chatModeArg;
    private volatile int _livenessDelayMs;
    private volatile int _chatStreamDelayMs;
    private volatile int _abortCount;

    /// <summary>Diagnostic: number of times the chat handler took an abort path (AbortBeforeCommit / StreamNThenAbort).</summary>
    public int AbortCount => _abortCount;

    public FakeOllamaDaemon(string name, IReadOnlyList<DaemonModel> models, int capacity)
    {
        _name = name;
        _models = models;
        _capacity = capacity;
        _port = AllocateLoopbackPort();
    }

    public string Name => _name;

    /// <summary>The daemon's loopback base address (stable across crash+restart on the same port).</summary>
    public string Url => $"http://127.0.0.1:{_port}";

    /// <summary>The daemon's loopback port (the loop-free config-invariant checks target only these).</summary>
    public int Port => _port;

    /// <summary>Hardware class VRAM in MB surfaced to the router; set by the fleet fixture.</summary>
    public int? MaxVramMB { get; set; }

    /// <summary>Max concurrent requests surfaced to the router; set by the fleet fixture.</summary>
    public int MaxParallel { get; set; } = 1;

    public bool IsRunning { get; private set; }

    public IReadOnlyList<RecordedRequest> Requests => _requests.ToList();

    public IReadOnlyList<string> LoadedModels
    {
        get { lock (_loadedLock) { return _loaded.ToList(); } }
    }

    public IReadOnlyList<DaemonModel> Models => _models;

    // ------------------------------------------------------------------
    //  Fault-injection knobs
    // ------------------------------------------------------------------

    public void SetChatBehavior(ChatMode mode, int arg = 0)
    {
        _chatMode = mode;
        _chatModeArg = arg;
    }

    /// <summary>Applies a latency to <c>GET /</c> (liveness probe) — a latency spike against the 1.5 s probe timeout.</summary>
    public void SetLivenessDelay(TimeSpan delay) => _livenessDelayMs = (int)delay.TotalMilliseconds;

    /// <summary>Applies a delay before the chat response body is written (holds the request in flight on the router).</summary>
    public void SetChatStreamDelay(TimeSpan delay) => _chatStreamDelayMs = (int)delay.TotalMilliseconds;

    public void ResetBehavior()
    {
        _chatMode = ChatMode.Ok;
        _chatModeArg = 0;
        _livenessDelayMs = 0;
        _chatStreamDelayMs = 0;
        _abortCount = 0;
        lock (_loadedLock)
        {
            _loaded.Clear();
        }
    }

    public void ResetRequests()
    {
        while (_requests.TryDequeue(out _))
        {
        }
    }

    /// <summary>Loads (or reloads) the given models into the daemon's "loaded" set without serving a chat (preload helper).</summary>
    public void PreloadModel(string modelName)
    {
        LoadModel(modelName);
    }

    public Task StartAsync() => StartAsync(CancellationToken.None);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsRunning)
        {
            return;
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(Url);
        builder.Logging.ClearProviders();

        // Observation plane: record every inbound request (source endpoint + method + path + headers + body).


        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            EndPoint? source = context.Connection.RemoteIpAddress is not null
                ? new IPEndPoint(context.Connection.RemoteIpAddress, context.Connection.RemotePort)
                : null;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in context.Request.Headers)
            {
                headers[key] = value.ToString();
            }

            string body = string.Empty;
            if (context.Request.Method != HttpMethods.Get)
            {
                context.Request.EnableBuffering();
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
                body = await reader.ReadToEndAsync(cancellationToken);
                context.Request.Body.Position = 0;
            }

            _requests.Enqueue(new RecordedRequest(source, context.Request.Method, context.Request.Path.Value ?? "/", headers, body, DateTimeOffset.UtcNow));
            await next(context);
        });

        app.MapGet("/", async context =>
        {
            var delay = _livenessDelayMs;
            if (delay > 0)
            {
                try
                {
                    await Task.Delay(delay, context.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("Ollama is running", context.RequestAborted);
        });

        app.MapGet("/api/tags", async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(TagsJson(), context.RequestAborted);
        });

        app.MapGet("/api/ps", async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(PsJson(), context.RequestAborted);
        });

        app.MapPost("/api/show", async context =>
        {
            context.Response.ContentType = "application/json";
            var model = ReadModel(context.Request);
            var show = ShowJson(model);
            await context.Response.WriteAsync(show, context.RequestAborted);
        });

        app.MapPost("/api/chat", async context =>
        {
            var model = ReadModel(context.Request);
            await WriteChatNdJsonAsync(context, model, context.RequestAborted);
        });

        app.MapPost("/v1/chat/completions", async context =>
        {
            var model = ReadModel(context.Request);
            await WriteChatSseAsync(context, model, context.RequestAborted);
        });

        await app.StartAsync(cancellationToken);
        _app = app;
        IsRunning = true;

        // Wait until the daemon actually answers liveness (the port is bound but Kestrel may take a moment).
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var i = 0; i < 200; i++)
        {
            try
            {
                var response = await probe.GetAsync($"{Url}/", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
                // Not ready yet.
            }

            await Task.Delay(10, cancellationToken);
        }
    }

    public async Task StopAsync()
    {
        if (_app is not null)
        {
            var app = _app;
            _app = null;
            IsRunning = false;
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    // ------------------------------------------------------------------
    //  Recording / observation helpers
    // ------------------------------------------------------------------

    /// <summary>All recorded <c>POST /api/chat</c> requests (the chat-hit counting surface).</summary>
    public IReadOnlyList<RecordedRequest> ChatHits() =>
        Requests.Where(r => r.Method == "POST" && r.Path.StartsWith("/api/chat", StringComparison.Ordinal)).ToList();

    /// <summary>The recorded <c>POST /api/chat</c> request body for <paramref name="model"/> (most recent), or null.</summary>
    public string? LastChatBody(string model) =>
        ChatHits().Where(r => ReadModelFromBody(r.Body) is { } m && string.Equals(m, model, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Body).LastOrDefault();

    /// <summary>
    /// The <c>options.num_ctx</c> value the daemon received in the most recent <c>POST /api/chat</c>
    /// body for <paramref name="model"/> (null when the body carried no num_ctx or no such hit).
    /// </summary>
    public int? LastReceivedNumCtx(string model)
    {
        var body = LastChatBody(model);
        if (body is null)
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(body) is JsonObject root && root["options"] is JsonObject options &&
                options["num_ctx"] is JsonValue value && value.TryGetValue(out int? numCtx))
            {
                return numCtx;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    // ------------------------------------------------------------------
    //  Internals
    // ------------------------------------------------------------------

    /// <summary>Loads (or reloads) the given model into the daemon's "loaded" set, evicting the
    /// least-recently-used model when the capacity is exceeded (single-slot daemons evict the previous model).</summary>
    private void LoadModel(string modelName)
    {
        lock (_loadedLock)
        {
            _loaded.Remove(modelName);
            _loaded.Add(modelName); // most-recently-used at the end
            while (_loaded.Count > _capacity)
            {
                _loaded.RemoveAt(0); // evict the least-recently-used (single-slot eviction)
            }
        }
    }

    private static string ReadModel(HttpRequest request)
    {
        // The observation middleware already buffered non-GET bodies and rewound Position.
        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        var body = reader.ReadToEnd();
        request.Body.Position = 0;
        return ReadModelFromBody(body);
    }

    private static string? ReadModelFromBody(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is JsonObject root && root["model"] is JsonValue v && v.TryGetValue(out string? s))
            {
                return s;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private string TagsJson()
    {
        var models = new JsonArray();
        foreach (var m in _models)
        {
            models.Add(new JsonObject
            {
                ["name"] = m.Name,
                ["model"] = m.Name,
                ["size"] = m.SizeBytes,
                ["details"] = new JsonObject { ["family"] = m.Family, ["parameter_size"] = FormatParams(m.ParameterCount) }
            });
        }

        return new JsonObject { ["models"] = models }.ToJsonString();
    }

    private string PsJson()
    {
        lock (_loadedLock)
        {
            var models = new JsonArray();
            for (var i = 0; i < _loaded.Count; i++)
            {
                var name = _loaded[i];
                var entry = new JsonObject
                {
                    ["name"] = name,
                    ["model"] = name,
                    ["pid"] = 1000 + i,
                    ["size_vram"] = _models.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))?.SizeBytes ?? 0
                };
                models.Add(entry);
            }

            return new JsonObject { ["models"] = models }.ToJsonString();
        }
    }

    private string ShowJson(string? model)
    {
        var m = _models.FirstOrDefault(x => string.Equals(x.Name, model, StringComparison.OrdinalIgnoreCase));
        var modelInfo = new JsonObject
        {
            ["general.parameter_count"] = m?.ParameterCount ?? 0
        };
        if (m is not null && m.ExpertCount > 0)
        {
            modelInfo[$"{m.Family}.expert_count"] = m.ExpertCount;
        }

        if (m is not null && m.NativeContextLength.HasValue)
        {
            modelInfo[$"{m.Family}.context_length"] = m.NativeContextLength.Value;
        }

        var result = new JsonObject
        {
            ["model"] = model ?? "",
            ["details"] = new JsonObject
            {
                ["family"] = m?.Family ?? "llama",
                ["parameter_size"] = FormatParams(m?.ParameterCount ?? 0)
            },
            ["model_info"] = modelInfo
        };

        if (m is not null && m.ModelfileNumCtx.HasValue)
        {
            result["parameters"] = $"num_ctx {m.ModelfileNumCtx.Value}\nnum_predict 2048";
        }

        return result.ToJsonString();
    }

    private async Task WriteChatNdJsonAsync(HttpContext context, string? model, CancellationToken ct)
    {
        switch (_chatMode)
        {
            case ChatMode.AbortBeforeCommit:
                _abortCount++;
                context.Abort();
                return;

            case ChatMode.Hang:
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(_chatModeArg, 0)), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                break;
        }

        var streamDelay = _chatStreamDelayMs;
        if (streamDelay > 0)
        {
            try
            {
                await Task.Delay(streamDelay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (!string.IsNullOrEmpty(model))
        {
            LoadModel(model!);
        }

        context.Response.ContentType = "application/x-ndjson";
        await context.Response.WriteAsync(NdJsonChunk(model, "Hel", done: false), ct);
        await context.Response.Body.FlushAsync(ct);
        await context.Response.WriteAsync(NdJsonChunk(model, "lo world", done: false), ct);
        await context.Response.Body.FlushAsync(ct);

        if (_chatMode == ChatMode.StreamNThenAbort)
        {
            // Committed headers + N chunks, then an abrupt socket reset (mid-stream failure).
            // On loopback a context.Abort() (TCP RST) races with the just-flushed bytes still
            // in the kernel send buffer: the reader can receive the RST *before* the 200
            // headers, which the router classifies as an UNCOMMITTED failure (and would then
            // fail over / re-drive the request). A short settle delay lets the loopback reader
            // drain the committed bytes first, so the abort is deterministic: the response is
            // already committed (headers + chunks delivered) and only the stream terminates,
            // which the dispatch loop treats as terminal (no retry).
            _abortCount++;
            try
            {
                await Task.Delay(StreamAbortSettleMs, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            context.Abort();
            return;
        }

        await context.Response.WriteAsync(NdJsonChunk(model, "", done: true), ct);
        await context.Response.Body.FlushAsync(ct);
    }

    private async Task WriteChatSseAsync(HttpContext context, string? model, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(model))
        {
            LoadModel(model!);
        }

        context.Response.ContentType = "text/event-stream";
        await context.Response.WriteAsync($"data: {SseChunk(model, delta: true, done: false)}\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
        await context.Response.WriteAsync($"data: {SseChunk(model, delta: true, done: true)}\n\n", ct);
        await context.Response.WriteAsync("data: [DONE]\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }

    private static string NdJsonChunk(string? model, string content, bool done)
    {
        var obj = new JsonObject
        {
            ["model"] = model ?? "",
            ["created_at"] = DateTime.UtcNow.ToString("o"),
            ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
            ["done"] = done
        };
        if (done)
        {
            obj["prompt_eval_count"] = 12;
            obj["eval_count"] = 7;
        }

        return obj.ToJsonString() + "\n";
    }

    private static string SseChunk(string? model, bool delta, bool done)
    {
        var obj = new JsonObject
        {
            ["id"] = "cmpl-1",
            ["object"] = "chat.completion.chunk",
            ["model"] = model ?? "",
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject { ["content"] = done ? "" : "Hi" },
                    ["finish_reason"] = done ? "stop" : null
                }
            }
        };
        if (!delta)
        {
            obj["usage"] = new JsonObject { ["prompt_tokens"] = 12, ["completion_tokens"] = 7 };
        }

        return obj.ToJsonString();
    }

    private static string FormatParams(long count)
    {
        if (count >= 1_000_000_000)
        {
            return $"{count / 1_000_000_000.0:0.0}B";
        }

        if (count >= 1_000_000)
        {
            return $"{count / 1_000_000.0:0.0}M";
        }

        return $"{count}";
    }

    private static int AllocateLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
