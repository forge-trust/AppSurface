using ForgeTrust.AppSurface.Coverage;

/// <summary>
/// Copyable public-API recipes. Every path, source identity, inventory, and run identity is
/// supplied by the calling application; this helper does not infer consumer policy.
/// </summary>
public static class CoverageFanInApiExample
{
    /// <summary>Runs and captures the complete test inventory through the supervised collector.</summary>
    public static ValueTask<CoverageFanInResult> CollectAndCaptureAsync(
        CoverageFanInPolicy policy,
        string repositoryRoot,
        string outputDirectory,
        string collectionDirectory,
        string solutionPath,
        string producerId,
        int deadlineSeconds,
        CancellationToken cancellationToken = default) =>
        CoverageFanIn.CollectAndCaptureAsync(
            new CoverageCollectRequest(
                policy,
                repositoryRoot,
                outputDirectory,
                collectionDirectory,
                producerId,
                deadlineSeconds,
                SolutionPath: solutionPath,
                Parallelism: 1),
            cancellationToken);

    /// <summary>Captures a successful primary producer report and its complete timing inventory.</summary>
    public static ValueTask<CoverageFanInResult> CapturePrimaryAsync(
        CoverageFanInPolicy policy,
        string repositoryRoot,
        string outputDirectory,
        string normalizedCoveragePath,
        string timingsPath,
        CancellationToken cancellationToken = default) =>
        CoverageFanIn.CaptureAsync(
            new CoverageCaptureRequest(
                policy,
                repositoryRoot,
                outputDirectory,
                "primary",
                CoveragePath: normalizedCoveragePath,
                TimingsPath: timingsPath),
            cancellationToken);

    /// <summary>Captures native measurements from the original collector report; the normalized path is a bounded XML compatibility input.</summary>
    public static ValueTask<CoverageFanInResult> CaptureNativeAsync(
        CoverageFanInPolicy policy,
        string repositoryRoot,
        string outputDirectory,
        string supplementId,
        string normalizedCoveragePath,
        string rawCollectorCoveragePath,
        CancellationToken cancellationToken = default) =>
        CoverageFanIn.CaptureAsync(
            new CoverageCaptureRequest(
                policy,
                repositoryRoot,
                outputDirectory,
                supplementId,
                CoveragePath: normalizedCoveragePath,
                RawCoveragePath: rawCollectorCoveragePath),
            cancellationToken);

    /// <summary>
    /// Captures one or more native test projects from the exact project-to-original-report inventory and
    /// complete successful collection timings. The capture retains byte-exact originals as
    /// <c>collector-0000.cobertura.xml</c>, <c>collector-0001.cobertura.xml</c>, and so on, plus
    /// <c>collector-inventory.json</c> and <c>timings.json</c>; its portable projection is derived from
    /// those originals and independently verified.
    /// </summary>
    /// <remarks>
    /// Keys must exactly match the supplement's declared test-project set, and each value must identify that
    /// project's original collector attachment. The capture orders index records ordinally. Native inventories
    /// support at most 9,995 projects because the unchanged 10,000-file storage inventory also reserves entries for the
    /// projection, timings, index, manifest, and completion marker. Use <see cref="CaptureNativeAsync"/>
    /// when a single-project caller has no collection timings. The indexed form also supports one project.
    /// </remarks>
    public static ValueTask<CoverageFanInResult> CaptureNativeSetAsync(
        CoverageFanInPolicy policy,
        string repositoryRoot,
        string outputDirectory,
        string supplementId,
        IReadOnlyDictionary<string, string> rawCoveragePaths,
        string timingsPath,
        CancellationToken cancellationToken = default) =>
        CoverageFanIn.CaptureAsync(
            new CoverageCaptureRequest(
                policy,
                repositoryRoot,
                outputDirectory,
                supplementId,
                CoveragePath: null,
                TimingsPath: timingsPath,
                RawCoveragePath: null,
                RawCoveragePaths: rawCoveragePaths),
            cancellationToken);

    /// <summary>Materializes a transported v2 capture into new local custody without changing its observation.</summary>
    public static ValueTask<CoverageFanInResult> ImportPortableSupplementAsync(
        CoverageFanInPolicy policy,
        string matchingRepositoryRoot,
        string freshReceivingDirectory,
        string supplementId,
        string portableCaptureDirectory,
        string exactOriginalSourceRoot,
        CancellationToken cancellationToken = default) =>
        CoverageFanIn.CaptureAsync(
            new CoverageCaptureRequest(
                policy,
                matchingRepositoryRoot,
                freshReceivingDirectory,
                supplementId,
                ImportDirectory: portableCaptureDirectory,
                OriginalSourceRoot: exactOriginalSourceRoot),
            cancellationToken);

    /// <summary>Composes one primary capture with the policy's exact supplementary set.</summary>
    public static ValueTask<CoverageFanInResult> ComposeAsync(
        CoverageFanInPolicy policy,
        string repositoryRoot,
        string primaryCaptureDirectory,
        IReadOnlyList<string> supplementaryCaptureDirectories,
        string freshOutputDirectory,
        CancellationToken cancellationToken = default) =>
        CoverageFanIn.ComposeAsync(
            new CoverageComposeRequest(
                policy,
                repositoryRoot,
                primaryCaptureDirectory,
                supplementaryCaptureDirectories,
                freshOutputDirectory),
            cancellationToken);

    /// <summary>Runs the primary aggregate gate, requested patch gate, and independent acceptance checks.</summary>
    public static ValueTask<CoverageFanInResult> AcceptAsync(
        CoverageFanInPolicy policy,
        string repositoryRoot,
        string primaryCaptureDirectory,
        IReadOnlyList<string> supplementaryCaptureDirectories,
        string composedOutputDirectory,
        string freshAcceptedOutputDirectory,
        CancellationToken cancellationToken = default) =>
        CoverageFanIn.AcceptAsync(
            new CoverageAcceptRequest(
                policy,
                repositoryRoot,
                primaryCaptureDirectory,
                supplementaryCaptureDirectories,
                composedOutputDirectory,
                freshAcceptedOutputDirectory),
            cancellationToken);

    /// <summary>Read-only verification of the existing accepted generation.</summary>
    public static ValueTask<CoverageFanInResult> VerifyAsync(
        CoverageFanInPolicy policy,
        string repositoryRoot,
        string primaryCaptureDirectory,
        IReadOnlyList<string> supplementaryCaptureDirectories,
        string composedOutputDirectory,
        string acceptedOutputDirectory,
        CancellationToken cancellationToken = default) =>
        CoverageFanIn.VerifyAsync(
            new CoverageVerifyRequest(
                policy,
                repositoryRoot,
                primaryCaptureDirectory,
                supplementaryCaptureDirectories,
                composedOutputDirectory,
                acceptedOutputDirectory),
            cancellationToken);
}
