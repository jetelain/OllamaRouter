using System.Text.Json.Nodes;
using OllamaRouter.Services;

namespace OllamaRouter.Tests.Services;

public class ModelMetadataParserTests
{
    private static JsonObject Show(params (string Key, JsonNode? Value)[] entries)
    {
        var root = new JsonObject();
        foreach (var (key, value) in entries)
        {
            root[key] = value;
        }

        return root;
    }

    // --- num_ctx extraction -----------------------------------------------------------

    [Fact]
    public void ParseNumCtxFromParameters_SingleLine_ReturnsValue()
    {
        Assert.Equal(49152, ModelMetadataParser.ParseNumCtxFromParameters("num_ctx 49152"));
    }

    [Fact]
    public void ParseNumCtxFromParameters_MultiLineWithOtherParameters_ReturnsValue()
    {
        const string parameters = "stop 0\nnum_ctx 262144\nnum_predict 4096\ntemperature 0.7";
        Assert.Equal(262144, ModelMetadataParser.ParseNumCtxFromParameters(parameters));
    }

    [Fact]
    public void ParseNumCtxFromParameters_NoNumCtx_ReturnsNull()
    {
        Assert.Null(ModelMetadataParser.ParseNumCtxFromParameters("num_predict 4096\ntemperature 0.7"));
    }

    [Fact]
    public void ParseNumCtxFromParameters_NonNumericValue_ReturnsNull()
    {
        // "num_ctx abc" does not match the line-anchored numeric regex.
        Assert.Null(ModelMetadataParser.ParseNumCtxFromParameters("num_ctx abc\nnum_predict 4096"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseNumCtxFromParameters_NullOrEmpty_ReturnsNull(string? parameters)
    {
        Assert.Null(ModelMetadataParser.ParseNumCtxFromParameters(parameters));
    }

    [Fact]
    public void ParseNumCtxFromModelfile_ParameterLine_ReturnsValue()
    {
        const string modelfile = "FROM qwen3:27b\nTEMPLATE qwen\nPARAMETER num_ctx 262144\nPARAMETER temperature 0.7\n";
        Assert.Equal(262144, ModelMetadataParser.ParseNumCtxFromModelfile(modelfile));
    }

    [Fact]
    public void ParseNumCtxFromModelfile_NoParameterLine_ReturnsNull()
    {
        const string modelfile = "FROM qwen3:27b\nTEMPLATE qwen\n";
        Assert.Null(ModelMetadataParser.ParseNumCtxFromModelfile(modelfile));
    }

    [Fact]
    public void ParseNumCtx_PreferencesParametersBlockOverModelfile()
    {
        var show = Show(
            ("parameters", (JsonNode)"num_ctx 49152\n"),
            ("modelfile", "FROM qwen3:27b\nPARAMETER num_ctx 262144\n"));
        Assert.Equal(49152, ModelMetadataParser.ParseNumCtx(show));
    }

    [Fact]
    public void ParseNumCtx_FallsBackToModelfile_WhenParametersBlockMissing()
    {
        var show = Show(("modelfile", "FROM qwen3:27b\nPARAMETER num_ctx 262144\n"));
        Assert.Equal(262144, ModelMetadataParser.ParseNumCtx(show));
    }

    [Fact]
    public void ParseNumCtx_NoSignals_ReturnsNull()
    {
        var show = Show(("modelfile", "FROM qwen3:27b\n"));
        Assert.Null(ModelMetadataParser.ParseNumCtx(show));
        Assert.Null(ModelMetadataParser.ParseNumCtx(null));
    }

    // --- parameter count ---------------------------------------------------------------

    [Fact]
    public void ExtractParameterCount_ModelInfoNumber_WinsOverParameterSize()
    {
        var modelInfo = new JsonObject { ["general.parameter_count"] = (JsonNode)27_800_000_000L };
        Assert.Equal(27_800_000_000L, ModelMetadataParser.ExtractParameterCount(modelInfo, "27.8B"));
    }

    [Fact]
    public void ExtractParameterCount_ModelInfoString_IsAccepted()
    {
        var modelInfo = new JsonObject { ["general.parameter_count"] = (JsonNode)"27800000000" };
        Assert.Equal(27_800_000_000L, ModelMetadataParser.ExtractParameterCount(modelInfo, null));
    }

    [Fact]
    public void ExtractParameterCount_NoModelInfo_FallsBackToParameterSizeRegex()
    {
        Assert.Equal(27_800_000_000L, ModelMetadataParser.ExtractParameterCount(null, "27.8B"));
        Assert.Equal(4_000_000_000L, ModelMetadataParser.ExtractParameterCount(null, "4B"));
        Assert.Equal(138_000_000L, ModelMetadataParser.ExtractParameterCount(null, "138M"));
        Assert.Equal(4_000L, ModelMetadataParser.ExtractParameterCount(null, "4K"));
        Assert.Null(ModelMetadataParser.ExtractParameterCount(null, "garbage"));
        Assert.Null(ModelMetadataParser.ExtractParameterCount(null, null));
    }

    // --- expert count ------------------------------------------------------------------

    [Fact]
    public void ExtractExpertCount_ExpertKeyPresent_ReturnsValue()
    {
        var modelInfo = new JsonObject { ["qwen35moe.expert_count"] = (JsonNode)128L, ["general.parameter_count"] = (JsonNode)35_000_000_000L };
        Assert.Equal(128, ModelMetadataParser.ExtractExpertCount(modelInfo));
    }

    [Fact]
    public void ExtractExpertCount_MultipleExpertKeys_ReturnsMaximum()
    {
        var modelInfo = new JsonObject
        {
            ["a.expert_count"] = (JsonNode)64L,
            ["b.expert_count"] = (JsonNode)256L
        };
        Assert.Equal(256, ModelMetadataParser.ExtractExpertCount(modelInfo));
    }

    [Fact]
    public void ExtractExpertCount_NoExpertKeysOrNull_ReturnsZero()
    {
        var modelInfo = new JsonObject { ["general.parameter_count"] = (JsonNode)27_800_000_000L };
        Assert.Equal(0, ModelMetadataParser.ExtractExpertCount(modelInfo));
        Assert.Equal(0, ModelMetadataParser.ExtractExpertCount(null));
    }

    // --- native context length -----------------------------------------------------

    [Fact]
    public void ExtractNativeContextLength_SingleKey_ReturnsValue()
    {
        var modelInfo = new JsonObject { ["qwen3.context_length"] = (JsonNode)32_768L };
        Assert.Equal(32_768L, ModelMetadataParser.ExtractNativeContextLength(modelInfo));
    }

    [Fact]
    public void ExtractNativeContextLength_MultipleKeys_ReturnsMaximum()
    {
        var modelInfo = new JsonObject
        {
            ["a.context_length"] = (JsonNode)32_768L,
            ["b.context_length"] = (JsonNode)131_072L,
            ["c.context_length"] = (JsonNode)65_536L
        };
        Assert.Equal(131_072L, ModelMetadataParser.ExtractNativeContextLength(modelInfo));
    }

    [Fact]
    public void ExtractNativeContextLength_NoKeysOrNull_ReturnsNull()
    {
        var modelInfo = new JsonObject { ["general.parameter_count"] = (JsonNode)27_800_000_000L };
        Assert.Null(ModelMetadataParser.ExtractNativeContextLength(modelInfo));
        Assert.Null(ModelMetadataParser.ExtractNativeContextLength(null));
    }

    [Fact]
    public void ExtractNativeContextLength_NonNumericValue_Ignored()
    {
        var modelInfo = new JsonObject
        {
            ["a.context_length"] = (JsonNode)"not-a-number",
            ["general.parameter_count"] = (JsonNode)27_800_000_000L
        };
        Assert.Null(ModelMetadataParser.ExtractNativeContextLength(modelInfo));
    }

    [Fact]
    public void ExtractNativeContextLength_ZeroOrNegative_Ignored()
    {
        var modelInfo = new JsonObject
        {
            ["a.context_length"] = (JsonNode)0L,
            ["b.context_length"] = (JsonNode)(-1L)
        };
        Assert.Null(ModelMetadataParser.ExtractNativeContextLength(modelInfo));
    }

    [Fact]
    public void ExtractNativeContextLength_EmbeddedAmongUnrelatedKeys_ReturnsValue()
    {
        var modelInfo = new JsonObject
        {
            ["general.parameter_count"] = (JsonNode)27_800_000_000L,
            ["qwen3.context_length"] = (JsonNode)40_960L,
            ["qwen3.block_count"] = (JsonNode)64L,
            ["qwen3.expert_count"] = (JsonNode)0L
        };
        Assert.Equal(40_960L, ModelMetadataParser.ExtractNativeContextLength(modelInfo));
    }

    // --- full metadata parse -------------------------------------------------------------


    [Fact]
    public void ParseModelMetadata_NoShow_ReturnsDegradedMetadata()
    {
        var metadata = ModelMetadataParser.ParseModelMetadata("old-model", new JsonArray(), null);

        Assert.Null(metadata.ParameterCount);
        Assert.Equal(0, metadata.ExpertCount);
        Assert.Null(metadata.ModelFamily);
        Assert.Null(metadata.ModelfileNumCtx);
        Assert.False(metadata.HasModelInfo);
        Assert.Null(metadata.ParametersRaw);
        Assert.Null(metadata.NativeContextLength);
    }

    [Fact]
    public void ParseModelMetadata_ModelInfoAbsent_HasModelInfoFalse()
    {
        var show = Show(("details", new JsonObject { ["family"] = "qwen2" }));
        var metadata = ModelMetadataParser.ParseModelMetadata("m", [], show);

        Assert.False(metadata.HasModelInfo);
        Assert.Equal("qwen2", metadata.ModelFamily);
    }

    [Fact]
    public void ParseModelMetadata_ModelInfoPresentWithoutParameterCount_UsesParameterSizeFallback()
    {
        var show = Show(
            ("details", new JsonObject { ["family"] = "llama", ["parameter_size"] = "9B" }),
            ("model_info", new JsonObject { ["llama.block_count"] = (JsonNode)48L }));
        var metadata = ModelMetadataParser.ParseModelMetadata("m", [], show);

        Assert.True(metadata.HasModelInfo);
        Assert.Equal(9_000_000_000L, metadata.ParameterCount);
    }

    [Fact]
    public void ParseModelMetadata_ContextLengthKey_PopulatesNativeContextLength()
    {
        var show = Show(
            ("details", new JsonObject { ["family"] = "qwen3" }),
            ("model_info", new JsonObject { ["qwen3.context_length"] = (JsonNode)262_144L }));

        var metadata = ModelMetadataParser.ParseModelMetadata("m", [], show);

        Assert.Equal(262_144L, metadata.NativeContextLength);
    }

    [Fact]
    public void ParseModelMetadata_NoContextLengthKey_NativeContextLengthNull()
    {
        var show = Show(("details", new JsonObject { ["family"] = "llama" }));
        var metadata = ModelMetadataParser.ParseModelMetadata("m", [], show);
        Assert.Null(metadata.NativeContextLength);
    }

}
