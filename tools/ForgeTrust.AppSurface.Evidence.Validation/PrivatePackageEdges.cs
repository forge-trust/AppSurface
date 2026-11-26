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
}
