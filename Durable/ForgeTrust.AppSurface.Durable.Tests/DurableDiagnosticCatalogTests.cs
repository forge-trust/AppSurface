using ForgeTrust.AppSurface.Durable;

namespace ForgeTrust.AppSurface.Durable.Tests;

public sealed class DurableDiagnosticCatalogTests
{
    private const string DocumentationBase =
        "https://github.com/forge-trust/AppSurface/blob/main/troubleshooting/durable-diagnostics.md#";

    public static TheoryData<string, string, string, string, string> CanonicalDiagnostics =>
        new()
        {
            {
                DurableProblemCodes.StoreUnavailable,
                "Store unavailable",
                "A PostgreSQL transport, permission, session-affinity, timeout, or cleanup failure prevented the bounded durable operation from completing.",
                "Restore PostgreSQL connectivity and read permissions, establish session affinity, or unblock the cooperative maintenance fence; retry only under application policy.",
                "asdur103-store-unavailable"
            },
            {
                DurableProblemCodes.RecoveryEpochRequired,
                "Recovery epoch required",
                "The configured runtime epoch differs from the active store epoch.",
                "Perform authorized epoch initialization or rotation before enabling the worker host.",
                "asdur108-recovery-epoch-required"
            },
            {
                DurableProblemCodes.SchemaMissing,
                "Durable schema is missing",
                "The Durable schema or migration history is not installed.",
                "Apply reviewed forward-only migrations with a migration-owner connection.",
                "asdur400-durable-schema-is-missing"
            },
            {
                DurableProblemCodes.SchemaUpgradeRequired,
                "Durable schema upgrade is required",
                "Known migrations are pending in the installed Durable schema.",
                "Apply every known pending migration before this reader or writer.",
                "asdur401-durable-schema-upgrade-is-required"
            },
            {
                DurableProblemCodes.SchemaVersionUnsupported,
                "Durable schema version is too new or unsupported",
                "The installed reader or writer range excludes this package.",
                "Deploy compatible package code; do not bypass supported ranges.",
                "asdur402-durable-schema-version-is-too-new-or-unsupported"
            },
            {
                DurableProblemCodes.SchemaInconsistent,
                "Durable schema history is inconsistent",
                "Recorded migration names, checksums, order, or metadata do not match the expected schema history.",
                "Compare ordered names and checksums; never rewrite applied history.",
                "asdur403-durable-schema-history-is-inconsistent"
            },
            {
                DurableProblemCodes.ActivatorStale,
                "Initial heartbeat not observed or activator stale",
                "NotStarted may mean the first worker heartbeat is absent; Stale means the observed heartbeat or sweep exceeded HeartbeatStaleAfter.",
                "For NotStarted, treat it as a compatible initial assessment and follow the host's activation policy. For Stale, inspect the configured runtime and role/schema prerequisites.",
                "asdur404-initial-heartbeat-not-observed-or-activator-stale"
            },
            {
                DurableProblemCodes.RestrictedRuntimeCredentialRequired,
                "Restricted runtime credential required",
                "The connected role has a prohibited attribute, membership, ownership, grant option, or heartbeat-table privilege.",
                "Use a dedicated restricted LOGIN runtime credential and complete the reviewed runtime-role preflight; do not use a migration owner.",
                "asdur408-restricted-runtime-credential-required"
            },
            {
                DurableProblemCodes.HeartbeatRetentionUnavailable,
                "Heartbeat-retention capability unavailable",
                "The retention function, its required permissions or configuration, or the retention index is missing or does not meet the schema 0011 contract.",
                "Review the schema 0011 retention function and index contract, apply an authorized repair, then rerun doctor and complete runtime preflight.",
                "asdur409-heartbeat-retention-capability-unavailable"
            },
            {
                DurableProblemCodes.WorkerHeartbeatMissing,
                "No retained heartbeat for the selected worker",
                "No retained heartbeat matches the selected worker ID; the worker may not have started, the ID may differ, or retention may have removed the row.",
                "Verify the configured worker ID and host activation policy; rerun doctor after the worker records a heartbeat.",
                "asdur410-no-retained-heartbeat-for-the-selected-worker"
            },
            {
                DurableProblemCodes.WorkerDraining,
                "Selected worker is draining",
                "The selected worker heartbeat records that the worker is refusing new passes while existing work drains.",
                "Let in-flight work finish and follow the host's drain or deployment procedure before expecting new claims.",
                "asdur411-selected-worker-is-draining"
            },
            {
                DurableProblemCodes.WorkerHeartbeatEpochMismatch,
                "Selected heartbeat belongs to another runtime epoch",
                "The retained heartbeat's runtime epoch does not match the configured and active store epoch.",
                "Resolve the authorized runtime-epoch mismatch, then rerun doctor with the host's configured epoch.",
                "asdur412-selected-heartbeat-belongs-to-another-runtime-epoch"
            },
            {
                DurableProblemCodes.DoctorInputInvalid,
                "Doctor input is invalid",
                "A command option or selected environment value is absent or malformed.",
                "Provide required values through the selected environment variables, use a restricted runtime credential, and check supported options with appsurface durable doctor --help.",
                "asdur413-doctor-input-is-invalid"
            },
            {
                DurableProblemCodes.DoctorCanceled,
                "Doctor was canceled by its caller",
                "The caller canceled the valid doctor request before it completed.",
                "Retry only when the caller intends a fresh diagnostic attempt.",
                "asdur414-doctor-was-canceled-by-its-caller"
            },
            {
                DurableProblemCodes.DoctorContractFailed,
                "Doctor encountered an unexpected contract failure",
                "The CLI or provider returned malformed, contradictory, or otherwise unexpected doctor evidence.",
                "Verify the matching CLI and provider packages, investigate their contract compatibility and the canonical troubleshooting diagnostics, and retry only after resolving the mismatch.",
                "asdur415-doctor-encountered-an-unexpected-contract-failure"
            },
        };

    [Theory]
    [MemberData(nameof(CanonicalDiagnostics))]
    public void TryGet_returns_stable_canonical_diagnostic(
        string code,
        string problem,
        string cause,
        string fix,
        string documentationAnchor)
    {
        Assert.True(DurableDiagnosticCatalog.TryGet(code, out var descriptor));
        Assert.NotNull(descriptor);
        Assert.Equal(code, descriptor.Code);
        Assert.Equal(problem, descriptor.Problem);
        Assert.Equal(cause, descriptor.Cause);
        Assert.Equal(fix, descriptor.Fix);
        Assert.Equal(DocumentationBase + documentationAnchor, descriptor.DocumentationUrl.AbsoluteUri);
        Assert.Same(descriptor, GetDescriptor(code));
    }

    [Fact]
    public void TryGet_leaves_unknown_codes_unclassified_and_rejects_null()
    {
        foreach (var code in new[] { "APP123", "ASDUR999", "asdur103" })
        {
            Assert.False(DurableDiagnosticCatalog.TryGet(code, out var descriptor));
            Assert.Null(descriptor);
        }

        Assert.Throws<ArgumentNullException>(() => DurableDiagnosticCatalog.TryGet(null!, out _));
    }

    [Fact]
    public void Canonical_descriptors_can_populate_DurableProblem_without_replacing_caller_correlation()
    {
        var descriptor = GetDescriptor(DurableProblemCodes.RecoveryEpochRequired);
        var problem = new DurableProblem(
            descriptor.Code,
            descriptor.Problem,
            descriptor.Cause,
            descriptor.Fix,
            descriptor.DocumentationUrl,
            "caller-correlation-801");

        Assert.Equal(descriptor.Code, problem.Code);
        Assert.Equal(descriptor.Problem, problem.Problem);
        Assert.Equal(descriptor.Cause, problem.Cause);
        Assert.Equal(descriptor.Fix, problem.Fix);
        Assert.Equal(descriptor.DocumentationUrl, problem.DocumentationUrl);
        Assert.Equal("caller-correlation-801", problem.CorrelationId);
    }

    [Fact]
    public void Troubleshooting_anchors_and_canonical_words_match_the_catalog()
    {
        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var guide = File.ReadAllText(TestPathUtils.PathUnder(repositoryRoot, "troubleshooting", "durable-diagnostics.md"));

        foreach (var row in CanonicalDiagnostics)
        {
            var code = (string)row[0];
            var problem = (string)row[1];
            var cause = (string)row[2];
            var fix = (string)row[3];
            var documentationAnchor = (string)row[4];
            var descriptor = GetDescriptor(code);
            Assert.Equal(problem, descriptor.Problem);
            Assert.Equal(cause, descriptor.Cause);
            Assert.Equal(fix, descriptor.Fix);
            Assert.Equal(documentationAnchor, descriptor.DocumentationUrl.Fragment[1..]);
            Assert.Contains($"### {code} {problem}", guide, StringComparison.Ordinal);
            Assert.Contains($"Problem: {problem}", guide, StringComparison.Ordinal);
            Assert.Contains($"Cause: {cause}", guide, StringComparison.Ordinal);
            Assert.Contains($"Fix: {fix}", guide, StringComparison.Ordinal);
            Assert.Contains($"](#{documentationAnchor})", guide, StringComparison.Ordinal);
        }
    }

    private static DurableDiagnosticDescriptor GetDescriptor(string code)
    {
        Assert.True(DurableDiagnosticCatalog.TryGet(code, out var descriptor));
        return Assert.IsType<DurableDiagnosticDescriptor>(descriptor);
    }
}
