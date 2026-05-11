using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace ForgeTrust.AppSurface.Aspire;

/// <summary>
/// Indicates that the implementing class is an Aspire component that can generate resources for the application.
///
/// Classes implementing this interface are responsible for generating resources of type <typeparamref name="T"/>,
/// </summary>
/// <typeparam name="T">The type of resource the component generates.</typeparam>
public interface IAspireComponent<out T> : IAspireComponent
    where T : IResource
{
    /// <summary>
    /// Generates and adds a resource to the Aspire distributed application builder.
    /// </summary>
    /// <param name="context">The Aspire startup context.</param>
    /// <param name="appBuilder">The distributed application builder.</param>
    /// <returns>A resource builder for the generated resource.</returns>
    IResourceBuilder<T> Generate(
        AspireStartupContext context,
        IDistributedApplicationBuilder appBuilder);
}

/// <summary>
/// The base interface for Aspire components.
///
/// For now this interface does not define any members, but it serves as a marker interface
/// to identify classes that are Aspire components. This allows for future expansion and
/// additional functionality to be added to all Aspire components if needed.
/// </summary>
public interface IAspireComponent
{
}
