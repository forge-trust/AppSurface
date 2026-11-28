using System.Diagnostics;
using System.IO.Compression;
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
        var workflow = new LocalEvidenceWorkflow(workflow: async (options, cancellationToken, execution) =>
        {
            runCalls++;
            Assert.Equal("run", options["--operation"]);
            Assert.Null(execution.ManagedRun?.PackageRestoreCache);
            await execution.VerifySourceAsync(cancellationToken);
            return await BuildCompleteProofAsync(options, cancellationToken);
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SourceOrPolicyDriftAfterCompleteRun_PersistsValidObservationOnlyManifest(bool policyDrift, bool summaryUnavailable)
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "src/new-feature.cs", "class Feature { }");
        var runOptions = Options(fixture, "local", "coverage");
        var externalPolicy = TestPathUtils.PathUnder(_root, "external-policy.json");
        if (policyDrift)
        {
            File.Copy(runOptions["--policy"], externalPolicy);
            runOptions["--policy"] = externalPolicy;
        }
        var workflow = new LocalEvidenceWorkflow(workflow: async (options, cancellationToken, _) =>
        {
            var result = await BuildCompleteProofAsync(options, cancellationToken);
            if (summaryUnavailable)
            {
                var summary = TestPathUtils.PathUnder(options["--output"], "evidence-summary.md");
                File.Delete(summary);
                Directory.CreateDirectory(summary);
            }
            if (summaryUnavailable)
                File.Delete(externalPolicy);
            else if (policyDrift)
                await File.AppendAllTextAsync(externalPolicy, "\n", cancellationToken);
            else
                await ChangeAsync(fixture, "src/new-feature.cs", "class Feature { int Changed; }");
            return result;
        });

        var expectedFailure = summaryUnavailable ? typeof(FileNotFoundException) : typeof(InvalidOperationException);
        var failure = await Assert.ThrowsAsync(expectedFailure, () =>
            workflow.RunAsync(runOptions, CancellationToken.None));

        if (!summaryUnavailable)
            Assert.Contains("source or policy changed", failure.Message, StringComparison.Ordinal);
        var plan = ReadPlan(LastOutput);
        var manifest = ReadManifest(LastOutput);
        Assert.Equal(EvidenceClaimKind.ObservationOnly, manifest.ClaimKind);
        Assert.Equal(EvidenceExecutionVerdict.Passed, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimEligibility.Informational, manifest.Eligibility);
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
        var summaryPath = TestPathUtils.PathUnder(LastOutput, "proof", "evidence-summary.md");
        if (summaryUnavailable)
            Assert.False(File.Exists(summaryPath));
        else
            Assert.Contains("Claim downgraded to ObservationOnly", await File.ReadAllTextAsync(summaryPath));
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
        var workflow = new LocalEvidenceWorkflow(workflow: (_, _, _) => throw new Xunit.Sdk.XunitException("Invalid host must not execute producers."));

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
        var workflow = new LocalEvidenceWorkflow(workflow: async (runOptions, token, _) =>
        {
            Assert.Equal(host, runOptions["--dotnet"]);
            return await BuildCompleteProofAsync(runOptions, token);
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
    public async Task FailedRunWorkflow_DoesNotPublishAProofManifest()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "src/new-feature.cs", "class Feature { }");
        var workflow = new LocalEvidenceWorkflow(workflow: (_, _, _) => throw new InvalidOperationException("producer startup failed"));

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

    public void Dispose() => NativeEvidenceTestDirectory.Delete(_root);

    [Fact]
    public async Task ManagedOutput_ArchivesVerifiableProofFromRepositoryWorkflow()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "docs/guide.md", "local docs change");
        var options = Options(fixture, "local");
        options.Remove("--output");
        var workflow = new LocalEvidenceWorkflow((_, _) => throw new Xunit.Sdk.XunitException("Docs do not select a producer."));

        Assert.Equal(0, await workflow.RunAsync(options, CancellationToken.None));

        var retained = TestPathUtils.PathUnder(fixture.Root, "TestResults", "repository-evidence", "local");
        var extracted = TestPathUtils.PathUnder(_root, "extracted");
        ZipFile.ExtractToDirectory(TestPathUtils.PathUnder(retained, "latest.zip"), extracted);
        Assert.Equal(
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(extracted, "plan", "evidence-plan.json")),
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(extracted, "proof", "evidence-plan.json")));
        Assert.True(File.Exists(TestPathUtils.PathUnder(extracted, "proof", "evidence-summary.md")));
        Assert.True(File.Exists(TestPathUtils.PathUnder(extracted, "proof", "selection.json")));
        var manifest = ReadManifest(extracted);
        Assert.Equal(EvidenceClaimKind.NoEvidenceRequired, manifest.ClaimKind);
        await RepositoryEvidenceWorkflow.VerifyAsync(ReadPlan(extracted), manifest,
            TestPathUtils.PathUnder(extracted, "proof", "artifacts"), CancellationToken.None);
    }

    [Fact]
    public async Task ManagedOutput_ReportsRetainedArchivePath()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "docs/guide.md", "local docs change");
        var options = Options(fixture, "local");
        options.Remove("--output");
        var workflow = new LocalEvidenceWorkflow((_, _) => throw new Xunit.Sdk.XunitException("Docs do not select a producer."));
        var retainedArchive = TestPathUtils.PathUnder(fixture.Root, "TestResults", "repository-evidence", "local", "latest.zip");

        var result = await CaptureConsoleOutputAsync(() => workflow.RunAsync(options, CancellationToken.None));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["Local evidence output: " + retainedArchive], LocalEvidenceOutputLines(result.Output));
    }

    [Fact]
    public async Task ExplicitOutput_ReportsRawPath()
    {
        var fixture = await CreateFixtureAsync();
        var options = Options(fixture, "local");
        var outputPath = options["--output"];
        var workflow = new LocalEvidenceWorkflow();

        var result = await CaptureConsoleOutputAsync(() => workflow.RunAsync(options, CancellationToken.None));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["Local evidence output: " + outputPath], LocalEvidenceOutputLines(result.Output));
    }

    [Fact]
    public async Task LocalPlan_UsesThePolicySnapshotCapturedBeforeOutputAnnouncement()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "docs/guide.md", "local docs change");
        var options = Options(fixture, "local-plan");
        var policyPath = options["--policy"];
        var originalPolicy = EvidenceCanonicalJson.Deserialize<EvidencePolicy>(await File.ReadAllBytesAsync(policyPath));
        var replacementPolicy = originalPolicy with { Id = "replacement-policy" };
        var replacementBytes = EvidenceCanonicalJson.Serialize(replacementPolicy);
        var announcement = "Local evidence output: " + options["--output"];
        var policyReplaced = false;
        var workflow = new LocalEvidenceWorkflow();

        var result = await CaptureConsoleOutputAsync(() => workflow.RunAsync(options, CancellationToken.None), line =>
        {
            if (line != announcement) return;
            File.WriteAllBytes(policyPath, replacementBytes);
            policyReplaced = true;
        });

        Assert.Equal(0, result.ExitCode);
        Assert.True(policyReplaced);
        Assert.Equal([announcement], LocalEvidenceOutputLines(result.Output));
        using var sourceState = JsonDocument.Parse(await File.ReadAllTextAsync(TestPathUtils.PathUnder(options["--output"], "source-state.json")));
        var sourceDigest = sourceState.RootElement.GetProperty("sourceDigest").GetString();
        Assert.Equal(originalPolicy.Id + "-local-" + sourceDigest, ReadPlan(options["--output"]).PolicyId);
    }

    [Fact]
    public async Task LocalPlan_RejectsOversizedPolicyBeforeCreatingOutput()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "docs/guide.md", "local docs change");
        var options = Options(fixture, "local-plan");
        var policy = TestPathUtils.PathUnder(_root, "oversized-policy.json");
        using (var stream = File.Create(policy))
        {
            stream.SetLength(EvidenceCanonicalJson.MaximumInputBytes + 1L);
        }
        options["--policy"] = policy;

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new LocalEvidenceWorkflow().RunAsync(options, CancellationToken.None));

        Assert.Contains("size limit", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(LastOutput));
    }

    [Fact]
    public async Task ManagedOutput_ReplacesSuccessAndPreservesSeparateFailureAndPlan()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "src/feature.cs", "class Feature { }");
        var options = Options(fixture, "local", "coverage");
        options.Remove("--output");
        var run = 0;
        var workflow = new LocalEvidenceWorkflow(workflow: async (runOptions, token, _) =>
        {
            run++;
            var output = runOptions["--output"];
            Assert.False(Directory.Exists(output));
            var result = await BuildCompleteProofAsync(runOptions, token, failed: run == 2);
            var coverage = TestPathUtils.PathUnder(output, "coverage");
            Directory.CreateDirectory(coverage);
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(coverage, "coverage.cobertura.xml"), $"run {run}", token);
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(coverage, "coverage-run.log"), $"log {run}", token);
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(coverage, "generated.bin"), "scratch", token);
            var packageChecks = TestPathUtils.PathUnder(output, "package-checks");
            Directory.CreateDirectory(packageChecks);
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(packageChecks, "generated.md"), "scratch", token);
            return result;
        });
        var retained = TestPathUtils.PathUnder(fixture.Root, "TestResults", "repository-evidence", "local");

        Assert.Equal(0, await workflow.RunAsync(options, CancellationToken.None));
        Assert.Equal("run 1", ReadArchive(retained, "latest.zip", "proof/coverage/coverage.cobertura.xml"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RunAsync(options, CancellationToken.None));
        Assert.Equal("run 1", ReadArchive(retained, "latest.zip", "proof/coverage/coverage.cobertura.xml"));
        Assert.Equal("log 2", ReadArchive(retained, "latest-failure.zip", "proof/coverage/coverage-run.log"));

        options["--operation"] = "local-plan";
        Assert.Equal(0, await workflow.RunAsync(options, CancellationToken.None));
        Assert.NotNull(ReadArchive(retained, "latest-plan.zip", "plan/evidence-plan.json"));
        Assert.Equal(2, run);

        options["--operation"] = "local";
        Assert.Equal(0, await workflow.RunAsync(options, CancellationToken.None));
        Assert.Equal("run 3", ReadArchive(retained, "latest.zip", "proof/coverage/coverage.cobertura.xml"));
        Assert.Equal("log 2", ReadArchive(retained, "latest-failure.zip", "proof/coverage/coverage-run.log"));
        var extracted = TestPathUtils.PathUnder(_root, "extracted");
        ZipFile.ExtractToDirectory(TestPathUtils.PathUnder(retained, "latest.zip"), extracted);
        await RepositoryEvidenceWorkflow.VerifyAsync(ReadPlan(extracted), ReadManifest(extracted),
            TestPathUtils.PathUnder(extracted, "proof", "artifacts"), CancellationToken.None);
        Assert.Null(ReadArchive(retained, "latest.zip", "proof/coverage/generated.bin"));
        Assert.Null(ReadArchive(retained, "latest.zip", "proof/package-checks/generated.md"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(TestPathUtils.PathUnder(retained, ".scratch")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedOutput_RejectsChangedReplayInputBeforeReplacingProof(bool removeInput)
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "src/feature.cs", "class Feature { }");
        var options = Options(fixture, "local", "coverage");
        options.Remove("--output");
        var replaceInput = false;
        var workflow = new LocalEvidenceWorkflow(workflow: async (runOptions, token, _) =>
        {
            var result = await BuildCompleteProofAsync(runOptions, token);
            if (replaceInput)
            {
                var input = runOptions["--policy"];
                if (removeInput)
                    File.Delete(input);
                else
                {
                    var bytes = await File.ReadAllBytesAsync(input, token);
                    bytes[0] = (byte)'[';
                    await File.WriteAllBytesAsync(input, bytes, token);
                }
            }
            return result;
        });
        var retained = TestPathUtils.PathUnder(fixture.Root, "TestResults", "repository-evidence", "local");
        var proof = TestPathUtils.PathUnder(retained, "latest.zip");
        Assert.Equal(0, await workflow.RunAsync(options, CancellationToken.None));
        var prior = await File.ReadAllBytesAsync(proof);

        replaceInput = true;
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => workflow.RunAsync(options, CancellationToken.None));

        Assert.Contains("local-policy.json", failure.Message, StringComparison.Ordinal);
        Assert.Equal(prior, await File.ReadAllBytesAsync(proof));
        Assert.Contains(failure.Message, ReadArchive(retained, "latest-failure.zip", "run-status.txt"));
    }

    [Fact]
    public async Task ManagedOutput_CancellationRetainsOnlyAnObservationAlongsideThePriorProof()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "src/feature.cs", "class Feature { }");
        var options = Options(fixture, "local", "coverage");
        options.Remove("--output");
        using var cancellation = new CancellationTokenSource();
        var cancelAfterCollection = false;
        var workflow = new LocalEvidenceWorkflow(workflow: async (runOptions, token, _) =>
        {
            var result = await BuildCompleteProofAsync(runOptions, token);
            if (cancelAfterCollection)
                cancellation.Cancel();
            return result;
        });
        var retained = TestPathUtils.PathUnder(fixture.Root, "TestResults", "repository-evidence", "local");
        var proof = TestPathUtils.PathUnder(retained, "latest.zip");
        Assert.Equal(0, await workflow.RunAsync(options, CancellationToken.None));
        var prior = await File.ReadAllBytesAsync(proof);

        cancelAfterCollection = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workflow.RunAsync(options, cancellation.Token));

        Assert.Equal(prior, await File.ReadAllBytesAsync(proof));
        var manifest = ReadArchive(retained, "latest-cancelled.zip", "proof/evidence-manifest.json");
        Assert.NotNull(manifest);
        Assert.Equal(EvidenceClaimKind.ObservationOnly,
            EvidenceCanonicalJson.Deserialize<EvidenceManifest>(System.Text.Encoding.UTF8.GetBytes(manifest)).ClaimKind);
        Assert.False(File.Exists(TestPathUtils.PathUnder(fixture.Root, ".git", "appsurface-local-evidence", "run.pending-cleanup")));
    }

    [Fact]
    public async Task ManagedOutput_ForwardsCacheAndRetainsScratchWhenExecutorReportsUnconfirmedCleanup()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "src/feature.cs", "class Feature { }");
        var options = Options(fixture, "local", "coverage");
        options.Remove("--output");
        var retained = TestPathUtils.PathUnder(fixture.Root, "TestResults", "repository-evidence", "local");
        var workflow = new LocalEvidenceWorkflow(workflow: async (runOptions, token, execution) =>
        {
            var managed = Assert.IsType<LocalEvidenceOutput.ManagedRun>(execution.ManagedRun);
            Assert.Equal(TestPathUtils.PathUnder(retained, ".scratch", "package-restore"), managed.PackageRestoreCache);
            await execution.VerifySourceAsync(token);

            var failure = new EvidenceCommandException("cleanup_failed", "The child did not exit.");
            var command = managed.Settlement.Track((_, _) => Task.FromException<EvidenceCommandResult>(failure));
            var request = new EvidenceCommandRequest("test-command", [], fixture.Root, TimeSpan.FromSeconds(1));
            Assert.Same(failure, await Assert.ThrowsAsync<EvidenceCommandException>(() => command(request, token)));

            return await BuildCompleteProofAsync(runOptions, token);
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RunAsync(options, CancellationToken.None));

        Assert.False(File.Exists(TestPathUtils.PathUnder(retained, "latest.zip")));
        var scratch = Assert.Single(Directory.EnumerateDirectories(TestPathUtils.PathUnder(retained, ".scratch")));
        Assert.Equal(EvidenceClaimKind.ObservationOnly, ReadManifest(TestPathUtils.PathUnder(scratch, "run")).ClaimKind);
        var marker = TestPathUtils.PathUnder(fixture.Root, ".git", "appsurface-local-evidence", "run.pending-cleanup");
        Assert.True(File.Exists(marker));
        var retry = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RunAsync(options, CancellationToken.None));
        Assert.Contains(marker, retry.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.RunAsync(Options(fixture, "local", "coverage"), CancellationToken.None));
        Assert.False(Directory.Exists(LastOutput));

        options["--operation"] = "local-plan";
        Assert.Equal(0, await workflow.RunAsync(options, CancellationToken.None));
        Assert.True(File.Exists(marker));
        Assert.NotNull(ReadArchive(retained, "latest-plan.zip", "plan/evidence-plan.json"));
    }

    [Fact]
    public async Task ExplicitRun_RejectsANestedDirectoryBeforeCreatingOutput()
    {
        var fixture = await CreateFixtureAsync();
        var nested = Directory.CreateDirectory(TestPathUtils.PathUnder(fixture.Root, "nested")).FullName;
        var options = Options(fixture, "local");
        options["--repository"] = nested;

        var failure = await Assert.ThrowsAsync<ArgumentException>(() =>
            new LocalEvidenceWorkflow().RunAsync(options, CancellationToken.None));

        Assert.Contains("Git worktree root", failure.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(LastOutput));
    }

    [Fact]
    public async Task ManagedEmptyInventory_PreservesTheSuccessAndPlanArchives()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "docs/guide.md", "local docs change");
        var workflow = new LocalEvidenceWorkflow();
        var options = Options(fixture, "local");
        options.Remove("--output");
        var planOptions = new Dictionary<string, string>(options) { ["--operation"] = "local-plan" };
        var retained = TestPathUtils.PathUnder(fixture.Root, "TestResults", "repository-evidence", "local");
        var proof = TestPathUtils.PathUnder(retained, "latest.zip");
        var plan = TestPathUtils.PathUnder(retained, "latest-plan.zip");
        Assert.Equal(0, await workflow.RunAsync(options, CancellationToken.None));
        Assert.Equal(0, await workflow.RunAsync(planOptions, CancellationToken.None));
        var previousProof = await File.ReadAllBytesAsync(proof);
        var previousPlan = await File.ReadAllBytesAsync(plan);

        File.Delete(TestPathUtils.PathUnder(fixture.Root, "docs", "guide.md"));
        Assert.Equal(0, await workflow.RunAsync(options, CancellationToken.None));
        Assert.Equal(0, await workflow.RunAsync(planOptions, CancellationToken.None));

        Assert.Equal(previousProof, await File.ReadAllBytesAsync(proof));
        Assert.Equal(previousPlan, await File.ReadAllBytesAsync(plan));
        Assert.Empty(Directory.EnumerateDirectories(TestPathUtils.PathUnder(retained, ".scratch")));
    }

    [Fact]
    public async Task LinkedWorktree_UsesItsOwnCleanupMarker()
    {
        var fixture = await CreateFixtureAsync();
        var linked = new Fixture(TestPathUtils.PathUnder(_root, "linked"), fixture.Head);
        await GitAsync(fixture.Root, "worktree", "add", "--detach", "--quiet", linked.Root, fixture.Head);
        var policy = TestPathUtils.PathUnder(linked.Root, ".github", "evidence.policy.json");
        Assert.True(File.Exists(policy));
        var marker = TestPathUtils.PathUnder(fixture.Root, ".git", "appsurface-local-evidence", "run.pending-cleanup");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        await File.WriteAllTextAsync(marker, "primary worktree needs cleanup");
        await ChangeAsync(linked, "docs/guide.md", "linked docs change");
        var options = Options(linked, "local");
        options.Remove("--output");

        Assert.Equal(0, await new LocalEvidenceWorkflow().RunAsync(options, CancellationToken.None));

        var retained = TestPathUtils.PathUnder(linked.Root, "TestResults", "repository-evidence", "local");
        Assert.NotNull(ReadArchive(retained, "latest.zip", "proof/evidence-manifest.json"));
        Assert.True(File.Exists(marker));
        Assert.False(Directory.Exists(TestPathUtils.PathUnder(fixture.Root, "TestResults")));
    }

    [Fact]
    public async Task ManagedOutput_RejectsOverlappingRunInTheSameWorktree()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "src/feature.cs", "class Feature { }");
        var options = Options(fixture, "local", "coverage");
        options.Remove("--output");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = new LocalEvidenceWorkflow(workflow: async (runOptions, token, _) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
            return await BuildCompleteProofAsync(runOptions, token);
        });

        var active = workflow.RunAsync(options, CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await Assert.ThrowsAsync<IOException>(() => workflow.RunAsync(options, CancellationToken.None));
        }
        finally
        {
            release.TrySetResult();
            Assert.Equal(0, await active);
        }
    }

    private static string? ReadArchive(string root, string archiveName, string entryName)
    {
        using var archive = ZipFile.OpenRead(TestPathUtils.PathUnder(root, archiveName));
        var entry = archive.GetEntry(entryName);
        if (entry is null) return null;
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private string LastOutput => _lastOutput ?? throw new InvalidOperationException("Test output path was not initialized.");

    private static string[] LocalEvidenceOutputLines(string output) => output
        .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
        .Where(line => line.StartsWith("Local evidence output:", StringComparison.Ordinal))
        .ToArray();

    private static async Task<(int ExitCode, string Output)> CaptureConsoleOutputAsync(
        Func<Task<int>> run,
        Action<string?>? afterWriteLine = null)
    {
        var originalOutput = global::System.Console.Out;
        using var capturedOutput = new CallbackTextWriter(afterWriteLine ?? (_ => { }));
        try
        {
            global::System.Console.SetOut(capturedOutput);
            var exitCode = await run();
            return (exitCode, capturedOutput.ToString());
        }
        finally
        {
            global::System.Console.SetOut(originalOutput);
        }
    }

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

    private static async Task<EvidenceManifest> BuildCompleteProofAsync(IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken, bool failed = false)
    {
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(await File.ReadAllBytesAsync(options["--plan"], cancellationToken));
        var output = options["--output"];
        Directory.CreateDirectory(output);
        await File.WriteAllBytesAsync(TestPathUtils.PathUnder(output, "evidence-plan.json"), EvidenceCanonicalJson.Serialize(plan), cancellationToken);
        var results = new List<EvidenceProducerResult>();
        foreach (var producer in plan.Profile.Producers)
        {
            var writer = new EvidenceArtifactWriter(producer, TestPathUtils.PathUnder(output, "artifacts", producer.Id));
            foreach (var slot in producer.ArtifactSlots.Where(slot => slot.Required))
            {
                await writer.WriteAsync(slot.LogicalName, $"proof/{slot.LogicalName}.json", "{}"u8.ToArray(), cancellationToken);
            }

            var outcome = failed ? EvidenceProducerOutcome.Failed : EvidenceProducerOutcome.Passed;
            results.Add(new EvidenceProducerResult(producer.Id, outcome,
                failed ? [] : producer.AssertionIds, Artifacts: writer.WrittenArtifacts));
        }

        await File.WriteAllTextAsync(TestPathUtils.PathUnder(output, "evidence-summary.md"), "Producer results collected.\n", cancellationToken);
        return EvidenceManifestBuilder.Build(plan, results);
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

    private sealed class CallbackTextWriter(Action<string?> afterWriteLine) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            afterWriteLine(value);
        }
    }
}
