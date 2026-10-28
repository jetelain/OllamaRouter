using Pmad.AI.Tokens;
using PmadTokenEstimator = Pmad.AI.Tokens.HeuristicTokenEstimator;

namespace OllamaRouter.Services;

/// <summary>
/// A fast, zero-allocation token estimator that approximates token counts using character,
/// word, punctuation, and Unicode heuristics without loading any vocabulary tables into memory.
/// Powered by <see cref="Pmad.AI.Tokens.HeuristicTokenEstimator"/>, with tuned variants for
/// model families such as Qwen and Gemma.
/// </summary>
public sealed class HeuristicTokenEstimator : IModelAwareTokenEstimator
{
    /// <summary>
    /// Gets the default heuristic token estimator instance.
    /// </summary>
    public static PmadTokenEstimator Default => PmadTokenEstimator.Default;

    /// <summary>
    /// Gets the tuned heuristic token estimator instance for Qwen models.
    /// </summary>
    public static PmadTokenEstimator Qwen => PmadTokenEstimator.Qwen;

    /// <summary>
    /// Gets the tuned heuristic token estimator instance for Gemma models.
    /// </summary>
    public static PmadTokenEstimator Gemma => PmadTokenEstimator.Gemma;

    public int EstimateTokens(IReadOnlyList<string> texts)
    {
        return PmadTokenEstimator.Default.Estimate(texts);
    }

    public int EstimateTokens(IReadOnlyList<string> texts, string? modelName)
    {
        return ResolveEstimator(modelName).Estimate(texts);
    }

    public static int EstimateTextTokens(ReadOnlySpan<char> text, string? modelName = null)
    {
        return ResolveEstimator(modelName).Estimate(text);
    }

    /// <summary>
    /// Analyzes the character span to extract structural token metrics (words, symbols, non-ASCII characters, character count).
    /// </summary>
    public static TokenMetrics Analyze(ReadOnlySpan<char> text)
    {
        return PmadTokenEstimator.Analyze(text);
    }

    /// <summary>
    /// Analyzes a collection of text strings to aggregate their structural token metrics.
    /// </summary>
    public static TokenMetrics Analyze(IReadOnlyList<string>? texts)
    {
        return PmadTokenEstimator.Analyze(texts);
    }

    /// <summary>
    /// Resolves the tuned <see cref="PmadTokenEstimator"/> instance based on the target model name.
    /// Falls back to <see cref="Default"/> for uncalibrated models or empty model names.
    /// </summary>
    public static PmadTokenEstimator ResolveEstimator(string? modelName)
    {
        if (string.IsNullOrEmpty(modelName))
        {
            return PmadTokenEstimator.Default;
        }

        if (modelName.Contains("qwen", StringComparison.OrdinalIgnoreCase))
        {
            return PmadTokenEstimator.Qwen;
        }

        if (modelName.Contains("gemma", StringComparison.OrdinalIgnoreCase))
        {
            return PmadTokenEstimator.Gemma;
        }

        return PmadTokenEstimator.Default;
    }
}
