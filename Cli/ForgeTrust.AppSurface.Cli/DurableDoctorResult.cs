namespace ForgeTrust.AppSurface.Cli;

/// <summary>Immutable, bounded projection shared by the doctor's text and JSON renderers.</summary>
internal sealed class DurableDoctorResult
{
    internal DurableDoctorResult(
        string status,
        int exitCode,
        IEnumerable<DurableDoctorCheckResult> requestedChecks,
        DateTimeOffset? observedAtUtc,
        DurableDoctorSchemaResult? schema,
        Guid? storeId,
        Guid? configuredRuntimeEpoch,
        Guid? activeRuntimeEpoch,
        DurableDoctorCredentialResult? credential,
        DurableDoctorRetentionResult? retention,
        DurableDoctorWorkerResult? worker,
        IEnumerable<DurableDoctorFinding> findings,
        DurableDoctorAction nextAction)
    {
        Status = status ?? throw new ArgumentNullException(nameof(status));
        ExitCode = exitCode;
        RequestedChecks = DurableDoctorResultCollections.Copy(requestedChecks, 5, nameof(requestedChecks));
        ObservedAtUtc = observedAtUtc?.ToUniversalTime();
        Schema = schema?.Copy();
        StoreId = storeId;
        ConfiguredRuntimeEpoch = configuredRuntimeEpoch;
        ActiveRuntimeEpoch = activeRuntimeEpoch;
        Credential = credential?.Copy();
        Retention = retention?.Copy();
        Worker = worker?.Normalize();
        var findingCopies = DurableDoctorResultCollections.Copy(findings, 12, nameof(findings))
            .Select(static finding => finding?.Copy() ?? throw new ArgumentException("Finding entries cannot be null.", "findings"))
            .ToArray();
        Findings = Array.AsReadOnly(findingCopies);
        NextAction = nextAction?.Copy() ?? throw new ArgumentNullException(nameof(nextAction));
    }

    internal string Status { get; }

    internal int ExitCode { get; }

    internal IReadOnlyList<DurableDoctorCheckResult> RequestedChecks { get; }

    internal DateTimeOffset? ObservedAtUtc { get; }

    internal DurableDoctorSchemaResult? Schema { get; }

    internal Guid? StoreId { get; }

    internal Guid? ConfiguredRuntimeEpoch { get; }

    internal Guid? ActiveRuntimeEpoch { get; }

    internal DurableDoctorCredentialResult? Credential { get; }

    internal DurableDoctorRetentionResult? Retention { get; }

    internal DurableDoctorWorkerResult? Worker { get; }

    internal IReadOnlyList<DurableDoctorFinding> Findings { get; }

    internal DurableDoctorAction NextAction { get; }
}

/// <summary>One fixed v1 check entry in credential, schema, epoch, retention, worker order.</summary>
internal sealed record DurableDoctorCheckResult(string Name, bool Requested, string Status);

/// <summary>Schema facts projected according to the v1 compatibility and availability matrix.</summary>
internal sealed record DurableDoctorSchemaResult(
    string Compatibility,
    int? InstalledVersion,
    int RequiredVersion,
    int? MinimumReaderVersion,
    int? MaximumReaderVersion,
    int? MinimumWriterVersion,
    int? MaximumWriterVersion,
    IReadOnlyList<int>? AppliedVersions,
    IReadOnlyList<int>? PendingVersions)
{
    internal DurableDoctorSchemaResult Copy() => this with
    {
        AppliedVersions = AppliedVersions is null ? null : DurableDoctorResultCollections.Copy(AppliedVersions, 64, nameof(AppliedVersions)),
        PendingVersions = PendingVersions is null ? null : DurableDoctorResultCollections.Copy(PendingVersions, 64, nameof(PendingVersions)),
    };
}

/// <summary>Secret-free evidence that the connected role passed or failed the fixed runtime-role checks.</summary>
internal sealed record DurableDoctorCredentialResult(bool Restricted, IReadOnlyList<string> FailedChecks)
{
    internal DurableDoctorCredentialResult Copy() => this with
    {
        FailedChecks = DurableDoctorResultCollections.Copy(FailedChecks, 6, nameof(FailedChecks)),
    };
}

/// <summary>Secret-free evidence for the schema-11 heartbeat-retention capability.</summary>
internal sealed record DurableDoctorRetentionResult(bool Available, IReadOnlyList<string> FailedChecks)
{
    internal DurableDoctorRetentionResult Copy() => this with
    {
        FailedChecks = DurableDoctorResultCollections.Copy(FailedChecks, 9, nameof(FailedChecks)),
    };
}

/// <summary>Bounded facts for only the worker identity explicitly selected by the caller.</summary>
internal sealed record DurableDoctorWorkerResult(
    string WorkerId,
    bool Found,
    string State,
    Guid? RuntimeEpoch,
    DateTimeOffset? LastHeartbeatAtUtc,
    long? AgeTicks,
    long StaleAfterTicks,
    bool? IsDraining)
{
    internal DurableDoctorWorkerResult Normalize() => this with
    {
        LastHeartbeatAtUtc = LastHeartbeatAtUtc?.ToUniversalTime(),
    };
}

/// <summary>Canonical catalog wording and the finding-specific action for one diagnosis.</summary>
internal sealed record DurableDoctorFinding(
    string Code,
    string Problem,
    string Cause,
    string Fix,
    Uri DocumentationUrl,
    IReadOnlyList<string> FailedChecks,
    DurableDoctorAction NextAction)
{
    internal DurableDoctorFinding Copy() => this with
    {
        FailedChecks = DurableDoctorResultCollections.Copy(FailedChecks, 28, nameof(FailedChecks)),
        NextAction = NextAction?.Copy() ?? throw new ArgumentNullException(nameof(NextAction)),
    };
}

/// <summary>One safe command or application-composition handoff with an absolute documentation destination.</summary>
internal sealed record DurableDoctorAction(
    string Kind,
    DurableDoctorCommandAction? Command,
    IReadOnlyList<string> RequiredInputs,
    Uri DocumentationUrl)
{
    internal DurableDoctorAction Copy() => this with
    {
        Command = Command?.Copy(),
        RequiredInputs = DurableDoctorResultCollections.Copy(RequiredInputs, 1, nameof(RequiredInputs)),
    };
}

/// <summary>Validated executable and bounded argv; arguments remain separate from shell formatting.</summary>
internal sealed record DurableDoctorCommandAction(string Executable, IReadOnlyList<string> Arguments)
{
    internal DurableDoctorCommandAction Copy() => this with
    {
        Arguments = DurableDoctorResultCollections.Copy(Arguments, 14, nameof(Arguments)),
    };
}

/// <summary>Copies bounded-result collections into read-only arrays before they cross the pure output boundary.</summary>
internal static class DurableDoctorResultCollections
{
    internal static IReadOnlyList<T> Copy<T>(IEnumerable<T> values, int maximumCount, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (maximumCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        var copy = values.Take(maximumCount + 1).ToArray();
        if (copy.Length > maximumCount)
        {
            throw new ArgumentException("Doctor result collection exceeds its fixed bound.", parameterName);
        }
        return Array.AsReadOnly(copy);
    }
}
