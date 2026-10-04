namespace ForgeTrust.AppSurface.Durable;

/// <summary>Looks up canonical, privacy-safe wording for selected health/admission codes and runtime-doctor findings.</summary>
/// <remarks>
/// The catalog includes selected existing provider health/admission codes and doctor-only observations and terminal
/// findings. Lookup provides wording only; it does not classify a health snapshot or change provider predicates. A
/// code outside this set remains application-owned and is not assigned an inferred diagnosis.
/// </remarks>
public static class DurableDiagnosticCatalog
{
    private const string DocumentationBase =
        "https://github.com/forge-trust/AppSurface/blob/main/troubleshooting/durable-diagnostics.md#";

    private static readonly DurableDiagnosticDescriptor StoreUnavailable = new(
        DurableProblemCodes.StoreUnavailable,
        "Store unavailable",
        "A PostgreSQL transport, permission, session-affinity, timeout, or cleanup failure prevented the bounded durable operation from completing.",
        "Restore PostgreSQL connectivity and read permissions, establish session affinity, or unblock the cooperative maintenance fence; retry only under application policy.",
        Documentation("asdur103-store-unavailable"));

    private static readonly DurableDiagnosticDescriptor RecoveryEpochRequired = new(
        DurableProblemCodes.RecoveryEpochRequired,
        "Recovery epoch required",
        "The configured runtime epoch differs from the active store epoch.",
        "Perform authorized epoch initialization or rotation before enabling the worker host.",
        Documentation("asdur108-recovery-epoch-required"));

    private static readonly DurableDiagnosticDescriptor SchemaMissing = new(
        DurableProblemCodes.SchemaMissing,
        "Durable schema is missing",
        "The Durable schema or migration history is not installed.",
        "Apply reviewed forward-only migrations with a migration-owner connection.",
        Documentation("asdur400-durable-schema-is-missing"));

    private static readonly DurableDiagnosticDescriptor SchemaUpgradeRequired = new(
        DurableProblemCodes.SchemaUpgradeRequired,
        "Durable schema upgrade is required",
        "Known migrations are pending in the installed Durable schema.",
        "Apply every known pending migration before this reader or writer.",
        Documentation("asdur401-durable-schema-upgrade-is-required"));

    private static readonly DurableDiagnosticDescriptor SchemaVersionUnsupported = new(
        DurableProblemCodes.SchemaVersionUnsupported,
        "Durable schema version is too new or unsupported",
        "The installed reader or writer range excludes this package.",
        "Deploy compatible package code; do not bypass supported ranges.",
        Documentation("asdur402-durable-schema-version-is-too-new-or-unsupported"));

    private static readonly DurableDiagnosticDescriptor SchemaInconsistent = new(
        DurableProblemCodes.SchemaInconsistent,
        "Durable schema history is inconsistent",
        "Recorded migration names, checksums, order, or metadata do not match the expected schema history.",
        "Compare ordered names and checksums; never rewrite applied history.",
        Documentation("asdur403-durable-schema-history-is-inconsistent"));

    private static readonly DurableDiagnosticDescriptor ActivatorStale = new(
        DurableProblemCodes.ActivatorStale,
        "Initial heartbeat not observed or activator stale",
        "NotStarted may mean the first worker heartbeat is absent; Stale means the observed heartbeat or sweep exceeded HeartbeatStaleAfter.",
        "For NotStarted, treat it as a compatible initial assessment and follow the host's activation policy. For Stale, inspect the configured runtime and role/schema prerequisites.",
        Documentation("asdur404-initial-heartbeat-not-observed-or-activator-stale"));

    private static readonly DurableDiagnosticDescriptor RestrictedRuntimeCredentialRequired = new(
        DurableProblemCodes.RestrictedRuntimeCredentialRequired,
        "Restricted runtime credential required",
        "The connected role has a prohibited attribute, membership, ownership, grant option, or heartbeat-table privilege.",
        "Use a dedicated restricted LOGIN runtime credential and complete the reviewed runtime-role preflight; do not use a migration owner.",
        Documentation("asdur408-restricted-runtime-credential-required"));

    private static readonly DurableDiagnosticDescriptor HeartbeatRetentionUnavailable = new(
        DurableProblemCodes.HeartbeatRetentionUnavailable,
        "Heartbeat-retention capability unavailable",
        "The retention function, its required permissions or configuration, or the retention index is missing or does not meet the schema 0011 contract.",
        "Review the schema 0011 retention function and index contract, apply an authorized repair, then rerun doctor and complete runtime preflight.",
        Documentation("asdur409-heartbeat-retention-capability-unavailable"));

    private static readonly DurableDiagnosticDescriptor WorkerHeartbeatMissing = new(
        DurableProblemCodes.WorkerHeartbeatMissing,
        "No retained heartbeat for the selected worker",
        "No retained heartbeat matches the selected worker ID; the worker may not have started, the ID may differ, or retention may have removed the row.",
        "Verify the configured worker ID and host activation policy; rerun doctor after the worker records a heartbeat.",
        Documentation("asdur410-no-retained-heartbeat-for-the-selected-worker"));

    private static readonly DurableDiagnosticDescriptor WorkerDraining = new(
        DurableProblemCodes.WorkerDraining,
        "Selected worker is draining",
        "The selected worker heartbeat records that the worker is refusing new passes while existing work drains.",
        "Let in-flight work finish and follow the host's drain or deployment procedure before expecting new claims.",
        Documentation("asdur411-selected-worker-is-draining"));

    private static readonly DurableDiagnosticDescriptor WorkerHeartbeatEpochMismatch = new(
        DurableProblemCodes.WorkerHeartbeatEpochMismatch,
        "Selected heartbeat belongs to another runtime epoch",
        "The retained heartbeat's runtime epoch does not match the configured and active store epoch.",
        "Resolve the authorized runtime-epoch mismatch, then rerun doctor with the host's configured epoch.",
        Documentation("asdur412-selected-heartbeat-belongs-to-another-runtime-epoch"));

    private static readonly DurableDiagnosticDescriptor DoctorInputInvalid = new(
        DurableProblemCodes.DoctorInputInvalid,
        "Doctor input is invalid",
        "A command option or selected environment value is absent or malformed.",
        "Provide required values through the selected environment variables, use a restricted runtime credential, and check supported options with appsurface durable doctor --help.",
        Documentation("asdur413-doctor-input-is-invalid"));

    private static readonly DurableDiagnosticDescriptor DoctorCanceled = new(
        DurableProblemCodes.DoctorCanceled,
        "Doctor was canceled by its caller",
        "The caller canceled the valid doctor request before it completed.",
        "Retry only when the caller intends a fresh diagnostic attempt.",
        Documentation("asdur414-doctor-was-canceled-by-its-caller"));

    private static readonly DurableDiagnosticDescriptor DoctorContractFailed = new(
        DurableProblemCodes.DoctorContractFailed,
        "Doctor encountered an unexpected contract failure",
        "The CLI or provider returned malformed, contradictory, or otherwise unexpected doctor evidence.",
        "Verify the matching CLI and provider packages, investigate their contract compatibility and the canonical troubleshooting diagnostics, and retry only after resolving the mismatch.",
        Documentation("asdur415-doctor-encountered-an-unexpected-contract-failure"));

    /// <summary>Looks up a known canonical diagnostic without assigning meaning to unknown application codes.</summary>
    /// <param name="code">Stable diagnostic code to look up.</param>
    /// <param name="descriptor">The canonical descriptor when found; otherwise null.</param>
    /// <returns><see langword="true"/> when <paramref name="code"/> belongs to this catalog.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> is null.</exception>
    public static bool TryGet(string code, out DurableDiagnosticDescriptor? descriptor)
    {
        ArgumentNullException.ThrowIfNull(code);

        descriptor = code switch
        {
            DurableProblemCodes.StoreUnavailable => StoreUnavailable,
            DurableProblemCodes.RecoveryEpochRequired => RecoveryEpochRequired,
            DurableProblemCodes.SchemaMissing => SchemaMissing,
            DurableProblemCodes.SchemaUpgradeRequired => SchemaUpgradeRequired,
            DurableProblemCodes.SchemaVersionUnsupported => SchemaVersionUnsupported,
            DurableProblemCodes.SchemaInconsistent => SchemaInconsistent,
            DurableProblemCodes.ActivatorStale => ActivatorStale,
            DurableProblemCodes.RestrictedRuntimeCredentialRequired => RestrictedRuntimeCredentialRequired,
            DurableProblemCodes.HeartbeatRetentionUnavailable => HeartbeatRetentionUnavailable,
            DurableProblemCodes.WorkerHeartbeatMissing => WorkerHeartbeatMissing,
            DurableProblemCodes.WorkerDraining => WorkerDraining,
            DurableProblemCodes.WorkerHeartbeatEpochMismatch => WorkerHeartbeatEpochMismatch,
            DurableProblemCodes.DoctorInputInvalid => DoctorInputInvalid,
            DurableProblemCodes.DoctorCanceled => DoctorCanceled,
            DurableProblemCodes.DoctorContractFailed => DoctorContractFailed,
            _ => null,
        };

        return descriptor is not null;
    }

    private static Uri Documentation(string anchor) => new(DocumentationBase + anchor, UriKind.Absolute);
}
