using System.Diagnostics;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.Extensions.Time.Testing;

namespace ForgeTrust.AppSurface.Durable.Provider.Tests;

public sealed class ExternalActivationTimestampTests
{
    private static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(1);

    [Theory]
    [InlineData("invalid-operation", false, false)]
    [InlineData("invalid-operation", true, false)]
    [InlineData("operation-canceled", false, false)]
    [InlineData("operation-canceled", true, true)]
    public async Task Timestamp_capture_failures_use_caller_cancellation_only_for_caller_canceled_operation_cancellation(
        string exceptionKind,
        bool cancelCaller,
        bool expectedCanceled)
    {
        using var caller = new CancellationTokenSource();
        Exception exception = exceptionKind switch
        {
            "invalid-operation" => new InvalidOperationException("private clock detail"),
            "operation-canceled" => new OperationCanceledException("private clock detail"),
            _ => throw new ArgumentOutOfRangeException(nameof(exceptionKind)),
        };
        var clock = new ThrowingTimestampProvider(exception, () =>
        {
            if (cancelCaller)
            {
                caller.Cancel();
            }
        });
        var health = new ExternalActivationHealth(_ => ValueTask.FromResult(ExternalActivationTestSupport.Health()));
        var admission = new ExternalActivationAdmission((_, _) => ValueTask.FromResult(
            new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Refused, null, null)));
        var logger = new ExternalActivationLogger();
        var service = new DurableExternalActivationService(health, admission, clock, logger);

        var result = await service.ActivateAsync(Request(), caller.Token);

        Assert.Equal(expectedCanceled
            ? DurableExternalActivationOutcomeKind.CanceledBeforeAdmission
            : DurableExternalActivationOutcomeKind.ActivationFailed, result.Kind);
        Assert.Null(result.ObservedHealthState);
        Assert.Null(result.PumpResult);
        Assert.Equal(expectedCanceled ? null : DurableProblemCodes.ExternalActivationFailed, result.ProblemCode);
        Assert.Equal(1, clock.CallCount);
        Assert.Equal(0, health.CallCount);
        Assert.Equal(0, admission.CallCount);
        Assert.All(logger.Entries, entry => Assert.Null(entry.Exception));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("private clock detail", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nonfatal_timestamp_failure_during_health_cancellation_classification_maps_to_activation_failure()
    {
        var clock = new SwitchableFaultTimeProvider();
        var health = new ExternalActivationHealth(_ =>
        {
            clock.ArmTimestampFailure(new InvalidOperationException("private clock detail"));
            return ValueTask.FromException<DurableRuntimeHealthSnapshot>(new OperationCanceledException());
        });
        var admission = new ExternalActivationAdmission((_, _) => ValueTask.FromResult(
            new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Refused, null, null)));
        var service = new DurableExternalActivationService(health, admission, clock, new ExternalActivationLogger());

        var result = await service.ActivateAsync(Request());

        Assert.Equal(DurableExternalActivationOutcomeKind.ActivationFailed, result.Kind);
        Assert.Null(result.ObservedHealthState);
        Assert.Equal(DurableProblemCodes.ExternalActivationFailed, result.ProblemCode);
        Assert.Null(result.PumpResult);
        Assert.Equal(1, clock.FaultedTimestampCallCount);
        Assert.Equal(1, health.CallCount);
        Assert.Equal(0, admission.CallCount);
    }

    [Fact]
    public async Task Nonfatal_timestamp_failure_during_pump_cancellation_classification_maps_to_pump_failure()
    {
        var clock = new SwitchableFaultTimeProvider();
        var health = new ExternalActivationHealth(_ => ValueTask.FromResult(ExternalActivationTestSupport.Health()));
        var admission = new ExternalActivationAdmission((_, _) =>
        {
            clock.ArmTimestampFailure(new InvalidOperationException("private clock detail"));
            return ValueTask.FromException<DurableRuntimePumpAttempt>(new OperationCanceledException());
        });
        var service = new DurableExternalActivationService(health, admission, clock, new ExternalActivationLogger());

        var result = await service.ActivateAsync(Request());

        Assert.Equal(DurableExternalActivationOutcomeKind.PumpFailed, result.Kind);
        Assert.Equal(DurableRuntimeHealthState.Healthy, result.ObservedHealthState);
        Assert.Equal(DurableProblemCodes.ExternalActivationFailed, result.ProblemCode);
        Assert.Null(result.PumpResult);
        Assert.Equal(1, clock.FaultedTimestampCallCount);
        Assert.Equal(1, health.CallCount);
        Assert.Equal(1, admission.CallCount);
    }

    [Fact]
    public async Task Fatal_timestamp_failure_during_pump_cancellation_classification_wins_over_fatal_cleanup()
    {
        var timestampFailure = new OutOfMemoryException("fatal clock classification sentinel");
        var cleanupFailure = new AccessViolationException("fatal cleanup sentinel");
        var clock = new SwitchableFaultTimeProvider(cleanupFailure);
        var health = new ExternalActivationHealth(_ => ValueTask.FromResult(ExternalActivationTestSupport.Health()));
        var admission = new ExternalActivationAdmission((_, _) =>
        {
            clock.ArmTimestampFailure(timestampFailure);
            return ValueTask.FromException<DurableRuntimePumpAttempt>(new OperationCanceledException());
        });
        var service = new DurableExternalActivationService(health, admission, clock, new ExternalActivationLogger());

        var observed = await Assert.ThrowsAnyAsync<Exception>(() => service.ActivateAsync(Request()).AsTask());

        Assert.Same(timestampFailure, observed);
        Assert.Equal(1, clock.FaultedTimestampCallCount);
        Assert.True(clock.Timer?.IsDisposed);
        Assert.Same(cleanupFailure, clock.Timer?.ThrowOnDispose);
        Assert.Equal(1, health.CallCount);
        Assert.Equal(1, admission.CallCount);
    }

    [Fact]
    public async Task Caller_cancellation_during_health_cancellation_classification_short_circuits_clock()
    {
        using var caller = new CancellationTokenSource();
        var clock = new SwitchableFaultTimeProvider();
        var health = new ExternalActivationHealth(_ =>
        {
            clock.ArmTimestampFailure(new InvalidOperationException("must not be observed"));
            caller.Cancel();
            return ValueTask.FromException<DurableRuntimeHealthSnapshot>(new OperationCanceledException());
        });
        var admission = new ExternalActivationAdmission((_, _) => ValueTask.FromResult(
            new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Refused, null, null)));
        var service = new DurableExternalActivationService(health, admission, clock, new ExternalActivationLogger());

        var result = await service.ActivateAsync(Request(), caller.Token);

        Assert.Equal(DurableExternalActivationOutcomeKind.CanceledBeforeAdmission, result.Kind);
        Assert.Null(result.ObservedHealthState);
        Assert.Null(result.ProblemCode);
        Assert.Equal(0, clock.FaultedTimestampCallCount);
        Assert.Equal(1, health.CallCount);
        Assert.Equal(0, admission.CallCount);
    }

    [Fact]
    public async Task Caller_cancellation_during_pump_cancellation_classification_short_circuits_clock()
    {
        using var caller = new CancellationTokenSource();
        var clock = new SwitchableFaultTimeProvider();
        var health = new ExternalActivationHealth(_ => ValueTask.FromResult(ExternalActivationTestSupport.Health()));
        var admission = new ExternalActivationAdmission((_, _) =>
        {
            clock.ArmTimestampFailure(new InvalidOperationException("must not be observed"));
            caller.Cancel();
            return ValueTask.FromException<DurableRuntimePumpAttempt>(new OperationCanceledException());
        });
        var service = new DurableExternalActivationService(health, admission, clock, new ExternalActivationLogger());

        var result = await service.ActivateAsync(Request(), caller.Token);

        Assert.Equal(DurableExternalActivationOutcomeKind.PumpCanceled, result.Kind);
        Assert.Equal(DurableRuntimeHealthState.Healthy, result.ObservedHealthState);
        Assert.Null(result.ProblemCode);
        Assert.Equal(0, clock.FaultedTimestampCallCount);
        Assert.Equal(1, health.CallCount);
        Assert.Equal(1, admission.CallCount);
    }

    [Theory]
    [InlineData("stack")]
    [InlineData("memory")]
    [InlineData("access")]
    public async Task Fatal_timestamp_capture_failures_propagate_unchanged(string fatalKind)
    {
        Exception exception = fatalKind switch
        {
            "stack" => new StackOverflowException("fatal timestamp sentinel"),
            "memory" => new OutOfMemoryException("fatal timestamp sentinel"),
            "access" => new AccessViolationException("fatal timestamp sentinel"),
            _ => throw new ArgumentOutOfRangeException(nameof(fatalKind)),
        };
        var clock = new ThrowingTimestampProvider(exception);
        var health = new ExternalActivationHealth(_ => ValueTask.FromResult(ExternalActivationTestSupport.Health()));
        var admission = new ExternalActivationAdmission((_, _) => ValueTask.FromResult(
            new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Refused, null, null)));
        var service = new DurableExternalActivationService(health, admission, clock, new ExternalActivationLogger());
        using var caller = new CancellationTokenSource();
        caller.Cancel();

        var observed = await Assert.ThrowsAnyAsync<Exception>(() => service.ActivateAsync(Request(), caller.Token).AsTask());

        Assert.Same(exception, observed);
        Assert.Equal(1, clock.CallCount);
        Assert.Equal(0, health.CallCount);
        Assert.Equal(0, admission.CallCount);
    }

    [Fact]
    public async Task Request_budget_starts_at_service_entry_before_invocation_setup()
    {
        var clock = new SetupAdvancingTimeProvider();
        var health = new ExternalActivationHealth(_ => ValueTask.FromResult(ExternalActivationTestSupport.Health()));
        var admission = new ExternalActivationAdmission((_, _) => ValueTask.FromResult(
            new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Refused, null, null)));
        var service = new DurableExternalActivationService(health, admission, clock, new ExternalActivationLogger());
        var inThisInvocation = new AsyncLocal<bool>();
        var timestampCapturesAtActivityStart = -1;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AppSurfaceActivitySources.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                if (!inThisInvocation.Value || activity.OperationName != DurableExternalActivationInvocation.OperationName)
                {
                    return;
                }

                timestampCapturesAtActivityStart = clock.TimestampCaptureCount;
                clock.Advance(RequestBudget);
            },
        };
        ActivitySource.AddActivityListener(listener);

        inThisInvocation.Value = true;
        DurableExternalActivationResult result;
        try
        {
            result = await service.ActivateAsync(Request());
        }
        finally
        {
            inThisInvocation.Value = false;
        }

        Assert.Equal(1, timestampCapturesAtActivityStart);
        Assert.Equal(DurableExternalActivationOutcomeKind.RequestBudgetExceeded, result.Kind);
        Assert.Null(result.ObservedHealthState);
        Assert.Null(result.ProblemCode);
        Assert.Equal(0, health.CallCount);
        Assert.Equal(0, admission.CallCount);
    }

    // Value: protects=timer setup failure maps safely and stops its activity; fails_when=CreateTimer throws and the service leaks the started activity or exposes the exception; why_new=timestamp tests cover capture and cleanup faults, not timer creation in Start; seam=none
    [Fact]
    public async Task Nonfatal_timer_creation_failure_during_setup_maps_to_safe_failure_and_stops_activity()
    {
        var clock = new TimerCreationFaultTimeProvider(new InvalidOperationException("private timer setup detail"));
        var health = new ExternalActivationHealth(_ => ValueTask.FromResult(ExternalActivationTestSupport.Health()));
        var admission = new ExternalActivationAdmission((_, _) => ValueTask.FromResult(
            new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Refused, null, null)));
        var logger = new ExternalActivationLogger();
        var stopped = new List<Activity>();
        var inThisInvocation = new AsyncLocal<bool>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AppSurfaceActivitySources.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (inThisInvocation.Value && activity.OperationName == DurableExternalActivationInvocation.OperationName)
                {
                    stopped.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        var service = new DurableExternalActivationService(health, admission, clock, logger);

        inThisInvocation.Value = true;
        DurableExternalActivationResult result;
        try
        {
            result = await service.ActivateAsync(Request());
        }
        finally
        {
            inThisInvocation.Value = false;
        }

        Assert.Equal(DurableExternalActivationOutcomeKind.ActivationFailed, result.Kind);
        Assert.Null(result.ObservedHealthState);
        Assert.Equal(DurableProblemCodes.ExternalActivationFailed, result.ProblemCode);
        Assert.Null(result.PumpResult);
        Assert.Equal(1, clock.TimerCreationCount);
        Assert.Equal(0, health.CallCount);
        Assert.Equal(0, admission.CallCount);
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("private timer setup detail", StringComparison.Ordinal));
        Assert.All(logger.Entries, entry => Assert.Null(entry.Exception));

        var activity = Assert.Single(stopped);
        Assert.Equal(ActivityKind.Internal, activity.Kind);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("pre_admission", Assert.Single(activity.TagObjects, tag => tag.Key == "appsurface.durable.activation.phase").Value);
        Assert.Equal("ActivationFailed", Assert.Single(activity.TagObjects, tag => tag.Key == "appsurface.durable.activation.outcome").Value);
        Assert.Equal(DurableProblemCodes.ExternalActivationFailed,
            Assert.Single(activity.TagObjects, tag => tag.Key == "appsurface.durable.activation.problem_code").Value);
    }

    private static DurableExternalActivationRequest Request() => new(
        new DurableRuntimePumpRequest(5, TimeSpan.FromSeconds(2), DurableRuntimeSurface.Work),
        RequestBudget);

    private sealed class ThrowingTimestampProvider(Exception exception, Action? beforeThrow = null) : TimeProvider
    {
        private int _callCount;

        internal int CallCount => Volatile.Read(ref _callCount);

        public override long GetTimestamp()
        {
            Interlocked.Increment(ref _callCount);
            beforeThrow?.Invoke();
            throw exception;
        }
    }

    private sealed class SetupAdvancingTimeProvider : FakeTimeProvider
    {
        private int _timestampCaptureCount;

        internal int TimestampCaptureCount => Volatile.Read(ref _timestampCaptureCount);

        public override long GetTimestamp()
        {
            Interlocked.Increment(ref _timestampCaptureCount);
            return base.GetTimestamp();
        }
    }

    private sealed class TimerCreationFaultTimeProvider(Exception exception) : FakeTimeProvider
    {
        private int _timerCreationCount;

        internal int TimerCreationCount => Volatile.Read(ref _timerCreationCount);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _timerCreationCount);
            throw exception;
        }
    }

    private sealed class SwitchableFaultTimeProvider(Exception? timerDisposeFailure = null) : FakeTimeProvider
    {
        private Exception? _timestampFailure;
        private ExternalActivationTimer? _timer;
        private int _faultedTimestampCallCount;

        internal int FaultedTimestampCallCount => Volatile.Read(ref _faultedTimestampCallCount);

        internal ExternalActivationTimer? Timer => Volatile.Read(ref _timer);

        internal void ArmTimestampFailure(Exception exception) => Volatile.Write(ref _timestampFailure, exception);

        public override long GetTimestamp()
        {
            if (Volatile.Read(ref _timestampFailure) is { } exception)
            {
                Interlocked.Increment(ref _faultedTimestampCallCount);
                throw exception;
            }

            return base.GetTimestamp();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ExternalActivationTimer(base.CreateTimer(callback, state, dueTime, period))
            {
                ThrowOnDispose = timerDisposeFailure,
            };
            Volatile.Write(ref _timer, timer);
            return timer;
        }
    }
}
