using ForgeTrust.AppSurface.Flow.DurableTask;

namespace ProductReadinessLab;

/// <summary>
/// Builds product-readiness reports from the lab's local probes.
/// </summary>
internal sealed class ProductReadinessReportService
{
    private readonly IProductStateStore _store;
    private readonly ProductApprovalInProcessHost _host;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a report service.
    /// </summary>
    /// <param name="store">Product state store used by the lab.</param>
    /// <param name="host">In-process workflow host used by the lab.</param>
    /// <param name="timeProvider">Time provider used for deterministic report timestamps.</param>
    public ProductReadinessReportService(
        IProductStateStore store,
        ProductApprovalInProcessHost host,
        TimeProvider? timeProvider = null)
    {
        _store = store;
        _host = host;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Builds a report.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The generated report.</returns>
    public async Task<ReadinessReport> BuildAsync(CancellationToken cancellationToken = default)
    {
        var rows = new List<ReadinessRow>
        {
            new(
                "startup-routing",
                ReadinessStatus.ProvenLocally,
                "AppSurface Web maps the lab root, readiness, auth, and workflow endpoints.",
                "None.",
                "The web app runs through AppSurface Web endpoint composition.",
                "Run the lab and request /readiness.md.",
                "Copy the endpoint/module pattern when it names real startup policy."),
            new(
                "config-summary",
                ReadinessStatus.ProvenLocally,
                "The report uses allowlisted labels and does not print raw provider names, paths, environment variable names, or values.",
                "None.",
                "Configuration evidence is summarized for evaluator safety.",
                "Add product-specific allowlisted labels when new config proofs are added.",
                "Copy the allowlist approach, not raw config dumps."),
            new(
                "proof-auth",
                ReadinessStatus.UnsafeToCopy,
                "Development/local proof auth maps X-Proof-User users into AppSurface Auth.AspNetCore policy outcomes.",
                "Proof headers are impersonation primitives outside the lab.",
                "The sample exists to avoid requiring cookies, OIDC, or Identity for evaluation.",
                "Use real ASP.NET Core authentication handlers and policies in product apps.",
                "Do not copy X-Proof-User authentication into production."),
        };

        rows.Add(await BuildWorkflowRowAsync(cancellationToken));
        rows.Add(await BuildPostgresRowAsync(cancellationToken));
        rows.Add(BuildInProcessHostShapeRow());
        rows.Add(BuildDurableTaskBoundaryRow());
        rows.Add(new(
            "deployment",
            ReadinessStatus.Deferred,
            "The lab intentionally stops at local proof.",
            "No cloud deployment or production hosting is provided here.",
            "Deployment shape is application and platform specific.",
            "Add a deployment lab later that consumes the same report row model.",
            "Do not treat this lab as deployment guidance."));
        rows.Add(new(
            "secrets",
            ReadinessStatus.HostOwned,
            "No production secret store is configured by AppSurface.",
            "Local proof configuration is not a rotation or vaulting story.",
            "Secret management belongs to the host platform and application.",
            "Use your production secret manager and keep values out of report rows.",
            "Copy the no-secret-output rule, not local proof values."));

        return new ReadinessReport(_timeProvider.GetUtcNow(), rows);
    }

    private async Task<ReadinessRow> BuildWorkflowRowAsync(CancellationToken cancellationToken)
    {
        try
        {
            var probe = await _host.ProbeAsync(cancellationToken);
            return new ReadinessRow(
                "workflow",
                ReadinessStatus.ProvenLocally,
                $"In-process host produced {probe.WaitingStatus} then {probe.CompletedStatus}; timeout={probe.TimeoutMinutes:0}m; late={probe.LateEventStatus}; fault={probe.FaultCode}.",
                "None.",
                "AppSurface Flow and DurableTask-facing decisions are exercised inside the app process.",
                "Use the host-shape section when replacing the in-process loop with a real Durable Task host.",
                "Copy the decision mapping pattern; choose your own production host/backend.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ReadinessRow(
                "workflow",
                ReadinessStatus.Blocked,
                "The in-process workflow probe did not complete.",
                "Workflow proof failed.",
                exception.GetType().Name,
                "Run the focused lab tests and inspect the workflow probe.",
                "Do not copy a failing workflow proof.");
        }
    }

    private async Task<ReadinessRow> BuildPostgresRowAsync(CancellationToken cancellationToken)
    {
        var probe = await _store.ProbeAsync(cancellationToken);
        if (probe.Succeeded && probe.IsPostgresBacked)
        {
            return new ReadinessRow(
                "postgres-product-state",
                ReadinessStatus.ProvenLocally,
                "Product/domain state was persisted through the configured Postgres connection.",
                "None.",
                "The Aspire AppHost supplies a Postgres database to the web app.",
                "Use migrations and production connection policy in your application.",
                "Copy the boundary: Postgres stores product state, not DurableTask orchestration state.");
        }

        return new ReadinessRow(
            "postgres-product-state",
            ReadinessStatus.Blocked,
            probe.Succeeded
                ? "The lab used an in-process fallback store because no Postgres connection was configured."
                : "The configured Postgres product-state probe failed.",
            "Postgres product-state proof is not complete.",
            probe.SafeDiagnostic,
            "Run the Aspire AppHost local profile or set ConnectionStrings:ProductReadiness.",
            "Do not describe product-state persistence as orchestration storage.");
    }

    private static ReadinessRow BuildInProcessHostShapeRow() =>
        new(
            "in-process-host-shape",
            ReadinessStatus.ProvenLocally,
            "The lab host starts a flow, schedules the next node, waits for approval-submitted, maps timeout, maps fault, and ignores late events.",
            "None.",
            "The evaluator host loop lives in the web app process for clarity.",
            "Replace this loop with a real Durable Task worker/client when production durability is required.",
            "Copy the host responsibilities list; do not copy this as a durability guarantee.");

    private static ReadinessRow BuildDurableTaskBoundaryRow() =>
        new(
            "durabletask-backend-boundary",
            ReadinessStatus.HostOwned,
            "`IDurableTaskFlowRunner<TContext>` and `IDurableTaskFlowClient<TContext>` are registered; worker/client hosting and storage providers are not.",
            "Durable orchestration persistence is not proven by this lab.",
            "AppSurface.Flow.DurableTask is a passive adapter boundary.",
            "Choose and configure a Durable Task host/backend in the application.",
            "Copy the adapter boundary; do not claim AppSurface provides a backend.");
}
