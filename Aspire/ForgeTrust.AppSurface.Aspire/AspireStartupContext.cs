using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace ForgeTrust.AppSurface.Aspire;

/// <summary>
/// Provides context and helper methods during the Aspire application startup process.
/// </summary>
public class AspireStartupContext
{
    private readonly IDistributedApplicationBuilder _appBuilder;
    private readonly Dictionary<IAspireComponent, object> _resolvedComponents = new();
    private readonly string _applicationRoot;

    /// <summary>
    /// Initializes a new instance of the <see cref="AspireStartupContext"/> class.
    /// </summary>
    /// <param name="appBuilder">The distributed application builder.</param>
    /// <param name="applicationRoot">Optional root directory for the application. If not provided, it will attempt to find the repository root.</param>
    public AspireStartupContext(
        IDistributedApplicationBuilder appBuilder,
        string? applicationRoot = null)
    {
        _appBuilder = appBuilder;
        _applicationRoot = applicationRoot ?? FindRepoRoot();
    }

    /// <summary>
    /// Resolves an Aspire resource from a component, ensuring each component is only generated once.
    /// </summary>
    /// <typeparam name="TResource">The type of the resource.</typeparam>
    /// <param name="dependency">The component providing the resource.</param>
    /// <returns>A resource builder for the resolved resource.</returns>
    public IResourceBuilder<TResource> Resolve<TResource>(IAspireComponent<TResource> dependency)
        where TResource : IResource
    {
        if (!_resolvedComponents.TryGetValue(dependency, out var rssBuilder))
        {
            rssBuilder = dependency.Generate(this, _appBuilder);
            _resolvedComponents[dependency] = rssBuilder;
        }

        return (IResourceBuilder<TResource>)rssBuilder;
    }

    /// <summary>
    /// Computes an absolute path from a path relative to the application root.
    /// </summary>
    /// <param name="relativePath">The relative path.</param>
    /// <returns>The computed absolute path.</returns>
    public string GetPathFromRoot(string relativePath)
    {
        return Path.Combine(_applicationRoot, relativePath);
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
        var lastRepo = default(string);
        while (dir != null)
        {
            // TODO: handle other repo types.
            var isRepo = Directory.Exists(Path.Combine(dir.FullName, ".git"));
            if (isRepo)
            {
                lastRepo = dir.FullName;
            }

            dir = dir.Parent;
        }

        if (lastRepo != null)
        {
            return lastRepo;
        }

        // TODO: Is there a better default we can use here?
        return string.Empty;
    }
}
