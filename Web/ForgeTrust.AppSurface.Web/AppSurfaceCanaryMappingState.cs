namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Coordinates the process-wide single mapping claim and startup validator activation.
/// </summary>
internal sealed class AppSurfaceCanaryMappingState
{
    private ICollection<Microsoft.AspNetCore.Routing.EndpointDataSource>? _dataSources;

    /// <summary>
    /// Gets the endpoint data-source collection captured by the first successful mapping claim, or
    /// <see langword="null"/> before named canaries are mapped.
    /// </summary>
    internal ICollection<Microsoft.AspNetCore.Routing.EndpointDataSource>? DataSources =>
        Volatile.Read(ref _dataSources);

    /// <summary>
    /// Atomically claims named-canary mapping for one endpoint data-source collection.
    /// </summary>
    /// <param name="dataSources">The application-root data sources inspected by the startup validator.</param>
    /// <returns>
    /// <see langword="true"/> for the first claim; otherwise <see langword="false"/> without replacing the collection
    /// captured by the first caller.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="dataSources"/> is <see langword="null"/>.</exception>
    internal bool TryClaim(ICollection<Microsoft.AspNetCore.Routing.EndpointDataSource> dataSources)
    {
        ArgumentNullException.ThrowIfNull(dataSources);
        return Interlocked.CompareExchange(ref _dataSources, dataSources, null) is null;
    }
}
