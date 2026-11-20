using OllamaRouter.Configuration;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OllamaRouter.Tests.Configuration;

/// <summary>
/// Task #146: the JSONC → JSON preprocessor used by the <c>config/config.json</c>
/// load path — line/block comment removal OUTSIDE strings only, trailing-comma
/// removal, line-terminator preservation, escape handling, the BOM pass-through,
/// the strict-JSON no-op invariant, and the never-throw contract.
/// </summary>
public class JsonCommentStripperTests
{
    [Fact]
    public void LineComment_Removed_NewlinePreserved()
    {
        var result = JsonCommentStripper.Strip("{\n// a comment\n  \"a\": 1\n}\n");

        Assert.Equal("{\n\n  \"a\": 1\n}\n", result);
        AssertValidJson(result);
    }

    [Fact]
    public void LineComment_AtEofWithoutTerminator_Removed()
    {
        var result = JsonCommentStripper.Strip("{ \"a\": 1 } // trailing comment");

        Assert.Equal("{ \"a\": 1 } ", result);
        AssertValidJson(result);
    }

    [Fact]
    public void LineComment_CrlfTerminator_Preserved()
    {
        var result = JsonCommentStripper.Strip("{ // c1\r\n  \"a\": 1\r\n}");

        Assert.Equal("{ \r\n  \"a\": 1\r\n}", result);
        AssertValidJson(result);
    }

    [Fact]
    public void LineComment_LoneCrTerminator_Preserved()
    {
        var result = JsonCommentStripper.Strip("{ // c1\r  \"a\": 1\r}");

        Assert.Equal("{ \r  \"a\": 1\r}", result);
        AssertValidJson(result);
    }

    [Fact]
    public void BlockComment_Removed_ReplacedBySingleSpace()
    {
        var result = JsonCommentStripper.Strip("{\"a\":/*c*/1}");

        // The comment collapses to ONE space so the tokens stay separated.
        Assert.Equal("{\"a\": 1}", result);
        AssertValidJson(result);
    }

    [Fact]
    public void BlockComment_MultiLine_Removed()
    {
        var result = JsonCommentStripper.Strip("{ /* first\n   second */ \"a\": 1 }");

        // Three spaces: the one before the comment + the token-separating replacement +
        // the one after it.
        Assert.Equal("{   \"a\": 1 }", result);
        AssertValidJson(result);
    }

    [Fact]
    public void BlockComment_AtEof_Removed()
    {
        var result = JsonCommentStripper.Strip("{ \"a\": 1 } /* done */");

        // Two trailing spaces: the ordinary space before the comment + the
        // token-separating replacement space.
        Assert.Equal("{ \"a\": 1 }  ", result);
        AssertValidJson(result);
    }

    [Fact]
    public void BlockComment_UnterminatedAtEof_SwallowsToEof_AndResultFailsParse()
    {
        // The stripper itself never throws — it swallows to EOF, and the RESULT then
        // fails strict parsing with a clear "unexpected end" style error downstream.
        var result = JsonCommentStripper.Strip("{ \"a\": 1 } /* never closed");

        // The two spaces: the ordinary space before the comment + the token-separating
        // space the unterminated comment region collapses to.
        Assert.Equal("{ \"a\": 1 }  ", result);
        Assert.ThrowsAny<JsonException>(() => JsonNode.Parse("{\"a\": 1 /* never closed"));
    }

    [Fact]
    public void CommentOnlyFile_YieldsEmptyText_DownstreamParseFails()
    {
        // Comment-only / empty documents are CORRUPT by contract (parity with the
        // daemon configuration provider, which refuses them at startup).
        var result = JsonCommentStripper.Strip("// only\n/* a comment */ // another");

        // Only whitespace survives (a newline terminator + token-separating spaces) —
        // the document is "empty" of JSON and MUST fail strict parsing downstream.
        Assert.All(result, c => Assert.True(char.IsWhiteSpace(c)));
        Assert.ThrowsAny<JsonException>(() => JsonNode.Parse(result));
    }

    [Fact]
    public void SlashSlashInsideString_Preserved()
    {
        var result = JsonCommentStripper.Strip("{ \"url\": \"http://127.0.0.1:11435\" }");

        Assert.Equal("{ \"url\": \"http://127.0.0.1:11435\" }", result);
        AssertValidJson(result);
    }

    [Fact]
    public void StarSlashInsideString_Preserved()
    {
        var result = JsonCommentStripper.Strip("{ \"p\": \"a/*b*/c\" }");

        Assert.Equal("{ \"p\": \"a/*b*/c\" }", result);
        AssertValidJson(result);
    }

    [Fact]
    public void BackslashesAndUnicodeInsideString_Preserved()
    {
        var jsonc = "{ \"path\": \"C:\\\\dir\\\\file\", \"emoji\": \"\\u0041\\ud83d\\ude00\" }";

        var result = JsonCommentStripper.Strip(jsonc);

        Assert.Equal(jsonc, result); // no comments → byte-identical
        AssertValidJson(result);
    }

    [Fact]
    public void EscapedQuoteInString_FollowedByRealComment_StringNotTerminatedEarly()
    {
        // The input text is: {"a \" b": 1} // c  — the escaped quote stays inside the
        // string, and the REAL comment after the closing brace is removed.
        var result = JsonCommentStripper.Strip("{\"a \\\" b\": 1} // c");

        Assert.Equal("{\"a \\\" b\": 1} ", result);
        AssertValidJson(result);
    }

    [Fact]
    public void EscapedBackslashBeforeQuote_DoesNotSwallowTheQuote()
    {
        // The value ends with a real (unescaped) quote — the comment after it IS removed.
        var result = JsonCommentStripper.Strip("{\"p\": \"a\\\\\"} // gone");

        Assert.Equal("{\"p\": \"a\\\\\"} ", result);
        AssertValidJson(result);
    }

    [Fact]
    public void TrailingComma_BeforeObjectClose_Removed()
    {
        var result = JsonCommentStripper.Strip("{ \"a\": 1, }");

        Assert.Equal("{ \"a\": 1 }", result);
        AssertValidJson(result);
    }

    [Fact]
    public void TrailingComma_BeforeArrayClose_Removed()
    {
        var result = JsonCommentStripper.Strip("[1, 2,]");

        Assert.Equal("[1, 2]", result);
        AssertValidJson(result);
    }

    [Fact]
    public void TrailingComma_WithWhitespace_Removed()
    {
        var result = JsonCommentStripper.Strip("{\n  \"a\": 1,\n}");

        Assert.Equal("{\n  \"a\": 1\n}", result);
        AssertValidJson(result);
    }

    [Fact]
    public void TrailingComma_WithInterveningComment_Removed()
    {
        var result = JsonCommentStripper.Strip("{ \"a\": 1, // why\n }");

        Assert.Equal("{ \"a\": 1 \n }", result);
        AssertValidJson(result);
    }

    [Fact]
    public void TrailingComma_BeforeArrayClose_WithBlockComment_Removed()
    {
        var result = JsonCommentStripper.Strip("[1, /* keep */ 2, ]");

        // The block comment collapses to one space (plus the ordinary space before it);
        // the trailing comma is dropped, the other comma stays in its exact position.
        Assert.Equal("[1,   2 ]", result);
        AssertValidJson(result);
    }

    [Fact]
    public void CommaLikeTextInsideString_Preserved()
    {
        var result = JsonCommentStripper.Strip("{ \"a\": \"a,b,\", }");

        Assert.Equal("{ \"a\": \"a,b,\" }", result);
        AssertValidJson(result);
    }

    [Fact]
    public void RealCommaFollowedByValue_Kept()
    {
        var result = JsonCommentStripper.Strip("[1, 2, 3]");

        Assert.Equal("[1, 2, 3]", result);
        AssertValidJson(result);
    }

    [Fact]
    public void MultipleCommas_KeptExactly()
    {
        // `,,` is invalid JSON either way — the stripper must not "fix" or drop it.
        var result = JsonCommentStripper.Strip("[1,,2]");

        Assert.Equal("[1,,2]", result);
        Assert.ThrowsAny<JsonException>(() => JsonNode.Parse(result));
    }

    [Fact]
    public void CommaPendingAcrossNestedValue_FlushedBeforeTheValue()
    {
        var result = JsonCommentStripper.Strip("[1,\n  { \"x\": 2 }]");

        Assert.Equal("[1,\n  { \"x\": 2 }]", result);
        AssertValidJson(result);
    }

    [Fact]
    public void Bom_PassesThrough_ByteIdentical()
    {
        // The stripper copies the BOM through verbatim (strict-validation no-op contract);
        // the persistence layer strips a leading BOM before parsing (STJ rejects it).
        var jsonc = "\uFEFF{ \"a\": 1 }";

        var result = JsonCommentStripper.Strip(jsonc);

        Assert.Equal(jsonc, result);
        AssertValidJson(result[1..]);
    }

    [Fact]
    public void StrictJson_NoOp_ByteIdentical()
    {
        var strict = """
            {
              "OllamaRouter": {
                "Targets": [
                  { "Name": "Local", "Url": "http://127.0.0.1:11435", "SpeedFactor": 2.0 },
                  { "Name": "Remote", "Url": "http://aiserver.local:11434", "Note": "a, b, // not a comment" }
                ],
                "Flag": false,
                "Nothing": null
              }
            }
            """;

        Assert.Equal(strict, JsonCommentStripper.Strip(strict));
    }

    [Fact]
    public void Idempotent()
    {
        var jsonc = """
            { // header
              "OllamaRouter": { /* section */
                "Targets": [
                  { "Name": "Local", "Url": "http://127.0.0.1:11435", }, // trailing
                ],
              },
            }
            """;
        var once = JsonCommentStripper.Strip(jsonc);

        Assert.Equal(once, JsonCommentStripper.Strip(once));
        AssertValidJson(once);
    }

    [Fact]
    public void RoundTrip_JsonNodeParseSucceeds_ForCommentAndTrailingCommaDocument()
    {
        var jsonc = """
            // fleet configuration
            { /* user-owned layer */
              "OllamaRouter": {
                "Targets": [
                  // local workstation
                  { "Name": "Local", "Url": "http://127.0.0.1:11435", "SpeedFactor": 2.0, },
                  { "Name": "Remote", "Url": "http://aiserver.local:11434", },
                ],
              },
            }
            // end
            """;

        AssertValidJson(JsonCommentStripper.Strip(jsonc));
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, JsonCommentStripper.Strip(string.Empty));
    }

    [Fact]
    public void RandomCommentNoise_NeverThrows()
    {
        // The stripper is a preprocessor: it never throws, strict validation is the
        // parser's job. These malformed inputs must all just produce text.
        var inputs = new[]
        {
            "//", "/*", "*/", "//", "\"unclosed string /* not a comment",
            "[1,", "{\"a\": 1, // eof", "/* unterminated",
        };
        foreach (var input in inputs)
        {
            var result = JsonCommentStripper.Strip(input);
            _ = result; // never throwing is the assertion
        }
    }

    private static void AssertValidJson(string json) =>
        _ = JsonNode.Parse(json);
}
