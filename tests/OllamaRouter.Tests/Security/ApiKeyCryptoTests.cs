using Pmad.LocalApi.Security;

namespace OllamaRouter.Tests.Security;

/// <summary>
/// Unit tests for the API key crypto helpers (task #50/#113): key generation format (32 bytes /
/// 43-char base64url / uniqueness), the format pre-filter (wrong length, non-base64url chars,
/// embedded padding / spaces) and the SALTED SHA-256 key hashing (deterministic per salt,
/// salt-first ordering, salt uniqueness, GenerateSalt length/uniqueness).
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
    public void HashKey_IsSaltedSha256_SaltFirst_ThenUtf8Key_AndDeterministic()
    {
        var key = ApiKeyCrypto.GenerateKey();
        var salt = ApiKeyCrypto.GenerateSalt();

        // Deterministic: same key + same salt ⇒ identical hash (twice).
        var hash1 = ApiKeyCrypto.HashKey(key, salt);
        Assert.Equal(hash1, ApiKeyCrypto.HashKey(key, salt));

        // Independently computed expected value: SHA256(salt ‖ UTF8(key)), salt FIRST.
        var expected = System.Security.Cryptography.SHA256.HashData(
            [.. salt, .. System.Text.Encoding.UTF8.GetBytes(key)]);
        Assert.Equal(expected, hash1);
        Assert.Equal(32, hash1.Length);

        // A different raw key with the SAME salt yields a different hash.
        Assert.NotEqual(hash1, ApiKeyCrypto.HashKey(ApiKeyCrypto.GenerateKey(), salt));
    }

    [Fact]
    public void HashKey_DifferentSalt_DifferentHash_ForTheSameRawKey()
    {
        var key = ApiKeyCrypto.GenerateKey();
        var saltA = ApiKeyCrypto.GenerateSalt();
        var saltB = ApiKeyCrypto.GenerateSalt();
        Assert.NotEqual(saltA, saltB);

        // Same raw key, two different salts ⇒ different stored hashes (no cross-key collisions
        // are possible by construction).
        Assert.NotEqual(ApiKeyCrypto.HashKey(key, saltA), ApiKeyCrypto.HashKey(key, saltB));
    }

    [Fact]
    public void HashKey_IsSaltFirst_Ordering_NotKeyFirst()
    {
        var key = ApiKeyCrypto.GenerateKey();
        var salt = ApiKeyCrypto.GenerateSalt();

        // Ordering matters: SHA256(salt ‖ key) ≠ SHA256(key ‖ salt) for a 16-byte salt.
        var keyFirst = System.Security.Cryptography.SHA256.HashData(
            [.. System.Text.Encoding.UTF8.GetBytes(key), .. salt]);
        Assert.NotEqual(keyFirst, ApiKeyCrypto.HashKey(key, salt));

        // The API accepts ANY salt length (the store always uses 16 bytes):
        // an empty salt degenerates to plain SHA256(UTF8(key)).
        Assert.Equal(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)),
            ApiKeyCrypto.HashKey(key, System.Array.Empty<byte>()));

        // A short (non-standard) salt still works: SHA256(shortSalt ‖ UTF8(key)).
        var shortSalt = new byte[] { 1, 2, 3 };
        Assert.Equal(
            System.Security.Cryptography.SHA256.HashData([1, 2, 3, .. System.Text.Encoding.UTF8.GetBytes(key)]),
            ApiKeyCrypto.HashKey(key, shortSalt));
    }

    [Fact]
    public void GenerateSalt_ReturnsSixteenBytes_AndIsUniqueAcrossDraws()
    {
        var salt = ApiKeyCrypto.GenerateSalt();
        Assert.Equal(ApiKeyCrypto.SaltByteLength, salt.Length);
        Assert.Equal(16, ApiKeyCrypto.SaltByteLength);

        var seen = new HashSet<string>();
        for (var i = 0; i < 200; i++)
        {
            var s = ApiKeyCrypto.GenerateSalt();
            Assert.Equal(16, s.Length);
            Assert.True(seen.Add(Convert.ToBase64String(s)), $"salt collision at iteration {i}");
        }
    }
}
