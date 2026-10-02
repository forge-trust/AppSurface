using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

/// <summary>Focused contract tests for the exact-bundle durable preflight carrier.</summary>
public sealed class DurablePreflightArtifactProofTests : IDisposable
{
    private const string Version = "1.2.3-ci.4";
    private const string SourceCommit = "0123456789abcdef0123456789abcdef01234567";
    private const string RunId = "run-845";
    private const string ArtifactId = "artifact-845";
    private readonly string _root = TestPathUtils.PathUnder(Path.GetTempPath(), "durable-preflight-proof", Guid.NewGuid().ToString("N"));

    public DurablePreflightArtifactProofTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Theory]
    [InlineData("candidate")]
    [InlineData("promote")]
    public void CommandOptions_ResolvesCandidatePathsAgainstDefaultRepositoryRoot(string mode)
    {
        var options = DurablePreflightArtifactProofCommandOptions.Parse(ProofArguments(mode).ToArray(), _root);

        Assert.Equal(mode, options.Mode);
        Assert.Equal(_root, options.Request.RepositoryRoot);
        Assert.Equal(TestPathUtils.PathUnder(_root, "bundle"), options.Request.ArtifactDirectory);
        Assert.Equal(TestPathUtils.PathUnder(_root, "bundle", "unapproved.json"), options.Request.ArtifactManifestPath);
        Assert.Equal(TestPathUtils.PathUnder(_root, "approved.json"), options.Request.ApprovedManifestPath);
        Assert.Equal(TestPathUtils.PathUnder(_root, "candidate-receipt.json"), options.Request.ReceiptPath);
        Assert.Equal(SourceCommit, options.Request.SourceCommit);
        Assert.Equal(RunId, options.Request.RunId);
        Assert.Equal(ArtifactId, options.Request.ArtifactId);
        Assert.Null(options.CandidateReceiptPath);
        Assert.Null(options.RestoredPackagesPath);
        Assert.Null(options.PublishedReceiptPath);
    }

    [Fact]
    public void CommandOptions_PublishedResolvesRelativeInputsAgainstExplicitRootAndKeepsAbsolutePaths()
    {
        var repositoryRoot = TestPathUtils.PathUnder(_root, "repo with spaces");
        var restoredPackages = TestPathUtils.PathUnder(_root, "isolated packages");
        var publishedReceipt = TestPathUtils.PathUnder(_root, "public proof", "receipt.json");
        var arguments = ProofArguments("published");
        arguments.AddRange([
            "--repo-root", repositoryRoot,
            "--candidate-receipt", "proof/candidate.json",
            "--restored-packages", restoredPackages,
            "--published-receipt", publishedReceipt
        ]);

        var options = DurablePreflightArtifactProofCommandOptions.Parse(arguments.ToArray(), _root);

        Assert.Equal("published", options.Mode);
        Assert.Equal(repositoryRoot, options.Request.RepositoryRoot);
        Assert.Equal(TestPathUtils.PathUnder(repositoryRoot, "bundle"), options.Request.ArtifactDirectory);
        Assert.Equal(TestPathUtils.PathUnder(repositoryRoot, "bundle", "unapproved.json"), options.Request.ArtifactManifestPath);
        Assert.Equal(TestPathUtils.PathUnder(repositoryRoot, "approved.json"), options.Request.ApprovedManifestPath);
        Assert.Equal(TestPathUtils.PathUnder(repositoryRoot, "candidate-receipt.json"), options.Request.ReceiptPath);
        Assert.Equal(TestPathUtils.PathUnder(repositoryRoot, "proof", "candidate.json"), options.CandidateReceiptPath);
        Assert.Equal(restoredPackages, options.RestoredPackagesPath);
        Assert.Equal(publishedReceipt, options.PublishedReceiptPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Candidate")]
    [InlineData("smoke")]
    public void CommandOptions_RequiresSupportedExplicitMode(string? mode)
    {
        var arguments = ProofArguments();
        SetProofOption(arguments, "--mode", mode);

        var exception = Assert.Throws<PackageIndexException>(() =>
            DurablePreflightArtifactProofCommandOptions.Parse(arguments.ToArray(), _root));

        Assert.Contains("--mode candidate, promote, or published", exception.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, string?> MissingRequiredProofOptions()
    {
        var cases = new TheoryData<string, string?>();
        foreach (var option in new[]
        {
            "--artifact-dir", "--artifact-manifest", "--approved-manifest", "--receipt",
            "--source-commit", "--run-id", "--artifact-id"
        })
        {
            cases.Add(option, null);
            cases.Add(option, "");
            cases.Add(option, " \t");
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(MissingRequiredProofOptions))]
    public void CommandOptions_RequiresEveryBundleAndProvenanceInput(string option, string? value)
    {
        var arguments = ProofArguments();
        SetProofOption(arguments, option, value);

        var exception = Assert.Throws<PackageIndexException>(() =>
            DurablePreflightArtifactProofCommandOptions.Parse(arguments.ToArray(), _root));

        Assert.Contains($"requires '{option}'", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void CommandOptions_PublishedRequiresAllRetainedAndRestoredEvidencePaths(bool candidate, bool restored, bool published)
    {
        var arguments = ProofArguments("published");
        if (candidate) arguments.AddRange(["--candidate-receipt", "proof/candidate.json"]);
        if (restored) arguments.AddRange(["--restored-packages", "nuget"]);
        if (published) arguments.AddRange(["--published-receipt", "proof/public.json"]);

        var exception = Assert.Throws<PackageIndexException>(() =>
            DurablePreflightArtifactProofCommandOptions.Parse(arguments.ToArray(), _root));

        Assert.Contains("--candidate-receipt, --restored-packages, and --published-receipt", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--candidate-receipt", "")]
    [InlineData("--restored-packages", " \t")]
    [InlineData("--published-receipt", "")]
    public void CommandOptions_PublishedRejectsBlankEvidencePath(string option, string value)
    {
        var arguments = ProofArguments("published");
        arguments.AddRange([
            "--candidate-receipt", "proof/candidate.json",
            "--restored-packages", "nuget",
            "--published-receipt", "proof/public.json"
        ]);
        SetProofOption(arguments, option, value);

        Assert.Throws<PackageIndexException>(() =>
            DurablePreflightArtifactProofCommandOptions.Parse(arguments.ToArray(), _root));
    }

    [Fact]
    public void CommandOptions_RejectsUnknownFlagWithoutReturningARequest()
    {
        var arguments = ProofArguments();
        arguments.AddRange(["--skip-proof", "true"]);

        var exception = Assert.Throws<PackageIndexException>(() =>
            DurablePreflightArtifactProofCommandOptions.Parse(arguments.ToArray(), _root));

        Assert.Contains("Unknown preflight artifact proof option '--skip-proof'", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommandOptions_RejectsValueMissingAtEndOrReplacedByAnotherFlag(bool nextFlag)
    {
        var arguments = ProofArguments();
        SetProofOption(arguments, "--artifact-dir", null);
        arguments.Add("--artifact-dir");
        if (nextFlag) arguments.AddRange(["--repo-root", _root]);

        var exception = Assert.Throws<PackageIndexException>(() =>
            DurablePreflightArtifactProofCommandOptions.Parse(arguments.ToArray(), _root));

        Assert.Contains("Option '--artifact-dir' requires a value", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--repo-root")]
    [InlineData("--artifact-dir")]
    [InlineData("--artifact-manifest")]
    [InlineData("--approved-manifest")]
    [InlineData("--receipt")]
    [InlineData("--candidate-receipt")]
    [InlineData("--restored-packages")]
    [InlineData("--published-receipt")]
    public void CommandOptions_RejectsNullByteInFilesystemPath(string option)
    {
        var arguments = ProofArguments("published");
        arguments.AddRange([
            "--repo-root", _root,
            "--candidate-receipt", "proof/candidate.json",
            "--restored-packages", "nuget",
            "--published-receipt", "proof/public.json"
        ]);
        SetProofOption(arguments, option, "malformed\0path");

        Assert.Throws<ArgumentException>(() =>
            DurablePreflightArtifactProofCommandOptions.Parse(arguments.ToArray(), _root));
    }

    [Theory]
    [InlineData("--source-commit", "HEAD")]
    [InlineData("--source-commit", "0123456789abcdef")]
    [InlineData("--source-commit", "0123456789abcdef0123456789abcdef0123456g")]
    [InlineData("--source-commit", "0123456789abcdef0123456789abcdef01234567\n")]
    [InlineData("--run-id", "run/845")]
    [InlineData("--run-id", "run 845")]
    [InlineData("--artifact-id", "artifact/845")]
    [InlineData("--artifact-id", "artifact\n845")]
    public async Task CommandOptions_ParsedUnsafeIdentityFailsBeforeConsumerOrApproval(string option, string value)
    {
        var fixture = await ProofFixture.CreateAsync(_root);
        var arguments = ProofArgumentsFor(fixture.Request);
        SetProofOption(arguments, option, value);
        var options = DurablePreflightArtifactProofCommandOptions.Parse(arguments.ToArray(), _root);
        var runner = new FakeRunner((_, _) => throw new InvalidOperationException("An invalid identity must never reach the consumer."));

        await Assert.ThrowsAsync<PackageIndexException>(() =>
            new DurablePreflightArtifactProof(runner).RunCandidateAsync(options.Request, CancellationToken.None));

        Assert.Equal(0, runner.Calls);
        Assert.True(File.Exists(fixture.Request.ArtifactManifestPath));
        Assert.False(File.Exists(options.Request.ApprovedManifestPath));
        Assert.False(File.Exists(options.Request.ReceiptPath));
    }

    [Fact]
    public async Task CommandOptions_ParsedSha256SourceIdentityRunsExactCandidateProof()
    {
        var sourceCommit = new string('a', 64);
        var fixture = await ProofFixture.CreateAsync(_root, packageCommit: sourceCommit);
        var arguments = ProofArgumentsFor(fixture.Request);
        SetProofOption(arguments, "--source-commit", sourceCommit);
        var options = DurablePreflightArtifactProofCommandOptions.Parse(arguments.ToArray(), _root);
        var runner = new FakeRunner(async (request, token) =>
        {
            var receipt = await fixture.CompleteReceiptJsonAsync(sourceCommit: sourceCommit);
            await File.WriteAllTextAsync(request.Arguments[Array.IndexOf(request.Arguments.ToArray(), "--receipt") + 1], receipt, token);
            return new ExternalCommandResult(0, "issue-845-proof=passed\n", string.Empty);
        });

        var result = await new DurablePreflightArtifactProof(runner).RunCandidateAsync(options.Request, CancellationToken.None);

        Assert.Equal(1, runner.Calls);
        Assert.Equal(sourceCommit, runner.LastRequest!.Arguments[Array.IndexOf(runner.LastRequest.Arguments.ToArray(), "--source-commit") + 1]);
        Assert.Equal(fixture.ManifestSha256, result.ArtifactManifestSha256);
        Assert.True(File.Exists(options.Request.ApprovedManifestPath));
        Assert.True(File.Exists(options.Request.ReceiptPath));
    }

    [Fact]
    public async Task CommandOptions_ParsedPromotionRevalidatesRetainedProofAndCopiesExactManifest()
    {
        var fixture = await ProofFixture.CreateAsync(_root);
        var unapproved = TestPathUtils.PathUnder(fixture.ArtifactDirectory, "unapproved.json");
        var manifestBytes = await File.ReadAllBytesAsync(fixture.Request.ArtifactManifestPath);
        File.Move(fixture.Request.ArtifactManifestPath, unapproved);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Request.ApprovedManifestPath)!);
        await File.WriteAllBytesAsync(fixture.Request.ApprovedManifestPath, manifestBytes);
        await File.WriteAllTextAsync(fixture.Request.ReceiptPath, await fixture.CompleteReceiptJsonAsync());
        var arguments = ProofArgumentsFor(fixture.Request, "promote");
        SetProofOption(arguments, "--artifact-manifest", unapproved);
        var options = DurablePreflightArtifactProofCommandOptions.Parse(arguments.ToArray(), _root);

        await DurablePreflightArtifactProof.ValidateAndPromoteCandidateManifestAsync(
            options.Request, options.Request.ReceiptPath, options.Request.RepositoryRoot, CancellationToken.None);

        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(fixture.Request.ArtifactManifestPath));
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(unapproved));
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(options.Request.ApprovedManifestPath));
        Assert.Empty(Directory.GetFiles(fixture.ArtifactDirectory, "*.tmp"));
    }

    private static List<string> ProofArguments(string mode = "candidate")
        => [
            "--mode", mode, "--artifact-dir", "bundle", "--artifact-manifest", "bundle/unapproved.json",
            "--approved-manifest", "approved.json", "--receipt", "candidate-receipt.json",
            "--source-commit", SourceCommit, "--run-id", RunId, "--artifact-id", ArtifactId
        ];

    private static List<string> ProofArgumentsFor(DurablePreflightArtifactProofRequest request, string mode = "candidate")
        => [
            "--mode", mode, "--repo-root", request.RepositoryRoot, "--artifact-dir", request.ArtifactDirectory,
            "--artifact-manifest", request.ArtifactManifestPath, "--approved-manifest", request.ApprovedManifestPath,
            "--receipt", request.ReceiptPath, "--source-commit", request.SourceCommit,
            "--run-id", request.RunId, "--artifact-id", request.ArtifactId
        ];

    private static void SetProofOption(List<string> arguments, string option, string? value)
    {
        var index = arguments.IndexOf(option);
        if (index >= 0) arguments.RemoveRange(index, 2);
        if (value is not null) arguments.AddRange([option, value]);
    }

    [Fact]
    public async Task Candidate_CompleteReceiptPromotesManifestOnlyAfterRunnerSucceeds()
    {
        var fixture = await ProofFixture.CreateAsync(_root);
        var runner = new FakeRunner(async (request, token) =>
        {
            await fixture.WriteCompleteReceiptAsync(request, token);
            return new ExternalCommandResult(0, "issue-845-proof=passed\n", string.Empty);
        });

        var result = await new DurablePreflightArtifactProof(runner).RunCandidateAsync(fixture.Request, CancellationToken.None);

        Assert.Equal(1, runner.Calls);
        Assert.Contains("--consumer-project", runner.LastRequest!.Arguments);
        Assert.Equal(DurablePreflightArtifactProof.CandidateProofTimeoutMilliseconds, runner.LastRequest.TimeoutMilliseconds);
        Assert.Equal(TestPathUtils.PathUnder(fixture.Request.RepositoryRoot, "Durable/consumers/PostgreSqlPreflightConsumer/PostgreSqlPreflightConsumer.csproj"),
            runner.LastRequest.Arguments[Array.IndexOf(runner.LastRequest.Arguments.ToArray(), "--consumer-project") + 1]);
        Assert.Equal(fixture.Request.SourceCommit, runner.LastRequest.Arguments[Array.IndexOf(runner.LastRequest.Arguments.ToArray(), "--source-commit") + 1]);
        Assert.Equal(fixture.Request.RunId, runner.LastRequest.Arguments[Array.IndexOf(runner.LastRequest.Arguments.ToArray(), "--run-id") + 1]);
        Assert.Equal(fixture.Request.ArtifactId, runner.LastRequest.Arguments[Array.IndexOf(runner.LastRequest.Arguments.ToArray(), "--artifact-id") + 1]);
        Assert.Equal(TestPathUtils.PathUnder(fixture.Request.RepositoryRoot, "examples/durable-postgresql/role-pairs-full-and-work-only.example.json"),
            runner.LastRequest.Environment!["PREFLIGHT_ROLE_PAIRS_FILE"]);
        Assert.Equal("appsurface_durable_owner", runner.LastRequest.Environment["PREFLIGHT_MIGRATION_OWNER_ROLE"]);
        Assert.Equal("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa8450", runner.LastRequest.Environment["PREFLIGHT_STORE_ID"]);
        Assert.Equal("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb8450", runner.LastRequest.Environment["PREFLIGHT_ACTIVE_EPOCH"]);
        // The real consumer parses both overrides before it creates a disposable fixture.
        // String equality alone must not let the carrier and receipt fixture share a malformed identity.
        Assert.True(Guid.TryParseExact(runner.LastRequest.Environment["PREFLIGHT_STORE_ID"], "D", out var storeId));
        Assert.NotEqual(Guid.Empty, storeId);
        Assert.True(Guid.TryParseExact(runner.LastRequest.Environment["PREFLIGHT_ACTIVE_EPOCH"], "D", out var activeEpoch));
        Assert.NotEqual(Guid.Empty, activeEpoch);
        Assert.Equal(4, result.ScenarioCount);
        Assert.False(File.Exists(fixture.Request.ArtifactManifestPath));
        Assert.True(File.Exists(fixture.Request.ApprovedManifestPath));
        Assert.True(File.Exists(fixture.Request.ReceiptPath));
        Assert.Equal(fixture.ManifestSha256, result.ArtifactManifestSha256);
    }

    [Fact]
    public async Task Candidate_AcceptsBoundedWriterFailureOnlyAfterReleaseAndCompleteRerun()
    {
        var fixture = await ProofFixture.CreateAsync(_root);
        var runner = new FakeRunner(async (request, token) =>
        {
            var receipt = await fixture.CompleteReceiptJsonAsync(mutation: "writer-recovery");
            await File.WriteAllTextAsync(request.Arguments[Array.IndexOf(request.Arguments.ToArray(), "--receipt") + 1], receipt, token);
            return new ExternalCommandResult(0, "issue-845-proof=passed\n", string.Empty);
        });

        var result = await new DurablePreflightArtifactProof(runner).RunCandidateAsync(fixture.Request, CancellationToken.None);

        Assert.Equal(4, result.ScenarioCount);
        Assert.True(File.Exists(fixture.Request.ApprovedManifestPath));
        Assert.True(File.Exists(fixture.Request.ReceiptPath));
    }

    public static TheoryData<string, string> InvalidGuardLossEvidenceCases()
    {
        var cases = new TheoryData<string, string>();
        foreach (var mutation in new[] { "missing", "null", "array", "string", "false", "empty-object" }) cases.Add("guardLossNegativeProof", mutation);
        foreach (var field in new[]
        {
            "lanePassBackendTerminated", "lanePassCancellationObserved", "lanePassChildDrained", "lanePassReceiptWithheld",
            "activationBackendTerminated", "activationCancellationObserved", "activationHostDrainPersisted",
            "activationAdmissionClosed", "activationSessionsReleased", "activationReceiptWithheld",
            "lanePassStarted", "lanePassObservedExecuting", "activationWorkInvocationStartedBeforeCompletion",
            "activationDrainVerifiedCheckpointObserved", "activationHostedServicesStopped", "activationChildDrained",
            "activationIdentityUnchanged", "finalReceiptAbsentBeforeAndAfter"
        })
        {
            foreach (var mutation in new[] { "missing", "false", "string" }) cases.Add(field, mutation);
        }

        foreach (var field in new[] { "lanePassBackendPid", "lanePassExecutingBackendPid", "activationBackendPid" })
        {
            foreach (var mutation in new[] { "missing", "zero", "negative", "string" }) cases.Add(field, mutation);
        }

        cases.Add("lanePassExecutingBackendPid", "same-as-guard");

        foreach (var field in new[] { "lanePassStoreId", "lanePassActiveEpoch", "activationStoreId", "activationActiveEpoch" })
        {
            foreach (var mutation in new[] { "missing", "false", "string", "mismatch" }) cases.Add(field, mutation);
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(InvalidGuardLossEvidenceCases))]
    public async Task Candidate_MissingFailedOrMalformedGuardLossEvidenceWithholdsApproval(string field, string mutation)
    {
        var fixture = await ProofFixture.CreateAsync(_root);
        var manifestBytes = await File.ReadAllBytesAsync(fixture.Request.ArtifactManifestPath);
        var runner = new FakeRunner(async (request, token) =>
        {
            var receipt = JsonNode.Parse(await fixture.CompleteReceiptJsonAsync())!.AsObject();
            var target = field == "guardLossNegativeProof" ? receipt : receipt["guardLossNegativeProof"]!.AsObject();
            switch (mutation)
            {
                case "missing": target.Remove(field); break;
                case "null": target[field] = null; break;
                case "array": target[field] = new JsonArray(); break;
                case "empty-object": target[field] = new JsonObject(); break;
                case "false": target[field] = false; break;
                case "zero": target[field] = 0; break;
                case "negative": target[field] = -1; break;
                case "same-as-guard": target[field] = 51; break;
                case "string": target[field] = "true"; break;
                case "mismatch": target[field] = "cccccccc-cccc-cccc-cccc-cccccccc8450"; break;
            }

            await File.WriteAllTextAsync(request.Arguments[Array.IndexOf(request.Arguments.ToArray(), "--receipt") + 1],
                receipt.ToJsonString(PackageArtifactJson.Options), token);
            return new ExternalCommandResult(0, "issue-845-proof=passed\n", string.Empty);
        });

        var exception = await Assert.ThrowsAsync<PackageIndexException>(() =>
            new DurablePreflightArtifactProof(runner).RunCandidateAsync(fixture.Request, CancellationToken.None));

        Assert.Contains(field == "guardLossNegativeProof" ? "guard-loss" : field, exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, runner.Calls);
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(fixture.Request.ArtifactManifestPath));
        Assert.False(File.Exists(fixture.Request.ApprovedManifestPath));
        Assert.False(File.Exists(fixture.Request.ReceiptPath));
        Assert.Empty(Directory.GetFiles(_root, "candidate-receipt.json*.tmp"));
    }

    [Theory]
    [InlineData("nonzero")]
    [InlineData("missing")]
    [InlineData("missing-success-marker")]
    [InlineData("partial")]
    [InlineData("stale")]
    [InlineData("mismatched")]
    [InlineData("wrong-package-hash")]
    [InlineData("wrong-recipe-hash")]
    [InlineData("wrong-store")]
    [InlineData("lane-proof-skipped")]
    [InlineData("wrong-lane-scenario")]
    [InlineData("wrong-result-scenario")]
    [InlineData("missing-source-denial")]
    [InlineData("one-pair-uninstalled-source")]
    [InlineData("bad-writer-outcome")]
    [InlineData("wrong-writer-scenario")]
    [InlineData("writer-runtime-connection-string")]
    [InlineData("duplicate-role")]
    [InlineData("duplicate-pair")]
    [InlineData("empty-guard")]
    [InlineData("missing-runtime")]
    [InlineData("duplicate-property")]
    public async Task Candidate_InvalidRunnerOrReceiptDoesNotPromoteOrLeavePassingEvidence(string failure)
    {
        var fixture = await ProofFixture.CreateAsync(_root);
        var manifestBytes = await File.ReadAllBytesAsync(fixture.Request.ArtifactManifestPath);
        var runner = new FakeRunner(async (request, token) =>
        {
            if (failure == "nonzero")
            {
                return new ExternalCommandResult(7, string.Empty, "failure");
            }

            if (failure == "missing")
            {
                return new ExternalCommandResult(0, "issue-845-proof=passed\n", string.Empty);
            }

            if (failure == "missing-success-marker")
            {
                await fixture.WriteCompleteReceiptAsync(request, token);
                return new ExternalCommandResult(0, string.Empty, string.Empty);
            }

            var receipt = failure == "partial"
                ? "{}"
                : await fixture.CompleteReceiptJsonAsync(failure == "stale" ? SourceCommit.Replace('0', '1') : SourceCommit,
                    failure == "mismatched" ? new string('f', 64) : fixture.ManifestSha256,
                    failure is "wrong-package-hash" or "wrong-recipe-hash" or "wrong-store" or "duplicate-role"
                        or "duplicate-pair" or "empty-guard" or "missing-runtime" or "duplicate-property"
                        or "lane-proof-skipped" or "wrong-lane-scenario" or "wrong-result-scenario"
                        or "missing-source-denial" or "one-pair-uninstalled-source" or "bad-writer-outcome" or "wrong-writer-scenario"
                        or "writer-runtime-connection-string" ? failure : null);
            await File.WriteAllTextAsync(request.Arguments[Array.IndexOf(request.Arguments.ToArray(), "--receipt") + 1], receipt, token);
            return new ExternalCommandResult(0, "issue-845-proof=passed\n", string.Empty);
        });

        await Assert.ThrowsAsync<PackageIndexException>(() =>
            new DurablePreflightArtifactProof(runner).RunCandidateAsync(fixture.Request, CancellationToken.None));

        Assert.Equal(1, runner.Calls);
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(fixture.Request.ArtifactManifestPath));
        Assert.False(File.Exists(fixture.Request.ApprovedManifestPath));
        Assert.False(File.Exists(fixture.Request.ReceiptPath));
        Assert.Empty(Directory.GetFiles(_root, "candidate-receipt.json*.tmp"));
    }

    [Fact]
    public async Task Candidate_CancellationPreservesCallerManifestAndRemovesTemporaryReceipt()
    {
        var fixture = await ProofFixture.CreateAsync(_root);
        var manifestBytes = await File.ReadAllBytesAsync(fixture.Request.ArtifactManifestPath);
        using var cancellation = new CancellationTokenSource();
        var runner = new FakeRunner(async (request, token) =>
        {
            await fixture.WriteCompleteReceiptAsync(request, token);
            cancellation.Cancel();
            return await Task.FromCanceled<ExternalCommandResult>(cancellation.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DurablePreflightArtifactProof(runner)
            .RunCandidateAsync(fixture.Request, cancellation.Token));

        Assert.Equal(1, runner.Calls);
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(fixture.Request.ArtifactManifestPath));
        Assert.False(File.Exists(fixture.Request.ApprovedManifestPath));
        Assert.False(File.Exists(fixture.Request.ReceiptPath));
        Assert.Empty(Directory.GetFiles(_root, "candidate-receipt.json*.tmp"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("tampered")]
    [InlineData("source")]
    [InlineData("version")]
    public async Task Candidate_MissingOrTamperedPackageFailsBeforeRunner(string mutation)
    {
        var fixture = await ProofFixture.CreateAsync(_root);
        var package = fixture.ProviderPackagePath;
        if (mutation == "missing")
        {
            File.Delete(package);
        }
        else if (mutation == "tampered")
        {
            await File.AppendAllTextAsync(package, "tampered");
        }
        else
        {
            fixture = await ProofFixture.CreateAsync(_root, mutation == "source"
                ? "fedcba9876543210fedcba9876543210fedcba98" : SourceCommit,
                mutation == "version" ? "9.9.9" : Version);
        }

        var manifestBytes = await File.ReadAllBytesAsync(fixture.Request.ArtifactManifestPath);

        var runner = new FakeRunner(async (request, token) =>
        {
            await fixture.WriteCompleteReceiptAsync(request, token);
            return new ExternalCommandResult(0, "issue-845-proof=passed\n", string.Empty);
        });
        await Assert.ThrowsAsync<PackageIndexException>(() =>
            new DurablePreflightArtifactProof(runner).RunCandidateAsync(fixture.Request, CancellationToken.None));

        Assert.Equal(0, runner.Calls);
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(fixture.Request.ArtifactManifestPath));
        Assert.False(File.Exists(fixture.Request.ApprovedManifestPath));
        Assert.False(File.Exists(fixture.Request.ReceiptPath));
        Assert.Empty(Directory.GetFiles(_root, "candidate-receipt.json*.tmp"));
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("recipe")]
    public async Task Published_MismatchFailsBeforeInvokingSharedProof(string mismatch)
    {
        var fixture = await ProofFixture.CreateAsync(_root);
        var candidateReceipt = TestPathUtils.PathUnder(_root, "candidate-receipt.json");
        await File.WriteAllTextAsync(candidateReceipt, await fixture.CompleteReceiptJsonAsync());
        var restored = await fixture.CreateRestoredPackagesAsync();
        if (mismatch == "archive")
        {
            using var archive = ZipFile.Open(fixture.RestoredProviderPackagePath(restored), ZipArchiveMode.Update);
            archive.CreateEntry("unexpected-payload.txt");
        }
        else
        {
            await File.WriteAllTextAsync(fixture.RestoredRecipePath(restored), "different recipe");
        }

        var publicReceipt = TestPathUtils.PathUnder(_root, "public-receipt.json");
        var runner = new FakeRunner(async (request, token) =>
        {
            await fixture.WriteCompleteReceiptAsync(request, token);
            return new ExternalCommandResult(0, "issue-845-proof=passed\n", string.Empty);
        });
        await Assert.ThrowsAsync<PackageIndexException>(() => new DurablePreflightArtifactProof(runner).RunPublishedAsync(
            fixture.Request, candidateReceipt, restored, publicReceipt, CancellationToken.None));

        Assert.Equal(0, runner.Calls);
        Assert.False(File.Exists(publicReceipt));
        Assert.False(File.Exists(publicReceipt + ".carrier.json"));
    }

    [Fact]
    public async Task Published_ExactByteMatchesInvokeSharedProofAndRetainPublicCarrierReceipt()
    {
        var fixture = await ProofFixture.CreateAsync(_root);
        var candidateReceipt = TestPathUtils.PathUnder(_root, "candidate-receipt.json");
        await File.WriteAllTextAsync(candidateReceipt, await fixture.CompleteReceiptJsonAsync());
        var restored = await fixture.CreateRestoredPackagesAsync();
        var publicReceipt = TestPathUtils.PathUnder(_root, "public-receipt.json");
        var runner = new FakeRunner(async (request, token) =>
        {
            await fixture.WriteCompleteReceiptAsync(request, token);
            return new ExternalCommandResult(0, "issue-845-proof=passed\n", string.Empty);
        });

        var result = await new DurablePreflightArtifactProof(runner).RunPublishedAsync(
            fixture.Request, candidateReceipt, restored, publicReceipt, CancellationToken.None);

        Assert.Equal(1, runner.Calls);
        Assert.Equal(4, result.ScenarioCount);
        Assert.True(File.Exists(publicReceipt));
        var carrier = await JsonDocument.ParseAsync(File.OpenRead(publicReceipt + ".carrier.json"));
        Assert.Equal("issue845-public-feed-package-content-identity", carrier.RootElement.GetProperty("ProofKind").GetString());
        Assert.Equal(2, carrier.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(fixture.ManifestSha256, carrier.RootElement.GetProperty("ArtifactManifestSha256").GetString());
        Assert.Equal(fixture.CliSha256, carrier.RootElement.GetProperty("PublicPackageSha256").GetProperty("ForgeTrust.AppSurface.Cli").GetString());
        var scratch = runner.LastRequest!.Arguments[Array.IndexOf(runner.LastRequest.Arguments.ToArray(), "--artifact-dir") + 1];
        Assert.False(Directory.Exists(scratch));
    }

    [Theory]
    [InlineData("added")]
    [InlineData("replaced")]
    [InlineData("removed")]
    public async Task Published_SignatureEnvelopeChangesPreserveCandidateProofAndRecordBothArchiveHashes(string envelopeChange)
    {
        var fixture = await ProofFixture.CreateAsync(_root, candidateSigned: envelopeChange != "added", largePayload: true);
        var candidateReceipt = TestPathUtils.PathUnder(_root, "candidate-receipt.json");
        await File.WriteAllTextAsync(candidateReceipt, await fixture.CompleteReceiptJsonAsync());
        var restored = await fixture.CreateRestoredPackagesAsync();
        var restoredPath = fixture.RestoredProviderPackagePath(restored);
        using (var archive = ZipFile.Open(restoredPath, ZipArchiveMode.Update))
        {
            archive.GetEntry(".signature.p7s")?.Delete();
            if (envelopeChange != "removed")
            {
                await using var signature = archive.CreateEntry(".signature.p7s").Open();
                await signature.WriteAsync(Encoding.UTF8.GetBytes("synthetic repository signature envelope"));
            }
        }
        var publicHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(restoredPath)));
        Assert.NotEqual(fixture.ProviderSha256, publicHash);
        var publicReceipt = TestPathUtils.PathUnder(_root, "public-receipt.json");
        var runner = new FakeRunner(async (request, token) =>
        {
            var scratch = request.Arguments[Array.IndexOf(request.Arguments.ToArray(), "--artifact-dir") + 1];
            Assert.Equal(await File.ReadAllBytesAsync(fixture.ProviderPackagePath, token),
                await File.ReadAllBytesAsync(TestPathUtils.PathUnder(scratch, Path.GetFileName(fixture.ProviderPackagePath)), token));
            await fixture.WriteCompleteReceiptAsync(request, token);
            return new ExternalCommandResult(0, "issue-845-proof=passed\n", string.Empty);
        });

        await new DurablePreflightArtifactProof(runner).RunPublishedAsync(
            fixture.Request, candidateReceipt, restored, publicReceipt, CancellationToken.None);

        using var carrier = JsonDocument.Parse(await File.ReadAllTextAsync(publicReceipt + ".carrier.json"));
        Assert.Equal("issue845-public-feed-package-content-identity", carrier.RootElement.GetProperty("ProofKind").GetString());
        Assert.Equal(fixture.ProviderSha256, carrier.RootElement.GetProperty("CandidatePackageSha256")
            .GetProperty("ForgeTrust.AppSurface.Durable.PostgreSql").GetString());
        Assert.Equal(publicHash, carrier.RootElement.GetProperty("PublicPackageSha256")
            .GetProperty("ForgeTrust.AppSurface.Durable.PostgreSql").GetString());
        Assert.Equal(1, runner.Calls);
        Assert.Equal(3, carrier.RootElement.GetProperty("CandidatePackageSha256").EnumerateObject().Count());
        Assert.Equal(3, carrier.RootElement.GetProperty("PublicPackageSha256").EnumerateObject().Count());
    }

    [Theory]
    [InlineData("invalid-zip")]
    [InlineData("extra-payload")]
    [InlineData("missing-payload")]
    [InlineData("renamed-payload")]
    [InlineData("changed-payload")]
    [InlineData("changed-length")]
    [InlineData("duplicate-payload")]
    [InlineData("duplicate-signature")]
    [InlineData("nested-signature")]
    [InlineData("case-signature")]
    public async Task Published_SignatureExemptionRejectsEveryOtherContentChangeBeforeProof(string mutation)
    {
        var fixture = await ProofFixture.CreateAsync(_root);
        var candidateReceipt = TestPathUtils.PathUnder(_root, "candidate-receipt.json");
        await File.WriteAllTextAsync(candidateReceipt, await fixture.CompleteReceiptJsonAsync());
        var restored = await fixture.CreateRestoredPackagesAsync();
        var path = fixture.RestoredDependencyPackagePath(restored);
        if (mutation == "invalid-zip") await File.WriteAllTextAsync(path, "not a ZIP");
        else
        {
            using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
            var nuspec = archive.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
            var name = nuspec.FullName;
            if (mutation == "missing-payload") nuspec.Delete();
            else if (mutation is "changed-payload" or "changed-length" or "renamed-payload")
            {
                using var output = new MemoryStream();
                await using (var input = nuspec.Open()) await input.CopyToAsync(output);
                var bytes = output.ToArray();
                nuspec.Delete();
                if (mutation == "changed-length") bytes = [.. bytes, (byte)' '];
                else if (mutation == "changed-payload") bytes[0] ^= 1;
                await using var replacement = archive.CreateEntry(mutation == "renamed-payload" ? "renamed.nuspec" : name).Open();
                await replacement.WriteAsync(bytes);
            }
            else
            {
                archive.CreateEntry(mutation switch
                {
                    "duplicate-payload" => name,
                    "nested-signature" => "nested/.signature.p7s",
                    "case-signature" => ".SIGNATURE.P7S",
                    "duplicate-signature" => ".signature.p7s",
                    _ => "extra.txt"
                });
                if (mutation == "duplicate-signature") archive.CreateEntry(".signature.p7s");
            }
        }
        var publicReceipt = TestPathUtils.PathUnder(_root, "public-receipt.json");
        var runner = new FakeRunner((_, _) => throw new InvalidOperationException("Changed content must not execute."));

        await Assert.ThrowsAsync<PackageIndexException>(() => new DurablePreflightArtifactProof(runner).RunPublishedAsync(
            fixture.Request, candidateReceipt, restored, publicReceipt, CancellationToken.None));

        Assert.Equal(0, runner.Calls);
        Assert.False(File.Exists(publicReceipt));
        Assert.False(File.Exists(publicReceipt + ".carrier.json"));
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("manifest")]
    public async Task Published_CandidateStagingMutationDuringProofWithholdsBothReceipts(string mutation)
    {
        var fixture = await ProofFixture.CreateAsync(_root);
        var candidateReceipt = TestPathUtils.PathUnder(_root, "candidate-receipt.json");
        await File.WriteAllTextAsync(candidateReceipt, await fixture.CompleteReceiptJsonAsync());
        var restored = await fixture.CreateRestoredPackagesAsync();
        var publicReceipt = TestPathUtils.PathUnder(_root, "public-receipt.json");
        string? scratch = null;
        var runner = new FakeRunner(async (request, token) =>
        {
            scratch = request.Arguments[Array.IndexOf(request.Arguments.ToArray(), "--artifact-dir") + 1];
            await fixture.WriteCompleteReceiptAsync(request, token);
            if (mutation == "archive")
                await File.AppendAllTextAsync(TestPathUtils.PathUnder(scratch, Path.GetFileName(fixture.ProviderPackagePath)), "changed", token);
            else
                await File.AppendAllTextAsync(fixture.Request.ArtifactManifestPath, " ", token);
            return new ExternalCommandResult(0, "issue-845-proof=passed\n", string.Empty);
        });

        await Assert.ThrowsAsync<PackageIndexException>(() => new DurablePreflightArtifactProof(runner).RunPublishedAsync(
            fixture.Request, candidateReceipt, restored, publicReceipt, CancellationToken.None));

        Assert.Equal(1, runner.Calls);
        Assert.False(Directory.Exists(scratch));
        Assert.False(File.Exists(publicReceipt));
        Assert.False(File.Exists(publicReceipt + ".carrier.json"));
    }

    [Theory]
    [InlineData("io")]
    [InlineData("access")]
    [InlineData("directory-left-behind")]
    public async Task Published_CleanupFailureWithholdsReceiptsAndRetriesOwnedScratchCleanup(string failure)
    {
        var fixture = await ProofFixture.CreateAsync(_root);
        var candidateReceipt = TestPathUtils.PathUnder(_root, "candidate-receipt.json");
        await File.WriteAllTextAsync(candidateReceipt, await fixture.CompleteReceiptJsonAsync());
        var restored = await fixture.CreateRestoredPackagesAsync();
        var publicReceipt = TestPathUtils.PathUnder(_root, "public-receipt.json");
        var cleanupCalls = 0;
        string? scratch = null;
        var runner = new FakeRunner(async (request, token) =>
        {
            await fixture.WriteCompleteReceiptAsync(request, token);
            return new ExternalCommandResult(0, "issue-845-proof=passed\n", string.Empty);
        });
        var proof = new DurablePreflightArtifactProof(runner, path =>
        {
            scratch = path;
            cleanupCalls++;
            Assert.False(File.Exists(publicReceipt));
            Assert.False(File.Exists(publicReceipt + ".carrier.json"));
            Assert.True(Directory.Exists(path));
            if (cleanupCalls == 1)
            {
                if (failure == "io") throw new IOException("Deterministic owned-scratch deletion failure.");
                if (failure == "access") throw new UnauthorizedAccessException("Deterministic owned-scratch access failure.");
                return;
            }

            Directory.Delete(path, recursive: true);
        });

        await Assert.ThrowsAsync<PackageIndexException>(() => proof.RunPublishedAsync(
            fixture.Request, candidateReceipt, restored, publicReceipt, CancellationToken.None));

        Assert.Equal(1, runner.Calls);
        Assert.Equal(2, cleanupCalls);
        Assert.NotNull(scratch);
        Assert.False(Directory.Exists(scratch));
        Assert.False(File.Exists(publicReceipt));
        Assert.False(File.Exists(publicReceipt + ".carrier.json"));
        Assert.Empty(Directory.GetFiles(_root, "public-receipt.json*.tmp"));
        Assert.True(File.Exists(candidateReceipt));
        Assert.True(File.Exists(fixture.ProviderPackagePath));
    }

    [Fact]
    public async Task Published_CommandFailureStillCleansOwnedScratchBeforeReturningFailure()
    {
        var fixture = await ProofFixture.CreateAsync(_root);
        var candidateReceipt = TestPathUtils.PathUnder(_root, "candidate-receipt.json");
        await File.WriteAllTextAsync(candidateReceipt, await fixture.CompleteReceiptJsonAsync());
        var restored = await fixture.CreateRestoredPackagesAsync();
        var publicReceipt = TestPathUtils.PathUnder(_root, "public-receipt.json");
        var runner = new FakeRunner((_, _) => Task.FromResult(new ExternalCommandResult(7, string.Empty, "consumer failed")));

        await Assert.ThrowsAsync<PackageIndexException>(() => new DurablePreflightArtifactProof(runner).RunPublishedAsync(
            fixture.Request, candidateReceipt, restored, publicReceipt, CancellationToken.None));

        var scratch = runner.LastRequest!.Arguments[Array.IndexOf(runner.LastRequest.Arguments.ToArray(), "--artifact-dir") + 1];
        Assert.False(Directory.Exists(scratch));
        Assert.False(File.Exists(publicReceipt));
        Assert.False(File.Exists(publicReceipt + ".carrier.json"));
        Assert.Empty(Directory.GetFiles(_root, "public-receipt.json*.tmp"));
    }

    [Theory]
    [InlineData("candidate")]
    [InlineData("published")]
    public async Task Published_MissingGuardLossEvidenceInEitherReceiptWithholdsPublicSuccess(string receiptStage)
    {
        var fixture = await ProofFixture.CreateAsync(_root);
        var complete = await fixture.CompleteReceiptJsonAsync();
        var incomplete = JsonNode.Parse(complete)!.AsObject();
        incomplete.Remove("guardLossNegativeProof");
        var incompleteJson = incomplete.ToJsonString(PackageArtifactJson.Options);
        var candidateReceipt = TestPathUtils.PathUnder(_root, "candidate-receipt.json");
        await File.WriteAllTextAsync(candidateReceipt, receiptStage == "candidate" ? incompleteJson : complete);
        var restored = await fixture.CreateRestoredPackagesAsync();
        var publicReceipt = TestPathUtils.PathUnder(_root, "public-receipt.json");
        var runner = new FakeRunner(async (request, token) =>
        {
            await File.WriteAllTextAsync(request.Arguments[Array.IndexOf(request.Arguments.ToArray(), "--receipt") + 1],
                incompleteJson, token);
            return new ExternalCommandResult(0, "issue-845-proof=passed\n", string.Empty);
        });

        var exception = await Assert.ThrowsAsync<PackageIndexException>(() => new DurablePreflightArtifactProof(runner).RunPublishedAsync(
            fixture.Request, candidateReceipt, restored, publicReceipt, CancellationToken.None));

        Assert.Contains("guard-loss", exception.Message, StringComparison.Ordinal);
        Assert.Equal(receiptStage == "candidate" ? 0 : 1, runner.Calls);
        if (runner.LastRequest is { } command)
        {
            var scratch = command.Arguments[Array.IndexOf(command.Arguments.ToArray(), "--artifact-dir") + 1];
            Assert.False(Directory.Exists(scratch));
        }

        Assert.False(File.Exists(publicReceipt));
        Assert.False(File.Exists(publicReceipt + ".carrier.json"));
        Assert.Empty(Directory.GetFiles(_root, "public-receipt.json*.tmp"));
    }

    public static TheoryData<string, string> IncompleteReleaseReceiptCases()
        => new()
        {
            { "empty", "empty or exceeds" },
            { "oversized", "empty or exceeds" },
            { "invalid-json", "malformed" },
            { "wrong-root-type", "malformed field types" },
            { "wrong-schema", "schemaVersion" },
            { "schema-type", "schemaVersion" },
            { "limitations", "limitations" },
            { "limitations-type", "limitations" },
            { "scenarios-absent", "four required scenario" },
            { "scenarios-type", "four required scenario" },
            { "scenario-omitted", "missing one or more required scenarios" },
            { "scenario-duplicate", "missing, duplicate, or failed scenario" },
            { "schema10-baseline-absent", "missing, duplicate, or failed scenario" },
            { "guard-window-lost", "intact guard window" },
            { "guard-window-pids", "intact guard window" },
            { "activation-incomplete", "intact guard window" },
            { "cli-results-absent", "no runtime credential evidence" },
            { "cli-results-empty", "no runtime credential evidence" },
            { "cli-result-negative-time", "ElapsedMilliseconds" },
            { "writer-unobserved", "guarded lane and writer-race" },
            { "writer-window-incomplete", "guarded lane and writer-race" },
            { "catalog-changed", "guarded lane and writer-race" },
            { "writer-finished-too-soon", "pre-completion branch" },
            { "writer-contradictory-failure", "pre-completion branch" },
            { "recovery-unreleased", "recovery branch" },
            { "recovery-unbounded", "recovery branch" },
            { "recovery-invalid-kind", "recovery branch" },
            { "total-time-zero", "totalDurationMilliseconds" },
            { "stages-absent", "missing stage duration" },
            { "stages-empty", "missing stage duration" },
            { "stage-negative", "invalid stage duration" },
            { "stage-type", "invalid stage duration" },
            { "stage-missing", "required stage timings" },
            { "stage-zero", "required stage timings" },
            { "checksums-absent", "missing normalized migration checksums" },
            { "checksum-omitted", "all ten normalized" },
            { "checksum-version-order", "invalid migration checksum" },
            { "checksum-hash", "invalid migration checksum" },
            { "checksum-name", "invalid migration checksum" },
            { "packages-absent", "package set" },
            { "package-omitted", "package set" },
            { "package-duplicate", "invalid package identities" },
            { "package-wrong-version", "invalid package identities" },
            { "package-wrong-hash", "invalid package identities" },
            { "package-foreign", "invalid package identities" },
            { "baseline-hash-malformed", "schema10BaselineSnapshotSha256" }
        };

    [Theory]
    [MemberData(nameof(IncompleteReleaseReceiptCases))]
    public async Task Candidate_UnprovenReleaseEvidenceWithholdsApprovalAndRemovesTemporaryReceipt(string mutation, string diagnostic)
    {
        var fixture = await ProofFixture.CreateAsync(_root);
        var manifestBytes = await File.ReadAllBytesAsync(fixture.Request.ArtifactManifestPath);
        var json = await MutateReleaseReceiptAsync(fixture, mutation);
        var runner = new FakeRunner(async (request, token) =>
        {
            await File.WriteAllTextAsync(request.Arguments[Array.IndexOf(request.Arguments.ToArray(), "--receipt") + 1], json, token);
            return new ExternalCommandResult(0, "issue-845-proof=passed\n", string.Empty);
        });

        var exception = await Assert.ThrowsAsync<PackageIndexException>(() =>
            new DurablePreflightArtifactProof(runner).RunCandidateAsync(fixture.Request, CancellationToken.None));

        Assert.Contains(diagnostic, exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, runner.Calls);
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(fixture.Request.ArtifactManifestPath));
        Assert.False(File.Exists(fixture.Request.ApprovedManifestPath));
        Assert.False(File.Exists(fixture.Request.ReceiptPath));
        Assert.Empty(Directory.GetFiles(_root, "candidate-receipt.json*.tmp"));
        Assert.True(File.Exists(fixture.ProviderPackagePath));
    }

    private static async Task<string> MutateReleaseReceiptAsync(ProofFixture fixture, string mutation)
    {
        if (mutation == "empty") return string.Empty;
        if (mutation == "oversized") return new string(' ', 2 * 1024 * 1024 + 1);
        if (mutation == "invalid-json") return "{\"schemaVersion\":";
        if (mutation == "wrong-root-type") return "[]";
        var root = JsonNode.Parse(await fixture.CompleteReceiptJsonAsync(mutation: mutation.StartsWith("recovery-", StringComparison.Ordinal)
            ? "writer-recovery" : null))!.AsObject();
        switch (mutation)
        {
            case "wrong-schema": root["schemaVersion"] = 2; break;
            case "schema-type": root["schemaVersion"] = "1"; break;
            case "limitations": root["limitations"] = new JsonArray("guard monitor unavailable"); break;
            case "limitations-type": root["limitations"] = "none"; break;
            case "scenarios-absent": root.Remove("scenarios"); break;
            case "scenarios-type": root["scenarios"] = new JsonObject(); break;
            case "scenario-omitted": root["scenarios"]!.AsArray().RemoveAt(3); break;
            case "scenario-duplicate": root["scenarios"]!.AsArray()[3] = root["scenarios"]![0]!.DeepClone(); break;
            case "schema10-baseline-absent": root["scenarios"]![3]!["modeledSchema10BaselinePresent"] = false; break;
            case "guard-window-lost": root["guardWindow"]!["intact"] = false; break;
            case "guard-window-pids": root["guardWindow"]!["backendPids"]![2] = 0; break;
            case "activation-incomplete": root["activationCallbackCompleted"] = false; break;
            case "cli-results-absent": root.Remove("cliResults"); break;
            case "cli-results-empty": root["cliResults"] = new JsonArray(); break;
            case "cli-result-negative-time": root["cliResults"]![0]!["ElapsedMilliseconds"] = -1; break;
            case "writer-unobserved": root["queuedWriter"]!["queuedWriterObserved"] = false; break;
            case "writer-window-incomplete": root["queuedWriter"]!["authoritativeWindow"]!["fixtureActivationCallbackCompleted"] = false; break;
            case "catalog-changed": root["catalogSnapshotSha256BeforeAndAfterIdenticalRerun"]![1] = new string('9', 64); break;
            case "writer-finished-too-soon": root["queuedWriter"]!["writerAcquiredAfterWindow"] = false; break;
            case "writer-contradictory-failure": root["queuedWriter"]!["boundedFailure"] = new JsonObject(); break;
            case "recovery-unreleased": root["queuedWriter"]!["writerReleasedAfterFailure"] = false; break;
            case "recovery-unbounded": root["queuedWriter"]!["boundedFailure"]!["durationMilliseconds"] = 20_001; break;
            case "recovery-invalid-kind": root["queuedWriter"]!["boundedFailure"]!["failureKind"] = "ignored"; break;
            case "total-time-zero": root["totalDurationMilliseconds"] = 0; break;
            case "stages-absent": root.Remove("stageDurationsMilliseconds"); break;
            case "stages-empty": root["stageDurationsMilliseconds"] = new JsonObject(); break;
            case "stage-negative": root["stageDurationsMilliseconds"]!["one_pair_bootstrap_ms"] = -1; break;
            case "stage-type": root["stageDurationsMilliseconds"]!["one_pair_bootstrap_ms"] = "1"; break;
            case "stage-missing": root["stageDurationsMilliseconds"]!.AsObject().Remove("schema10_to_11_upgrade_ms"); break;
            case "stage-zero": root["stageDurationsMilliseconds"]!["schema10_to_11_upgrade_ms"] = 0; break;
            case "checksums-absent": root.Remove("migrationChecksums"); break;
            case "checksum-omitted": root["migrationChecksums"]!.AsArray().RemoveAt(9); break;
            case "checksum-version-order": root["migrationChecksums"]![1]!["Version"] = 1; break;
            case "checksum-hash": root["migrationChecksums"]![0]!["sha256"] = new string('A', 64); break;
            case "checksum-name": root["migrationChecksums"]![0]!["Name"] = " "; break;
            case "packages-absent": root.Remove("packages"); break;
            case "package-omitted": root["packages"]!.AsArray().RemoveAt(2); break;
            case "package-duplicate": root["packages"]!.AsArray()[2] = root["packages"]![0]!.DeepClone(); break;
            case "package-wrong-version": root["packages"]![2]!["version"] = "9.9.9"; break;
            case "package-wrong-hash": root["packages"]![2]!["sha256"] = new string('f', 64); break;
            case "package-foreign": root["packages"]![2]!["packageId"] = "Foreign.Dependency"; break;
            case "baseline-hash-malformed": root["schema10BaselineSnapshotSha256"] = "not-a-sha256"; break;
            default: throw new ArgumentException("Unknown receipt mutation.", nameof(mutation));
        }

        return root.ToJsonString(PackageArtifactJson.Options);
    }

    [Theory]
    [InlineData("approved-file")]
    [InlineData("approved-directory")]
    [InlineData("receipt-file")]
    [InlineData("receipt-directory")]
    public async Task Candidate_ExistingGateOutputCannotBeOverwritten(string existing)
    {
        var fixture = await ProofFixture.CreateAsync(_root);
        var path = existing.StartsWith("approved", StringComparison.Ordinal) ? fixture.Request.ApprovedManifestPath : fixture.Request.ReceiptPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (existing.EndsWith("directory", StringComparison.Ordinal)) Directory.CreateDirectory(path);
        else await File.WriteAllTextAsync(path, "retained output");
        var runner = new FakeRunner((_, _) => throw new InvalidOperationException("Existing output must close the gate before the consumer."));

        await Assert.ThrowsAsync<PackageIndexException>(() =>
            new DurablePreflightArtifactProof(runner).RunCandidateAsync(fixture.Request, CancellationToken.None));

        Assert.Equal(0, runner.Calls);
        Assert.True(File.Exists(fixture.Request.ArtifactManifestPath));
        Assert.True(File.Exists(path) || Directory.Exists(path));
        if (File.Exists(path)) Assert.Equal("retained output", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("bundle-changed")]
    [InlineData("approval-created")]
    [InlineData("receipt-created")]
    public async Task Candidate_ChangesDuringProofCannotPromotePassingEvidence(string mutation)
    {
        var fixture = await ProofFixture.CreateAsync(_root);
        var manifestBytes = await File.ReadAllBytesAsync(fixture.Request.ArtifactManifestPath);
        byte[]? expectedManifestBytes = manifestBytes;
        var runner = new FakeRunner(async (request, token) =>
        {
            await fixture.WriteCompleteReceiptAsync(request, token);
            if (mutation == "bundle-changed")
            {
                await File.AppendAllTextAsync(fixture.Request.ArtifactManifestPath, "\n", token);
                expectedManifestBytes = await File.ReadAllBytesAsync(fixture.Request.ArtifactManifestPath, token);
            }
            else
            {
                var path = mutation == "approval-created" ? fixture.Request.ApprovedManifestPath : fixture.Request.ReceiptPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, "concurrent output", token);
            }

            return new ExternalCommandResult(0, "issue-845-proof=passed\n", string.Empty);
        });

        await Assert.ThrowsAsync<PackageIndexException>(() =>
            new DurablePreflightArtifactProof(runner).RunCandidateAsync(fixture.Request, CancellationToken.None));

        Assert.Equal(1, runner.Calls);
        Assert.Empty(Directory.GetFiles(_root, "candidate-receipt.json*.tmp"));
        if (mutation == "receipt-created")
        {
            Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(fixture.Request.ArtifactManifestPath));
            Assert.False(File.Exists(fixture.Request.ApprovedManifestPath));
        }
        else
        {
            Assert.NotNull(expectedManifestBytes);
            Assert.Equal(expectedManifestBytes, await File.ReadAllBytesAsync(fixture.Request.ArtifactManifestPath));
            if (mutation == "approval-created") Assert.Equal("concurrent output", await File.ReadAllTextAsync(fixture.Request.ApprovedManifestPath));
            else Assert.False(File.Exists(fixture.Request.ApprovedManifestPath));
        }

        if (mutation == "receipt-created") Assert.Equal("concurrent output", await File.ReadAllTextAsync(fixture.Request.ReceiptPath));
        else Assert.False(File.Exists(fixture.Request.ReceiptPath));
    }

    [Theory]
    [InlineData("role-fixture-missing")]
    [InlineData("schema-fixture-missing")]
    [InlineData("fixture-version")]
    [InlineData("fixture-pair-count")]
    [InlineData("fixture-profile")]
    [InlineData("fixture-duplicate-runtime")]
    [InlineData("script-missing")]
    [InlineData("consumer-missing")]
    public async Task Candidate_InvalidDisposableFixtureOrEntrypointFailsBeforeConsumer(string mutation)
    {
        var fixture = await ProofFixture.CreateAsync(_root);
        var manifestBytes = await File.ReadAllBytesAsync(fixture.Request.ArtifactManifestPath);
        var repository = await CreateDisposableRepositoryAsync(fixture.Request.RepositoryRoot);
        var rolePath = TestPathUtils.PathUnder(repository, "examples/durable-postgresql/role-pairs-full-and-work-only.example.json");
        switch (mutation)
        {
            case "role-fixture-missing": File.Delete(rolePath); break;
            case "schema-fixture-missing": File.Delete(TestPathUtils.PathUnder(repository, "Durable/consumers/PostgreSqlPreflightConsumer/schema10-two-pair.sql")); break;
            case "script-missing": File.Delete(TestPathUtils.PathUnder(repository, "Durable/verify-preflight-artifacts.sh")); break;
            case "consumer-missing": File.Delete(TestPathUtils.PathUnder(repository, "Durable/consumers/PostgreSqlPreflightConsumer/PostgreSqlPreflightConsumer.csproj")); break;
            default:
                var roles = JsonNode.Parse(await File.ReadAllTextAsync(rolePath))!.AsObject();
                if (mutation == "fixture-version") roles["version"] = 2;
                if (mutation == "fixture-pair-count") roles["pairs"]!.AsArray().RemoveAt(1);
                if (mutation == "fixture-profile") roles["pairs"]![1]!["dispatcher_profile"] = "full";
                if (mutation == "fixture-duplicate-runtime") roles["pairs"]![1]!["runtime"] = roles["pairs"]![0]!["runtime"]!.DeepClone();
                await File.WriteAllTextAsync(rolePath, roles.ToJsonString());
                break;
        }

        var runner = new FakeRunner((_, _) => throw new InvalidOperationException("Incomplete disposable inputs must never run."));
        await Assert.ThrowsAsync<PackageIndexException>(() => new DurablePreflightArtifactProof(runner).RunCandidateAsync(
            fixture.Request with { RepositoryRoot = repository }, CancellationToken.None));

        Assert.Equal(0, runner.Calls);
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(fixture.Request.ArtifactManifestPath));
        Assert.False(File.Exists(fixture.Request.ApprovedManifestPath));
        Assert.False(File.Exists(fixture.Request.ReceiptPath));
        Assert.Empty(Directory.GetFiles(_root, "candidate-receipt.json*.tmp"));
    }

    private async Task<string> CreateDisposableRepositoryAsync(string sourceRepository)
    {
        var repository = TestPathUtils.PathUnder(_root, "disposable-repository");
        foreach (var relativePath in new[]
        {
            "examples/durable-postgresql/role-pairs-full-and-work-only.example.json",
            "Durable/consumers/PostgreSqlPreflightConsumer/schema10-two-pair.sql"
        })
        {
            var destination = TestPathUtils.PathUnder(repository, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(TestPathUtils.PathUnder(sourceRepository, relativePath), destination);
        }

        // These files are only existence markers for FakeRunner; no shell or consumer is executed.
        await File.WriteAllTextAsync(TestPathUtils.PathUnder(repository, "Durable/verify-preflight-artifacts.sh"), "test entrypoint marker");
        await File.WriteAllTextAsync(TestPathUtils.PathUnder(repository, "Durable/consumers/PostgreSqlPreflightConsumer/PostgreSqlPreflightConsumer.csproj"), "test project marker");
        return repository;
    }

    [Theory]
    [InlineData("approved-missing")]
    [InlineData("receipt-missing")]
    [InlineData("already-promoted")]
    [InlineData("different-repository")]
    [InlineData("stale-receipt")]
    [InlineData("tampered-approval")]
    public async Task Promotion_InvalidRetainedInputsCannotAuthorizeDownloadedBundle(string mutation)
    {
        var fixture = await ProofFixture.CreateAsync(_root);
        var unapproved = TestPathUtils.PathUnder(fixture.ArtifactDirectory, "unapproved.json");
        var bytes = await File.ReadAllBytesAsync(fixture.Request.ArtifactManifestPath);
        File.Move(fixture.Request.ArtifactManifestPath, unapproved);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Request.ApprovedManifestPath)!);
        await File.WriteAllBytesAsync(fixture.Request.ApprovedManifestPath, bytes);
        await File.WriteAllTextAsync(fixture.Request.ReceiptPath, await fixture.CompleteReceiptJsonAsync(
            sourceCommit: mutation == "stale-receipt" ? new string('a', 40) : SourceCommit));
        var repository = fixture.Request.RepositoryRoot;
        if (mutation == "approved-missing") File.Delete(fixture.Request.ApprovedManifestPath);
        if (mutation == "receipt-missing") File.Delete(fixture.Request.ReceiptPath);
        if (mutation == "already-promoted") await File.WriteAllTextAsync(fixture.Request.ArtifactManifestPath, "existing publication input");
        if (mutation == "different-repository") repository = _root;
        if (mutation == "tampered-approval") await File.AppendAllTextAsync(fixture.Request.ApprovedManifestPath, "\n");

        await Assert.ThrowsAsync<PackageIndexException>(() => DurablePreflightArtifactProof.ValidateAndPromoteCandidateManifestAsync(
            fixture.Request with { ArtifactManifestPath = unapproved }, fixture.Request.ReceiptPath, repository, CancellationToken.None));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(unapproved));
        if (mutation == "already-promoted") Assert.Equal("existing publication input", await File.ReadAllTextAsync(fixture.Request.ArtifactManifestPath));
        else Assert.False(File.Exists(fixture.Request.ArtifactManifestPath));
        Assert.Empty(Directory.GetFiles(fixture.ArtifactDirectory, "*.tmp"));
    }

    [Theory]
    [InlineData("candidate-receipt-missing")]
    [InlineData("restore-missing")]
    [InlineData("recipe-missing")]
    [InlineData("dependency-archive-missing")]
    [InlineData("public-receipt-exists")]
    [InlineData("public-carrier-exists")]
    public async Task Published_MissingProofInputsOrExistingOutputsKeepActivationClosed(string mutation)
    {
        var fixture = await ProofFixture.CreateAsync(_root);
        var candidate = fixture.Request.ReceiptPath;
        await File.WriteAllTextAsync(candidate, await fixture.CompleteReceiptJsonAsync());
        var restored = await fixture.CreateRestoredPackagesAsync();
        var output = TestPathUtils.PathUnder(_root, "public-receipt.json");
        if (mutation == "candidate-receipt-missing") File.Delete(candidate);
        if (mutation == "restore-missing") Directory.Delete(restored, recursive: true);
        if (mutation == "recipe-missing") File.Delete(fixture.RestoredRecipePath(restored));
        if (mutation == "dependency-archive-missing") File.Delete(fixture.RestoredDependencyPackagePath(restored));
        if (mutation == "public-receipt-exists") await File.WriteAllTextAsync(output, "retained output");
        if (mutation == "public-carrier-exists") await File.WriteAllTextAsync(output + ".carrier.json", "retained output");
        var runner = new FakeRunner((_, _) => throw new InvalidOperationException("Missing public inputs cannot reach the consumer."));

        await Assert.ThrowsAsync<PackageIndexException>(() => new DurablePreflightArtifactProof(runner).RunPublishedAsync(
            fixture.Request, candidate, restored, output, CancellationToken.None));

        Assert.Equal(0, runner.Calls);
        if (mutation == "public-receipt-exists") Assert.Equal("retained output", await File.ReadAllTextAsync(output));
        else Assert.False(File.Exists(output));
        if (mutation == "public-carrier-exists") Assert.Equal("retained output", await File.ReadAllTextAsync(output + ".carrier.json"));
        else Assert.False(File.Exists(output + ".carrier.json"));
        Assert.Empty(Directory.GetFiles(_root, "public-receipt.json*.tmp"));
    }

    [Theory]
    [InlineData("cli-missing")]
    [InlineData("provider-missing")]
    [InlineData("cli-not-tool")]
    [InlineData("foreign-package")]
    [InlineData("nuspec-missing")]
    [InlineData("nuspec-duplicate")]
    [InlineData("nuspec-empty")]
    [InlineData("nuspec-oversized")]
    [InlineData("nuspec-malformed")]
    [InlineData("nuspec-wrong-repository")]
    [InlineData("archive-invalid")]
    [InlineData("recipe-missing")]
    [InlineData("recipe-duplicate")]
    public async Task Candidate_InvalidBundleClosureOrArchiveMetadataCannotProduceApproval(string mutation)
    {
        var fixture = await ProofFixture.CreateAsync(_root);
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Request.ArtifactManifestPath))!.AsObject();
        var entries = manifest["entries"]!.AsArray();
        if (mutation == "cli-missing") entries.RemoveAt(0);
        else if (mutation == "provider-missing") entries.RemoveAt(1);
        else if (mutation == "cli-not-tool")
        {
            entries[0]!["is_tool"] = false;
            entries[0]!["tool_command_name"] = "";
        }
        else if (mutation == "foreign-package") entries[2]!["package_id"] = "Foreign.Dependency";
        else
        {
            if (mutation == "archive-invalid") await File.WriteAllTextAsync(fixture.ProviderPackagePath, "invalid archive");
            else
            {
                using var archive = ZipFile.Open(fixture.ProviderPackagePath, ZipArchiveMode.Update);
                var nuspec = archive.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
                if (mutation.StartsWith("nuspec-", StringComparison.Ordinal))
                {
                    using var reader = new StreamReader(nuspec.Open());
                    var metadata = await reader.ReadToEndAsync();
                    reader.Dispose();
                    var name = nuspec.FullName;
                    if (mutation == "nuspec-duplicate")
                    {
                        await WriteArchiveEntryAsync(archive, "duplicate.nuspec", metadata);
                    }
                    else
                    {
                        nuspec.Delete();
                        if (mutation != "nuspec-missing") await WriteArchiveEntryAsync(archive, name, mutation switch
                        {
                            "nuspec-empty" => "",
                            "nuspec-oversized" => new string(' ', 1024 * 1024 + 1),
                            "nuspec-malformed" => "<package><metadata>",
                            _ => metadata.Replace("https://github.com/forge-trust/AppSurface", "https://example.invalid/other", StringComparison.Ordinal)
                        });
                    }
                }
                else
                {
                    var recipe = archive.Entries.Single(entry => entry.FullName.EndsWith("configure-postgresql-roles.sql", StringComparison.Ordinal));
                    if (mutation == "recipe-missing") recipe.Delete();
                    else await WriteArchiveEntryAsync(archive, recipe.FullName, "duplicate recipe");
                }
            }

            // These are producer-shaped malformed archives: the manifest honestly binds their bytes,
            // so rejection must come from archive identity/recipe validation rather than an old digest.
            entries[1]!["sha512"] = Convert.ToHexStringLower(SHA512.HashData(await File.ReadAllBytesAsync(fixture.ProviderPackagePath)));
        }

        await File.WriteAllTextAsync(fixture.Request.ArtifactManifestPath, manifest.ToJsonString(PackageArtifactJson.Options));
        var manifestBytes = await File.ReadAllBytesAsync(fixture.Request.ArtifactManifestPath);
        var runner = new FakeRunner((_, _) => throw new InvalidOperationException("Invalid bundle metadata cannot reach the consumer."));

        await Assert.ThrowsAsync<PackageIndexException>(() =>
            new DurablePreflightArtifactProof(runner).RunCandidateAsync(fixture.Request, CancellationToken.None));

        Assert.Equal(0, runner.Calls);
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(fixture.Request.ArtifactManifestPath));
        Assert.False(File.Exists(fixture.Request.ApprovedManifestPath));
        Assert.False(File.Exists(fixture.Request.ReceiptPath));
        Assert.Empty(Directory.GetFiles(_root, "candidate-receipt.json*.tmp"));
    }

    private static async Task WriteArchiveEntryAsync(ZipArchive archive, string name, string content)
    {
        await using var stream = archive.CreateEntry(name).Open();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BundleValidation_MissingFrozenInputsNeverReturnsAnIdentity(bool directoryMissing)
    {
        var fixture = await ProofFixture.CreateAsync(_root);
        if (directoryMissing) Directory.Delete(fixture.ArtifactDirectory, recursive: true);
        else File.Delete(fixture.Request.ArtifactManifestPath);

        await Assert.ThrowsAsync<PackageIndexException>(() => DurablePreflightArtifactProof.ValidateBundleAsync(
            fixture.ArtifactDirectory, fixture.Request.ArtifactManifestPath, SourceCommit, CancellationToken.None));

        Assert.False(File.Exists(fixture.Request.ApprovedManifestPath));
        Assert.False(File.Exists(fixture.Request.ReceiptPath));
    }

    [Theory]
    [InlineData("cache-missing")]
    [InlineData("cli-missing")]
    [InlineData("provider-missing")]
    [InlineData("archive-same-length-mismatch")]
    public async Task PublicArchiveComparison_MissingOrWrongRestoredInputsNeverReturnsMatchingEvidence(string mutation)
    {
        var fixture = await ProofFixture.CreateAsync(_root);
        var restored = await fixture.CreateRestoredPackagesAsync();
        var scratch = TestPathUtils.PathUnder(_root, "comparison-scratch");
        if (mutation == "cache-missing") Directory.Delete(restored, recursive: true);
        else if (mutation == "archive-same-length-mismatch")
        {
            var bytes = await File.ReadAllBytesAsync(fixture.RestoredDependencyPackagePath(restored));
            bytes[^1] ^= 1;
            await File.WriteAllBytesAsync(fixture.RestoredDependencyPackagePath(restored), bytes);
        }
        else
        {
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Request.ArtifactManifestPath))!.AsObject();
            manifest["entries"]!.AsArray().RemoveAt(mutation == "cli-missing" ? 0 : 1);
            await File.WriteAllTextAsync(fixture.Request.ArtifactManifestPath, manifest.ToJsonString(PackageArtifactJson.Options));
        }

        await Assert.ThrowsAsync<PackageIndexException>(() => DurablePreflightArtifactProof.CompareRestoredPackageBytesAsync(
            fixture.ArtifactDirectory, fixture.Request.ArtifactManifestPath, restored, scratch, CancellationToken.None));

        Assert.False(File.Exists(fixture.Request.ApprovedManifestPath));
        Assert.False(File.Exists(fixture.Request.ReceiptPath));
        Assert.False(File.Exists(TestPathUtils.PathUnder(scratch, "package-artifact-manifest.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeRunner(Func<ExternalCommandRequest, CancellationToken, Task<ExternalCommandResult>> callback) : IExternalCommandRunner
    {
        public int Calls { get; private set; }
        public ExternalCommandRequest? LastRequest { get; private set; }

        public async Task<ExternalCommandResult> RunAsync(ExternalCommandRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            return await callback(request, cancellationToken);
        }
    }

    private sealed class ProofFixture
    {
        private const string CliId = "ForgeTrust.AppSurface.Cli";
        private const string ProviderId = "ForgeTrust.AppSurface.Durable.PostgreSql";
        private const string RecipePath = "contentFiles/any/any/configure-postgresql-roles.sql";
        private readonly string _root;
        private readonly byte[] _cliBytes;
        private readonly byte[] _providerBytes;
        private readonly byte[] _dependencyBytes;
        private readonly byte[] _recipeBytes = Encoding.UTF8.GetBytes("create role appsurface_durable_owner;");

        private ProofFixture(string root, string artifactDirectory, string manifestPath, string cliPath, string providerPath,
            string dependencyPath, string manifestSha256, string cliSha256, string providerSha256, string dependencySha256)
        {
            _root = root;
            ArtifactDirectory = artifactDirectory;
            ManifestSha256 = manifestSha256;
            CliSha256 = cliSha256;
            ProviderSha256 = providerSha256;
            ProviderPackagePath = providerPath;
            DependencyPackagePath = dependencyPath;
            DependencySha256 = dependencySha256;
            _cliBytes = File.ReadAllBytes(cliPath);
            _providerBytes = File.ReadAllBytes(providerPath);
            _dependencyBytes = File.ReadAllBytes(dependencyPath);
            Request = new DurablePreflightArtifactProofRequest(
                FindRepositoryRoot(), artifactDirectory, manifestPath,
                TestPathUtils.PathUnder(root, "approved", "package-artifact-manifest.json"),
                TestPathUtils.PathUnder(root, "candidate-receipt.json"), SourceCommit, RunId, ArtifactId);
        }

        public DurablePreflightArtifactProofRequest Request { get; }
        public string ArtifactDirectory { get; }
        public string ProviderPackagePath { get; }
        public string DependencyPackagePath { get; }
        public string ManifestSha256 { get; }
        public string CliSha256 { get; }
        public string ProviderSha256 { get; }
        public string DependencySha256 { get; }

        public static async Task<ProofFixture> CreateAsync(string root, string? packageCommit = null, string? packageVersion = null,
            bool candidateSigned = false, bool largePayload = false)
        {
            var artifacts = TestPathUtils.PathUnder(root, "candidate");
            Directory.CreateDirectory(artifacts);
            var cliPath = TestPathUtils.PathUnder(artifacts, "ForgeTrust.AppSurface.Cli.1.2.3-ci.4.nupkg");
            var providerPath = TestPathUtils.PathUnder(artifacts, "ForgeTrust.AppSurface.Durable.PostgreSql.1.2.3-ci.4.nupkg");
            var dependencyPath = TestPathUtils.PathUnder(artifacts, "ForgeTrust.AppSurface.Durable.1.2.3-ci.4.nupkg");
            var nuspecCommit = packageCommit ?? SourceCommit;
            var nuspecVersion = packageVersion ?? Version;
            await CreatePackageAsync(cliPath, "ForgeTrust.AppSurface.Cli", nuspecVersion, nuspecCommit);
            await CreatePackageAsync(providerPath, "ForgeTrust.AppSurface.Durable.PostgreSql", nuspecVersion, nuspecCommit,
                "create role appsurface_durable_owner;");
            await CreatePackageAsync(dependencyPath, "ForgeTrust.AppSurface.Durable", nuspecVersion, nuspecCommit);
            if (candidateSigned)
            {
                using var archive = ZipFile.Open(providerPath, ZipArchiveMode.Update);
                await using var signature = archive.CreateEntry(".signature.p7s").Open();
                await signature.WriteAsync(Encoding.UTF8.GetBytes("synthetic candidate signature envelope"));
            }
            if (largePayload)
            {
                using var archive = ZipFile.Open(dependencyPath, ZipArchiveMode.Update);
                await using var payload = archive.CreateEntry("lib/net10.0/payload.bin").Open();
                await payload.WriteAsync(Enumerable.Range(0, 150_000).Select(index => (byte)(index % 251)).ToArray());
            }
            var entries = new[]
            {
                new PackageArtifactManifestEntry("ForgeTrust.AppSurface.Cli", "tools/cli.csproj", "publish", Path.GetFileName(cliPath), Hash512(cliPath), true, "appsurface"),
                new PackageArtifactManifestEntry("ForgeTrust.AppSurface.Durable.PostgreSql", "Durable/provider.csproj", "publish", Path.GetFileName(providerPath), Hash512(providerPath), false, ""),
                new PackageArtifactManifestEntry("ForgeTrust.AppSurface.Durable", "Durable/runtime.csproj", "publish", Path.GetFileName(dependencyPath), Hash512(dependencyPath), false, "")
            };
            var manifestPath = TestPathUtils.PathUnder(artifacts, "package-artifact-manifest.json");
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new PackageArtifactManifest(1, Version, DateTimeOffset.UnixEpoch, entries), PackageArtifactJson.Options));
            var manifestSha = Sha256(manifestPath);
            return new ProofFixture(root, artifacts, manifestPath, cliPath, providerPath, dependencyPath,
                manifestSha, Sha256(cliPath), Sha256(providerPath), Sha256(dependencyPath));
        }

        public async Task<string> CompleteReceiptJsonAsync(string sourceCommit = SourceCommit, string? manifestSha = null, string? mutation = null)
        {
            var repositoryRoot = FindRepositoryRoot();
            var rolePairsPath = TestPathUtils.PathUnder(repositoryRoot, "examples/durable-postgresql/role-pairs-full-and-work-only.example.json");
            var schema10Path = TestPathUtils.PathUnder(repositoryRoot, "Durable/consumers/PostgreSqlPreflightConsumer/schema10-two-pair.sql");
            var rolePairsSha = Sha256(rolePairsPath);
            using var rolePairsDoc = JsonDocument.Parse(await File.ReadAllBytesAsync(rolePairsPath));
            var firstPair = rolePairsDoc.RootElement.GetProperty("pairs")[0];
            var onePairBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1,
                pairs = new[]
                {
                    new
                    {
                        dispatcher = firstPair.GetProperty("dispatcher").GetString(),
                        runtime = firstPair.GetProperty("runtime").GetString(),
                        dispatcher_profile = "full"
                    }
                }
            });
            var onePairSha = Sha256(onePairBytes);
            var laneSkipped = mutation == "lane-proof-skipped";
            var scenarios = new object[]
            {
                Scenario("one-pair", 1, false, laneSkipped),
                Scenario("pair-enrollment", 2, false),
                Scenario("identical-rerun-stable-catalog", 2, false),
                Scenario("schema10-to-11-two-pair", 2, true)
            };
            var cliResults = new List<object>();
            AddScenarioResults("one-pair", 1, onePairSha, includeSource: false);
            foreach (var scenario in new[] { "pair-enrollment", "identical-rerun-stable-catalog", "schema10-to-11-two-pair" })
            {
                AddScenarioResults(scenario, 2, rolePairsSha, includeSource: true);
            }

            var writerRecovery = mutation == "writer-recovery";
            var writerScenario = writerRecovery ? "queued-writer-postwriter-rerun" : "queued-writer-prewriter-complete";
            AddScenarioResults(writerScenario, 2, rolePairsSha, includeSource: true);

            var packages = new[]
            {
                new { packageId = "ForgeTrust.AppSurface.Cli", version = Version, sha256 = CliSha256 },
                new { packageId = "ForgeTrust.AppSurface.Durable.PostgreSql", version = Version, sha256 = ProviderSha256 },
                new { packageId = "ForgeTrust.AppSurface.Durable", version = Version, sha256 = DependencySha256 }
            };
            var migrationChecksums = Enumerable.Range(1, 10)
                .Select(version => new { Version = version, Name = $"migration_{version:0000}", sha256 = new string('e', 64) })
                .ToArray();
            var json = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                proofKind = "issue-845-exact-package-disposable-postgresql-consumer",
                sourceCommit,
                runId = RunId,
                artifactId = ArtifactId,
                packageVersion = Version,
                artifactManifestSha256 = manifestSha ?? ManifestSha256,
                packages,
                exactCliPackageSha256 = CliSha256,
                exactProviderPackageSha256 = ProviderSha256,
                extractedRoleRecipeSha256 = Sha256(Encoding.UTF8.GetBytes("create role appsurface_durable_owner;")),
                schema10FixtureSha256 = Sha256(schema10Path),
                completeRoleManifestSha256 = rolePairsSha,
                onePairManifestSha256 = onePairSha,
                migrationOwnerRole = "appsurface_durable_owner",
                storeId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa8450",
                activeEpoch = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb8450",
                postgresImage = "postgres:16.5@sha256:53f3e608f9475ce120ced2d0f430b89458d7faa28530e0b0977a6af64d294877",
                scenarios,
                cliResults,
                migrationChecksums,
                guardWindow = new { intact = true, backendPids = new[] { 41, 42, 43, 44 } },
                activationCallbackCompleted = true,
                schema10BaselineSnapshotSha256 = new string('8', 64),
                catalogSnapshotSha256BeforeAndAfterIdenticalRerun = new[] { new string('7', 64), new string('7', 64) },
                queuedWriter = new
                {
                    outcome = writerRecovery ? "bounded-failure-then-rerun" : "completed-before-writer",
                    authoritativeScenario = writerScenario,
                    queuedWriterObserved = true,
                    writerFinished = true,
                    authoritativeWindowCompletedBeforeWriter = !writerRecovery,
                    writerAcquiredAfterWindow = !writerRecovery,
                    boundedFailure = writerRecovery ? new { failureKind = "runtime-child", stage = "cli-runtime", durationMilliseconds = 125.0 } : null,
                    writerReleasedAfterFailure = writerRecovery,
                    writerFinishedBeforeRerun = writerRecovery,
                    authoritativeWindow = Scenario(writerScenario, 2, false)
                },
                guardLossNegativeProof = new
                {
                    lanePassBackendPid = 51,
                    lanePassExecutingBackendPid = 53,
                    lanePassStoreId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa8450",
                    lanePassActiveEpoch = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb8450",
                    lanePassStarted = true,
                    lanePassObservedExecuting = true,
                    lanePassBackendTerminated = true,
                    lanePassCancellationObserved = true,
                    lanePassChildDrained = true,
                    lanePassReceiptWithheld = true,
                    activationBackendTerminated = true,
                    activationCancellationObserved = true,
                    activationHostDrainPersisted = true,
                    activationAdmissionClosed = true,
                    activationSessionsReleased = true,
                    activationReceiptWithheld = true,
                    activationBackendPid = 52,
                    activationStoreId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa8450",
                    activationActiveEpoch = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb8450",
                    activationWorkInvocationStartedBeforeCompletion = true,
                    activationDrainVerifiedCheckpointObserved = true,
                    activationHostedServicesStopped = true,
                    activationChildDrained = true,
                    activationIdentityUnchanged = true,
                    finalReceiptAbsentBeforeAndAfter = true
                },
                stageDurationsMilliseconds = new
                {
                    one_pair_bootstrap_ms = 1.0,
                    pair_enrollment_recipe_ms = 1.0,
                    identical_rerun_ms = 1.0,
                    schema10_to_11_upgrade_ms = 1.0,
                    queued_writer_fresh_rerun_ms = 1.0
                },
                totalDurationMilliseconds = 10.0
            }, PackageArtifactJson.Options);
            if (mutation == "duplicate-property")
            {
                return json.Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"schemaVersion\": 1,", StringComparison.Ordinal);
            }

            var mutated = JsonNode.Parse(json)!.AsObject();
            switch (mutation)
            {
                case "wrong-package-hash": mutated["exactCliPackageSha256"] = new string('f', 64); break;
                case "wrong-recipe-hash": mutated["extractedRoleRecipeSha256"] = new string('f', 64); break;
                case "wrong-store": mutated["storeId"] = Guid.NewGuid().ToString("D"); break;
                case "duplicate-role": mutated["cliResults"]!.AsArray()[3]!["Role"] = "appsurface_durable_runtime"; break;
                case "duplicate-pair":
                    mutated["cliResults"]!.AsArray()[3]!["Pair"] = 1;
                    mutated["cliResults"]!.AsArray()[3]!["Role"] = "appsurface_durable_runtime";
                    break;
                case "empty-guard": mutated["scenarios"]!.AsArray()[1]!["ownerGuard"] = ""; break;
                case "missing-runtime": mutated["cliResults"]!.AsArray().RemoveAt(3); break;
                case "lane-proof-skipped": break;
                case "wrong-lane-scenario": mutated["scenarios"]!.AsArray()[1]!["laneEvidenceBefore"]!["scenario"] = "one-pair"; break;
                case "wrong-result-scenario": mutated["cliResults"]!.AsArray()[2]!["Scenario"] = "one-pair"; break;
                case "missing-source-denial": mutated["scenarios"]!.AsArray()[1]!["laneEvidenceAfter"]!["sourceWorkOnly"]!["flowResult"] = "completed"; break;
                case "one-pair-uninstalled-source":
                    // The one-pair window has no Source runtime. A producer cannot project the global
                    // two-pair fixture onto this scenario, even when every Source result says missing.
                    mutated["scenarios"]!.AsArray()[0]!["laneEvidenceBefore"]!["sourceWorkOnly"] = new JsonObject
                    {
                        ["runtimeRole"] = "appsurface_durable_source_runtime",
                        ["workResult"] = "missing",
                        ["flowResult"] = "missing",
                        ["scheduleResult"] = "missing",
                        ["allResult"] = "missing"
                    };
                    break;
                case "bad-writer-outcome": mutated["queuedWriter"]!["outcome"] = "pretend-success"; break;
                case "wrong-writer-scenario": mutated["queuedWriter"]!["authoritativeScenario"] = "queued-writer-postwriter-rerun"; break;
                case "writer-runtime-connection-string":
                    mutated["queuedWriter"]!["authoritativeWindow"]!["laneEvidenceBefore"]!["forwarder"]!["runtimeRole"] =
                        "Host=disposable.invalid;Username=appsurface_durable_runtime";
                    break;
            }
            json = mutated.ToJsonString(PackageArtifactJson.Options);
            return await Task.FromResult(json);

            void AddScenarioResults(string scenario, int count, string manifestHash, bool includeSource)
            {
                cliResults.Add(new ReceiptRecord(scenario, "runtime", 1, "appsurface_durable_runtime", "appsurface_durable_owner", count,
                    manifestHash, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa8450", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb8450", 3.1));
                if (includeSource)
                {
                    cliResults.Add(new ReceiptRecord(scenario, "runtime", 2, "appsurface_durable_source_runtime", "appsurface_durable_owner", count,
                        manifestHash, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa8450", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb8450", 3.2));
                }

                cliResults.Add(new ReceiptRecord(scenario, "owner-diagnostic", null, "appsurface_durable_owner", "appsurface_durable_owner", count,
                    manifestHash, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa8450", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb8450", 4.0));
            }

            static object Scenario(string name, int runtimeCount, bool schema10, bool laneSkipped = false)
                => new
                {
                    name,
                    scenario = name,
                    ownerGuard = "continuous-shared-session-lock",
                    guardBackendPid = 42,
                    runtimePreflightCount = runtimeCount,
                    ownerDiagnostic = "separate-and-never-counted",
                    laneEvidence = laneSkipped ? "lane-proof-skipped" : runtimeCount == 1
                        ? "forwarder-work-flow-schedule-passed"
                        : "forwarder-work-flow-schedule-and-source-work-only-denials-passed",
                    laneEvidenceBefore = laneSkipped ? null : LaneEvidence(name, runtimeCount == 2),
                    laneEvidenceAfter = laneSkipped ? null : LaneEvidence(name, runtimeCount == 2),
                    fixtureActivationCallbackCompleted = true,
                    storeId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa8450",
                    activeEpoch = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb8450",
                    modeledSchema10BaselinePresent = schema10
                };

            static object LaneEvidence(string name, bool includeSource)
                => new
                {
                    scenario = name,
                    storeId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa8450",
                    activeEpoch = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb8450",
                    guardBackendPid = 42,
                    forwarder = new
                    {
                        runtimeRole = "appsurface_durable_runtime",
                        workResult = "completed",
                        flowResult = "completed",
                        scheduleResult = "completed"
                    },
                    sourceWorkOnly = includeSource ? new
                    {
                        runtimeRole = "appsurface_durable_source_runtime",
                        workResult = "completed",
                        flowResult = "denied",
                        scheduleResult = "denied",
                        allResult = "denied"
                    } : null
                };

        }

        public async Task WriteCompleteReceiptAsync(ExternalCommandRequest request, CancellationToken token)
            => await File.WriteAllTextAsync(request.Arguments[Array.IndexOf(request.Arguments.ToArray(), "--receipt") + 1], await CompleteReceiptJsonAsync(), token);

        public async Task<string> CreateRestoredPackagesAsync()
        {
            var restored = TestPathUtils.PathUnder(_root, "restored");
            Directory.CreateDirectory(Path.GetDirectoryName(RestoredProviderPackagePath(restored))!);
            Directory.CreateDirectory(Path.GetDirectoryName(RestoredRecipePath(restored))!);
            Directory.CreateDirectory(Path.GetDirectoryName(RestoredCliPackagePath(restored))!);
            Directory.CreateDirectory(Path.GetDirectoryName(RestoredDependencyPackagePath(restored))!);
            await File.WriteAllBytesAsync(RestoredProviderPackagePath(restored), _providerBytes);
            await File.WriteAllBytesAsync(RestoredCliPackagePath(restored), _cliBytes);
            await File.WriteAllBytesAsync(RestoredDependencyPackagePath(restored), _dependencyBytes);
            await File.WriteAllBytesAsync(RestoredRecipePath(restored), _recipeBytes);
            return restored;
        }

        public string RestoredProviderPackagePath(string root) => TestPathUtils.PathUnder(root, "forgetrust.appsurface.durable.postgresql", Version.ToLowerInvariant(), "forgetrust.appsurface.durable.postgresql.1.2.3-ci.4.nupkg");
        public string RestoredRecipePath(string root) => TestPathUtils.PathUnder(root, "forgetrust.appsurface.durable.postgresql", Version.ToLowerInvariant(), RecipePath.Replace('/', Path.DirectorySeparatorChar));
        public string RestoredCliPackagePath(string root) => TestPathUtils.PathUnder(root, "forgetrust.appsurface.cli", Version.ToLowerInvariant(), "forgetrust.appsurface.cli.1.2.3-ci.4.nupkg");
        public string RestoredDependencyPackagePath(string root) => TestPathUtils.PathUnder(root, "forgetrust.appsurface.durable", Version.ToLowerInvariant(), "forgetrust.appsurface.durable.1.2.3-ci.4.nupkg");

        private static async Task CreatePackageAsync(string path, string packageId, string packageVersion, string repositoryCommit, string? recipe = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var stream = File.Create(path);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            var nuspec = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\"><metadata><id>{packageId}</id><version>{packageVersion}</version><repository type=\"git\" url=\"https://github.com/forge-trust/AppSurface\" commit=\"{repositoryCommit}\" /></metadata></package>";
            await using (var nuspecStream = archive.CreateEntry($"{packageId}.nuspec").Open())
                await nuspecStream.WriteAsync(Encoding.UTF8.GetBytes(nuspec));
            if (recipe is not null)
            {
                await using var recipeStream = archive.CreateEntry(RecipePath).Open();
                await recipeStream.WriteAsync(Encoding.UTF8.GetBytes(recipe));
            }
        }

        private static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        private static string Hash512(string path) => Convert.ToHexStringLower(SHA512.HashData(File.ReadAllBytes(path)));

        private sealed record ReceiptRecord(string Scenario, string Caller, int? Pair, string Role, string Owner, int RuntimeCount,
            string ManifestSha256, string StoreId, string ActiveEpoch, double ElapsedMilliseconds);

        private static string FindRepositoryRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(TestPathUtils.PathUnder(directory.FullName, "Durable", "verify-preflight-artifacts.sh")))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException("Could not locate the repository root containing the durable proof script.");
        }
    }
}
