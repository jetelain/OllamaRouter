namespace OllamaRouter.Endpoints;

public static partial class MonitorEndpointsExtensions
{
    /// <summary>
    /// Monitor page markup loaded from the embedded <c>monitor.html</c> resource
    /// (see <c>OllamaRouter.csproj</c>), preserving the raw asset byte-for-byte
    /// without shipping a ~560-line string literal in source.
    /// </summary>
    private static readonly string MonitorPageHtml = ReadMonitorPageHtml();

    private static string ReadMonitorPageHtml()
    {
        var resourceName = "OllamaRouter.Endpoints.monitor.html";
        using var stream = typeof(MonitorEndpointsExtensions).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
