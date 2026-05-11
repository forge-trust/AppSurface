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

    // TODO: Need to implement this upstream: https://github.com/Tyrrrz/CliFx/issues/39
    // It seems we need to be able to pass args upstream or this will cause
    // problems trying to use deployments in Aspire.
    /// <summary>
    /// Gets the command-line arguments to pass through to the Aspire host.
    /// </summary>
    public string[] PassThroughArgs => [];

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

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IConsole console)
    {
        var appBuilder = new DistributedApplicationBuilder(PassThroughArgs);
        var context = new AspireStartupContext(appBuilder);

        foreach (var profile in GetDependencies())
        {
            foreach (var component in profile.GetComponents())
            {
                if (component is IAspireComponent<IResource> typedComponent)
                {
                    context.Resolve(typedComponent);
                }
            }
        }

        foreach (var component in GetComponents())
        {
            if (component is IAspireComponent<IResource> typedComponent)
            {
                context.Resolve(typedComponent);
            }
        }

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
}
