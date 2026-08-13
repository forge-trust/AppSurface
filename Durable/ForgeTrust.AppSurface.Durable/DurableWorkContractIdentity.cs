namespace ForgeTrust.AppSurface.Durable;

/// <summary>
/// Identity pair for one durable work contract.
/// </summary>
public readonly record struct DurableWorkContractIdentity
{
    /// <summary>
    /// Initializes a work contract identity.
    /// </summary>
    /// <param name="workName">Work contract name.</param>
    /// <param name="workVersion">Work contract version.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="workName"/> or <paramref name="workVersion"/> is invalid.</exception>
    public DurableWorkContractIdentity(string workName, string workVersion)
    {
        WorkName = DurableIdentifier.Require(workName, nameof(workName), 200);
        WorkVersion = DurableIdentifier.Require(workVersion, nameof(workVersion), 100);
    }

    /// <summary>
    /// Gets whether this value is the default struct value.
    /// </summary>
    public bool IsDefault => WorkName == default && WorkVersion == default;

    /// <summary>
    /// Gets the work contract name.
    /// </summary>
    public string WorkName { get; }

    /// <summary>
    /// Gets the work contract version.
    /// </summary>
    public string WorkVersion { get; }
}
