using System.Collections.Concurrent;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Durable.Provider.Tests;

public sealed class ExternalActivationServiceTests
{
    private static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(12);

    [Fact]
    public async Task Null_request_fails_before_health_admission_or_operational_mapping()
    {
        var health = new ExternalActivationHealth(_ => ValueTask.FromResult(ExternalActivationTestSupport.Health()));
        var admission = RefusingAdmission();
        var logger = new ExternalActivationLogger();
        var service = CreateService(health, admission, logger: logger);

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.ActivateAsync(null!).AsTask());

        Assert.Equal(0, health.CallCount);
        Assert.Equal(0, admission.CallCount);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Health_state_table_classifies_all_six_states_and_keeps_initial_not_started_code_out_of_results()
    {
        var cases = new[]
        {
            new HealthCase(
                ExternalActivationTestSupport.Health(DurableRuntimeHealthState.Unavailable, DurableProblemCodes.StoreUnavailable),
                DurableExternalActivationOutcomeKind.Unavailable,
                DurableProblemCodes.StoreUnavailable,
                0),
            new HealthCase(
                ExternalActivationTestSupport.Health(DurableRuntimeHealthState.Incompatible, DurableProblemCodes.RecoveryEpochRequired),
                DurableExternalActivationOutcomeKind.Incompatible,
                DurableProblemCodes.RecoveryEpochRequired,
                0),
            new HealthCase(
                ExternalActivationTestSupport.Health(DurableRuntimeHealthState.Draining),
                DurableExternalActivationOutcomeKind.Draining,
                null,
                0),
            new HealthCase(
                ExternalActivationTestSupport.Health(DurableRuntimeHealthState.NotStarted, DurableProblemCodes.ActivatorStale),
                DurableExternalActivationOutcomeKind.Busy,
                null,
                1),
            new HealthCase(
                ExternalActivationTestSupport.Health(DurableRuntimeHealthState.Healthy),
                DurableExternalActivationOutcomeKind.Busy,
                null,
                1),
            new HealthCase(
                ExternalActivationTestSupport.Health(DurableRuntimeHealthState.Stale, DurableProblemCodes.WorkerIdentityConflict),
                DurableExternalActivationOutcomeKind.Busy,
                DurableProblemCodes.WorkerIdentityConflict,
                1),
        };

        foreach (var item in cases)
        {
            var health = FixedHealth(item.Snapshot);
            var admission = RefusingAdmission();
            var service = CreateService(health, admission);

            var result = await service.ActivateAsync(Request());

            Assert.Equal(item.ExpectedKind, result.Kind);
            Assert.Equal(item.Snapshot.State, result.ObservedHealthState);
            Assert.Equal(item.ExpectedCode, result.ProblemCode);
            Assert.Equal(item.ExpectedAdmissionCalls, admission.CallCount);
            Assert.Equal(1, health.CallCount);
        }

        var notStarted = ExternalActivationTestSupport.Health(
            DurableRuntimeHealthState.NotStarted,
            DurableProblemCodes.ActivatorStale);
        Assert.True(notStarted.CanEnableActivation);
        Assert.False(notStarted.IsReady);
    }

    [Fact]
    public async Task Public_service_admits_compatible_not_started_with_ASDUR404_and_omits_the_observation_code()
    {
        var pumpRequest = new DurableRuntimePumpRequest(9, TimeSpan.FromSeconds(2), DurableRuntimeSurface.Work);
        var activationRequest = new DurableExternalActivationRequest(pumpRequest, RequestBudget);
        var snapshot = ExternalActivationTestSupport.Health(
            DurableRuntimeHealthState.NotStarted,
            DurableProblemCodes.ActivatorStale);
        var aggregate = ExternalActivationTestSupport.PumpResult(failed: 1);
        var health = FixedHealth(snapshot);
        var admission = new ExternalActivationAdmission((_, _) => ValueTask.FromResult(
            new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Completed, aggregate, null)));
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton<TimeProvider>(new ExternalActivationClock());
        services.AddSingleton<IDurableRuntimeHealth>(health);
        services.AddSingleton<IDurableRuntimePumpAdmission>(admission);
        services.AddDurableExternalActivation();
        using var provider = services.BuildServiceProvider(new Microsoft.Extensions.DependencyInjection.ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        var service = provider.GetRequiredService<IDurableExternalActivationService>();

        var result = await service.ActivateAsync(activationRequest);

        Assert.True(snapshot.CanEnableActivation);
        Assert.False(snapshot.IsReady);
        Assert.Equal(DurableExternalActivationOutcomeKind.Completed, result.Kind);
        Assert.Equal(DurableRuntimeHealthState.NotStarted, result.ObservedHealthState);
        Assert.Null(result.ProblemCode);
        Assert.Same(aggregate, result.PumpResult);
        Assert.Equal(1, health.CallCount);
        Assert.Equal(1, admission.CallCount);
        Assert.Same(pumpRequest, Assert.Single(admission.Requests));
    }

    [Fact]
    public async Task Every_canonical_health_code_is_accepted_only_with_its_normative_state_and_flags()
    {
        var compatibilityCodes = new[]
        {
            DurableProblemCodes.RecoveryEpochRequired,
            DurableProblemCodes.SchemaMissing,
            DurableProblemCodes.SchemaUpgradeRequired,
            DurableProblemCodes.SchemaVersionUnsupported,
            DurableProblemCodes.SchemaInconsistent,
        };

        var allowed = new List<(DurableRuntimeHealthSnapshot Snapshot, DurableExternalActivationOutcomeKind Kind, string? Code, int AdmissionCalls)>
        {
            (ExternalActivationTestSupport.Health(DurableRuntimeHealthState.Unavailable, DurableProblemCodes.StoreUnavailable),
                DurableExternalActivationOutcomeKind.Unavailable, DurableProblemCodes.StoreUnavailable, 0),
            (ExternalActivationTestSupport.Health(DurableRuntimeHealthState.NotStarted, DurableProblemCodes.ActivatorStale),
                DurableExternalActivationOutcomeKind.Busy, null, 1),
            (ExternalActivationTestSupport.Health(DurableRuntimeHealthState.Stale, DurableProblemCodes.ActivatorStale),
                DurableExternalActivationOutcomeKind.Busy, DurableProblemCodes.ActivatorStale, 1),
            (ExternalActivationTestSupport.Health(DurableRuntimeHealthState.Stale, DurableProblemCodes.WorkerIdentityConflict),
                DurableExternalActivationOutcomeKind.Busy, DurableProblemCodes.WorkerIdentityConflict, 1),
        };
        foreach (var code in compatibilityCodes)
        {
            allowed.Add((
                ExternalActivationTestSupport.Health(DurableRuntimeHealthState.Incompatible, code),
                DurableExternalActivationOutcomeKind.Incompatible,
                code,
                0));
        }

        foreach (var item in allowed)
        {
            var admission = RefusingAdmission();
            var result = await CreateService(FixedHealth(item.Snapshot), admission).ActivateAsync(Request());

            Assert.Equal(item.Kind, result.Kind);
            Assert.Equal(item.Snapshot.State, result.ObservedHealthState);
            Assert.Equal(item.Code, result.ProblemCode);
            Assert.Equal(item.AdmissionCalls, admission.CallCount);
        }
    }

    [Fact]
    public async Task Compatible_flag_failures_map_to_incompatible_and_contradictory_evidence_fails_closed()
    {
        var incompatible = new[]
        {
            ExternalActivationTestSupport.Health(
                DurableRuntimeHealthState.NotStarted,
                DurableProblemCodes.SchemaMissing,
                schemaCompatible: false),
            ExternalActivationTestSupport.Health(
                DurableRuntimeHealthState.Healthy,
                DurableProblemCodes.SchemaInconsistent,
                epochCompatible: false),
            ExternalActivationTestSupport.Health(
                DurableRuntimeHealthState.Draining,
                DurableProblemCodes.SchemaUpgradeRequired,
                schemaCompatible: false,
                isDraining: true),
            ExternalActivationTestSupport.Health(
                DurableRuntimeHealthState.Stale,
                DurableProblemCodes.RecoveryEpochRequired,
                epochCompatible: false,
                isDraining: true),
        };
        foreach (var snapshot in incompatible)
        {
            var admission = RefusingAdmission();
            var result = await CreateService(FixedHealth(snapshot), admission).ActivateAsync(Request());

            Assert.Equal(DurableExternalActivationOutcomeKind.Incompatible, result.Kind);
            Assert.Equal(snapshot.State, result.ObservedHealthState);
            Assert.Equal(snapshot.ProblemCode, result.ProblemCode);
            Assert.Equal(0, admission.CallCount);
        }

        var invalid = new[]
        {
            ExternalActivationTestSupport.Health(
                DurableRuntimeHealthState.Unavailable,
                DurableProblemCodes.StoreUnavailable,
                schemaCompatible: true),
            ExternalActivationTestSupport.Health(
                DurableRuntimeHealthState.Healthy,
                DurableProblemCodes.SchemaMissing),
            ExternalActivationTestSupport.Health(
                DurableRuntimeHealthState.Stale,
                DurableProblemCodes.StoreUnavailable),
            ExternalActivationTestSupport.Health(
                DurableRuntimeHealthState.Draining,
                isDraining: false),
            ExternalActivationTestSupport.Health(
                DurableRuntimeHealthState.Healthy,
                isDraining: true),
            ExternalActivationTestSupport.Health(
                DurableRuntimeHealthState.NotStarted,
                isDraining: true),
            ExternalActivationTestSupport.Health(
                DurableRuntimeHealthState.NotStarted,
                DurableProblemCodes.WorkerIdentityConflict),
            ExternalActivationTestSupport.Health(
                DurableRuntimeHealthState.Incompatible,
                "ASDUR999"),
            ExternalActivationTestSupport.Health(
                DurableRuntimeHealthState.NotStarted,
                "ASDUR106"),
            ExternalActivationTestSupport.Health(
                DurableRuntimeHealthState.Healthy,
                "asdur108"),
        };

        foreach (var snapshot in invalid)
        {
            var admission = RefusingAdmission();
            var result = await CreateService(FixedHealth(snapshot), admission).ActivateAsync(Request());

            Assert.Equal(DurableExternalActivationOutcomeKind.ActivationFailed, result.Kind);
            Assert.Equal(snapshot.State, result.ObservedHealthState);
            Assert.Equal(DurableProblemCodes.ExternalActivationFailed, result.ProblemCode);
            Assert.Null(result.PumpResult);
            Assert.Equal(0, admission.CallCount);
        }

        var staleDraining = ExternalActivationTestSupport.Health(
            DurableRuntimeHealthState.Stale,
            DurableProblemCodes.ActivatorStale,
            isDraining: true);
        var staleResult = await CreateService(
            FixedHealth(staleDraining),
            RefusingAdmission()).ActivateAsync(Request());
        Assert.Equal(DurableExternalActivationOutcomeKind.Busy, staleResult.Kind);
    }

    [Fact]
    public async Task Health_with_unrelated_exception_or_null_snapshot_maps_to_safe_pre_invocation_failure()
    {
        var healthError = new InvalidOperationException("sensitive-health-value");
        var throwingHealth = new ExternalActivationHealth(_ => ValueTask.FromException<DurableRuntimeHealthSnapshot>(healthError));
        var logger = new ExternalActivationLogger();
        var admission = RefusingAdmission();
        var failed = await CreateService(throwingHealth, admission, logger: logger).ActivateAsync(Request());

        Assert.Equal(DurableExternalActivationOutcomeKind.ActivationFailed, failed.Kind);
        Assert.Null(failed.ObservedHealthState);
        Assert.Equal(DurableProblemCodes.ExternalActivationFailed, failed.ProblemCode);
        Assert.Equal(0, admission.CallCount);
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("sensitive-health-value", StringComparison.Ordinal));
        Assert.All(logger.Entries, entry => Assert.Null(entry.Exception));

        var nullHealth = new ExternalActivationHealth(_ => ValueTask.FromResult<DurableRuntimeHealthSnapshot>(null!));
        var nullResult = await CreateService(nullHealth, RefusingAdmission()).ActivateAsync(Request());
        Assert.Equal(DurableExternalActivationOutcomeKind.ActivationFailed, nullResult.Kind);
        Assert.Null(nullResult.ObservedHealthState);
        Assert.Equal(DurableProblemCodes.ExternalActivationFailed, nullResult.ProblemCode);
    }

    [Fact]
    public async Task Admission_outcomes_are_authoritative_and_completed_aggregate_is_preserved_exactly()
    {
        var pumpRequest = new DurableRuntimePumpRequest(11, TimeSpan.FromSeconds(4), DurableRuntimeSurface.Work);
        var request = new DurableExternalActivationRequest(pumpRequest, RequestBudget);
        var aggregate = ExternalActivationTestSupport.PumpResult(
            discovered: 19,
            claimed: 13,
            processed: 7,
            deferred: 3,
            failed: 3,
            hasMore: true,
            nextDueAtUtc: new DateTimeOffset(2026, 10, 2, 1, 2, 3, TimeSpan.FromHours(-4)),
            elapsed: TimeSpan.FromTicks(987654321));
        var admissionCases = new[]
        {
            new AdmissionCase(
                new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Refused, null, null),
                DurableExternalActivationOutcomeKind.Busy,
                DurableProblemCodes.ActivatorStale),
            new AdmissionCase(
                new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Completed, aggregate, null),
                DurableExternalActivationOutcomeKind.Completed,
                DurableProblemCodes.ActivatorStale),
            new AdmissionCase(
                new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Unavailable, null, DurableProblemCodes.StoreUnavailable),
                DurableExternalActivationOutcomeKind.PumpFailed,
                DurableProblemCodes.StoreUnavailable),
            new AdmissionCase(
                new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Incompatible, null, DurableProblemCodes.SchemaVersionUnsupported),
                DurableExternalActivationOutcomeKind.PumpFailed,
                DurableProblemCodes.SchemaVersionUnsupported),
        };

        foreach (var item in admissionCases)
        {
            var admission = new ExternalActivationAdmission((_, _) => ValueTask.FromResult(item.Attempt));
            var health = FixedHealth(ExternalActivationTestSupport.Health(
                DurableRuntimeHealthState.Stale,
                DurableProblemCodes.ActivatorStale));
            var result = await CreateService(health, admission).ActivateAsync(request);

            Assert.Equal(item.ExpectedKind, result.Kind);
            Assert.Same(pumpRequest, Assert.Single(admission.Requests));
            Assert.Equal(1, health.CallCount);
            Assert.Equal(1, admission.CallCount);
            if (item.Attempt.Kind == DurableRuntimePumpAttemptKind.Completed)
            {
                Assert.Same(aggregate, result.PumpResult);
                Assert.Equal(19, result.PumpResult!.Discovered);
                Assert.Equal(13, result.PumpResult.Claimed);
                Assert.Equal(7, result.PumpResult.Processed);
                Assert.Equal(3, result.PumpResult.Deferred);
                Assert.Equal(3, result.PumpResult.Failed);
                Assert.True(result.PumpResult.HasMore);
                Assert.Equal(new DateTimeOffset(2026, 10, 2, 5, 2, 3, TimeSpan.Zero), result.PumpResult.NextDueAtUtc);
                Assert.Equal(TimeSpan.FromTicks(987654321), result.PumpResult.Elapsed);
            }
            else
            {
                Assert.Null(result.PumpResult);
            }

            Assert.Equal(item.ExpectedCode, result.ProblemCode);
        }

        var initialNotStartedResult = await CreateService(
            FixedHealth(ExternalActivationTestSupport.Health(
                DurableRuntimeHealthState.NotStarted,
                DurableProblemCodes.ActivatorStale)),
            new ExternalActivationAdmission((_, _) => ValueTask.FromResult(
                new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Completed, aggregate, null))))
            .ActivateAsync(request);
        Assert.Equal(DurableExternalActivationOutcomeKind.Completed, initialNotStartedResult.Kind);
        Assert.Null(initialNotStartedResult.ProblemCode);
    }

    [Fact]
    public async Task Admission_is_the_only_pump_path_and_late_cancellation_cannot_rewrite_its_returns()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var health = FixedHealth(ExternalActivationTestSupport.Health());
        var caller = new CancellationTokenSource();
        var aggregate = ExternalActivationTestSupport.PumpResult(failed: 2);
        var admission = new ExternalActivationAdmission((_, _) =>
        {
            caller.Cancel();
            return ValueTask.FromResult(new DurableRuntimePumpAttempt(
                DurableRuntimePumpAttemptKind.Completed,
                aggregate,
                null));
        });
        var legacy = new ExternalActivationLegacyPump();
        services.AddSingleton<IDurableRuntimeHealth>(health);
        services.AddSingleton<IDurableRuntimePumpAdmission>(admission);
        services.AddSingleton<IDurableRuntimePump>(legacy);
        services.AddDurableExternalActivation();
        using var provider = services.BuildServiceProvider(new Microsoft.Extensions.DependencyInjection.ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        var service = provider.GetRequiredService<IDurableExternalActivationService>();
        var completed = await service.ActivateAsync(Request(), caller.Token);

        Assert.Equal(DurableExternalActivationOutcomeKind.Completed, completed.Kind);
        Assert.Same(aggregate, completed.PumpResult);
        Assert.Equal(2, completed.PumpResult!.Failed);
        Assert.Equal(1, admission.CallCount);
        Assert.Equal(0, legacy.CallCount);

        foreach (var source in new[] { "caller", "budget" })
        {
            foreach (var lateAttempt in new[]
                     {
                         new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Refused, null, null),
                         new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Unavailable, null, DurableProblemCodes.StoreUnavailable),
                         new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Incompatible, null, DurableProblemCodes.SchemaMissing),
                         new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Completed, aggregate, null),
                     })
            {
                var lateCaller = new CancellationTokenSource();
                var lateClock = new ExternalActivationClock();
                var lateAdmission = new ExternalActivationAdmission((_, _) =>
                {
                    if (source == "caller")
                    {
                        lateCaller.Cancel();
                    }
                    else
                    {
                        lateClock.Advance(RequestBudget);
                    }

                    return ValueTask.FromResult(lateAttempt);
                });
                var lateResult = await CreateService(
                        FixedHealth(ExternalActivationTestSupport.Health()),
                        lateAdmission,
                        lateClock)
                    .ActivateAsync(Request(), lateCaller.Token);

                Assert.Equal(lateAttempt.Kind switch
                {
                    DurableRuntimePumpAttemptKind.Completed => DurableExternalActivationOutcomeKind.Completed,
                    DurableRuntimePumpAttemptKind.Refused => DurableExternalActivationOutcomeKind.Busy,
                    _ => DurableExternalActivationOutcomeKind.PumpFailed,
                }, lateResult.Kind);
                Assert.Equal(lateAttempt.ProblemCode, lateResult.ProblemCode);
                Assert.Same(lateAttempt.Result, lateResult.PumpResult);
                Assert.Equal(1, lateAdmission.CallCount);
                Assert.True(lateClock.Timer.IsDisposed);
            }
        }
    }

    [Fact]
    public async Task Fatal_dependency_exceptions_propagate_unchanged_from_health_and_admission()
    {
        foreach (var fatalName in new[] { "stack", "memory", "access" })
        {
            var healthFatal = Fatal(fatalName);
            var health = new ExternalActivationHealth(_ => ValueTask.FromException<DurableRuntimeHealthSnapshot>(healthFatal));
            var healthClock = new ExternalActivationClock();
            var healthObserved = await Assert.ThrowsAnyAsync<Exception>(() => CreateService(health, RefusingAdmission(), healthClock)
                .ActivateAsync(Request()).AsTask());
            Assert.Same(healthFatal, healthObserved);
            Assert.True(healthClock.Timer.IsDisposed);

            var pumpFatal = Fatal(fatalName);
            var admission = new ExternalActivationAdmission((_, _) => ValueTask.FromException<DurableRuntimePumpAttempt>(pumpFatal));
            var pumpClock = new ExternalActivationClock();
            var pumpObserved = await Assert.ThrowsAnyAsync<Exception>(() => CreateService(
                    FixedHealth(ExternalActivationTestSupport.Health()),
                    admission,
                    pumpClock)
                .ActivateAsync(Request()).AsTask());
            Assert.Same(pumpFatal, pumpObserved);
            Assert.True(pumpClock.Timer.IsDisposed);
        }
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("budget")]
    public async Task Pre_admission_cancellation_during_ignored_health_is_rechecked_after_the_read(string source)
    {
        var caller = new CancellationTokenSource();
        var clock = new ExternalActivationClock();
        var health = new ExternalActivationHealth(_ =>
        {
            if (source == "caller")
            {
                caller.Cancel();
            }
            else
            {
                clock.Advance(RequestBudget);
            }

            return ValueTask.FromResult(ExternalActivationTestSupport.Health(DurableRuntimeHealthState.Draining));
        });
        var admission = RefusingAdmission();

        var result = await CreateService(health, admission, clock).ActivateAsync(Request(RequestBudget), caller.Token);

        Assert.Equal(source == "caller"
            ? DurableExternalActivationOutcomeKind.CanceledBeforeAdmission
            : DurableExternalActivationOutcomeKind.RequestBudgetExceeded, result.Kind);
        Assert.Equal(DurableRuntimeHealthState.Draining, result.ObservedHealthState);
        Assert.Null(result.ProblemCode);
        Assert.Equal(1, health.CallCount);
        Assert.Equal(0, admission.CallCount);
        Assert.True(clock.Timer.IsDisposed);
    }

    [Fact]
    public async Task Caller_cancellation_wins_when_caller_and_budget_are_both_signaled_at_health_return()
    {
        var caller = new CancellationTokenSource();
        var clock = new ExternalActivationClock();
        var health = new ExternalActivationHealth(_ =>
        {
            caller.Cancel();
            clock.Advance(RequestBudget);
            return ValueTask.FromResult(ExternalActivationTestSupport.Health(DurableRuntimeHealthState.Healthy));
        });
        var admission = RefusingAdmission();

        var result = await CreateService(health, admission, clock).ActivateAsync(Request(RequestBudget), caller.Token);

        Assert.Equal(DurableExternalActivationOutcomeKind.CanceledBeforeAdmission, result.Kind);
        Assert.Equal(DurableRuntimeHealthState.Healthy, result.ObservedHealthState);
        Assert.Equal(0, admission.CallCount);
    }

    [Fact]
    public async Task Pre_entry_caller_cancellation_and_null_health_are_classified_without_admission()
    {
        var caller = new CancellationTokenSource();
        caller.Cancel();
        var health = new ExternalActivationHealth(_ => ValueTask.FromResult(ExternalActivationTestSupport.Health()));
        var admission = RefusingAdmission();

        var canceled = await CreateService(health, admission).ActivateAsync(Request(), caller.Token);
        var nullHealth = new ExternalActivationHealth(_ => ValueTask.FromResult<DurableRuntimeHealthSnapshot>(null!));
        var nullSnapshot = await CreateService(nullHealth, RefusingAdmission()).ActivateAsync(Request());

        Assert.Equal(DurableExternalActivationOutcomeKind.CanceledBeforeAdmission, canceled.Kind);
        Assert.Null(canceled.ObservedHealthState);
        Assert.Equal(0, health.CallCount);
        Assert.Equal(0, admission.CallCount);
        Assert.Equal(DurableExternalActivationOutcomeKind.ActivationFailed, nullSnapshot.Kind);
        Assert.Null(nullSnapshot.ObservedHealthState);
        Assert.Equal(DurableProblemCodes.ExternalActivationFailed, nullSnapshot.ProblemCode);
    }

    [Fact]
    public async Task Setup_and_health_elapsed_time_share_the_original_monotonic_budget()
    {
        var clock = new ExternalActivationClock();
        clock.OnTimerCreated = () => clock.Advance(TimeSpan.FromMilliseconds(400));
        var health = new ExternalActivationHealth(_ =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(600));
            return ValueTask.FromResult(ExternalActivationTestSupport.Health());
        });
        var admission = RefusingAdmission();

        var result = await CreateService(health, admission, clock).ActivateAsync(Request(TimeSpan.FromSeconds(1)));

        Assert.Equal(DurableExternalActivationOutcomeKind.RequestBudgetExceeded, result.Kind);
        Assert.Equal(DurableRuntimeHealthState.Healthy, result.ObservedHealthState);
        Assert.Equal(1, health.CallCount);
        Assert.Equal(0, admission.CallCount);
        Assert.True(clock.Timer.IsDisposed);
    }

    [Fact]
    public async Task Elapsed_monotonic_budget_blocks_admission_even_when_timer_delivery_is_delayed()
    {
        var clock = new ExternalActivationDelayedClock();
        var health = new ExternalActivationHealth(_ =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.False(clock.Timer.IsDisposed);
            return ValueTask.FromResult(ExternalActivationTestSupport.Health());
        });
        var admission = RefusingAdmission();
        var requestBudget = TimeSpan.FromSeconds(1);

        var result = await CreateService(health, admission, clock).ActivateAsync(Request(requestBudget));

        Assert.Equal(DurableExternalActivationOutcomeKind.RequestBudgetExceeded, result.Kind);
        Assert.Equal(0, admission.CallCount);
        Assert.True(clock.Timer.IsDisposed);
    }

    [Fact]
    public async Task Monotonic_deadline_does_not_expire_when_only_wall_clock_changes()
    {
        var clock = new ExternalActivationWallClock();
        var aggregate = ExternalActivationTestSupport.PumpResult();
        var health = new ExternalActivationHealth(_ =>
        {
            clock.SetUtcNow(DateTimeOffset.UnixEpoch.AddYears(20));
            return ValueTask.FromResult(ExternalActivationTestSupport.Health());
        });
        var admission = new ExternalActivationAdmission((_, _) => ValueTask.FromResult(
            new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Completed, aggregate, null)));

        var result = await CreateService(health, admission, clock).ActivateAsync(Request(TimeSpan.FromSeconds(1)));

        Assert.Equal(DurableExternalActivationOutcomeKind.Completed, result.Kind);
        Assert.Same(aggregate, result.PumpResult);
        Assert.True(clock.Timer.IsDisposed);
    }

    [Fact]
    public async Task Post_invocation_cancellation_uses_source_signals_not_exception_token_identity()
    {
        foreach (var source in new[] { "caller", "budget" })
        {
            var caller = new CancellationTokenSource();
            var clock = new ExternalActivationClock();
            var health = FixedHealth(ExternalActivationTestSupport.Health(
                DurableRuntimeHealthState.Stale,
                DurableProblemCodes.ActivatorStale));
            var admission = new ExternalActivationAdmission((_, _) =>
            {
                if (source == "caller")
                {
                    caller.Cancel();
                }
                else
                {
                    clock.Advance(RequestBudget);
                }

                return ValueTask.FromException<DurableRuntimePumpAttempt>(
                    new OperationCanceledException("unrelated exception token", new CancellationTokenSource().Token));
            });

            var result = await CreateService(health, admission, clock).ActivateAsync(Request(RequestBudget), caller.Token);

            Assert.Equal(DurableExternalActivationOutcomeKind.PumpCanceled, result.Kind);
            Assert.Equal(DurableRuntimeHealthState.Stale, result.ObservedHealthState);
            Assert.Equal(DurableProblemCodes.ActivatorStale, result.ProblemCode);
            Assert.Equal(1, admission.CallCount);
        }
    }

    [Fact]
    public async Task Unrelated_operation_cancellation_maps_to_407_by_invocation_phase()
    {
        var preInvocation = new ExternalActivationHealth(_ => ValueTask.FromException<DurableRuntimeHealthSnapshot>(
            new OperationCanceledException("not the caller token", new CancellationTokenSource().Token)));
        var before = await CreateService(preInvocation, RefusingAdmission()).ActivateAsync(Request());
        var afterInvocation = new ExternalActivationAdmission((_, _) => ValueTask.FromException<DurableRuntimePumpAttempt>(
            new OperationCanceledException("not the caller token", new CancellationTokenSource().Token)));
        var after = await CreateService(FixedHealth(ExternalActivationTestSupport.Health()), afterInvocation)
            .ActivateAsync(Request());

        Assert.Equal(DurableExternalActivationOutcomeKind.ActivationFailed, before.Kind);
        Assert.Equal(DurableProblemCodes.ExternalActivationFailed, before.ProblemCode);
        Assert.Null(before.ObservedHealthState);
        Assert.Equal(DurableExternalActivationOutcomeKind.PumpFailed, after.Kind);
        Assert.Equal(DurableProblemCodes.ExternalActivationFailed, after.ProblemCode);
        Assert.Equal(DurableRuntimeHealthState.Healthy, after.ObservedHealthState);
    }

    [Fact]
    public async Task A_non_cancellation_failure_remains_a_failure_even_after_a_cancellation_signal()
    {
        var caller = new CancellationTokenSource();
        var providerFailure = new InvalidOperationException("private provider detail");
        var admission = new ExternalActivationAdmission((_, _) =>
        {
            caller.Cancel();
            return ValueTask.FromException<DurableRuntimePumpAttempt>(providerFailure);
        });
        var logger = new ExternalActivationLogger();

        var result = await CreateService(
                FixedHealth(ExternalActivationTestSupport.Health(DurableRuntimeHealthState.Healthy)),
                admission,
                logger: logger)
            .ActivateAsync(Request(), caller.Token);

        Assert.Equal(DurableExternalActivationOutcomeKind.PumpFailed, result.Kind);
        Assert.Equal(DurableProblemCodes.ExternalActivationFailed, result.ProblemCode);
        Assert.Equal(DurableRuntimeHealthState.Healthy, result.ObservedHealthState);
        Assert.Equal(1, admission.CallCount);
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("private provider detail", StringComparison.Ordinal));
        Assert.All(logger.Entries, entry => Assert.Null(entry.Exception));
    }

    [Fact]
    public async Task Concurrent_calls_keep_health_phase_budget_and_result_isolated()
    {
        var clock = new ExternalActivationClock();
        var firstHealthEntered = NewSignal();
        var secondHealthEntered = NewSignal();
        var releaseFirstHealth = NewSignal();
        var releaseSecondHealth = NewSignal();
        var healthNumber = 0;
        var health = new ExternalActivationHealth(async _ =>
        {
            var current = Interlocked.Increment(ref healthNumber);
            if (current == 1)
            {
                firstHealthEntered.TrySetResult();
                await releaseFirstHealth.Task;
                return ExternalActivationTestSupport.Health(
                    DurableRuntimeHealthState.Stale,
                    DurableProblemCodes.ActivatorStale);
            }

            secondHealthEntered.TrySetResult();
            await releaseSecondHealth.Task;
            return ExternalActivationTestSupport.Health(DurableRuntimeHealthState.Healthy);
        });
        var aggregate = ExternalActivationTestSupport.PumpResult();
        var admission = new ExternalActivationAdmission((_, _) => ValueTask.FromResult(
            new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Completed, aggregate, null)));
        var service = CreateService(health, admission, clock);

        var first = service.ActivateAsync(Request(TimeSpan.FromSeconds(10))).AsTask();
        var second = service.ActivateAsync(Request(TimeSpan.FromSeconds(30))).AsTask();
        await Task.WhenAll(firstHealthEntered.Task, secondHealthEntered.Task).WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(11));
        releaseFirstHealth.TrySetResult();
        releaseSecondHealth.TrySetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(DurableExternalActivationOutcomeKind.RequestBudgetExceeded, results[0].Kind);
        Assert.Equal(DurableRuntimeHealthState.Stale, results[0].ObservedHealthState);
        Assert.Equal(DurableProblemCodes.ActivatorStale, results[0].ProblemCode);
        Assert.Equal(DurableExternalActivationOutcomeKind.Completed, results[1].Kind);
        Assert.Equal(DurableRuntimeHealthState.Healthy, results[1].ObservedHealthState);
        Assert.Same(aggregate, results[1].PumpResult);
        Assert.Equal(2, health.CallCount);
        Assert.Equal(1, admission.CallCount);
        Assert.All(ClockTimers(clock), timer => Assert.True(timer.IsDisposed));
    }

    private static DurableExternalActivationService CreateService(
        IDurableRuntimeHealth health,
        IDurableRuntimePumpAdmission admission,
        TimeProvider? clock = null,
        ExternalActivationLogger? logger = null) => new(
        health,
        admission,
        clock ?? new ExternalActivationClock(),
        logger ?? new ExternalActivationLogger());

    private static ExternalActivationHealth FixedHealth(DurableRuntimeHealthSnapshot snapshot) =>
        new(_ => ValueTask.FromResult(snapshot));

    private static ExternalActivationAdmission RefusingAdmission() => new((_, _) => ValueTask.FromResult(
        new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Refused, null, null)));

    private static DurableExternalActivationRequest Request(TimeSpan? budget = null) => new(
        new DurableRuntimePumpRequest(5, TimeSpan.FromSeconds(2), DurableRuntimeSurface.Work),
        budget ?? RequestBudget);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static IReadOnlyCollection<ExternalActivationTimer> ClockTimers(ExternalActivationClock clock) =>
        clock.Timers;

    private static Exception Fatal(string name) => name switch
    {
        "stack" => new StackOverflowException("fatal stack sentinel"),
        "memory" => new OutOfMemoryException("fatal memory sentinel"),
        "access" => new AccessViolationException("fatal access sentinel"),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private sealed record HealthCase(
        DurableRuntimeHealthSnapshot Snapshot,
        DurableExternalActivationOutcomeKind ExpectedKind,
        string? ExpectedCode,
        int ExpectedAdmissionCalls);

    private sealed record AdmissionCase(
        DurableRuntimePumpAttempt Attempt,
        DurableExternalActivationOutcomeKind ExpectedKind,
        string? ExpectedCode);
}
