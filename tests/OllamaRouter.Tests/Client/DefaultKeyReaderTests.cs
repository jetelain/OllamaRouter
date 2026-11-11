using System.Runtime.Versioning;
using System.Security.Cryptography;
using OllamaRouter.Client;
using OllamaRouter.Security;

namespace OllamaRouter.Tests.Client;

/// <summary>
/// Unit tests for the local default-key reader (task #98): envelope unwrapping through
/// <see cref="IKeyProtector"/>, the fails-safe null contract (missing file / corrupt blob /
/// malformed envelope / failing protector — NEVER throws) and the resolution precedence
/// (explicit config directory → OLLAMAROUTER_HOME → platform default). Real DPAPI is never
/// touched in CI (a fake protector / PlainKeyProtector is injected; the one DPAPI round-trip
/// is Windows-only and skipped elsewhere).
/// </summary>
public sealed class DefaultKeyReaderTests : IDisposable
{
    private static readonly DateTimeOffset GeneratedAt = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempRoot;

    public DefaultKeyReaderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ollamarouter-client-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp-dir cleanup.
        }
    }

    /// <summary>Writes the exact <c>ApiKeyStore.PersistDefaultKey</c> envelope, wrapped by <paramref name="protector"/>.</summary>
    private static void WriteEnvelope(string configDirectory, string key, IKeyProtector protector)
    {
        Directory.CreateDirectory(configDirectory);
        var path = Path.Combine(configDirectory, "default-key.dat");
        var envelope = $"{{\"key\":\"{key}\",\"generatedAt\":\"{GeneratedAt:O}\"}}";
        File.WriteAllBytes(path, protector.Protect(System.Text.Encoding.UTF8.GetBytes(envelope)));
    }

    private static string MakeKey(string label) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(label)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void ValidEnvelope_PlainProtector_ReturnsExactRawKey()
    {
        var configDir = Path.Combine(_tempRoot, "config");
        var key = MakeKey("default-key-1");
        WriteEnvelope(configDir, key, new PlainKeyProtector());

        var reader = new DefaultKeyReader(configDir, new PlainKeyProtector());

        Assert.Equal(key, reader.ReadDefaultKey());
    }

    [Fact]
    public void ValidEnvelope_MarkerProtector_UnwrapsAndReturnsExactKey()
    {
        var protector = new MarkerProtector();
        var configDir = Path.Combine(_tempRoot, "config");
        var key = MakeKey("default-key-2");
        WriteEnvelope(configDir, key, protector);

        var reader = new DefaultKeyReader(configDir, protector);

        Assert.Equal(key, reader.ReadDefaultKey());
    }

    [Fact]
    public void MissingFile_ReturnsNull()
    {
        var reader = new DefaultKeyReader(Path.Combine(_tempRoot, "does-not-exist"), new PlainKeyProtector());

        Assert.Null(reader.ReadDefaultKey());
    }

    [Fact]
    public void CorruptBlob_NonJson_ReturnsNull()
    {
        var configDir = Path.Combine(_tempRoot, "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllBytes(Path.Combine(configDir, "default-key.dat"), new byte[] { 0x00, 0x01, 0x02, 0xFF });

        var reader = new DefaultKeyReader(configDir, new PlainKeyProtector());

        Assert.Null(reader.ReadDefaultKey());
    }

    [Fact]
    public void EnvelopeMissingKeyProperty_ReturnsNull()
    {
        var configDir = Path.Combine(_tempRoot, "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "default-key.dat"),
            $"{{\"generatedAt\":\"{GeneratedAt:O}\"}}");

        var reader = new DefaultKeyReader(configDir, new PlainKeyProtector());

        Assert.Null(reader.ReadDefaultKey());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EnvelopeWithEmptyKey_ReturnsNull(string key)
    {
        var configDir = Path.Combine(_tempRoot, "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "default-key.dat"),
            $"{{\"key\":\"{key}\",\"generatedAt\":\"{GeneratedAt:O}\"}}");

        var reader = new DefaultKeyReader(configDir, new PlainKeyProtector());

        Assert.Null(reader.ReadDefaultKey());
    }

    [Fact]
    public void ProtectorThrowing_ReturnsNull()
    {
        var configDir = Path.Combine(_tempRoot, "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllBytes(Path.Combine(configDir, "default-key.dat"), new byte[] { 1, 2, 3 });

        var reader = new DefaultKeyReader(configDir, new ThrowingProtector());

        Assert.Null(reader.ReadDefaultKey());
    }

    [Fact]
    public void KeyFileWrittenByOtherCandidate_ExplicitConfigDirectoryWins()
    {
        var explicitDir = Path.Combine(_tempRoot, "explicit");
        var homeDir = Path.Combine(_tempRoot, "home");
        var explicitKey = MakeKey("explicit-key");
        var homeKey = MakeKey("home-key");
        WriteEnvelope(explicitDir, explicitKey, new PlainKeyProtector());
        Directory.CreateDirectory(Path.Combine(homeDir, "config"));
        WriteEnvelope(homeDir, homeKey, new PlainKeyProtector());

        var reader = new DefaultKeyReader(explicitDir, new PlainKeyProtector(),
            name => name == "OLLAMAROUTER_HOME" ? homeDir : null);

        Assert.Equal(explicitKey, reader.ReadDefaultKey());
        Assert.Equal(Path.Combine(explicitDir, "default-key.dat"), reader.KeyFilePath);
    }

    [Fact]
    public void KeyFileInHome_EnvVarBeatsPlatformDefault()
    {
        var homeDir = Path.Combine(_tempRoot, "home");
        var homeKey = MakeKey("home-key");
        WriteEnvelope(Path.Combine(homeDir, "config"), homeKey, new PlainKeyProtector());

        var reader = new DefaultKeyReader(null, new PlainKeyProtector(),
            name => name == "OLLAMAROUTER_HOME" ? homeDir : null);

        Assert.Equal(homeKey, reader.ReadDefaultKey());
        Assert.Equal(Path.Combine(homeDir, "config", "default-key.dat"), reader.KeyFilePath);
    }

    [Fact]
    public void NoCandidatesExist_KeyFilePathIsFirstInPriorityOrder()
    {
        var reader = new DefaultKeyReader(null, new PlainKeyProtector(), _ => null);

        // No explicit dir, no env var → the platform default is reported.
        Assert.EndsWith(Path.Combine("default-key.dat"), reader.KeyFilePath);
        Assert.Null(reader.ReadDefaultKey());
    }

    [Fact]
    public void PlatformDefault_Unix_HomeDotOllamarouterConfig()
    {
        var homeDir = Path.Combine(_tempRoot, "platform-home");
        var key = MakeKey("platform-key");
        WriteEnvelope(Path.Combine(homeDir, ".ollamarouter", "config"), key, new PlainKeyProtector());

        // The platform default derives from the user profile (HOME on Unix). Point it at the
        // temp dir for the duration of the assertion and restore it afterwards.
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // The Unix layout assertion only applies off-Windows; on Windows the reader
                // resolves %LOCALAPPDATA%\OllamaRouter\config (not unit-testable safely).
                return;
            }

            Environment.SetEnvironmentVariable("HOME", homeDir);
            var reader = new DefaultKeyReader(null, new PlainKeyProtector(), _ => null);
            Assert.Equal(key, reader.ReadDefaultKey());
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", originalHome);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task PlatformDefault_Windows_DpapiRoundTrip()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Skipped off-Windows (DPAPI is Windows-only).
        }

        var configDir = Path.Combine(Path.GetTempPath(), "ollamarouter-client-tests",
            "dpapi-" + Guid.NewGuid().ToString("N"));
        var key = MakeKey("dpapi-key");
        var protector = new DpapiKeyProtector();
        WriteEnvelope(configDir, key, protector);
        try
        {
            var reader = new DefaultKeyReader(configDir, protector);
            Assert.Equal(key, await Task.Run(reader.ReadDefaultKey));
        }
        finally
        {
            Directory.Delete(configDir, recursive: true);
        }
    }

    [Fact]
    public void RepeatedReads_ReReadFromDisk_NoCaching()
    {
        var configDir = Path.Combine(_tempRoot, "config");
        var first = MakeKey("first-key");
        var second = MakeKey("second-key");
        WriteEnvelope(configDir, first, new PlainKeyProtector());

        var reader = new DefaultKeyReader(configDir, new PlainKeyProtector());
        Assert.Equal(first, reader.ReadDefaultKey());

        // Simulate a default-key rotation on disk: the next read must observe the new key.
        WriteEnvelope(configDir, second, new PlainKeyProtector());
        Assert.Equal(second, reader.ReadDefaultKey());
    }

    [Fact]
    public void CreateDefault_DoesNotThrowAndResolvesAPath()
    {
        var reader = DefaultKeyReader.CreateDefault();

        Assert.False(string.IsNullOrWhiteSpace(reader.KeyFilePath));
        // Must not throw whether or not the local default-key file exists.
        _ = reader.ReadDefaultKey();
    }

    /// <summary>Prefix/suffix marker transform: stands in for DPAPI wrapping in the CI (no real DPAPI).</summary>
    private sealed class MarkerProtector : IKeyProtector
    {
        private static readonly byte[] Marker = { 0xAB, 0xCD, 0xEF };

        public byte[] Protect(byte[] plaintext) => [.. Marker, .. plaintext];

        public byte[] Unprotect(byte[] protectedData)
        {
            if (protectedData.Length < Marker.Length ||
                !protectedData.AsSpan(0, Marker.Length).SequenceEqual(Marker))
            {
                throw new CryptographicException("bad marker");
            }

            return protectedData.AsSpan(Marker.Length).ToArray();
        }
    }

    /// <summary>Stands in for a DPAPI blob that cannot be unwrapped (wrong user / tampered).</summary>
    private sealed class ThrowingProtector : IKeyProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;

        public byte[] Unprotect(byte[] protectedData) =>
            throw new CryptographicException("unwrappable");
    }
}
