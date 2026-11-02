using OllamaRouter.Tests.TestHelpers;

namespace OllamaRouter.Tests.TestHelpers;

public class TestOptionsMonitorTests
{
    private sealed class SampleOptions
    {
        public int Value { get; set; }
    }

    [Fact]
    public void SetValue_UpdatesCurrentValue_AndFiresListenersExactlyOnce_WithNewValue()
    {
        var monitor = new TestOptionsMonitor<SampleOptions>(new SampleOptions { Value = 1 });
        var calls = new List<int>();
        var reg1 = monitor.OnChange(o => calls.Add(o.Value));
        var reg2 = monitor.OnChange(o => calls.Add(o.Value));

        monitor.SetValue(new SampleOptions { Value = 42 });

        Assert.Equal(42, monitor.CurrentValue.Value);
        Assert.Equal([42, 42], calls);
        Assert.Equal(42, monitor.Get("any-name").Value);

        // No further firing without another change.
        monitor.SetValue(new SampleOptions { Value = 7 });
        Assert.Equal([42, 42, 7, 7], calls);
    }

    [Fact]
    public void OnChange_AfterSetValue_DoesNotFireUntilNextChange_AndDisposableDeregisters()
    {
        var monitor = new TestOptionsMonitor<SampleOptions>(new SampleOptions { Value = 1 });
        monitor.SetValue(new SampleOptions { Value = 2 });

        var calls = new List<int>();
        using var reg = monitor.OnChange(o => calls.Add(o.Value));

        Assert.Empty(calls); // late registration does not replay the previous change

        monitor.SetValue(new SampleOptions { Value = 3 });
        Assert.Equal([3], calls);

        reg.Dispose();
        monitor.SetValue(new SampleOptions { Value = 4 });
        Assert.Equal([3], calls); // deregistered listener no longer fires

        reg.Dispose(); // double dispose is a no-op
    }

    [Fact]
    public void Trigger_FiresListenersWithUnchangedCurrentValue()
    {
        var monitor = new TestOptionsMonitor<SampleOptions>(new SampleOptions { Value = 5 });
        SampleOptions? observed = null;
        var reg = monitor.OnChange(o => observed = o);

        monitor.Trigger();

        Assert.Same(monitor.CurrentValue, observed);
        Assert.Equal(5, observed!.Value);
        Assert.Same(monitor.CurrentValue, monitor.Get("ignored"));
    }

    [Fact]
    public void Trigger_WithNoListeners_DoesNotThrow()
    {
        var monitor = new TestOptionsMonitor<SampleOptions>(new SampleOptions());
        monitor.Trigger();
        Assert.NotNull(monitor.CurrentValue);
    }

    [Fact]
    public void ParallelSetValueOnChangeCurrentValue_CompletesWithoutExceptions()
    {
        var monitor = new TestOptionsMonitor<SampleOptions>(new SampleOptions());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Parallel.For(0, 8, new ParallelOptions { CancellationToken = cts.Token }, worker =>
        {
            for (var i = 0; i < 500; i++)
            {
                _ = monitor.CurrentValue;
                _ = monitor.Get("x");
                monitor.SetValue(new SampleOptions { Value = i });
                using var reg = monitor.OnChange(_ => { });
                monitor.Trigger();
            }
        });

        Assert.NotNull(monitor.CurrentValue);
    }

    [Fact]
    public void Constructor_NullValue_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new TestOptionsMonitor<SampleOptions>(null!));
        var monitor = new TestOptionsMonitor<SampleOptions>(new SampleOptions());
        Assert.Throws<ArgumentNullException>(() => monitor.SetValue(null!));
        Assert.Throws<ArgumentNullException>(() => monitor.OnChange((Action<SampleOptions>)null!));
    }
}
