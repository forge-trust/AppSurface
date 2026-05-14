namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Defines the central manager for configuration, which aggregates multiple <see cref="IConfigProvider"/> instances.
/// </summary>
public interface IConfigManager : IConfigProvider
{
}
