namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>
/// Defines the managed test-result artifact formats available to the built-in coverage producer.
/// </summary>
public enum EvidenceCoverageTestResultFormat
{
    /// <summary>Do not create managed test-result artifacts.</summary>
    None,

    /// <summary>Create managed JUnit XML test-result artifacts.</summary>
    Junit,
}

/// <summary>
/// Configures the bounded execution envelope for a built-in Evidence coverage producer.
/// </summary>
/// <param name="Configuration">A simple build and test configuration identifier, defaulting to <c>Debug</c>.</param>
/// <param name="TestResults">The managed test-result artifact format, defaulting to <see cref="EvidenceCoverageTestResultFormat.None"/>.</param>
/// <param name="Parallelism">The maximum number of non-exclusive test projects to run concurrently, defaulting to <c>1</c>.</param>
/// <remarks>
/// This configuration selects an execution envelope; it does not change the producer's coverage filters,
/// coverage gate thresholds, or source scope. The planner accepts simple identifiers only so a configuration
/// value cannot inject command-line arguments. Validated values are forwarded exactly to the coverage engine.
/// </remarks>
public sealed record EvidenceCoverageExecutionOptions(
    string Configuration = "Debug",
    EvidenceCoverageTestResultFormat TestResults = EvidenceCoverageTestResultFormat.None,
    int Parallelism = 1)
{
    /// <summary>Determines whether these values fit the fixed protected coverage command.</summary>
    /// <param name="producer">The complete coverage declaration to evaluate.</param>
    /// <returns><see langword="true"/> when restricted execution uses only its fixed Debug, serial, no-results command.</returns>
    /// <remarks>Ordinary coverage producers may use the wider planner-validated envelope. Restricted execution may omit
    /// this property or specify exactly Debug, serial execution, and no managed results; artifact retention must be managed.</remarks>
    internal static bool SupportsRestrictedExecution(EvidenceProducerDeclaration producer)
    {
        ArgumentNullException.ThrowIfNull(producer);
        return string.Equals(producer.CoverageArtifactRetention, "managed", StringComparison.Ordinal)
            && (producer.CoverageExecution is null
                || (string.Equals(producer.CoverageExecution.Configuration, "Debug", StringComparison.Ordinal)
                    && producer.CoverageExecution.Parallelism == 1
                    && producer.CoverageExecution.TestResults == EvidenceCoverageTestResultFormat.None));
    }
}
