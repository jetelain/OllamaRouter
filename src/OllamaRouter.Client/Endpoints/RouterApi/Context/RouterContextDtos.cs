using System.Text.Json.Serialization;

namespace OllamaRouter.Endpoints.RouterApi.Context;

/// <summary>
/// Request body of <c>POST /api/router/context/evaluate</c>: the model to evaluate, the
/// estimated prompt token count and the client's static <c>options.num_ctx</c> (if any).
/// </summary>
public sealed record RouterContextEvaluateRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("estimatedTokens")] int EstimatedTokens,
    [property: JsonPropertyName("clientRequestedNumCtx")] int? ClientRequestedNumCtx = null);

/// <summary>
/// Preview response of <c>POST /api/router/context/evaluate</c>: the predicted target, the
/// num_ctx the sizing engine would inject, the context mode — one of the four lowercase engine
/// modes (task #26): <c>passthrough</c> (dense, payload untouched; <c>injectedNumCtx</c>
/// reports what would flow through), <c>clamped</c> (dense, oversized client value clamped
/// down to the pre-configured context), <c>baseline</c> / <c>expanded</c> (MoE two-bucket
/// ladder) — the margin-adjusted token estimate, the target's effective capacity (MoE
/// extensible limit / dense pre-configured CMax) and the winner's affinity score.
/// </summary>
public sealed record RouterContextEvaluateResponse(
    [property: JsonPropertyName("selectedTarget")] string SelectedTarget,
    [property: JsonPropertyName("injectedNumCtx")] int InjectedNumCtx,
    [property: JsonPropertyName("contextMode")] string ContextMode,
    [property: JsonPropertyName("estimatedTokensWithMargin")] int EstimatedTokensWithMargin,
    [property: JsonPropertyName("targetMaxContext")] int TargetMaxContext,
    [property: JsonPropertyName("affinityScore")] double AffinityScore);
