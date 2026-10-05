using ForgeTrust.AppSurface.Durable.PostgreSql;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>Validated, printable doctor intent. Connection values remain private to the invocation.</summary>
/// <remarks>Names identify environment sources, not their values. Worker and threshold are paired. See the doctor command reference.</remarks>
internal sealed record DurableDoctorRequest(
    Guid ConfiguredRuntimeEpoch, string ConnectionEnvironmentName, string EpochEnvironmentName,
    string? WorkerId, TimeSpan? StaleAfter, TimeSpan Timeout, string Format);

/// <summary>Fixed categories accepted by the version-one observation and output contract.</summary>
internal static class DurableDoctorChecks
{
    internal static readonly IReadOnlyList<string> Credential = Array.AsReadOnly(new[]
    { "caller-role", "role-attributes", "role-membership", "role-ownership", "role-grant-options", "heartbeat-table-privileges" });
    internal static readonly IReadOnlyList<string> Retention = Array.AsReadOnly(new[]
    { "function-signature", "function-owner", "function-security-definer", "function-search-path", "function-execute", "function-public-acl", "function-grant-option", "retention-index-presence", "retention-index-shape" });
    internal static readonly IReadOnlyList<string> All = Array.AsReadOnly(new[] { "input", "session-affinity" }
        .Concat(Credential).Concat(new[] { "schema-compatibility", "active-epoch" }).Concat(Retention)
        .Concat(new[] { "worker-heartbeat-missing", "worker-heartbeat-epoch", "worker-heartbeat-stale", "worker-draining", "dependency", "deadline", "cleanup", "caller-canceled", "catalog-contract" }).ToArray());
}

/// <summary>Bounded immutable observations captured on the single fenced database session.</summary>
/// <remarks>Null denotes a check not reached. Copies prevent mutable caller arrays entering classification.</remarks>
internal sealed class DurableDoctorObservation
{
    private const int MaximumSchemaVersions = 64;
    private readonly DurableRuntimeSchemaStatus? _schema;

    internal DurableDoctorObservation(IEnumerable<string> credentialFailures, DurableRuntimeSchemaStatus? schema = null,
        IEnumerable<string>? retentionFailures = null, DateTimeOffset? observedAtUtc = null,
        Guid? storeId = null, Guid? activeRuntimeEpoch = null, DurableDoctorHeartbeat? heartbeat = null)
    {
        ArgumentNullException.ThrowIfNull(credentialFailures);
        CredentialFailures = CopyCategories(credentialFailures, DurableDoctorChecks.Credential.Count);
        _schema = schema is null ? null : CopySchema(schema);
        RetentionFailures = retentionFailures is null ? null : CopyCategories(retentionFailures, DurableDoctorChecks.Retention.Count);
        ObservedAtUtc = observedAtUtc;
        StoreId = storeId;
        ActiveRuntimeEpoch = activeRuntimeEpoch;
        Heartbeat = heartbeat;
    }

    internal IReadOnlyList<string> CredentialFailures { get; }
    /// <summary>Returns a defensive copy of captured schema facts without the source status's free-form problem text.</summary>
    internal DurableRuntimeSchemaStatus? Schema => _schema is null ? null : CopySchema(_schema);
    internal IReadOnlyList<string>? RetentionFailures { get; }
    internal DateTimeOffset? ObservedAtUtc { get; }
    internal Guid? StoreId { get; }
    internal Guid? ActiveRuntimeEpoch { get; }
    internal DurableDoctorHeartbeat? Heartbeat { get; }

    private static IReadOnlyList<string> CopyCategories(IEnumerable<string> categories, int maximum)
    {
        var copy = categories.Take(maximum + 1).ToArray();
        if (copy.Length > maximum)
        {
            throw new InvalidDataException("Doctor category projection exceeds its fixed bound.");
        }
        return Array.AsReadOnly(copy);
    }

    private static DurableRuntimeSchemaStatus CopySchema(DurableRuntimeSchemaStatus schema)
    {
        var applied = CopyVersions(schema.AppliedVersions, "appliedVersions");
        var pending = CopyVersions(schema.PendingVersions, "pendingVersions");
        return new DurableRuntimeSchemaStatus(
            schema.Compatibility,
            schema.StoreId,
            schema.ActiveRuntimeEpoch,
            schema.InstalledVersion,
            schema.RequiredVersion,
            schema.MinimumReaderVersion,
            schema.MaximumReaderVersion,
            schema.MinimumWriterVersion,
            schema.MaximumWriterVersion,
            applied,
            pending,
            problem: null);
    }

    private static int[] CopyVersions(IReadOnlyList<int>? versions, string name)
    {
        if (versions is null)
        {
            throw new InvalidDataException("Doctor schema version facts are unavailable.");
        }

        var copy = versions.Take(MaximumSchemaVersions + 1).ToArray();
        if (copy.Length > MaximumSchemaVersions)
        {
            throw new InvalidDataException("Doctor schema version facts exceed their fixed bound.");
        }
        return copy;
    }
}

/// <summary>Selected heartbeat facts only; a missing row is represented by Found=false and null row values.</summary>
internal sealed record DurableDoctorHeartbeat(bool Found, Guid? RuntimeEpoch, DateTimeOffset? LastHeartbeatAtUtc, bool? IsDraining);

/// <summary>Terminal execution family, with caller cancellation winning over every other family.</summary>
internal enum DurableDoctorFailureKind { Unavailable, Canceled, Failed }

/// <summary>Secret-free failure envelope. Only fixed categories may reach rendering.</summary>
internal sealed class DurableDoctorFailureException(DurableDoctorFailureKind kind, params string[] categories) : Exception
{
    internal DurableDoctorFailureKind Kind { get; } = kind;
    internal IReadOnlyList<string> Categories { get; } = Array.AsReadOnly(categories.ToArray());
}

/// <summary>Dedicated read-only operation; implementations do not register a Durable worker or mutate its store.</summary>
internal interface IDurableDoctorService
{
    /// <summary>Inspects a validated request and returns only after checked owned resource release.</summary>
    /// <param name="connectionString">Private ephemeral configuration, never included in observations or failures.</param>
    /// <param name="request">Validated printable intent.</param>
    /// <param name="cancellationToken">Original caller cancellation, distinct from the operation deadline.</param>
    ValueTask<DurableDoctorObservation> InspectAsync(string connectionString, DurableDoctorRequest request, CancellationToken cancellationToken);
}
