using System.Net;

namespace OllamaRouter.Services.Clients;

/// <summary>
/// Domain exception for every OpenAI-compatible downstream failure (task #60): both HTTP error
/// responses (401/403, 429, 503, other 4xx/5xx) and transport-level failures (connect/DNS/read).
/// Task #61's failover logic catches this single type and branches on <see cref="StatusCode"/>
/// (429 → uncommitted retry/backoff, 503 → liveness offline mark, 401/403 → credential
/// misconfiguration). Cancellation is NEVER wrapped into this type — a cancelled request keeps
/// surfacing <see cref="OperationCanceledException"/> to the caller unmodified.
/// </summary>
public sealed class OpenAiHttpException : HttpRequestException
{
    /// <param name="message">Composed, operator-friendly message (class-specific prefix + payload message or raw-body snippet).</param>
    /// <param name="statusCode">Downstream HTTP status; <c>null</c> ⇒ transport-level failure (no response was received).</param>
    /// <param name="errorType">
    /// <c>error.type</c> of the OpenAI error envelope (e.g. "invalid_request_error"), when parseable.
    /// </param>
    /// <param name="errorCode"><c>error.code</c> of the OpenAI error envelope, when present.</param>
    /// <param name="innerException">The underlying <see cref="HttpRequestException"/> for transport failures.</param>
    public OpenAiHttpException(
        string message,
        HttpStatusCode? statusCode,
        string? errorType,
        string? errorCode,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ErrorType = errorType;
        ErrorCode = errorCode;
    }

    /// <summary>Downstream HTTP status code; <c>null</c> for transport-level failures.</summary>
    public new HttpStatusCode? StatusCode { get; }

    /// <summary><c>error.type</c> from the OpenAI error envelope, when parseable.</summary>
    public string? ErrorType { get; }

    /// <summary><c>error.code</c> from the OpenAI error envelope, when present.</summary>
    public string? ErrorCode { get; }
}
