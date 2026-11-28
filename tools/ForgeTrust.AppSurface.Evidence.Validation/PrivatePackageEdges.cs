using System.Xml.Linq;

/// <summary>Defines the expected private package edges for the neutral and packed reuse proofs.</summary>
internal static class PrivatePackageEdges
{
    /// <summary>Gets the exact private dependency ID required by a package, if any.</summary>
    internal static string? RequiredDependency(string packageId) => packageId switch
    {
        "ForgeTrust.AppSurface.Coverage" => "ForgeTrust.AppSurface.Evidence.Coverage",
        "ForgeTrust.AppSurface.Evidence.Coverage" => "ForgeTrust.AppSurface.Evidence.Contracts",
        "ForgeTrust.AppSurface.Evidence.Aspire" or "ForgeTrust.AppSurface.Evidence.Cli" => "ForgeTrust.AppSurface.Evidence.Coverage",
        _ => null,
    };

    /// <summary>Checks for one case-exact, closed version pin in every framework group or the flat dependency declaration.</summary>
    /// <param name="dependencies">The package's single NuSpec dependencies element.</param>
    /// <param name="dependencyId">The required private package ID.</param>
    /// <param name="version">The exact candidate package version without range delimiters.</param>
    /// <returns>Whether each dependency group has exactly one valid pin and no extra private edges.</returns>
    internal static bool HasSingleExactPin(XElement dependencies, string dependencyId, string version)
    {
        var groupsWithoutPin = dependencies.Elements().Where(element => element.Name.LocalName == "group").ToHashSet();
        if (groupsWithoutPin.Count == 0)
            groupsWithoutPin.Add(dependencies);

        var exactRange = $"[{version}]";
        foreach (var edge in dependencies.Descendants().Where(element => element.Name.LocalName == "dependency"
            && string.Equals((string?)element.Attribute("id"), dependencyId, StringComparison.OrdinalIgnoreCase)))
        {
            if ((string?)edge.Attribute("id") != dependencyId || (string?)edge.Attribute("version") != exactRange
                || edge.Parent is not { } group || !groupsWithoutPin.Remove(group))
                return false;
        }
        return groupsWithoutPin.Count == 0;
    }
}
