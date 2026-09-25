using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaRouter.Options;
using OllamaRouter.Services;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace OllamaRouter.Tests.Services;

public class InferenceRequestRecorderTests : IDisposable
{
    private const string RequestBody = """
        {
          "model": "Qwen3.8-27B",
          "messages": [ { "role": "user", "content": "hello" } ],
          "stream": false
        }
        """;

    private readonly string tempDirectory;
    private readonly string outputDirectory;

    public InferenceRequestRecorderTests()
    {
        tempDirectory = Directory.CreateTempSubdirectory("OllamaRouterTests").FullName;
        outputDirectory = Path.Combine(tempDirectory, "inference-requests");
    }

    public void Dispose()
    {
        Directory.Delete(tempDirectory, recursive: true);
    }

    private string ManifestPath => Path.Combine(outputDirectory, "requests.jsonl");

    private static ActivityLogEntry CreateEntry() => new(
        DateTimeOffset.UtcNow,
        RoutingTarget.Local,
        "Qwen3.8-27B",
        4096,
        3800,
        128,
        1234,
        200,
        true,
        3413);

    private InferenceRequestRecorder CreateSut(RecordingOptions? recording) =>
        new(
            MsOptions.Create(new OllamaRouterOptions { Recording = recording }),
            NullLogger<InferenceRequestRecorder>.Instance);

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void Record_WhenDisabled_CreatesNoFiles(bool? enabled)
    {
        var sut = CreateSut(enabled.HasValue
            ? new RecordingOptions { Enabled = enabled.Value, OutputDirectory = outputDirectory }
            : null);

        sut.Record(CreateEntry(), "/api/chat", RequestBody);

        Assert.False(Directory.Exists(outputDirectory));
        Assert.False(File.Exists(ManifestPath));
    }

    [Fact]
    public void Record_WritesManifestLineAndPrettyPayload()
    {
        var sut = CreateSut(new RecordingOptions { Enabled = true, OutputDirectory = outputDirectory });

        sut.Record(CreateEntry(), "/api/chat", RequestBody);

        var manifestLines = File.ReadAllLines(ManifestPath);
        Assert.Single(manifestLines);

        var line = JsonNode.Parse(manifestLines[0])!;
        Assert.Equal("Local", line["target"]!.GetValue<string>());
        Assert.Equal("Qwen3.8-27B", line["model"]!.GetValue<string>());
        Assert.Equal("/api/chat", line["endpoint"]!.GetValue<string>());
        Assert.Equal(4096, line["estimatedPromptTokens"]!.GetValue<int>());
        Assert.Equal(3413, line["rawPromptTokens"]!.GetValue<int>());
        Assert.Equal(3800, line["actualPromptTokens"]!.GetValue<int>());
        Assert.Equal(128, line["actualResponseTokens"]!.GetValue<int>());
        Assert.Equal(200, line["statusCode"]!.GetValue<int>());
        Assert.True(line["success"]!.GetValue<bool>());

        var payloadPath = Path.Combine(outputDirectory, line["file"]!.GetValue<string>());
        var payloadContent = File.ReadAllText(payloadPath);
        var payload = JsonNode.Parse(payloadContent)!;
        Assert.Equal("Qwen3.8-27B", payload["model"]!.GetValue<string>());
        Assert.True(payloadContent.Contains('\n'), "payload should be pretty-printed");
    }

    [Fact]
    public void Record_NonJsonRequestBody_IsStoredRaw()
    {
        var sut = CreateSut(new RecordingOptions { Enabled = true, OutputDirectory = outputDirectory });

        sut.Record(CreateEntry(), "/api/generate", "raw non-json body");

        var line = JsonNode.Parse(File.ReadAllLines(ManifestPath)[0])!;
        var payloadPath = Path.Combine(outputDirectory, line["file"]!.GetValue<string>());
        Assert.Equal("raw non-json body", File.ReadAllText(payloadPath));
    }

    [Fact]
    public void Record_ExceedingMaxFiles_DeletesOldestPayloadsAndTrimsManifest()
    {
        var sut = CreateSut(new RecordingOptions { Enabled = true, OutputDirectory = outputDirectory, MaxFiles = 3 });

        for (var i = 0; i < 5; i++)
        {
            sut.Record(CreateEntry(), "/api/chat", RequestBody);
        }

        var payloadFiles = Directory.EnumerateFiles(outputDirectory, "*.json")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(3, payloadFiles.Length);

        var manifestLines = File.ReadAllLines(ManifestPath);
        Assert.Equal(3, manifestLines.Length);
        foreach (var manifestLine in manifestLines)
        {
            var file = JsonNode.Parse(manifestLine)!["file"]!.GetValue<string>();
            Assert.True(File.Exists(Path.Combine(outputDirectory, file)), "manifest should only reference existing payloads");
        }
    }

    [Fact]
    public void Record_ZeroMaxFiles_KeepsAllPayloads()
    {
        var sut = CreateSut(new RecordingOptions { Enabled = true, OutputDirectory = outputDirectory, MaxFiles = 0 });

        for (var i = 0; i < 4; i++)
        {
            sut.Record(CreateEntry(), "/api/chat", RequestBody);
        }

        Assert.Equal(4, Directory.EnumerateFiles(outputDirectory, "*.json").Count());
        Assert.Equal(4, File.ReadAllLines(ManifestPath).Length);
    }
}
