using System.Text.Json.Nodes;
using OllamaRouter.Services;

namespace OllamaRouter.Tests.Services;

public class OllamaModelCatalogClientMergeTests
{
    [Fact]
    public void MergeByKey_NoDuplicates_ReturnsAllEntries()
    {
        var remote = new JsonArray(new JsonObject { ["name"] = "modelA" });
        var local = new JsonArray(new JsonObject { ["name"] = "modelB" });

        var merged = OllamaModelCatalogClient.MergeByKey(remote, local, "name");

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void MergeByKey_DuplicateKey_HigherPriorityWins()
    {
        var local = new JsonArray(new JsonObject { ["name"] = "modelA", ["source"] = "local" });
        var remote = new JsonArray(new JsonObject { ["name"] = "modelA", ["source"] = "remote" });

        var merged = OllamaModelCatalogClient.MergeByKey(local, remote, "name");

        Assert.Single(merged);
        Assert.Equal("remote", merged[0]!["source"]!.ToString());
    }

    [Fact]
    public void MergeByKey_EmptyArrays_ReturnsEmptyArray()
    {
        var merged = OllamaModelCatalogClient.MergeByKey([], [], "name");

        Assert.Empty(merged);
    }
}
