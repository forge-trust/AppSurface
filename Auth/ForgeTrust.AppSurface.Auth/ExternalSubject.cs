namespace ForgeTrust.AppSurface.Auth;

/// <summary>
/// Describes an authenticated external subject before it has been resolved to an app-owned user id.
/// </summary>
/// <remarks>
/// The uniqueness key is the ordinal tuple of <see cref="Issuer"/>, <see cref="Subject"/>, and optional
/// <see cref="PartitionKey"/>. Use <see cref="PartitionKey"/> only for host-validated realm, tenant, client, or
/// environment context that is part of the subject namespace. AppSurface does not treat the partition as tenant
/// authority or authorization truth. The raw values are intentionally omitted from <see cref="ToString"/>.
/// </remarks>
public readonly struct ExternalSubject : IEquatable<ExternalSubject>
{
    /// <summary>
    /// Creates an external subject description.
    /// </summary>
    /// <param name="issuer">Stable issuer or identity-provider namespace. The value must be non-empty.</param>
    /// <param name="subject">Stable subject id inside the issuer namespace. The value must be non-empty.</param>
    /// <param name="partitionKey">
    /// Optional host-validated partition for issuers where subject ids are only unique within a realm, tenant, client,
    /// or environment. Null or whitespace values are normalized to <see langword="null"/>.
    /// </param>
    public ExternalSubject(string issuer, string subject, string? partitionKey = null)
    {
        Issuer = AppSurfaceAuthMetadata.RequireIdentifier(issuer, nameof(issuer));
        Subject = AppSurfaceAuthMetadata.RequireIdentifier(subject, nameof(subject));
        PartitionKey = AppSurfaceAuthMetadata.NormalizeOptionalText(partitionKey);
    }

    /// <summary>
    /// Gets the stable issuer or identity-provider namespace.
    /// </summary>
    public string Issuer { get; }

    /// <summary>
    /// Gets the stable subject id inside the issuer namespace.
    /// </summary>
    public string Subject { get; }

    /// <summary>
    /// Gets the optional host-validated partition that participates in the uniqueness key.
    /// </summary>
    public string? PartitionKey { get; }

    /// <inheritdoc />
    public bool Equals(ExternalSubject other)
    {
        return string.Equals(Issuer, other.Issuer, StringComparison.Ordinal)
            && string.Equals(Subject, other.Subject, StringComparison.Ordinal)
            && string.Equals(PartitionKey, other.PartitionKey, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is ExternalSubject other && Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(
            Issuer is null ? 0 : StringComparer.Ordinal.GetHashCode(Issuer),
            Subject is null ? 0 : StringComparer.Ordinal.GetHashCode(Subject),
            PartitionKey is null ? 0 : StringComparer.Ordinal.GetHashCode(PartitionKey));
    }

    /// <summary>
    /// Compares two external subjects with ordinal tuple semantics.
    /// </summary>
    /// <param name="left">The left subject.</param>
    /// <param name="right">The right subject.</param>
    /// <returns><see langword="true"/> when issuer, subject, and partition key match.</returns>
    public static bool operator ==(ExternalSubject left, ExternalSubject right)
    {
        return left.Equals(right);
    }

    /// <summary>
    /// Compares two external subjects with ordinal tuple semantics.
    /// </summary>
    /// <param name="left">The left subject.</param>
    /// <param name="right">The right subject.</param>
    /// <returns><see langword="true"/> when any tuple member differs.</returns>
    public static bool operator !=(ExternalSubject left, ExternalSubject right)
    {
        return !left.Equals(right);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var partitionState = PartitionKey is null ? "none" : "present";
        return $"ExternalSubject {{ Issuer = <redacted>, Subject = <redacted>, PartitionKey = {partitionState} }}";
    }

    internal void EnsureInitialized(string parameterName)
    {
        _ = AppSurfaceAuthMetadata.RequireIdentifier(Issuer, parameterName);
        _ = AppSurfaceAuthMetadata.RequireIdentifier(Subject, parameterName);
    }
}
