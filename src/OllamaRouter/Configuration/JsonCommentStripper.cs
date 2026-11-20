using System.Text;

namespace OllamaRouter.Configuration;

/// <summary>
/// JSONC → JSON preprocessor for the user-owned <c>config/config.json</c> load path
/// (task #146). Removes <c>//</c> line comments and <c>/* */</c> block comments and
/// trailing commas — but ONLY outside string literals, so URLs like
/// <c>http://127.0.0.1:11435</c> (which contain <c>//</c> INSIDE a string) survive
/// untouched.
/// <para>
/// Trailing-comma stripping is deliberate parity with the daemon's configuration layer
/// (<see cref="RouterConfigurationSources"/> pre-validation, which rides the lenient
/// .NET JSON configuration provider): without it, a file that loads fine at startup
/// would still block every save.
/// </para>
/// <para>
/// Implementation: two pure passes — (1) a state machine that removes the comments
/// (preserving line terminators and token separation), then (2) a string-aware scan
/// that drops a comma whose next non-whitespace character is a container closer
/// (comments are already gone after pass 1), IN PLACE so a strict-JSON input returns
/// byte-identical output.
/// </para>
/// <para>
/// Invariants: string contents are never altered (URLs with <c>//</c>, <c>/*</c>,
/// backslashes, Unicode all survive); the stripper never throws (any malformed input
/// yields whatever text it can — strict validation remains <see cref="JsonNode.Parse"/>'s
/// job); it is idempotent; a strict-JSON input without comments or trailing commas
/// returns byte-identical output.
/// </para>
/// </summary>
internal static class JsonCommentStripper
{
    /// <summary>
    /// Removes <c>//</c> and <c>/* */</c> comments (outside strings) and trailing commas
    /// (a comma whose next SIGNIFICANT character, skipping whitespace and whole comments,
    /// is <c>}</c> or <c>]</c>). Pure, synchronous, no I/O, never throws.
    /// </summary>
    public static string Strip(string jsonc)
    {
        ArgumentNullException.ThrowIfNull(jsonc);
        return RemoveTrailingCommas(RemoveComments(jsonc));
    }

    /// <summary>
    /// Pass 1: removes <c>//</c> and <c>/* */</c> comments outside string literals.
    /// Line-comment terminators are preserved (source line numbers in parse-error
    /// diagnostics stay aligned with the on-disk file); a block comment collapses to a
    /// single space (token separation — <c>"a":/*c*/1</c> stays parseable). An
    /// unterminated comment swallows to EOF — the result then fails <see cref="JsonNode.Parse"/>
    /// with a clear "unexpected end" style error downstream.
    /// </summary>
    private static string RemoveComments(string jsonc)
    {
        var output = new StringBuilder(jsonc.Length + 16);
        var inString = false;
        var inLineComment = false;
        var inBlockComment = false;

        for (var i = 0; i < jsonc.Length; i++)
        {
            var c = jsonc[i];

            if (inString)
            {
                // Verbatim copy; a backslash copies itself AND the following character so
                // an escaped quote (`"a\"b"`) cannot terminate the string early.
                output.Append(c);
                if (c == '\\' && i + 1 < jsonc.Length)
                {
                    i++;
                    output.Append(jsonc[i]);
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (inLineComment)
            {
                if (c == '\n')
                {
                    inLineComment = false;
                    output.Append('\n');
                }
                else if (c == '\r')
                {
                    inLineComment = false;
                    output.Append('\r');
                    if (i + 1 < jsonc.Length && jsonc[i + 1] == '\n')
                    {
                        i++;
                        output.Append('\n');
                    }
                }

                continue;
            }

            if (inBlockComment)
            {
                if (c == '*' && i + 1 < jsonc.Length && jsonc[i + 1] == '/')
                {
                    i++;
                    inBlockComment = false;
                    output.Append(' ');
                }

                continue;
            }

            // Normal state.
            if (c == '"')
            {
                output.Append('"');
                inString = true;
                continue;
            }

            if (c == '/' && i + 1 < jsonc.Length && (jsonc[i + 1] == '/' || jsonc[i + 1] == '*'))
            {
                i++;
                if (jsonc[i] == '/')
                {
                    inLineComment = true;
                }
                else
                {
                    inBlockComment = true;
                }

                continue;
            }

            output.Append(c);
        }

        // Unterminated block comment at EOF: close with the same token-separating space.
        if (inBlockComment)
        {
            output.Append(' ');
        }

        return output.ToString();
    }

    /// <summary>
    /// Pass 2: drops a trailing comma — a comma (outside a string) whose next
    /// NON-WHITESPACE character is <c>}</c> or <c>]</c> — IN PLACE, so every other
    /// character (including the whitespace around kept commas) keeps its exact position.
    /// Comments were already removed by pass 1, so only whitespace can intervene.
    /// </summary>
    private static string RemoveTrailingCommas(string text)
    {
        var output = new StringBuilder(text.Length);
        var inString = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                output.Append(c);
                if (c == '\\' && i + 1 < text.Length)
                {
                    i++;
                    output.Append(text[i]);
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                output.Append('"');
                inString = true;
                continue;
            }

            if (c == ',')
            {
                var j = i + 1;
                while (j < text.Length && char.IsWhiteSpace(text[j]))
                {
                    j++;
                }

                if (j < text.Length && (text[j] == '}' || text[j] == ']'))
                {
                    // Trailing comma: drop it (a comma at EOF or before any other value
                    // is kept — strict validation is the parser's job).
                    continue;
                }
            }

            output.Append(c);
        }

        return output.ToString();
    }
}
