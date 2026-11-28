using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Evidence.Cli;

/// <summary>Resolves the built-in coverage settings shared by collection and receipt identity.</summary>
/// <param name="options">Declared execution options, or null for the contract defaults.</param>
/// <remarks>Keep invocation paths and deadlines out of these settings. The identity records the same effective
/// configuration, collection format, and build policy that the coverage request uses.</remarks>
internal sealed class CoverageEvidenceRunSettings(EvidenceCoverageExecutionOptions? options)
{
    private readonly EvidenceCoverageExecutionOptions _options = options ?? new();

    /// <summary>Gets the build and test configuration.</summary>
    internal string Configuration => _options.Configuration;
    /// <summary>Gets the maximum concurrent non-exclusive test projects.</summary>
    internal int Parallelism => _options.Parallelism;
    /// <summary>Gets the declared format used for the stable identity spelling and to select the result collector.</summary>
    internal EvidenceCoverageTestResultFormat TestResults => _options.TestResults;

    /// <summary>The built-in producer's coverage driver.</summary>
    internal const CoverageRunDriver Driver = CoverageRunDriver.Collector;
    /// <summary>The built-in producer's project scheduling policy.</summary>
    internal const CoverageRunScheduleMode Schedule = CoverageRunScheduleMode.InputOrder;
    /// <summary>Whether the request skips restore.</summary>
    internal const bool NoRestore = false;
    /// <summary>Whether the request explicitly builds the solution.</summary>
    internal const bool Build = true;
    /// <summary>The built-in producer excludes test assemblies from measured coverage.</summary>
    internal const string TestExclusion = "[*.Tests]*,[*.IntegrationTests]*";
}
