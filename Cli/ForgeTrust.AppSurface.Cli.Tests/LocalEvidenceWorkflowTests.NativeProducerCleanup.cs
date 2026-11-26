using System.IO.Compression;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Evidence.Validation;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed partial class LocalEvidenceWorkflowTests
{
    [Fact]
    public async Task NativeProducerCleanup_CanRetryDeletingOwnedScratchAfterADisposalError()
    {
        var owner = new NativeProducerWorkspace();
        var workspace = owner.Create(_root, new EvidenceWorkspaceOptions(MinimumFreeBytes: 0));
        var moved = TestPathUtils.PathUnder(_root, "moved-workspace");
        Directory.Move(workspace.RootPath, moved);

        await Assert.ThrowsAsync<IOException>(() => owner.ReleaseIfSettledAsync().AsTask());
        Assert.True(Directory.Exists(moved));
        Directory.Move(moved, workspace.RootPath);

        await owner.DisposeAsync();
        Assert.False(Directory.Exists(workspace.RootPath));
    }

    [Theory]
    [InlineData("installed-consumer", false)]
    [InlineData("installed-consumer", true)]
    [InlineData("packed-storage", false)]
    [InlineData("packed-storage", true)]
    public async Task NativeProducerCleanupFailure_RetainsWorkspaceAndFailsDirectAndRegisteredRuns(
        string producerId,
        bool registeredHost)
    {
        var scratch = TestPathUtils.PathUnder(_root, "native-producer-cleanup", "scratch");
        var artifacts = TestPathUtils.PathUnder(_root, "native-producer-cleanup", "artifacts");
        var package = TestPathUtils.PathUnder(_root, "native-producer-cleanup", "candidate.nupkg");
        Directory.CreateDirectory(scratch);
        CreateCleanupCandidatePackage(package);

        var settings = new ValidationSettings(
            _repo,
            package,
            "1.2.3-cleanup-test",
            "cleanup-test-commit",
            "/missing/dotnet",
            scratch,
            artifacts,
            MinimumFreeBytes: 0);
        var declaration = RepositoryEvidenceWorkflow.Declaration(producerId);
        var plan = CreateNativeCleanupPlan(declaration);
        string? workspacePath = null;
        string? canaryPath = null;
        var commandCount = 0;

        Task<EvidenceCommandResult> FailCleanupBeforeLaunchingCommand(
            EvidenceCommandRequest _request,
            CancellationToken _cancellationToken)
        {
            commandCount++;
            var createdWorkspacePath = Assert.Single(Directory.EnumerateDirectories(scratch, "evidence-workspace-*"));
            workspacePath = createdWorkspacePath;
            canaryPath = TestPathUtils.PathUnder(createdWorkspacePath, "retained-canary.txt");
            File.WriteAllText(canaryPath, "producer-owned workspace survived");
            throw new EvidenceCommandException("cleanup_failed", "Synthetic command cleanup failure.");
        }

        var producer = CreateCleanupProducer(producerId, settings, FailCleanupBeforeLaunchingCommand);
        if (registeredHost)
        {
            await using var host = EvidenceHostBootstrap.Create(
                plan,
                registration => registration.AddProducer(producer),
                new EvidenceHostOptions(ArtifactDirectory: artifacts));

            var manifest = await host.RunAsync();
            var result = Assert.Single(manifest.ProducerResults);
            Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
            Assert.Contains("cleanup_failed", result.Diagnostic, StringComparison.Ordinal);
            Assert.False(manifest.Metrics.CleanupCompleted);
            Assert.Contains(nameof(EvidenceCommandException), manifest.Metrics.CleanupDiagnostic, StringComparison.Ordinal);
        }
        else
        {
            var receipt = await NativeValidationExecution.ExecuteAsync(
                plan,
                [producer],
                artifacts,
                new string('a', 64),
                CancellationToken.None);

            var journey = Assert.Single(receipt.Journeys);
            Assert.Equal("failed", journey.Outcome);
            Assert.Contains("cleanup_failed", journey.Diagnostic, StringComparison.Ordinal);
            Assert.False(receipt.CleanupCompleted);
            Assert.Contains(nameof(EvidenceCommandException), receipt.CleanupDiagnostic, StringComparison.Ordinal);
            Assert.Equal("failed", receipt.RunOutcome);
        }

        Assert.Equal(1, commandCount);
        var retainedWorkspacePath = Assert.IsType<string>(workspacePath);
        var retainedCanaryPath = Assert.IsType<string>(canaryPath);
        Assert.True(Directory.Exists(retainedWorkspacePath));
        Assert.Equal("producer-owned workspace survived", File.ReadAllText(retainedCanaryPath));

        var disposable = Assert.IsAssignableFrom<IAsyncDisposable>(producer);
        var disposalFailure = await Assert.ThrowsAsync<EvidenceCommandException>(() => disposable.DisposeAsync().AsTask());
        Assert.Equal("cleanup_failed", disposalFailure.Code);
        Assert.True(File.Exists(retainedCanaryPath));
    }

    private static IEvidenceProducer CreateCleanupProducer(
        string producerId,
        ValidationSettings settings,
        Func<EvidenceCommandRequest, CancellationToken, Task<EvidenceCommandResult>> runCommand) => producerId switch
        {
            "installed-consumer" => new InstalledConsumerProducer(settings, runCommand),
            "packed-storage" => new PackedStorageProducer(settings, runCommand),
            _ => throw new ArgumentException("Unknown native producer.", nameof(producerId)),
        };

    private static EvidencePlan CreateNativeCleanupPlan(EvidenceProducerDeclaration declaration)
    {
        var profile = new EvidenceProfile(
            "native-cleanup",
            EvidenceProfileScope.Targeted,
            [],
            [declaration],
            declaration.AssertionIds.Select((assertionId, index) => new EvidenceObligation(
                declaration.Id + "-" + index,
                "native-acceptance",
                "Verify the native producer cleanup lifecycle.",
                [declaration.Id],
                assertionId)).ToArray());
        var policy = new EvidencePolicy("native-cleanup-tests", "1", profile.Id, [profile], []);
        return new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("LocalEvidenceWorkflowTests.NativeProducerCleanup.cs")]);
    }

    private static void CreateCleanupCandidatePackage(string packagePath)
    {
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("package.nuspec").Open()))
        {
            writer.Write("<package><metadata><id>ForgeTrust.AppSurface.Cli</id><version>1.2.3-cleanup-test</version><repository commit='cleanup-test-commit' /></metadata></package>");
        }

        using var assembly = archive.CreateEntry("tools/net10.0/any/ForgeTrust.AppSurface.Cli.dll").Open();
        assembly.WriteByte(0);
    }
}
