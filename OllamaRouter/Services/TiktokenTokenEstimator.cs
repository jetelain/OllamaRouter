using Microsoft.ML.Tokenizers;

namespace OllamaRouter.Services;

/// <summary>
/// Estimates the number of tokens using the Tiktoken tokenizer (CL100kBase), used as a fast
/// and universal approximation regardless of the model actually targeted.
/// </summary>
public sealed class TiktokenTokenEstimator : ITokenEstimator
{
    private readonly TiktokenTokenizer _tokenizer = TiktokenTokenizer.CreateForModel("gpt-3.5-turbo");

    public int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return _tokenizer.EncodeToIds(text).Count;
    }
}
