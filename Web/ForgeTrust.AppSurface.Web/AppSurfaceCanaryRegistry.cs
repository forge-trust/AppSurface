using System.Collections.Frozen;

namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Provides immutable ordinal lookup for registered named canaries.
/// </summary>
internal sealed class AppSurfaceCanaryRegistry
{
    private readonly FrozenDictionary<string, AppSurfaceCanaryDescriptor> _descriptors;
    private readonly IReadOnlyList<AppSurfaceCanaryDescriptor> _orderedDescriptors;

    /// <summary>
    /// Builds an immutable registry and rejects duplicate exact names.
    /// </summary>
    /// <param name="descriptors">The validated descriptor sequence to snapshot.</param>
    /// <exception cref="InvalidOperationException">More than one descriptor uses the same ordinal name.</exception>
    internal AppSurfaceCanaryRegistry(IEnumerable<AppSurfaceCanaryDescriptor> descriptors)
    {
        var dictionary = new Dictionary<string, AppSurfaceCanaryDescriptor>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            if (!dictionary.TryAdd(descriptor.Name, descriptor))
            {
                throw new InvalidOperationException(
                    $"ASCAN102: The named canary '{descriptor.Name}' is registered more than once. Keep exactly one registration for each name.");
            }
        }

        _descriptors = dictionary.ToFrozenDictionary(StringComparer.Ordinal);
        _orderedDescriptors = dictionary.Values.ToArray();
    }

    /// <summary>Attempts exact ordinal lookup of a registered descriptor.</summary>
    /// <param name="name">The exact name to find.</param>
    /// <param name="descriptor">The registered descriptor when found.</param>
    /// <returns><see langword="true"/> when the name is registered; otherwise <see langword="false"/>.</returns>
    internal bool TryGet(string name, out AppSurfaceCanaryDescriptor descriptor) =>
        _descriptors.TryGetValue(name, out descriptor!);

    /// <summary>Gets the registration-order descriptor snapshot without resolving evaluators.</summary>
    internal IReadOnlyList<AppSurfaceCanaryDescriptor> OrderedDescriptors => _orderedDescriptors;
}
