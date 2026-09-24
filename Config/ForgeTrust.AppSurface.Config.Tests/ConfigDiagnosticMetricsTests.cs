using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ConfigDiagnosticMetricsTests
{
    [Fact]
    public void Counters_ExposeOnlyReviewedCategories()
    {
        var measurements = new ConcurrentQueue<(string Name, long Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == ConfigDiagnosticMetrics.MeterName) { owner.EnableMeasurementEvents(instrument); }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Enqueue((instrument.Name, value, tags.ToArray())));
        listener.Start();
        ConfigDiagnosticMetrics.Notice("SENTINEL_CODE", "SENTINEL_PROVIDER");
        ConfigDiagnosticMetrics.Terminal("config-key-collision", "FileBasedConfigProvider");
        ConfigDiagnosticMetrics.Terminal("config-environment-claim-limit", "EnvironmentConfigProvider");
        ConfigDiagnosticMetrics.Terminal("config-audit-notice-limit", "EnvironmentConfigProvider");
        Assert.Contains(measurements, measurement => measurement.Name == "appsurface.config.notice"
            && measurement.Tags.Any(tag => Equals(tag.Value, "config-provider-notice"))
            && measurement.Tags.Any(tag => Equals(tag.Value, "custom")));
        Assert.Contains(measurements, measurement => measurement.Name == "appsurface.config.terminal"
            && measurement.Tags.Any(tag => Equals(tag.Value, "config-key-collision")));
        Assert.Contains(measurements, measurement => measurement.Name == "appsurface.config.terminal"
            && measurement.Tags.Any(tag => Equals(tag.Value, "config-environment-claim-limit")));
        Assert.Contains(measurements, measurement => measurement.Name == "appsurface.config.terminal"
            && measurement.Tags.Any(tag => Equals(tag.Value, "config-audit-notice-limit")));
        Assert.All(measurements, measurement =>
        {
            Assert.Equal(1, measurement.Value);
            Assert.Equal(new[] { "code", "provider" }, measurement.Tags.Select(tag => tag.Key));
            Assert.DoesNotContain(measurement.Tags, tag => tag.Value?.ToString()?.Contains("SENTINEL", StringComparison.Ordinal) == true);
        });
    }

    [Fact]
    public void ListenerFailures_DoNotEscapeResolutionInstrumentation()
    {
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == ConfigDiagnosticMetrics.MeterName) { owner.EnableMeasurementEvents(instrument); }
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => throw new InvalidOperationException("Listener failed."));
        listener.Start();
        ConfigDiagnosticMetrics.Notice("config-key-legacy-dot-path", "Application");
        ConfigDiagnosticMetrics.Terminal("config-key-collision", "EnvironmentConfigProvider");
    }
}
