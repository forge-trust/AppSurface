using System.Diagnostics;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Durable.Provider.Tests;

/// <summary>Serializes tests that install listeners on the process-shared AppSurface activity source.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExternalActivationSourceCollection
{
    /// <summary>Gets the shared xUnit collection name used by source-listener tests.</summary>
    public const string Name = "External activation canonical source";
}

[Collection(ExternalActivationSourceCollection.Name)]
public sealed class ExternalActivationTelemetryTests
{
    private const string ContractVersionTag = "appsurface.durable.activation.contract_version";
    private const string PhaseTag = "appsurface.durable.activation.phase";
    private const string OutcomeTag = "appsurface.durable.activation.outcome";
    private const string HealthStateTag = "appsurface.durable.activation.health_state";
    private const string ProblemCodeTag = "appsurface.durable.activation.problem_code";

    [Fact]
    public async Task Completed_activation_emits_one_internal_activity_with_only_the_exact_allowlisted_tags()
    {
        var stopped = new List<Activity>();
        using var listener = Listen(stopped.Add);
        var aggregate = ExternalActivationTestSupport.PumpResult(failed: 2);
        using var provider = BuildProvider(
            FixedHealth(ExternalActivationTestSupport.Health()),
            FixedAdmission(new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Completed, aggregate, null)),
            new ExternalActivationClock(),
            new ExternalActivationLogger());

        var result = await provider.GetRequiredService<IDurableExternalActivationService>().ActivateAsync(Request());

        Assert.Equal(DurableExternalActivationOutcomeKind.Completed, result.Kind);
        Assert.Same(aggregate, result.PumpResult);
        var activity = Assert.Single(stopped);
        Assert.Equal("ForgeTrust.AppSurface", activity.Source.Name);
        Assert.Equal("appsurface.durable.runtime.activation", activity.OperationName);
        Assert.Equal(ActivityKind.Internal, activity.Kind);
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        Assert.Empty(activity.Events);
        AssertTags(activity,
            (ContractVersionTag, 1),
            (PhaseTag, "completed"),
            (OutcomeTag, "Completed"),
            (HealthStateTag, "Healthy"));
    }

    [Fact]
    public async Task Pump_failure_sets_error_status_and_preserves_the_exact_safe_tag_set()
    {
        var stopped = new List<Activity>();
        using var listener = Listen(stopped.Add);
        using var provider = BuildProvider(
            FixedHealth(ExternalActivationTestSupport.Health(DurableRuntimeHealthState.Stale, DurableProblemCodes.ActivatorStale)),
            FixedAdmission(new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Unavailable, null, DurableProblemCodes.StoreUnavailable)),
            new ExternalActivationClock(),
            new ExternalActivationLogger());

        var result = await provider.GetRequiredService<IDurableExternalActivationService>().ActivateAsync(Request());

        Assert.Equal(DurableExternalActivationOutcomeKind.PumpFailed, result.Kind);
        var activity = Assert.Single(stopped);
        Assert.Equal(ActivityKind.Internal, activity.Kind);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Null(activity.StatusDescription);
        Assert.Empty(activity.Events);
        AssertTags(activity,
            (ContractVersionTag, 1),
            (PhaseTag, "pump_invoked"),
            (OutcomeTag, "PumpFailed"),
            (HealthStateTag, "Stale"),
            (ProblemCodeTag, DurableProblemCodes.StoreUnavailable));
    }

    [Fact]
    public async Task Pre_admission_failure_has_no_health_tag_and_replaces_the_original_exception_with_407()
    {
        var stopped = new List<Activity>();
        using var listener = Listen(stopped.Add);
        var healthError = new InvalidOperationException("private-health-detail");
        var logger = new ExternalActivationLogger();
        using var provider = BuildProvider(
            new ExternalActivationHealth(_ => ValueTask.FromException<DurableRuntimeHealthSnapshot>(healthError)),
            FixedAdmission(new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Refused, null, null)),
            new ExternalActivationClock(),
            logger);

        var result = await provider.GetRequiredService<IDurableExternalActivationService>().ActivateAsync(Request());

        Assert.Equal(DurableExternalActivationOutcomeKind.ActivationFailed, result.Kind);
        Assert.Equal(DurableProblemCodes.ExternalActivationFailed, result.ProblemCode);
        var activity = Assert.Single(stopped);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Null(activity.StatusDescription);
        Assert.Empty(activity.Events);
        AssertTags(activity,
            (ContractVersionTag, 1),
            (PhaseTag, "pre_admission"),
            (OutcomeTag, "ActivationFailed"),
            (ProblemCodeTag, DurableProblemCodes.ExternalActivationFailed));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("private-health-detail", StringComparison.Ordinal));
        Assert.All(logger.Entries, entry => Assert.Null(entry.Exception));
    }

    [Fact]
    public async Task Fatal_primary_exception_emits_only_its_last_phase_and_health_without_events_or_error_status()
    {
        var stopped = new List<Activity>();
        using var listener = Listen(stopped.Add);
        var fatal = new AccessViolationException("private-fatal-detail");
        var logger = new ExternalActivationLogger();
        using var provider = BuildProvider(
            FixedHealth(ExternalActivationTestSupport.Health(DurableRuntimeHealthState.Healthy)),
            new ExternalActivationAdmission((_, _) => ValueTask.FromException<DurableRuntimePumpAttempt>(fatal)),
            new ExternalActivationClock(),
            logger);

        var observed = await Assert.ThrowsAnyAsync<Exception>(() =>
            provider.GetRequiredService<IDurableExternalActivationService>().ActivateAsync(Request()).AsTask());

        Assert.Same(fatal, observed);
        var activity = Assert.Single(stopped);
        Assert.Equal("appsurface.durable.runtime.activation", activity.OperationName);
        Assert.Equal(ActivityKind.Internal, activity.Kind);
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        Assert.Empty(activity.Events);
        AssertTags(activity,
            (ContractVersionTag, 1),
            (PhaseTag, "pump_invoked"),
            (HealthStateTag, "Healthy"));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task No_listener_means_no_activity_and_does_not_change_the_activation_result()
    {
        Assert.False(AppSurfaceActivitySources.Instance.HasListeners());
        var parent = Activity.Current;
        var aggregate = ExternalActivationTestSupport.PumpResult();
        using var provider = BuildProvider(
            new ExternalActivationHealth(_ =>
            {
                Assert.Same(parent, Activity.Current);
                return ValueTask.FromResult(ExternalActivationTestSupport.Health());
            }),
            new ExternalActivationAdmission((_, _) =>
            {
                Assert.Same(parent, Activity.Current);
                return ValueTask.FromResult(new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Completed, aggregate, null));
            }),
            new ExternalActivationClock(),
            new ExternalActivationLogger());

        var result = await provider.GetRequiredService<IDurableExternalActivationService>().ActivateAsync(Request());

        Assert.Equal(DurableExternalActivationOutcomeKind.Completed, result.Kind);
        Assert.Same(aggregate, result.PumpResult);
        Assert.Same(parent, Activity.Current);
    }

    [Fact]
    public async Task Nonfatal_logger_activity_stop_and_timer_disposal_faults_cannot_replace_a_selected_result()
    {
        foreach (var location in new[] { "logger", "stop", "timer" })
        {
            var fault = new InvalidOperationException($"nonfatal-{location}-observer");
            var clock = new ExternalActivationClock { TimerDisposeException = location == "timer" ? fault : null };
            var logger = new ExternalActivationLogger { ThrowOnLog = location == "logger" ? fault : null };
            var stopped = new List<Activity>();
            using var listener = Listen(activity =>
            {
                stopped.Add(activity);
                if (location == "stop")
                {
                    throw fault;
                }
            });
            var aggregate = ExternalActivationTestSupport.PumpResult();
            using var provider = BuildProvider(
                FixedHealth(ExternalActivationTestSupport.Health()),
                FixedAdmission(new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Completed, aggregate, null)),
                clock,
                logger);

            var result = await provider.GetRequiredService<IDurableExternalActivationService>().ActivateAsync(Request());

            Assert.Equal(DurableExternalActivationOutcomeKind.Completed, result.Kind);
            Assert.Same(aggregate, result.PumpResult);
            Assert.True(clock.Timer.IsDisposed);
            Assert.Single(stopped);
        }
    }

    [Fact]
    public async Task Nonfatal_activity_stop_and_cleanup_faults_cannot_replace_a_primary_fatal_exception()
    {
        foreach (var location in new[] { "stop", "timer" })
        {
            var primary = new OutOfMemoryException($"primary-{location}");
            var observerFault = new InvalidOperationException($"nonfatal-{location}-observer");
            var clock = new ExternalActivationClock { TimerDisposeException = location == "timer" ? observerFault : null };
            var stopped = new List<Activity>();
            using var listener = Listen(activity =>
            {
                stopped.Add(activity);
                if (location == "stop")
                {
                    throw observerFault;
                }
            });
            var logger = new ExternalActivationLogger();
            using var provider = BuildProvider(
                new ExternalActivationHealth(_ => ValueTask.FromException<DurableRuntimeHealthSnapshot>(primary)),
                FixedAdmission(new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Refused, null, null)),
                clock,
                logger);

            var observed = await Assert.ThrowsAnyAsync<Exception>(() =>
                provider.GetRequiredService<IDurableExternalActivationService>().ActivateAsync(Request()).AsTask());

            Assert.Same(primary, observed);
            Assert.True(clock.Timer.IsDisposed);
            Assert.Single(stopped);
            Assert.Empty(logger.Entries);
        }
    }

    [Fact]
    public async Task Every_fatal_final_observer_fault_is_propagated_only_after_independent_cleanup()
    {
        foreach (var fatalName in new[] { "stack", "memory", "access" })
        {
            foreach (var location in new[] { "logger", "stop", "timer" })
            {
                var fatal = Fatal(fatalName);
                var clock = new ExternalActivationClock { TimerDisposeException = location == "timer" ? fatal : null };
                var logger = new ExternalActivationLogger { ThrowOnLog = location == "logger" ? fatal : null };
                var stopped = new List<Activity>();
                using var listener = Listen(activity =>
                {
                    stopped.Add(activity);
                    if (location == "stop")
                    {
                        throw fatal;
                    }
                });
                var aggregate = ExternalActivationTestSupport.PumpResult();
                using var provider = BuildProvider(
                    FixedHealth(ExternalActivationTestSupport.Health()),
                    FixedAdmission(new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Completed, aggregate, null)),
                    clock,
                    logger);

                var observed = await Assert.ThrowsAnyAsync<Exception>(() =>
                    provider.GetRequiredService<IDurableExternalActivationService>().ActivateAsync(Request()).AsTask());

                Assert.Same(fatal, observed);
                Assert.True(clock.Timer.IsDisposed);
                Assert.Single(stopped);
            }
        }
    }

    [Fact]
    public async Task A_primary_fatal_exception_wins_over_each_fatal_cleanup_observation()
    {
        foreach (var primaryName in new[] { "stack", "memory", "access" })
        {
            foreach (var location in new[] { "stop", "timer" })
            {
                var primary = Fatal(primaryName);
                var observerFatal = Fatal(primaryName == "stack" ? "memory" : "stack");
                var clock = new ExternalActivationClock { TimerDisposeException = location == "timer" ? observerFatal : null };
                var stopped = new List<Activity>();
                using var listener = Listen(activity =>
                {
                    stopped.Add(activity);
                    if (location == "stop")
                    {
                        throw observerFatal;
                    }
                });
                var logger = new ExternalActivationLogger();
                using var provider = BuildProvider(
                    new ExternalActivationHealth(_ => ValueTask.FromException<DurableRuntimeHealthSnapshot>(primary)),
                    FixedAdmission(new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Refused, null, null)),
                    clock,
                    logger);

                var observed = await Assert.ThrowsAnyAsync<Exception>(() =>
                    provider.GetRequiredService<IDurableExternalActivationService>().ActivateAsync(Request()).AsTask());

                Assert.Same(primary, observed);
                Assert.True(clock.Timer.IsDisposed);
                Assert.Single(stopped);
                Assert.Empty(logger.Entries);
            }
        }
    }

    private static ActivityListener Listen(Action<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AppSurfaceActivitySources.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static void AssertTags(Activity activity, params (string Key, object Value)[] expected)
    {
        var actual = activity.TagObjects.ToDictionary(tag => tag.Key, tag => tag.Value);
        var expectedKeys = expected.Select(tag => tag.Key).Order(StringComparer.Ordinal).ToArray();
        var actualKeys = actual.Keys.Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(expectedKeys, actualKeys);
        foreach (var (key, value) in expected)
        {
            Assert.Equal(value, actual[key]);
        }

        Assert.Subset(
            new HashSet<string>([ContractVersionTag, PhaseTag, OutcomeTag, HealthStateTag, ProblemCodeTag], StringComparer.Ordinal),
            actualKeys.ToHashSet(StringComparer.Ordinal));
    }

    private static ServiceProvider BuildProvider(
        IDurableRuntimeHealth health,
        IDurableRuntimePumpAdmission admission,
        TimeProvider clock,
        ExternalActivationLogger logger)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDurableRuntimeHealth>(health);
        services.AddSingleton<IDurableRuntimePumpAdmission>(admission);
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<ILogger<DurableExternalActivationService>>(logger);
        services.AddDurableExternalActivation();
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static ExternalActivationHealth FixedHealth(DurableRuntimeHealthSnapshot snapshot) =>
        new(_ => ValueTask.FromResult(snapshot));

    private static ExternalActivationAdmission FixedAdmission(DurableRuntimePumpAttempt attempt) =>
        new((_, _) => ValueTask.FromResult(attempt));

    private static DurableExternalActivationRequest Request() => new(
        new DurableRuntimePumpRequest(4, TimeSpan.FromSeconds(2), DurableRuntimeSurface.Work),
        TimeSpan.FromSeconds(8));

    private static Exception Fatal(string name) => name switch
    {
        "stack" => new StackOverflowException("fatal stack sentinel"),
        "memory" => new OutOfMemoryException("fatal memory sentinel"),
        "access" => new AccessViolationException("fatal access sentinel"),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };
}
