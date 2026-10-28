namespace OllamaRouter.Services;

/// <summary>
/// Estimates the number of tokens represented by a collection of text parts or messages.
/// </summary>
public interface ITokenEstimator
{
    int EstimateTokens(IReadOnlyList<string> texts);
}

/// <summary>
/// Optional extension of <see cref="ITokenEstimator"/> that adjusts token estimation factors
/// based on the target model family (e.g. Qwen, Gemma).
/// </summary>
public interface IModelAwareTokenEstimator : ITokenEstimator
{
    int EstimateTokens(IReadOnlyList<string> texts, string? modelName);
}

public static class TokenEstimatorExtensions
{
    public static int EstimateTokens(this ITokenEstimator estimator, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return estimator.EstimateTokens([text]);
    }

    public static int EstimateTokens(this ITokenEstimator estimator, string text, string? modelName)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return estimator.EstimateTokens([text], modelName);
    }

    public static int EstimateTokens(this ITokenEstimator estimator, IReadOnlyList<string> texts, string? modelName)
    {
        if (estimator is IModelAwareTokenEstimator modelAware)
        {
            return modelAware.EstimateTokens(texts, modelName);
        }

        return estimator.EstimateTokens(texts);
    }
}
