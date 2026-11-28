using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Reads the package ID and version from a dependency library key without normalizing case.</summary>
/// <remarks>
/// Malformed keys retain the leading ID so a validator can still identify an entry belonging to its package family.
/// Only a key containing exactly one separator between a nonempty ID and version has a version.
/// </remarks>
internal readonly record struct PackedDependencyLibrary(JsonProperty Entry, string Id, string? Version)
{
    /// <summary>Reads the entries of a dependency manifest's libraries object.</summary>
    internal static IEnumerable<PackedDependencyLibrary> Read(JsonElement libraries)
        => libraries.EnumerateObject().Select(Parse);

    /// <summary>Matches the full package key using exact ID and version comparisons.</summary>
    internal bool Matches(string id, string? version)
        => Version is not null && Id == id && Version == version;

    private static PackedDependencyLibrary Parse(JsonProperty entry)
    {
        var name = entry.Name;
        var separator = name.IndexOf('/');
        if (separator < 0) return new(entry, name, null);
        var version = separator > 0 && separator < name.Length - 1 && name.IndexOf('/', separator + 1) < 0
            ? name[(separator + 1)..]
            : null;
        return new(entry, name[..separator], version);
    }
}
