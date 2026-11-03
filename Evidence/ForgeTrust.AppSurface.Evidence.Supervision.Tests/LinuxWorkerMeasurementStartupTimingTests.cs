#if EVIDENCE_PRIVATE_C3_SDK
using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

public sealed class LinuxWorkerMeasurementStartupTimingTests
{
    [Fact]
    public void StartupTimingRecordProjectsDetachedElapsedMillisecondsAndFalseClaim()
    {
        var record = LinuxWorkerMeasurement.FormatStartupTiming(
            WorkerMeasurementStartup.NativeDispatch, dispatchTimestamp: 100, observedTimestamp: 100, frequency: 1_000);

        Assert.NotNull(record);
        using var document = JsonDocument.Parse(record);
        var root = document.RootElement;
        Assert.Equal(["elapsed_ms", "native_acceptance", "schema", "stage"],
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("evidence-worker-measurement-startup-timing-v1", root.GetProperty("schema").GetString());
        Assert.Equal(nameof(WorkerMeasurementStartup.NativeDispatch), root.GetProperty("stage").GetString());
        Assert.Equal(0, root.GetProperty("elapsed_ms").GetInt64());
        Assert.False(root.GetProperty("native_acceptance").GetBoolean());

        var later = LinuxWorkerMeasurement.FormatStartupTiming(
            WorkerMeasurementStartup.ServerDispatch, dispatchTimestamp: 100, observedTimestamp: 2_600, frequency: 1_000);
        Assert.NotNull(later);
        using var laterDocument = JsonDocument.Parse(later);
        Assert.Equal(2_500, laterDocument.RootElement.GetProperty("elapsed_ms").GetInt64());
        Assert.Equal(nameof(WorkerMeasurementStartup.ServerDispatch), laterDocument.RootElement.GetProperty("stage").GetString());
    }

    [Fact]
    public void StartupOriginCapturesOnceAndIsNotResetByLaterMilestones()
    {
        var origin = long.MinValue;

        Assert.Equal(10_000, LinuxWorkerMeasurement.CaptureStartupOrigin(ref origin, observedTimestamp: 10_000));
        Assert.Equal(10_000, LinuxWorkerMeasurement.CaptureStartupOrigin(ref origin, observedTimestamp: 11_000));
        Assert.Equal(10_000, origin);

        var record = LinuxWorkerMeasurement.FormatStartupTiming(
            WorkerMeasurementStartup.MonitorRegistered, origin, observedTimestamp: 40_000, frequency: 1_000);
        Assert.NotNull(record);
        using var document = JsonDocument.Parse(record);
        Assert.Equal(30_000, document.RootElement.GetProperty("elapsed_ms").GetInt64());
    }

    [Theory]
    [InlineData(600_000, true)]
    [InlineData(600_001, false)]
    public void StartupTimingHasFixedMaximumWithoutClamping(long elapsedMilliseconds, bool expectedRecord)
    {
        var record = LinuxWorkerMeasurement.FormatStartupTiming(
            WorkerMeasurementStartup.NativeStartJoined, 0, elapsedMilliseconds, frequency: 1_000);

        Assert.Equal(expectedRecord, record is not null);
        if (record is not null)
        {
            using var document = JsonDocument.Parse(record);
            Assert.Equal(elapsedMilliseconds, document.RootElement.GetProperty("elapsed_ms").GetInt64());
        }
    }

    [Fact]
    public void InvalidClockPairsAndUnknownStagesEmitNoTimingRecord()
    {
        Assert.Null(LinuxWorkerMeasurement.FormatStartupTiming(
            WorkerMeasurementStartup.NativeDispatch, 10, 9, frequency: 1_000));
        Assert.Null(LinuxWorkerMeasurement.FormatStartupTiming(
            WorkerMeasurementStartup.NativeDispatch, 10, 11, frequency: 0));
        Assert.Null(LinuxWorkerMeasurement.FormatStartupTiming(
            (WorkerMeasurementStartup)int.MaxValue, 10, 11, frequency: 1_000));
    }
}
#endif
