using System.Net;

namespace OllamaRouter.Client;

/// <summary>
/// Thrown by <see cref="OllamaRouterApiClient"/> when the router returns a non-2xx response
/// (after the single automatic 401 default-key reload, when applicable). <see cref="Error"/>
/// carries the parsed <c>error</c> property of the shared <c>{"error": "..."}</c> wire shape
/// (or the raw response body, truncated to 512 characters, when the body is not
/// parseable as that shape).
/// </summary>
public sealed class OllamaRouterApiException : Exception
{
    /// <summary>
    /// Creates the exception with the HTTP <paramref name="status"/> and the parsed
    /// (or raw-fallback) <paramref name="error"/> text.
    /// </summary>
    public OllamaRouterApiException(HttpStatusCode status, string? error)
        : base($"OllamaRouter API request failed with {(int)status} {status}: {error ?? "(no error body)"}")
    {
        Status = status;
        Error = error;
    }

    /// <summary>The HTTP status code of the failed response (e.g. <see cref="HttpStatusCode.Unauthorized"/>).</summary>
    public HttpStatusCode Status { get; }

    /// <summary>
    /// The parsed <c>error</c> property of the response body, or the raw body text
    /// (truncated to 512 characters) when the body is not a JSON <c>{"error": "..."}</c> object.
    /// </summary>
    public string? Error { get; }
}
