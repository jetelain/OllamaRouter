using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OllamaSharp.Models;
using OllamaSharp.Models.Exceptions;

namespace OllamaRouter.Services.Clients;

/// <summary>
/// Thin <see cref="OllamaSharp.OllamaApiClient"/> subclass whose ONLY job is to restore the
/// user-facing error surfacing that stock OllamaSharp 5.5.2 loses for non-400 daemon errors
/// (task #111): the stock <c>EnsureSuccessStatusCode()</c> path throws a bare
/// <see cref="HttpRequestException"/> with the status set but NO body, so a
/// <c>{"error":"model 'ghost' not found"}</c> from a 404 would reach the client as an
/// undecodable <c>Response status code does not indicate success: 404 (Not Found)</c>.
/// <para>
/// The override of the <c>protected virtual</c> <c>SendToOllamaAsync</c> mirrors the stock flow
/// (header application → <c>HttpClient.SendAsync</c>) and then, on a non-2xx response, reads a
/// bounded (≤16 KiB) body and extracts the <c>error</c> property with <see cref="Utf8JsonReader"/>
/// (no reflection — AOT-safe) to restore the exact legacy message formats:
/// </para>
/// <list type="bullet">
/// <item><b>HTTP 400</b> → <see cref="OllamaException"/> / <see cref="ModelDoesNotSupportToolsException"/>
/// (same branch as stock 5.5.2, detected on "does not support tools") with message
/// <c>Ollama endpoint error (HTTP 400): {error}</c>;</item>
/// <item><b>any other status</b> → <see cref="HttpRequestException"/> with <see
/// cref="HttpRequestException.StatusCode"/> SET (so the routing middleware classifies it as an
/// upstream HTTP error) and message <c>Ollama endpoint error (HTTP {status}): {detail}</c>, where
/// <c>detail</c> is the parsed <c>error</c> text or a ≤200-char body snippet (exact parity with
/// the deleted <c>OllamaHttpException</c> format);</item>
/// <item><b>transport failures</b> propagate unmodified: <see cref="HttpRequestException"/> with
/// <c>StatusCode == null</c> / <see cref="IOException"/> / <see cref="OperationCanceledException"/>
/// for cancellations.</item>
/// </list>
/// The package's <c>ApplyCustomHeaders</c> extension is internal, so <see cref="DefaultRequestHeaders"/>
/// (public) and <see cref="OllamaRequest.CustomHeaders"/> (public) are applied manually with the
/// stock remove-then-add semantics. All other client behavior (NDJSON reads, streaming, metadata)
/// is inherited unchanged.
/// </summary>
internal sealed class RouterOllamaApiClient : OllamaSharp.OllamaApiClient
{
    private const int MaxErrorBodyBytes = 16 * 1024;
    private const int ErrorBodySnippetLength = 200;

    /// <summary>
    /// The caller-supplied transport. <see cref="OllamaSharp.OllamaApiClient"/> keeps its own
    /// private reference; the subclass needs one for the send path (non-owning — disposal stays
    /// with the <see cref="OllamaChatClientFactory"/>).
    /// </summary>
    private readonly HttpClient _httpClient;

    /// <summary>
    /// <paramref name="httpClient"/> must have its <c>BaseAddress</c> set to the daemon endpoint.
    /// This client does NOT own it (the <c>HttpClient</c> constructor leaves OllamaSharp's
    /// internal <c>_disposeHttpClient</c> flag false, so <see cref="OllamaSharp.OllamaApiClient.Dispose"/>
    /// never disposes a caller-supplied client). <paramref name="jsonSerializerContext"/> is the
    /// router's AOT context (<c>OllamaSharpWireContext.Default</c>) — mandatory for a clean Native
    /// AOT publish.
    /// </summary>
    public RouterOllamaApiClient(HttpClient httpClient, JsonSerializerContext jsonSerializerContext)
        : base(httpClient, string.Empty, jsonSerializerContext)
    {
        _httpClient = httpClient;
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendToOllamaAsync(
        HttpRequestMessage requestMessage,
        OllamaRequest? ollamaRequest,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        // Manual header application (the package's internal extension does remove-then-add;
        // mirror that so a pre-set request header is replaced, never duplicated).
        foreach (var header in DefaultRequestHeaders)
        {
            requestMessage.Headers.Remove(header.Key);
            requestMessage.Headers.Add(header.Key, header.Value);
        }

        if (ollamaRequest?.CustomHeaders is { Count: > 0 } customHeaders)
        {
            foreach (var header in customHeaders)
            {
                requestMessage.Headers.Remove(header.Key);
                requestMessage.Headers.Add(header.Key, header.Value);
            }
        }

        var response = await _httpClient.SendAsync(requestMessage, completionOption, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        // Bounded best-effort error-body read (a body read failure degrades to a snippet-less
        // message rather than masking the HTTP error). The response is disposed by the caller.
        var rawBody = await ReadBoundedBodyAsync(response, cancellationToken);
        var detail = ExtractErrorDetail(rawBody);

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var message = $"Ollama endpoint error (HTTP 400): {detail}";
            throw message.Contains("does not support tools", StringComparison.Ordinal)
                ? new ModelDoesNotSupportToolsException(message)
                : new OllamaException(message);
        }

        throw new HttpRequestException(
            $"Ollama endpoint error (HTTP {(int)response.StatusCode}): {detail}",
            null,
            response.StatusCode);
    }

    private static async Task<string> ReadBoundedBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var bodyStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[MaxErrorBodyBytes];
            var read = await bodyStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            return read > 0 ? Encoding.UTF8.GetString(buffer, 0, read) : string.Empty;
        }
        catch (Exception)
        {
            // Best effort — keep the body empty; the status-based error still surfaces.
            return string.Empty;
        }
    }

    /// <summary>
    /// Parses the <c>error</c> property out of a bounded error body with <see cref="Utf8JsonReader"/>
    /// (AOT-safe, no reflection): returns the property's string value when the body is a JSON
    /// object carrying a string <c>error</c>; otherwise a ≤<see cref="ErrorBodySnippetLength"/>-char
    /// snippet of the raw body (empty body ⇒ empty detail).
    /// </summary>
    private static string ExtractErrorDetail(string rawBody)
    {
        if (rawBody.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(rawBody));
            if (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
            {
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.PropertyName &&
                        reader.GetString() == "error" &&
                        reader.Read() &&
                        reader.TokenType == JsonTokenType.String)
                    {
                        return reader.GetString() ?? string.Empty;
                    }

                    if (reader.TokenType == JsonTokenType.EndObject)
                    {
                        break;
                    }

                    // Skip nested structures we do not care about.
                    if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    {
                        var depth = 1;
                        while (depth > 0 && reader.Read())
                        {
                            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                            {
                                depth++;
                            }
                            else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
                            {
                                depth--;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // Unparseable error body: fall through to the raw snippet.
        }

        return rawBody.Length > ErrorBodySnippetLength ? rawBody[..ErrorBodySnippetLength] : rawBody;
    }
}
