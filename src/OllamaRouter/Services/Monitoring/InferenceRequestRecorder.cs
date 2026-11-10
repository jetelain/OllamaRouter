using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;
using OllamaRouter.Serialization;

namespace OllamaRouter.Services.Monitoring;

/// <summary>
/// Optional, best-effort recorder for inference requests. Each recorded request produces:
/// <list type="bullet">
/// <item>a line in a JSON Lines manifest (<c>requests.jsonl</c>) carrying the routing metadata
/// (estimated vs actual token counts, target, endpoint, status);</item>
/// <item>a pretty-printed JSON payload file holding the full request body, referenced by name
/// from the manifest line.</item>
/// </list>
/// Payload file names start with a sortable UTC timestamp, which also makes them easy to order
/// by age for the retention policy. Nothing is written when the recording mode is disabled, and
/// I/O failures are logged, never thrown.
/// </summary>
public sealed class InferenceRequestRecorder : IInferenceRequestRecorder
{
    private const string ManifestFileName = "requests.jsonl";

    private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };

    private readonly object gate = new();
    private readonly IOptionsMonitor<OllamaRouterOptions> options;
    private readonly ILogger<InferenceRequestRecorder> logger;
    private long sequence;

    /// <summary>
    /// Creates the recorder. The Recording configuration (enabled switch, output directory,
    /// max files) is read from <c>IOptionsMonitor.CurrentValue</c> per recorded request, so a
    /// runtime re-enable/relocation in config takes effect without a restart.
    /// </summary>
    public InferenceRequestRecorder(IOptionsMonitor<OllamaRouterOptions> options, ILogger<InferenceRequestRecorder> logger)
    {
        this.options = options;
        this.logger = logger;

        // Informational startup log only; the per-request live read is authoritative.
        var outputDirectory = ResolveOutputDirectory(options.CurrentValue);
        if (outputDirectory is not null)
        {
            logger.LogInformation("Inference request recording enabled: {Directory}", outputDirectory);
        }
    }

    public void Record(ActivityLogEntry entry, string endpoint, string requestBody)
    {
        // Snapshot-per-operation: one CurrentValue read drives the whole record + prune pass.
        var settings = options.CurrentValue;
        var outputDirectory = ResolveOutputDirectory(settings);
        if (outputDirectory is null)
        {
            return;
        }

        var maxFiles = settings.Recording?.MaxFiles ?? 0;

        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(outputDirectory);

                var payloadFile = CreatePayloadFile(entry.Model, requestBody, outputDirectory);
                var record = new InferenceRequestRecord(
                    entry.Timestamp,
                    entry.Target.ToString(),
                    entry.Model,
                    endpoint,
                    entry.EstimatedPromptTokens,
                    entry.RawPromptTokens,
                    entry.ActualPromptTokens,
                    entry.ActualResponseTokens,
                    entry.ElapsedMilliseconds,
                    entry.StatusCode,
                    entry.Success,
                    payloadFile);

                File.AppendAllText(
                    Path.Combine(outputDirectory, ManifestFileName),
                    JsonSerializer.Serialize(record, InferenceRequestSerializerContext.Default.InferenceRequestRecord) + "\n");

                PruneIfNecessary(maxFiles, outputDirectory);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error recording inference request for model {Model}", entry.Model);
            }
        }
    }

    // Resolves the live output directory: null when recording is disabled (the fast path).
    private static string? ResolveOutputDirectory(OllamaRouterOptions settings)
    {
        if (settings.Recording is not { Enabled: true })
        {
            return null;
        }

        return !string.IsNullOrWhiteSpace(settings.Recording.OutputDirectory)
            ? settings.Recording.OutputDirectory
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OllamaRouter",
                "inference-requests");
    }

    // Writes the full request body as a standalone pretty-printed JSON payload file (falling
    // back to the raw text when the body is not valid JSON) and returns its file name.
    private string CreatePayloadFile(string model, string requestBody, string outputDirectory)
    {
        var fileName =
            $"{DateTime.UtcNow:yyyyMMdd-HHmm-ss-fff}_{Interlocked.Increment(ref sequence):D6}_{Slugify(model)}.json";
        var content = TryPrettyPrintRequestJson(requestBody) ?? requestBody;

        File.WriteAllText(Path.Combine(outputDirectory, fileName), content);
        return fileName;
    }

    // Pretty-prints a request body so payload files can be read directly in an editor.
    private static string? TryPrettyPrintRequestJson(string requestBody)
    {
        try
        {
            return JsonNode.Parse(requestBody)?.ToJsonString(PrettyOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Keeps the output directory below MaxFiles by deleting the oldest payload files, then
    // rewrites the manifest so that it only references files that still exist.
    private void PruneIfNecessary(int maxFiles, string outputDirectory)
    {
        if (maxFiles <= 0)
        {
            return;
        }

        // Payload file names start with a sortable UTC timestamp, so name order is creation order.
        var oldestFirst = Directory.EnumerateFiles(outputDirectory, "*.json")
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
                logger.LogDebug("Deleted oldest inference request recording: {File}", file);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not delete inference request recording: {File}", file);
            }
        }

        RewriteManifest(outputDirectory);
    }

    // Rewrites the manifest, dropping lines that are not valid JSON or whose payload file has
    // been deleted.
    private void RewriteManifest(string outputDirectory)
    {
        var manifestPath = Path.Combine(outputDirectory!, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return;
        }

        var kept = new List<string>();
        foreach (var line in File.ReadAllLines(manifestPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string? file;
            try
            {
                file = JsonNode.Parse(line)?["file"]?.GetValue<string>();
            }
            catch (JsonException)
            {
                file = null; // Corrupted line: drop it from the manifest.
            }

            if (file is null || !File.Exists(Path.Combine(outputDirectory, file)))
            {
                continue;
            }

            kept.Add(line);
        }

        File.WriteAllText(manifestPath, kept.Count > 0 ? string.Join("\n", kept) + "\n" : string.Empty);
    }

    // Maps a model name to a file-name-safe slug (e.g. "glm-4.6:cloud" => "glm-4.6-cloud").
    private static string Slugify(string modelName)
    {
        var slug = new string(modelName.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray());
        return string.IsNullOrEmpty(slug) ? "unknown" : slug;
    }
}
