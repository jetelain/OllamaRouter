using System.Diagnostics;

namespace OllamaRouter.Tests.TestHelpers;

/// <summary>
/// Minimal fake <see cref="TimeProvider"/> for deterministic clock-based tests (task #94):
/// <see cref="UtcNow"/> is settable, <see cref="Advance(TimeSpan)"/> moves it forward, and
/// everything else is delegated to <see cref="TimeProvider.System"/> (the base-class
/// implementations for <c>GetLocalNow</c>/<c>GetTimestamp</c>/<c>CreateTimer</c> etc.).
/// No external test package (e.g. Microsoft.Extensions.Time.Testing) is required.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;
    private long _frequency = TimeProvider.System.TimestampFrequency;

    /// <summary>
    /// The current (fake) UTC now; settable to pin the clock.
    /// </summary>
    public DateTimeOffset UtcNow
    {
        get => _utcNow;
        set => _utcNow = value;
    }

    /// <summary>
    /// Advances the fake clock by <paramref name="delta"/> (negative values move it back).
    /// </summary>
    public void Advance(TimeSpan delta) => _utcNow += delta;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override long TimestampFrequency => _frequency;

    public override long GetTimestamp() => TimeProvider.System.GetTimestamp();

    public override TimeZoneInfo LocalTimeZone => TimeProvider.System.LocalTimeZone;
}
