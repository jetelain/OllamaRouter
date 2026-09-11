namespace OllamaRouter.Services;

public enum RoutingTarget
{
    Local,
    Remote,

    /// <summary>
    /// Overflow target used when both Local and Remote are busy: the request is still sent to
    /// the Local instance (which relays to ollama.com cloud using its own signed-in credentials),
    /// but the model name is rewritten to the configured cloud equivalent.
    /// </summary>
    Cloud
}
