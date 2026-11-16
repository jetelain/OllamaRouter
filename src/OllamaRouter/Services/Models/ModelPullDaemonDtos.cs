using System.Text.Json.Serialization;

namespace OllamaRouter.Services.Models;

/// <summary>
/// Wire body of the daemon's <c>POST {url}/api/pull</c> (task #125, <c>stream: true</c> —
/// the response is an NDJSON stream of status lines).
/// </summary>
public sealed record OllamaDaemonPullRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("stream")] bool Stream);

/// <summary>
/// Wire body of the daemon's <c>POST {url}/api/create</c> (task #125): creates the context
/// alias <paramref name="Name"/> as a thin Modelfile over <paramref name="From"/> (the
/// pulled model — Ollama reuses the base blobs) pinning <c>parameters.num_ctx</c>.
/// <c>stream: false</c> → a single JSON body (<c>{"status":"success"}</c> or
/// <c>{"error":"..."}</c>).
/// </summary>
public sealed record OllamaDaemonCreateRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("parameters")] OllamaDaemonCreateParameters Parameters,
    [property: JsonPropertyName("stream")] bool Stream);

/// <summary>The Modelfile parameters of <see cref="OllamaDaemonCreateRequest"/>.</summary>
public sealed record OllamaDaemonCreateParameters(
    [property: JsonPropertyName("num_ctx")] int NumCtx);

/// <summary>
/// One NDJSON line of a daemon <c>/api/pull</c> stream (task #125): the status text, the
/// layer digest + total/completed byte counts (present on "pulling …" lines), and the
/// terminal daemon <c>{"error": "..."}</c> field. Unknown fields are ignored; a line that
/// is neither progress nor an error (e.g. the terminal <c>{"status":"success"}</c>) is
/// recognized by its <see cref="Status"/> value.
/// </summary>
public sealed record OllamaDaemonLineEvent(
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("digest")] string? Digest = null,
    [property: JsonPropertyName("total")] long? Total = null,
    [property: JsonPropertyName("completed")] long? Completed = null,
    [property: JsonPropertyName("error")] string? Error = null);
