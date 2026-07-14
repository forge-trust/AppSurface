namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Coordinates the process-wide single mapping claim and startup validator activation.
/// </summary>
internal sealed class AppSurfaceCanaryMappingState
{
    private ICollection<Microsoft.AspNetCore.Routing.EndpointDataSource>? _dataSources;

    internal ICollection<Microsoft.AspNetCore.Routing.EndpointDataSource>? DataSources =>
        Volatile.Read(ref _dataSources);

    internal bool TryClaim(ICollection<Microsoft.AspNetCore.Routing.EndpointDataSource> dataSources)
    {
        ArgumentNullException.ThrowIfNull(dataSources);
        return Interlocked.CompareExchange(ref _dataSources, dataSources, null) is null;
    }
}
