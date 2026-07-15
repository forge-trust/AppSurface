using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using CliFx;
using CliFx.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Aspire;

/// <summary>
/// A base class for defining an Aspire profile as a CLI command.
/// </summary>
public abstract class AspireProfile : ICommand
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AspireProfile"/> class.
    /// </summary>
    /// <param name="logger">The logger for the profile.</param>
    public AspireProfile(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the command-line arguments to pass through to the Aspire host.
    /// </summary>
    /// <remarks>
    /// Profiles default to an empty argument set so AppSurface command selection remains owned by CliFx. Override this
    /// property when a profile intentionally needs to pass known AppHost arguments into
    /// <see cref="DistributedApplicationBuilder"/>. Unknown command-line arguments are not forwarded automatically.
    /// </remarks>
    public virtual string[] PassThroughArgs => [];

    /// <summary>
    /// Gets the dependencies (other profiles) that this profile requires.
    /// </summary>
    /// <returns>An enumerable of dependent profiles.</returns>
    public virtual IEnumerable<AspireProfile> GetDependencies()
    {
        return [];
    }

    /// <summary>
    /// Gets the Aspire components that compose this profile.
    /// </summary>
    /// <returns>An enumerable of Aspire components.</returns>
    public abstract IEnumerable<IAspireComponent> GetComponents();

    /// <summary>
    /// Composes this profile into an Aspire distributed application builder.
    /// </summary>
    /// <param name="appBuilder">The builder that receives the profile graph.</param>
    /// <param name="cancellationToken">A token checked between synchronous composition steps.</param>
    internal void Compose(IDistributedApplicationBuilder appBuilder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(appBuilder);

        var context = new AspireStartupContext(appBuilder);

        foreach (var profile in GetDependencies())
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var component in profile.GetComponents())
            {
                cancellationToken.ThrowIfCancellationRequested();
                ResolveComponent(context, component);
            }
        }

        foreach (var component in GetComponents())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolveComponent(context, component);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IConsole console)
    {
        var appBuilder = DistributedApplication.CreateBuilder(PassThroughArgs);
        Compose(appBuilder, CancellationToken.None);

        try
        {
            var app = appBuilder.Build();
            await app.RunAsync();
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Error initializing Aspire application");
            Environment.ExitCode = -150;
        }
    }

    private static void ResolveComponent(AspireStartupContext context, IAspireComponent component)
    {
        if (component is IAspireComponent<IResource> typedComponent)
        {
            context.Resolve(typedComponent);
        }
    }
}
