using OllamaRouter.Security;

namespace OllamaRouter.Tests.Security;

/// <summary>
/// Unit tests for the API key crypto helpers (task #50): key generation format (32 bytes /
/// 43-char base64url / uniqueness), the format pre-filter (wrong length, non-base64url chars,
/// embedded padding / spaces) and the SHA-256 key hashing.
/// </summary>
public class ApiKeyCryptoTests
{
    [Fact]
    public void GenerateKey_Produces43CharBase64Url_DecodingTo32Bytes()
    {
        var key = ApiKeyCrypto.GenerateKey();
        Assert.Equal(ApiKeyCrypto.EncodedKeyLength, key.Length);
        Assert.All(
            key,
            c => Assert.True(
                (c is >= 'A' and <= 'Z') || (c is >= 'a' and <= 'z') || (c is >= '0' and <= '9') || c == '-' || c == '_',
                $"'{c}' is not a base64url character"));
        Assert.DoesNotContain('=', key);
        Assert.True(ApiKeyCrypto.IsValidApiKeyFormat(key));
    }

    [Fact]
    public void GenerateKey_IsUnique()
    {
        var keys = new HashSet<string>();
        for (var i = 0; i < 200; i++)
        {
            Assert.True(keys.Add(ApiKeyCrypto.GenerateKey()), $"key collision at iteration {i}");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("a")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789-")] // 37 chars (too short)
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789-ABCDEF")] // exactly 43 valid base64url chars
    public void IsValidApiKeyFormat_LengthAndShape(string? key)
    {
        if (key is "abcdefghijklmnopqrstuvwxyz0123456789-ABCDEF")
        {
            // 43 chars of base64url decode to exactly 32 bytes → structurally valid.
            Assert.True(ApiKeyCrypto.IsValidApiKeyFormat(key));
            return;
        }

        Assert.False(ApiKeyCrypto.IsValidApiKeyFormat(key));
    }

    [Fact]
    public void IsValidApiKeyFormat_RejectsNonBase64UrlCharacters()
    {
        var key = ApiKeyCrypto.GenerateKey();
        // Embed a '+' (standard-base64, not base64url), a '/' and a space at distinct positions.
        Assert.False(ApiKeyCrypto.IsValidApiKeyFormat($"+{key.AsSpan(1)}"));
        Assert.False(ApiKeyCrypto.IsValidApiKeyFormat($"e/{key.AsSpan(2)}"));
        Assert.False(ApiKeyCrypto.IsValidApiKeyFormat($" {key.AsSpan(1)}"));
        // Embedded standard-base64 padding is not base64url.
        Assert.False(ApiKeyCrypto.IsValidApiKeyFormat($"e={key.AsSpan(2)}"));
    }

    [Fact]
    public void IsValidApiKeyFormat_RejectsWrongLength()
    {
        var key = ApiKeyCrypto.GenerateKey();
        Assert.False(ApiKeyCrypto.IsValidApiKeyFormat(key[..42]));
        Assert.False(ApiKeyCrypto.IsValidApiKeyFormat(key + "a"));
    }

    [Fact]
    public void IsValidApiKeyFormat_AcceptsWithAndWithoutUrlSymbols()
    {
        // A canonical 32-byte key containing '-' and '_' in the alphabet is still valid.
        var key = ApiKeyCrypto.ToBase64Url([
            0x6b, 0xc1, 0xe9, 0x07, 0x2b, 0x4f, 0xd3, 0xa1, 0x58, 0xc2, 0x9e, 0x7a,
            0x34, 0x88, 0xfe, 0x60, 0x1d, 0x95, 0xbb, 0x42, 0xe6, 0x3c, 0x7f, 0x81,
            0xab, 0x2d, 0x5e, 0xc4, 0x90, 0x16, 0xd3, 0x7a]);
        Assert.Equal(43, key.Length);
        Assert.True(ApiKeyCrypto.IsValidApiKeyFormat(key));
    }

    [Fact]
    public void ToBase64Url_MapsStandardSymbolsAndStripsPadding()
    {
        // 0xFB 0xEF 0xBE 0x95 → standard base64 "++++lQ==" → base64url "----lQ".
        Assert.Equal("----lQ", ApiKeyCrypto.ToBase64Url([0xFB, 0xEF, 0xBE, 0x95]));
        // 3 bytes without padding: "abc" → "YWJj".
        Assert.Equal("YWJj", ApiKeyCrypto.ToBase64Url([0x61, 0x62, 0x63]));
    }

    [Fact]
    public void HashKey_IsSha256OfUtf8Key_AndDeterministic()
    {
        var key = ApiKeyCrypto.GenerateKey();
        var expected = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        Assert.Equal(expected, ApiKeyCrypto.HashKey(key));
        Assert.Equal(expected, ApiKeyCrypto.HashKey(key));
        Assert.NotEqual(expected, ApiKeyCrypto.HashKey(ApiKeyCrypto.GenerateKey()));
    }
}
