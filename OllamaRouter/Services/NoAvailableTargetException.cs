namespace OllamaRouter.Services;

/// <summary>
/// Thrown when no enabled routing target is capable of handling a request.
/// </summary>
public sealed class NoAvailableTargetException(string message) : InvalidOperationException(message);

