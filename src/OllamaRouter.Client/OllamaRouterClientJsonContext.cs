using System.Text.Json;
using System.Text.Json.Serialization;
using OllamaRouter.Endpoints;
using OllamaRouter.Endpoints.RouterApi.ApiKeys;
using OllamaRouter.Endpoints.RouterApi.Benchmark;
using OllamaRouter.Endpoints.RouterApi.Cartography;
using OllamaRouter.Endpoints.RouterApi.Config;
using OllamaRouter.Endpoints.RouterApi.Context;
using OllamaRouter.Endpoints.RouterApi.Fleet;
using OllamaRouter.Endpoints.RouterApi.Info;
using OllamaRouter.Endpoints.RouterApi.Models;
using OllamaRouter.Endpoints.RouterApi.Preload;
using OllamaRouter.Endpoints.RouterApi.Recommendations;
using OllamaRouter.Endpoints.RouterApi.SessionAffinity;
using OllamaRouter.Endpoints.RouterApi.Sessions;
using OllamaRouter.Endpoints.RouterApi.Targets;
using OllamaRouter.Endpoints.RouterApi.Vram;
using OllamaRouter.Middleware;
using OllamaRouter.Options;
using OllamaRouter.Services.Benchmarking;
using OllamaRouter.Services.Hardware;
using OllamaRouter.Services.Monitoring;
using OllamaRouter.Services.Recommendations;

namespace OllamaRouter.Client;

/// <summary>
/// Source-generated JSON serializer context for every wire type of the shared client SDK
/// (task #98): all <c>/api/router/*</c> + <c>/monitor/api</c> DTOs, the transitive wire
/// contract types (usage roles, benchmark results, activity totals, hardware profiles) and
/// the shared error records. Reflection-free and NativeAOT-safe (no <c>JsonNode</c> /
/// <c>dynamic</c> / reflection-based <see cref="JsonSerializer"/> overloads anywhere in the
/// client — everything serializes through this context). <see cref="Dictionary{TKey,TValue}"/>
/// of target-name to enabled flag covers the flat <c>POST /targets</c> monitor wire shape.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNameCaseInsensitive = true)]
// Monitor /admin API (flat flag dictionary + monitor page state).
[JsonSerializable(typeof(Dictionary<string, bool>))]
[JsonSerializable(typeof(ReclaimVramResponse))]
[JsonSerializable(typeof(MonitorCostStatus))]
[JsonSerializable(typeof(MonitorFleetTargetItem))]
[JsonSerializable(typeof(MonitorStateResponse))]
[JsonSerializable(typeof(MonitorStatisticsStatus))]
[JsonSerializable(typeof(MonitorInProgressItem))]
[JsonSerializable(typeof(MonitorRecentRequestItem))]
[JsonSerializable(typeof(MonitorOverheadStatus))]
[JsonSerializable(typeof(MonitorOverheadRecommendation))]
// Info + admin lifecycle.
[JsonSerializable(typeof(RouterInfoResponse))]
[JsonSerializable(typeof(RouterFleetSummary))]
[JsonSerializable(typeof(RouterFeatures))]
[JsonSerializable(typeof(RouterShutdownResponse))]
// Fleet + execution.
[JsonSerializable(typeof(RouterFleetResponse))]
[JsonSerializable(typeof(RouterTargetItem))]
[JsonSerializable(typeof(RouterTargetHardware))]
[JsonSerializable(typeof(RouterTargetExecution))]
[JsonSerializable(typeof(RouterTargetPreload))]
// Models.
[JsonSerializable(typeof(RouterModelsResponse))]
[JsonSerializable(typeof(RouterModelSummary))]
[JsonSerializable(typeof(RouterModelDetailResponse))]
[JsonSerializable(typeof(RouterModelTargetCompatibility))]
// Model pull (task #125): the POST /api/router/models/pull request + NDJSON event.
[JsonSerializable(typeof(OllamaRouterModelPullRequest))]
[JsonSerializable(typeof(OllamaRouterModelPullEvent))]
// Cartography.
[JsonSerializable(typeof(RouterCartographyResponse))]
[JsonSerializable(typeof(CartographyNodeRow))]
[JsonSerializable(typeof(CartographyNodeHardware))]
[JsonSerializable(typeof(CartographyNodeLoadedModel))]
[JsonSerializable(typeof(CartographyModelRow))]
[JsonSerializable(typeof(CartographyDeploymentRow))]
[JsonSerializable(typeof(CartographyLoadedModelRow))]
// Context evaluation (the DTOs moved with the SDK; the endpoint is not yet on the client).
[JsonSerializable(typeof(RouterContextEvaluateRequest))]
[JsonSerializable(typeof(RouterContextEvaluateResponse))]
// Preload + VRAM reclaim.
[JsonSerializable(typeof(RouterModelPreloadRequest))]
[JsonSerializable(typeof(RouterModelPreloadCandidate))]
[JsonSerializable(typeof(RouterModelPreloadResponse))]
// Model uninstall (task #126): POST /api/router/models/uninstall wire DTOs.
[JsonSerializable(typeof(RouterModelUninstallRequest))]
[JsonSerializable(typeof(RouterModelUninstallResponse))]
[JsonSerializable(typeof(RouterModelUninstallTargetResult))]
[JsonSerializable(typeof(RouterVramReclaimRequest))]
[JsonSerializable(typeof(RouterVramReclaimTargetResult))]
[JsonSerializable(typeof(RouterVramReclaimResponse))]
// Fleet hardware profiles.
[JsonSerializable(typeof(RouterFleetHardwareResponse))]
[JsonSerializable(typeof(FleetHardwareNode))]
[JsonSerializable(typeof(NodeHardwareProfile))]
[JsonSerializable(typeof(GpuDeviceProfile))]
[JsonSerializable(typeof(SystemRamType))]
[JsonSerializable(typeof(HardwareProfileState))]
// Configuration + target CRUD.
[JsonSerializable(typeof(RouterConfigResponse))]
[JsonSerializable(typeof(RouterTargetConfigDto))]
[JsonSerializable(typeof(RouterCloudConfigDto))]
[JsonSerializable(typeof(RouterDynamicContextConfigDto))]
[JsonSerializable(typeof(RouterRecordingConfigDto))]
[JsonSerializable(typeof(RouterResilienceConfigDto))]
[JsonSerializable(typeof(RouterPricingConfigDto))]
[JsonSerializable(typeof(UpdateRouterConfigRequest))]
[JsonSerializable(typeof(UpdateCloudConfigRequest))]
[JsonSerializable(typeof(UpdateRouterConfigResponse))]
[JsonSerializable(typeof(AddTargetRequest))]
[JsonSerializable(typeof(AddTargetResponse))]
[JsonSerializable(typeof(UpdateTargetRequest))]
[JsonSerializable(typeof(UpdateTargetResponse))]
[JsonSerializable(typeof(TargetModelDto))]
[JsonSerializable(typeof(DeleteTargetResponse))]
// Failure-streak reset (task #95).
[JsonSerializable(typeof(ResetTargetStreakResponse))]
[JsonSerializable(typeof(ResetFleetStreaksResponse))]
[JsonSerializable(typeof(ResetFleetStreaksTarget))]
// Benchmarks + catalog roles.
[JsonSerializable(typeof(RouterBenchmarkRequest))]
[JsonSerializable(typeof(RouterBenchmarkRunResponse))]
[JsonSerializable(typeof(RouterBenchmarkResultsResponse))]
[JsonSerializable(typeof(RouterBenchmarkClearedResponse))]
// Benchmark candidate plan + per-target run-scoped exclusions (task #152).
[JsonSerializable(typeof(RouterBenchmarkCandidatesResponse))]
[JsonSerializable(typeof(RouterBenchmarkCandidateTarget))]
[JsonSerializable(typeof(RouterBenchmarkCandidateModel))]
[JsonSerializable(typeof(BenchmarkCandidateFit))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, IReadOnlyList<string>>))]
[JsonSerializable(typeof(BenchmarkRunStatus))]
[JsonSerializable(typeof(BenchmarkPhase))]
[JsonSerializable(typeof(BenchmarkTargetProgress))]
[JsonSerializable(typeof(BenchmarkContextResult))]
[JsonSerializable(typeof(BenchmarkTargetResult))]
[JsonSerializable(typeof(BenchmarkTargetState))]
[JsonSerializable(typeof(SpeedSnapshotEntry))]
[JsonSerializable(typeof(UsageRole))]
// Sessions (usage + affinity lease release).
[JsonSerializable(typeof(RouterSessionUsageListResponse))]
[JsonSerializable(typeof(RouterSessionUsageItem))]
[JsonSerializable(typeof(RouterSessionUsageDetailResponse))]
[JsonSerializable(typeof(RouterSessionUsageTargetItem))]
[JsonSerializable(typeof(RouterSessionAffinityReleaseResponse))]
// API key management.
[JsonSerializable(typeof(ApiKeysListResponse))]
[JsonSerializable(typeof(ApiKeyInfoResponse))]
[JsonSerializable(typeof(ApiKeyCreateRequest))]
[JsonSerializable(typeof(ApiKeyCreatedResponse))]
[JsonSerializable(typeof(ApiKeyDeletedResponse))]
[JsonSerializable(typeof(ApiKeyRotatedResponse))]
[JsonSerializable(typeof(ApiSecurityConfigDto))]
// Role recommendation matrix (task #66; wire contracts relocated into the Client SDK in
// task #103 — the wizard's Generate/Apply/Read surface, AOT-safe, no new packages).
[JsonSerializable(typeof(FleetRecommendations))]
[JsonSerializable(typeof(RoleRecommendation))]
[JsonSerializable(typeof(ModelNodeProfile))]
[JsonSerializable(typeof(NodeRoleProfile))]
[JsonSerializable(typeof(RouterRecommendationsGeneratedResponse))]
[JsonSerializable(typeof(RouterRecommendationsAppliedResponse))]
[JsonSerializable(typeof(RouterRecommendationsAppliedTarget))]
// Shared error / diagnostics records (also the non-2xx error-parsing target).
[JsonSerializable(typeof(OllamaErrorResponse))]
[JsonSerializable(typeof(AllOfflineDiagnostics))]
[JsonSerializable(typeof(OfflineTargetInfo))]
public partial class OllamaRouterClientJsonContext : JsonSerializerContext
{
}
