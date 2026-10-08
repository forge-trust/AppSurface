using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableWorkExecutionMetricTests
{
    [Fact]
    public void RefusalCounter_HasOnlyClosedBoundaryAndReasonLabels()
    {
        var measurements = new ConcurrentBag<KeyValuePair<string, object?>[]>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Name == "durable.work.execution_fenced") current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            Assert.Equal(1, value);
            measurements.Add(tags.ToArray());
        });
        listener.Start();
        var boundaries = new[] { "acceptance", "claim", "permit", "invocation", "renewal", "completion", "recovery", "operator_release" };
        var reasons = new[] { "deadline_elapsed", "circuit_elapsed", "slots_exhausted", "elapsed_exhausted", "claim_lost", "lease_lost", "scope_disabled", "epoch_mismatch", "validation_failed" };
        foreach (var boundary in boundaries)
            foreach (var reason in reasons) PostgreSqlDurableWorkStore.RecordExecutionRefusal(boundary, reason);
        Assert.True(measurements.Count >= boundaries.Length * reasons.Length);
        Assert.All(measurements, tags =>
        {
            Assert.Equal(2, tags.Length);
            Assert.Equal("boundary", tags[0].Key);
            Assert.Contains(Assert.IsType<string>(tags[0].Value), boundaries);
            Assert.Equal("reason", tags[1].Key);
            Assert.Contains(Assert.IsType<string>(tags[1].Value), reasons);
        });
    }

    [Fact]
    public void RefusalCounter_RejectsUnboundedValuesAndIsolatesListenerFailure()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PostgreSqlDurableWorkStore.RecordExecutionRefusal("scope-private", "deadline_elapsed"));
        Assert.Throws<ArgumentOutOfRangeException>(() => PostgreSqlDurableWorkStore.RecordExecutionRefusal("claim", "provider-private"));
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Name == "durable.work.execution_fenced") current.EnableMeasurementEvents(instrument);
        };
        var invoked = false;
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            invoked = true;
            throw new InvalidOperationException("listener failed");
        });
        listener.Start();
        PostgreSqlDurableWorkStore.RecordExecutionRefusal("claim", "deadline_elapsed");
        Assert.True(invoked);
    }
}
