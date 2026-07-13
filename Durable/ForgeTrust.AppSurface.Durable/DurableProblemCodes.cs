namespace ForgeTrust.AppSurface.Durable;

/// <summary>
/// Stable diagnostic codes shared by durable results, logs, metrics, operator history, and documentation.
/// </summary>
/// <remarks>
/// Codes are append-only compatibility identifiers. Never renumber or reuse a code for a different condition.
/// </remarks>
public static class DurableProblemCodes
{
    /// <summary>The durable request failed validation before persistence.</summary>
    public const string ValidationFailed = "ASDUR100";
    /// <summary>The transactional writer did not receive an active caller-owned transaction.</summary>
    public const string TransactionRequired = "ASDUR101";
    /// <summary>A command or idempotency identity was reused with different semantic bytes.</summary>
    public const string CommandConflict = "ASDUR102";
    /// <summary>The PostgreSQL store could not accept or process work.</summary>
    public const string StoreUnavailable = "ASDUR103";
    /// <summary>Another worker or transition won the expected claim revision.</summary>
    public const string ClaimLost = "ASDUR104";
    /// <summary>The lease, scope generation, or runtime epoch no longer authorizes execution.</summary>
    public const string LeaseLost = "ASDUR105";
    /// <summary>A permitted external effect may have occurred but no authoritative result is known.</summary>
    public const string AmbiguousExternalOutcome = "ASDUR106";
    /// <summary>The owning application scope is disabled.</summary>
    public const string ScopeDisabled = "ASDUR107";
    /// <summary>The runtime epoch must be rotated or reconciled after recovery.</summary>
    public const string RecoveryEpochRequired = "ASDUR108";
    /// <summary>The registered work or payload contract is unavailable.</summary>
    public const string WorkContractUnavailable = "ASDUR109";
    /// <summary>The work aggregate is already terminal.</summary>
    public const string AlreadyTerminal = "ASDUR110";
    /// <summary>The authorized scope does not contain the requested work aggregate.</summary>
    public const string WorkNotFound = "ASDUR111";
    /// <summary>The expected work revision does not match authoritative state.</summary>
    public const string WorkRevisionConflict = "ASDUR112";
    /// <summary>The requested durable scope does not exist.</summary>
    public const string ScopeNotFound = "ASDUR113";
    /// <summary>The expected scope generation does not match authoritative state.</summary>
    public const string ScopeGenerationConflict = "ASDUR114";
    /// <summary>The caller-owned transaction targets a different physical durable store.</summary>
    public const string StoreIdentityMismatch = "ASDUR115";
    /// <summary>The requested operator transition is unsafe or invalid for the authoritative work state.</summary>
    public const string OperatorTransitionRejected = "ASDUR116";
    /// <summary>A prior effect permit remains ambiguous and cannot be released as an ordinary retry.</summary>
    public const string OperatorProofRequired = "ASDUR117";
    /// <summary>Another operator command is already reconciling the work aggregate.</summary>
    public const string OperatorCommandInProgress = "ASDUR118";

    /// <summary>The requested Flow definition or version is unavailable.</summary>
    public const string FlowDefinitionUnavailable = "ASDUR200";
    /// <summary>Persisted Flow history cannot be interpreted by the registered definition or codecs.</summary>
    public const string FlowHistoryIncompatible = "ASDUR201";
    /// <summary>An external event arrived before its exact active wait.</summary>
    public const string FlowNotWaitingYet = "ASDUR202";
    /// <summary>A timer, event, cancellation, or terminal transition won the same Flow revision.</summary>
    public const string FlowRaceLost = "ASDUR203";
    /// <summary>The external event identity was already consumed.</summary>
    public const string FlowEventDuplicate = "ASDUR204";
    /// <summary>The application did not authorize access to the scoped Flow operation.</summary>
    public const string FlowAccessDenied = "ASDUR205";
    /// <summary>A Flow start identity was reused with different semantic content.</summary>
    public const string FlowStartConflict = "ASDUR206";
    /// <summary>A Flow command or event identity was reused with different semantic content.</summary>
    public const string FlowCommandConflict = "ASDUR207";
    /// <summary>The authorized scope does not contain the requested Flow instance.</summary>
    public const string FlowNotFound = "ASDUR208";
    /// <summary>The external event payload does not match the exact active Flow wait contract.</summary>
    public const string FlowEventContractMismatch = "ASDUR209";
    /// <summary>The suspended Flow runtime manifest is incompatible with the registered runtime.</summary>
    public const string FlowReleaseManifestMismatch = "ASDUR210";
    /// <summary>The suspended Flow wait shape cannot safely restore its recorded state.</summary>
    public const string FlowReleaseStateMismatch = "ASDUR211";

    /// <summary>The durable PostgreSQL schema is not installed.</summary>
    public const string SchemaMissing = "ASDUR400";
    /// <summary>The durable PostgreSQL schema requires pending migrations.</summary>
    public const string SchemaUpgradeRequired = "ASDUR401";
    /// <summary>The installed schema reader/writer range excludes this package.</summary>
    public const string SchemaVersionUnsupported = "ASDUR402";
    /// <summary>Recorded migration names, hashes, order, or metadata are inconsistent.</summary>
    public const string SchemaInconsistent = "ASDUR403";
    /// <summary>No continuous worker or external activator has advanced the runtime recently.</summary>
    public const string ActivatorStale = "ASDUR404";
    /// <summary>A live process already owns the configured worker identity.</summary>
    public const string WorkerIdentityConflict = "ASDUR405";
}
