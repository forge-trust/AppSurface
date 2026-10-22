using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Coordinates bounded repository observations and persistent, local warning cooldowns.</summary>
/// <remarks>
/// Each instance shares one second and 10,000 entries across preflight and terminal inspection.
/// Discovered files remain observations: they never become artifact ownership or deletion authority.
/// Warning-state failure cannot replace the primary coverage result.
/// </remarks>
internal sealed class CoverageArtifactWarnings(string repository, CoverageArtifactStore? store, TimeProvider clock, TextWriter errors)
{
    private readonly CoverageArtifactPolicy _policy = store?.Policy ?? new();
    private readonly string _scopeId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(repository))));
    private readonly Dictionary<string, WarningCondition> _local = new(StringComparer.Ordinal);
    private TimeSpan _inspectionSpent;
    private int _entriesSpent;
    private CoverageArtifactObservation? _preflight;
    private IReadOnlyList<string>? _continuation;
    private bool _cursorLoaded;

    /// <summary>Inspects at preflight or terminal publication with the remaining cumulative observation budget.</summary>
    /// <param name="terminal">Whether the remaining inspection allowance is available.</param>
    /// <param name="inventory">Newly produced files, accounted even if traversal reaches its limit.</param>
    /// <param name="token">Caller cancellation; optional inspection never swallows a primary caller cancellation.</param>
    internal async Task ObserveAsync(bool terminal, IReadOnlyList<CoverageArtifactInventoryEntry>? inventory, CancellationToken token)
    {
        try
        {
            var started = clock.GetTimestamp();
            if (!_cursorLoaded)
            {
                _cursorLoaded = true;
                try { _continuation = CoverageArtifactInspectionCursorStore.Read(store, _scopeId, clock); }
                catch (Exception exception) when (IsSecondary(exception))
                {
                    await CoverageArtifactDiagnostic.Failure("ASCOV201", "inspection-cursor", exception).WriteAsync(errors).ConfigureAwait(false);
                }
            }
            var remaining = TimeSpan.FromSeconds(1) - _inspectionSpent - clock.GetElapsedTime(started);
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            var allowance = terminal ? remaining : TimeSpan.FromTicks(remaining.Ticks / 2);
            var entries = Math.Max(0, 10_000 - _entriesSpent);
            if (!terminal) entries /= 2;
            var observation = CoverageArtifactInspector.Inspect(repository, clock, allowance, entries, token, _continuation);
            _continuation = observation.ContinuationDirectories;
            try
            {
                if (_inspectionSpent + clock.GetElapsedTime(started) < TimeSpan.FromSeconds(1))
                    CoverageArtifactInspectionCursorStore.Write(store, _scopeId, _continuation, clock);
            }
            catch (Exception exception) when (IsSecondary(exception))
            {
                await CoverageArtifactDiagnostic.Failure("ASCOV201", "inspection-cursor", exception).WriteAsync(errors).ConfigureAwait(false);
            }
            _inspectionSpent += clock.GetElapsedTime(started);
            _entriesSpent += observation.Entries;
            if (!terminal) _preflight = observation;
            var bytes = Math.Max(observation.Bytes, _preflight?.Bytes ?? 0);
            var count = Math.Max(observation.FileCount, _preflight?.FileCount ?? 0);
            var producedBytes = inventory?.Aggregate(0L, (sum, file) => checked(sum + file.Length)) ?? 0;
            bytes = Math.Max(bytes, producedBytes); // A lower bound; inventory and traversal may overlap.
            count = Math.Max(count, inventory?.Count ?? 0);
            var complete = observation.Complete;
            if (!complete)
            {
                var failure = string.Join(',', observation.Errors.Select(error => error.Type).Distinct().Order(StringComparer.Ordinal));
                await EmitAsync(new("ASCOV203", "inspect", $"Repository observation is incomplete; at least {bytes} bytes in {count} files were observed after {_entriesSpent} entries. Cause: {(failure.Length == 0 ? "inspection budget" : CoverageArtifactDisplay.Bound(failure))}.",
                    "Run coverage storage inspect --root . --json to inspect the repository; review its lower-bound result before deciding retention.", ObservedBytes: bytes), "inspection:" + failure, bytes).ConfigureAwait(false);
            }
            if (bytes >= _policy.RepositoryWarningBytes)
            {
                var paths = string.Join(", ", observation.Largest.Take(5).Select(item => item.Path + " (" + item.Bytes + " bytes)"));
                await EmitAsync(new("ASCOV210", "storage-growth", $"Repository coverage output contains {(complete ? string.Empty : "at least ")}{bytes} bytes in {count} files; observed at {observation.ObservedAt:O}. Largest: {paths}",
                    "Run coverage storage inspect --root . --json; use a stable AppSurface output and review explicit-collection guidance for unmanaged TestResults. Current and unmanaged reports are preserved.", ObservedBytes: bytes, AllowedBytes: _policy.RepositoryWarningBytes), "repository-growth", bytes).ConfigureAwait(false);
            }
            if (store is not null)
            {
                var status = store.GetStatus();
                var total = checked(status.CurrentBytes + status.ArchivedBytes + status.MetadataBytes);
                if (!status.IsComplete)
                    await EmitAsync(new("ASCOV207", "accounting", "The registration domain reached its metadata capacity; current totals are lower bounds and recent overflow observations are reported separately.",
                        "Inspect coverage storage status --json and overflow observations; retain needed current and pinned outputs before an explicit cleanup decision.", ObservedBytes: total), "metadata-capacity", total).ConfigureAwait(false);
                if (total >= _policy.DomainWarningBytes)
                    await EmitAsync(new("ASCOV210", "storage-growth", $"Registered domain contains {(status.IsComplete ? string.Empty : "at least ")}{total} observed bytes: current={status.CurrentBytes}, history={status.ArchivedBytes}, pinned={status.PinnedBytes}; stagingReserved={status.StagingReservedBytes}. Observations may be stale.",
                        "Run coverage storage status --json; reuse stable outputs for repeated runs. Current outputs and pins require an explicit retention decision and cannot be evicted by the history budget.", ObservedBytes: total, AllowedBytes: _policy.DomainWarningBytes), "domain-growth", total).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (IsSecondary(exception))
        {
            await CoverageArtifactDiagnostic.Failure("ASCOV201", "warning-inspection", exception).WriteAsync(errors).ConfigureAwait(false);
        }
    }

    /// <summary>Reports configured collector risk or unknown settings, under the same persistent cooldown.</summary>
    /// <param name="observations">Effective observations for selected projects/frameworks.</param>
    internal async Task SettingsAsync(IEnumerable<CoverageArtifactSettingsResult> observations)
    {
        foreach (var observation in observations.Where(item => item.State is "enabled" or "unknown").Take(8))
        {
            var code = observation.State == "enabled" ? "ASCOV209" : "ASCOV211";
            var cause = observation.State == "enabled"
                ? "Effective settings enable XPlat coverage for ordinary test invocations; this is configured risk, not proof that the collector executed."
                : "Effective collector settings could not be established (" + CoverageArtifactDisplay.Bound(observation.Cause) + ").";
            await EmitAsync(new(code, "settings-inspection", cause,
                "Inspect effective settings and follow guides/coverage-storage.md#migrate-inherited-collector-settings; preserve required coverage configuration and gates."), code + ":" + observation.Project + ":" + observation.Framework + ":" + observation.Cause, 0).ConfigureAwait(false);
        }
    }

    /// <summary>Emits one trusted condition unless an equivalent persisted observation is still in cooldown.</summary>
    /// <param name="diagnostic">Bounded diagnostic supplied by an AppSurface observer.</param>
    /// <param name="condition">Internal condition fingerprint input; never emitted or used as a path.</param>
    /// <param name="bytes">Observed lower-bound bytes; repeated observations are not accumulated.</param>
    internal async Task EmitAsync(CoverageArtifactDiagnostic diagnostic, string condition, long bytes)
    {
        var conditionScope = condition == "domain-growth" ? "domain" : _scopeId;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(conditionScope + ":" + condition)));
        var now = clock.GetUtcNow();
        var emit = true;
        try
        {
            if (store is null) emit = Update(_local, key, bytes, now);
            else
            {
                using var staging = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(store.RootPath, "staging"), false, true)!;
                var statePath = Path.Join(staging.ArtifactDirectoryPath, "warnings");
                if (!Directory.Exists(statePath)) staging.CreateArtifactDirectory("warnings").Dispose();
                using var operation = CoverageArtifactOperationLease.Acquire(statePath, true);
                operation.VerifyBinding();
                using var state = CoverageRunOutputLease.AcquireArtifactDirectory(statePath, false, true)!;
                WarningState saved;
                try
                {
                    using var stream = state.OpenArtifactFile("state.json", false);
                    saved = CoverageArtifactJson.Read<WarningState>(stream, CoverageArtifactLimits.WarningStateMaxBytes, JsonOptions);
                }
                catch (FileNotFoundException) { saved = new(1, []); }
                if (saved.SchemaVersion != 1 || saved.Conditions is null || saved.Conditions.Count > 1000
                    || saved.Conditions.Any(item => item.Key.Length != 64 || item.Key.Any(character => !char.IsAsciiHexDigit(character)) || item.Value is null || item.Value.Bytes < 0))
                    throw new InvalidDataException("Warning state is invalid or unsupported.");
                emit = Update(saved.Conditions, key, bytes, now);
                if (emit)
                {
                    var encoded = JsonSerializer.SerializeToUtf8Bytes(saved, JsonOptions);
                    if (encoded.Length > CoverageArtifactLimits.WarningStateMaxBytes) throw new InvalidDataException("Warning state exceeds its byte limit.");
                    CoverageArtifactMetadataPublication.Write(state, "state.json", encoded,
                        CoverageArtifactLimits.WarningStateMaxBytes);
                }
            }
        }
        catch (Exception exception) when (IsSecondary(exception))
        {
            // Failure to save cooldown state must not suppress a useful growth warning.
            await CoverageArtifactDiagnostic.Failure("ASCOV201", "warning-state", exception).WriteAsync(errors).ConfigureAwait(false);
        }
        if (emit) await diagnostic.WriteAsync(errors).ConfigureAwait(false);
    }

    private bool Update(Dictionary<string, WarningCondition> conditions, string key, long bytes, DateTimeOffset now)
    {
        if (conditions.TryGetValue(key, out var prior) && now - prior.ObservedAtUtc < TimeSpan.FromHours(_policy.WarningCooldownHours)
            && (bytes - prior.Bytes < 256L * 1024 * 1024 || bytes < prior.Bytes * 1.25)) return false;
        foreach (var old in conditions.Where(item => now - item.Value.ObservedAtUtc > TimeSpan.FromDays(7)).Select(item => item.Key).ToArray())
            conditions.Remove(old);
        while (conditions.Count >= 1000 && !conditions.ContainsKey(key))
            conditions.Remove(conditions.MinBy(item => item.Value.ObservedAtUtc).Key);
        conditions[key] = new(bytes, now);
        return true;
    }

    private static bool IsSecondary(Exception exception) => CoverageArtifactSession.IsOptionalFailure(exception) || exception is CoverageExecutionException or OperationCanceledException;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 16 };
    private sealed record WarningCondition(long Bytes, DateTimeOffset ObservedAtUtc);
    private sealed record WarningState(int SchemaVersion, Dictionary<string, WarningCondition> Conditions);
}
