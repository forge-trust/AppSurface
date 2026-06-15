using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>
/// Registers the AppSurface LocalSecrets provider and supporting services.
/// </summary>
/// <remarks>
/// Add this module only when the application wants fail-closed local secret posture. Environment variables keep the
/// highest precedence, LocalSecrets sits above file configuration, and only true missing local secrets fall through.
/// </remarks>
public sealed class AppSurfaceLocalSecretsModule : IAppSurfaceModule
{
    /// <summary>
    /// Registers LocalSecrets services.
    /// </summary>
    /// <param name="context">Startup context for the current app.</param>
    /// <param name="services">Service collection that receives LocalSecrets registrations.</param>
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddOptions<AppSurfaceLocalSecretsOptions>();
        services.AddSingleton<AppSurfaceLocalSecretIdentityNormalizer>();
        services.TryAddSingleton<IAppSurfaceLocalSecretStore, PlatformAppSurfaceLocalSecretStore>();
        services.AddSingleton<AppSurfaceLocalSecretProvider>();
        services.AddSingleton<IConfigProvider>(sp => sp.GetRequiredService<AppSurfaceLocalSecretProvider>());
    }

    /// <inheritdoc />
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        builder.AddModule<AppSurfaceConfigModule>();
    }
}
