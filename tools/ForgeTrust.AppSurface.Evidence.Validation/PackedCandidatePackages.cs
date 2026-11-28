namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Identifies the local projects and archive IDs used to build and admit the packed reuse candidate.</summary>
internal static class PackedCandidatePackages
{
    /// <summary>Namespace shared by the candidate package IDs.</summary>
    internal const string Prefix = "ForgeTrust.AppSurface.";

    /// <summary>The candidate tool package.</summary>
    internal const string ToolId = Prefix + "Cli";
    /// <summary>The evidence contracts package.</summary>
    internal const string ContractsId = Prefix + "Evidence.Contracts";
    /// <summary>The evidence planner package.</summary>
    internal const string PlannerId = Prefix + "Evidence.Planner";
    /// <summary>The evidence readiness package.</summary>
    internal const string ReadinessId = Prefix + "Evidence.Readiness";
    /// <summary>The evidence coverage support package.</summary>
    internal const string SupportId = Prefix + "Evidence.Coverage";
    /// <summary>The framework-neutral coverage package.</summary>
    internal const string CoverageId = Prefix + "Coverage";
    /// <summary>The separately packable evidence CLI integration.</summary>
    internal const string EvidenceCliId = Prefix + "Evidence.Cli";
    /// <summary>The prepared Aspire host package.</summary>
    internal const string AspireId = Prefix + "Evidence.Aspire";

    /// <summary>The tool can be built from source or supplied as an existing archive.</summary>
    internal static Package Tool { get; } = new(ToolId, "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj");

    private static readonly Package[] Libraries =
    [
        new(ContractsId, "Evidence/ForgeTrust.AppSurface.Evidence.Contracts/ForgeTrust.AppSurface.Evidence.Contracts.csproj", IsNeutralLibrary: true),
        new(PlannerId, "Evidence/ForgeTrust.AppSurface.Evidence.Planner/ForgeTrust.AppSurface.Evidence.Planner.csproj"),
        new(ReadinessId, "Evidence/ForgeTrust.AppSurface.Evidence.Readiness/ForgeTrust.AppSurface.Evidence.Readiness.csproj"),
        new(SupportId, "Evidence/ForgeTrust.AppSurface.Evidence.Coverage/ForgeTrust.AppSurface.Evidence.Coverage.csproj", IsNeutralLibrary: true),
        new(CoverageId, "Coverage/ForgeTrust.AppSurface.Coverage/ForgeTrust.AppSurface.Coverage.csproj", IsNeutralLibrary: true),
        new(EvidenceCliId, "Evidence/ForgeTrust.AppSurface.Evidence.Cli/ForgeTrust.AppSurface.Evidence.Cli.csproj", RequiredForAdmission: false),
        new(AspireId, "Evidence/ForgeTrust.AppSurface.Evidence.Aspire/ForgeTrust.AppSurface.Evidence.Aspire.csproj"),
    ];

    /// <summary>Libraries packed for the neutral coverage consumer.</summary>
    internal static IReadOnlyList<Package> NeutralLibraries { get; } =
        Array.AsReadOnly(Libraries.Where(package => package.IsNeutralLibrary).ToArray());

    /// <summary>Additional libraries built for the packed reuse consumer.</summary>
    internal static IReadOnlyList<Package> ReuseLibraries { get; } =
        Array.AsReadOnly(Libraries.Where(package => !package.IsNeutralLibrary).ToArray());

    /// <summary>Archives required by reuse admission, in its stable verification order.</summary>
    internal static IReadOnlyList<Package> RequiredArchives { get; } =
        Array.AsReadOnly(Libraries.Where(package => package.RequiredForAdmission).Prepend(Tool).ToArray());

    /// <summary>Additional archives verified by reuse admission when present in the supplied feed.</summary>
    internal static IReadOnlyList<Package> OptionalArchives { get; } =
        Array.AsReadOnly(Libraries.Where(package => !package.RequiredForAdmission).ToArray());

    /// <summary>A package's explicit ID, build project and membership in the candidate feeds.</summary>
    internal sealed record Package(string Id, string ProjectPath, bool IsNeutralLibrary = false, bool RequiredForAdmission = true)
    {
        /// <summary>Gets the expected filename for the exact candidate version.</summary>
        internal string ArchiveName(string version) => $"{Id}.{version}.nupkg";
    }
}
