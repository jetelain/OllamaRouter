namespace OllamaRouter.Services.Preload;

/// <summary>
/// Fail-soft outcome of a model preload load call (<see cref="IOllamaModelCatalogClient.PreloadModelAsync"/>):
/// <see cref="Accepted"/> is true when the target's daemon acknowledged the
/// <c>POST /api/generate</c> load with a 2xx response; <see cref="Error"/> carries the
/// non-2xx status (e.g. "http 500") or the transport/parse exception message (null on success).
/// The method never throws — an unreachable daemon yields <c>(false, ...)</c>.
/// </summary>
/// <param name="Accepted">True when the daemon accepted the load (2xx).</param>
/// <param name="Error">Failure description (non-2xx HTTP status or transport error); null on success.</param>
public sealed record PreloadOutcome(bool Accepted, string? Error = null);
