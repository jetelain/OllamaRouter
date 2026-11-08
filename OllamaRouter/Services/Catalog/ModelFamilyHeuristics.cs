using System.Globalization;

namespace OllamaRouter.Services.Catalog;

/// <summary>
/// One row of the <see cref="ModelFamilyHeuristics"/> starter table (task #62): the family
/// label, the absolute parameter count (null = undisclosed, e.g. GPT/Claude/Gemini), the
/// Mixture-of-Experts expert count (null/0 = dense) and the family's nominal context window.
/// </summary>
public sealed record KnownModelFamily(string Family, long? ParameterCount, long? ExpertCount, int? ContextLength);

/// <summary>
/// Conservative, deliberately small family table used to SYNTHESIZE architecture and context
/// capabilities for models hosted on non-Ollama targets (task #62). The table is pure
/// configuration (no network): a model name that matches a row yields that row's family,
/// parameter count, expert count and context window; an unmatched name yields null so the
/// caller falls back to the permissive MoE path (decision D1). The table is trivially
/// extendable — add a row, done.
/// <para>
/// Matching: the name is trimmed, lower-cased, stripped of a model tag after a ':'
/// (<c>llama-3.3-70b:instruct</c> ⇒ <c>llama-3.3-70b</c>) and of a leading namespace before the
/// last '/' (OpenRouter-style <c>openai/gpt-4o</c> ⇒ <c>gpt-4o</c>); the longest matching
/// prefix wins (so <c>llama-3.3-70b</c> resolves to the 3.3 row, not a hypothetical
/// <c>llama-3</c> row).
/// </para>
/// </summary>
public static class ModelFamilyHeuristics
{
    private const long B = 1_000_000_000L;

    // Longest-prefix-first: the table is scanned in this exact order and the FIRST (longest,
    // on ties: declaration order) matching prefix wins.
    private static readonly (string Prefix, KnownModelFamily Family)[] Table =
    [
        ("claude-3-5-sonnet", new KnownModelFamily("claude", null, null, 200_000)),
        ("claude-3.5-sonnet", new KnownModelFamily("claude", null, null, 200_000)),
        ("claude-3-7-sonnet", new KnownModelFamily("claude", null, null, 200_000)),
        ("claude-3.7-sonnet", new KnownModelFamily("claude", null, null, 200_000)),
        ("claude-3-opus", new KnownModelFamily("claude", null, null, 200_000)),
        ("deepseek-v3", new KnownModelFamily("deepseek", 671 * B, 256, 131_072)),
        ("deepseek-r1", new KnownModelFamily("deepseek", 671 * B, 256, 131_072)),
        ("gemini-1.5-pro", new KnownModelFamily("gemini", null, null, 128_000)),
        ("gemini-1.5-flash", new KnownModelFamily("gemini", null, null, 128_000)),
        ("llama-3.3-70b", new KnownModelFamily("llama", 70 * B, null, 131_072)),
        ("llama-3.1-405b", new KnownModelFamily("llama", 405 * B, null, 131_072)),
        ("llama-3.1-8b", new KnownModelFamily("llama", 8 * B, null, 131_072)),
        ("llama-3-70b", new KnownModelFamily("llama", 70 * B, null, 8_192)),
        ("llama-3-8b", new KnownModelFamily("llama", 8 * B, null, 8_192)),
        ("mixtral-8x7b", new KnownModelFamily("mixtral", 46 * B + 700_000_000L, 8, 32_768)),
        ("qwen3-235b", new KnownModelFamily("qwen3", 235 * B, 128, 131_072)),
        ("gpt-4.1", new KnownModelFamily("gpt", null, null, 128_000)),
        ("gpt-4o", new KnownModelFamily("gpt", null, null, 128_000)),
        ("gpt-4", new KnownModelFamily("gpt", null, null, 128_000)),
        ("o1", new KnownModelFamily("o-series", null, null, 128_000)),
        ("o3", new KnownModelFamily("o-series", null, null, 200_000))
    ];

    /// <summary>
    /// Resolves the family table row for <paramref name="modelName"/> (normalization +
    /// longest-prefix-first match as described on the type). Returns null when no row
    /// matches (or the name is blank) — the caller then applies the permissive fallbacks.
    /// </summary>
    public static KnownModelFamily? TryMatch(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return null;
        }

        var name = modelName.Trim().ToLowerInvariant();

        // Strip a model tag after the last ':' (llama-3.3-70b:instruct ⇒ llama-3.3-70b).
        var tagIndex = name.LastIndexOf(':');
        if (tagIndex >= 0)
        {
            name = name[..tagIndex];
        }

        // Strip a leading namespace before the last '/' (openai/gpt-4o ⇒ gpt-4o).
        var slashIndex = name.LastIndexOf('/');
        if (slashIndex >= 0)
        {
            name = name[(slashIndex + 1)..];
        }

        name = name.Trim();
        if (name.Length == 0)
        {
            return null;
        }

        foreach (var (prefix, family) in Table)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return family;
            }
        }

        return null;
    }

    /// <summary>
    /// Formats an absolute parameter count as the Ollama-style human-readable size
    /// (<c>"70.0B"</c>, <c>"46.7B"</c>, <c>"138.0M"</c>); null for null input.
    /// </summary>
    public static string? ToHumanReadableParameterCount(long? parameterCount)
    {
        if (parameterCount is null)
        {
            return null;
        }

        var count = parameterCount.Value;
        if (count <= 0)
        {
            return null;
        }

        if (count >= 1_000_000_000L)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0}B", count / 1_000_000_000.0);
        }

        if (count >= 1_000_000L)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0}M", count / 1_000_000.0);
        }

        return string.Format(CultureInfo.InvariantCulture, "{0:0.0}K", count / 1_000.0);
    }
}
