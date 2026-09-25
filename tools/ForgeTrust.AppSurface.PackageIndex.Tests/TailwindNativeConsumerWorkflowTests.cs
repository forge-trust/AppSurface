using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class TailwindNativeConsumerWorkflowTests : IDisposable
{
    private readonly string _root = TestPathUtils.PathUnder(TailwindTestPaths.TemporaryRoot, "tailwind-native-consumer-workflow", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ProducerClosure_MustMatchTheValidatedPackagePlan()
    {
        var entry = new PackageArtifactManifestEntry("ForgeTrust.AppSurface.Web.Tailwind", "Web/Tailwind.csproj", "publish",
            "ForgeTrust.AppSurface.Web.Tailwind.1.2.3.nupkg", new string('a', 128), false);
        var planned = new PlannedPackageArtifact(entry, TestPathUtils.PathUnder(_root, entry.ArtifactFileName));
        var package = new TailwindSubjectPackage(entry.PackageId, "1.2.3", entry.ArtifactFileName, entry.Sha512);

        TailwindNativeConsumerWorkflow.ValidatePlannedProducerClosure([planned], [package]);

        foreach (var invalid in new[]
        {
            package with { PackageId = "ForgeTrust.Other" },
            package with { ArtifactFileName = "substituted.nupkg" },
            package with { PackageSha512 = new string('b', 128) }
        })
        {
            var error = Assert.Throws<PackageIndexException>(() =>
                TailwindNativeConsumerWorkflow.ValidatePlannedProducerClosure([planned], [invalid]));
            Assert.Contains("does not match the validated package plan", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RestoredClosure_MustMatchTheBoundProducerSubject()
    {
        var package = new TailwindSubjectPackage("ForgeTrust.AppSurface.Web.Tailwind", "1.2.3", "tailwind.nupkg", new string('a', 128));

        TailwindNativeConsumerWorkflow.RequireSameClosure([package], [package]);

        var countError = Assert.Throws<PackageIndexException>(() => TailwindNativeConsumerWorkflow.RequireSameClosure([package], []));
        Assert.Contains("closure differs", countError.Message, StringComparison.Ordinal);
        var hashError = Assert.Throws<PackageIndexException>(() => TailwindNativeConsumerWorkflow.RequireSameClosure(
            [package], [package with { PackageSha512 = new string('b', 128) }]));
        Assert.Contains("differs from producer subject", hashError.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-assets", "has no assets array")]
    [InlineData("missing-version", "version is absent")]
    [InlineData("missing-binary", "missing its binary identity")]
    public void RestoredReleaseManifest_RequiresSelectedHostBinaryIdentity(string mutation, string expectedDiagnostic)
    {
        var selected = $"{{\"rid\":\"{CurrentRid()}\",\"binaryName\":\"tailwindcss\",\"sha256\":\"{new string('a', 64)}\"}}";
        var manifest = mutation switch
        {
            "missing-assets" => "{\"version\":\"4.1.18\"}",
            "missing-version" => $"{{\"assets\":[{selected}]}}",
            "missing-binary" => $"{{\"version\":\"4.1.18\",\"assets\":[{{\"rid\":\"{CurrentRid()}\"}}]}}",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown release-manifest mutation.")
        };
        var archivePath = WriteManifestArchive("restored-" + mutation, manifest);

        var error = Assert.Throws<PackageIndexException>(() => TailwindNativeConsumerWorkflow.ReadRidAsset(archivePath, CurrentRid()));

        Assert.Contains(expectedDiagnostic, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-assets", "has no assets array")]
    [InlineData("missing-binary", "does not define exactly one binary identity")]
    public void ProducerReleaseManifest_RequiresSelectedHostBinaryIdentity(string mutation, string expectedDiagnostic)
    {
        var manifest = mutation switch
        {
            "missing-assets" => "{\"version\":\"4.1.18\"}",
            "missing-binary" => $"{{\"assets\":[{{\"rid\":\"{CurrentRid()}\"}}]}}",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown release-manifest mutation.")
        };
        var archivePath = WriteManifestArchive("producer-" + mutation, manifest);

        var error = Assert.Throws<PackageIndexException>(() => TailwindEvidenceWorkflow.ReadExpectedBinary(archivePath, CurrentRid()));

        Assert.Contains(expectedDiagnostic, error.Message, StringComparison.Ordinal);
    }

    private string WriteManifestArchive(string name, string manifest)
    {
        Directory.CreateDirectory(_root);
        var path = TestPathUtils.PathUnder(_root, name + ".nupkg");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        AddEntry(archive, "build/tailwind.release.json", Encoding.UTF8.GetBytes(manifest));
        return path;
    }

    [Fact]
    public async Task LocalMode_UsesCompatibilityProofAndMarksEvidenceIneligibleForRelease()
    {
        var manifest = await WriteManifestAsync();
        var runner = new LocalProofRunner();
        var report = TestPathUtils.PathUnder(_root, "local-report");
        var result = await TailwindNativeConsumerWorkflow.RunAsync(_root, Path.GetDirectoryName(manifest)!, manifest,
            Options("local", report), runner, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("local-only", result.Status);
        Assert.Equal("bash", Assert.Single(runner.Requests).FileName);
        Assert.Contains("scripts/verify-tailwind-package-consumer.sh", runner.Requests[0].Arguments);
        using var evidence = JsonDocument.Parse(await File.ReadAllBytesAsync(result.ReportPath));
        Assert.Equal("appsurface-tailwind-local-proof-v1", evidence.RootElement.GetProperty("schema").GetString());
        Assert.False(evidence.RootElement.GetProperty("releaseEligible").GetBoolean());
        Assert.True(File.Exists(TestPathUtils.PathUnder(report, "diagnostics.json")));
    }

    [Fact]
    public async Task LocalProofFailure_WritesFailedDiagnosticWithoutReceipt()
    {
        var manifest = await WriteManifestAsync();
        var runner = new LocalProofRunner(fail: true);
        var report = TestPathUtils.PathUnder(_root, "failed-local-report");
        var result = await TailwindNativeConsumerWorkflow.RunAsync(_root, Path.GetDirectoryName(manifest)!, manifest,
            Options("local", report), runner, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("failed", result.Status);
        Assert.False(File.Exists(result.ReportPath));
        using var diagnostic = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(report, "diagnostics.json")));
        Assert.Equal("failed", diagnostic.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task BoundedCommandRunner_ForwardsReleaseCaptureAndSuccessfulOutput()
    {
        var inner = new RecordingExternalCommandRunner(new ExternalCommandResult(0, "stdout", "stderr"));
        var runner = new TailwindNativeConsumerWorkflow.TailwindBoundedCommandRunner(inner);
        var environment = new Dictionary<string, string?> { ["NUGET_PACKAGES"] = "/private/cache" };
        using var cancellation = new CancellationTokenSource();
        var request = new CommandRunRequest("dotnet", ["restore", "proof.csproj"], "/work", "dotnet restore",
            "Tailwind native consumer", "restore", "restoring the proof project", 30_000, environment);

        var result = await runner.RunAsync(request, cancellation.Token);

        Assert.Equal(new CommandRunResult("stdout", "stderr"), result);
        var forwarded = Assert.IsType<ExternalCommandRequest>(inner.Request);
        Assert.Equal(request.FileName, forwarded.FileName);
        Assert.Equal(request.Arguments, forwarded.Arguments);
        Assert.Equal(request.WorkingDirectory, forwarded.WorkingDirectory);
        Assert.Equal(request.OperationName, forwarded.OperationName);
        Assert.Equal(request.TimeoutDescription, forwarded.TimeoutDescription);
        Assert.Equal(request.TimeoutMilliseconds, forwarded.TimeoutMilliseconds);
        Assert.Same(environment, forwarded.Environment);
        Assert.Same(ExternalCapturePolicy.ReleaseProof, forwarded.CapturePolicy);
        Assert.Equal(cancellation.Token, inner.CancellationToken);
    }

    [Fact]
    public async Task BoundedCommandRunner_RejectsNonzeroExitAndKeepsBoundedCapture()
    {
        var inner = new RecordingExternalCommandRunner(new ExternalCommandResult(17, "partial stdout", "failed stderr"));
        var runner = new TailwindNativeConsumerWorkflow.TailwindBoundedCommandRunner(inner);
        var request = new CommandRunRequest("dotnet", ["build", "proof.csproj"], "/work", "dotnet build",
            "Tailwind native consumer", "build", "building the proof project", 45_000);

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => runner.RunAsync(request, CancellationToken.None));

        Assert.Contains("dotnet build failed", error.Message, StringComparison.Ordinal);
        Assert.Contains("exit 17", error.Message, StringComparison.Ordinal);
        Assert.Contains("failed stderr", error.Message, StringComparison.Ordinal);
        Assert.Contains("partial stdout", error.Message, StringComparison.Ordinal);
        Assert.Same(ExternalCapturePolicy.ReleaseProof, inner.Request!.CapturePolicy);
    }

    [Fact]
    public async Task UnknownMode_FailsBeforeRunningConsumer()
    {
        var manifest = await WriteManifestAsync();
        var runner = new LocalProofRunner();
        var result = await TailwindNativeConsumerWorkflow.RunAsync(_root, Path.GetDirectoryName(manifest)!, manifest,
            Options("unknown", TestPathUtils.PathUnder(_root, "unknown-report")), runner, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task ReleaseMode_RejectsUnsupportedRidAfterValidatingProducerBinding()
    {
        var producer = await CreateProducerBundleAsync();
        var runner = new LocalProofRunner();
        var report = TestPathUtils.PathUnder(_root, "unsupported-rid-report");

        var result = await TailwindNativeConsumerWorkflow.RunAsync(producer.Repository, producer.Bundle, producer.ManifestPath,
            ReleaseOptions(producer, "not-a-rid", "unsupported-rid", report), runner, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("failed", result.Status);
        Assert.Empty(runner.Requests);
        using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(report, "diagnostics.json")));
        Assert.Equal("failed", diagnostics.RootElement.GetProperty("status").GetString());
        Assert.Equal("producer-binding", diagnostics.RootElement.GetProperty("stage").GetString());
        Assert.Contains("Unsupported expected RID", diagnostics.RootElement.GetProperty("errors")[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task ReleaseMode_RejectsHostMismatchBeforeStartingRestore()
    {
        var producer = await CreateProducerBundleAsync();
        var runner = new LocalProofRunner();
        var report = TestPathUtils.PathUnder(_root, "host-mismatch-report");
        var unsupportedOnThisHost = SupportedRids.First(rid => rid != CurrentRid());

        var result = await TailwindNativeConsumerWorkflow.RunAsync(producer.Repository, producer.Bundle, producer.ManifestPath,
            ReleaseOptions(producer, unsupportedOnThisHost, "host-mismatch", report), runner, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(runner.Requests);
        using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(report, "diagnostics.json")));
        Assert.Equal("producer-binding", diagnostics.RootElement.GetProperty("stage").GetString());
        Assert.Contains("Native host mismatch", diagnostics.RootElement.GetProperty("errors")[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task ReleaseMode_DefaultValidatorRejectsOnePackageFixtureBeforeRestore()
    {
        var producer = await CreateProducerBundleAsync();
        var runner = new LocalProofRunner();
        var report = TestPathUtils.PathUnder(_root, "default-validator-report");

        var result = await TailwindNativeConsumerWorkflow.RunAsync(producer.Repository, producer.Bundle, producer.ManifestPath,
            ReleaseOptions(producer, CurrentRid(), "default-validator", report), runner, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(runner.Requests);
        using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(report, "diagnostics.json")));
        Assert.Equal("producer-binding", diagnostics.RootElement.GetProperty("stage").GetString());
        Assert.Contains("package plan", diagnostics.RootElement.GetProperty("errors")[0].GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReleaseMode_RejectsMalformedArtifactManifestBeforeStartingRestore()
    {
        var producer = await CreateProducerBundleAsync();
        await File.WriteAllTextAsync(producer.ManifestPath, "{");
        var runner = new LocalProofRunner();
        var report = TestPathUtils.PathUnder(_root, "malformed-manifest-report");

        var result = await TailwindNativeConsumerWorkflow.RunAsync(producer.Repository, producer.Bundle, producer.ManifestPath,
            ReleaseOptions(producer, CurrentRid(), "malformed-manifest", report), runner, CancellationToken.None,
            ValidateFixtureProducerArtifactsAsync, producer.LockTemplatePath);

        Assert.False(result.Succeeded);
        Assert.Equal("failed", result.Status);
        Assert.Empty(runner.Requests);
        Assert.False(File.Exists(result.ReportPath));
        using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(report, "diagnostics.json")));
        Assert.Equal("producer-binding", diagnostics.RootElement.GetProperty("stage").GetString());
    }

    [Fact]
    public async Task ReleaseMode_RestoresPrivatePackageCacheBuildsAndWritesNativeReceipt()
    {
        var producer = await CreateProducerBundleAsync();
        var runner = new NativeReleaseRunner(producer, CurrentRid());
        var report = TestPathUtils.PathUnder(_root, "release-success-report");

        var result = await TailwindNativeConsumerWorkflow.RunAsync(producer.Repository, producer.Bundle, producer.ManifestPath,
            ReleaseOptions(producer, CurrentRid(), "release-success", report), runner, CancellationToken.None,
            ValidateFixtureProducerArtifactsAsync, producer.LockTemplatePath);

        Assert.True(result.Succeeded, await File.ReadAllTextAsync(TestPathUtils.PathUnder(report, "summary.md")));
        Assert.Equal("succeeded", result.Status);
        Assert.Equal(["sdk-version", "restore", "locked restore", "build"], runner.Requests.Select(request => request.FailureVerb));
        using var receipt = JsonDocument.Parse(await File.ReadAllBytesAsync(result.ReportPath));
        Assert.Equal("appsurface-tailwind-native-host-proof-v2", receipt.RootElement.GetProperty("schema").GetString());
        Assert.Equal("succeeded", receipt.RootElement.GetProperty("status").GetString());
        Assert.Equal(CurrentRid(), receipt.RootElement.GetProperty("observedRid").GetString());
        Assert.Equal("native-test-release-success", receipt.RootElement.GetProperty("nativeInvocationId").GetString());
        Assert.Equal(Environment.GetEnvironmentVariable("GITHUB_RUN_ID") is { Length: > 0 } runId ? runId : "901",
            receipt.RootElement.GetProperty("nativeRunId").GetString());
        Assert.Equal(Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") is { Length: > 0 } attempt ? attempt : "1",
            receipt.RootElement.GetProperty("nativeAttempt").GetString());
        Assert.True(receipt.RootElement.GetProperty("checks").GetProperty("postBuildPayloadUnchanged").GetBoolean());
        Assert.True(receipt.RootElement.GetProperty("checks").GetProperty("generatedCss").GetBoolean());
        Assert.True(receipt.RootElement.GetProperty("checks").GetProperty("hostCacheBinary").GetBoolean());
        Assert.True(receipt.RootElement.GetProperty("checks").GetProperty("noRuntimeCompanionDependency").GetBoolean());
        Assert.True(receipt.RootElement.GetProperty("checks").GetProperty("noNativeConsumerOutput").GetBoolean());
        Assert.Equal("10.0.100", receipt.RootElement.GetProperty("sdkVersion").GetString());
        Assert.Equal(BinaryName(CurrentRid()), receipt.RootElement.GetProperty("binaryName").GetString());
        Assert.False(string.IsNullOrEmpty(receipt.RootElement.GetProperty("binarySha256").GetString()));
        Assert.Single(receipt.RootElement.GetProperty("firstPartyPackages").EnumerateArray());
        Assert.Contains(receipt.RootElement.GetProperty("files").EnumerateArray(), file =>
            file.GetProperty("path").GetString() == "consumer/wwwroot/css/site.gen.css");
        Assert.True(File.Exists(TestPathUtils.PathUnder(runner.ConsumerDirectory!, "wwwroot", "css", "site.gen.css")));
        Assert.True(File.Exists(TestPathUtils.PathUnder(report, "diagnostics.json")));
    }

    [Fact]
    public async Task ReleaseMode_RejectsProducerClosureAbsentFromReviewedLockBeforeRestore()
    {
        var producer = await CreateProducerBundleAsync();
        var runner = new NativeReleaseRunner(producer, CurrentRid());
        var report = TestPathUtils.PathUnder(_root, "lock-closure-report");

        var result = await TailwindNativeConsumerWorkflow.RunAsync(producer.Repository, producer.Bundle, producer.ManifestPath,
            ReleaseOptions(producer, CurrentRid(), "lock-closure", report), runner, CancellationToken.None,
            ValidateFixtureProducerArtifactsAsync, TestPathUtils.PathUnder(FindRepositoryRoot(), TailwindConsumerLock.RelativeTemplatePath));

        Assert.False(result.Succeeded);
        Assert.Empty(runner.Requests);
        using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(report, "diagnostics.json")));
        Assert.Equal("consumer-lock-preparation", diagnostics.RootElement.GetProperty("stage").GetString());
        Assert.Contains("first-party closure", diagnostics.RootElement.GetProperty("errors")[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task ReleaseMode_RejectsProtectedPayloadMutationAfterBuildWithoutReceipt()
    {
        var producer = await CreateProducerBundleAsync();
        var runner = new NativeReleaseRunner(producer, CurrentRid(), mutateProtectedPayloadAfterBuild: true);
        var report = TestPathUtils.PathUnder(_root, "release-mutation-report");

        var result = await TailwindNativeConsumerWorkflow.RunAsync(producer.Repository, producer.Bundle, producer.ManifestPath,
            ReleaseOptions(producer, CurrentRid(), "release-mutation", report), runner, CancellationToken.None,
            ValidateFixtureProducerArtifactsAsync, producer.LockTemplatePath);

        Assert.False(result.Succeeded);
        Assert.Equal("failed", result.Status);
        Assert.Equal(["sdk-version", "restore", "locked restore", "build"], runner.Requests.Select(request => request.FailureVerb));
        Assert.False(File.Exists(result.ReportPath));
        using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(report, "diagnostics.json")));
        Assert.Equal("post-build-revalidation", diagnostics.RootElement.GetProperty("stage").GetString());
        Assert.Contains("Expanded first-party package payload bytes changed", diagnostics.RootElement.GetProperty("errors")[0].GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("empty-sdk", "sdk-version", "Native host .NET SDK version is empty")]
    [InlineData("missing-lock", "initial-restore", "missing or is not a regular file")]
    [InlineData("missing-css", "native-build", "did not generate fresh nonempty")]
    [InlineData("empty-css", "native-build", "did not generate fresh nonempty")]
    [InlineData("missing-binary", "native-build", "did not acquire the expected host cache binary")]
    [InlineData("missing-output-root", "native-build", "did not produce both bin and obj directories")]
    public async Task ReleaseMode_ReportsFailureAtTheStageWhoseRequiredOutputIsMissing(
        string failure, string expectedStage, string expectedMessage)
    {
        var producer = await CreateProducerBundleAsync();
        var runner = new NativeReleaseRunner(producer, CurrentRid(), failure: failure);
        var report = TestPathUtils.PathUnder(_root, "release-" + failure + "-report");

        var result = await TailwindNativeConsumerWorkflow.RunAsync(producer.Repository, producer.Bundle, producer.ManifestPath,
            ReleaseOptions(producer, CurrentRid(), failure, report), runner, CancellationToken.None,
            ValidateFixtureProducerArtifactsAsync, producer.LockTemplatePath);

        Assert.False(result.Succeeded);
        Assert.Equal("failed", result.Status);
        Assert.False(File.Exists(result.ReportPath));
        using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(report, "diagnostics.json")));
        Assert.Equal(expectedStage, diagnostics.RootElement.GetProperty("stage").GetString());
        Assert.Contains(expectedMessage, diagnostics.RootElement.GetProperty("errors")[0].GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("restore-failed", "initial-restore", "simulated initial restore failure", "sdk-version,restore")]
    [InlineData("changed-lock", "initial-restore", "changed during locked restore", "sdk-version,restore")]
    [InlineData("locked-restore-failed", "locked-restore", "simulated locked restore failure", "sdk-version,restore,locked restore")]
    [InlineData("unsafe-package-path", "restored-graph-and-payload", "unsafe package path", "sdk-version,restore,locked restore")]
    [InlineData("external-package-folder", "restored-graph-and-payload", "designated fresh private NuGet cache", "sdk-version,restore,locked restore")]
    [InlineData("multiple-package-folders", "restored-graph-and-payload", "designated fresh private NuGet cache", "sdk-version,restore,locked restore")]
    [InlineData("missing-package-path", "restored-graph-and-payload", "no package path", "sdk-version,restore,locked restore")]
    [InlineData("missing-package-folders", "restored-graph-and-payload", "given key was not present", "sdk-version,restore,locked restore")]
    [InlineData("missing-package-directory", "restored-graph-and-payload", "escapes the private cache or is absent", "sdk-version,restore,locked restore")]
    [InlineData("missing-cache-archive", "restored-graph-and-payload", "does not contain exactly the expected archive", "sdk-version,restore,locked restore")]
    [InlineData("extra-cache-archive", "restored-graph-and-payload", "does not contain exactly the expected archive", "sdk-version,restore,locked restore")]
    [InlineData("changed-cache-archive", "restored-graph-and-payload", "restored archive SHA-512", "sdk-version,restore,locked restore")]
    [InlineData("prebuild-payload-mismatch", "restored-graph-and-payload", "payload bytes changed", "sdk-version,restore,locked restore")]
    [InlineData("wrong-binary-hash", "native-build", "cache binary SHA-256 differs", "sdk-version,restore,locked restore,build")]
    [InlineData("changed-assets-after-build", "native-build", "assets graph changed during", "sdk-version,restore,locked restore,build")]
    [InlineData("native-executable-in-output", "native-build", "executable was copied into consumer build output", "sdk-version,restore,locked restore,build")]
    [InlineData("changed-cache-archive-after-build", "post-build-revalidation", "Restored archive", "sdk-version,restore,locked restore,build")]
    [InlineData("renamed-cache-archive-after-build", "post-build-revalidation", "changed its expected basename", "sdk-version,restore,locked restore,build")]
    public async Task ReleaseMode_FailsClosedForRestoreCachePayloadAndBuildMutations(
        string failure, string expectedStage, string expectedMessage, string expectedCommands)
    {
        await AssertReleaseModeRejectsMutationAsync(failure, expectedStage, expectedMessage, expectedCommands);
    }

    [LinkSupportTheory]
    [InlineData("linked-assets-file", "restored-graph-and-payload", "not a regular file", "sdk-version,restore,locked restore")]
    [InlineData("linked-cache-binary-after-build", "native-build", "link/reparse point", "sdk-version,restore,locked restore,build")]
    [InlineData("linked-cache-directory-after-build", "native-build", "link/reparse point", "sdk-version,restore,locked restore,build")]
    public async Task ReleaseMode_RejectsLinksInRestoredAndBuiltEvidence(
        string failure, string expectedStage, string expectedMessage, string expectedCommands)
    {
        await AssertReleaseModeRejectsMutationAsync(failure, expectedStage, expectedMessage, expectedCommands);
    }

    private async Task AssertReleaseModeRejectsMutationAsync(
        string failure, string expectedStage, string expectedMessage, string expectedCommands)
    {
        var producer = await CreateProducerBundleAsync();
        var runner = new NativeReleaseRunner(producer, CurrentRid(), failure: failure);
        var report = TestPathUtils.PathUnder(_root, "release-" + failure + "-report");

        var result = await TailwindNativeConsumerWorkflow.RunAsync(producer.Repository, producer.Bundle, producer.ManifestPath,
            ReleaseOptions(producer, CurrentRid(), failure, report), runner, CancellationToken.None,
            ValidateFixtureProducerArtifactsAsync, producer.LockTemplatePath);

        Assert.False(result.Succeeded);
        Assert.Equal("failed", result.Status);
        Assert.False(File.Exists(result.ReportPath));
        Assert.Equal(expectedCommands.Split(','), runner.Requests.Select(request => request.FailureVerb));
        using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(report, "diagnostics.json")));
        Assert.Equal(expectedStage, diagnostics.RootElement.GetProperty("stage").GetString());
        Assert.Contains(expectedMessage, diagnostics.RootElement.GetProperty("errors")[0].GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string[] SupportedRids = ["linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64"];
    private static readonly byte[] FakeCliBytes = Encoding.UTF8.GetBytes("fixture tailwind cli bytes");

    private TailwindCommandOptions ReleaseOptions(ProducerBundle producer, string rid, string name, string report)
        => TailwindCommandOptions.Extract([
            "--mode", "release",
            "--producer-subject", TailwindProofSubjectService.FileName,
            "--producer-artifact-id", "501",
            "--expected-subject-sha256", producer.SubjectSha256,
            "--repository-id", "12345",
            "--producer-run-id", "901",
            "--producer-attempt", "1",
            "--source-commit", producer.SourceCommit,
            "--native-invocation-id", "native-test-" + name,
            "--expected-rid", rid,
            "--work-directory", TestPathUtils.PathUnder(_root, "work-" + name),
            "--report-directory", report
        ]);

    private async Task<ProducerBundle> CreateProducerBundleAsync()
    {
        var sourceRepository = FindRepositoryRoot();
        var repository = TestPathUtils.PathUnder(_root, "clean-source-" + Guid.NewGuid().ToString("N"));
        await CloneSourceAsync(sourceRepository, repository);
        var commit = await RunGitAsync(repository, "rev-parse");
        var bundle = TestPathUtils.PathUnder(_root, "producer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bundle);
        var sourceManifest = CreateSyntheticReleaseManifest();
        var packageId = "ForgeTrust.AppSurface.Web.Tailwind";
        var fileName = $"{packageId}.1.2.3.nupkg";
        var archivePath = TestPathUtils.PathUnder(bundle, fileName);
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            AddEntry(archive, "build/", []);
            AddEntry(archive, "build/tailwind.release.json", sourceManifest);
            AddEntry(archive, "build/tailwind.version", Encoding.UTF8.GetBytes("4.1.18"));
            AddEntry(archive, "build/ForgeTrust.AppSurface.Web.Tailwind.targets", Encoding.UTF8.GetBytes("<Project />"));
            AddEntry(archive, "contentFiles/any/any/tailwind.css", Encoding.UTF8.GetBytes(".fixture{color:red}"));
        }

        var packageHash = PackageHash.ComputeSha512(archivePath);
        var lockTemplatePath = TestPathUtils.PathUnder(bundle, "fixture-consumer.lock.json");
        var lockTemplate = new
        {
            version = 1,
            dependencies = new Dictionary<string, object>
            {
                ["net10.0"] = new Dictionary<string, object>
                {
                    [packageId] = new
                    {
                        type = "Direct",
                        requested = "[1.2.3, )",
                        resolved = "1.2.3",
                        contentHash = Convert.ToBase64String(Convert.FromHexString(packageHash))
                    }
                }
            }
        };
        await File.WriteAllBytesAsync(lockTemplatePath, JsonSerializer.SerializeToUtf8Bytes(lockTemplate));
        var entry = new PackageArtifactManifestEntry(packageId,
            "Web/ForgeTrust.AppSurface.Web.Tailwind/ForgeTrust.AppSurface.Web.Tailwind.csproj",
            "publish", fileName, packageHash, false);
        var manifest = new PackageArtifactManifest(1, "1.2.3", DateTimeOffset.Parse("2026-09-23T00:00:00Z"), [entry]);
        var manifestPath = TestPathUtils.PathUnder(bundle, "package-artifact-manifest.json");
        await File.WriteAllBytesAsync(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, PackageArtifactJson.Options));
        var closure = new[] { new TailwindSubjectPackage(packageId, "1.2.3", fileName, packageHash) };
        var subject = await TailwindProofSubjectService.CreateAsync(bundle, manifestPath, "12345", "901", "1", commit,
            closure, CancellationToken.None);
        var subjectHash = await TailwindProofSubjectService.WriteAsync(subject, bundle, CancellationToken.None);
        return new ProducerBundle(repository, bundle, manifestPath, commit, subjectHash, lockTemplatePath);
    }

    private static byte[] CreateSyntheticReleaseManifest()
    {
        var hash = Convert.ToHexString(SHA256.HashData(FakeCliBytes)).ToLowerInvariant();
        var assets = SupportedRids.Select(rid => new
        {
            rid,
            binaryName = BinaryName(rid),
            sha256 = hash
        }).ToArray();
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            version = "4.1.18",
            baseUrl = "https://example.invalid/tailwind-test",
            assets
        });
    }

    private static string BinaryName(string rid) => rid switch
    {
        "linux-x64" => "tailwindcss-linux-x64",
        "linux-arm64" => "tailwindcss-linux-arm64",
        "osx-x64" => "tailwindcss-macos-x64",
        "osx-arm64" => "tailwindcss-macos-arm64",
        "win-x64" => "tailwindcss-windows-x64.exe",
        _ => throw new ArgumentOutOfRangeException(nameof(rid), rid, "Unsupported test RID.")
    };

    private static Task ValidateFixtureProducerArtifactsAsync(
        string repositoryRoot,
        string artifactsInputPath,
        PackageArtifactManifest manifest,
        IReadOnlyList<TailwindSubjectPackage> producerClosure,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Single(producerClosure);
        var closurePackage = Assert.Single(producerClosure);
        var entry = Assert.Single(manifest.Entries);
        Assert.Equal(closurePackage.PackageId, entry.PackageId);
        Assert.Equal(closurePackage.ArtifactFileName, entry.ArtifactFileName);
        Assert.Equal(closurePackage.PackageSha512, entry.Sha512);
        Assert.True(File.Exists(TestPathUtils.PathUnder(artifactsInputPath, entry.ArtifactFileName)));
        Assert.True(File.Exists(TestPathUtils.PathUnder(repositoryRoot, "packages", "package-index.yml")));
        return Task.CompletedTask;
    }

    private static string CurrentRid()
    {
        var os = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "Unknown";
        var architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();
        var processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        return (os, architecture) switch
        {
            ("Linux", "X64") => "linux-x64",
            ("Linux", "Arm64") => "linux-arm64",
            ("macOS", "X64") => "osx-x64",
            ("macOS", "Arm64") => "osx-arm64",
            ("Windows", "X64") when processArchitecture == "X64" => "win-x64",
            ("Windows", "Arm64") when processArchitecture == "X64" => "win-x64",
            _ => throw new InvalidOperationException($"Unsupported test host {os}/{architecture}.")
        };
    }

    private static void AddEntry(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "packages", "package-index.yml")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static async Task<string> RunGitAsync(string directory, string argument)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(argument);
        start.ArgumentList.Add("HEAD");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
        return output.Trim();
    }

    private static async Task CloneSourceAsync(string source, string destination)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = source, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "clone", "--local", "--no-hardlinks", "--quiet", source, destination })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git clone.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git clone failed: {error}{output}");
    }

    private sealed class RecordingExternalCommandRunner(ExternalCommandResult result) : IExternalCommandRunner
    {
        public ExternalCommandRequest? Request { get; private set; }
        public CancellationToken CancellationToken { get; private set; }

        public Task<ExternalCommandResult> RunAsync(ExternalCommandRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            CancellationToken = cancellationToken;
            return Task.FromResult(result);
        }
    }

    private sealed record ProducerBundle(string Repository, string Bundle, string ManifestPath, string SourceCommit, string SubjectSha256, string LockTemplatePath);

    private sealed class NativeReleaseRunner(ProducerBundle producer, string rid, bool mutateProtectedPayloadAfterBuild = false,
        string? failure = null) : ICommandRunner
    {
        private const string PackageId = "ForgeTrust.AppSurface.Web.Tailwind";
        private const string PackageVersion = "1.2.3";
        private const string TailwindVersion = "4.1.18";
        private readonly string _packageFileName = $"{PackageId}.{PackageVersion}.nupkg";

        public List<CommandRunRequest> Requests { get; } = [];
        public string? ConsumerDirectory { get; private set; }
        private string? _restoredPackageDirectory;
        private string? _cachedArchivePath;
        private string? _assetsPath;

        public async Task<CommandRunResult> RunAsync(CommandRunRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            ConsumerDirectory = request.WorkingDirectory;
            if (request.FailureVerb == "sdk-version") return new CommandRunResult(failure == "empty-sdk" ? string.Empty : "10.0.100", string.Empty);
            if (request.FailureVerb is "restore" or "locked restore")
            {
                Assert.Contains("--locked-mode", request.Arguments);
                Assert.DoesNotContain("--force-evaluate", request.Arguments);
                if (failure == "restore-failed" && request.FailureVerb == "restore")
                    throw new PackageIndexException("simulated initial restore failure");
                if (request.FailureVerb == "restore") await CreateRestoreOutputsAsync(request, cancellationToken);
                if (failure == "locked-restore-failed" && request.FailureVerb == "locked restore")
                    throw new PackageIndexException("simulated locked restore failure");
                return new CommandRunResult(string.Empty, string.Empty);
            }
            Assert.Equal("build", request.FailureVerb);
            if (failure != "missing-css")
                await File.WriteAllTextAsync(TestPathUtils.PathUnder(request.WorkingDirectory, "wwwroot", "css", "site.gen.css"),
                    failure == "empty-css" ? string.Empty : ".generated{color:red}", cancellationToken);
            var outputDirectory = TestPathUtils.PathUnder(request.WorkingDirectory, "bin", "Release", "net10.0");
            if (failure != "missing-output-root") Directory.CreateDirectory(outputDirectory);
            var work = Directory.GetParent(request.WorkingDirectory)!.FullName;
            if (failure != "missing-binary")
            {
                var binary = TestPathUtils.PathUnder(work, "tailwind-cache", "tailwind-" + TailwindVersion, rid, BinaryName(rid));
                if (failure == "linked-cache-directory-after-build")
                {
                    var versionDirectory = TestPathUtils.PathUnder(work, "tailwind-cache", "tailwind-" + TailwindVersion);
                    Directory.CreateDirectory(versionDirectory);
                    var outsideDirectory = TestPathUtils.PathUnder(work, "outside-cache-directory");
                    Directory.CreateDirectory(outsideDirectory);
                    await File.WriteAllBytesAsync(TestPathUtils.PathUnder(outsideDirectory, BinaryName(rid)), FakeCliBytes, cancellationToken);
                    Directory.CreateSymbolicLink(Path.Combine(versionDirectory, rid), outsideDirectory);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
                    var contents = failure == "wrong-binary-hash" ? Encoding.UTF8.GetBytes("unexpected tailwind bytes") : FakeCliBytes;
                    await File.WriteAllBytesAsync(binary, contents, cancellationToken);
                }
            }
            if (failure == "changed-assets-after-build")
                await File.AppendAllTextAsync(_assetsPath!, " ", cancellationToken);
            if (failure == "native-executable-in-output")
                await File.WriteAllTextAsync(TestPathUtils.PathUnder(outputDirectory, "tailwindcss-fixture"), "must stay in cache", cancellationToken);
            if (failure == "changed-cache-archive-after-build")
                await File.AppendAllTextAsync(_cachedArchivePath!, "changed after restore", cancellationToken);
            if (failure == "renamed-cache-archive-after-build")
                File.Move(_cachedArchivePath!, TestPathUtils.PathUnder(_restoredPackageDirectory!, "renamed-after-build.nupkg"));
            if (failure == "linked-cache-binary-after-build")
            {
                var binary = TestPathUtils.PathUnder(work, "tailwind-cache", "tailwind-" + TailwindVersion, rid, BinaryName(rid));
                File.Delete(binary);
                var target = TestPathUtils.PathUnder(work, "outside-cache-binary");
                await File.WriteAllBytesAsync(target, FakeCliBytes, cancellationToken);
                File.CreateSymbolicLink(binary, target);
            }
            if (mutateProtectedPayloadAfterBuild)
            {
                Assert.NotNull(_restoredPackageDirectory);
                var protectedFile = TestPathUtils.PathUnder(_restoredPackageDirectory!, "build", "tailwind.version");
                await File.AppendAllTextAsync(protectedFile, "-tampered", cancellationToken);
            }
            return new CommandRunResult(string.Empty, string.Empty);
        }

        private async Task CreateRestoreOutputsAsync(CommandRunRequest request, CancellationToken cancellationToken)
        {
            var environment = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(request.Environment);
            var packageCache = environment["NUGET_PACKAGES"]!;
            _restoredPackageDirectory = TestPathUtils.PathUnder(packageCache, PackageId.ToLowerInvariant(), PackageVersion.ToLowerInvariant());
            Directory.CreateDirectory(_restoredPackageDirectory);
            var cachedArchive = TestPathUtils.PathUnder(_restoredPackageDirectory, _packageFileName);
            _cachedArchivePath = cachedArchive;
            File.Copy(TestPathUtils.PathUnder(producer.Bundle, _packageFileName), cachedArchive);
            ZipFile.ExtractToDirectory(cachedArchive, _restoredPackageDirectory);
            if (failure == "missing-cache-archive") File.Delete(cachedArchive);
            if (failure == "extra-cache-archive")
                await File.WriteAllTextAsync(TestPathUtils.PathUnder(_restoredPackageDirectory, "unexpected.nupkg"), "unexpected cache archive", cancellationToken);
            if (failure == "changed-cache-archive")
                await File.AppendAllTextAsync(cachedArchive, "changed before verification", cancellationToken);
            if (failure == "prebuild-payload-mismatch")
                await File.AppendAllTextAsync(TestPathUtils.PathUnder(_restoredPackageDirectory, "build", "tailwind.version"), "changed before build", cancellationToken);

            var packagePath = $"{PackageId.ToLowerInvariant()}/{PackageVersion}";
            var nodeKey = $"{PackageId}/{PackageVersion}";
            var targetNode = new Dictionary<string, object>
            {
                ["type"] = "package",
                ["dependencies"] = new Dictionary<string, string>(),
                ["build"] = new Dictionary<string, object>()
            };
            var packageFolders = new Dictionary<string, object>
            {
                [Path.GetFullPath(packageCache) + Path.DirectorySeparatorChar] = new Dictionary<string, object>()
            };
            if (failure == "multiple-package-folders")
                packageFolders[Path.GetFullPath(packageCache + "-external") + Path.DirectorySeparatorChar] = new Dictionary<string, object>();
            if (failure == "external-package-folder")
            {
                packageFolders.Clear();
                packageFolders[Path.GetFullPath(packageCache + "-external") + Path.DirectorySeparatorChar] = new Dictionary<string, object>();
            }
            var assets = new
            {
                targets = new Dictionary<string, object> { ["net10.0"] = new Dictionary<string, object> { [nodeKey] = targetNode } },
                libraries = new Dictionary<string, object>
                {
                    [nodeKey] = new
                    {
                        type = "package",
                        path = failure switch
                        {
                            "unsafe-package-path" => "../outside",
                            "missing-package-path" => (string?)null,
                            "missing-package-directory" => "missing/1.2.3",
                            _ => packagePath
                        }
                    }
                },
                packageFolders,
                project = new
                {
                    frameworks = new Dictionary<string, object> { ["net10.0"] = new Dictionary<string, object>() },
                    restore = new Dictionary<string, object>()
                }
            };
            _assetsPath = TestPathUtils.PathUnder(request.WorkingDirectory, "obj", "project.assets.json");
            Directory.CreateDirectory(Path.GetDirectoryName(_assetsPath)!);
            if (failure == "missing-package-folders")
            {
                var missingFolders = JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(assets))!.AsObject();
                missingFolders.Remove("packageFolders");
                await File.WriteAllTextAsync(_assetsPath, missingFolders.ToJsonString(), cancellationToken);
            }
            else
            {
                await File.WriteAllBytesAsync(_assetsPath, JsonSerializer.SerializeToUtf8Bytes(assets), cancellationToken);
            }
            if (failure == "linked-assets-file")
            {
                var linkedAssetsTarget = TestPathUtils.PathUnder(Directory.GetParent(request.WorkingDirectory)!.FullName, "linked-project.assets.json");
                File.Move(_assetsPath, linkedAssetsTarget);
                File.CreateSymbolicLink(_assetsPath, linkedAssetsTarget);
            }
            if (failure == "missing-lock")
                File.Delete(TestPathUtils.PathUnder(request.WorkingDirectory, "packages.lock.json"));
            if (failure == "changed-lock")
                await File.AppendAllTextAsync(TestPathUtils.PathUnder(request.WorkingDirectory, "packages.lock.json"), " ", cancellationToken);
        }
    }

    private TailwindCommandOptions Options(string mode, string report)
        => TailwindCommandOptions.Extract([
            "--mode", mode,
            "--work-directory", TestPathUtils.PathUnder(_root, "work-" + mode),
            "--report-directory", report
        ]);

    private async Task<string> WriteManifestAsync()
    {
        var bundle = TestPathUtils.PathUnder(_root, "bundle");
        Directory.CreateDirectory(bundle);
        var entry = new PackageArtifactManifestEntry("ForgeTrust.AppSurface.Web.Tailwind",
            "Web/ForgeTrust.AppSurface.Web.Tailwind/ForgeTrust.AppSurface.Web.Tailwind.csproj",
            "publish", "ForgeTrust.AppSurface.Web.Tailwind.1.2.3.nupkg", new string('a', 128), false);
        var manifest = new PackageArtifactManifest(1, "1.2.3", DateTimeOffset.Parse("2026-09-23T00:00:00Z"), [entry]);
        var path = TestPathUtils.PathUnder(bundle, "package-artifact-manifest.json");
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(manifest, PackageArtifactJson.Options));
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class LocalProofRunner(bool fail = false) : ICommandRunner
    {
        public List<CommandRunRequest> Requests { get; } = [];

        public async Task<CommandRunResult> RunAsync(CommandRunRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (fail) throw new PackageIndexException("local consumer failed");
            var reportIndex = Array.IndexOf(request.Arguments.ToArray(), "--report-path");
            Assert.True(reportIndex >= 0);
            var report = request.Arguments[reportIndex + 1];
            await File.WriteAllTextAsync(report, "# Local Tailwind consumer proof\nSuccess.\n", cancellationToken);
            return new CommandRunResult("ok", string.Empty);
        }
    }
}

/// <summary>Discovers link mutation assertions only when file and directory symlinks are available.</summary>
public sealed class LinkSupportTheoryAttribute : TheoryAttribute
{
    public LinkSupportTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = TestPathUtils.PathUnder(Path.GetTempPath(), "tailwind-link-probe", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var file = TestPathUtils.PathUnder(root, "target-file");
            File.WriteAllText(file, "target");
            var directory = TestPathUtils.PathUnder(root, "target-directory");
            Directory.CreateDirectory(directory);
            File.CreateSymbolicLink(TestPathUtils.PathUnder(root, "file-link"), file);
            Directory.CreateSymbolicLink(TestPathUtils.PathUnder(root, "directory-link"), directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Skip = $"Requires file and directory symlink support: {exception.Message}";
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
