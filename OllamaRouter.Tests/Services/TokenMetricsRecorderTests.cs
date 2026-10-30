using Microsoft.Extensions.Logging.Abstractions;
using OllamaRouter.Options;
using OllamaRouter.Services;
using Pmad.AI.Tokens;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace OllamaRouter.Tests.Services;

public class TokenMetricsRecorderTests : IDisposable
{
    private const string ExpectedHeader =
        "timestamp_utc,request_id,model,target,endpoint,words,symbols,non_ascii,char_count," +
        "prior_context_tokens,estimated_prompt_tokens,actual_prompt_tokens,actual_response_tokens," +
        "elapsed_ms,status_code,success";

    private readonly string tempDirectory;
    private readonly string outputDirectory;

    public TokenMetricsRecorderTests()
    {
        tempDirectory = Directory.CreateTempSubdirectory("OllamaRouterTests").FullName;
        outputDirectory = Path.Combine(tempDirectory, "token-metrics");
    }

    public void Dispose()
    {
        Directory.Delete(tempDirectory, recursive: true);
    }

    private string ConsolidatedPath => Path.Combine(outputDirectory, "token-metrics.csv");

    private static TokenMetricsCsvRecord CreateRecord(
        string model = "Qwen3.8-27B",
        string target = "Local",
        string endpoint = "/api/chat",
        int? actualPrompt = 3800,
        int? actualResponse = 128,
        int statusCode = 200,
        bool success = true) =>
        new(
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            model,
            target,
            endpoint,
            new TokenMetrics(Words: 120, Symbols: 7, NonAscii: 4, CharCount: 655),
            3413,
            3533,
            actualPrompt,
            actualResponse,
            1234,
            statusCode,
            success);

    private static TokenMetricsRecorder CreateSut(TokenMetricsCsvOptions? csv) =>
        new(
            MsOptions.Create(new OllamaRouterOptions { Recording = csv is null ? null : new RecordingOptions { TokenMetricsCsv = csv } }),
            NullLogger<TokenMetricsRecorder>.Instance);

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void Record_WhenDisabled_CreatesNoFiles(bool? enabled)
    {
        var sut = CreateSut(enabled.HasValue
            ? new TokenMetricsCsvOptions { Enabled = enabled.Value, OutputDirectory = outputDirectory }
            : null);

        Assert.False(sut.IsEnabled);
        sut.Record(CreateRecord());

        Assert.False(Directory.Exists(outputDirectory));
        Assert.False(File.Exists(ConsolidatedPath));
    }

    [Fact]
    public void Record_WhenBothOutputModesDisabled_Disabled()
    {
        var sut = CreateSut(new TokenMetricsCsvOptions
        {
            Enabled = true,
            OutputDirectory = outputDirectory,
            GenerateIndividualFiles = false,
            AppendConsolidatedFile = false
        });

        Assert.False(sut.IsEnabled);
        sut.Record(CreateRecord());

        Assert.False(Directory.Exists(outputDirectory));
    }

    [Fact]
    public void Record_WritesPerRequestFile_WithHeaderAndAggregateRow()
    {
        var sut = CreateSut(new TokenMetricsCsvOptions
        {
            Enabled = true,
            OutputDirectory = outputDirectory,
            AppendConsolidatedFile = false
        });
        Assert.True(sut.IsEnabled);

        var requestId = Guid.NewGuid();
        sut.Record(new TokenMetricsCsvRecord(
            DateTimeOffset.UtcNow, requestId, "Qwen3.8-27B", "Local", "/api/chat",
            new TokenMetrics(Words: 120, Symbols: 7, NonAscii: 4, CharCount: 655),
            3413, 3533, null, null, 1234, 200, true));

        var files = Directory.EnumerateFiles(outputDirectory, "*_metrics.csv").ToArray();
        var file = Assert.Single(files);

        // File name pattern: {yyyyMMdd-HHmmss-fff}_{sequence:D6}_{modelSlug}_metrics.csv
        Assert.Matches(@"^\d{8}-\d{6}-\d{3}_\d{6}_Qwen3\.8-27B_metrics\.csv$", Path.GetFileName(file));

        var lines = File.ReadAllLines(file);
        Assert.Equal(2, lines.Length);
        Assert.Equal(ExpectedHeader, lines[0]);

        var fields = lines[1].Split(',');
        Assert.Equal(16, fields.Length);
        Assert.Equal(requestId.ToString("D"), fields[1]);
        Assert.Equal("Qwen3.8-27B", fields[2]);
        Assert.Equal("Local", fields[3]);
        Assert.Equal("/api/chat", fields[4]);
        Assert.Equal("120", fields[5]);
        Assert.Equal("7", fields[6]);
        Assert.Equal("4", fields[7]);
        Assert.Equal("655", fields[8]);
        Assert.Equal("3413", fields[9]);
        Assert.Equal("3533", fields[10]);
        Assert.True(fields[11] == string.Empty, "null actual prompt tokens must render as an empty field");
        Assert.True(fields[12] == string.Empty, "null actual response tokens must render as an empty field");
        Assert.Equal("1234", fields[13]);
        Assert.Equal("200", fields[14]);
        Assert.True(fields[15] == "true", "success must be lowercase true/false");

        // CRLF line endings, RFC 4180.
        var raw = File.ReadAllText(file);
        Assert.Contains("\r\n", raw);
        Assert.DoesNotContain("\n", raw.Replace("\r\n", string.Empty));
    }

    [Fact]
    public void Record_MetricsMatchHeuristicEstimatorAnalyze()
    {
        var sut = CreateSut(new TokenMetricsCsvOptions
        {
            Enabled = true,
            OutputDirectory = outputDirectory,
            AppendConsolidatedFile = false
        });

        var prompt = new[] { "hello", "world", "café 123" };
        var expected = global::OllamaRouter.Services.HeuristicTokenEstimator.Analyze(prompt);
        sut.Record(new TokenMetricsCsvRecord(
            DateTimeOffset.UtcNow, Guid.NewGuid(), "qwen", "Local", "/api/generate",
            expected, 0, 42, null, null, 5, 200, true));

        var lines = File.ReadAllLines(Directory.EnumerateFiles(outputDirectory, "*_metrics.csv").Single());
        var fields = lines[1].Split(',');
        Assert.Equal(expected.Words.ToString(), fields[5]);
        Assert.Equal(expected.Symbols.ToString(), fields[6]);
        Assert.Equal(expected.NonAscii.ToString(), fields[7]);
        Assert.Equal(expected.CharCount.ToString(), fields[8]);
    }

    [Theory]
    [InlineData("model", "bad,model")]
    [InlineData("target", "bad\"target")]
    [InlineData("endpoint", "/api/chat\nnewline")]
    public void Record_Rfc4180Escaping_QuotesAndDoublesSpecialCharacters(string kind, string value)
    {
        var sut = CreateSut(new TokenMetricsCsvOptions
        {
            Enabled = true,
            OutputDirectory = outputDirectory,
            AppendConsolidatedFile = false
        });

        TokenMetricsCsvRecord record = kind switch
        {
            "model" => CreateRecord() with { Model = value },
            "target" => CreateRecord() with { Target = value },
            _ => CreateRecord() with { Endpoint = value }
        };

        sut.Record(record);

        // The escaped field starts right after timestamp_utc and request_id, i.e. the raw file
        // content must contain ",<escaped field>," (the data row may legally span physical lines
        // when the field embeds CR/LF, so assert on the raw content, not on a single line).
        var expectedEscaped = "\"" + value.Replace("\"", "\"\"") + "\"";
        Assert.Contains("," + expectedEscaped + ",", File.ReadAllText(Directory.EnumerateFiles(outputDirectory, "*_metrics.csv").Single()));
    }

    [Fact]
    public void Record_ConsolidatedFile_HeaderOnce_AndAppendsRows()
    {
        var sut = CreateSut(new TokenMetricsCsvOptions
        {
            Enabled = true,
            OutputDirectory = outputDirectory,
            GenerateIndividualFiles = false
        });

        sut.Record(CreateRecord());
        sut.Record(CreateRecord(actualPrompt: 999, success: false));

        var lines = File.ReadAllLines(ConsolidatedPath);
        Assert.True(lines.Length == 3, "header written once, one row per record");
        Assert.Equal(ExpectedHeader, lines[0]);

        var firstFields = lines[1].Split(',');
        var secondFields = lines[2].Split(',');
        Assert.Equal(16, firstFields.Length);
        Assert.Equal(16, secondFields.Length);
        Assert.Equal("999", secondFields[11]);
        Assert.Equal("false", secondFields[15]);

        // Header appears exactly once.
        Assert.Equal(1, lines.Count(l => l == ExpectedHeader));
    }

    [Fact]
    public void Record_MaxFiles_PrunesOldestIndividualFiles_NeverConsolidated()
    {
        var sut = CreateSut(new TokenMetricsCsvOptions
        {
            Enabled = true,
            OutputDirectory = outputDirectory,
            MaxFiles = 3
        });

        for (var i = 0; i < 5; i++)
        {
            sut.Record(CreateRecord(actualPrompt: 100 + i));
            // The sortable timestamp prefix uses millisecond precision; wait so ordinal name
            // order unambiguously matches creation order.
            Thread.Sleep(2);
        }

        var individual = Directory.EnumerateFiles(outputDirectory, "*_metrics.csv").Count();
        Assert.True(individual == 3, "only the 3 newest per-request files may remain");

        Assert.True(File.Exists(ConsolidatedPath), "the consolidated file must never be pruned");
        var lines = File.ReadAllLines(ConsolidatedPath);
        Assert.True(lines.Length == 6, "all 5 rows plus the single header must remain in the consolidated file");
        Assert.Equal(1, lines.Count(l => l == ExpectedHeader));
    }

    [Fact]
    public void Record_ZeroMaxFiles_KeepsAllIndividualFiles()
    {
        var sut = CreateSut(new TokenMetricsCsvOptions
        {
            Enabled = true,
            OutputDirectory = outputDirectory,
            MaxFiles = 0
        });

        for (var i = 0; i < 4; i++)
        {
            sut.Record(CreateRecord());
            Thread.Sleep(2);
        }

        Assert.Equal(4, Directory.EnumerateFiles(outputDirectory, "*_metrics.csv").Count());
    }

    [Fact]
    public void Record_UncreatableOutputDirectory_NeverThrows()
    {
        // A path whose parent is an existing FILE makes Directory.CreateDirectory throw.
        var blocker = Path.Combine(tempDirectory, "blocker.txt");
        File.WriteAllText(blocker, "not a directory");
        var sut = CreateSut(new TokenMetricsCsvOptions
        {
            Enabled = true,
            OutputDirectory = Path.Combine(blocker, "token-metrics")
        });

        Assert.True(sut.IsEnabled);
        var ex = RecordThrowsNope(() => sut.Record(CreateRecord()));
        Assert.Null(ex);
        Assert.False(Directory.Exists(Path.Combine(blocker, "token-metrics")));
    }

    // Asserts the delegate returns without throwing (fail-soft contract).
    private static Exception? RecordThrowsNope(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
