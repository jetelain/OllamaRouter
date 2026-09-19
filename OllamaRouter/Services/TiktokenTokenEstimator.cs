using Microsoft.ML.Tokenizers;

namespace OllamaRouter.Services;

/// <summary>
/// Estimates the number of tokens using the Tiktoken tokenizer (CL100kBase), used as a fast
/// and universal approximation regardless of the model actually targeted.
/// </summary>
public sealed class TiktokenTokenEstimator : ITokenEstimator
{
    private readonly Lazy<TiktokenTokenizer> _tokenizer = new(() => TiktokenTokenizer.CreateForModel("gpt-3.5-turbo"));

    public int EstimateTokens(IReadOnlyList<string> texts)
    {
        if (texts == null || texts.Count == 0)
        {
            return 0;
        }

        var tokenizer = _tokenizer.Value;
        int total = 0;
        for (int i = 0; i < texts.Count; i++)
        {
            var text = texts[i];
            if (!string.IsNullOrEmpty(text))
            {
                total += tokenizer.CountTokens(text);
            }
        }

        return total;
    }
}
