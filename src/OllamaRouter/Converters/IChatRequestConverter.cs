using Microsoft.Extensions.AI;

namespace OllamaRouter.Converters;

/// <summary>
/// Normalizes an inbound chat/generate or chat/completions request body into the canonical
/// Microsoft.Extensions.AI representation. Implementations are stateless and thread-safe.
/// </summary>
public interface IChatRequestConverter
{
    /// <summary>
    /// Normalizes inbound request stream to canonical <see cref="ChatMessage"/> list and <see cref="ChatOptions"/>.
    /// Auto-detects the wire shape: "messages" array present => chat shape; otherwise
    /// "prompt" (or neither, e.g. {"model":"m"} keep-alive pings) => generate/completions shape.
    /// Throws <see cref="System.Text.Json.JsonException"/> for malformed JSON. Never throws for a structurally
    /// valid empty body (empty model name is legal; Ollama picks a default model).
    /// </summary>
    ValueTask<(IList<ChatMessage> Messages, ChatOptions Options, string ModelName)> ParseChatRequestAsync(
        Stream requestBody,
        CancellationToken cancellationToken);
}
