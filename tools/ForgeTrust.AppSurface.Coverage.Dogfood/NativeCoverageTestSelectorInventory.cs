using System.Reflection;

/// <summary>Reads the ordered native coverage policy embedded in the Dogfood assembly.</summary>
/// <remarks>The embedded copy binds attestation to this build and preserves selector order.</remarks>
internal static class NativeCoverageTestSelectorInventory
{
    private const string ResourceName = "ForgeTrust.AppSurface.Coverage.Dogfood.native-coverage-test-selectors.txt";

    /// <summary>Builds the native test filter from the embedded selector inventory.</summary>
    /// <returns>The selectors joined in their checked-in policy order.</returns>
    /// <exception cref="InvalidOperationException">The inventory resource is missing.</exception>
    /// <exception cref="InvalidDataException">The inventory is empty or contains an invalid entry.</exception>
    internal static string ReadNativeFilter()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The native coverage selector inventory is missing.");
        using var reader = new StreamReader(stream);
        var selectors = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var entry = line.Split('\t', 2);
            if (entry.Length != 2 || entry[0] is not ("native" or "shared-cli") ||
                !entry[1].StartsWith("FullyQualifiedName~", StringComparison.Ordinal))
                throw new InvalidDataException("The native coverage selector inventory contains an invalid entry.");

            selectors.Add(entry[1]);
        }

        if (selectors.Count == 0)
            throw new InvalidDataException("The native coverage selector inventory is empty.");

        return string.Join("|", selectors);
    }
}
