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
    private readonly string? _applicationRoot;

    /// <summary>
    /// Initializes a new instance of the <see cref="AspireStartupContext"/> class.
    /// </summary>
    /// <param name="appBuilder">The distributed application builder.</param>
    /// <param name="applicationRoot">
    /// An optional nonblank, fully qualified repository root. It takes precedence over discovery, is normalized once,
    /// and need not exist. When null, the nearest ancestor of the builder's AppHost directory containing a Git file
    /// or directory is selected. Missing metadata or a nonabsolute anchor leaves the root unresolved.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="applicationRoot"/> is blank or not fully qualified.</exception>
    /// <remarks>
    /// Root selection is a constructor snapshot. Unresolved roots permit path-free component composition and fail
    /// only when <see cref="GetPathFromRoot"/> is called. No process-directory or executable-directory fallback is used.
    /// See the <see href="https://github.com/forge-trust/AppSurface/blob/main/Aspire/ForgeTrust.AppSurface.Aspire/README.md#repository-relative-assets">repository asset guide</see>
    /// for checkout requirements and caller-owned explicit-root recovery.
    /// </remarks>
    public AspireStartupContext(
        IDistributedApplicationBuilder appBuilder,
        string? applicationRoot = null)
    {
        _appBuilder = appBuilder;
        if (applicationRoot is not null)
        {
            if (string.IsNullOrWhiteSpace(applicationRoot) || !Path.IsPathFullyQualified(applicationRoot))
            {
                throw new ArgumentException(
                    "The repository root must be a nonblank, fully qualified absolute path. " +
                    "Supply an absolute applicationRoot, or omit it to discover from AppHostDirectory.",
                    nameof(applicationRoot));
            }

            _applicationRoot = Path.GetFullPath(applicationRoot);
        }
        else
        {
            _applicationRoot = FindRepoRoot(appBuilder.AppHostDirectory);
        }
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
    /// Combines the constructor's repository root with a trusted repository-relative asset path.
    /// </summary>
    /// <param name="relativePath">The asset path, typically relative to the repository root.</param>
    /// <returns>A fully qualified path for repository-relative input, without checking asset existence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="relativePath"/> is null, even if no root was found.</exception>
    /// <exception cref="InvalidOperationException">The constructor could not discover a repository root.</exception>
    /// <remarks>
    /// Retains <see cref="Path.Combine(string, string)"/> semantics for empty, rooted and parent-traversal inputs;
    /// this is path computation, not containment or asset validation. A root is required even for rooted input.
    /// Callers check files/directories and own their import or mount errors. The root is never rediscovered here.
    /// See the <see href="https://github.com/forge-trust/AppSurface/blob/main/Aspire/ForgeTrust.AppSurface.Aspire/README.md#repository-relative-assets">repository asset guide</see>.
    /// </remarks>
    public string GetPathFromRoot(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (_applicationRoot is null)
        {
            throw new InvalidOperationException(
                "Repository discovery from AppHostDirectory found no Git file or directory at an absolute anchor. " +
                "Use an AppHost checkout with Git metadata, or construct your own AspireStartupContext with an " +
                "explicit absolute applicationRoot. See " +
                "https://github.com/forge-trust/AppSurface/blob/main/Aspire/ForgeTrust.AppSurface.Aspire/README.md#repository-relative-assets");
        }

        return Path.Combine(_applicationRoot, relativePath);
    }

    /// <summary>
    /// Selects the nearest Git-marked ancestor of a fully qualified AppHost directory, including the anchor itself.
    /// </summary>
    /// <param name="appHostDirectory">The source-project anchor; blank or nonabsolute values disable discovery.</param>
    /// <returns>The first Git file/directory ancestor's full path, or null when no usable root is available.</returns>
    /// <remarks>
    /// Marker contents are not parsed and Git validity is not checked. This single constructor-time walk performs
    /// no Git subprocess, existence requirement for the anchor, output/CWD fallback, or repeated path-time discovery.
    /// </remarks>
    private static string? FindRepoRoot(string? appHostDirectory)
    {
        if (string.IsNullOrWhiteSpace(appHostDirectory) || !Path.IsPathFullyQualified(appHostDirectory))
        {
            return null;
        }

        DirectoryInfo? directory = new DirectoryInfo(appHostDirectory);
        while (directory is not null)
        {
            var marker = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
