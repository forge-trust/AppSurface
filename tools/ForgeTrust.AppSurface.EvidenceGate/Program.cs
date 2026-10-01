using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.EvidenceGate;

internal static class Program
{
    private const string RunUsage = "Usage: appsurface-evidence-gate run --plan <canonical-v2-plan.json> --policy <trusted-base-policy.json> --repository <trusted-git-object-store> --output-dir <new-output-directory>";
    private const string VerifyUsage = "Usage: appsurface-evidence-gate verify-gate --plan <untrusted-plan.json> --manifest <untrusted-manifest.json> --policy <trusted-base-policy.json> --repository <trusted-git-object-store> --identity <trusted-expected-identity.json> [--artifacts-dir <trusted-extracted-artifact-root>] --output-dir <new-output-directory>";

    public static async Task<int> Main(string[] args)
    {
        if (IsHelp(args))
        {
            return await RunAsync(args, Console.Out, Console.Error).ConfigureAwait(false);
        }

        if (args.Length > 0 && string.Equals(args[0], "verify-gate", StringComparison.Ordinal))
        {
            if (!TryParseVerify(args, out _))
            {
                return await RunAsync(args, Console.Out, Console.Error).ConfigureAwait(false);
            }
        }
        else if (!TryParse(args, out _))
        {
            return await RunAsync(args, Console.Out, Console.Error).ConfigureAwait(false);
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            return await RunAsync(args, Console.Out, Console.Error, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    /// <summary>Dispatches one EvidenceGate command using the supplied output writers.</summary>
    /// <remarks>
    /// This internal entry point contains the same argument parsing and execution routing used by
    /// <see cref="Main(string[])"/>. Supplying writers lets in-process callers observe command output
    /// without replacing the process-wide <see cref="Console.Out"/> or <see cref="Console.Error"/>.
    /// Invalid arguments return 64; command execution retains the host and verifier exit codes.
    /// </remarks>
    internal static async Task<int> RunAsync(
        string[] args,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        if (IsHelp(args))
        {
            standardOutput.WriteLine(RunUsage);
            standardOutput.WriteLine(VerifyUsage);
            standardOutput.WriteLine("The run command reports host execution only. Gate verification is a separate trusted-controller operation.");
            standardOutput.WriteLine("verify-gate exits successfully only when the independent verifier confirms eligibility against current GitHub PR and subject-job state.");
            return 0;
        }

        if (args.Length > 0 && string.Equals(args[0], "verify-gate", StringComparison.Ordinal))
        {
            if (!TryParseVerify(args, out var verificationOptions))
            {
                standardError.WriteLine("ASEGG001: Invalid command arguments. " + VerifyUsage);
                return 64;
            }

            return await ExecuteVerifyAsync(verificationOptions, standardOutput, standardError, cancellationToken).ConfigureAwait(false);
        }

        if (!TryParse(args, out var options))
        {
            standardError.WriteLine("ASEGH001: Invalid command arguments. " + RunUsage);
            return 64;
        }

        return await EvidenceHostRunner.ExecuteAsync(
            options.PlanPath,
            options.PolicyPath,
            options.RepositoryPath,
            options.OutputDirectory,
            standardOutput,
            standardError,
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsHelp(string[] args) =>
        args.Length == 1 && string.Equals(args[0], "--help", StringComparison.Ordinal);

    private static async Task<int> ExecuteVerifyAsync(
        VerifyOptions verificationOptions,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        using GitHubActionsEvidenceAuthorityProvider? authorityProvider = GitHubActionsEvidenceAuthorityProvider.TryCreateFromEnvironment();
        return await EvidenceGateVerifier.ExecuteAsync(
            verificationOptions.PlanPath,
            verificationOptions.ManifestPath,
            verificationOptions.PolicyPath,
            verificationOptions.RepositoryPath,
            verificationOptions.IdentityPath,
            verificationOptions.OutputDirectory,
            verificationOptions.ArtifactHandoffRootPath,
            (IEvidencePullRequestGateAuthorityProvider?)authorityProvider ?? new UnavailableEvidenceAuthorityProvider(),
            artifactVerifier: new EvidencePullRequestGateNoFollowArtifactVerifier(),
            standardOutput,
            standardError,
            cancellationToken).ConfigureAwait(false);
    }

    private static bool TryParse(string[] args, out RunOptions options)
    {
        options = default;
        if (args.Length != 9 || !string.Equals(args[0], "run", StringComparison.Ordinal))
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            if (args[index] is not ("--plan" or "--policy" or "--repository" or "--output-dir")
                || string.IsNullOrWhiteSpace(args[index + 1])
                || !values.TryAdd(args[index], args[index + 1]))
            {
                return false;
            }
        }

        if (!values.TryGetValue("--plan", out var planPath)
            || !values.TryGetValue("--policy", out var policyPath)
            || !values.TryGetValue("--repository", out var repositoryPath)
            || !values.TryGetValue("--output-dir", out var outputDirectory))
        {
            return false;
        }

        options = new RunOptions(planPath, policyPath, repositoryPath, outputDirectory);
        return true;
    }

    private static bool TryParseVerify(string[] args, out VerifyOptions options)
    {
        options = default;
        if (args.Length < 13 || args.Length > 15 || !string.Equals(args[0], "verify-gate", StringComparison.Ordinal))
        {
            return false;
        }

        if ((args.Length - 1) % 2 != 0)
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            if (args[index] is not ("--plan" or "--manifest" or "--policy" or "--repository" or "--identity" or "--artifacts-dir" or "--output-dir")
                || string.IsNullOrWhiteSpace(args[index + 1])
                || !values.TryAdd(args[index], args[index + 1]))
            {
                return false;
            }
        }

        if (!values.TryGetValue("--plan", out var planPath)
            || !values.TryGetValue("--manifest", out var manifestPath)
            || !values.TryGetValue("--policy", out var policyPath)
            || !values.TryGetValue("--repository", out var repositoryPath)
            || !values.TryGetValue("--identity", out var identityPath)
            || !values.TryGetValue("--output-dir", out var outputDirectory))
        {
            return false;
        }

        values.TryGetValue("--artifacts-dir", out var artifactHandoffRootPath);
        options = new VerifyOptions(planPath, manifestPath, policyPath, repositoryPath, identityPath, outputDirectory, artifactHandoffRootPath);
        return true;
    }

    private readonly record struct RunOptions(string PlanPath, string PolicyPath, string RepositoryPath, string OutputDirectory);
    private readonly record struct VerifyOptions(
        string PlanPath,
        string ManifestPath,
        string PolicyPath,
        string RepositoryPath,
        string IdentityPath,
        string OutputDirectory,
        string? ArtifactHandoffRootPath);
}
