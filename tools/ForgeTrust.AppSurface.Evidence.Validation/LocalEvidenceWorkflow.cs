using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>
/// Runs the repository policy against a local branch and working tree, packing a candidate only after
/// planning selects native producers. Uses the same host and manifest verifier as CI on the current OS.
/// </summary>
/// <remarks>
/// No results are reused. The merge-base diff, index, working tree and untracked files are collected by
/// <see cref="RepositoryGitChanges"/>. Source drift prevents gate success. Output belongs outside the
/// source inventory (the default is ignored TestResults); package and producer logs remain in that output.
/// </remarks>
internal sealed class LocalEvidenceWorkflow
{
    private readonly Func<EvidenceCommandRequest, CancellationToken, Task<EvidenceCommandResult>> _command;
    private readonly Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<int>>? _workflow;

    /// <summary>
    /// Creates local orchestration. Tests may replace the package command or the execution operation while planning
    /// and final manifest and artifact verification continue through the repository workflow.
    /// </summary>
    /// <param name="command">Optional bounded seam for creating the candidate package.</param>
    /// <param name="workflow">Optional seam invoked only for the execution operation; planning and final verification remain real.</param>
    internal LocalEvidenceWorkflow(
        Func<EvidenceCommandRequest, CancellationToken, Task<EvidenceCommandResult>>? command = null,
        Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<int>>? workflow = null)
    {
        var runner = new EvidenceCommandRunner();
        _command = command ?? runner.RunAsync;
        _workflow = workflow;
    }

    /// <summary>Plans or executes current local changes and returns zero only for successful verification or an empty inventory.</summary>
    internal async Task<int> RunAsync(IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        string[] allowed = ["--operation", "--repository", "--base", "--policy", "--output", "--dotnet", "--scope"];
        if (options.Keys.Except(allowed, StringComparer.Ordinal).Any()) throw new ArgumentException("Unsupported local evidence option.");
        var operation = options.GetValueOrDefault("--operation");
        if (operation is not ("local" or "local-plan")) throw new ArgumentException("Expected local or local-plan.");
        var repository = Path.GetFullPath(options.GetValueOrDefault("--repository", Directory.GetCurrentDirectory()));
        var policy = Path.GetFullPath(options.GetValueOrDefault("--policy", Path.Join(repository, ".github/evidence.policy.json")));
        var baseRevision = options.GetValueOrDefault("--base", "origin/main");
        var output = Path.GetFullPath(options.GetValueOrDefault("--output",
            Path.Join(repository, "TestResults", "repository-evidence", Guid.NewGuid().ToString("N"))));
        if (Directory.Exists(output) || File.Exists(output)) throw new ArgumentException("Evidence output must be fresh.");
        var git = new RepositoryGitChanges();
        var changes = await git.CollectLocalAsync(repository, baseRevision, cancellationToken);
        var policyDigest = EvidenceDigest.Sha256(await File.ReadAllBytesAsync(policy, cancellationToken));
        Directory.CreateDirectory(output);
        Console.WriteLine("Local evidence output: " + output);
        var paths = Path.Join(output, "changed-paths.json");
        await File.WriteAllBytesAsync(paths, EvidenceCanonicalJson.Serialize(changes.Paths), cancellationToken);
        await File.WriteAllTextAsync(Path.Join(output, "source-state.json"), JsonSerializer.Serialize(changes, Program.JsonOptions), cancellationToken);
        if (changes.Paths.Count == 0)
        {
            Console.WriteLine("No local changes relative to the selected base; no evidence collected.");
            await File.WriteAllTextAsync(Path.Join(output, "evidence-summary.md"), "No changed paths. No evidence claim was produced.\n", cancellationToken);
            return 0;
        }

        // Preserve checked-in rules/contracts while binding dirty local content into the canonical
        // plan and manifest. HEAD remains package metadata, not the complete local identity.
        var localPolicy = EvidenceCanonicalJson.Deserialize<EvidencePolicy>(await File.ReadAllBytesAsync(policy, cancellationToken));
        localPolicy = localPolicy with { Id = localPolicy.Id + "-local-" + changes.SourceDigest };
        var resolvedPolicy = Path.Join(output, "local-policy.json");
        await File.WriteAllBytesAsync(resolvedPolicy, EvidenceCanonicalJson.Serialize(localPolicy), cancellationToken);

        var common = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--policy"] = resolvedPolicy,
            ["--changed-paths"] = paths,
            ["--operation"] = "plan",
            ["--output"] = Path.Join(output, "plan"),
            ["--scope"] = options.GetValueOrDefault("--scope", "all"),
        };
        await RepositoryEvidenceWorkflow.RunAsync(common, cancellationToken);
        var planPath = Path.Join(output, "plan", "evidence-plan.json");
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(await File.ReadAllBytesAsync(planPath, cancellationToken));
        if (operation == "local-plan") return 0;

        // Pin the resolved base, not a moving branch name, when checking for edits during collection.
        await RequireUnchangedAsync(git, repository, changes, policy, policyDigest, cancellationToken);
        var proof = Path.Join(output, "proof");
        common["--operation"] = "run";
        common["--output"] = proof;
        common["--plan"] = planPath;
        if (plan.Profile.Producers.Count > 0)
        {
            var dotnet = ResolveDotnet(options.GetValueOrDefault("--dotnet"));
            common["--dotnet"] = dotnet;
            common["--repository"] = repository;
            common["--diff-base"] = changes.BaseCommit;
            if (plan.Profile.Producers.Any(producer => producer.Id == "solution-coverage"))
            {
                var patch = Path.Join(output, "changed.patch");
                await git.WriteLocalPatchAsync(repository, changes.BaseCommit, changes.Paths, patch, cancellationToken);
                common["--diff-file"] = patch;
            }
            if (RepositoryEvidenceWorkflow.RequiresPackage(plan.Profile))
            {
                var version = "0.0.0-local." + Guid.NewGuid().ToString("N");
                var feed = Path.Join(output, "feed");
                var package = Path.Join(feed, "ForgeTrust.AppSurface.Cli." + version + ".nupkg");
                var request = new EvidenceCommandRequest(dotnet,
                    ["pack", Path.Join(repository, "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"),
                    "--configuration", "Release", "--output", feed, "/p:RestoreLockedMode=true", "/p:PackageVersion=" + version,
                    "/p:RepositoryCommit=" + changes.HeadCommit, "/p:SourceRevisionId=" + changes.HeadCommit],
                    repository, TimeSpan.FromMinutes(10))
                {
                    MaximumOutputBytes = 4 * 1024 * 1024,
                    Environment = new RepositoryCheckSettings(repository, dotnet, output).CommandEnvironment,
                };
                var result = await _command(request, cancellationToken);
                await File.WriteAllTextAsync(Path.Join(output, "package-build.log"), result.StandardOutput + "\n" + result.StandardError, cancellationToken);
                ValidationProof.Require(result.ExitCode == 0 && !result.OutputTruncated && File.Exists(package), "local candidate package build");
                await RequireUnchangedAsync(git, repository, changes, policy, policyDigest, cancellationToken);
                common["--package"] = package;
                common["--version"] = version;
                common["--source-commit"] = changes.HeadCommit;
                common["--dotnet"] = dotnet;
                common["--repository"] = repository;
                common["--scratch-parent"] = Path.GetTempPath();
            }
        }
        var runExitCode = _workflow is null
            ? await RepositoryEvidenceWorkflow.RunAsync(common, cancellationToken,
                token => RequireUnchangedAsync(git, repository, changes, policy, policyDigest, token))
            : await _workflow(common, cancellationToken);
        ValidationProof.Require(runExitCode == 0, "local evidence execution");
        var manifestPath = Path.Join(proof, "evidence-manifest.json");
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(await File.ReadAllBytesAsync(manifestPath, cancellationToken));
        try
        {
            await RequireUnchangedAsync(git, repository, changes, policy, policyDigest, cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // Keep useful producer observations, but do not leave a locally gate-eligible claim for a moving source tree.
            var observation = EvidenceManifestBuilder.Build(plan, manifest.ProducerResults, observationOnly: true,
                envelopeStatus: manifest.EnvelopeStatus, resourceResults: manifest.ResourceResults, metrics: manifest.Metrics);
            await File.WriteAllBytesAsync(manifestPath, EvidenceCanonicalJson.Serialize(observation), CancellationToken.None);
            await File.AppendAllTextAsync(Path.Join(proof, "evidence-summary.md"), "\nSource changed during execution. Claim downgraded to ObservationOnly; rerun evidence.\n", CancellationToken.None);
            throw;
        }
        await RepositoryEvidenceWorkflow.VerifyAsync(plan, manifest, Path.Join(proof, "artifacts"), cancellationToken);
        Console.WriteLine($"Local evidence gate passed: {manifest.ClaimKind}. This run covers the current OS; CI requires its native matrix.");
        return 0;
    }

    private static async Task RequireUnchangedAsync(RepositoryGitChanges git, string repository, RepositoryChangeSet original,
        string policy, string policyDigest, CancellationToken cancellationToken)
    {
        var current = await git.CollectLocalAsync(repository, original.BaseCommit, cancellationToken);
        ValidationProof.Require(current.SourceDigest == original.SourceDigest
            && EvidenceDigest.Sha256(await File.ReadAllBytesAsync(policy, cancellationToken)) == policyDigest,
            "local source or policy changed during evidence; rerun from the new source state");
    }

    private static string ResolveDotnet(string? selected)
    {
        if (selected is not null)
        {
            if (!Path.IsPathFullyQualified(selected) || !File.Exists(selected)) throw new ArgumentException("Select an absolute existing dotnet host.");
            return selected;
        }
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(host) && Path.IsPathFullyQualified(host) && File.Exists(host)) return host;
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            .Select(directory => Path.Join(directory, executable)).FirstOrDefault(File.Exists)
            ?? throw new ArgumentException("Could not locate dotnet; supply --dotnet with an absolute host path.");
    }
}
