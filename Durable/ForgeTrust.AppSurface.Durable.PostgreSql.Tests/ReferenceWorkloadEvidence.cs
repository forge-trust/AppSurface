using System.Collections.Frozen;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ForgeTrust.AppSurface.Durable.Tests.Support;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

internal sealed class ReferenceWorkloadEvidence
{
    internal const string DirectoryEnvironmentVariable = "APPSURFACE_POSTGRES_REFERENCE_EVIDENCE_DIRECTORY";
    internal const string ModeEnvironmentVariable = "APPSURFACE_POSTGRES_REFERENCE_EVIDENCE_MODE";
    internal const string RunIdEnvironmentVariable = "APPSURFACE_POSTGRES_REFERENCE_EVIDENCE_RUN_ID";

    private static readonly FrozenSet<string> AllowedScenarios = new[]
    {
        "caller-owned-transaction",
        "operator-disable-scope",
        "process-loss-idempotent",
        "process-loss-providerkeyed",
        "process-loss-reconcilebeforeretry",
        "process-loss-manualresolution",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> AllowedCategories = new[]
    {
        "application", "child-process", "operator", "provider", "recovery", "transaction",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> AllowedOperations = new[]
    {
        "domain-mutation-and-work-acceptance", "effect-permit.acquire", "process.force-terminate",
        "provider.invoke", "schema.apply-and-activate", "scope.disable", "work.accept", "work.claim",
        "work.complete", "work.discover", "work.reclaim-after-lease-expiry",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> AllowedOutcomes = new[]
    {
        "applied-and-audited", "committed", "compatible", "completed", "found", "reclaimed", "rolled-back",
        "scope-disabled", "suspended-by-provider-safety", "terminated-after-permit",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> AllowedTransactionBoundaries = new[]
    {
        "caller-owned", "migration-owner", "no-database-transaction", "none", "provider-owned", "read-only",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> AllowedFinalStates = new[]
    {
        "accepted", "disabled", "succeeded", "suspended_manual_resolution", "suspended_reconciliation_required",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> AllowedModes = new[] { "cold", "warm" }
        .ToFrozenSet(StringComparer.Ordinal);

    private static readonly Regex RunIdPattern = new(
        "^[0-9]{8}T[0-9]{6}Z-[1-9][0-9]{0,9}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly List<ReferenceWorkloadEvidenceEvent> _events = [];
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private readonly string _scenario;

    internal ReferenceWorkloadEvidence(string scenario)
    {
        _scenario = RequireAllowed(scenario, AllowedScenarios, nameof(scenario));
    }

    internal void Record(
        string category,
        string operation,
        string outcome,
        string transactionBoundary,
        long? sourceElapsedMilliseconds = null)
    {
        _events.Add(new ReferenceWorkloadEvidenceEvent(
            _events.Count + 1,
            RequireAllowed(category, AllowedCategories, nameof(category)),
            RequireAllowed(operation, AllowedOperations, nameof(operation)),
            RequireAllowed(outcome, AllowedOutcomes, nameof(outcome)),
            RequireAllowed(transactionBoundary, AllowedTransactionBoundaries, nameof(transactionBoundary)),
            _stopwatch.ElapsedMilliseconds,
            sourceElapsedMilliseconds));
    }

    internal async ValueTask<string?> WriteAsync(string finalState, CancellationToken cancellationToken = default)
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var mode = RequireAllowed(
            Environment.GetEnvironmentVariable(ModeEnvironmentVariable),
            AllowedModes,
            ModeEnvironmentVariable);
        var runId = Environment.GetEnvironmentVariable(RunIdEnvironmentVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId, RunIdEnvironmentVariable);
        if (!RunIdPattern.IsMatch(runId))
        {
            throw new ArgumentException(
                "The reference-evidence run ID must use the UTC timestamp and process ID format yyyyMMddTHHmmssZ-pid.",
                RunIdEnvironmentVariable);
        }

        finalState = RequireAllowed(finalState, AllowedFinalStates, nameof(finalState));
        Directory.CreateDirectory(directory);
        var document = new ReferenceWorkloadEvidenceDocument(
            1,
            runId,
            _scenario,
            mode,
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPSURFACE_POSTGRES_TEST_CONNECTION"))
                ? PostgreSqlTestContainerImage.Reference
                : "external-postgresql-17.5",
            _startedAtUtc,
            _stopwatch.ElapsedMilliseconds,
            finalState,
            _events.ToArray());
        var path = TestPathUtils.PathUnder(directory, $"{_scenario}.json");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(
            stream,
            document,
            new JsonSerializerOptions { WriteIndented = true },
            cancellationToken);
        await stream.FlushAsync(cancellationToken);
        return path;
    }

    private static string RequireAllowed(string? value, FrozenSet<string> allowed, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!allowed.Contains(value))
        {
            throw new ArgumentException($"'{value}' is not an allowlisted reference-evidence value.", parameterName);
        }

        return value;
    }
}

internal sealed record ReferenceWorkloadEvidenceDocument(
    int SchemaVersion,
    string RunId,
    string Scenario,
    string Mode,
    string DatabaseSource,
    DateTimeOffset StartedAtUtc,
    long ElapsedMilliseconds,
    string FinalState,
    IReadOnlyList<ReferenceWorkloadEvidenceEvent> Events);

internal sealed record ReferenceWorkloadEvidenceEvent(
    int Sequence,
    string Category,
    string Operation,
    string Outcome,
    string TransactionBoundary,
    long ElapsedMilliseconds,
    long? SourceElapsedMilliseconds);
