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
    internal const int MaximumContractCount = 10_000;
    private readonly DurableWorkContractIdentity[] _contracts;
    private readonly string[] _workNames;
    private readonly string[] _workVersions;

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
            throw CreateUnavailableException();
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            throw CreateUnavailableException(exception);
        }

        if (registeredContracts is null)
        {
            throw CreateUnavailableException();
        }

        try
        {
            _contracts = registeredContracts.ToArray();
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            throw CreateUnavailableException(exception);
        }

        if (_contracts.Length > MaximumContractCount || _contracts.Any(static contract => contract.IsDefault))
        {
            throw CreateUnavailableException();
        }

        Array.Sort(_contracts, CompareOrdinally);
        for (var index = 1; index < _contracts.Length; index++)
        {
            if (CompareOrdinally(_contracts[index - 1], _contracts[index]) == 0)
            {
                throw CreateUnavailableException();
            }
        }

        _workNames = _contracts.Select(static contract => contract.WorkName).ToArray();
        _workVersions = _contracts.Select(static contract => contract.WorkVersion).ToArray();
    }

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

    private static InvalidOperationException CreateUnavailableException(Exception? innerException = null) =>
        new(
            $"{DurableProblemCodes.WorkDiscoveryContractSelectionUnavailable}: PostgreSQL durable activation requires IDurableWorkRegistry.RegisteredContracts to return one complete exact Work name/version snapshot.",
            innerException);
}
