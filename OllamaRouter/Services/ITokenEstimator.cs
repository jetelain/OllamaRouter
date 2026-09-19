namespace OllamaRouter.Services;

/// <summary>
/// Estimates the number of tokens represented by a collection of text parts or messages.
/// </summary>
public interface ITokenEstimator
{
    int EstimateTokens(IReadOnlyList<string> texts);
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
}
