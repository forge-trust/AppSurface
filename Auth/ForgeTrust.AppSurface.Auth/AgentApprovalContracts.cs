namespace ForgeTrust.AppSurface.Auth;

/// <summary>
/// Classifies the host-declared risk of an action an agent proposes.
/// </summary>
/// <remarks>
/// Risk is descriptive metadata for host policy and user-facing confirmation. It is not permission truth and does not
/// allow an agent to execute an action.
/// </remarks>
public enum AgentActionRisk
{
    /// <summary>
    /// The host considers the action low risk.
    /// </summary>
    Low = 0,

    /// <summary>
    /// The host considers the action consequential and may require additional review.
    /// </summary>
    Elevated = 1,

    /// <summary>
    /// The host considers the action high risk.
    /// </summary>
    High = 2,
}

/// <summary>
/// States the confirmation posture declared for an action.
/// </summary>
/// <remarks>
/// The posture is an input to host policy. A host can require confirmation for any action, and only a host-issued
/// <see cref="AgentAuthorizationDecision"/> determines whether the requested action may proceed.
/// </remarks>
public enum AgentConfirmationPosture
{
    /// <summary>
    /// The host chooses whether the action requires confirmation.
    /// </summary>
    HostDetermines = 0,

    /// <summary>
    /// The action declares that a human confirmation should be required.
    /// </summary>
    AlwaysRequireHuman = 1,
}

/// <summary>
/// States how a host should treat action arguments in confirmation, audit, and diagnostic displays.
/// </summary>
public enum AgentActionRedaction
{
    /// <summary>
    /// Hosts may display only the action's supplied safe summary.
    /// </summary>
    SafeSummaryOnly = 0,

    /// <summary>
    /// Hosts must apply their own redaction policy before displaying any additional action detail.
    /// </summary>
    RequireHostRedaction = 1,

    /// <summary>
    /// Hosts must not expose raw action arguments in confirmation, audit, or diagnostic displays.
    /// </summary>
    DoNotExposeArguments = 2,
}

/// <summary>
/// Defines the outcome of host evaluation for an agent action request.
/// </summary>
public enum AgentAuthorizationDecisionKind
{
    /// <summary>
    /// The host allows the proposed action under its current policy.
    /// </summary>
    Allowed = 0,

    /// <summary>
    /// The host denies the proposed action.
    /// </summary>
    Denied = 1,

    /// <summary>
    /// The host requires an explicit human confirmation before execution.
    /// </summary>
    ConfirmationRequired = 2,
}

/// <summary>
/// Defines terminal outcomes when a host attempts to consume an approval receipt.
/// </summary>
/// <remarks>
/// Hosts own receipt storage and atomicity. A host must return exactly one terminal outcome for a consumption attempt
/// and must not retry execution after <see cref="AlreadyConsumed"/>.
/// </remarks>
public enum AgentApprovalConsumptionOutcome
{
    /// <summary>
    /// The host atomically consumed the receipt and may execute the bound action once.
    /// </summary>
    Consumed = 0,

    /// <summary>
    /// Another attempt already consumed the receipt.
    /// </summary>
    AlreadyConsumed = 1,

    /// <summary>
    /// The receipt expired before it could be consumed.
    /// </summary>
    Expired = 2,

    /// <summary>
    /// The host revoked the receipt before it could be consumed.
    /// </summary>
    Revoked = 3,

    /// <summary>
    /// The expected workflow state or concurrency version no longer matches.
    /// </summary>
    Stale = 4,

    /// <summary>
    /// The host could not reproduce the receipt's bound action representation.
    /// </summary>
    BindingMismatch = 5,

    /// <summary>
    /// A required current human authority or agent grant is absent.
    /// </summary>
    Denied = 6,
}

/// <summary>
/// Identifies a passive delegated-agent authorization audit description.
/// </summary>
/// <remarks>
/// This enum describes lifecycle events only. AppSurface does not write logs, metrics, traces, or persisted audit
/// records; hosts own transport, retention, redaction, access control, and failure handling.
/// </remarks>
public enum AgentAuthorizationAuditEventKind
{
    /// <summary>
    /// An agent proposed an action.
    /// </summary>
    Proposed = 0,

    /// <summary>
    /// The host allowed the proposed action.
    /// </summary>
    Allowed = 1,

    /// <summary>
    /// The host denied the proposed action.
    /// </summary>
    Denied = 2,

    /// <summary>
    /// The host requested an explicit human confirmation.
    /// </summary>
    ConfirmationRequired = 3,

    /// <summary>
    /// A human approved an exact bound action.
    /// </summary>
    Approved = 4,

    /// <summary>
    /// The host revoked an unconsumed approval receipt.
    /// </summary>
    Revoked = 5,

    /// <summary>
    /// A host attempted to consume an approval receipt.
    /// </summary>
    ConsumptionAttempted = 6,

    /// <summary>
    /// A host consumed an approval receipt.
    /// </summary>
    Consumed = 7,

    /// <summary>
    /// A host rejected a replayed receipt.
    /// </summary>
    AlreadyConsumed = 8,

    /// <summary>
    /// A host rejected an expired receipt.
    /// </summary>
    Expired = 9,

    /// <summary>
    /// A host rejected a request whose workflow state changed.
    /// </summary>
    Stale = 10,

    /// <summary>
    /// A host rejected a request whose binding no longer matched.
    /// </summary>
    BindingMismatch = 11,

    /// <summary>
    /// A host rejected receipt consumption after current-authority or narrow-grant checks.
    /// </summary>
    ConsumptionDenied = 12,
}

/// <summary>
/// Defines stable diagnostic codes for the delegated-agent authorization lifecycle.
/// </summary>
/// <remarks>
/// Hosts may add a stable subcode to the canonical family for the typed outcome, such as
/// <c>agent-approval.consumption-denied.grant-missing</c>. Do not use display messages as machine-readable branching
/// values, and do not renumber public outcome enums after release.
/// </remarks>
public static class AgentApprovalDiagnosticCodes
{
    /// <summary>
    /// Indicates that an agent proposed an action.
    /// </summary>
    public const string Proposed = "agent-approval.proposed";

    /// <summary>
    /// Indicates that host evaluation allowed the requested action.
    /// </summary>
    public const string Allowed = "agent-approval.allowed";

    /// <summary>
    /// Indicates that host evaluation denied the requested action.
    /// </summary>
    public const string Denied = "agent-approval.denied";

    /// <summary>
    /// Indicates that host evaluation requires an explicit human confirmation.
    /// </summary>
    public const string ConfirmationRequired = "agent-approval.confirmation-required";

    /// <summary>
    /// Indicates that a host attempted to consume a receipt.
    /// </summary>
    public const string ConsumptionAttempted = "agent-approval.consumption-attempted";

    /// <summary>
    /// Indicates that a human approved the requested action.
    /// </summary>
    public const string Approved = "agent-approval.approved";

    /// <summary>
    /// Indicates that a receipt was consumed and the bound action may execute once.
    /// </summary>
    public const string Consumed = "agent-approval.consumed";

    /// <summary>
    /// Indicates that a receipt was already consumed.
    /// </summary>
    public const string AlreadyConsumed = "agent-approval.already-consumed";

    /// <summary>
    /// Indicates that a receipt expired.
    /// </summary>
    public const string Expired = "agent-approval.expired";

    /// <summary>
    /// Indicates that a receipt was revoked.
    /// </summary>
    public const string Revoked = "agent-approval.revoked";

    /// <summary>
    /// Indicates that the expected workflow state or version changed.
    /// </summary>
    public const string Stale = "agent-approval.stale";

    /// <summary>
    /// Indicates that a request no longer matches the receipt's binding.
    /// </summary>
    public const string BindingMismatch = "agent-approval.binding-mismatch";

    /// <summary>
    /// Indicates that a host denied a receipt consumption after current-state checks.
    /// </summary>
    public const string ConsumptionDenied = "agent-approval.consumption-denied";
}

internal static class AgentApprovalContractValidation
{
    private const int MaximumDisplayTextLength = 4096;
    private const int MaximumMetadataEntries = 32;
    private const int MaximumMetadataKeyLength = 128;
    private const int MaximumMetadataValueLength = 1024;
    private const int MaximumMetadataCharacterCount = 16384;

    public static string RequireCode(string value, string parameterName)
    {
        var code = AppSurfaceAuthMetadata.RequireIdentifier(value, parameterName);
        if (code.Any(char.IsWhiteSpace) || code.Any(char.IsControl))
        {
            throw new ArgumentException("Diagnostic codes must not contain whitespace or control characters.", parameterName);
        }

        return code;
    }

    public static string RequireSafeDisplayText(string value, string parameterName)
    {
        var text = AppSurfaceAuthMetadata.RequireIdentifier(value, parameterName);
        EnsureNoControlCharacters(text, parameterName);
        return text;
    }

    public static string? NormalizeSafeDisplayText(string? value, string parameterName)
    {
        var normalized = AppSurfaceAuthMetadata.NormalizeOptionalText(value);
        if (normalized is not null)
        {
            EnsureNoControlCharacters(normalized, parameterName);
        }

        return normalized;
    }

    public static IReadOnlyDictionary<string, string> NormalizeMetadata(
        IReadOnlyDictionary<string, string>? metadata,
        string parameterName)
    {
        if (metadata is not null && metadata.Count > MaximumMetadataEntries)
        {
            throw new ArgumentException($"Metadata must contain at most {MaximumMetadataEntries} entries.", parameterName);
        }

        var normalized = AppSurfaceAuthMetadata.Normalize(metadata, parameterName);
        var characterCount = 0;
        foreach (var item in normalized)
        {
            EnsureNoControlCharacters(item.Key, parameterName);
            EnsureNoControlCharacters(item.Value, parameterName);
            if (item.Key.Length > MaximumMetadataKeyLength || item.Value.Length > MaximumMetadataValueLength)
            {
                throw new ArgumentException(
                    $"Metadata keys must contain at most {MaximumMetadataKeyLength} characters and values at most {MaximumMetadataValueLength} characters.",
                    parameterName);
            }

            characterCount += item.Key.Length + item.Value.Length;
            if (characterCount > MaximumMetadataCharacterCount)
            {
                throw new ArgumentException(
                    $"Metadata must contain at most {MaximumMetadataCharacterCount} characters in total.",
                    parameterName);
            }
        }

        return normalized;
    }

    public static void ValidateDecisionCode(
        AgentAuthorizationDecisionKind kind,
        string code,
        string parameterName)
    {
        var expected = kind switch
        {
            AgentAuthorizationDecisionKind.Allowed => AgentApprovalDiagnosticCodes.Allowed,
            AgentAuthorizationDecisionKind.Denied => AgentApprovalDiagnosticCodes.Denied,
            AgentAuthorizationDecisionKind.ConfirmationRequired => AgentApprovalDiagnosticCodes.ConfirmationRequired,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        ValidateCodeFamily(code, expected, parameterName);
    }

    public static void ValidateConsumptionCode(
        AgentApprovalConsumptionOutcome outcome,
        string code,
        string parameterName)
    {
        var expected = outcome switch
        {
            AgentApprovalConsumptionOutcome.Consumed => AgentApprovalDiagnosticCodes.Consumed,
            AgentApprovalConsumptionOutcome.AlreadyConsumed => AgentApprovalDiagnosticCodes.AlreadyConsumed,
            AgentApprovalConsumptionOutcome.Expired => AgentApprovalDiagnosticCodes.Expired,
            AgentApprovalConsumptionOutcome.Revoked => AgentApprovalDiagnosticCodes.Revoked,
            AgentApprovalConsumptionOutcome.Stale => AgentApprovalDiagnosticCodes.Stale,
            AgentApprovalConsumptionOutcome.BindingMismatch => AgentApprovalDiagnosticCodes.BindingMismatch,
            AgentApprovalConsumptionOutcome.Denied => AgentApprovalDiagnosticCodes.ConsumptionDenied,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };

        ValidateCodeFamily(code, expected, parameterName);
    }

    public static void ValidateAuditCode(
        AgentAuthorizationAuditEventKind kind,
        string code,
        string parameterName)
    {
        var expected = kind switch
        {
            AgentAuthorizationAuditEventKind.Proposed => AgentApprovalDiagnosticCodes.Proposed,
            AgentAuthorizationAuditEventKind.Allowed => AgentApprovalDiagnosticCodes.Allowed,
            AgentAuthorizationAuditEventKind.Denied => AgentApprovalDiagnosticCodes.Denied,
            AgentAuthorizationAuditEventKind.ConfirmationRequired => AgentApprovalDiagnosticCodes.ConfirmationRequired,
            AgentAuthorizationAuditEventKind.Approved => AgentApprovalDiagnosticCodes.Approved,
            AgentAuthorizationAuditEventKind.Revoked => AgentApprovalDiagnosticCodes.Revoked,
            AgentAuthorizationAuditEventKind.ConsumptionAttempted => AgentApprovalDiagnosticCodes.ConsumptionAttempted,
            AgentAuthorizationAuditEventKind.Consumed => AgentApprovalDiagnosticCodes.Consumed,
            AgentAuthorizationAuditEventKind.AlreadyConsumed => AgentApprovalDiagnosticCodes.AlreadyConsumed,
            AgentAuthorizationAuditEventKind.Expired => AgentApprovalDiagnosticCodes.Expired,
            AgentAuthorizationAuditEventKind.Stale => AgentApprovalDiagnosticCodes.Stale,
            AgentAuthorizationAuditEventKind.BindingMismatch => AgentApprovalDiagnosticCodes.BindingMismatch,
            AgentAuthorizationAuditEventKind.ConsumptionDenied => AgentApprovalDiagnosticCodes.ConsumptionDenied,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        ValidateCodeFamily(code, expected, parameterName);
    }

    public static bool RequiresReceipt(AgentAuthorizationAuditEventKind kind)
    {
        return kind is AgentAuthorizationAuditEventKind.Revoked
            or AgentAuthorizationAuditEventKind.ConsumptionAttempted
            or AgentAuthorizationAuditEventKind.Consumed
            or AgentAuthorizationAuditEventKind.AlreadyConsumed
            or AgentAuthorizationAuditEventKind.Expired
            or AgentAuthorizationAuditEventKind.Stale
            or AgentAuthorizationAuditEventKind.BindingMismatch
            or AgentAuthorizationAuditEventKind.ConsumptionDenied;
    }

    private static void ValidateCodeFamily(string code, string expected, string parameterName)
    {
        var normalized = RequireCode(code, parameterName);
        if (string.Equals(normalized, expected, StringComparison.Ordinal))
        {
            return;
        }

        var prefix = expected + ".";
        if (!normalized.StartsWith(prefix, StringComparison.Ordinal)
            || normalized.Length == prefix.Length
            || normalized.Split('.').Any(string.IsNullOrEmpty))
        {
            throw new ArgumentException($"Diagnostic code must be '{expected}' or a stable subcode in that family.", parameterName);
        }
    }

    private static void EnsureNoControlCharacters(string value, string parameterName)
    {
        if (value.Any(character => char.IsControl(character)
                || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format)
            || value.Length > MaximumDisplayTextLength)
        {
            throw new ArgumentException(
                $"Display-safe values must contain at most {MaximumDisplayTextLength} characters and no control or Unicode format characters.",
                parameterName);
        }
    }
}

/// <summary>
/// Identifies a stable host-local agent or harness identity without exposing credentials.
/// </summary>
/// <remarks>
/// The value belongs to the host's agent namespace. It is not an application-user id, a user permission, a bearer
/// token, or evidence that the agent may act. <see cref="ToString"/> redacts the raw value by default.
/// </remarks>
public readonly struct AgentIdentityReference : IEquatable<AgentIdentityReference>
{
    /// <summary>
    /// Creates an agent identity reference.
    /// </summary>
    /// <param name="value">Stable host-local agent or harness identity. The value must be non-empty.</param>
    public AgentIdentityReference(string value)
    {
        Value = AppSurfaceAuthMetadata.RequireIdentifier(value, nameof(value));
    }

    /// <summary>
    /// Gets the stable host-local agent or harness identity.
    /// </summary>
    public string Value { get; }

    /// <inheritdoc />
    public bool Equals(AgentIdentityReference other)
    {
        return string.Equals(Value, other.Value, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is AgentIdentityReference other && Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return Value is null ? 0 : StringComparer.Ordinal.GetHashCode(Value);
    }

    /// <summary>
    /// Compares two agent identity references with ordinal semantics.
    /// </summary>
    public static bool operator ==(AgentIdentityReference left, AgentIdentityReference right)
    {
        return left.Equals(right);
    }

    /// <summary>
    /// Compares two agent identity references with ordinal semantics.
    /// </summary>
    public static bool operator !=(AgentIdentityReference left, AgentIdentityReference right)
    {
        return !left.Equals(right);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return "AgentIdentityReference { Value = <redacted> }";
    }

    internal void EnsureInitialized(string parameterName)
    {
        _ = AppSurfaceAuthMetadata.RequireIdentifier(Value, parameterName);
    }
}

/// <summary>
/// Identifies the human or host authority that approved an exact action without prescribing an identity provider.
/// </summary>
/// <remarks>
/// A host can derive this reference from <see cref="ExternalSubject"/>, an app-owned identity, or another validated
/// subject namespace. The reference is not an agent grant or a reusable approval credential. <see cref="ToString"/>
/// redacts the raw value by default.
/// </remarks>
public readonly struct AgentApproverReference : IEquatable<AgentApproverReference>
{
    /// <summary>
    /// Creates an approver reference.
    /// </summary>
    /// <param name="value">Stable host-local approver identity. The value must be non-empty.</param>
    public AgentApproverReference(string value)
    {
        Value = AppSurfaceAuthMetadata.RequireIdentifier(value, nameof(value));
    }

    /// <summary>
    /// Gets the stable host-local approver identity.
    /// </summary>
    public string Value { get; }

    /// <inheritdoc />
    public bool Equals(AgentApproverReference other)
    {
        return string.Equals(Value, other.Value, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is AgentApproverReference other && Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return Value is null ? 0 : StringComparer.Ordinal.GetHashCode(Value);
    }

    /// <summary>
    /// Compares two approver references with ordinal semantics.
    /// </summary>
    public static bool operator ==(AgentApproverReference left, AgentApproverReference right)
    {
        return left.Equals(right);
    }

    /// <summary>
    /// Compares two approver references with ordinal semantics.
    /// </summary>
    public static bool operator !=(AgentApproverReference left, AgentApproverReference right)
    {
        return !left.Equals(right);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return "AgentApproverReference { Value = <redacted> }";
    }

    internal void EnsureInitialized(string parameterName)
    {
        _ = AppSurfaceAuthMetadata.RequireIdentifier(Value, parameterName);
    }
}

/// <summary>
/// Describes a host-normalized workflow transition that an approval receipt binds.
/// </summary>
/// <remarks>
/// The binding profile and digest are opaque host values. Hosts must use the same profile, normalisation rules, and
/// safe digest representation when issuing and consuming a receipt. This type does not prescribe canonicalisation,
/// cryptography, persistence, or a workflow runtime. <see cref="ToString"/> intentionally redacts binding values.
/// </remarks>
public sealed class AgentActionBinding
{
    /// <summary>
    /// Creates an action binding.
    /// </summary>
    /// <param name="actionId">Stable action identifier from host-controlled action metadata.</param>
    /// <param name="taskId">Host task or harness run identifier.</param>
    /// <param name="workflowInstanceId">Host workflow instance identifier.</param>
    /// <param name="expectedState">Expected current workflow state.</param>
    /// <param name="expectedStateVersion">Expected state version or concurrency stamp.</param>
    /// <param name="transition">Requested transition or decision.</param>
    /// <param name="bindingProfile">Host-defined canonicalisation profile and version.</param>
    /// <param name="safeIntentDigest">Host-derived safe digest of the requested intent.</param>
    public AgentActionBinding(
        string actionId,
        string taskId,
        string workflowInstanceId,
        string expectedState,
        string expectedStateVersion,
        string transition,
        string bindingProfile,
        string safeIntentDigest)
    {
        ActionId = AppSurfaceAuthMetadata.RequireIdentifier(actionId, nameof(actionId));
        TaskId = AppSurfaceAuthMetadata.RequireIdentifier(taskId, nameof(taskId));
        WorkflowInstanceId = AppSurfaceAuthMetadata.RequireIdentifier(workflowInstanceId, nameof(workflowInstanceId));
        ExpectedState = AppSurfaceAuthMetadata.RequireIdentifier(expectedState, nameof(expectedState));
        ExpectedStateVersion = AppSurfaceAuthMetadata.RequireIdentifier(expectedStateVersion, nameof(expectedStateVersion));
        Transition = AppSurfaceAuthMetadata.RequireIdentifier(transition, nameof(transition));
        BindingProfile = AppSurfaceAuthMetadata.RequireIdentifier(bindingProfile, nameof(bindingProfile));
        SafeIntentDigest = AppSurfaceAuthMetadata.RequireIdentifier(safeIntentDigest, nameof(safeIntentDigest));
    }

    /// <summary>
    /// Gets the stable action identifier.
    /// </summary>
    public string ActionId { get; }

    /// <summary>
    /// Gets the host task or harness run identifier.
    /// </summary>
    public string TaskId { get; }

    /// <summary>
    /// Gets the host workflow instance identifier.
    /// </summary>
    public string WorkflowInstanceId { get; }

    /// <summary>
    /// Gets the expected current workflow state.
    /// </summary>
    public string ExpectedState { get; }

    /// <summary>
    /// Gets the expected state version or concurrency stamp.
    /// </summary>
    public string ExpectedStateVersion { get; }

    /// <summary>
    /// Gets the requested transition or decision.
    /// </summary>
    public string Transition { get; }

    /// <summary>
    /// Gets the host-defined canonicalisation profile and version.
    /// </summary>
    public string BindingProfile { get; }

    /// <summary>
    /// Gets the host-derived safe intent digest.
    /// </summary>
    public string SafeIntentDigest { get; }

    /// <summary>
    /// Determines whether another binding has the same ordinal action, workflow, state, transition, profile, and digest values.
    /// </summary>
    /// <param name="other">The binding to compare.</param>
    /// <returns><see langword="true"/> when every approval-relevant binding field matches.</returns>
    public bool Matches(AgentActionBinding? other)
    {
        return other is not null
            && string.Equals(ActionId, other.ActionId, StringComparison.Ordinal)
            && string.Equals(TaskId, other.TaskId, StringComparison.Ordinal)
            && string.Equals(WorkflowInstanceId, other.WorkflowInstanceId, StringComparison.Ordinal)
            && string.Equals(ExpectedState, other.ExpectedState, StringComparison.Ordinal)
            && string.Equals(ExpectedStateVersion, other.ExpectedStateVersion, StringComparison.Ordinal)
            && string.Equals(Transition, other.Transition, StringComparison.Ordinal)
            && string.Equals(BindingProfile, other.BindingProfile, StringComparison.Ordinal)
            && string.Equals(SafeIntentDigest, other.SafeIntentDigest, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return "AgentActionBinding { ActionId = <redacted>, TaskId = <redacted>, WorkflowInstanceId = <redacted>, ExpectedState = <redacted>, ExpectedStateVersion = <redacted>, Transition = <redacted>, BindingProfile = <redacted>, SafeIntentDigest = <redacted> }";
    }

    internal void EnsureInitialized(string parameterName)
    {
        _ = AppSurfaceAuthMetadata.RequireIdentifier(ActionId, parameterName);
        _ = AppSurfaceAuthMetadata.RequireIdentifier(TaskId, parameterName);
        _ = AppSurfaceAuthMetadata.RequireIdentifier(WorkflowInstanceId, parameterName);
        _ = AppSurfaceAuthMetadata.RequireIdentifier(ExpectedState, parameterName);
        _ = AppSurfaceAuthMetadata.RequireIdentifier(ExpectedStateVersion, parameterName);
        _ = AppSurfaceAuthMetadata.RequireIdentifier(Transition, parameterName);
        _ = AppSurfaceAuthMetadata.RequireIdentifier(BindingProfile, parameterName);
        _ = AppSurfaceAuthMetadata.RequireIdentifier(SafeIntentDigest, parameterName);
    }
}

/// <summary>
/// Declares safe, host-controlled metadata for an action an agent can propose.
/// </summary>
/// <remarks>
/// This metadata aids host policy and confirmation presentation. It does not classify untrusted agent input, grant
/// authority, or replace a host's policy evaluation.
/// </remarks>
public sealed class AgentActionMetadata
{
    /// <summary>
    /// Creates action metadata.
    /// </summary>
    /// <param name="actionId">Stable action identifier.</param>
    /// <param name="displayName">Host-controlled display name.</param>
    /// <param name="risk">Host-declared risk classification.</param>
    /// <param name="confirmationPosture">Declared confirmation posture for host policy.</param>
    /// <param name="redaction">Guidance for confirmation and diagnostic displays.</param>
    /// <param name="metadata">Optional display-safe host metadata copied with ordinal keys.</param>
    public AgentActionMetadata(
        string actionId,
        string displayName,
        AgentActionRisk risk = AgentActionRisk.Elevated,
        AgentConfirmationPosture confirmationPosture = AgentConfirmationPosture.HostDetermines,
        AgentActionRedaction redaction = AgentActionRedaction.SafeSummaryOnly,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        if (!Enum.IsDefined(risk))
        {
            throw new ArgumentOutOfRangeException(nameof(risk));
        }

        if (!Enum.IsDefined(confirmationPosture))
        {
            throw new ArgumentOutOfRangeException(nameof(confirmationPosture));
        }

        if (!Enum.IsDefined(redaction))
        {
            throw new ArgumentOutOfRangeException(nameof(redaction));
        }

        ActionId = AppSurfaceAuthMetadata.RequireIdentifier(actionId, nameof(actionId));
        DisplayName = AgentApprovalContractValidation.RequireSafeDisplayText(displayName, nameof(displayName));
        Risk = risk;
        ConfirmationPosture = confirmationPosture;
        Redaction = redaction;
        Metadata = AgentApprovalContractValidation.NormalizeMetadata(metadata, nameof(metadata));
    }

    /// <summary>
    /// Gets the stable action identifier.
    /// </summary>
    public string ActionId { get; }

    /// <summary>
    /// Gets the host-controlled display name.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the host-declared risk classification.
    /// </summary>
    public AgentActionRisk Risk { get; }

    /// <summary>
    /// Gets the declared confirmation posture.
    /// </summary>
    public AgentConfirmationPosture ConfirmationPosture { get; }

    /// <summary>
    /// Gets display redaction guidance.
    /// </summary>
    public AgentActionRedaction Redaction { get; }

    /// <summary>
    /// Gets copied display-safe host metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }

    internal void EnsureInitialized(string parameterName)
    {
        _ = AppSurfaceAuthMetadata.RequireIdentifier(ActionId, parameterName);
        _ = AppSurfaceAuthMetadata.RequireIdentifier(DisplayName, parameterName);
        if (!Enum.IsDefined(Risk)
            || !Enum.IsDefined(ConfirmationPosture)
            || !Enum.IsDefined(Redaction))
        {
            throw new ArgumentException("Action metadata contains an undefined enum value.", parameterName);
        }
    }
}

/// <summary>
/// Describes one action an agent proposes to a host.
/// </summary>
/// <remarks>
/// The request is immutable and contains only host-safe display fields. It does not carry user credentials, bearer
/// tokens, an agent grant, an approval receipt, or permission to execute the action.
/// </remarks>
public sealed class AgentActionRequest
{
    /// <summary>
    /// Creates an agent action request.
    /// </summary>
    /// <param name="action">Host-controlled action metadata.</param>
    /// <param name="binding">Host-normalized transition binding.</param>
    /// <param name="agent">Host-local agent or harness reference.</param>
    /// <param name="correlationId">Host-generated correlation identifier.</param>
    /// <param name="requestedAt">Timestamp supplied by the host.</param>
    /// <param name="safeSummary">Display-safe description of the proposed action.</param>
    /// <param name="rationale">Optional display-safe rationale for the proposal.</param>
    /// <param name="metadata">Optional display-safe host metadata copied with ordinal keys.</param>
    public AgentActionRequest(
        AgentActionMetadata action,
        AgentActionBinding binding,
        AgentIdentityReference agent,
        string correlationId,
        DateTimeOffset requestedAt,
        string safeSummary,
        string? rationale = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(binding);
        action.EnsureInitialized(nameof(action));
        binding.EnsureInitialized(nameof(binding));
        agent.EnsureInitialized(nameof(agent));
        if (!string.Equals(action.ActionId, binding.ActionId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Action metadata and binding action identifiers must match.", nameof(binding));
        }

        Action = action;
        Binding = binding;
        Agent = agent;
        CorrelationId = AppSurfaceAuthMetadata.RequireIdentifier(correlationId, nameof(correlationId));
        RequestedAt = requestedAt;
        SafeSummary = AgentApprovalContractValidation.RequireSafeDisplayText(safeSummary, nameof(safeSummary));
        Rationale = AgentApprovalContractValidation.NormalizeSafeDisplayText(rationale, nameof(rationale));
        Metadata = AgentApprovalContractValidation.NormalizeMetadata(metadata, nameof(metadata));
    }

    /// <summary>
    /// Gets host-controlled action metadata.
    /// </summary>
    public AgentActionMetadata Action { get; }

    /// <summary>
    /// Gets the bound workflow transition.
    /// </summary>
    public AgentActionBinding Binding { get; }

    /// <summary>
    /// Gets the proposing agent or local harness reference.
    /// </summary>
    public AgentIdentityReference Agent { get; }

    /// <summary>
    /// Gets the host-generated correlation identifier.
    /// </summary>
    public string CorrelationId { get; }

    /// <summary>
    /// Gets the host-supplied request timestamp.
    /// </summary>
    public DateTimeOffset RequestedAt { get; }

    /// <summary>
    /// Gets the display-safe action summary.
    /// </summary>
    public string SafeSummary { get; }

    /// <summary>
    /// Gets the optional display-safe proposal rationale.
    /// </summary>
    public string? Rationale { get; }

    /// <summary>
    /// Gets copied display-safe host metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }
}

/// <summary>
/// Describes the exact confirmation a host presents to one approver.
/// </summary>
/// <remarks>
/// This type is passive. Hosts choose how to render a confirmation card, re-evaluate current authority, and issue an
/// opaque receipt after approval. A changed action must be submitted as a new <see cref="AgentActionRequest"/>; hosts
/// must not edit an approved request in place.
/// </remarks>
public sealed class AgentConfirmationRequest
{
    /// <summary>
    /// Creates a confirmation request.
    /// </summary>
    /// <param name="actionRequest">The exact action request awaiting confirmation.</param>
    /// <param name="approver">The host-local human or authority expected to confirm the action.</param>
    /// <param name="expiresAt">The host-supplied expiration timestamp.</param>
    /// <param name="metadata">Optional display-safe host metadata copied with ordinal keys.</param>
    public AgentConfirmationRequest(
        AgentActionRequest actionRequest,
        AgentApproverReference approver,
        DateTimeOffset expiresAt,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(actionRequest);
        approver.EnsureInitialized(nameof(approver));
        if (expiresAt <= actionRequest.RequestedAt)
        {
            throw new ArgumentException("Confirmation expiration must follow the request timestamp.", nameof(expiresAt));
        }

        ActionRequest = actionRequest;
        Approver = approver;
        ExpiresAt = expiresAt;
        Metadata = AgentApprovalContractValidation.NormalizeMetadata(metadata, nameof(metadata));
    }

    /// <summary>
    /// Gets the exact action request awaiting confirmation.
    /// </summary>
    public AgentActionRequest ActionRequest { get; }

    /// <summary>
    /// Gets the expected approver reference.
    /// </summary>
    public AgentApproverReference Approver { get; }

    /// <summary>
    /// Gets the expiration timestamp.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>
    /// Gets copied display-safe host metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }
}

/// <summary>
/// Describes the host's evaluation of an <see cref="AgentActionRequest"/>.
/// </summary>
/// <remarks>
/// The decision does not evaluate policy, issue a receipt, or execute an action. The diagnostic code must be the
/// canonical family for <see cref="Kind"/> or a stable subcode in that family; consumers branch on <see cref="Kind"/>
/// rather than on a display message or subcode.
/// </remarks>
public sealed class AgentAuthorizationDecision
{
    /// <summary>
    /// Creates an authorization decision.
    /// </summary>
    /// <param name="kind">The host evaluation outcome.</param>
    /// <param name="code">Stable machine-readable diagnostic code.</param>
    /// <param name="correlationId">Host-generated correlation identifier.</param>
    /// <param name="message">Optional display-safe diagnostic message.</param>
    /// <param name="confirmationRequest">Required only when <paramref name="kind"/> is <see cref="AgentAuthorizationDecisionKind.ConfirmationRequired"/>.</param>
    /// <param name="metadata">Optional display-safe host metadata copied with ordinal keys.</param>
    public AgentAuthorizationDecision(
        AgentAuthorizationDecisionKind kind,
        string code,
        string correlationId,
        string? message = null,
        AgentConfirmationRequest? confirmationRequest = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (kind == AgentAuthorizationDecisionKind.ConfirmationRequired && confirmationRequest is null)
        {
            throw new ArgumentException("Confirmation-required decisions must include a confirmation request.", nameof(confirmationRequest));
        }

        if (kind != AgentAuthorizationDecisionKind.ConfirmationRequired && confirmationRequest is not null)
        {
            throw new ArgumentException("Only confirmation-required decisions may include a confirmation request.", nameof(confirmationRequest));
        }

        var normalizedCorrelationId = AppSurfaceAuthMetadata.RequireIdentifier(correlationId, nameof(correlationId));
        if (confirmationRequest is not null
            && !string.Equals(confirmationRequest.ActionRequest.CorrelationId, normalizedCorrelationId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Confirmation request correlation must match the decision correlation.", nameof(confirmationRequest));
        }

        var normalizedCode = AgentApprovalContractValidation.RequireCode(code, nameof(code));
        AgentApprovalContractValidation.ValidateDecisionCode(kind, normalizedCode, nameof(code));

        Kind = kind;
        Code = normalizedCode;
        CorrelationId = normalizedCorrelationId;
        Message = AgentApprovalContractValidation.NormalizeSafeDisplayText(message, nameof(message));
        ConfirmationRequest = confirmationRequest;
        Metadata = AgentApprovalContractValidation.NormalizeMetadata(metadata, nameof(metadata));
    }

    /// <summary>
    /// Gets the host evaluation outcome.
    /// </summary>
    public AgentAuthorizationDecisionKind Kind { get; }

    /// <summary>
    /// Gets the stable machine-readable diagnostic code.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Gets the host-generated correlation identifier.
    /// </summary>
    public string CorrelationId { get; }

    /// <summary>
    /// Gets the optional display-safe diagnostic message.
    /// </summary>
    public string? Message { get; }

    /// <summary>
    /// Gets the confirmation request when <see cref="Kind"/> is <see cref="AgentAuthorizationDecisionKind.ConfirmationRequired"/>.
    /// </summary>
    public AgentConfirmationRequest? ConfirmationRequest { get; }

    /// <summary>
    /// Gets copied display-safe host metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }

    /// <summary>
    /// Gets a value indicating whether the host allowed the action without confirmation.
    /// </summary>
    public bool IsAllowed => Kind == AgentAuthorizationDecisionKind.Allowed;

    /// <summary>
    /// Gets a value indicating whether the host denied the action.
    /// </summary>
    public bool IsDenied => Kind == AgentAuthorizationDecisionKind.Denied;

    /// <summary>
    /// Gets a value indicating whether the host requires a human confirmation.
    /// </summary>
    public bool RequiresConfirmation => Kind == AgentAuthorizationDecisionKind.ConfirmationRequired;

    /// <summary>
    /// Creates an allowed decision with the standard AppSurface diagnostic code.
    /// </summary>
    /// <param name="correlationId">Host-generated correlation identifier.</param>
    /// <param name="message">Optional display-safe diagnostic message.</param>
    /// <param name="metadata">Optional display-safe host metadata copied with ordinal keys.</param>
    /// <returns>An allowed decision.</returns>
    public static AgentAuthorizationDecision Allowed(
        string correlationId,
        string? message = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return new AgentAuthorizationDecision(
            AgentAuthorizationDecisionKind.Allowed,
            AgentApprovalDiagnosticCodes.Allowed,
            correlationId,
            message,
            metadata: metadata);
    }

    /// <summary>
    /// Creates a denied decision with the standard AppSurface diagnostic code.
    /// </summary>
    /// <param name="correlationId">Host-generated correlation identifier.</param>
    /// <param name="message">Optional display-safe diagnostic message.</param>
    /// <param name="metadata">Optional display-safe host metadata copied with ordinal keys.</param>
    /// <returns>A denied decision.</returns>
    public static AgentAuthorizationDecision Denied(
        string correlationId,
        string? message = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return new AgentAuthorizationDecision(
            AgentAuthorizationDecisionKind.Denied,
            AgentApprovalDiagnosticCodes.Denied,
            correlationId,
            message,
            metadata: metadata);
    }

    /// <summary>
    /// Creates a confirmation-required decision with the standard AppSurface diagnostic code.
    /// </summary>
    /// <param name="confirmationRequest">The exact confirmation request the host presents.</param>
    /// <param name="message">Optional display-safe diagnostic message.</param>
    /// <param name="metadata">Optional display-safe host metadata copied with ordinal keys.</param>
    /// <returns>A confirmation-required decision.</returns>
    public static AgentAuthorizationDecision ConfirmationRequired(
        AgentConfirmationRequest confirmationRequest,
        string? message = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(confirmationRequest);
        return new AgentAuthorizationDecision(
            AgentAuthorizationDecisionKind.ConfirmationRequired,
            AgentApprovalDiagnosticCodes.ConfirmationRequired,
            confirmationRequest.ActionRequest.CorrelationId,
            message,
            confirmationRequest,
            metadata);
    }
}

/// <summary>
/// Describes an opaque host-issued approval proof bound to one action, approver, and expiration.
/// </summary>
/// <remarks>
/// The receipt is not a bearer-token format, signature, database record, or transport message. Hosts own issuance,
/// storage, revocation, atomic one-use consumption, current-authority checks, and execution. Use
/// <see cref="FromConfirmedRequest"/> to issue a new receipt after durable human approval. Direct construction supports
/// host-owned data reconstruction; it neither proves issuance nor authorizes execution. <see cref="ToString"/> redacts
/// the opaque receipt reference by default.
/// </remarks>
public sealed class AgentApprovalReceipt
{
    /// <summary>
    /// Creates an approval receipt for host-owned data reconstruction.
    /// </summary>
    /// <param name="receiptId">Opaque host-issued receipt reference.</param>
    /// <param name="binding">Exact bound action representation.</param>
    /// <param name="agent">Proposing agent reference.</param>
    /// <param name="approver">Approving human or authority reference.</param>
    /// <param name="correlationId">Host-generated correlation identifier.</param>
    /// <param name="issuedAt">Host-supplied issuance timestamp.</param>
    /// <param name="expiresAt">Host-supplied expiration timestamp.</param>
    /// <param name="metadata">Optional display-safe host metadata copied with ordinal keys.</param>
    public AgentApprovalReceipt(
        string receiptId,
        AgentActionBinding binding,
        AgentIdentityReference agent,
        AgentApproverReference approver,
        string correlationId,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        binding.EnsureInitialized(nameof(binding));
        agent.EnsureInitialized(nameof(agent));
        approver.EnsureInitialized(nameof(approver));
        if (expiresAt <= issuedAt)
        {
            throw new ArgumentException("Receipt expiration must follow issuance.", nameof(expiresAt));
        }

        ReceiptId = AgentApprovalContractValidation.RequireSafeDisplayText(receiptId, nameof(receiptId));
        Binding = binding;
        Agent = agent;
        Approver = approver;
        CorrelationId = AppSurfaceAuthMetadata.RequireIdentifier(correlationId, nameof(correlationId));
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
        Metadata = AgentApprovalContractValidation.NormalizeMetadata(metadata, nameof(metadata));
    }

    /// <summary>
    /// Creates an approval receipt from the exact confirmation request that a host durably approved.
    /// </summary>
    /// <param name="receiptId">Opaque host-issued receipt reference.</param>
    /// <param name="confirmationRequest">The exact confirmed request.</param>
    /// <param name="issuedAt">Host-supplied issuance timestamp.</param>
    /// <param name="expiresAt">Host-supplied receipt expiration timestamp.</param>
    /// <param name="metadata">Optional display-safe host metadata copied with ordinal keys.</param>
    /// <returns>A receipt bound to the confirmation request's action, agent, approver, and correlation identifier.</returns>
    /// <remarks>
    /// This factory validates structural consistency only. The caller must first perform and durably record the human
    /// approval, then persist and later atomically consume the resulting receipt.
    /// </remarks>
    public static AgentApprovalReceipt FromConfirmedRequest(
        string receiptId,
        AgentConfirmationRequest confirmationRequest,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(confirmationRequest);
        if (issuedAt < confirmationRequest.ActionRequest.RequestedAt)
        {
            throw new ArgumentException("Receipt issuance must not precede the action request.", nameof(issuedAt));
        }

        if (issuedAt >= confirmationRequest.ExpiresAt)
        {
            throw new ArgumentException("Receipt issuance must precede confirmation expiration.", nameof(issuedAt));
        }

        if (expiresAt > confirmationRequest.ExpiresAt)
        {
            throw new ArgumentException("Receipt expiration must not exceed confirmation expiration.", nameof(expiresAt));
        }

        return new AgentApprovalReceipt(
            receiptId,
            confirmationRequest.ActionRequest.Binding,
            confirmationRequest.ActionRequest.Agent,
            confirmationRequest.Approver,
            confirmationRequest.ActionRequest.CorrelationId,
            issuedAt,
            expiresAt,
            metadata);
    }

    /// <summary>
    /// Gets the opaque host-issued receipt reference.
    /// </summary>
    public string ReceiptId { get; }

    /// <summary>
    /// Gets the exact action binding.
    /// </summary>
    public AgentActionBinding Binding { get; }

    /// <summary>
    /// Gets the proposing agent reference.
    /// </summary>
    public AgentIdentityReference Agent { get; }

    /// <summary>
    /// Gets the approving authority reference.
    /// </summary>
    public AgentApproverReference Approver { get; }

    /// <summary>
    /// Gets the host-generated correlation identifier.
    /// </summary>
    public string CorrelationId { get; }

    /// <summary>
    /// Gets the host-supplied issuance timestamp.
    /// </summary>
    public DateTimeOffset IssuedAt { get; }

    /// <summary>
    /// Gets the host-supplied expiration timestamp.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>
    /// Gets copied display-safe host metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }

    /// <inheritdoc />
    public override string ToString()
    {
        return "AgentApprovalReceipt { ReceiptId = <redacted>, Binding = <redacted>, Agent = <redacted>, Approver = <redacted>, CorrelationId = <redacted>, IssuedAt = <redacted>, ExpiresAt = <redacted> }";
    }
}

/// <summary>
/// Describes one terminal host outcome while consuming an approval receipt.
/// </summary>
/// <remarks>
/// This result does not consume a receipt or retry an action. Hosts return it after their atomic claim and current
/// authority, grant, state, expiry, revocation, and binding checks complete.
/// </remarks>
public sealed class AgentApprovalConsumptionResult
{
    /// <summary>
    /// Creates a receipt consumption result.
    /// </summary>
    /// <param name="outcome">Terminal host outcome.</param>
    /// <param name="code">Stable machine-readable diagnostic code.</param>
    /// <param name="correlationId">Host-generated correlation identifier.</param>
    /// <param name="message">Optional display-safe diagnostic message.</param>
    /// <param name="metadata">Optional display-safe host metadata copied with ordinal keys.</param>
    public AgentApprovalConsumptionResult(
        AgentApprovalConsumptionOutcome outcome,
        string code,
        string correlationId,
        string? message = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        var normalizedCode = AgentApprovalContractValidation.RequireCode(code, nameof(code));
        AgentApprovalContractValidation.ValidateConsumptionCode(outcome, normalizedCode, nameof(code));

        Outcome = outcome;
        Code = normalizedCode;
        CorrelationId = AppSurfaceAuthMetadata.RequireIdentifier(correlationId, nameof(correlationId));
        Message = AgentApprovalContractValidation.NormalizeSafeDisplayText(message, nameof(message));
        Metadata = AgentApprovalContractValidation.NormalizeMetadata(metadata, nameof(metadata));
    }

    /// <summary>
    /// Gets the terminal host outcome.
    /// </summary>
    public AgentApprovalConsumptionOutcome Outcome { get; }

    /// <summary>
    /// Gets the stable machine-readable diagnostic code.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Gets the host-generated correlation identifier.
    /// </summary>
    public string CorrelationId { get; }

    /// <summary>
    /// Gets the optional display-safe diagnostic message.
    /// </summary>
    public string? Message { get; }

    /// <summary>
    /// Gets copied display-safe host metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }

    /// <summary>
    /// Gets a value indicating whether the host consumed the receipt.
    /// </summary>
    public bool IsConsumed => Outcome == AgentApprovalConsumptionOutcome.Consumed;

    /// <summary>
    /// Creates a consumed result with the standard AppSurface diagnostic code.
    /// </summary>
    /// <param name="correlationId">Host-generated correlation identifier.</param>
    /// <param name="message">Optional display-safe diagnostic message.</param>
    /// <param name="metadata">Optional display-safe host metadata copied with ordinal keys.</param>
    /// <returns>A consumed result.</returns>
    public static AgentApprovalConsumptionResult Consumed(
        string correlationId,
        string? message = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return Create(AgentApprovalConsumptionOutcome.Consumed, AgentApprovalDiagnosticCodes.Consumed, correlationId, message, metadata);
    }

    /// <summary>
    /// Creates an already-consumed result with the standard AppSurface diagnostic code.
    /// </summary>
    /// <param name="correlationId">Host-generated correlation identifier.</param>
    /// <param name="message">Optional display-safe diagnostic message.</param>
    /// <param name="metadata">Optional display-safe host metadata copied with ordinal keys.</param>
    /// <returns>An already-consumed result.</returns>
    public static AgentApprovalConsumptionResult AlreadyConsumed(
        string correlationId,
        string? message = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return Create(AgentApprovalConsumptionOutcome.AlreadyConsumed, AgentApprovalDiagnosticCodes.AlreadyConsumed, correlationId, message, metadata);
    }

    /// <summary>
    /// Creates an expired result with the standard AppSurface diagnostic code.
    /// </summary>
    /// <param name="correlationId">Host-generated correlation identifier.</param>
    /// <param name="message">Optional display-safe diagnostic message.</param>
    /// <param name="metadata">Optional display-safe host metadata copied with ordinal keys.</param>
    /// <returns>An expired result.</returns>
    public static AgentApprovalConsumptionResult Expired(
        string correlationId,
        string? message = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return Create(AgentApprovalConsumptionOutcome.Expired, AgentApprovalDiagnosticCodes.Expired, correlationId, message, metadata);
    }

    /// <summary>
    /// Creates a revoked result with the standard AppSurface diagnostic code.
    /// </summary>
    /// <param name="correlationId">Host-generated correlation identifier.</param>
    /// <param name="message">Optional display-safe diagnostic message.</param>
    /// <param name="metadata">Optional display-safe host metadata copied with ordinal keys.</param>
    /// <returns>A revoked result.</returns>
    public static AgentApprovalConsumptionResult Revoked(
        string correlationId,
        string? message = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return Create(AgentApprovalConsumptionOutcome.Revoked, AgentApprovalDiagnosticCodes.Revoked, correlationId, message, metadata);
    }

    /// <summary>
    /// Creates a stale result with the standard AppSurface diagnostic code.
    /// </summary>
    /// <param name="correlationId">Host-generated correlation identifier.</param>
    /// <param name="message">Optional display-safe diagnostic message.</param>
    /// <param name="metadata">Optional display-safe host metadata copied with ordinal keys.</param>
    /// <returns>A stale result.</returns>
    public static AgentApprovalConsumptionResult Stale(
        string correlationId,
        string? message = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return Create(AgentApprovalConsumptionOutcome.Stale, AgentApprovalDiagnosticCodes.Stale, correlationId, message, metadata);
    }

    /// <summary>
    /// Creates a binding-mismatch result with the standard AppSurface diagnostic code.
    /// </summary>
    /// <param name="correlationId">Host-generated correlation identifier.</param>
    /// <param name="message">Optional display-safe diagnostic message.</param>
    /// <param name="metadata">Optional display-safe host metadata copied with ordinal keys.</param>
    /// <returns>A binding-mismatch result.</returns>
    public static AgentApprovalConsumptionResult BindingMismatch(
        string correlationId,
        string? message = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return Create(AgentApprovalConsumptionOutcome.BindingMismatch, AgentApprovalDiagnosticCodes.BindingMismatch, correlationId, message, metadata);
    }

    /// <summary>
    /// Creates a denied result with the standard AppSurface diagnostic code.
    /// </summary>
    /// <param name="correlationId">Host-generated correlation identifier.</param>
    /// <param name="message">Optional display-safe diagnostic message.</param>
    /// <param name="metadata">Optional display-safe host metadata copied with ordinal keys.</param>
    /// <returns>A denied result.</returns>
    public static AgentApprovalConsumptionResult Denied(
        string correlationId,
        string? message = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return Create(AgentApprovalConsumptionOutcome.Denied, AgentApprovalDiagnosticCodes.ConsumptionDenied, correlationId, message, metadata);
    }

    private static AgentApprovalConsumptionResult Create(
        AgentApprovalConsumptionOutcome outcome,
        string code,
        string correlationId,
        string? message,
        IReadOnlyDictionary<string, string>? metadata)
    {
        return new AgentApprovalConsumptionResult(outcome, code, correlationId, message, metadata);
    }
}

/// <summary>
/// Describes a passive audit event for a delegated-agent authorization lifecycle.
/// </summary>
/// <remarks>
/// Hosts own audit event delivery and should use only display-safe values. This contract must not be treated as proof
/// that an audit sink persisted an event successfully.
/// </remarks>
public sealed class AgentAuthorizationAuditEvent
{
    /// <summary>
    /// Creates an agent authorization audit event description.
    /// </summary>
    /// <param name="kind">Lifecycle event kind.</param>
    /// <param name="timestamp">Host-supplied event timestamp.</param>
    /// <param name="code">Stable machine-readable diagnostic code.</param>
    /// <param name="correlationId">Host-generated correlation identifier.</param>
    /// <param name="binding">Bound action representation.</param>
    /// <param name="agent">Proposing agent reference.</param>
    /// <param name="approver">Optional human or authority reference.</param>
    /// <param name="receiptId">Optional opaque receipt reference.</param>
    /// <param name="safeSummary">Optional display-safe event summary.</param>
    /// <param name="metadata">Optional display-safe host metadata copied with ordinal keys.</param>
    public AgentAuthorizationAuditEvent(
        AgentAuthorizationAuditEventKind kind,
        DateTimeOffset timestamp,
        string code,
        string correlationId,
        AgentActionBinding binding,
        AgentIdentityReference agent,
        AgentApproverReference? approver = null,
        string? receiptId = null,
        string? safeSummary = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentNullException.ThrowIfNull(binding);
        binding.EnsureInitialized(nameof(binding));
        agent.EnsureInitialized(nameof(agent));
        if (approver is not null)
        {
            approver.Value.EnsureInitialized(nameof(approver));
        }

        var normalizedReceiptId = AgentApprovalContractValidation.NormalizeSafeDisplayText(receiptId, nameof(receiptId));
        if (AgentApprovalContractValidation.RequiresReceipt(kind) && normalizedReceiptId is null)
        {
            throw new ArgumentException("This audit event kind requires an opaque receipt reference.", nameof(receiptId));
        }

        if (kind == AgentAuthorizationAuditEventKind.Approved && approver is null)
        {
            throw new ArgumentException("An approval audit event requires an approver reference.", nameof(approver));
        }

        var normalizedCode = AgentApprovalContractValidation.RequireCode(code, nameof(code));
        AgentApprovalContractValidation.ValidateAuditCode(kind, normalizedCode, nameof(code));

        Kind = kind;
        Timestamp = timestamp;
        Code = normalizedCode;
        CorrelationId = AppSurfaceAuthMetadata.RequireIdentifier(correlationId, nameof(correlationId));
        Binding = binding;
        Agent = agent;
        Approver = approver;
        ReceiptId = normalizedReceiptId;
        SafeSummary = AgentApprovalContractValidation.NormalizeSafeDisplayText(safeSummary, nameof(safeSummary));
        Metadata = AgentApprovalContractValidation.NormalizeMetadata(metadata, nameof(metadata));
    }

    /// <summary>
    /// Creates an audit event from a host-issued receipt so the binding, agent, approver, receipt reference, and
    /// correlation identifier stay consistent.
    /// </summary>
    /// <param name="kind">Lifecycle event kind.</param>
    /// <param name="timestamp">Host-supplied event timestamp.</param>
    /// <param name="code">Stable machine-readable diagnostic code.</param>
    /// <param name="receipt">Host-issued receipt whose references the event records.</param>
    /// <param name="safeSummary">Optional display-safe event summary.</param>
    /// <param name="metadata">Optional display-safe host metadata copied with ordinal keys.</param>
    /// <returns>An audit event structurally consistent with <paramref name="receipt"/>.</returns>
    /// <remarks>
    /// This factory does not prove that the host persisted the event. It removes caller-side copying for receipt-backed
    /// events; hosts reconstructing persisted records may use the constructor after validating their stored references.
    /// </remarks>
    public static AgentAuthorizationAuditEvent FromReceipt(
        AgentAuthorizationAuditEventKind kind,
        DateTimeOffset timestamp,
        string code,
        AgentApprovalReceipt receipt,
        string? safeSummary = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return new AgentAuthorizationAuditEvent(
            kind,
            timestamp,
            code,
            receipt.CorrelationId,
            receipt.Binding,
            receipt.Agent,
            receipt.Approver,
            receipt.ReceiptId,
            safeSummary,
            metadata);
    }

    /// <summary>
    /// Gets the lifecycle event kind.
    /// </summary>
    public AgentAuthorizationAuditEventKind Kind { get; }

    /// <summary>
    /// Gets the host-supplied event timestamp.
    /// </summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>
    /// Gets the stable machine-readable diagnostic code.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Gets the host-generated correlation identifier.
    /// </summary>
    public string CorrelationId { get; }

    /// <summary>
    /// Gets the bound action representation.
    /// </summary>
    public AgentActionBinding Binding { get; }

    /// <summary>
    /// Gets the proposing agent reference.
    /// </summary>
    public AgentIdentityReference Agent { get; }

    /// <summary>
    /// Gets the optional approver reference.
    /// </summary>
    public AgentApproverReference? Approver { get; }

    /// <summary>
    /// Gets the optional opaque receipt reference.
    /// </summary>
    public string? ReceiptId { get; }

    /// <summary>
    /// Gets the optional display-safe event summary.
    /// </summary>
    public string? SafeSummary { get; }

    /// <summary>
    /// Gets copied display-safe host metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }
}
