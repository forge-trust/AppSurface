using Npgsql;
using NpgsqlTypes;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>
/// An immutable, provider-owned snapshot of the Work contracts that a host may discover.
/// </summary>
/// <remarks>
/// This selection is created while the runtime pump is resolved. It intentionally never rereads a custom registry,
/// so later registry mutation cannot widen a running host's discovery authority.
/// </remarks>
internal sealed class PostgreSqlDurableWorkContractSelection
{
    /// <summary>
    /// The largest Work-contract count accepted by <c>appsurface_durable.discover_work_dispatch</c>.
    /// </summary>
    /// <remarks>
    /// This value must stay aligned with the bound in <c>Migrations/0009_work_contract_discovery.sql</c>.
    /// </remarks>
    internal const int MaximumContractCount = 10_000;
    private readonly DurableWorkContractIdentity[] _contracts;
    private readonly string[] _workNames;
    private readonly string[] _workVersions;

    /// <summary>
    /// Snapshots registered Work contracts once and validates them for bounded discovery.
    /// </summary>
    /// <param name="registry">Registry that reports the contracts this host may discover.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="registry"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown with <see cref="DurableProblemCodes.WorkDiscoveryContractSelectionUnavailable"/> when the registry
    /// cannot produce one complete, valid snapshot.
    /// </exception>
    /// <remarks>
    /// Construct the selection during activation, before the pump runs. It is not reread, so later registry mutation
    /// cannot widen a running host's discovery authority.
    /// </remarks>
    internal PostgreSqlDurableWorkContractSelection(IDurableWorkRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        IReadOnlyList<DurableWorkContractIdentity> registeredContracts;
        try
        {
            registeredContracts = registry.RegisteredContracts;
        }
        catch (NotSupportedException)
        {
            throw CreateUnavailableException("The registry does not expose a contract snapshot.");
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            throw CreateUnavailableException("The registry threw while producing its contract snapshot.", exception);
        }

        if (registeredContracts is null)
        {
            throw CreateUnavailableException("The registry returned a null contract snapshot.");
        }

        try
        {
            _contracts = registeredContracts.ToArray();
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            throw CreateUnavailableException("The registry contract snapshot could not be enumerated.", exception);
        }

        if (_contracts.Length > MaximumContractCount)
        {
            throw CreateUnavailableException("The registry reported more contracts than the supported maximum.");
        }

        if (_contracts.Any(static contract => contract.IsDefault))
        {
            throw CreateUnavailableException("The registry reported a default Work contract identity.");
        }

        Array.Sort(_contracts, CompareOrdinally);
        for (var index = 1; index < _contracts.Length; index++)
        {
            if (CompareOrdinally(_contracts[index - 1], _contracts[index]) == 0)
            {
                throw CreateUnavailableException("The registry reported a duplicate Work name and version pair.");
            }
        }

        _workNames = _contracts.Select(static contract => contract.WorkName).ToArray();
        _workVersions = _contracts.Select(static contract => contract.WorkVersion).ToArray();
    }

    /// <summary>
    /// Gets whether this host registered no Work contracts.
    /// </summary>
    /// <remarks>
    /// Callers use this fail-closed gate to avoid invoking discovery with an empty contract array, which PostgreSQL
    /// rejects.
    /// </remarks>
    internal bool IsEmpty => _contracts.Length == 0;

    /// <summary>
    /// Adds the immutable selection arrays to a Work-discovery command without rebuilding them for every poll.
    /// </summary>
    internal void AddDiscoveryParameters(NpgsqlParameterCollection parameters, int maximumCandidates)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.AddWithValue("work_names", NpgsqlDbType.Array | NpgsqlDbType.Text, _workNames);
        parameters.AddWithValue("work_versions", NpgsqlDbType.Array | NpgsqlDbType.Text, _workVersions);
        parameters.AddWithValue("maximum_candidates", maximumCandidates);
    }

    private static int CompareOrdinally(DurableWorkContractIdentity left, DurableWorkContractIdentity right)
    {
        var nameComparison = StringComparer.Ordinal.Compare(left.WorkName, right.WorkName);
        return nameComparison != 0
            ? nameComparison
            : StringComparer.Ordinal.Compare(left.WorkVersion, right.WorkVersion);
    }

    private static InvalidOperationException CreateUnavailableException(
        string reason,
        Exception? innerException = null) =>
        new(
            $"{DurableProblemCodes.WorkDiscoveryContractSelectionUnavailable}: PostgreSQL durable activation requires IDurableWorkRegistry.RegisteredContracts to return one complete exact Work name/version snapshot. {reason}",
            innerException);
}
