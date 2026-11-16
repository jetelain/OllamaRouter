namespace OllamaRouter.Services.ModelUninstall;

/// <summary>
/// The fail-soft result of one daemon-level <c>DELETE /api/delete</c> call
/// (<see cref="IOllamaModelCatalogClient.DeleteModelAsync"/> in
/// <c>OllamaRouter.Services.Catalog</c>): <see cref="Deleted"/> true on a 2xx delete,
/// false otherwise with a bounded <see cref="Error"/> text (non-2xx → "http {status}"
/// + the daemon's <c>error</c> property when present; transport failure → the exception
/// message). The call never throws (mirroring <see cref="OllamaRouter.Services.Preload.PreloadOutcome"/>).
/// </summary>
public sealed record ModelDeleteOutcome(bool Deleted, string? Error = null);
