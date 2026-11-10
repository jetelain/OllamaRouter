using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace OllamaRouter.Options;

/// <summary>
/// Never-changing <see cref="IOptionsMonitor{TOptions}"/> that always returns the captured
/// instance (live-by-reference: mutations to the captured instance are observed through
/// <see cref="CurrentValue"/> because the same reference is returned on every access).
/// <para>
/// Used by the backwards-compatible instance overload of
/// <c>OllamaRouterServiceCollectionExtensions.AddOllamaRouter(OllamaRouterOptions, ...)</c>
/// so the identical service graph (which consumes <c>IOptionsMonitor&lt;OllamaRouterOptions&gt;</c>)
/// resolves in both the configuration-driven production host and the instance-based unit-test /
/// mesh-fleet hosts. A caller-registered monitor (e.g. the test
/// <c>TestOptionsMonitor&lt;T&gt;</c>) wins because the extension registers via
/// <c>TryAddSingleton</c>.
/// </para>
/// <para>
/// <see cref="OnChange(Action{TOptions})"/> returns a registration whose change token never
/// fires (no rebind happens for instance-registered options); v1 supports unnamed options
/// only (<see cref="Get(string?)"/> ignores <paramref name="name"/>).
/// </para>
/// </summary>
/// <typeparam name="TOptions">The options type.</typeparam>
internal sealed class FixedOptionsMonitor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>
    : IOptionsMonitor<TOptions> where TOptions : class
{
    private readonly TOptions _options;

    /// <summary>
    /// Creates the monitor around <paramref name="options"/>.
    /// </summary>
    /// <param name="options">The options instance to expose (must not be null).</param>
    public FixedOptionsMonitor(TOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public TOptions CurrentValue => _options;

    /// <inheritdoc />
    public TOptions Get(string? name) => _options;

    /// <inheritdoc />
    public IDisposable OnChange(Action<TOptions> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return new NeverFiringRegistration();
    }

    /// <inheritdoc />
    public IDisposable OnChange(Action<TOptions, string?> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return new NeverFiringRegistration();
    }

    /// <summary>Registration whose change token never signals (no reloads for fixed options).</summary>
    private sealed class NeverFiringRegistration : IDisposable
    {
        private static readonly IChangeToken _neverToken = new NeverChangeToken();

        public IChangeToken GetChangeToken() => _neverToken;

        public void Dispose()
        {
        }

        /// <summary>Change token that never signals.</summary>
        private sealed class NeverChangeToken : IChangeToken
        {
            public bool HasChanged => false;

            public bool ActiveChangeCallbacks => false;

            public IDisposable RegisterChangeCallback(Action<object?> callback, object? state) => NullDisposable.Instance;

            /// <summary>Singleton no-op disposable.</summary>
            private sealed class NullDisposable : IDisposable
            {
                public static readonly NullDisposable Instance = new();

                public void Dispose()
                {
                }
            }
        }
    }
}
