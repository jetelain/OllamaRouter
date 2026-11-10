using Microsoft.Extensions.AI;

namespace OllamaRouter.Services.Clients;

/// <summary>
/// Immutable per-endpoint client configuration for the OpenAI-compatible HTTP client (task #60).
/// Deliberately SCHEMA-INDEPENDENT: <c>OllamaTargetOptions</c> does not yet carry
/// <c>Type</c>/<c>ApiKey</c>/<c>Provider</c> fields (that schema evolution belongs to task #61),
/// which maps its new schema fields onto this record before calling <see cref="IOpenAiChatClientFactory"/>.
/// </summary>
/// <param name="TargetName">Human-readable target/endpoint name (surfaced as the provider name in <c>ChatClientMetadata</c>).</param>
/// <param name="BaseAddress">
/// Absolute base address of the OpenAI-compatible endpoint. May already carry a provider path prefix
/// (e.g. <c>http://host:8000/v1</c>); request URLs are resolved relatively against it
/// (<c>chat/completions</c>), so both root-mounted and prefixed endpoints work.
/// </param>
/// <param name="ApiKey">Optional bearer token; <c>null</c>/empty ⇒ no <c>Authorization</c> header is sent.</param>
public sealed record OpenAiTargetEndpoint(
    string TargetName,
    Uri BaseAddress,
    string? ApiKey = null);

/// <summary>
/// Produces and pools OpenAI-compatible <see cref="IChatClient"/> instances per endpoint (task #60).
/// One pooled client (and its dedicated <c>SocketsHttpHandler</c>/<c>HttpClient</c>) is created per
/// normalized (base address, API key) pair and reused for all dispatches to that endpoint, so
/// connection pools survive across requests. Registered as a singleton via
/// <c>AddOllamaRouterCore</c>; disposing the factory releases every pooled client deterministically.
/// </summary>
public interface IOpenAiChatClientFactory
{
    /// <summary>Returns the pooled client for <paramref name="endpoint"/>, creating it on first use.</summary>
    IChatClient GetClient(OpenAiTargetEndpoint endpoint);

    /// <summary>
    /// Disposes and removes the pooled client for <paramref name="endpoint"/>
    /// (fleet/target removal or reconfiguration, e.g. an API key change).
    /// </summary>
    void DropClient(OpenAiTargetEndpoint endpoint);
}
