using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeTrust.AppSurface.Caching;

/// <summary>
/// A AppSurface module that registers the <see cref="IMemo"/> caching services.
/// </summary>
public class AppSurfaceCachingModule : IAppSurfaceModule
{
    /// <inheritdoc />
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddMemoryCache();
        services.TryAddSingleton<IMemo, Memo>();
    }

    /// <inheritdoc />
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }
}
