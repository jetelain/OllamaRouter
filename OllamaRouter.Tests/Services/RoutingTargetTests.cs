using System.Text.Json;
using OllamaRouter.Serialization;
using OllamaRouter.Services;

namespace OllamaRouter.Tests.Services;

public class RoutingTargetTests
{
    [Fact]
    public void Equality_StringConversion_IsCaseInsensitiveAndEqualsWellKnownTargets()
    {
        RoutingTarget fromString = "Local";

        Assert.Equal(RoutingTarget.Local, fromString);
        Assert.Equal(RoutingTarget.Local, new RoutingTarget("local"));
        Assert.Equal(new RoutingTarget("REMOTE"), RoutingTarget.Remote);
        Assert.Equal(new RoutingTarget("Cloud"), RoutingTarget.Cloud);
    }

    [Fact]
    public void Equality_DifferentNames_AreNotEqual()
    {
        Assert.NotEqual(RoutingTarget.Local, RoutingTarget.Remote);
        Assert.NotEqual(RoutingTarget.Local, RoutingTarget.Cloud);
        Assert.NotEqual(new RoutingTarget("desktop"), RoutingTarget.Local);
        Assert.True(RoutingTarget.Local != RoutingTarget.Remote);
    }

    [Fact]
    public void ImplicitStringConversion_RoundTrips()
    {
        Assert.Equal("Cloud", (string)RoutingTarget.Cloud);
        Assert.Equal("Local", (string)RoutingTarget.Local);

        string name = RoutingTarget.Remote;
        Assert.Equal("Remote", name);
        Assert.Equal(RoutingTarget.Remote, (RoutingTarget)name);
    }

    [Fact]
    public void ToString_ReturnsValue()
    {
        Assert.Equal("Local", RoutingTarget.Local.ToString());
        Assert.Equal("desktop", new RoutingTarget("desktop").ToString());
        Assert.Equal("", new RoutingTarget(null).ToString());
    }

    [Fact]
    public void DictionaryKey_LookupIsCaseInsensitive()
    {
        var counts = new Dictionary<RoutingTarget, int>
        {
            [RoutingTarget.Local] = 1
        };

        Assert.Equal(1, counts[new RoutingTarget("local")]);
        Assert.True(counts.ContainsKey((RoutingTarget)"LOCAL"));

        counts[RoutingTarget.Local] = 5;
        Assert.Single(counts);
        Assert.Equal(5, counts[(RoutingTarget)"local"]);
    }

    [Fact]
    public void Equality_Object_AndHashConsistency()
    {
        var a = (RoutingTarget)"laptop";
        var b = new RoutingTarget("LAPTOP");

        Assert.True(a.Equals((object)b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.False(a.Equals("laptop"));
    }

    [Fact]
    public void JsonDictionary_RoutingTargetKeys_RoundTripsViaSourceGeneratedContext()
    {
        // RoutingTarget-keyed dictionaries serialize via the implicit string conversion through
        // the source-generated context (Dictionary<RoutingTarget, ActivityStatisticsEntry> is
        // reachable from List<ActivityStatisticsDate>).
        var original = new List<ActivityStatisticsDate>
        {
            new()
            {
                Date = new DateTime(2026, 10, 21),
                Statistics =
                {
                    [RoutingTarget.Local] = new ActivityStatisticsEntry { TotalRequests = 1 },
                    [RoutingTarget.Cloud] = new ActivityStatisticsEntry { TotalRequests = 2 },
                    [new RoutingTarget("desktop")] = new ActivityStatisticsEntry { TotalRequests = 3 }
                }
            }
        };

        var json = JsonSerializer.Serialize(original, OllamaRouterJsonSerializerContext.Default.ListActivityStatisticsDate);
        var restored = JsonSerializer.Deserialize(json, OllamaRouterJsonSerializerContext.Default.ListActivityStatisticsDate);

        Assert.NotNull(restored);
        var day = Assert.Single(restored!);
        Assert.Equal(3, day.Statistics.Count);
        Assert.Equal(1, day.Statistics[RoutingTarget.Local].TotalRequests);
        Assert.Equal(2, day.Statistics[RoutingTarget.Cloud].TotalRequests);
        Assert.Equal(3, day.Statistics[new RoutingTarget("desktop")].TotalRequests);
    }
}
