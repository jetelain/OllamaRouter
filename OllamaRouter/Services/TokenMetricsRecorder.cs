using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;

namespace OllamaRouter.Services;

/// <summary>
/// Best-effort CSV recorder for token metrics. Each recorded completion request produces one
/// aggregate data row pairing the request's structural text metrics (words, symbols, non-ASCII
/// characters, character count — <see cref="Pmad.AI.Tokens.TokenMetrics"/>) with the actual
/// prompt/response token counts reported by the Ollama instance. Rows are emitted in a fixed
/// column order with RFC 4180 escaping (CRLF line endings, quote doubling) and written to:
/// <list type="bullet">
/// <item>a per-request CSV file named <c>{yyyyMMdd-HHmmss-fff}_{sequence:D6}_{modelSlug}_metrics.csv</c>
/// (header + one row) when <c>GenerateIndividualFiles</c> is on;</item>
/// <item>the consolidated <c>token-metrics.csv</c> dataset (header once, one appended row per
/// request) when <c>AppendConsolidatedFile</c> is on.</item>
/// </list>
/// Per-request files are pruned oldest-first (ordinal name order = chronological) when their
/// count exceeds <c>MaxFiles</c>; the consolidated file never matches the pruning glob and is
/// never pruned. Nothing is written when the export is disabled, and I/O failures are logged,
/// never thrown.
/// </summary>
public sealed class TokenMetricsRecorder : ITokenMetricsRecorder
{
    private const string ConsolidatedFileName = "token-metrics.csv";

    private const string ColumnHeader =
        "timestamp_utc,request_id,model,target,endpoint,words,symbols,non_ascii,char_count," +
        "prior_context_tokens,estimated_prompt_tokens,actual_prompt_tokens,actual_response_tokens," +
        "elapsed_ms,status_code,success";

    private static readonly char[] EscapableChars = { ',', '"', '\r', '\n' };

    private readonly object gate = new();
    private readonly string? outputDirectory;
    private readonly int maxFiles;
    private readonly bool generateIndividualFiles;
    private readonly bool appendConsolidatedFile;
    private readonly ILogger<TokenMetricsRecorder> logger;
    private long sequence;

    public TokenMetricsRecorder(IOptions<OllamaRouterOptions> options, ILogger<TokenMetricsRecorder> logger)
    {
        this.logger = logger;

        var csv = options.Value.Recording?.TokenMetricsCsv;
        if (csv is null || !csv.Enabled || (!csv.GenerateIndividualFiles && !csv.AppendConsolidatedFile))
        {
            outputDirectory = null;
            maxFiles = 0;
            generateIndividualFiles = false;
            appendConsolidatedFile = false;
            return;
        }

        generateIndividualFiles = csv.GenerateIndividualFiles;
        appendConsolidatedFile = csv.AppendConsolidatedFile;
        maxFiles = Math.Max(0, csv.MaxFiles);
        outputDirectory = !string.IsNullOrWhiteSpace(csv.OutputDirectory)
            ? csv.OutputDirectory
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OllamaRouter",
                "token-metrics");

        logger.LogInformation("Token metrics CSV recording enabled: {Directory}", outputDirectory);
    }

    public bool IsEnabled => outputDirectory is not null;

    public void Record(TokenMetricsCsvRecord record)
    {
        if (!IsEnabled)
        {
            return;
        }

        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(outputDirectory!);

                var row = BuildDataRow(record);

                if (generateIndividualFiles)
                {
                    var fileName =
                        $"{record.Timestamp.UtcDateTime:yyyyMMdd-HHmmss-fff}_{Interlocked.Increment(ref sequence):D6}_{Slugify(record.Model)}_metrics.csv";
                    File.WriteAllText(
                        Path.Combine(outputDirectory!, fileName),
                        ColumnHeader + "\r\n" + row + "\r\n",
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }

                if (appendConsolidatedFile)
                {
                    var consolidatedPath = Path.Combine(outputDirectory!, ConsolidatedFileName);
                    if (!File.Exists(consolidatedPath) || new FileInfo(consolidatedPath).Length == 0)
                    {
                        File.AppendAllText(consolidatedPath, ColumnHeader + "\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    }

                    File.AppendAllText(consolidatedPath, row + "\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }

                if (generateIndividualFiles && maxFiles > 0)
                {
                    PruneIndividualFiles();
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error recording token metrics CSV for model {Model}", record.Model);
            }
        }
    }

    // Builds the single RFC 4180 data row in the fixed column order: invariant-culture
    // formatting for numbers/bools, ISO 8601 ("O") for the timestamp, empty fields for
    // missing actual token counts, and quoting/escaping for text fields.
    private static string BuildDataRow(TokenMetricsCsvRecord record)
    {
        var metrics = record.AggregateMetrics;
        return string.Join(
            ',',
            record.Timestamp.ToString("O", CultureInfo.InvariantCulture),
            record.RequestId.ToString("D", CultureInfo.InvariantCulture),
            CsvEscape(record.Model),
            CsvEscape(record.Target),
            CsvEscape(record.Endpoint),
            metrics.Words.ToString(CultureInfo.InvariantCulture),
            metrics.Symbols.ToString(CultureInfo.InvariantCulture),
            metrics.NonAscii.ToString(CultureInfo.InvariantCulture),
            metrics.CharCount.ToString(CultureInfo.InvariantCulture),
            record.PriorContextTokenCount.ToString(CultureInfo.InvariantCulture),
            record.EstimatedPromptTokens.ToString(CultureInfo.InvariantCulture),
            (record.ActualPromptTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
            (record.ActualResponseTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
            record.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture),
            record.StatusCode.ToString(CultureInfo.InvariantCulture),
            record.Success ? "true" : "false");
    }

    // RFC 4180: wrap in double quotes (doubling any internal double quotes) when the field
    // contains a comma, a double quote, a CR or an LF.
    private static string CsvEscape(string field)
    {
        if (field.IndexOfAny(EscapableChars) < 0)
        {
            return field;
        }

        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }

    // Keeps the output directory below MaxFiles per-request CSV files by deleting the oldest
    // ones. File names start with a sortable UTC timestamp, so ordinal name order is creation
    // order. The consolidated token-metrics.csv does not match the "*_metrics.csv" glob and is
    // never pruned.
    private void PruneIndividualFiles()
    {
        var oldestFirst = Directory.EnumerateFiles(outputDirectory!, "*_metrics.csv")
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToArray();

        var excess = oldestFirst.Length - maxFiles;
        if (excess <= 0)
        {
            return;
        }

        foreach (var file in oldestFirst.Take(excess))
        {
            try
            {
                File.Delete(file);
                logger.LogDebug("Deleted oldest token metrics CSV file: {File}", file);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not delete token metrics CSV file: {File}", file);
            }
        }
    }

    // Maps a model name to a file-name-safe slug (same character-class rule as the
    // InferenceRequestRecorder, e.g. "glm-4.6:cloud" => "glm-4.6-cloud").
    private static string Slugify(string modelName)
    {
        var slug = new string(modelName.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray());
        return string.IsNullOrEmpty(slug) ? "unknown" : slug;
    }
}
