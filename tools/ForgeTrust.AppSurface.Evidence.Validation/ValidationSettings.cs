namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Explicit candidate and workspace inputs for the repository's local validation procedures.</summary>
/// <param name="RepositoryRoot">Checkout containing this validation suite.</param>
/// <param name="PackagePath">Candidate CLI nupkg that must be installed and verified.</param>
/// <param name="PackageVersion">Exact candidate version; no floating package selection.</param>
/// <param name="ExpectedSourceCommit">Caller-pinned candidate source commit checked against package metadata; not an admission credential.</param>
/// <param name="DotnetPath">Explicit SDK host executable.</param>
/// <param name="ScratchParent">Existing parent for freshly owned workspace leases.</param>
/// <param name="EvidenceDirectory">Fresh caller-owned durable output outside scratch.</param>
/// <param name="MaximumScratchBytes">Per-producer logical scratch budget.</param>
/// <param name="MinimumFreeBytes">Free-space floor checked before and during commands.</param>
/// <param name="Consumer">Optional pinned external consumer; null selects the checked-in fixture.</param>
internal sealed record ValidationSettings(
    string RepositoryRoot,
    string PackagePath,
    string PackageVersion,
    string ExpectedSourceCommit,
    string DotnetPath,
    string ScratchParent,
    string EvidenceDirectory,
    long MaximumScratchBytes = 2L * 1024 * 1024 * 1024,
    long MinimumFreeBytes = 1024L * 1024 * 1024,
    ConsumerValidationOptions? Consumer = null);
