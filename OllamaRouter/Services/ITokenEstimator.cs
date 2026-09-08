namespace OllamaRouter.Services;

/// <summary>
/// Estimates the number of tokens represented by a piece of text.
/// </summary>
public interface ITokenEstimator
{
    int EstimateTokens(string text);
}
