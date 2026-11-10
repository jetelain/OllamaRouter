using System.Net;

namespace OllamaRouter.Services.Clients;

/// <summary>
/// Transport / protocol error of the router's own Ollama <c>IChatClient</c> (task #61) — the
/// mirror image of <see cref="OpenAiHttpException"/>. Carries the upstream HTTP status when the
/// daemon answered with a non-2xx (e.g. 404 model-not-found, 400 bad request); a null status
/// means a transport-level failure (connection refused, DNS, timeout) and is classified as
/// "uncommitted" by the dispatch loop (failover applies).
/// </summary>
/// <param name="message">Human-readable error description (includes a ≤200-char body snippet on non-2xx).</param>
/// <param name="statusCode">Upstream HTTP status, or <c>null</c> for transport-level failures.</param>
public sealed class OllamaHttpException : Exception
{
    public OllamaHttpException(string message, HttpStatusCode? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    /// <summary>Upstream HTTP status; <c>null</c> when the failure happened before any HTTP response.</summary>
    public HttpStatusCode? StatusCode { get; }
}
