using System.IO;
using ForgeTrust.AppSurface.Durable;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableFailureClassifierTests
{
    [Fact]
    public void Classify_MapsEveryAllowlistedTransportSqlStateToUnavailable()
    {
        foreach (var sqlState in new[] { "08000", "08001", "08003", "08006", "53300", "57P01", "57P02", "57P03" })
        {
            var classification = Classify(Postgres(sqlState));

            Assert.Equal(PostgreSqlDurableFailureDisposition.Unavailable, classification.Disposition);
            Assert.Equal(DurableProblemCodes.StoreUnavailable, classification.ProblemCode);
            Assert.Equal(PostgreSqlDurableUnavailableCause.Transport, classification.UnavailableCause);
        }
    }

    [Theory]
    [InlineData((int)PostgreSqlDurableControlPlaneOperation.HealthObservation)]
    [InlineData((int)PostgreSqlDurableControlPlaneOperation.SchemaAdmission)]
    [InlineData((int)PostgreSqlDurableControlPlaneOperation.RuntimeAdmission)]
    public void Classify_MapsControlPlanePermissionFailureToUnavailable(
        int operation)
    {
        var classification = Classify(
            Postgres(PostgresErrorCodes.InsufficientPrivilege),
            (PostgreSqlDurableControlPlaneOperation)operation);

        Assert.Equal(PostgreSqlDurableFailureDisposition.Unavailable, classification.Disposition);
        Assert.Equal(DurableProblemCodes.StoreUnavailable, classification.ProblemCode);
        Assert.Equal(PostgreSqlDurableUnavailableCause.PermissionDenied, classification.UnavailableCause);
    }

    [Fact]
    public void Classify_MapsProviderTimeoutEvidenceAndTimeoutExceptionsToUnavailable()
    {
        var canceled = Postgres(PostgresErrorCodes.QueryCanceled);
        PostgreSqlDurableControlPlaneCommand.RecordTimeoutEvidence(
            canceled,
            PostgreSqlDurableTimeoutEvidence.ProviderDeadlineElapsed);

        var typedCancellation = new OperationCanceledException();
        PostgreSqlDurableControlPlaneCommand.RecordTimeoutEvidence(
            typedCancellation,
            PostgreSqlDurableTimeoutEvidence.ProviderDeadlineElapsed);

        foreach (var exception in new Exception[]
                 {
                     new TimeoutException("provider timeout"),
                     new NpgsqlException("provider timeout", new TimeoutException("socket timeout")),
                     canceled,
                     typedCancellation,
                 })
        {
            var classification = Classify(exception);

            Assert.Equal(PostgreSqlDurableFailureDisposition.Unavailable, classification.Disposition);
            Assert.Equal(DurableProblemCodes.StoreUnavailable, classification.ProblemCode);
            Assert.Equal(PostgreSqlDurableUnavailableCause.ProviderDeadline, classification.UnavailableCause);
        }
    }

    [Fact]
    public void Classify_MapsTransientProviderTransportFailureToUnavailable()
    {
        var exception = new NpgsqlException(
            "transport failed",
            new IOException("socket closed"));

        var classification = Classify(exception);

        Assert.True(exception.IsTransient);
        Assert.Equal(PostgreSqlDurableFailureDisposition.Unavailable, classification.Disposition);
        Assert.Equal(DurableProblemCodes.StoreUnavailable, classification.ProblemCode);
        Assert.Equal(PostgreSqlDurableUnavailableCause.Transport, classification.UnavailableCause);
    }

    [Fact]
    public void Classify_GivesCallerCancellationPrecedenceOverProviderDeadline()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var exception = new OperationCanceledException(cancellation.Token);
        PostgreSqlDurableControlPlaneCommand.RecordTimeoutEvidence(
            exception,
            PostgreSqlDurableTimeoutEvidence.ProviderDeadlineElapsed);

        var classification = Classify(exception, cancellationToken: cancellation.Token);

        Assert.Equal(PostgreSqlDurableFailureDisposition.Propagate, classification.Disposition);
        Assert.Null(classification.ProblemCode);
        Assert.Null(classification.UnavailableCause);
    }

    [Theory]
    [InlineData(PostgresErrorCodes.QueryCanceled)]
    [InlineData(PostgresErrorCodes.UndefinedTable)]
    [InlineData(PostgresErrorCodes.UndefinedFunction)]
    [InlineData("22000")]
    public void Classify_PropagatesBareCancellationSchemaDefectsAndUnlistedProviderFailures(
        string sqlState)
    {
        var classification = Classify(Postgres(sqlState));

        Assert.Equal(PostgreSqlDurableFailureDisposition.Propagate, classification.Disposition);
        Assert.Null(classification.ProblemCode);
        Assert.Null(classification.UnavailableCause);
    }

    [Fact]
    public void Classify_PropagatesUnrelatedCancellationAndProgrammingFailures()
    {
        Assert.Equal(
            PostgreSqlDurableFailureDisposition.Propagate,
            Classify(new OperationCanceledException()).Disposition);
        Assert.Equal(
            PostgreSqlDurableFailureDisposition.Propagate,
            Classify(new InvalidDataException("malformed provider row")).Disposition);
        Assert.Equal(
            PostgreSqlDurableFailureDisposition.Propagate,
            Classify(new InvalidOperationException("programming failure")).Disposition);
    }

    [Fact]
    public void Classify_PropagatesPermissionFailureOutsideTheControlPlane()
    {
        var classification = Classify(
            Postgres(PostgresErrorCodes.InsufficientPrivilege),
            (PostgreSqlDurableControlPlaneOperation)999);

        Assert.Equal(PostgreSqlDurableFailureDisposition.Propagate, classification.Disposition);
        Assert.Null(classification.ProblemCode);
        Assert.Null(classification.UnavailableCause);
    }

    [Fact]
    public void Classify_RequiresMatchingProviderEvidenceForQueryCancellation()
    {
        var withoutEvidence = Classify(Postgres(PostgresErrorCodes.QueryCanceled));
        Assert.Equal(PostgreSqlDurableFailureDisposition.Propagate, withoutEvidence.Disposition);

        var withEvidence = Postgres(PostgresErrorCodes.QueryCanceled);
        PostgreSqlDurableControlPlaneCommand.RecordTimeoutEvidence(
            withEvidence,
            PostgreSqlDurableTimeoutEvidence.ProviderDeadlineElapsed);
        var classified = Classify(withEvidence);
        Assert.Equal(PostgreSqlDurableFailureDisposition.Unavailable, classified.Disposition);
        Assert.Equal(PostgreSqlDurableUnavailableCause.ProviderDeadline, classified.UnavailableCause);
    }

    [Theory]
    [InlineData(DurableRuntimeSchemaCompatibility.Missing, DurableProblemCodes.SchemaMissing)]
    [InlineData(DurableRuntimeSchemaCompatibility.UpgradeRequired, DurableProblemCodes.SchemaUpgradeRequired)]
    [InlineData(DurableRuntimeSchemaCompatibility.StoreTooNew, DurableProblemCodes.SchemaVersionUnsupported)]
    [InlineData(DurableRuntimeSchemaCompatibility.Inconsistent, DurableProblemCodes.SchemaInconsistent)]
    public void Classify_MapsObservedSchemaStatusToItsExactIncompatibility(
        DurableRuntimeSchemaCompatibility compatibility,
        string expectedProblemCode)
    {
        var status = new DurableRuntimeSchemaStatus(
            compatibility,
            Guid.NewGuid(),
            null,
            installedVersion: 1,
            requiredVersion: 2,
            minimumReaderVersion: 1,
            maximumReaderVersion: 2,
            minimumWriterVersion: 1,
            maximumWriterVersion: 2,
            appliedVersions: [1],
            pendingVersions: [2],
            problem: "schema status");

        var classification = Classify(new DurableRuntimeSchemaException(status));

        Assert.Equal(PostgreSqlDurableFailureDisposition.Incompatible, classification.Disposition);
        Assert.Equal(expectedProblemCode, classification.ProblemCode);
        Assert.Null(classification.UnavailableCause);
    }

    [Fact]
    public void TimeoutEvidence_RejectsUndefinedValuesAndCanBeCleared()
    {
        var exception = new OperationCanceledException();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PostgreSqlDurableControlPlaneCommand.RecordTimeoutEvidence(
                exception,
                (PostgreSqlDurableTimeoutEvidence)999));

        PostgreSqlDurableControlPlaneCommand.RecordTimeoutEvidence(
            exception,
            PostgreSqlDurableTimeoutEvidence.ProviderDeadlineElapsed);
        Assert.Equal(
            PostgreSqlDurableTimeoutEvidence.ProviderDeadlineElapsed,
            PostgreSqlDurableControlPlaneCommand.GetTimeoutEvidence(exception));

        PostgreSqlDurableControlPlaneCommand.RecordTimeoutEvidence(
            exception,
            PostgreSqlDurableTimeoutEvidence.None);
        Assert.Equal(
            PostgreSqlDurableTimeoutEvidence.None,
            PostgreSqlDurableControlPlaneCommand.GetTimeoutEvidence(exception));
    }

    [Fact]
    public async Task TimeoutEvidence_ConcurrentReplacementDoesNotThrowOrLoseEvidence()
    {
        var exception = new OperationCanceledException();
        using var start = new Barrier(8);
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            start.SignalAndWait();
            for (var iteration = 0; iteration < 10_000; iteration++)
            {
                PostgreSqlDurableControlPlaneCommand.RecordTimeoutEvidence(
                    exception,
                    PostgreSqlDurableTimeoutEvidence.ProviderDeadlineElapsed);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(
            PostgreSqlDurableTimeoutEvidence.ProviderDeadlineElapsed,
            PostgreSqlDurableControlPlaneCommand.GetTimeoutEvidence(exception));
    }

    [Fact]
    public async Task ControlPlaneCommand_RecordsItsInheritedProviderDeadline()
    {
        await using var command = new NpgsqlCommand
        {
            CommandTimeout = 1,
        };

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await PostgreSqlDurableControlPlaneCommand.ExecuteOperationAsync(
                command,
                CancellationToken.None,
                static async effectiveToken =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, effectiveToken);
                    return 0;
                }));

        Assert.Equal(
            PostgreSqlDurableTimeoutEvidence.ProviderDeadlineElapsed,
            PostgreSqlDurableControlPlaneCommand.GetTimeoutEvidence(exception));
        var classification = Classify(exception);
        Assert.Equal(PostgreSqlDurableFailureDisposition.Unavailable, classification.Disposition);
        Assert.Equal(PostgreSqlDurableUnavailableCause.ProviderDeadline, classification.UnavailableCause);
    }

    [Fact]
    public async Task ControlPlaneCommand_PreservesCallerCancellationAndDisabledTimeouts()
    {
        await using var disabledTimeoutCommand = new NpgsqlCommand
        {
            CommandTimeout = 0,
        };
        using var callerCancellation = new CancellationTokenSource();
        var observedToken = CancellationToken.None;
        var result = await PostgreSqlDurableControlPlaneCommand.ExecuteOperationAsync(
            disabledTimeoutCommand,
            callerCancellation.Token,
            effectiveToken =>
            {
                observedToken = effectiveToken;
                return Task.FromResult(42);
            });
        Assert.Equal(42, result);
        Assert.Equal(callerCancellation.Token, observedToken);

        await using var boundedCommand = new NpgsqlCommand
        {
            CommandTimeout = 30,
        };
        callerCancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await PostgreSqlDurableControlPlaneCommand.ExecuteOperationAsync(
                boundedCommand,
                callerCancellation.Token,
                static effectiveToken => Task.FromCanceled<int>(effectiveToken)));

        Assert.Equal(
            PostgreSqlDurableTimeoutEvidence.None,
            PostgreSqlDurableControlPlaneCommand.GetTimeoutEvidence(exception));
        Assert.Equal(
            PostgreSqlDurableFailureDisposition.Propagate,
            Classify(exception, cancellationToken: callerCancellation.Token).Disposition);
    }

    [Fact]
    public async Task ControlPlaneNonQuery_MapsOnlyItsProviderDeadlineToTimeoutException()
    {
        await using var command = new NpgsqlCommand
        {
            CommandTimeout = 1,
        };

        var providerTimeout = await Assert.ThrowsAsync<TimeoutException>(
            async () => await PostgreSqlDurableControlPlaneCommand.ExecuteNonQueryAsync(
                command,
                CancellationToken.None,
                static async effectiveToken =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, effectiveToken);
                    return 0;
                }));

        Assert.IsAssignableFrom<OperationCanceledException>(providerTimeout.InnerException);

        using var callerCancellation = new CancellationTokenSource();
        callerCancellation.Cancel();
        var callerException = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await PostgreSqlDurableControlPlaneCommand.ExecuteNonQueryAsync(
                command,
                callerCancellation.Token,
                static effectiveToken => Task.FromCanceled<int>(effectiveToken)));

        Assert.Equal(
            PostgreSqlDurableTimeoutEvidence.None,
            PostgreSqlDurableControlPlaneCommand.GetTimeoutEvidence(callerException));
    }

    [Fact]
    public void AdmissionFailureContext_MarksOnlyTheOriginalIndeterminateFailure()
    {
        var indeterminate = new NpgsqlException("acknowledgement lost");
        var unrelated = new NpgsqlException("connection not opened");

        Assert.False(PostgreSqlDurableAdmissionFailureContext.TakeIndeterminate(indeterminate));
        PostgreSqlDurableAdmissionFailureContext.MarkIndeterminate(indeterminate);

        Assert.True(PostgreSqlDurableAdmissionFailureContext.TakeIndeterminate(indeterminate));
        Assert.False(PostgreSqlDurableAdmissionFailureContext.TakeIndeterminate(indeterminate));
        Assert.False(PostgreSqlDurableAdmissionFailureContext.TakeIndeterminate(unrelated));
        Assert.Throws<ArgumentNullException>(() =>
            PostgreSqlDurableAdmissionFailureContext.MarkIndeterminate(null!));
        Assert.Throws<ArgumentNullException>(() =>
            PostgreSqlDurableAdmissionFailureContext.TakeIndeterminate(null!));
    }

    [Fact]
    public async Task AdmissionFailureContext_ConcurrentMarkingRetainsMarkerAndTakeConsumesOnce()
    {
        var indeterminate = new NpgsqlException("concurrent acknowledgement lost");
        using var start = new Barrier(8);
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            start.SignalAndWait();
            for (var iteration = 0; iteration < 10_000; iteration++)
            {
                PostgreSqlDurableAdmissionFailureContext.MarkIndeterminate(indeterminate);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.True(PostgreSqlDurableAdmissionFailureContext.TakeIndeterminate(indeterminate));
        Assert.False(PostgreSqlDurableAdmissionFailureContext.TakeIndeterminate(indeterminate));
    }

    private static PostgreSqlDurableFailureClassification Classify(
        Exception exception,
        PostgreSqlDurableControlPlaneOperation operation =
            PostgreSqlDurableControlPlaneOperation.HealthObservation,
        CancellationToken cancellationToken = default) =>
        PostgreSqlDurableFailureClassifier.Classify(
            operation,
            exception,
            cancellationToken);

    private static PostgresException Postgres(string sqlState) =>
        new("server-controlled detail", "ERROR", "ERROR", sqlState);
}
