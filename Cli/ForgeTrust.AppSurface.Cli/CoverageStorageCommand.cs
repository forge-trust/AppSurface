using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>Shared execution and schema-v1 rendering for coverage storage commands.</summary>
/// <remarks>Commands are noninteractive and expose safe identifiers and bounded diagnostics only.</remarks>
internal abstract class CoverageStorageCommandBase(string operation) : ICommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private const int MaximumDiagnostics = 32;
    private object? _partialDetails;

    /// <summary>Gets or sets whether the result is emitted as schema-versioned JSON.</summary>
    [CommandOption("json", Description = "Write the bounded schema-version 1 result as JSON.")]
    public bool Json { get; set; }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IConsole console) => await ExecuteAsync(console, console.RegisterCancellationHandler());

    /// <summary>Executes the operation with explicit cancellation and emits one safe result.</summary>
    /// <param name="console">CliFx output and error streams.</param>
    /// <param name="cancellationToken">Cancellation observed by storage operations.</param>
    internal async ValueTask ExecuteAsync(IConsole console, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(console);
        _partialDetails = null;
        var started = DateTimeOffset.UtcNow;
        var status = "complete";
        var effects = "none";
        object? details = null;
        var diagnostics = new List<CoverageArtifactDiagnostic>();
        string? recovery = null;
        string? reconcile = null;
        var exitCode = 0;
        var inputValidated = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateInput();
            inputValidated = true;
            var readOnly = (operation is "status" or "inspect" or "maintain") && !RequestsMutation;
            using var store = readOnly
                ? CoverageArtifactStore.OpenReadOnly() : CoverageArtifactStore.Open();
            cancellationToken.ThrowIfCancellationRequested();
            details = await ExecuteOperationAsync(store, cancellationToken, value => effects = value, value => status = value).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            status = "cancelled";
            effects = FailureEffects(effects);
            diagnostics.Add(CoverageArtifactDiagnostic.Failure(DiagnosticCode(operation), operation, new OperationCanceledException(), effects));
            recovery = "Inspect coverage storage status --json and reconcile the operation before retrying.";
            if (effects != "none") reconcile = "Check the affected run or destination before retrying; the operation may have committed partially.";
        }
        catch (ArgumentException)
        {
            status = "failed";
            exitCode = 2;
            effects = FailureEffects(effects);
            var diagnostic = CoverageArtifactDiagnostic.Failure("ASCOV208", operation, new ArgumentException(), effects);
            if (!inputValidated) diagnostic = diagnostic with { Cause = InvalidInputGuidance, Recovery = InvalidInputGuidance };
            diagnostics.Add(diagnostic);
            recovery = diagnostic.Recovery;
            if (effects != "none") reconcile = "Inspect status and the affected run or destination before retrying.";
        }
        catch (CoverageExecutionException exception)
        {
            status = "failed";
            if (exception.Code == "ASCOV208")
            {
                effects = FailureEffects(effects);
                exitCode = 2;
                diagnostics.Add(CoverageArtifactDiagnostic.Failure("ASCOV208", operation, new ArgumentException(), effects));
                recovery = "Supply valid command options and retry once.";
                if (effects != "none") reconcile = "Inspect status and the affected run or destination before retrying.";
            }
            else
            {
                var code = exception.Code == "ASCOV202" ? "ASCOV202" : DiagnosticCode(operation);
                effects = code == "ASCOV202" && effects != "partial" ? "none" : FailureEffects(effects);
                var diagnostic = CoverageArtifactDiagnostic.Failure(code, operation, exception, effects);
                diagnostics.Add(diagnostic);
                recovery = diagnostic.Recovery;
                if (effects != "none") reconcile = "Inspect status and the affected run or destination before retrying.";
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            status = "failed";
            effects = FailureEffects(effects);
            var diagnostic = CoverageArtifactDiagnostic.Failure(DiagnosticCode(operation), operation, exception, effects);
            diagnostics.Add(diagnostic);
            recovery = diagnostic.Recovery;
            if (effects != "none") reconcile = "Inspect status and the affected run or destination before retrying.";
        }
        catch (NotSupportedException exception)
        {
            status = "unsupported";
            effects = FailureEffects(effects);
            var diagnostic = CoverageArtifactDiagnostic.Failure(DiagnosticCode(operation), operation, exception, effects);
            diagnostics.Add(diagnostic);
            recovery = diagnostic.Recovery;
            if (effects != "none") reconcile = "Inspect status and the affected run or destination before retrying.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or JsonException)
        {
            status = "failed";
            effects = FailureEffects(effects);
            var diagnostic = CoverageArtifactDiagnostic.Failure(DiagnosticCode(operation), operation, exception, effects);
            diagnostics.Add(diagnostic);
            recovery = diagnostic.Recovery;
            if (effects != "none") reconcile = "Inspect status and the affected run or destination before retrying.";
        }
        catch (Exception exception)
        {
            status = "failed";
            effects = FailureEffects(effects);
            var diagnostic = CoverageArtifactDiagnostic.Failure(DiagnosticCode(operation), operation, exception, effects);
            diagnostics.Add(diagnostic);
            recovery = diagnostic.Recovery;
            if (effects != "none") reconcile = "Inspect status and the affected run or destination before retrying.";
        }

        details ??= _partialDetails;

        if ((status is "partial" or "cancelled") && diagnostics.Count == 0)
        {
            var capacityBound = operation == "status" && HasCapacityBound(details);
            var code = capacityBound ? "ASCOV207" : DiagnosticCode(operation);
            var (cause, action) = capacityBound
                ? ("The bounded store reached capacity; exact totals are lower bounds and overflow observations are reported separately.", "Review overflow observations and effective limits, preserve needed outputs, then retry status once after addressing capacity.")
                : operation switch
                {
                    "inspect" => ("Repository inspection ended before its time or entry budget; reported counts are lower bounds.", "Retry inspection once with the same root after resolving the reported access or scope condition."),
                    "maintain" => ("History maintenance stopped after a partial pass; reconcile completed deletions and remaining candidates.", "Inspect status, then retry maintenance once after resolving the named condition."),
                    _ => ("The operation returned an incomplete result.", "Inspect coverage storage status --json, resolve the reported condition, then retry once.")
                };
            diagnostics.Add(new CoverageArtifactDiagnostic(code, operation, cause, action, effects));
            recovery ??= action;
            if (effects != "none") reconcile ??= "Inspect status and the affected run or destination before retrying.";
        }

        var envelope = new CoverageStorageResult(
            1, operation, status, DomainId(), started, new { scope = "registered-domain", complete = status == "complete", completeness = status switch { "complete" => "complete", "partial" => "lower-bound", "cancelled" => "cancelled", _ => "unavailable" } },
            details, diagnostics.Take(MaximumDiagnostics).ToArray(), effects, recovery, reconcile, 1);
        if (Json)
            await console.Output.WriteLineAsync(JsonSerializer.Serialize(envelope, JsonOptions)).ConfigureAwait(false);
        else
        {
            await WriteHumanResultAsync(console, envelope).ConfigureAwait(false);
            foreach (var diagnostic in diagnostics.Take(MaximumDiagnostics)) await diagnostic.WriteAsync(console.Error).ConfigureAwait(false);
        }

        if (status == "cancelled") throw new CommandException("Coverage storage operation cancelled.", 130);
        if (status == "failed") throw new CommandException("Coverage storage operation failed; see the bounded diagnostic output.", exitCode == 2 ? 2 : 1);
        if (status is "partial" or "unsupported") throw new CommandException("Coverage storage could not complete; review the bounded result and diagnostics.", 1);
    }

    private async ValueTask WriteHumanResultAsync(IConsole console, CoverageStorageResult envelope)
    {
        await console.Output.WriteLineAsync($"coverage storage {operation}: {envelope.Status}").ConfigureAwait(false);
        if (envelope.Data is null) return;

        var data = JsonSerializer.SerializeToElement(envelope.Data, JsonOptions);
        if (operation == "status")
        {
            await console.Output.WriteLineAsync($"  runs: {ReadInt(data, "registeredRuns")} registered, {ReadInt(data, "currentRuns")} current, {ReadInt(data, "archivedRuns")} archived").ConfigureAwait(false);
            await console.Output.WriteLineAsync($"  bytes: {FormatBytes(ReadLong(data, "currentBytes"))} current; {FormatBytes(ReadLong(data, "historyBytes"))} history; {FormatBytes(ReadLong(data, "pinnedBytes"))} pinned; {FormatBytes(ReadLong(data, "metadataBytes"))} metadata; {FormatBytes(ReadLong(data, "stagingBytes"))} staging reserved").ConfigureAwait(false);
            await console.Output.WriteLineAsync($"  protected bytes: {FormatBytes(ReadLong(data, "protectedBytes"))}; overflow observations: {ReadInt(data, "overflowObservationCount")} (reported separately)").ConfigureAwait(false);
            await console.Output.WriteLineAsync($"  history limits: {FormatBytes(ReadLong(data, "historyMaxBytes"))}; {ReadInt(data, "historyMaxAgeDays")} days").ConfigureAwait(false);
            if (data.TryGetProperty("policy", out var policy))
                await console.Output.WriteLineAsync($"  policy: {ReadString(policy, "path")}; repository warning {FormatBytes(ReadLong(policy, "repositoryWarningBytes"))}; domain warning {FormatBytes(ReadLong(policy, "domainWarningBytes"))}; cooldown {ReadInt(policy, "warningCooldownHours")} hours").ConfigureAwait(false);
            if (data.TryGetProperty("managementCapability", out var capability))
                await console.Output.WriteLineAsync($"  management: {(ReadString(capability, "status") == "not-probed" ? "not verified (read-only status)" : "not verified")}").ConfigureAwait(false);
            if (!ReadBool(data, "complete")) await console.Output.WriteLineAsync("  inventory is incomplete; totals are lower bounds").ConfigureAwait(false);
        }
        else if (operation == "inspect")
        {
            await console.Output.WriteLineAsync($"  observed: {FormatBytes(ReadLong(data, "bytes"))} across {ReadInt(data, "files")} files and {ReadInt(data, "entries")} entries").ConfigureAwait(false);
            await console.Output.WriteLineAsync($"  inspection limits: 100,000 entries; 30 seconds").ConfigureAwait(false);
        }
        else if (operation == "maintain")
        {
            var candidates = data.TryGetProperty("candidateCount", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var value)
                ? value.ToString(CultureInfo.InvariantCulture)
                : "unknown";
            await console.Output.WriteLineAsync($"  candidates: {candidates}; apply: {ReadBool(data, "apply")}").ConfigureAwait(false);
        }
        else if (operation is "restore" or "pin" or "unpin")
        {
            await console.Output.WriteLineAsync("  operation completed for the requested opaque run identifier").ConfigureAwait(false);
        }
    }

    private static int ReadInt(JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result) ? result : 0;
    private static long ReadLong(JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result) ? result : 0;
    private static bool ReadBool(JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static string ReadString(JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "unknown" : "unknown";

    private static string FailureEffects(string effects) => effects is "none" or "partial" ? effects : "unknown";

    private static bool HasCapacityBound(object? details)
    {
        if (details is null) return false;
        var data = JsonSerializer.SerializeToElement(details, JsonOptions);
        if (ReadInt(data, "overflowObservationCount") > 0) return true;
        if (!data.TryGetProperty("incompleteReason", out var reason) || reason.ValueKind != JsonValueKind.String) return false;
        var value = reason.GetString() ?? string.Empty;
        return value.Contains("capacity", StringComparison.OrdinalIgnoreCase)
            || value.Contains("limit", StringComparison.OrdinalIgnoreCase)
            || value.Contains("lower bound", StringComparison.OrdinalIgnoreCase)
            || value.Contains("lower-bound", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatBytes(long value)
    {
        if (value < 1024) return value.ToString("N0", CultureInfo.InvariantCulture) + " B";
        var kibibytes = value / 1024d;
        if (kibibytes < 1024) return kibibytes.ToString("N1", CultureInfo.InvariantCulture) + " KiB";
        var mebibytes = kibibytes / 1024d;
        if (mebibytes < 1024) return mebibytes.ToString("N1", CultureInfo.InvariantCulture) + " MiB";
        return (mebibytes / 1024d).ToString("N2", CultureInfo.InvariantCulture) + " GiB";
    }

    /// <summary>Performs the operation and returns data that contains no raw filesystem paths.</summary>
    protected abstract ValueTask<object?> ExecuteOperationAsync(CoverageArtifactStore store, CancellationToken token, Action<string> setEffects, Action<string> setStatus);

    /// <summary>Gets whether an explicit operation authorizes private-store mutation.</summary>
    protected virtual bool RequestsMutation => false;

    /// <summary>Validates explicit command inputs before opening or initializing the store.</summary>
    protected virtual void ValidateInput() { }

    /// <summary>Gets a trusted field and accepted-form hint for a failed preflight, without echoing supplied values.</summary>
    protected virtual string InvalidInputGuidance => "Supply valid command options and retry once.";

    /// <summary>Gets the accepted opaque run identifier form used by pin, unpin and restore.</summary>
    protected const string RunInputGuidance = "Supply --run as 32 hexadecimal characters without hyphens, then retry once.";

    /// <summary>Publishes safe progress details so failures retain completed work in the result envelope.</summary>
    protected void PublishPartialDetails(object details) => _partialDetails = details ?? throw new ArgumentNullException(nameof(details));

    /// <summary>Creates a bounded status payload while keeping overflow measurements separate from exact totals.</summary>
    protected static object CreateStatusData(CoverageArtifactStore store, Action<string> setStatus)
    {
        var value = store.GetStatus();
        if (!value.IsComplete) setStatus("partial");
        var policy = store.Policy;
        var capability = ReadManagementCapability();
        var protectedReasons = new List<string>();
        if (value.CurrentBytes > 0) protectedReasons.Add("current");
        if (value.PinnedBytes > 0) protectedReasons.Add("pinned");
        if (value.StagingReservedBytes > 0) protectedReasons.Add("unreconciled-reservation");
        var overflow = value.OverflowObservations.Select(item => new
        {
            runId = item.RunId,
            bytes = item.Bytes,
            observedAt = item.ObservedAtUtc
        }).ToArray();
        return new
        {
            registeredRuns = value.RegisteredRuns,
            currentRuns = value.CurrentRuns,
            archivedRuns = value.ArchivedRuns,
            currentBytes = value.CurrentBytes,
            historyBytes = value.ArchivedBytes,
            pinnedBytes = value.PinnedBytes,
            protectedBytes = checked(value.CurrentBytes + value.PinnedBytes),
            metadataBytes = value.MetadataBytes,
            stagingBytes = value.StagingReservedBytes,
            stagingReservedBytes = value.StagingReservedBytes,
            expiryReceiptCount = value.ExpiryReceiptCount,
            protectedReasons,
            overflowObservationCount = overflow.Length,
            overflowObservations = overflow,
            policy = new
            {
                path = CoverageArtifactDisplay.PathLabel(Path.Join(store.RootPath, "policy.json"), store.RootPath),
                schemaVersion = 1,
                historyMaxBytes = policy.HistoryMaxBytes,
                historyMaxAgeDays = policy.HistoryMaxAgeDays,
                repositoryWarningBytes = policy.RepositoryWarningBytes,
                domainWarningBytes = policy.DomainWarningBytes,
                warningCooldownHours = policy.WarningCooldownHours
            },
            managementCapability = capability,
            limits = new
            {
                historyMaxBytes = policy.HistoryMaxBytes,
                historyMaxAgeDays = policy.HistoryMaxAgeDays,
                fixedResources = new
                {
                    maximumPolicyBytes = CoverageArtifactLimits.MaximumPolicyBytes,
                    automaticSnapshotMaxOriginalBytes = CoverageArtifactLimits.AutomaticSnapshotMaxOriginalBytes,
                    automaticSnapshotMaxStagingBytes = CoverageArtifactLimits.AutomaticSnapshotMaxStagingBytes,
                    domainStagingMaxBytes = CoverageArtifactLimits.DomainStagingMaxBytes,
                    explicitExpandedMaxBytes = CoverageArtifactLimits.ExplicitExpandedMaxBytes,
                    maximumFiles = CoverageArtifactLimits.MaximumFiles,
                    maximumRegistrations = CoverageArtifactLimits.MaximumRegistrations,
                    runInventoryMaxBytes = CoverageArtifactLimits.RunInventoryMaxBytes,
                    registryMaxBytes = CoverageArtifactLimits.RegistryMaxBytes,
                    overflowObservationReserveBytes = CoverageArtifactLimits.OverflowObservationReserveBytes,
                    maximumOverflowObservations = CoverageArtifactLimits.MaximumOverflowObservations,
                    operationBufferMaxBytes = CoverageArtifactLimits.OperationBufferMaxBytes,
                    inspectionBudgetMilliseconds = CoverageArtifactLimits.InspectionBudgetMilliseconds,
                    inspectionMaxEntries = CoverageArtifactLimits.InspectionMaxEntries,
                    routineMaintenanceBudgetMilliseconds = CoverageArtifactLimits.RoutineMaintenanceBudgetMilliseconds,
                    warningStateMaxConditions = CoverageArtifactLimits.WarningStateMaxConditions,
                    warningStateMaxBytes = CoverageArtifactLimits.WarningStateMaxBytes,
                    expiryReceiptsMaxEntries = CoverageArtifactLimits.ExpiryReceiptsMaxEntries,
                    expiryReceiptsMaxBytes = CoverageArtifactLimits.ExpiryReceiptsMaxBytes,
                    expiryReceiptMaxAgeDays = CoverageArtifactLimits.ExpiryReceiptMaxAgeDays
                }
            },
            historyMaxBytes = value.Policy.HistoryMaxBytes,
            historyMaxAgeDays = value.Policy.HistoryMaxAgeDays,
            complete = value.IsComplete,
            incompleteReason = value.IncompleteReason
        };
    }

    private static object ReadManagementCapability() => new
    {
        status = "not-probed",
        capabilityLastVerified = "unknown",
        supported = (bool?)null,
        fileSystem = (string?)null,
        nativeLocking = (bool?)null,
        noFollowTraversal = (bool?)null,
        atomicNoReplaceRename = (bool?)null
    };

    private static string DiagnosticCode(string name) => name switch
    {
        "inspect" => "ASCOV203",
        "maintain" => "ASCOV204",
        "restore" => "ASCOV205",
        "pin" or "unpin" => "ASCOV206",
        _ => "ASCOV201"
    };

    /// <summary>Hashes the effective domain path so machine-local prefixes are never emitted.</summary>
    /// <param name="openStore">Optional per-call observation seam; production opens the configured read-only store.</param>
    /// <remarks>Every nonfatal observation failure returns domain-unknown so envelope rendering preserves the primary diagnostic.</remarks>
    internal static string DomainId(Func<CoverageArtifactStore>? openStore = null)
    {
        try
        {
            using var store = openStore is null ? CoverageArtifactStore.OpenReadOnly() : openStore();
            return "domain-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(store.RootPath))))[..16].ToLowerInvariant();
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            return "domain-unknown";
        }
    }

    /// <summary>Stable schema-v1 response envelope used by every command in this family.</summary>
    protected sealed record CoverageStorageResult(int SchemaVersion, string Operation, string Status, string DomainId, DateTimeOffset ObservedAt, object Observation, object? Data, IReadOnlyList<CoverageArtifactDiagnostic> Diagnostics, string SideEffects, string? RecoveryAction, string? ReconcileAction, int MaxRetries);
}

/// <summary>Shows registered coverage storage status; this is also the bare storage-group default.</summary>
[Command("coverage storage", Description = "Show current coverage artifact storage status.")]
internal sealed partial class CoverageStorageStatusCommand : CoverageStorageCommandBase
{
    /// <summary>Creates the default status command.</summary>
    public CoverageStorageStatusCommand() : base("status") { }
    /// <inheritdoc />
    protected override ValueTask<object?> ExecuteOperationAsync(CoverageArtifactStore store, CancellationToken token, Action<string> setEffects, Action<string> setStatus)
        => ValueTask.FromResult<object?>(CreateStatusData(store, setStatus));
}

/// <summary>Shows the explicitly named coverage storage status.</summary>
[Command("coverage storage status", Description = "Show registered coverage artifact storage status.")]
internal sealed partial class CoverageStorageNamedStatusCommand : CoverageStorageCommandBase
{
    /// <summary>Creates the named status command.</summary>
    public CoverageStorageNamedStatusCommand() : base("status") { }
    /// <inheritdoc />
    protected override ValueTask<object?> ExecuteOperationAsync(CoverageArtifactStore store, CancellationToken token, Action<string> setEffects, Action<string> setStatus)
        => ValueTask.FromResult<object?>(CreateStatusData(store, setStatus));
}

/// <summary>Inspects a repository's coverage output with fixed time and entry bounds, without adopting files.</summary>
[Command("coverage storage inspect", Description = "Inspect coverage artifacts below --root without registering or changing them.")]
internal sealed partial class CoverageStorageInspectCommand : CoverageStorageCommandBase
{
    /// <summary>Creates the inspection command.</summary>
    public CoverageStorageInspectCommand() : base("inspect") { }
    /// <summary>Gets or sets the repository root to inspect; defaults to the current directory.</summary>
    [CommandOption("root", Description = "Repository directory to inspect. Defaults to the current directory.")]
    public string? Root { get; set; }
    /// <inheritdoc />
    protected override ValueTask<object?> ExecuteOperationAsync(CoverageArtifactStore store, CancellationToken token, Action<string> setEffects, Action<string> setStatus)
    {
        var observed = CoverageArtifactInspector.Inspect(Root ?? Directory.GetCurrentDirectory(), TimeProvider.System, TimeSpan.FromSeconds(30), 100_000, token);
        if (!observed.Complete)
        {
            // The shared envelope is conservatively partial even when traversal returns a useful lower bound.
            setStatus(observed.Cancelled ? "cancelled" : "partial");
        }
        return ValueTask.FromResult<object?>(new { scope = "repository-coverage-output", observedAt = observed.ObservedAt, complete = observed.Complete, cancelled = observed.Cancelled, bytes = observed.Bytes, files = observed.FileCount, entries = observed.Entries, protectedReasons = (string[]?)null, limits = new { maximumEntries = 100_000, budgetSeconds = 30 }, largest = observed.Largest, errors = observed.Errors });
    }
}

/// <summary>Previews history expiry, applying it only when explicitly requested with --apply.</summary>
[Command("coverage storage maintain", Description = "Preview eligible coverage history expiry; pass --apply to remove it.")]
internal sealed partial class CoverageStorageMaintainCommand : CoverageStorageCommandBase
{
    /// <summary>Creates the maintenance command.</summary>
    public CoverageStorageMaintainCommand() : base("maintain") { }
    /// <summary>Gets or sets whether eligible history should be deleted.</summary>
    [CommandOption("apply", Description = "Apply the preview and remove eligible archived history.")]
    public bool Apply { get; set; }
    /// <inheritdoc />
    protected override bool RequestsMutation => Apply;
    /// <inheritdoc />
    protected override async ValueTask<object?> ExecuteOperationAsync(CoverageArtifactStore store, CancellationToken token, Action<string> setEffects, Action<string> setStatus)
    {
        if (Apply) setEffects("unknown");
        var progress = new MaintenanceProgress(Apply);
        if (Apply) PublishPartialDetails(progress);
        var ids = await store.MaintainAsync(Apply, token, onArchiveDeleted: runId =>
        {
            progress.DeletedRunIds.Add(runId);
            setEffects("partial");
        }).ConfigureAwait(false);
        progress.SetCandidates(ids);
        if (Apply) setEffects(ids.Count > 0 ? "complete" : "none");
        return progress;
    }

    private sealed class MaintenanceProgress(bool apply)
    {
        public bool Apply { get; } = apply;
        public IReadOnlyList<string>? CandidateRunIds { get; private set; }
        public List<string> DeletedRunIds { get; } = [];
        public int? CandidateCount { get; private set; }

        public void SetCandidates(IReadOnlyList<string> runIds)
        {
            CandidateRunIds = runIds;
            CandidateCount = runIds.Count;
        }
    }
}

/// <summary>Restores one verified archived run to a fresh output directory.</summary>
[Command("coverage storage restore", Description = "Restore a verified archived run to a fresh --output directory.")]
internal sealed partial class CoverageStorageRestoreCommand : CoverageStorageCommandBase
{
    /// <summary>Creates the restore command.</summary>
    public CoverageStorageRestoreCommand() : base("restore") { }
    /// <summary>Gets or sets the opaque run identifier.</summary>
    [CommandOption("run", Description = "Opaque registered run identifier to restore.")]
    public string Run { get; set; } = string.Empty;
    /// <summary>Gets or sets the fresh destination directory.</summary>
    [CommandOption("output", Description = "Fresh destination directory; it must not already exist.")]
    public string Output { get; set; } = string.Empty;
    /// <inheritdoc />
    protected override void ValidateInput()
    {
        if (!Guid.TryParseExact(Run, "N", out _) || string.IsNullOrWhiteSpace(Output))
            throw new ArgumentException("restore requires --run and a fresh --output.");
        _ = Path.GetFullPath(Output);
    }
    /// <inheritdoc />
    protected override string InvalidInputGuidance => !Guid.TryParseExact(Run, "N", out _)
        ? RunInputGuidance
        : "Supply --output as a nonempty fresh destination directory, then retry once.";
    /// <inheritdoc />
    protected override async ValueTask<object?> ExecuteOperationAsync(CoverageArtifactStore store, CancellationToken token, Action<string> setEffects, Action<string> setStatus)
    {
        setEffects("unknown");
        await store.RestoreAsync(Run, Output, token).ConfigureAwait(false);
        setEffects("complete");
        return new { runId = Run, restored = true };
    }
}

/// <summary>Pins or unpins a verified archived run so normal history expiry respects operator intent.</summary>
[Command("coverage storage pin", Description = "Pin a verified archived coverage run.")]
internal sealed partial class CoverageStoragePinCommand : CoverageStorageCommandBase
{
    /// <summary>Creates the pin command.</summary>
    public CoverageStoragePinCommand() : base("pin") { }
    /// <summary>Gets or sets the opaque run identifier.</summary>
    [CommandOption("run", Description = "Opaque registered archived run identifier.")]
    public string Run { get; set; } = string.Empty;
    /// <inheritdoc />
    protected override void ValidateInput()
    {
        if (!Guid.TryParseExact(Run, "N", out _)) throw new ArgumentException("pin requires --run.");
    }
    /// <inheritdoc />
    protected override string InvalidInputGuidance => RunInputGuidance;
    /// <inheritdoc />
    protected override async ValueTask<object?> ExecuteOperationAsync(CoverageArtifactStore store, CancellationToken token, Action<string> setEffects, Action<string> setStatus)
    {
        setEffects("unknown"); await store.PinAsync(Run, true, token).ConfigureAwait(false); setEffects("complete");
        return new { runId = Run, pinned = true };
    }
}

/// <summary>Removes the protection pin from a verified archived run.</summary>
[Command("coverage storage unpin", Description = "Unpin an archived coverage run.")]
internal sealed partial class CoverageStorageUnpinCommand : CoverageStorageCommandBase
{
    /// <summary>Creates the unpin command.</summary>
    public CoverageStorageUnpinCommand() : base("unpin") { }
    /// <summary>Gets or sets the opaque run identifier.</summary>
    [CommandOption("run", Description = "Opaque registered archived run identifier.")]
    public string Run { get; set; } = string.Empty;
    /// <inheritdoc />
    protected override void ValidateInput()
    {
        if (!Guid.TryParseExact(Run, "N", out _)) throw new ArgumentException("unpin requires --run.");
    }
    /// <inheritdoc />
    protected override string InvalidInputGuidance => RunInputGuidance;
    /// <inheritdoc />
    protected override async ValueTask<object?> ExecuteOperationAsync(CoverageArtifactStore store, CancellationToken token, Action<string> setEffects, Action<string> setStatus)
    {
        setEffects("unknown"); await store.PinAsync(Run, false, token).ConfigureAwait(false); setEffects("complete");
        return new { runId = Run, pinned = false };
    }
}
