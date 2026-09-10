using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;

/// <summary>Verifies the example's public admission-result projection without requiring PostgreSQL.</summary>
[Collection(DurablePostgreSqlLocalExampleCollection.Name)]
public sealed class DurableRuntimeAdmissionConsumerTests
{
    [Theory]
    [MemberData(nameof(Attempts))]
    public void DescribeAttempt_handles_every_public_kind(DurableRuntimePumpAttempt attempt, string expected)
    {
        var description = DurablePostgreSqlLocalExample.DescribeAttempt(attempt);

        Assert.StartsWith(expected, description, StringComparison.Ordinal);
        if (attempt.Kind == DurableRuntimePumpAttemptKind.Completed)
        {
            Assert.DoesNotContain("RunPassAsync was not entered", description, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("RunPassAsync was not entered", description, StringComparison.Ordinal);
            Assert.Null(attempt.Result);
        }
    }

    [Fact]
    public void HealthCheckpoint_uses_the_activation_predicate()
    {
        DurablePostgreSqlLocalExample.EnsureRuntimeHealthIsCompatible(
            Snapshot(DurableRuntimeHealthState.NotStarted, schemaCompatible: true, epochCompatible: true));

        Assert.Throws<InvalidOperationException>(() =>
            DurablePostgreSqlLocalExample.EnsureRuntimeHealthIsCompatible(
                Snapshot(DurableRuntimeHealthState.Healthy, schemaCompatible: false, epochCompatible: true)));
        Assert.Throws<InvalidOperationException>(() =>
            DurablePostgreSqlLocalExample.EnsureRuntimeHealthIsCompatible(
                Snapshot(DurableRuntimeHealthState.Unavailable, schemaCompatible: false, epochCompatible: false)));
    }

    [Fact]
    public void DirectProof_requires_completed_useful_work_with_zero_failures()
    {
        DurablePostgreSqlLocalExample.EnsureDirectProofPassSucceeded(
            CompletedAttempt(processed: 2, failed: 0));

        var refused = Assert.Throws<InvalidOperationException>(() =>
            DurablePostgreSqlLocalExample.EnsureDirectProofPassSucceeded(
                new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Refused, null, null)));
        var empty = Assert.Throws<InvalidOperationException>(() =>
            DurablePostgreSqlLocalExample.EnsureDirectProofPassSucceeded(
                CompletedAttempt(processed: 0, failed: 0)));
        var failed = Assert.Throws<InvalidOperationException>(() =>
            DurablePostgreSqlLocalExample.EnsureDirectProofPassSucceeded(
                CompletedAttempt(processed: 2, failed: 1)));

        Assert.Contains("Refused", refused.Message, StringComparison.Ordinal);
        Assert.Contains("processed=0", empty.Message, StringComparison.Ordinal);
        Assert.Contains("failed=1", failed.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() =>
            DurablePostgreSqlLocalExample.EnsureDirectProofPassSucceeded(null!));
    }

    public static TheoryData<DurableRuntimePumpAttempt, string> Attempts => new()
    {
        {
            new DurableRuntimePumpAttempt(
                DurableRuntimePumpAttemptKind.Completed,
                new DurableRuntimePumpResult(0, 0, 0, 0, 0, false, null, TimeSpan.Zero),
                null),
            "Completed"
        },
        { new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Refused, null, null), "Refused" },
        {
            new DurableRuntimePumpAttempt(
                DurableRuntimePumpAttemptKind.Unavailable,
                null,
                DurableProblemCodes.StoreUnavailable),
            "Unavailable"
        },
        {
            new DurableRuntimePumpAttempt(
                DurableRuntimePumpAttemptKind.Incompatible,
                null,
                DurableProblemCodes.SchemaUpgradeRequired),
            "Incompatible"
        },
    };

    private static DurableRuntimePumpAttempt CompletedAttempt(int processed, int failed) =>
        new(
            DurableRuntimePumpAttemptKind.Completed,
            new DurableRuntimePumpResult(
                discovered: processed,
                claimed: processed,
                processed,
                deferred: 0,
                failed,
                hasMore: false,
                nextDueAtUtc: null,
                elapsed: TimeSpan.Zero),
            problemCode: null);

    private static DurableRuntimeHealthSnapshot Snapshot(
        DurableRuntimeHealthState state,
        bool schemaCompatible,
        bool epochCompatible)
    {
        var epoch = Guid.NewGuid();
        return new DurableRuntimeHealthSnapshot(
            state,
            problemCode: state == DurableRuntimeHealthState.Unavailable ? DurableProblemCodes.StoreUnavailable : null,
            schemaCompatible,
            epochCompatible,
            installedSchemaVersion: schemaCompatible ? 10 : 0,
            requiredSchemaVersion: 10,
            configuredRuntimeEpoch: epoch,
            activeRuntimeEpoch: epochCompatible ? epoch : null,
            workerId: "consumer-test",
            workerInstanceId: null,
            hostedSurfaces: DurableRuntimeSurface.All,
            observedAtUtc: DateTimeOffset.UtcNow,
            startedAtUtc: null,
            lastHeartbeatAtUtc: null,
            lastSuccessfulSweepAtUtc: null,
            isDraining: false,
            isPassActive: false,
            dueDispatchCount: 0,
            oldestDueAtUtc: null,
            oldestDueAge: null);
    }
}
