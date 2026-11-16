using System.Text.Json.Serialization;

namespace OllamaRouter.Endpoints.RouterApi.Models;

/// <summary>
/// Wire request of <c>POST /api/router/models/pull</c> (task #125): pulls
/// <see cref="Model"/> (a <c>name[:tag]</c> model reference — the tag IS the quantization
/// selector in the Ollama registry) onto <see cref="Target"/> (null/absent = the primary
/// target fallback: "Local" when present+enabled, else the first enabled effective target,
/// else the first effective target). When <see cref="AliasContext"/> (>= 1) is set, the
/// target's daemon additionally creates the derived model (alias)
/// <c>{name}:{tag}-{ctx}</c> via <c>POST /api/create</c> with
/// <c>parameters.num_ctx = aliasContext</c> AFTER the pull (Ollama reuses the base blobs;
/// only a new Modelfile blob is written).
/// </summary>
public sealed record OllamaRouterModelPullRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("target")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Target = null,
    [property: JsonPropertyName("aliasContext")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? AliasContext = null);

/// <summary>
/// One NDJSON event line of the <c>POST /api/router/models/pull</c> streaming response
/// (task #125). <see cref="Event"/> is the discriminator
/// (<c>started</c> | <c>present</c> | <c>progress</c> | <c>alias_started</c> |
/// <c>alias_created</c> | <c>success</c> | <c>error</c>); every other field is nullable and
/// omitted from the JSON when null (<see cref="JsonIgnoreCondition.WhenWritingNull"/>).
/// <c>success</c> / <c>error</c> are TERMINAL (the stream ends after them).
/// </summary>
public sealed record OllamaRouterModelPullEvent(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("target")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Target = null,
    [property: JsonPropertyName("model")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Model = null,
    [property: JsonPropertyName("status")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Status = null,
    [property: JsonPropertyName("digest")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Digest = null,
    [property: JsonPropertyName("totalBytes")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? TotalBytes = null,
    [property: JsonPropertyName("completedBytes")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? CompletedBytes = null,
    [property: JsonPropertyName("alias")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Alias = null,
    [property: JsonPropertyName("context")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Context = null,
    [property: JsonPropertyName("stage")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Stage = null,
    [property: JsonPropertyName("error")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null,
    [property: JsonPropertyName("durationMs")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? DurationMs = null);

/// <summary>
/// Client-side (NOT wire) progress item reported by
/// <see cref="IOllamaRouterApiClient.PullModelAsync"/> (task #125): <see cref="Stage"/> is
/// <c>"pull"</c> or <c>"alias"</c>; <see cref="Status"/> is the daemon status text (or
/// <c>"present"</c> / <c>"started"</c> / <c>"created"</c> for the mapped lifecycle events).
/// The client reports on the caller's synchronization context (the desktop view model wraps
/// the handler with UI-thread marshaling).
/// </summary>
public sealed record OllamaRouterModelPullProgress(
    string Stage,
    string Status,
    long? TotalBytes,
    long? CompletedBytes,
    string? Alias,
    int? Context);

/// <summary>
/// Client-side (NOT wire) terminal result of <see cref="IOllamaRouterApiClient.PullModelAsync"/>
/// (task #125). <see cref="Succeeded"/> is false for an in-stream <c>error</c> event or an
/// unexpected end of the stream (both surface here, NOT as an exception — pre-stream HTTP
/// failures 400/404/409 surface as <see cref="OllamaRouterApiException"/>; a cancelled
/// request surfaces as <c>OperationCanceledException</c>).
/// </summary>
public sealed record OllamaRouterModelPullResult(
    bool Succeeded,
    string Target,
    string Model,
    string? Alias,
    int? AliasContext,
    string? Error,
    long? DurationMs);

/// <summary>
/// Pure, allocation-light model-reference + alias naming rules shared by the router pull
/// endpoint/service AND the desktop Install Model wizard (task #125) — the single source of
/// truth so the client-side validation/alias preview never drifts from the server's.
/// <para>
/// A model reference is <c>name</c> or <c>name:tag</c> (exactly what <c>ollama pull</c>
/// accepts; the tag IS the quantization selector in the Ollama registry): <c>/</c>-separated
/// name segments each matching <c>^[A-Za-z0-9][A-Za-z0-9._-]*$</c> (namespaced
/// <c>org/model:tag</c> is valid), AT MOST one <c>:</c>, and a non-empty tag when present.
/// The alias name for a context is <c>{name}:{tag}-{suffix}</c> (default tag <c>latest</c>;
/// the suffix is <c>{context/1024}k</c> for whole-k contexts, else the raw context — e.g.
/// <c>llama3.2:13b-q4_K_M</c> + 8192 → <c>llama3.2:13b-q4_K_M-8k</c>,
/// <c>qwen3</c> + 4096 → <c>qwen3:latest-4k</c>, 20000 → <c>-20000</c>). A computed alias
/// equal to the model reference (case-insensitive) is INVALID (a collision).
/// </para>
/// </summary>
public static class ModelPullNaming
{
    /// <summary>
    /// Parses a <c>name[:tag]</c> model reference. Returns false (and empty outputs) for
    /// null/blank input, two or more colons, an empty tag, or a name/tag segment that does
    /// not match <c>^[A-Za-z0-9][A-Za-z0-9._-]*$</c>. <see cref="tag"/> is null when the
    /// reference carries no explicit tag (the registry default <c>latest</c> applies).
    /// </summary>
    public static bool TryParseModelReference(string? reference, out string name, out string? tag)
    {
        name = string.Empty;
        tag = null;
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        var trimmed = reference.Trim();
        var colonIndex = trimmed.IndexOf(':');
        string candidateName;
        string? candidateTag = null;
        if (colonIndex >= 0)
        {
            if (trimmed.IndexOf(':', colonIndex + 1) >= 0)
            {
                return false; // more than one ':'
            }

            candidateName = trimmed[..colonIndex];
            candidateTag = trimmed[(colonIndex + 1)..];
            if (candidateTag.Length == 0)
            {
                return false; // trailing ':' with no tag
            }

            if (!IsValidSegment(candidateTag))
            {
                return false;
            }
        }
        else
        {
            candidateName = trimmed;
        }

        if (!IsValidModelName(candidateName))
        {
            return false;
        }

        name = candidateName;
        tag = candidateTag;
        return true;
    }

    /// <summary>
    /// Computes the context-alias name for <paramref name="modelReference"/> +
    /// <paramref name="context"/> (see the class summary for the shape). Returns false when
    /// the reference is invalid, <paramref name="context"/> &lt; 1, or the computed alias
    /// collides with the model reference (case-insensitive equality).
    /// </summary>
    public static bool TryComputeAliasName(string modelReference, int context, out string alias)
    {
        alias = string.Empty;
        if (context < 1 || !TryParseModelReference(modelReference, out var name, out string? tag))
        {
            return false;
        }

        var suffix = context % 1024 == 0 ? $"{context / 1024}k" : $"{context}";
        var candidate = $"{name}:{tag ?? "latest"}-{suffix}";
        if (string.Equals(candidate, modelReference.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false; // collision: the alias would be the model reference itself
        }

        alias = candidate;
        return true;
    }

    private static bool IsValidModelName(string value)
    {
        var segments = value.Split('/');
        if (segments.Length is 0 or > 3)
        {
            return false;
        }

        return segments.All(IsValidSegment);
    }

    private static bool IsValidSegment(string segment)
    {
        if (segment.Length == 0 || !IsValidFirstChar(segment[0]))
        {
            return false;
        }

        for (var i = 1; i < segment.Length; i++)
        {
            if (!IsValidChar(segment[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidFirstChar(char c) =>
        (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9'));

    private static bool IsValidChar(char c) => IsValidFirstChar(c) || c is '.' or '_' or '-';
}
