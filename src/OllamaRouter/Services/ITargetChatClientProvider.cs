using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;

namespace OllamaRouter.Services;

/// <summary>
/// Resolves the dispatch <see cref="IChatClient"/> for a fleet target by name (task #61).
/// The converter path of the completion dispatch loop needs a client for BOTH protocols:
/// Ollama targets serving OpenAI-protocol clients (the <see cref="Clients.OllamaChatClient"/>)
/// and OpenAI targets serving Ollama-protocol clients (the task #60 / #112
/// <see cref="Clients.OpenAiChatClient"/> native-client adapter).
/// <para>
/// The provider resolves the LIVE <c>OllamaTargetOptions</c> snapshot per call (case-insensitive
/// name match) and delegates to the pooled per-endpoint factories, so a Setup-API
/// add/update/remove is routable on the very next request. <see cref="GetClient"/>/
/// <see cref="TryResolve"/> return null for unknown targets, targets without a usable URL, and
/// <c>TargetType.OsNative</c> targets (no client implementation until tasks #63/#64).
/// </para>
/// </summary>
public interface ITargetChatClientProvider
{
    /// <summary>
    /// Returns the pooled <see cref="IChatClient"/> for the named target, or null when the
    /// target is unknown, not an effective target, or its <c>Type</c> has no client implementation.
    /// </summary>
    IChatClient? GetClient(string targetName);

    /// <summary>
    /// Out-param overload: returns true and the resolved client when one exists, otherwise
    /// false with a null client.
    /// </summary>
    bool TryResolve(string targetName, [NotNullWhen(true)] out IChatClient? client);
}
