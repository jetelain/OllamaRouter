using System.Text.Json.Serialization;
using OllamaRouter.Endpoints;
using OllamaRouter.Middleware;
using OllamaRouter.Services;

namespace OllamaRouter.Serialization;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(TargetsState))]
[JsonSerializable(typeof(List<ActivityStatisticsDate>))]
[JsonSerializable(typeof(OllamaErrorResponse))]
[JsonSerializable(typeof(MonitorStateResponse))]
[JsonSerializable(typeof(MonitorCostStatus))]
[JsonSerializable(typeof(TargetsUpdateRequest))]
[JsonSerializable(typeof(TargetsUpdateResponse))]
[JsonSerializable(typeof(ReclaimVramResponse))]
[JsonSerializable(typeof(MonitorOverheadStatus))]
[JsonSerializable(typeof(MonitorOverheadRecommendation))]
public partial class OllamaRouterJsonSerializerContext : JsonSerializerContext
{
}

