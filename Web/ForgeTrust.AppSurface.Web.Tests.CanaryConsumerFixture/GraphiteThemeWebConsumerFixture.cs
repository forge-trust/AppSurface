using ForgeTrust.AppSurface.Theming;
using ForgeTrust.AppSurface.Web;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Web.Tests.CanaryConsumerFixture;

/// <summary>
/// Provides the isolated Web-only consumer registration for the shared Graphite theme pair.
/// </summary>
/// <remarks>
/// This fixture intentionally owns the direct Web package reference. It does not reference AppSurface Docs so the
/// registration proves that a Web consumer can configure and render the shared pair independently.
/// </remarks>
public static class GraphiteThemeWebConsumerFixture
{
    /// <summary>
    /// Registers Graphite as the System-mode default theme and enables its Web document provider.
    /// </summary>
    /// <param name="services">The service collection receiving the Graphite Web registration.</param>
    /// <returns>The original <paramref name="services"/> instance.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> is <see langword="null"/>.
    /// </exception>
    public static IServiceCollection AddGraphiteThemeWebConsumer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services
            .AddAppSurfaceTheming(options =>
            {
                options.DefaultTheme = new AppSurfaceThemeId("graphite");
                options.DefaultMode = AppSurfaceThemeMode.System;
                options.Pairs.Add(AppSurfaceThemePair.Graphite());
            })
            .AddAppSurfaceWebTheming();
    }
}
