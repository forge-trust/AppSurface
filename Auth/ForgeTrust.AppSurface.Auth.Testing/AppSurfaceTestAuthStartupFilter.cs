using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace ForgeTrust.AppSurface.Auth.Testing;

internal sealed class AppSurfaceTestAuthStartupFilter : IStartupFilter
{
    private readonly IWebHostEnvironment _environment;
    private readonly AppSurfaceTestAuthOptions _options;

    public AppSurfaceTestAuthStartupFilter(IWebHostEnvironment environment, AppSurfaceTestAuthOptions options)
    {
        _environment = environment;
        _options = options;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            if (!_options.AllowProductionEnvironmentForTestHost && IsProductionLike(_environment.EnvironmentName))
            {
                throw new InvalidOperationException(
                    $"Problem: AppSurface test auth cannot start in environment '{_environment.EnvironmentName}'. Cause: test authentication is blocked outside Development, Test, or Testing by default. Fix: run the test host with a test environment or set AllowProductionEnvironmentForTestHost only for isolated production-like integration tests. Docs: Auth/ForgeTrust.AppSurface.Auth.Testing/README.md. Code: {AppSurfaceTestAuthDiagnosticCodes.ProductionEnvironmentBlocked}.");
            }

            next(app);
        };
    }

    private static bool IsProductionLike(string? environmentName)
    {
        return !string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(environmentName, "Test", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase);
    }
}
