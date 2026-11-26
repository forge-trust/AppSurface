using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Validation;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed partial class LocalEvidenceWorkflowTests
{
    [Fact]
    public async Task UnconfirmedCleanup_BlocksExecutionRetriesButAllowsPlanningAndManualRecovery()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "src/feature.cs", "class Feature { }");
        var retained = TestPathUtils.PathUnder(fixture.Root, "TestResults", "repository-evidence", "local");
        var gitDirectory = TestPathUtils.PathUnder(fixture.Root, ".git");
        var pendingCleanup = PendingCleanupMarker(gitDirectory);
        var callbackCalls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalEvidenceOutput.RunAsync(
            fixture.Root,
            "local",
            gitDirectory,
            async run =>
            {
                Directory.CreateDirectory(run.OutputDirectory);
                var request = new EvidenceCommandRequest("dotnet", ["--version"], run.OutputDirectory, TimeSpan.FromSeconds(1));
                await Assert.ThrowsAsync<EvidenceCommandException>(() => run.ObserveCommandAsync(
                    (_, _) => throw new EvidenceCommandException("cleanup_failed", "command still owns scratch"),
                    request,
                    CancellationToken.None));
                return null;
            }));

        Assert.True(File.Exists(pendingCleanup));

        var workflow = new LocalEvidenceWorkflow(workflow: async (options, token) =>
        {
            callbackCalls++;
            Assert.True(File.Exists(pendingCleanup));
            await WriteCompleteProofAsync(options, token);
            return 0;
        });
        var managedRun = Options(fixture, "local", "coverage");
        managedRun.Remove("--output");
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RunAsync(managedRun, CancellationToken.None));
        Assert.Contains(pendingCleanup, blocked.Message, StringComparison.Ordinal);
        Assert.Equal(0, callbackCalls);

        var explicitRun = Options(fixture, "local", "coverage");
        var explicitOutput = explicitRun["--output"];
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RunAsync(explicitRun, CancellationToken.None));
        Assert.False(Directory.Exists(explicitOutput));
        Assert.Equal(0, callbackCalls);

        var managedPlan = Options(fixture, "local-plan", "coverage");
        managedPlan.Remove("--output");
        Assert.Equal(0, await workflow.RunAsync(managedPlan, CancellationToken.None));
        Assert.True(File.Exists(pendingCleanup));
        Assert.NotNull(ReadArchive(retained, "latest-plan.zip", "plan/evidence-plan.json"));

        var explicitPlan = Options(fixture, "local-plan", "coverage");
        Assert.Equal(0, await workflow.RunAsync(explicitPlan, CancellationToken.None));
        Assert.True(File.Exists(pendingCleanup));

        File.Delete(pendingCleanup);
        Assert.Equal(0, await workflow.RunAsync(managedRun, CancellationToken.None));
        Assert.Equal(1, callbackCalls);
        Assert.False(File.Exists(pendingCleanup));
    }

    [Fact]
    public async Task SettledFailure_ClearsPendingCleanupMarker()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "src/feature.cs", "class Feature { }");
        var marker = PendingCleanupMarker(TestPathUtils.PathUnder(fixture.Root, ".git"));
        var workflow = new LocalEvidenceWorkflow(workflow: (options, _) =>
        {
            Assert.True(File.Exists(marker));
            Directory.CreateDirectory(options["--output"]);
            return Task.FromResult(1);
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RunAsync(Options(fixture, "local", "coverage"), CancellationToken.None));

        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task SettledFailure_PreservesOriginalErrorWhenPendingCleanupMarkerCannotBeCleared()
    {
        var pendingCleanup = PendingCleanupMarker(_root);
        var failure = new InvalidOperationException("Original validation failure.");
        var result = await Assert.ThrowsAsync<InvalidOperationException>(() => LocalEvidenceOutput.RunAsync(_root, "local", _root, _ =>
        {
            File.Delete(pendingCleanup);
            Directory.CreateDirectory(pendingCleanup);
            return Task.FromException<LocalEvidenceRunResult?>(failure);
        }));

        Assert.Same(failure, result);
        Assert.True(Directory.Exists(pendingCleanup));
    }

    [Fact]
    public async Task UnconfirmedExplicitPack_CarriesGuardToManagedExecution()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, StoragePath, "// Select a candidate package.\n");
        var marker = PendingCleanupMarker(TestPathUtils.PathUnder(fixture.Root, ".git"));
        var failure = new EvidenceCommandException(EvidenceCommandException.CleanupFailedCode, "Package command still owns the worktree.");
        var explicitRun = Options(fixture, "local", "native");
        var packCount = 0;
        var workflow = new LocalEvidenceWorkflow(command: (request, _) =>
        {
            packCount++;
            Assert.Equal("pack", request.Arguments[0]);
            Assert.True(File.Exists(marker));
            return Task.FromException<EvidenceCommandResult>(failure);
        });

        Assert.Same(failure, await Assert.ThrowsAsync<EvidenceCommandException>(
            () => workflow.RunAsync(explicitRun, CancellationToken.None)));
        Assert.Equal(explicitRun["--output"], (await File.ReadAllTextAsync(marker)).TrimEnd('\r', '\n'));
        Assert.False(Directory.Exists(TestPathUtils.PathUnder(fixture.Root, "TestResults")));

        explicitRun.Remove("--output");
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RunAsync(explicitRun, CancellationToken.None));
        Assert.Equal(1, packCount);
    }

    [Fact]
    public async Task UnconfirmedHostCleanup_RetainsGuardBeforeManifestPublication()
    {
        var pendingCleanup = PendingCleanupMarker(_root);
        var hostFailure = new IOException("Manifest could not be published.");
        var result = await Assert.ThrowsAsync<IOException>(() => LocalEvidenceOutput.RunAsync(_root, "local", _root, run =>
        {
            run.BeginHostExecution();
            run.RecordHostCleanup(completed: false);
            return Task.FromException<LocalEvidenceRunResult?>(hostFailure);
        }));

        Assert.Same(hostFailure, result);
        Assert.True(File.Exists(pendingCleanup));
    }

    private static string PendingCleanupMarker(string gitDirectory) =>
        TestPathUtils.PathUnder(gitDirectory, "appsurface-local-evidence", "run.pending-cleanup");
}
