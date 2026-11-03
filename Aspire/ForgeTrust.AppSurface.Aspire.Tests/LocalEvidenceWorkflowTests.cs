using System.Diagnostics;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Validation;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

[Collection("Native evidence program")]
public sealed class LocalEvidenceWorkflowTests : IDisposable
{
    private const string PolicyPath = ".github/evidence.policy.json";
    private const string StoragePath = "tools/ForgeTrust.AppSurface.Evidence.Validation/PackedStorageProducer.cs";
    private readonly string _repo = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
    private readonly string _root = Directory.CreateTempSubdirectory("local-evidence-workflow-").FullName;
    private string? _lastOutput;

    [Fact]
    public async Task EmptyInventory_WritesSummaryWithoutPlanningOrPacking()
    {
        var fixture = await CreateFixtureAsync();
        var workflow = new LocalEvidenceWorkflow((_, _) => throw new Xunit.Sdk.XunitException("Empty inventory must not pack."));

        Assert.Equal(0, await workflow.RunAsync(Options(fixture, "local"), CancellationToken.None));
        var output = LastOutput;
        Assert.True(File.Exists(Path.Join(output, "changed-paths.json")));
        Assert.False(File.Exists(Path.Join(output, "plan", "evidence-plan.json")));
        Assert.False(File.Exists(Path.Join(output, "proof", "evidence-manifest.json")));
        Assert.Contains("No changed paths", await File.ReadAllTextAsync(Path.Join(output, "evidence-summary.md")));
    }

    [Fact]
    public async Task DocsOnly_LocalPlanAndLocalCompleteWithoutPackage_UseFreshDefaultOutput()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "docs/guide.md", "local docs change");
        var workflow = new LocalEvidenceWorkflow((_, _) => throw new Xunit.Sdk.XunitException("Docs do not select a producer."));

        Assert.Equal(0, await workflow.RunAsync(Options(fixture, "local-plan"), CancellationToken.None));
        var planOutput = LastOutput;
        Assert.Equal("no-evidence", ReadPlan(planOutput).Profile.Id);
        Assert.False(File.Exists(Path.Join(planOutput, "proof", "evidence-manifest.json")));

        Assert.Equal(0, await workflow.RunAsync(Options(fixture, "local"), CancellationToken.None));
        var runOutput = LastOutput;
        Assert.NotEqual(planOutput, runOutput);
        var manifest = ReadManifest(runOutput);
        Assert.Equal(EvidenceClaimKind.NoEvidenceRequired, manifest.ClaimKind);
        Assert.Empty(manifest.ProducerResults);
        using var source = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Join(runOutput, "source-state.json")));
        var digest = source.RootElement.GetProperty("sourceDigest").GetString();
        Assert.EndsWith("-local-" + digest, ReadPlan(runOutput).PolicyId);
        Assert.Equal(ReadPlan(runOutput).PlanDigest, manifest.PlanDigest);
    }

    [Fact]
    public async Task SourceChange_DefaultLocalPlanSelectsCoverageAndPackageChecksWithoutPacking()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "src/new-feature.cs", "class Feature { }");
        var calls = 0;
        var workflow = new LocalEvidenceWorkflow((_, _) =>
        {
            calls++;
            throw new Xunit.Sdk.XunitException("Planning must not invoke package command.");
        });

        Assert.Equal(0, await workflow.RunAsync(Options(fixture, "local-plan"), CancellationToken.None));
        var plan = ReadPlan(LastOutput);
        Assert.Equal(["package-checks", "solution-coverage"], plan.Profile.Producers.Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CoveragePlan_UsesRunSeamAndStillPassesRealManifestAndArtifactVerification()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "src/new-feature.cs", "class Feature { }");
        var runCalls = 0;
        var workflow = new LocalEvidenceWorkflow(workflow: async (options, cancellationToken) =>
        {
            runCalls++;
            Assert.Equal("run", options["--operation"]);
            await WriteCompleteProofAsync(options, cancellationToken);
            return 0;
        });

        Assert.Equal(0, await workflow.RunAsync(Options(fixture, "local", "coverage"), CancellationToken.None));

        Assert.Equal(1, runCalls);
        var plan = ReadPlan(LastOutput);
        Assert.Equal("solution-coverage", Assert.Single(plan.Profile.Producers).Id);
        var manifest = ReadManifest(LastOutput);
        Assert.Equal(EvidenceClaimKind.TargetedComplete, manifest.ClaimKind);
        Assert.Equal(EvidenceExecutionVerdict.Passed, manifest.ExecutionVerdict);
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
        Assert.True(File.Exists(Path.Join(LastOutput, "proof", "artifacts", "solution-coverage", "proof", "result.json")));
    }

    [Fact]
    public async Task SourceDriftAfterCompleteRun_PersistsValidObservationOnlyManifest()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "src/new-feature.cs", "class Feature { }");
        var workflow = new LocalEvidenceWorkflow(workflow: async (options, cancellationToken) =>
        {
            await WriteCompleteProofAsync(options, cancellationToken);
            await ChangeAsync(fixture, "src/new-feature.cs", "class Feature { int Changed; }");
            return 0;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.RunAsync(Options(fixture, "local", "coverage"), CancellationToken.None));

        var plan = ReadPlan(LastOutput);
        var manifest = ReadManifest(LastOutput);
        Assert.Equal(EvidenceClaimKind.ObservationOnly, manifest.ClaimKind);
        Assert.Equal(EvidenceExecutionVerdict.Passed, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimEligibility.Informational, manifest.Eligibility);
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
        Assert.Contains("Claim downgraded to ObservationOnly", await File.ReadAllTextAsync(Path.Join(LastOutput, "proof", "evidence-summary.md")));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("missing")]
    public async Task InvalidSelectedDotnetHost_IsRejectedBeforeProducerExecution(string selection)
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "src/feature.cs", "class Feature { }");
        var options = Options(fixture, "local", "coverage");
        options["--dotnet"] = selection == "relative" ? "dotnet" : Path.Join(_root, "missing-dotnet");
        var workflow = new LocalEvidenceWorkflow(workflow: (_, _) => throw new Xunit.Sdk.XunitException("Invalid host must not execute producers."));

        await Assert.ThrowsAsync<ArgumentException>(() => workflow.RunAsync(options, CancellationToken.None));

        Assert.False(File.Exists(Path.Join(LastOutput, "proof", "evidence-manifest.json")));
    }

    [Theory]
    [InlineData("selected")]
    [InlineData("environment")]
    [InlineData("path")]
    public async Task ValidDotnetHostSelection_IsPassedToTheProducer(string selection)
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "src/feature.cs", "class Feature { }");
        var options = Options(fixture, "local", "coverage");
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var host = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            .Select(directory => Path.Join(directory, executable)).First(File.Exists);
        host = Path.GetFullPath(host);
        if (selection == "selected") options["--dotnet"] = host;
        var previous = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        var workflow = new LocalEvidenceWorkflow(workflow: async (runOptions, token) =>
        {
            Assert.Equal(host, runOptions["--dotnet"]);
            await WriteCompleteProofAsync(runOptions, token);
            return 0;
        });

        try
        {
            Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", selection == "environment" ? host : "missing-relative-host");
            Assert.Equal(0, await workflow.RunAsync(options, CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", previous);
        }
    }

    [Fact]
    public async Task FailedRunWorkflowExitCode_DoesNotAcceptOrReadAProofManifest()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "src/new-feature.cs", "class Feature { }");
        var workflow = new LocalEvidenceWorkflow(workflow: (_, _) => Task.FromResult(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.RunAsync(Options(fixture, "local", "coverage"), CancellationToken.None));

        Assert.False(File.Exists(Path.Join(LastOutput, "proof", "evidence-manifest.json")));
    }

    [Fact]
    public async Task SameChangedPathsWithNewContent_ProduceADifferentBoundLocalPlan()
    {
        var fixture = await CreateFixtureAsync();
        var workflow = new LocalEvidenceWorkflow((_, _) => throw new Xunit.Sdk.XunitException("Planning must not pack."));
        await ChangeAsync(fixture, "docs/guide.md", "first version");
        await workflow.RunAsync(Options(fixture, "local-plan"), CancellationToken.None);
        var first = ReadPlan(LastOutput);
        await ChangeAsync(fixture, "docs/guide.md", "second version");
        await workflow.RunAsync(Options(fixture, "local-plan"), CancellationToken.None);
        var second = ReadPlan(LastOutput);
        Assert.Equal(first.DiffDigest, second.DiffDigest);
        Assert.NotEqual(first.PlanDigest, second.PlanDigest);
    }

    [Fact]
    public async Task CiCollectionEntryPoint_WritesInputsForTheSamePolicyPlanner()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "docs/guide.md", "committed docs");
        await GitAsync(fixture.Root, "add", ".");
        await GitAsync(fixture.Root, "commit", "-qm", "docs change");
        var head = (await GitAsync(fixture.Root, "rev-parse", "HEAD")).Trim();
        var eventFile = Path.Join(_root, "event.json");
        await File.WriteAllTextAsync(eventFile, JsonSerializer.Serialize(new { before = fixture.Head, after = head }));
        var collection = Path.Join(_root, "collected");
        Assert.Equal(0, await Program.RunAsync(["--operation", "collect", "--repository", fixture.Root,
            "--event-file", eventFile, "--event-name", "push", "--workflow-sha", head, "--output", collection]));
        var paths = EvidenceCanonicalJson.Deserialize<NormalizedDiffPath[]>(await File.ReadAllBytesAsync(Path.Join(collection, "changed-paths.json")));
        Assert.Equal("docs/guide.md", Assert.Single(paths).Path);
        using var state = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Join(collection, "source-state.json")));
        Assert.Equal(head, state.RootElement.GetProperty("headCommit").GetString());
        var planned = Path.Join(_root, "ci-plan");
        Assert.Equal(0, await Program.RunAsync(["--operation", "plan", "--policy", TestPathUtils.PathUnder(fixture.Root, PolicyPath),
            "--changed-paths", Path.Join(collection, "changed-paths.json"), "--output", planned]));
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(await File.ReadAllBytesAsync(Path.Join(planned, "evidence-plan.json")));
        Assert.Equal("no-evidence", plan.Profile.Id);
    }

    [Fact]
    public async Task StorageChange_PacksCandidateAndPersistsOnlySelectedProducerObservation()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, StoragePath, "// changed storage producer");
        EvidenceCommandRequest? request = null;
        var workflow = new LocalEvidenceWorkflow((command, _) =>
        {
            request = command;
            var outputIndex = command.Arguments.ToList().IndexOf("--output");
            Directory.CreateDirectory(command.Arguments[outputIndex + 1]);
            var version = command.Arguments.Single(argument => argument.StartsWith("/p:PackageVersion=", StringComparison.Ordinal))["/p:PackageVersion=".Length..];
            File.WriteAllText(Path.Join(command.Arguments[outputIndex + 1], $"ForgeTrust.AppSurface.Cli.{version}.nupkg"), "deliberately invalid package");
            return Task.FromResult(new EvidenceCommandResult(0, "pack output", "pack warning", 1));
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RunAsync(Options(fixture, "local", "native"), CancellationToken.None));

        Assert.NotNull(request);
        Assert.Contains("pack", request!.Arguments);
        Assert.Contains("--configuration", request.Arguments);
        var output = LastOutput;
        Assert.Contains("pack warning", await File.ReadAllTextAsync(Path.Join(output, "package-build.log")));
        var manifest = ReadManifest(output);
        Assert.Equal(["packed-storage"], manifest.ProducerResults.Select(item => item.ProducerId));
        Assert.NotEqual(EvidenceExecutionVerdict.Passed, manifest.ExecutionVerdict);
    }

    [Theory]
    [InlineData(1, false, true)]
    [InlineData(0, true, true)]
    [InlineData(0, false, false)]
    public async Task FailedTruncatedOrMissingPackage_RetainsPackageLogAndNoManifest(int exitCode, bool truncated, bool createPackage)
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, StoragePath, "// changed storage producer");
        var workflow = new LocalEvidenceWorkflow((command, _) =>
        {
            var index = command.Arguments.ToList().IndexOf("--output");
            if (createPackage)
            {
                Directory.CreateDirectory(command.Arguments[index + 1]);
                var version = command.Arguments.Single(argument => argument.StartsWith("/p:PackageVersion=", StringComparison.Ordinal))["/p:PackageVersion=".Length..];
                File.WriteAllText(Path.Join(command.Arguments[index + 1], $"ForgeTrust.AppSurface.Cli.{version}.nupkg"), "invalid");
            }
            return Task.FromResult(new EvidenceCommandResult(exitCode, "stdout", "stderr", 1) { OutputTruncated = truncated });
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RunAsync(Options(fixture, "local", "native"), CancellationToken.None));
        var output = LastOutput;
        Assert.Contains("stdout", await File.ReadAllTextAsync(Path.Join(output, "package-build.log")));
        Assert.False(File.Exists(Path.Join(output, "proof", "evidence-manifest.json")));
    }

    [Fact]
    public async Task SourceDriftDuringPackageBuild_RejectsBeforeManifestCreation()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, StoragePath, "// initial change");
        var workflow = new LocalEvidenceWorkflow((command, _) =>
        {
            File.WriteAllText(TestPathUtils.PathUnder(fixture.Root, StoragePath), "// changed while collecting");
            var index = command.Arguments.ToList().IndexOf("--output");
            Directory.CreateDirectory(command.Arguments[index + 1]);
            var version = command.Arguments.Single(argument => argument.StartsWith("/p:PackageVersion=", StringComparison.Ordinal))["/p:PackageVersion=".Length..];
            File.WriteAllText(Path.Join(command.Arguments[index + 1], $"ForgeTrust.AppSurface.Cli.{version}.nupkg"), "invalid package");
            return Task.FromResult(new EvidenceCommandResult(0, "built", string.Empty, 1));
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RunAsync(Options(fixture, "local", "native"), CancellationToken.None));
        Assert.False(File.Exists(Path.Join(LastOutput, "proof", "evidence-manifest.json")));
    }

    [Fact]
    public async Task InvalidOptionsOutputCollisionAndCancellationAreRejected()
    {
        var fixture = await CreateFixtureAsync();
        var workflow = new LocalEvidenceWorkflow();
        await Assert.ThrowsAsync<ArgumentException>(() => workflow.RunAsync(Options(fixture, "unknown"), CancellationToken.None));
        var unsupported = Options(fixture, "local");
        unsupported["--unexpected"] = "x";
        await Assert.ThrowsAsync<ArgumentException>(() => workflow.RunAsync(unsupported, CancellationToken.None));
        var collision = TestPathUtils.PathUnder(_root, "existing");
        Directory.CreateDirectory(collision);
        var collisionOptions = Options(fixture, "local");
        collisionOptions["--output"] = collision;
        await Assert.ThrowsAsync<ArgumentException>(() => workflow.RunAsync(collisionOptions, CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workflow.RunAsync(Options(fixture, "local"), cancelled.Token));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string LastOutput => _lastOutput ?? throw new InvalidOperationException("Test output path was not initialized.");

    private async Task<Fixture> CreateFixtureAsync()
    {
        var root = TestPathUtils.PathUnder(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(TestPathUtils.PathUnder(root, ".gitignore"), "TestResults/\nbin/\nobj/\n");
        var policy = TestPathUtils.PathUnder(root, ".github", "evidence.policy.json");
        Directory.CreateDirectory(Path.GetDirectoryName(policy)!);
        File.Copy(TestPathUtils.PathUnder(_repo, ".github", "evidence.policy.json"), policy);
        await GitAsync(root, "init", "-q");
        await GitAsync(root, "config", "user.email", "evidence-tests@example.invalid");
        await GitAsync(root, "config", "user.name", "Evidence Tests");
        await GitAsync(root, "add", ".");
        await GitAsync(root, "commit", "-qm", "baseline");
        var head = await GitAsync(root, "rev-parse", "HEAD");
        return new Fixture(root, head.Trim());
    }

    private static async Task ChangeAsync(Fixture fixture, string relativePath, string content)
    {
        var path = TestPathUtils.PathUnder(fixture.Root, relativePath.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private Dictionary<string, string> Options(Fixture fixture, string operation, string? scope = null)
    {
        _lastOutput = TestPathUtils.PathUnder(_root, "output", Guid.NewGuid().ToString("N"));
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--operation"] = operation,
            ["--repository"] = fixture.Root,
            ["--base"] = fixture.Head,
            ["--policy"] = TestPathUtils.PathUnder(fixture.Root, ".github", "evidence.policy.json"),
            ["--output"] = _lastOutput,
            ["--scope"] = scope ?? "all",
        };
    }

    private static EvidencePlan ReadPlan(string output) => EvidenceCanonicalJson.Deserialize<EvidencePlan>(File.ReadAllBytes(Path.Join(output, "plan", "evidence-plan.json")));
    private static EvidenceManifest ReadManifest(string output) => EvidenceCanonicalJson.Deserialize<EvidenceManifest>(File.ReadAllBytes(Path.Join(output, "proof", "evidence-manifest.json")));

    private static async Task WriteCompleteProofAsync(IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(await File.ReadAllBytesAsync(options["--plan"], cancellationToken));
        var output = options["--output"];
        Directory.CreateDirectory(output);
        await File.WriteAllBytesAsync(Path.Join(output, "evidence-plan.json"), EvidenceCanonicalJson.Serialize(plan), cancellationToken);
        var results = new List<EvidenceProducerResult>();
        foreach (var producer in plan.Profile.Producers)
        {
            var writer = new EvidenceArtifactWriter(producer, Path.Join(output, "artifacts", producer.Id));
            foreach (var slot in producer.ArtifactSlots.Where(slot => slot.Required))
            {
                await writer.WriteAsync(slot.LogicalName, $"proof/{slot.LogicalName}.json", "{}"u8.ToArray(), cancellationToken);
            }

            results.Add(new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Passed,
                producer.AssertionIds, Artifacts: writer.WrittenArtifacts));
        }

        var manifest = EvidenceManifestBuilder.Build(plan, results);
        await File.WriteAllBytesAsync(Path.Join(output, "evidence-manifest.json"), EvidenceCanonicalJson.Serialize(manifest), cancellationToken);
        await File.WriteAllTextAsync(Path.Join(output, "evidence-summary.md"), $"Claim: `{manifest.ClaimKind}`.\n", cancellationToken);
    }

    private static async Task<string> GitAsync(string workingDirectory, params string[] arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(await stderr);
        return await stdout;
    }

    private sealed record Fixture(string Root, string Head);
}
