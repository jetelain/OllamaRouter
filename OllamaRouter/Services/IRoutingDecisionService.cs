namespace OllamaRouter.Services;

/// <summary>
/// Decides whether a request should be routed to the local or remote Ollama instance. The
/// decision, as well as the choice of information needed to make it (free VRAM, model already
/// loaded...), is entirely encapsulated by the service: the caller only needs to provide the
/// estimated token count and the model name.
/// </summary>
public interface IRoutingDecisionService
{
    /// <summary>
    /// Determines whether the request should be routed to the local or remote instance.
    /// </summary>
    /// <param name="tokenCount">Estimated token count of the prompt.</param>
    /// <param name="modelName">Name of the model targeted by the request.</param>
    Task<RoutingTarget> DecideAsync(int tokenCount, string modelName, CancellationToken cancellationToken = default);
}
