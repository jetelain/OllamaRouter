using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;
using OllamaRouter.Services.Clients;

namespace OllamaRouter.Services;

/// <summary>
/// Default <see cref="ITargetChatClientProvider"/> (task #61): a singleton that resolves the
/// LIVE <c>OllamaTargetOptions</c> snapshot per call and delegates to the pooled per-endpoint
/// factories — <see cref="IOllamaChatClientFactory"/> for <c>TargetType.Ollama</c> targets and
/// <see cref="IOpenAiChatClientFactory"/> for <c>TargetType.OpenAI</c> targets.
/// <para>
/// Subscribes to <c>IOptionsMonitor&lt;OllamaRouterOptions&gt;.OnChange</c> and RECONCILES the
/// pooled factories on every rebind: a target whose URL (and, for OpenAI, API key) changed or
/// whose name was removed has its pooled client dropped, so reconfigured/removed targets never
/// leak connection pools. Setup-API mutations that go through the in-memory apply +
/// <c>IConfiguration</c> reload fire the change token; a <c>FixedOptionsMonitor</c> test host
/// never fires it (pools then live for the process lifetime — bounded by the fleet size).
/// </para>
/// </summary>
public sealed class TargetChatClientProvider : ITargetChatClientProvider, IDisposable
{
    private readonly IOptionsMonitor<OllamaRouterOptions> _options;
    private readonly IOpenAiChatClientFactory _openAiFactory;
    private readonly IOllamaChatClientFactory _ollamaFactory;
    private readonly ILogger? _logger;
    private readonly IDisposable? _changeRegistration;

    // The last reconciled endpoint snapshot (name → endpoint identity), kept for the
    // OnChange diff (removed / reconfigured targets are dropped from the pools).
    private ImmutableDictionary<string, Endpoint> _known;

    // One-time warnings for TargetType.OsNative resolution attempts (bounded by the fleet size).
    private readonly ConcurrentDictionary<string, byte> _warnedOsNative = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Immutable identity of one routable target's client endpoint (the reconciliation diff key).</summary>
    private readonly record struct Endpoint(TargetType Type, Uri Url, string? ApiKey);

    public TargetChatClientProvider(
        IOptionsMonitor<OllamaRouterOptions> options,
        IOpenAiChatClientFactory openAiFactory,
        IOllamaChatClientFactory ollamaFactory,
        ILogger<TargetChatClientProvider>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _openAiFactory = openAiFactory ?? throw new ArgumentNullException(nameof(openAiFactory));
        _ollamaFactory = ollamaFactory ?? throw new ArgumentNullException(nameof(ollamaFactory));
        _logger = logger;
        _known = CaptureEndpoints(options.CurrentValue);
        _changeRegistration = options.OnChange(Reconcile);
    }

    /// <inheritdoc/>
    public IChatClient? GetClient(string targetName)
        => TryResolve(targetName, out var client) ? client : null;

    /// <inheritdoc/>
    public bool TryResolve(string targetName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IChatClient? client)
    {
        client = null;
        if (string.IsNullOrWhiteSpace(targetName))
        {
            return false;
        }

        var target = _options.CurrentValue.GetEffectiveTargets()
            .FirstOrDefault(t => string.Equals(t.Name, targetName, StringComparison.OrdinalIgnoreCase));
        if (target is null || string.IsNullOrWhiteSpace(target.Url) ||
            !Uri.TryCreate(target.Url, UriKind.Absolute, out var baseUrl))
        {
            return false;
        }

        switch (target.Type)
        {
            case TargetType.Ollama:
                client = _ollamaFactory.GetClient(baseUrl);
                return true;

            case TargetType.OpenAI:
                // The provider label (when configured) is the ChatClientMetadata provider name;
                // the target name is the fallback.
                var metadataName = string.IsNullOrWhiteSpace(target.Provider) ? target.Name! : target.Provider!;
                client = _openAiFactory.GetClient(new OpenAiTargetEndpoint(metadataName, baseUrl, target.ApiKey));
                return true;

            case TargetType.OsNative:
            default:
                // Reserved provider type (tasks #63/#64): no client implementation — the target
                // is never a dispatch candidate (the middleware skips it with a warning).
                if (_warnedOsNative.TryAdd(targetName, 0))
                {
                    _logger?.LogWarning(
                        "Target '{TargetName}' is of type {Type}: no IChatClient implementation exists until the " +
                        "OS-native provider lands; the target is not routable.",
                        targetName, target.Type);
                }

                return false;
        }
    }

    /// <summary>
    /// Reconciles the pooled factories against the freshly bound options: drops the pooled
    /// client of every target that was REMOVED or RECONFIGURED (URL and, for OpenAI, API key
    /// changes) since the last snapshot, then installs the new snapshot. Never throws — a
    /// disposal hiccup must not break the options pipeline.
    /// </summary>
    private void Reconcile(OllamaRouterOptions options, string? name)
    {
        var fresh = CaptureEndpoints(options);
        lock (_known)
        {
            foreach (var pair in _known)
            {
                if (!fresh.TryGetValue(pair.Key, out var current) || !current.Equals(pair.Value))
                {
                    DropPooledClient(pair.Value);
                }
            }

            _known = fresh;
            // Prune the one-time-warning set to the surviving names (bounded memory).
            foreach (var warned in _warnedOsNative.Keys)
            {
                if (!fresh.ContainsKey(warned))
                {
                    _warnedOsNative.TryRemove(warned, out _);
                }
            }
        }
    }

    private void DropPooledClient(Endpoint endpoint)
    {
        try
        {
            if (endpoint.Type == TargetType.OpenAI)
            {
                _openAiFactory.DropClient(new OpenAiTargetEndpoint(string.Empty, endpoint.Url, endpoint.ApiKey));
            }
            else if (endpoint.Type == TargetType.Ollama)
            {
                _ollamaFactory.DropClient(endpoint.Url);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to drop the pooled client of a removed/reconfigured target ({Url}); continuing.", endpoint.Url);
        }
    }

    private static ImmutableDictionary<string, Endpoint> CaptureEndpoints(OllamaRouterOptions options)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, Endpoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in options.GetEffectiveTargets())
        {
            if (string.IsNullOrWhiteSpace(target.Name) || string.IsNullOrWhiteSpace(target.Url) ||
                !Uri.TryCreate(target.Url, UriKind.Absolute, out var url) ||
                builder.ContainsKey(target.Name))
            {
                continue;
            }

            builder[target.Name] = new Endpoint(target.Type, url, target.ApiKey);
        }

        return builder.ToImmutable();
    }

    /// <inheritdoc/>
    public void Dispose() => _changeRegistration?.Dispose();
}
