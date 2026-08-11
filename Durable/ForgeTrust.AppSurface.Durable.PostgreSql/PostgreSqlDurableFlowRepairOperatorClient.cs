using System.Diagnostics;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Durable.Provider;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Implements the preview, evidence-first Flow repair boundary for a scoped PostgreSQL durable runtime.</summary>
/// <remarks>
/// Applications authorize the trusted scope before this client is invoked. The client never reads a raw Work result
/// into its public assessment and never invokes an executor while applying a repair assertion.
/// </remarks>
public sealed class PostgreSqlDurableFlowRepairOperatorClient : IFlowRepairOperatorClient
{
    private readonly PostgreSqlDurableFlowStore _store;
    private readonly IDurableWorkRegistry _workRegistry;

    /// <summary>Initializes a Flow repair client without applying schema or starting background work.</summary>
    public PostgreSqlDurableFlowRepairOperatorClient(
        NpgsqlDataSource dataSource,
        IDurableWorkRegistry workRegistry,
        PostgreSqlDurableWorkOptions options)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _workRegistry = workRegistry ?? throw new ArgumentNullException(nameof(workRegistry));
        _store = new PostgreSqlDurableFlowStore(dataSource, options ?? throw new ArgumentNullException(nameof(options)));
    }

    /// <inheritdoc />
    public ValueTask<DurableOperationResult<DurableFlowRepairAssessment>> GetAssessmentAsync(
        DurableFlowRepairAssessmentRequest request,
        CancellationToken cancellationToken = default) =>
        _store.GetRepairAssessmentAsync(request, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<DurableOperationResult<DurableFlowRepairResult>> RepairAsync(
        DurableFlowRepairRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var activity = AppSurfaceActivitySources.Instance.StartActivity(
            "appsurface.durable.flow.repair",
            ActivityKind.Producer);
        var result = await _store.RepairAsync(request, _workRegistry, cancellationToken).ConfigureAwait(false);
        activity?.SetTag("appsurface.durable.repair.action", request.Action.ToString());
        activity?.SetTag("appsurface.durable.repair.outcome", result.Value?.Outcome.ToString() ?? "operation_failure");
        activity?.SetTag("appsurface.durable.repair.problem_code", result.Value?.Problem?.Code ?? result.Problem?.Code);
        return result;
    }
}
