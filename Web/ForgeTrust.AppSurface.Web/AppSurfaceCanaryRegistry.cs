using System.Collections.Frozen;

namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Provides immutable ordinal lookup for registered named canaries.
/// </summary>
internal sealed class AppSurfaceCanaryRegistry
{
    private readonly FrozenDictionary<string, AppSurfaceCanaryDescriptor> _descriptors;

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
    }

    internal bool TryGet(string name, out AppSurfaceCanaryDescriptor descriptor) =>
        _descriptors.TryGetValue(name, out descriptor!);
}
