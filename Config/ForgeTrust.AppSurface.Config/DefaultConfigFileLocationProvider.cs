namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Default implementation of <see cref="IConfigFileLocationProvider"/> that uses the application's base directory.
/// </summary>
/// <remarks>
/// <see cref="DefaultConfigFileLocationProvider"/> returns <see cref="AppContext.BaseDirectory"/> from
/// <see cref="Directory"/>. This is appropriate for simple apps that keep configuration beside application binaries.
/// Use a custom <see cref="IConfigFileLocationProvider"/> for environment-specific, user-scoped, container-mounted, or
/// service-hosted configuration. Pitfall: <see cref="AppContext.BaseDirectory"/> is not necessarily
/// <see cref="System.IO.Directory.GetCurrentDirectory"/> and can vary by deployment model, test runner, or host.
/// </remarks>
public class DefaultConfigFileLocationProvider : IConfigFileLocationProvider
{
    /// <inheritdoc />
    public string Directory { get; } = AppContext.BaseDirectory;
}
