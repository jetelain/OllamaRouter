using OllamaRouter.Tests.TestHelpers;
using Microsoft.Extensions.Options;

namespace OllamaRouter.Tests.TestHelpers;

/// <summary>
/// Lightweight, thread-safe <see cref="IOptionsMonitor{TOptions}"/> test double for verifying
/// live runtime configuration rebinding without a real configuration change token.
/// <para>
/// <see cref="SetValue"/> atomically swaps the current value and fires every registered
/// <see cref="OnChange"/> listener exactly once with the new value (listeners run outside the
/// internal lock, in registration order). <see cref="Trigger"/> re-fires listeners with the
/// unchanged current value (simulates a configuration reload that keeps the same values).
/// </para>
/// <para>
/// v1 supports unnamed options only: <see cref="Get(string?)"/> returns <see cref="CurrentValue"/>
/// regardless of <paramref name="name"/>.
/// </para>
/// </summary>
/// <typeparam name="TOptions">The options type.</typeparam>
public sealed class TestOptionsMonitor<TOptions> : IOptionsMonitor<TOptions> where TOptions : class
{
    private readonly object _gate = new();
    private TOptions _current;
    private readonly List<Action<TOptions>> _listeners = [];

    /// <summary>
    /// Creates the monitor with an initial value.
    /// </summary>
    /// <param name="initialValue">The initial options instance (must not be null).</param>
    public TestOptionsMonitor(TOptions initialValue)
    {
        ArgumentNullException.ThrowIfNull(initialValue);
        _current = initialValue;
    }

    /// <inheritdoc />
    public TOptions CurrentValue
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc />
    public TOptions Get(string? name) => CurrentValue;

    /// <summary>
    /// Atomically replaces the current value and fires every registered listener exactly once
    /// with the new value (registration order, outside the lock).
    /// </summary>
    /// <param name="value">The new options instance (must not be null).</param>
    public void SetValue(TOptions value)
    {
        ArgumentNullException.ThrowIfNull(value);
        List<Action<TOptions>> snapshot;
        lock (_gate)
        {
            _current = value;
            snapshot = _listeners.ToList();
        }

        foreach (var listener in snapshot)
        {
            listener(value);
        }
    }

    /// <summary>
    /// Fires every registered listener with the unchanged current value (simulates a
    /// configuration reload that kept the same values).
    /// </summary>
    public void Trigger()
    {
        List<Action<TOptions>> snapshot;
        TOptions value;
        lock (_gate)
        {
            value = _current;
            snapshot = _listeners.ToList();
        }

        foreach (var listener in snapshot)
        {
            listener(value);
        }
    }

    /// <inheritdoc />
    public IDisposable OnChange(Action<TOptions> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return Register(listener);
    }

    /// <inheritdoc />
    public IDisposable OnChange(Action<TOptions, string?> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return Register(value => listener(value, string.Empty));
    }

    private IDisposable Register(Action<TOptions> listener)
    {
        var registration = new Registration(this, listener);
        lock (_gate)
        {
            _listeners.Add(listener);
        }

        return registration;
    }

    /// <summary>Idempotent <see cref="IDisposable"/> that removes the listener once.</summary>
    private sealed class Registration : IDisposable
    {
        private readonly TestOptionsMonitor<TOptions> _monitor;
        private readonly Action<TOptions> _listener;
        private int _disposed;

        public Registration(TestOptionsMonitor<TOptions> monitor, Action<TOptions> listener)
        {
            _monitor = monitor;
            _listener = listener;
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (_monitor._gate)
            {
                _monitor._listeners.Remove(_listener);
            }
        }
    }
}
