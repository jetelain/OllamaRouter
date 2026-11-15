using OllamaRouter.Client;
using OllamaRouter.Client.Process;
using OllamaRouter.Security;

namespace OllamaRouter.Cli;

/// <summary>
/// The per-invocation context handed to every <see cref="Commands.IRouterCommand"/> (task
/// #100): the shared <see cref="IOllamaRouterApiClient"/> + <see cref="IOllamaRouterDaemonLauncher"/>
/// (ONE base URL for both), the base URL and the stdout/stderr writers.
/// <para>
/// The base URL is resolved from the <c>OLLAMAROUTER_URL</c> environment variable when set
/// (fallback: <see cref="OllamaRouterDaemonLauncher.DefaultBaseUrl"/>, the conventional Ollama
/// loopback port) — tests build the context explicitly with a fake client/launcher +
/// <see cref="System.IO.StringWriter"/>s and never touch the environment.
/// </para>
/// </summary>
public sealed class CliContext
{
    /// <summary>The environment variable that overrides the daemon base URL.</summary>
    public const string UrlEnvironmentVariable = "OLLAMAROUTER_URL";

    private CliContext(
        IOllamaRouterApiClient client,
        IOllamaRouterDaemonLauncher launcher,
        Uri baseUrl,
        System.IO.TextWriter output,
        System.IO.TextWriter error)
    {
        Client = client;
        Launcher = launcher;
        BaseUrl = baseUrl;
        Output = output;
        Error = error;
    }

    /// <summary>The management/monitoring API client.</summary>
    public IOllamaRouterApiClient Client { get; }

    /// <summary>The daemon lifecycle launcher (shares the client's base URL).</summary>
    public IOllamaRouterDaemonLauncher Launcher { get; }

    /// <summary>The daemon base URL (always with a trailing slash).</summary>
    public Uri BaseUrl { get; }

    /// <summary>The standard-output writer (default: <see cref="Console.Out"/>).</summary>
    public System.IO.TextWriter Output { get; }

    /// <summary>The standard-error writer (default: <see cref="Console.Error"/>).</summary>
    public System.IO.TextWriter Error { get; }

    /// <summary>
    /// The <c>/monitor/api</c> endpoint URL of the configured daemon (footer link of the TUI).
    /// </summary>
    public string MonitorApiUrl => BaseUrl.ToString().TrimEnd('/') + "/monitor/api";

    /// <summary>
    /// Builds the production context: real client + launcher over the resolved base URL
    /// (<c>OLLAMAROUTER_URL</c> env override, fallback default) and the console writers.
    /// </summary>
    public static CliContext Create(
        Uri? baseUrl = null,
        IOllamaRouterApiClient? client = null,
        IOllamaRouterDaemonLauncher? launcher = null,
        System.IO.TextWriter? output = null,
        System.IO.TextWriter? error = null,
        System.Net.Http.HttpClient? httpClient = null)
    {
        var url = baseUrl ?? ResolveBaseUrl();
        var keyReader = OllamaRouterDefaultKeyReader.Create();
        var resolvedClient = client ?? new OllamaRouterApiClient(url, keyReader: keyReader, httpClient: httpClient);
        var resolvedLauncher = launcher ?? new OllamaRouterDaemonLauncher(
            url, keyReader: keyReader, httpClient: httpClient);
        return new CliContext(resolvedClient, resolvedLauncher, url, output ?? Console.Out, error ?? Console.Error);
    }

    /// <summary>
    /// Resolves the base URL: <c>OLLAMAROUTER_URL</c> when set (a relative scheme is accepted
    /// — "127.0.0.1:11434" becomes "http://127.0.0.1:11434/"), else the conventional default.
    /// </summary>
    public static Uri ResolveBaseUrl()
    {
        var configured = Environment.GetEnvironmentVariable(UrlEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return OllamaRouterDaemonLauncher.DefaultBaseUrl;
        }

        var value = configured.Trim();
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "http://" + value;
        }

        return new Uri(value, UriKind.Absolute); // throws UriFormatException → usage error at the call site
    }
}
