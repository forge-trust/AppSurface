namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>Tailwind-specific command inputs, separated from established PackageIndex option parsing.</summary>
internal sealed record TailwindCommandOptions(
    string? Mode,
    string? ProducerSubject,
    string? ProducerArtifactId,
    string? ExpectedSubjectSha256,
    string? RepositoryId,
    string? ProducerRunId,
    string? ProducerAttempt,
    string? SourceCommit,
    string? NativeInvocationId,
    string? ExpectedRid,
    string? WorkDirectory,
    string? ReportDirectory,
    string? EvidenceInput,
    string? HostArtifactsMap,
    string? AggregateInput,
    string? AggregateArtifactId,
    string? ExpectedAggregateSha256,
    string? PublicationDirectory,
    string? PublicationStartReceipt,
    string? PublicationStartArtifactId,
    string? ResolvedBindingOutput,
    string[] RemainingArguments)
{
    private static readonly IReadOnlySet<string> Keys = new HashSet<string>(StringComparer.Ordinal)
    {
        "--mode", "--producer-subject", "--producer-artifact-id", "--expected-subject-sha256", "--repository-id",
        "--producer-run-id", "--producer-attempt", "--source-commit", "--native-invocation-id", "--expected-rid",
        "--work-directory", "--report-directory", "--evidence-input", "--host-artifacts-map", "--aggregate-input",
        "--aggregate-artifact-id", "--expected-aggregate-sha256", "--publication-directory",
        "--publication-start-receipt", "--publication-start-artifact-id", "--resolved-binding-output"
    };

    internal static TailwindCommandOptions Extract(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var remaining = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            var key = args[index];
            if (!Keys.Contains(key)) { remaining.Add(key); continue; }
            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
                throw new PackageIndexException($"Option '{key}' requires a value.");
            if (!values.TryAdd(key, args[++index])) throw new PackageIndexException($"Option '{key}' was supplied more than once.");
        }

        string? Get(string name) => values.GetValueOrDefault(name);
        return new TailwindCommandOptions(Get("--mode"), Get("--producer-subject"), Get("--producer-artifact-id"),
            Get("--expected-subject-sha256"), Get("--repository-id"), Get("--producer-run-id"), Get("--producer-attempt"),
            Get("--source-commit"), Get("--native-invocation-id"), Get("--expected-rid"), Get("--work-directory"),
            Get("--report-directory"), Get("--evidence-input"), Get("--host-artifacts-map"), Get("--aggregate-input"),
            Get("--aggregate-artifact-id"), Get("--expected-aggregate-sha256"), Get("--publication-directory"),
            Get("--publication-start-receipt"), Get("--publication-start-artifact-id"), Get("--resolved-binding-output"), remaining.ToArray());
    }

    internal TailwindPublicationRequest CreatePublicationRequest(
        string repositoryRoot,
        string artifactsInputPath,
        string artifactManifestPath)
    {
        return new TailwindPublicationRequest(
            repositoryRoot,
            artifactsInputPath,
            artifactManifestPath,
            Require(ProducerSubject, "--producer-subject"),
            Require(ProducerArtifactId, "--producer-artifact-id"),
            Require(ExpectedSubjectSha256, "--expected-subject-sha256"),
            Require(RepositoryId, "--repository-id"),
            Require(ProducerRunId, "--producer-run-id"),
            Require(SourceCommit, "--source-commit"),
            ResolveRequiredPath(AggregateInput, repositoryRoot, "--aggregate-input"),
            Require(AggregateArtifactId, "--aggregate-artifact-id"),
            Require(ExpectedAggregateSha256, "--expected-aggregate-sha256"),
            ResolveRequiredPath(PublicationDirectory, repositoryRoot, "--publication-directory"),
            ResolveRequiredPath(PublicationStartReceipt, repositoryRoot, "--publication-start-receipt"),
            Require(PublicationStartArtifactId, "--publication-start-artifact-id"),
            ResolveRequiredPath(ReportDirectory, repositoryRoot, "--report-directory"));
    }

    internal TailwindPublicationRequest CreatePreflightRequest(
        string repositoryRoot,
        string artifactsInputPath,
        string artifactManifestPath)
    {
        var reportDirectory = ResolveRequiredPath(ReportDirectory, repositoryRoot, "--report-directory");
        return new TailwindPublicationRequest(
            repositoryRoot, artifactsInputPath, artifactManifestPath,
            Require(ProducerSubject, "--producer-subject"), Require(ProducerArtifactId, "--producer-artifact-id"),
            Require(ExpectedSubjectSha256, "--expected-subject-sha256"), Require(RepositoryId, "--repository-id"),
            Require(ProducerRunId, "--producer-run-id"), Require(SourceCommit, "--source-commit"),
            ResolveRequiredPath(AggregateInput, repositoryRoot, "--aggregate-input"), Require(AggregateArtifactId, "--aggregate-artifact-id"),
            Require(ExpectedAggregateSha256, "--expected-aggregate-sha256"),
            ResolveRequiredPath(PublicationDirectory, repositoryRoot, "--publication-directory"),
            Path.Join(reportDirectory, "publication-start-receipt.json"), string.Empty, reportDirectory);
    }

    internal TailwindPublicationRequest CreateStartValidationRequest(
        string repositoryRoot,
        string artifactsInputPath,
        string artifactManifestPath)
    {
        var reportDirectory = ResolveRequiredPath(ReportDirectory, repositoryRoot, "--report-directory");
        return new TailwindPublicationRequest(
            repositoryRoot, artifactsInputPath, artifactManifestPath,
            Require(ProducerSubject, "--producer-subject"), Require(ProducerArtifactId, "--producer-artifact-id"),
            Require(ExpectedSubjectSha256, "--expected-subject-sha256"), Require(RepositoryId, "--repository-id"),
            Require(ProducerRunId, "--producer-run-id"), Require(SourceCommit, "--source-commit"),
            ResolveRequiredPath(AggregateInput, repositoryRoot, "--aggregate-input"),
            Require(AggregateArtifactId, "--aggregate-artifact-id"), Require(ExpectedAggregateSha256, "--expected-aggregate-sha256"),
            ResolveRequiredPath(PublicationDirectory, repositoryRoot, "--publication-directory"),
            ResolveRequiredPath(PublicationStartReceipt, repositoryRoot, "--publication-start-receipt"),
            Require(PublicationStartArtifactId, "--publication-start-artifact-id"), reportDirectory);
    }

    internal string ResolveRequiredPath(string? value, string root, string flag)
    {
        var required = Require(value, flag);
        return Path.GetFullPath(Path.IsPathRooted(required) ? required : Path.Join(root, required));
    }

    internal static string Require(string? value, string flag)
        => !string.IsNullOrWhiteSpace(value) ? value : throw new PackageIndexException($"Required option '{flag}' is missing.");
}
