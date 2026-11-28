using System.Reflection;

/// <summary>Reads the ordered test policy embedded in the Dogfood assembly.</summary>
/// <remarks>The same filter parser serves workflow collection, attestation, and native lifecycle validation.</remarks>
internal static class NativeCoverageTestInventory
{
    private const string ResourcePrefix = "ForgeTrust.AppSurface.Coverage.Dogfood.native-coverage-test-";

    /// <summary>Reads the repository-relative test projects in collection and attestation order.</summary>
    /// <returns>The complete ordered native project inventory.</returns>
    /// <exception cref="InvalidOperationException">The project inventory resource is missing.</exception>
    /// <exception cref="InvalidDataException">A project is missing, blank, or duplicated.</exception>
    internal static string[] ReadNativeProjects()
    {
        using var reader = OpenInventory("projects");
        var projects = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!line.EndsWith(".csproj", StringComparison.Ordinal) || line != line.Trim() || !seen.Add(line))
                throw new InvalidDataException("The native coverage project inventory contains an invalid entry.");

            projects.Add(line);
        }

        if (projects.Count == 0)
            throw new InvalidDataException("The native coverage project inventory is empty.");

        return projects.ToArray();
    }

    /// <summary>Builds a test filter from the embedded selector inventory.</summary>
    /// <param name="lane">The native coverage, shared CLI, or native lifecycle CLI filter.</param>
    /// <returns>The selected filter terms joined in checked-in policy order.</returns>
    /// <exception cref="ArgumentException">The filter name is unsupported.</exception>
    /// <exception cref="InvalidOperationException">The selector inventory resource is missing.</exception>
    /// <exception cref="InvalidDataException">The selection is empty or the inventory contains an invalid entry.</exception>
    internal static string ReadFilter(string lane = "native")
    {
        if (lane is not ("native" or "shared-cli" or "lifecycle-cli"))
            throw new ArgumentException("Unknown native test filter.", nameof(lane));

        using var reader = OpenInventory("selectors");
        var selectors = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var entry = line.Split('\t', 2);
            if (entry.Length != 2 || entry[0] is not ("native" or "shared-cli" or "lifecycle-cli") ||
                !entry[1].StartsWith("FullyQualifiedName~", StringComparison.Ordinal))
                throw new InvalidDataException("The native coverage selector inventory contains an invalid entry.");

            if (entry[0] == lane || entry[0] == "shared-cli")
                selectors.Add(entry[1]);
        }

        if (selectors.Count == 0)
            throw new InvalidDataException("The native coverage selector inventory has no entries for the requested filter.");

        return string.Join("|", selectors);
    }

    private static StreamReader OpenInventory(string inventory)
        => new(Assembly.GetExecutingAssembly().GetManifestResourceStream($"{ResourcePrefix}{inventory}.txt")
            ?? throw new InvalidOperationException($"The native coverage {inventory} inventory is missing."));
}
