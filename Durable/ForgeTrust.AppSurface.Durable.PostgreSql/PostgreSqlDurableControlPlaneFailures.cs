using System.Runtime.CompilerServices;
using ForgeTrust.AppSurface.Durable;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Holds canonical, value-free diagnostic destinations shared by PostgreSQL runtime components.</summary>
internal static class PostgreSqlDurableDiagnostics
{
    /// <summary>Gets the operational-assessment troubleshooting anchor.</summary>
    internal const string OperationalAssessmentTroubleshooting =
        "https://github.com/forge-trust/AppSurface/blob/main/Durable/operational-assessments.md#diagnostics-and-recovery";
}

/// <summary>Identifies the bounded PostgreSQL control-plane operation that observed a failure.</summary>
internal enum PostgreSqlDurableControlPlaneOperation
{
    /// <summary>A runtime-health assessment was being read.</summary>
    HealthObservation = 0,

    /// <summary>The durable schema was being validated before pump admission.</summary>
    SchemaAdmission = 1,

    /// <summary>Runtime epoch, worker generation, drain, and pass state were being admitted.</summary>
    RuntimeAdmission = 2,
}

/// <summary>Identifies whether a control-plane failure is safe to return or must propagate.</summary>
internal enum PostgreSqlDurableFailureDisposition
{
    /// <summary>The failure is not an expected pre-execution outcome and must propagate.</summary>
    Propagate = 0,

    /// <summary>The authoritative store could not be observed.</summary>
    Unavailable = 1,

    /// <summary>The observed schema or runtime epoch is incompatible.</summary>
    Incompatible = 2,
}

/// <summary>Identifies the fixed, value-free diagnostic cause for an unavailable store assessment.</summary>
internal enum PostgreSqlDurableUnavailableCause
{
    /// <summary>The PostgreSQL transport or server was unavailable.</summary>
    Transport = 0,

    /// <summary>A provider command deadline elapsed.</summary>
    ProviderDeadline = 1,

    /// <summary>The runtime role cannot read the required control-plane state.</summary>
    PermissionDenied = 2,
}

/// <summary>Identifies package-owned evidence that an inherited provider command deadline elapsed.</summary>
internal enum PostgreSqlDurableTimeoutEvidence
{
    /// <summary>No package-owned provider deadline was observed.</summary>
    None = 0,

    /// <summary>The sidecar deadline derived from the command's positive timeout elapsed.</summary>
    ProviderDeadlineElapsed = 1,
}

/// <summary>Reports the allowlisted interpretation of one PostgreSQL control-plane failure.</summary>
internal readonly record struct PostgreSqlDurableFailureClassification(
    PostgreSqlDurableFailureDisposition Disposition,
    string? ProblemCode,
    PostgreSqlDurableUnavailableCause? UnavailableCause)
{
    /// <summary>Gets the canonical descriptor for a catalog-owned problem code, or null for unclassified failures.</summary>
    internal DurableDiagnosticDescriptor? Diagnostic =>
        PostgreSqlDurableFailureClassifier.DiagnosticFor(ProblemCode);

    /// <summary>Gets the propagate-by-default classification.</summary>
    internal static PostgreSqlDurableFailureClassification Propagate { get; } =
        new(PostgreSqlDurableFailureDisposition.Propagate, null, null);
}

/// <summary>
/// Executes already-configured Npgsql commands with a cancellation sidecar derived from their inherited timeout.
/// </summary>
/// <remarks>
/// The helper does not assign <see cref="NpgsqlCommand.CommandTimeout"/> or alter connection settings. It records
/// typed deadline evidence against the original exception before any boundary-specific translation. Non-query
/// cancellation caused only by that package deadline becomes <see cref="TimeoutException"/>; caller cancellation and
/// every other failure preserve their concrete type, token, SQLSTATE, and stack. The helper is used only for schema
/// status, health observation, and pre-execution runtime admission.
/// </remarks>
internal static class PostgreSqlDurableControlPlaneCommand
{
    private static readonly ConditionalWeakTable<Exception, TimeoutEvidenceHolder> TimeoutEvidence = new();
    private static readonly ConditionalWeakTable<Exception, CallerCancellationEvidenceHolder> CallerCancellationEvidence = new();

    /// <summary>Executes a non-query control-plane command.</summary>
    internal static ValueTask<int> ExecuteNonQueryAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken) =>
        ExecuteNonQueryAsync(command, cancellationToken, command.ExecuteNonQueryAsync);

    /// <summary>Executes a non-query control-plane operation and maps only package-owned deadline cancellation.</summary>
    /// <param name="command">The configured command whose positive timeout defines the package deadline.</param>
    /// <param name="cancellationToken">The caller-owned cancellation token.</param>
    /// <param name="operation">The operation to execute with the effective cancellation token.</param>
    /// <returns>The number of rows affected by the operation.</returns>
    /// <exception cref="TimeoutException">
    /// Thrown when the package-owned command deadline expires before caller cancellation.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Propagates caller-owned cancellation without translating it.
    /// </exception>
    internal static async ValueTask<int> ExecuteNonQueryAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<int>> operation)
    {
        try
        {
            return await ExecuteOperationAsync(command, cancellationToken, operation).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested
            && GetTimeoutEvidence(exception) == PostgreSqlDurableTimeoutEvidence.ProviderDeadlineElapsed)
        {
            throw new TimeoutException(
                $"The PostgreSQL control-plane command exceeded its configured {command.CommandTimeout}-second timeout.",
                exception);
        }
    }

    /// <summary>Executes a scalar control-plane command.</summary>
    internal static ValueTask<object?> ExecuteScalarAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken) =>
        ExecuteOperationAsync(command, cancellationToken, command.ExecuteScalarAsync);

    /// <summary>Executes and completely projects a control-plane reader while its deadline remains active.</summary>
    internal static ValueTask<TResult> ExecuteReaderAsync<TResult>(
        NpgsqlCommand command,
        Func<NpgsqlDataReader, CancellationToken, ValueTask<TResult>> projector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projector);
        return ExecuteOperationAsync(
            command,
            cancellationToken,
            async effectiveToken =>
            {
                await using var reader = await command.ExecuteReaderAsync(effectiveToken).ConfigureAwait(false);
                return await projector(reader, effectiveToken).ConfigureAwait(false);
            });
    }

    /// <summary>Gets deadline evidence recorded for the original exception.</summary>
    internal static PostgreSqlDurableTimeoutEvidence GetTimeoutEvidence(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return TimeoutEvidence.TryGetValue(exception, out var holder)
            ? holder.Value
            : PostgreSqlDurableTimeoutEvidence.None;
    }

    /// <summary>Reports whether the command's linked token propagated cancellation from this exact caller token.</summary>
    /// <remarks>
    /// This provenance does not replace the original exception or its token. A doctor-owned deadline can therefore
    /// be recognized through the shared status reader while cancellation from an unrelated source still propagates.
    /// </remarks>
    internal static bool IsCancellationFrom(Exception exception, CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return callerToken.CanBeCanceled
            && CallerCancellationEvidence.TryGetValue(exception, out var holder)
            && holder.Token == callerToken;
    }

    /// <summary>
    /// Records package-owned deadline evidence while retaining the original exception object.
    /// </summary>
    internal static void RecordTimeoutEvidence(
        Exception exception,
        PostgreSqlDurableTimeoutEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (!Enum.IsDefined(evidence))
        {
            throw new ArgumentOutOfRangeException(nameof(evidence));
        }

        if (evidence == PostgreSqlDurableTimeoutEvidence.None)
        {
            TimeoutEvidence.Remove(exception);
            return;
        }

        TimeoutEvidence.AddOrUpdate(exception, new TimeoutEvidenceHolder(evidence));
    }

    /// <summary>
    /// Executes one control-plane operation through the inherited command-timeout sidecar.
    /// </summary>
    /// <remarks>
    /// This internal seam lets tests exercise cancellation precedence without depending on provider timer races.
    /// Production callers use the command-shaped helpers above.
    /// </remarks>
    internal static async ValueTask<TResult> ExecuteOperationAsync<TResult>(
        NpgsqlCommand command,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<TResult>> operation)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(operation);

        if (command.CommandTimeout <= 0)
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }

        using var providerDeadline = new CancellationTokenSource(
            TimeSpan.FromSeconds(command.CommandTimeout));
        using var effectiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            providerDeadline.Token);
        try
        {
            return await operation(effectiveCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            if (cancellationToken.IsCancellationRequested
                && exception is OperationCanceledException canceled
                && canceled.CancellationToken == effectiveCancellation.Token)
            {
                CallerCancellationEvidence.AddOrUpdate(exception, new CallerCancellationEvidenceHolder(cancellationToken));
            }
            if (providerDeadline.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested
                && IsCancellationShaped(exception, effectiveCancellation.Token))
            {
                RecordTimeoutEvidence(
                    exception,
                    PostgreSqlDurableTimeoutEvidence.ProviderDeadlineElapsed);
            }

            throw;
        }
    }

    private static bool IsCancellationShaped(Exception exception, CancellationToken effectiveToken)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException canceled && canceled.CancellationToken == effectiveToken
                || current is PostgresException { SqlState: PostgresErrorCodes.QueryCanceled })
            {
                return true;
            }
        }

        return false;
    }

    private sealed record TimeoutEvidenceHolder(PostgreSqlDurableTimeoutEvidence Value);
    private sealed record CallerCancellationEvidenceHolder(CancellationToken Token);
}

/// <summary>Classifies only explicit, pre-execution PostgreSQL control-plane evidence.</summary>
internal static class PostgreSqlDurableFailureClassifier
{
    /// <summary>
    /// Returns an allowlisted unavailable or incompatible classification, otherwise the propagate result.
    /// </summary>
    internal static PostgreSqlDurableFailureClassification Classify(
        PostgreSqlDurableControlPlaneOperation operation,
        Exception exception,
        CancellationToken callerCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OperationCanceledException && callerCancellationToken.IsCancellationRequested)
        {
            return PostgreSqlDurableFailureClassification.Propagate;
        }

        if (exception is DurableRuntimeSchemaException schemaException)
        {
            var diagnostic = DiagnosticForSchema(schemaException.Status.Compatibility);
            return new PostgreSqlDurableFailureClassification(
                PostgreSqlDurableFailureDisposition.Incompatible,
                diagnostic.Code,
                null);
        }

        var timeoutEvidence = PostgreSqlDurableControlPlaneCommand.GetTimeoutEvidence(exception);
        var postgresException = Find<PostgresException>(exception);
        if (Find<TimeoutException>(exception) is not null
            || (timeoutEvidence == PostgreSqlDurableTimeoutEvidence.ProviderDeadlineElapsed
                && (exception is OperationCanceledException
                    || string.Equals(postgresException?.SqlState, PostgresErrorCodes.QueryCanceled, StringComparison.Ordinal))))
        {
            return Unavailable(PostgreSqlDurableUnavailableCause.ProviderDeadline);
        }

        if (postgresException is not null)
        {
            if (string.Equals(postgresException.SqlState, PostgresErrorCodes.InsufficientPrivilege, StringComparison.Ordinal)
                && operation is PostgreSqlDurableControlPlaneOperation.HealthObservation
                    or PostgreSqlDurableControlPlaneOperation.SchemaAdmission
                    or PostgreSqlDurableControlPlaneOperation.RuntimeAdmission)
            {
                return Unavailable(PostgreSqlDurableUnavailableCause.PermissionDenied);
            }

            if (IsTransportSqlState(postgresException.SqlState))
            {
                return Unavailable(PostgreSqlDurableUnavailableCause.Transport);
            }

            return PostgreSqlDurableFailureClassification.Propagate;
        }

        if (Find<NpgsqlException>(exception) is { IsTransient: true })
        {
            return Unavailable(PostgreSqlDurableUnavailableCause.Transport);
        }

        return PostgreSqlDurableFailureClassification.Propagate;
    }

    /// <summary>Maps a schema compatibility value to its stable public problem code.</summary>
    internal static string ProblemForSchema(DurableRuntimeSchemaCompatibility compatibility) =>
        DiagnosticForSchema(compatibility).Code;

    /// <summary>Gets the canonical descriptor for an existing schema-compatibility health/admission code.</summary>
    internal static DurableDiagnosticDescriptor DiagnosticForSchema(DurableRuntimeSchemaCompatibility compatibility)
    {
        var problemCode = compatibility switch
        {
            DurableRuntimeSchemaCompatibility.Missing => DurableProblemCodes.SchemaMissing,
            DurableRuntimeSchemaCompatibility.UpgradeRequired => DurableProblemCodes.SchemaUpgradeRequired,
            DurableRuntimeSchemaCompatibility.StoreTooNew => DurableProblemCodes.SchemaVersionUnsupported,
            _ => DurableProblemCodes.SchemaInconsistent,
        };
        return RequireDiagnostic(problemCode);
    }

    /// <summary>Gets the canonical descriptor for a known code; unknown application codes remain unclassified.</summary>
    internal static DurableDiagnosticDescriptor? DiagnosticFor(string? problemCode) =>
        problemCode is not null && DurableDiagnosticCatalog.TryGet(problemCode, out var descriptor)
            ? descriptor
            : null;

    /// <summary>Returns a known internal health/admission code through its canonical descriptor.</summary>
    internal static string? CanonicalProblemCode(string? problemCode) =>
        DiagnosticFor(problemCode)?.Code ?? problemCode;

    private static PostgreSqlDurableFailureClassification Unavailable(
        PostgreSqlDurableUnavailableCause cause)
    {
        var diagnostic = RequireDiagnostic(DurableProblemCodes.StoreUnavailable);
        return new PostgreSqlDurableFailureClassification(
            PostgreSqlDurableFailureDisposition.Unavailable,
            diagnostic.Code,
            cause);
    }

    private static DurableDiagnosticDescriptor RequireDiagnostic(string problemCode) =>
        DiagnosticFor(problemCode)
        ?? throw new InvalidOperationException($"The PostgreSQL control-plane diagnostic code '{problemCode}' is not cataloged.");

    private static bool IsTransportSqlState(string sqlState) =>
        sqlState.StartsWith("08", StringComparison.Ordinal)
        || string.Equals(sqlState, PostgresErrorCodes.TooManyConnections, StringComparison.Ordinal)
        || string.Equals(sqlState, PostgresErrorCodes.AdminShutdown, StringComparison.Ordinal)
        || string.Equals(sqlState, PostgresErrorCodes.CrashShutdown, StringComparison.Ordinal)
        || string.Equals(sqlState, PostgresErrorCodes.CannotConnectNow, StringComparison.Ordinal);

    private static TException? Find<TException>(Exception exception)
        where TException : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException match)
            {
                return match;
            }
        }

        return null;
    }
}
