using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using OllamaRouter.Configuration;
using OllamaRouter.Endpoints.RouterApi.Config;
using OllamaRouter.Options;
using OllamaRouter.Security;
using OllamaRouter.Tests.TestHelpers;
using Pmad.LocalApi.Security;

namespace OllamaRouter.Tests.Configuration;

/// <summary>
/// Task #146: the JSONC-tolerant LOAD path of <see cref="RouterConfigPersistence"/> —
/// comment/trailing-comma files load, merge and save as clean strict JSON
/// (read-tolerant, write-normalizing), a truly corrupt file blocks every persist
/// byte-identical with an actionable message + exactly one diagnostics log line,
/// a non-object root is rejected, and a comment-only / empty file is corrupt
/// (parity with the daemon's configuration provider).
/// </summary>
public class RouterConfigPersistenceJsoncTests : IAsyncLifetime
{
    private string _contentRoot = null!;
    private readonly CapturingLogger _logger = new();
    private RouterConfigPersistence _persistence = null!;
    private readonly TestOptionsMonitor<OllamaRouterOptions> _options = new(new OllamaRouterOptions());

    public async Task InitializeAsync()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), "task146-jsonc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_contentRoot);

        var services = new ServiceCollection();
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(h => h.ContentRootPath).Returns(_contentRoot);
        environment.SetupGet(h => h.ApplicationName).Returns("OllamaRouter.Tests");
        environment.SetupGet(h => h.EnvironmentName).Returns("Development");
        services.AddSingleton(environment.Object);
        _persistence = new RouterConfigPersistence(
            _options,
            services.BuildServiceProvider(),
            logger: _logger,
            keyProtector: new PlainKeyProtector(),
            credentialStore: new FakeCredentialStore());
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_contentRoot))
            {
                Directory.Delete(_contentRoot, recursive: true);
            }
        }
        catch
        {
            // Temp cleanup must never mask the test outcome.
        }

        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    //  Read-tolerant: comments + trailing commas load, output normalizes
    // ------------------------------------------------------------------

    [Fact]
    public async Task PersistTargets_CommentedFile_LoadsMergesAndSavesCleanStrictJson()
    {
        WriteConfigFile("""
            // fleet configuration (hand-edited)
            { /* user-owned layer */
              "OllamaRouter": {
                "TokenEstimationOverheadFactor": 1.11, // user tweak
                "Targets": [
                  { "Name": "Local", "Url": "http://127.0.0.1:11435", "SpeedFactor": 2.0, }, // trailing comma!
                  { "Name": "Remote", "Url": "http://aiserver.local:11434" },
                ],
              },
            }
            """);

        var result = await _persistence.PersistTargetsAsync(
        [
            new OllamaTargetOptions { Name = "Local", Url = "http://127.0.0.1:11435", Enabled = true },
            new OllamaTargetOptions { Name = "Remote", Url = "http://aiserver.local:11434", Enabled = true },
        ]);

        Assert.True(result.Success, result.Error);

        // The saved file is VALID STRICT JSON (default strict parse) — comments and
        // trailing commas are gone (read-tolerant, write-normalizing). URLs legitimately
        // contain `//`, so "clean" is proven by the stripper being a no-op on the output.
        var saved = File.ReadAllText(_persistence.ConfigFilePath);
        var root = JsonNode.Parse(saved) as JsonObject; // strict parse must succeed
        Assert.NotNull(root);
        Assert.Equal(saved, JsonCommentStripper.Strip(saved)); // no comments / trailing commas left
        Assert.DoesNotContain("/*", saved);

        // ALL values + every unmanaged key are preserved through the merge.
        var router = root!["OllamaRouter"]!;
        Assert.Equal(1.11, router["TokenEstimationOverheadFactor"]!.GetValue<double>());
        var targets = (JsonArray)router["Targets"]!;
        var local = targets[0] as JsonObject;
        Assert.Equal("http://127.0.0.1:11435", local!["Url"]!.GetValue<string>()); // URL with // intact
        Assert.Equal(2.0, local["SpeedFactor"]!.GetValue<double>());               // unmanaged key kept
        var remote = targets[1] as JsonObject;
        Assert.Equal("http://aiserver.local:11434", remote!["Url"]!.GetValue<string>());
    }

    [Fact]
    public async Task PersistTargets_TrailingCommasOnly_LoadsAndNormalizes()
    {
        WriteConfigFile("""
            {
              "OllamaRouter": {
                "Targets": [
                  { "Name": "Local", "Url": "http://127.0.0.1:11435", },
                ],
              },
            }
            """);

        var result = await _persistence.PersistTargetsAsync(
        [
            new OllamaTargetOptions { Name = "Local", Url = "http://127.0.0.1:11435", Enabled = true },
        ]);

        Assert.True(result.Success, result.Error);
        var saved = File.ReadAllText(_persistence.ConfigFilePath);
        var root = JsonNode.Parse(saved) as JsonObject; // strict parse must succeed
        var targets = (JsonArray)root!["OllamaRouter"]!["Targets"]!;
        Assert.Single(targets);
        Assert.Equal("Local", targets[0]!["Name"]!.GetValue<string>());
    }

    [Fact]
    public async Task PersistConfigAsync_CommentedFile_LoadsMergesAndSavesCleanStrictJson()
    {
        WriteConfigFile("""
            // hand-edited
            {
              "OllamaRouter": {
                "Recording": { "TokenMetricsCsv": { "OutputDirectory": "logs\\metrics", }, }, // comment with apostrophe: it's fine
                "Pricing": { "PricePerMillion": 3.5, },
              },
            }
            """);

        var result = await _persistence.PersistConfigAsync(
            new UpdateRouterConfigRequest(Pricing: new RouterPricingConfigDto(null, null, 4.2)));

        Assert.True(result.Success, result.Error);

        var saved = File.ReadAllText(_persistence.ConfigFilePath);
        var root = JsonNode.Parse(saved) as JsonObject; // strict
        var router = root!["OllamaRouter"]!;
        Assert.Equal(4.2, router["Pricing"]!["PricePerMillion"]!.GetValue<double>());
        // Unmanaged sibling section preserved through the merge.
        Assert.Equal("logs\\metrics", router["Recording"]!["TokenMetricsCsv"]!["OutputDirectory"]!.GetValue<string>());
        Assert.Equal(saved, JsonCommentStripper.Strip(saved)); // no comments / trailing commas left
        Assert.DoesNotContain("/*", saved);
    }

    // ------------------------------------------------------------------
    //  Truly corrupt = block, never touch
    // ------------------------------------------------------------------

    [Fact]
    public async Task PersistTargets_CorruptFile_ByteIdentical_ActionableMessage_DiagnosticsLogged()
    {
        var corrupt = """
            {
              "OllamaRouter": {
                "Targets": [
                  { "Name": "Local", "Url": "http://127
            """; // truncated mid-string
        WriteConfigFile(corrupt);
        var originalBytes = File.ReadAllBytes(_persistence.ConfigFilePath);

        var result = await _persistence.PersistTargetsAsync(
        [
            new OllamaTargetOptions { Name = "Local", Url = "http://127.0.0.1:11435", Enabled = true },
        ]);

        // The file is left BYTE-IDENTICAL (no write/rename/delete on the failure path).
        Assert.Equal(originalBytes, File.ReadAllBytes(_persistence.ConfigFilePath));

        // The failure message is actionable: absolute path + fix instructions.
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains(_persistence.ConfigFilePath, result.Error);
        Assert.Contains("fix or rename the file", result.Error);

        // Exactly ONE diagnostics error line, carrying the file size + the hex prefix.
        var diagnostics = _logger.Entries
            .Where(e => e.Level == LogLevel.Error && e.FormattedMessage.Contains("first bytes (hex)", StringComparison.Ordinal))
            .ToList();
        Assert.Single(diagnostics);
        var message = diagnostics[0].FormattedMessage;
        Assert.Contains(_persistence.ConfigFilePath, message);
        Assert.Contains($"file size {originalBytes.Length} bytes", message);
        Assert.Contains(Convert.ToHexString(originalBytes.AsSpan(0, Math.Min(64, originalBytes.Length))), message);
    }

    [Fact]
    public async Task PersistConfigAsync_CorruptFile_BlockedSameWay_FileUntouched()
    {
        WriteConfigFile("{ \"OllamaRouter\": { \"Pricing\": "); // truncated
        var originalBytes = File.ReadAllBytes(_persistence.ConfigFilePath);

        var result = await _persistence.PersistConfigAsync(
            new UpdateRouterConfigRequest(Pricing: new RouterPricingConfigDto(null, null, 1.0)));

        Assert.False(result.Success);
        Assert.Equal(originalBytes, File.ReadAllBytes(_persistence.ConfigFilePath));
        Assert.Contains(_persistence.ConfigFilePath, result.Error);
        Assert.Contains("fix or rename the file", result.Error);
    }

    [Fact]
    public async Task PersistTargets_CommentOnlyFile_IsCorrupt_MatchesConfigurationProvider()
    {
        // The daemon's configuration provider THROWS on comment-only files (treats them
        // as invalid at startup) — the persistence load path must agree: corrupt, not
        // an empty object.
        WriteConfigFile("// just comments\n/* nothing else */\n");
        var originalBytes = File.ReadAllBytes(_persistence.ConfigFilePath);

        var result = await _persistence.PersistTargetsAsync(
        [
            new OllamaTargetOptions { Name = "Local", Url = "http://127.0.0.1:11435", Enabled = true },
        ]);

        Assert.False(result.Success);
        Assert.Equal(originalBytes, File.ReadAllBytes(_persistence.ConfigFilePath));
        Assert.Contains(_persistence.ConfigFilePath, result.Error);
        Assert.Contains("fix or rename the file", result.Error);
    }

    [Fact]
    public async Task PersistTargets_WhitespaceOnlyFile_IsCorrupt()
    {
        WriteConfigFile("   \n\t  \n");
        var originalBytes = File.ReadAllBytes(_persistence.ConfigFilePath);

        var result = await _persistence.PersistTargetsAsync(
        [
            new OllamaTargetOptions { Name = "Local", Url = "http://127.0.0.1:11435", Enabled = true },
        ]);

        Assert.False(result.Success);
        Assert.Equal(originalBytes, File.ReadAllBytes(_persistence.ConfigFilePath));
        Assert.Contains("is not valid JSON", result.Error);
    }

    [Fact]
    public async Task PersistTargets_NonObjectRoot_ActionableMessage_FileUntouched()
    {
        WriteConfigFile("[1, 2, 3]");
        var originalBytes = File.ReadAllBytes(_persistence.ConfigFilePath);

        var result = await _persistence.PersistTargetsAsync(
        [
            new OllamaTargetOptions { Name = "Local", Url = "http://127.0.0.1:11435", Enabled = true },
        ]);

        Assert.False(result.Success);
        Assert.Equal(originalBytes, File.ReadAllBytes(_persistence.ConfigFilePath));
        Assert.Contains(_persistence.ConfigFilePath, result.Error);
        Assert.Contains("does not contain a JSON object at the top level", result.Error);
        Assert.Contains("fix or rename the file", result.Error);
    }

    [Fact]
    public async Task PersistTargets_Utf8BomPrefix_Loads()
    {
        // Hand-saved files may carry a UTF-8 BOM — not JSON itself, but must not count
        // as corruption (the loader drops the single leading BOM before parsing).
        Directory.CreateDirectory(Path.GetDirectoryName(_persistence.ConfigFilePath)!);
        // No-BOM writer: the single BOM in the text below becomes the file's one BOM
        // (the default File.WriteAllText would emit a SECOND, encoder-level BOM).
        File.WriteAllText(_persistence.ConfigFilePath, "\uFEFF{\n  \"OllamaRouter\": {\n  },\n}\n", new System.Text.UTF8Encoding(false));

        var result = await _persistence.PersistTargetsAsync(
        [
            new OllamaTargetOptions { Name = "Local", Url = "http://127.0.0.1:11435", Enabled = true },
        ]);

        Assert.True(result.Success, result.Error);
        var saved = File.ReadAllText(_persistence.ConfigFilePath);
        var root = JsonNode.Parse(saved) as JsonObject; // strict
        Assert.NotNull(root);
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private void WriteConfigFile(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_persistence.ConfigFilePath)!);
        File.WriteAllText(_persistence.ConfigFilePath, content);
    }

    /// <summary>Captures formatted log messages + levels for diagnostics assertions.</summary>
    private sealed class CapturingLogger : ILogger
    {
        private readonly object _gate = new();

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                Entries.Add(new Entry(logLevel, formatter(state, exception)));
            }
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

        internal List<Entry> Entries { get; } = [];

        internal record Entry(LogLevel Level, string FormattedMessage);

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
