namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>Explicitly selects the authority and capabilities of an evidence execution.</summary>
public enum EvidenceExecutionMode
{
    /// <summary>Requires a registered protected verifier and an accepted consumer/platform proof.</summary>
    Trusted,

    /// <summary>Uses a declared dependency-free, secret-free profile and can issue only informational evidence.</summary>
    Observation,
}

/// <summary>Records the bounded, non-secret facts accepted by the protected verifier.</summary>
/// <param name="SchemaVersion">Supported assertion schema, currently <c>1.0</c>.</param>
/// <param name="VerifierId">Protected registration identifier.</param>
/// <param name="VerifierVersion">Exact protected registration version.</param>
/// <param name="Provider">Registered CI provider.</param>
/// <param name="WorkflowIdentity">Immutable protected workflow identity.</param>
/// <param name="RunId">Current CI execution identity including its attempt.</param>
/// <param name="BaseRevision">Immutable protected tool and policy revision.</param>
/// <param name="SubjectRevision">Exact tested revision.</param>
/// <param name="ToolRootIdentity">Protected tool root identity.</param>
/// <param name="SubjectRootIdentity">Separately restricted subject root identity.</param>
/// <param name="OutputParentIdentity">Protected output allocation parent identity.</param>
/// <param name="OutputIdentity">Actual fresh output handle identity, bound after admission.</param>
/// <param name="AllocationPolicyDigest">Digest of the protected allocation policy.</param>
/// <param name="CatalogueDigest">Digest of the protected producer/resource catalogue.</param>
/// <param name="CapabilitiesDigest">Digest of non-secret accepted capabilities.</param>
/// <param name="AcceptanceProofDigest">Matching immutable consumer/platform acceptance proof.</param>
/// <param name="VerifiedAtUtc">Protected verifier time.</param>
/// <remarks>
/// This serialized record grants no authority. It documents structural validation without independent sandbox
/// attestation. Gates need expected identities and artifacts through a protected channel.
/// </remarks>
public sealed record EvidenceEnvelopeAssertion(
    string SchemaVersion,
    string VerifierId,
    string VerifierVersion,
    string Provider,
    string WorkflowIdentity,
    string RunId,
    string BaseRevision,
    string SubjectRevision,
    string ToolRootIdentity,
    string SubjectRootIdentity,
    string OutputParentIdentity,
    string OutputIdentity,
    string AllocationPolicyDigest,
    string CatalogueDigest,
    string CapabilitiesDigest,
    string AcceptanceProofDigest,
    DateTimeOffset VerifiedAtUtc);

/// <summary>A single-use runtime capability issued by the supervised host for one immutable plan.</summary>
/// <remarks>
/// Obtain this capability through a supported supervised execution entry. It has no public constructor or JSON
/// factory. Deserialized assertions, environment flags and envelope-status enums cannot replace it. The builder
/// accepts it only after the host joins owned work, verifies artifacts and finishes cleanup. See the
/// <c>EvidenceHost trust-boundary</c> guide for protected consumer acceptance and fatal recovery.
/// </remarks>
public sealed class EvidenceAdmissionResult
{
    private readonly object _sync = new();
    private readonly EvidencePlan _snapshot;
    private AdmissionState _state;
    private bool _terminalFailure;

    /// <summary>Creates an admitted capability only from the shared internal admission boundary.</summary>
    internal EvidenceAdmissionResult(EvidencePlan snapshot, EvidenceExecutionMode mode, string runId, EvidenceEnvelopeAssertion? assertion)
    {
        _snapshot = snapshot;
        Mode = mode;
        RunId = runId;
        Assertion = assertion;
    }

    /// <summary>Gets the explicit mode selected before execution.</summary>
    public EvidenceExecutionMode Mode { get; }

    /// <summary>Gets the protected run identity bound to this capability.</summary>
    public string RunId { get; }

    /// <summary>Gets accepted non-secret envelope facts; Observation has no verifier assertion.</summary>
    public EvidenceEnvelopeAssertion? Assertion { get; private set; }

    /// <summary>Checks that an application factory or artifact write belongs to the still-active admitted execution.</summary>
    internal void ValidateActive(EvidencePlan? plan = null)
    {
        lock (_sync)
        {
            if (_state != AdmissionState.Running || _terminalFailure
                || (plan is not null && !EvidenceCanonicalJson.Serialize(_snapshot).AsSpan().SequenceEqual(EvidenceCanonicalJson.Serialize(plan))))
            {
                throw new EvidenceAdmissionException("ASEVD410", "Execution admission is closed or does not match this plan.");
            }
        }
    }

    /// <summary>Activates once after exclusive output allocation binds the actual retained handle identity.</summary>
    internal void Activate(string outputIdentity)
    {
        lock (_sync)
        {
            if (_state != AdmissionState.Admitted || _terminalFailure || string.IsNullOrWhiteSpace(outputIdentity))
            {
                throw new EvidenceAdmissionException("ASEVD409", "Output activation is unavailable.");
            }

            if (Assertion is not null)
            {
                Assertion = Assertion with { OutputIdentity = outputIdentity };
            }

            _state = AdmissionState.Running;
        }
    }

    /// <summary>Irreversibly removes claim eligibility before cancellation/stop requests.</summary>
    internal void LatchFailure()
    {
        lock (_sync)
        {
            _terminalFailure = true;
        }
    }

    /// <summary>Records stopped work and completed collection/cleanup, or makes the run ineligible.</summary>
    internal void Complete(bool ownedWorkStopped, bool artifactsVerified, bool cleanupCompleted)
    {
        lock (_sync)
        {
            if (_state != AdmissionState.Running || !ownedWorkStopped)
            {
                throw new EvidenceAdmissionException("ASEVD410", "Owned work has not stopped; finalization is forbidden.");
            }

            _terminalFailure |= !artifactsVerified || !cleanupCompleted;
            _state = AdmissionState.Completed;
        }
    }

    /// <summary>Consumes the completed capability once, comparing the entire plan to its admission snapshot.</summary>
    internal bool Consume(EvidencePlan plan)
    {
        lock (_sync)
        {
            if (_state != AdmissionState.Completed
                || !EvidenceCanonicalJson.Serialize(_snapshot).AsSpan().SequenceEqual(EvidenceCanonicalJson.Serialize(plan)))
            {
                throw new EvidenceAdmissionException("ASEVD411", "A completed admission for this exact plan is required.");
            }

            _state = AdmissionState.Consumed;
            return !_terminalFailure;
        }
    }

    private enum AdmissionState { Admitted, Running, Completed, Consumed }
}

/// <summary>Reports a bounded admission failure without echoing supplied values or exception text.</summary>
public sealed class EvidenceAdmissionException : InvalidOperationException
{
    /// <summary>Creates a stable failure with the common migration/recovery reference.</summary>
    /// <param name="code">Stable host-selected diagnostic code.</param>
    /// <param name="problem">Host-selected description containing no supplied values.</param>
    internal EvidenceAdmissionException(string code, string problem)
        : base($"{code}: {problem} Fix: use an explicit mode and supported protected worker. See start-here/evidencehost.md.") => Code = code;

    /// <summary>Gets the stable diagnostic code.</summary>
    public string Code { get; }
}

/// <summary>Protected declarations captured before callback invocation; never constructed from subject discovery.</summary>
/// <remarks>Provisional internal integration shape pending the complete consumer proof. Each catalogue entry is an exact declaration.</remarks>
internal sealed record EvidenceAdmissionContext(
    string RunId,
    EvidencePolicy Policy,
    IReadOnlyList<NormalizedDiffPath> Diff,
    EvidencePlan ExpectedPlan,
    IReadOnlyList<EvidenceProducerDeclaration> Producers,
    IReadOnlyList<EvidenceResourceDeclaration> Resources,
    IReadOnlyList<string> ObservationProfiles,
    IReadOnlyList<string> ObservationProducerIds,
    IReadOnlyList<string> SensitiveProjectionKeys,
    bool ProtectedSecretsPresent,
    bool ConsumerAcceptanceMatches,
    EvidenceEnvelopeAssertion? ExpectedAssertion,
    IReadOnlyList<EvidenceObservationProducerClass>? ObservationProducerClasses = null);

/// <summary>A protected registration's dependency-free Observation capability class and exact version.</summary>
internal sealed record EvidenceObservationProducerClass(string Kind, string Version);

/// <summary>Lease facts supplied by the independent protected launcher, never by an environment Boolean.</summary>
internal interface IEvidenceArmedWorker
{
    /// <summary>Gets whether the independent watchdog is armed and its worker is still usable.</summary>
    bool IsArmed { get; }
    /// <summary>Gets the exact run/attempt identity.</summary>
    string RunId { get; }
}

/// <summary>Registered protected verifier callback; subject code is never loaded into this callback's process.</summary>
internal interface IEvidenceAdmissionVerifier
{
    /// <summary>Accepts the bounded protected facts or returns null; raw diagnostic values are forbidden.</summary>
    ValueTask<EvidenceEnvelopeAssertion?> VerifyAsync(EvidencePlan plan, CancellationToken cancellationToken);
}

/// <summary>The one pure admission rule and registered-verifier invocation shared by execution entries.</summary>
/// <remarks>The owning lifecycle must arm its independent watchdog before invoking this boundary and track/join its task.</remarks>
internal static class EvidenceAdmission
{
    /// <summary>Validates provenance, catalogue and mode, invoking exactly one verifier only for Trusted.</summary>
    internal static async ValueTask<EvidenceAdmissionResult> AdmitAsync(
        EvidenceExecutionMode mode,
        EvidencePlan plan,
        EvidenceAdmissionContext context,
        IEvidenceArmedWorker worker,
        IEvidenceAdmissionVerifier? verifier,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(worker);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(mode))
        {
            throw new EvidenceAdmissionException("ASEVD401", "Select Trusted or Observation explicitly.");
        }

        if (!worker.IsArmed || string.IsNullOrWhiteSpace(context.RunId) || worker.RunId != context.RunId)
        {
            throw new EvidenceAdmissionException("ASEVD402", "Matching independent supervision is not armed.");
        }

        // Defensive canonical round trips separate the capability from all mutable caller-owned collections.
        var snapshot = EvidenceCanonicalJson.Deserialize<EvidencePlan>(EvidenceCanonicalJson.Serialize(plan));
        if (snapshot.PolicySnapshot is null
            || snapshot.PolicyDigest != EvidenceDigest.CanonicalSha256(context.Policy)
            || snapshot.PolicyDigest != EvidenceDigest.CanonicalSha256(snapshot.PolicySnapshot)
            || snapshot.DiffDigest != EvidenceDigest.CanonicalSha256(context.Diff)
            || snapshot.DiffDigest != EvidenceDigest.CanonicalSha256(snapshot.ChangedPaths)
            || snapshot.PlanDigest != EvidenceDigest.CanonicalSha256(snapshot with { PlanDigest = string.Empty })
            || !Same(snapshot, context.ExpectedPlan))
        {
            throw new EvidenceAdmissionException("ASEVD403", "Protected policy, diff or resolved plan binding failed.");
        }

        if (!Unique(context.Producers.Select(static item => item.Id))
            || !Unique(context.Resources.Select(static item => item.Id))
            || !Same(snapshot.Profile.Producers.OrderBy(static item => item.Id, StringComparer.Ordinal).ToArray(),
                context.Producers.OrderBy(static item => item.Id, StringComparer.Ordinal).ToArray())
            || !Same(snapshot.Profile.Resources.OrderBy(static item => item.Id, StringComparer.Ordinal).ToArray(),
                context.Resources.OrderBy(static item => item.Id, StringComparer.Ordinal).ToArray()))
        {
            throw new EvidenceAdmissionException("ASEVD404", "Protected declaration catalogue does not match.");
        }

        if (context.ProtectedSecretsPresent || context.SensitiveProjectionKeys.Count != 0)
        {
            throw new EvidenceAdmissionException("ASEVD405", "Protected secrets and sensitive projections are unsupported.");
        }

        if (mode == EvidenceExecutionMode.Observation)
        {
            if (snapshot.Profile.Scope != EvidenceProfileScope.Targeted
                || snapshot.Profile.Resources.Count != 0
                || !context.ObservationProfiles.Contains(snapshot.Profile.Id, StringComparer.Ordinal)
                || snapshot.Profile.Producers.Any(producer => producer.RequiredResources.Count != 0
                    || !context.ObservationProducerIds.Contains(producer.Id, StringComparer.Ordinal)
                    || context.ObservationProducerClasses?.Any(capability => capability.Kind == producer.Kind
                        && capability.Version == producer.Version) != true))
            {
                throw new EvidenceAdmissionException("ASEVD406", "Observation requires a declared dependency-free profile.");
            }

            return new EvidenceAdmissionResult(snapshot, mode, context.RunId, null);
        }

        if (!context.ConsumerAcceptanceMatches || verifier is null || !ValidAssertion(context.ExpectedAssertion, context.RunId))
        {
            throw new EvidenceAdmissionException("ASEVD407", "Trusted consumer acceptance or registered verifier is unavailable.");
        }

        var assertion = await verifier.VerifyAsync(snapshot, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!worker.IsArmed || assertion is null || !Same(assertion, context.ExpectedAssertion))
        {
            throw new EvidenceAdmissionException("ASEVD408", "Registered verifier rejected the protected execution facts.");
        }

        return new EvidenceAdmissionResult(snapshot, mode, context.RunId, assertion);
    }

    /// <summary>Checks closed assertion schema and required identities; output identity is bound only at activation.</summary>
    internal static bool ValidAssertion(EvidenceEnvelopeAssertion? assertion, string runId) =>
        assertion is not null && assertion.SchemaVersion == "1.0" && assertion.RunId == runId
        && !string.IsNullOrWhiteSpace(runId) && runId.Length <= 256 && !runId.Any(char.IsControl)
        && new[] { assertion.VerifierId, assertion.VerifierVersion, assertion.Provider, assertion.WorkflowIdentity,
            assertion.BaseRevision, assertion.SubjectRevision, assertion.ToolRootIdentity, assertion.SubjectRootIdentity,
            assertion.OutputParentIdentity }.All(static value => !string.IsNullOrWhiteSpace(value) && value.Length <= 256)
        && assertion.ToolRootIdentity != assertion.SubjectRootIdentity
        && assertion.ToolRootIdentity != assertion.OutputParentIdentity
        && assertion.SubjectRootIdentity != assertion.OutputParentIdentity
        && new[] { assertion.AllocationPolicyDigest, assertion.CatalogueDigest, assertion.CapabilitiesDigest,
            assertion.AcceptanceProofDigest }.All(static value => value is { Length: 64 } && value.All(char.IsAsciiHexDigit))
        && assertion.VerifiedAtUtc != default;

    private static bool Unique(IEnumerable<string> ids)
    {
        var values = ids.ToArray();
        return values.All(static value => !string.IsNullOrWhiteSpace(value))
            && values.Distinct(StringComparer.Ordinal).Count() == values.Length;
    }

    private static bool Same<T>(T left, T right) =>
        EvidenceCanonicalJson.Serialize(left).AsSpan().SequenceEqual(EvidenceCanonicalJson.Serialize(right));
}
