using ForgeTrust.AppSurface.Core;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Defines a configuration provider that also provides environment information.
/// </summary>
public interface IEnvironmentConfigProvider : IConfigProvider, IEnvironmentProvider
{
}
