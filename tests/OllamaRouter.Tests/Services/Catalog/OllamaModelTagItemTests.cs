using System.Globalization;
using System.Text.Json.Nodes;
using OllamaRouter.Options;
using OllamaRouter.Services.Catalog;

namespace OllamaRouter.Tests.Services.Catalog;

public class OllamaModelTagItemTests
{
    private static OllamaTargetOptions Target(string? provider = null, params (string Name, string? Quant, int? Ctx)[] roster)
    {
        var target = new OllamaTargetOptions { Name = "Provider", Provider = provider };
        foreach (var (name, quant, ctx) in roster)
        {
            target.Models.Add(new TargetModelConfig { Name = name, Quantization = quant, ContextLength = ctx });
        }

        return target;
    }

    // --- /api/tags node shape ---------------------------------------------------------------

    [Fact]
    public void ToOllamaTagNode_ProducesExactOllamaShape()
    {
        var modifiedAt = DateTimeOffset.UtcNow;
        var item = new OllamaModelTagItem("gpt-4o", modifiedAt, 0, "gpt", "cloud", "cloud");

        var node = item.ToOllamaTagNode();

        Assert.Equal("gpt-4o", node["name"]?.GetValue<string>());
        Assert.Equal("gpt-4o", node["model"]?.GetValue<string>());
        Assert.Equal(0L, node["size"]?.GetValue<long>());
        Assert.True(DateTimeOffset.TryParseExact(
            node["modified_at"]!.GetValue<string>(), "o", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed));
        Assert.Equal(modifiedAt.UtcDateTime, parsed.UtcDateTime);

        var details = node["details"]!.AsObject();
        Assert.Equal("gpt", details["family"]?.GetValue<string>());
        Assert.Equal("cloud", details["parameter_size"]?.GetValue<string>());
        Assert.Equal("cloud", details["quantization_level"]?.GetValue<string>());
    }

    [Fact]
    public void ToOpenAiModelNode_ProducesExactOpenAiShape()
    {
        var modifiedAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000L);
        var item = new OllamaModelTagItem("gpt-4o", modifiedAt, 0, "gpt", "cloud", "cloud", OwnedBy: "openai");

        var node = item.ToOpenAiModelNode();

        Assert.Equal("gpt-4o", node["id"]?.GetValue<string>());
        Assert.Equal("model", node["object"]?.GetValue<string>());
        Assert.Equal(1_700_000_000L, node["created"]?.GetValue<long>());
        Assert.Equal("openai", node["owned_by"]?.GetValue<string>());
    }

    // --- FromOpenAiModel --------------------------------------------------------------------

    [Fact]
    public void FromOpenAiModel_ReadsIdCreatedAndAppliesConventions()
    {
        var modelNode = new JsonObject { ["id"] = "gpt-4o", ["created"] = 1_700_000_000L };

        var item = OllamaModelTagItem.FromOpenAiModel(modelNode, Target());

        Assert.Equal("gpt-4o", item.Name);
        Assert.Equal(0, item.Size);
        Assert.Equal(1_700_000_000L, item.ModifiedAt.ToUnixTimeSeconds());
        // Heuristic family + the "cloud" conventions for undisclosed params / quantization.
        Assert.Equal("gpt", item.Family);
        Assert.Equal("cloud", item.ParameterSize);
        Assert.Equal("cloud", item.QuantizationLevel);
        Assert.Equal("Provider", item.OwnedBy);
    }

    [Fact]
    public void FromOpenAiModel_CreatedAbsentOrNonPositive_FallsBackToUtcNow()
    {
        var before = DateTimeOffset.UtcNow;
        var noCreated = OllamaModelTagItem.FromOpenAiModel(new JsonObject { ["id"] = "m1" }, Target());
        var zeroCreated = OllamaModelTagItem.FromOpenAiModel(new JsonObject { ["id"] = "m2", ["created"] = 0 }, Target());
        var negativeCreated = OllamaModelTagItem.FromOpenAiModel(new JsonObject { ["id"] = "m3", ["created"] = -5 }, Target());
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(noCreated.ModifiedAt, before, after);
        Assert.InRange(zeroCreated.ModifiedAt, before, after);
        Assert.InRange(negativeCreated.ModifiedAt, before, after);
        // In particular: NOT epoch (the created=0 guard).
        Assert.NotEqual(0L, zeroCreated.ModifiedAt.ToUnixTimeSeconds());
    }

    [Fact]
    public void FromOpenAiModel_UnknownModel_ProviderLabelBecomesFamily()
    {
        var item = OllamaModelTagItem.FromOpenAiModel(new JsonObject { ["id"] = "mystery-1" }, Target(provider: "MyProvider"));

        Assert.Equal("myprovider", item.Family);
        Assert.Equal("cloud", item.ParameterSize);
        Assert.Equal("cloud", item.QuantizationLevel);
    }

    [Fact]
    public void FromOpenAiModel_LiveModelInRoster_ReceivesQuantizationHint()
    {
        var target = Target(provider: "openai", ("gpt-4o", "none", 98_304));

        var item = OllamaModelTagItem.FromOpenAiModel(new JsonObject { ["id"] = "GPT-4O" }, target);

        Assert.Equal("none", item.QuantizationLevel);
        Assert.Equal("gpt", item.Family);
    }

    [Fact]
    public void FromOpenAiModel_LiveModelNotInRoster_QuantizationStaysCloud()
    {
        var target = Target(provider: "openai", ("gpt-4o", "none", 98_304));

        var item = OllamaModelTagItem.FromOpenAiModel(new JsonObject { ["id"] = "gpt-4o-mini" }, target);

        Assert.Equal("cloud", item.QuantizationLevel);
    }

    [Fact]
    public void FromOpenAiModel_KnownFamily_CarriesHumanReadableParameterSize()
    {
        var item = OllamaModelTagItem.FromOpenAiModel(new JsonObject { ["id"] = "llama-3.3-70b" }, Target());

        Assert.Equal("llama", item.Family);
        Assert.Equal("70.0B", item.ParameterSize);
    }

    // --- FromRosterEntry --------------------------------------------------------------------

    [Fact]
    public void FromRosterEntry_AppliesEntryHints()
    {
        var entry = new TargetModelConfig { Name = "  gpt-4o ", Quantization = "none", ContextLength = 98_304 };

        var item = OllamaModelTagItem.FromRosterEntry(entry, Target());

        Assert.Equal("gpt-4o", item.Name);
        Assert.Equal(0, item.Size);
        Assert.Equal("gpt", item.Family);
        Assert.Equal("cloud", item.ParameterSize);
        Assert.Equal("none", item.QuantizationLevel);
    }

    [Fact]
    public void FromRosterEntry_NoQuantizationHint_FallsBackToCloud()
    {
        var entry = new TargetModelConfig { Name = "claude-3-5-sonnet" };

        var item = OllamaModelTagItem.FromRosterEntry(entry, Target(provider: "anthropic"));

        Assert.Equal("claude", item.Family);
        Assert.Equal("cloud", item.QuantizationLevel);
    }

    [Fact]
    public void FromRosterEntry_UnknownName_UsesProviderFamilyOrCloud()
    {
        var withProvider = OllamaModelTagItem.FromRosterEntry(
            new TargetModelConfig { Name = "mystery-1" }, Target(provider: "Groq"));
        var withoutProvider = OllamaModelTagItem.FromRosterEntry(
            new TargetModelConfig { Name = "mystery-1" }, Target());

        Assert.Equal("groq", withProvider.Family);
        Assert.Equal("cloud", withoutProvider.Family);
    }

    // --- RosterOpenAiModelNode (created = 0 convention) --------------------------------------

    [Fact]
    public void RosterOpenAiModelNode_CarriesCreatedZeroAndOwnedBy()
    {
        var node = OllamaModelTagItem.RosterOpenAiModelNode(
            new TargetModelConfig { Name = "claude-3-5-sonnet" }, Target(provider: "anthropic"));

        Assert.Equal("claude-3-5-sonnet", node["id"]?.GetValue<string>());
        Assert.Equal("model", node["object"]?.GetValue<string>());
        Assert.Equal(0L, node["created"]?.GetValue<long>());
        Assert.Equal("anthropic", node["owned_by"]?.GetValue<string>());
    }

    [Fact]
    public void OwnedBy_FallsBackToTargetName_WhenProviderUnset()
    {
        var item = OllamaModelTagItem.FromRosterEntry(new TargetModelConfig { Name = "m1" }, Target(provider: null));

        Assert.Equal("Provider", item.OwnedBy);
    }
}
