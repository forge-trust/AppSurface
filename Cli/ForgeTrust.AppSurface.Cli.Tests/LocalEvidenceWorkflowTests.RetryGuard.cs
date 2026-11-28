using System.ComponentModel;
using System.IO.Pipelines;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
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
                var failedReader = Task.FromException(new IOException("Git output capture failed before EOF."));
                var verifySource = run.TrackSourceVerification(_ => EvidenceProcessShutdown.ConfirmAsync(
                    () => { }, () => Task.CompletedTask, [failedReader, Task.CompletedTask], TimeSpan.FromSeconds(1)));
                var failure = await Assert.ThrowsAsync<EvidenceCommandException>(() => verifySource(CancellationToken.None));
                Assert.True(failure.CleanupUnconfirmed);
                return null;
            }));

        Assert.True(File.Exists(pendingCleanup));

        var workflow = new LocalEvidenceWorkflow(workflow: async (options, token) =>
        {
            callbackCalls++;
            Assert.True(File.Exists(pendingCleanup));
            return await BuildCompleteProofAsync(options, token);
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
            throw new InvalidOperationException("Simulated producer failure.");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RunAsync(Options(fixture, "local", "coverage"), CancellationToken.None));

        Assert.False(File.Exists(marker));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GitShutdownFailure_PreservesPendingCleanupMarker(bool terminationFails)
    {
        var marker = PendingCleanupMarker(_root);
        var waitCalls = 0;

        var failure = await Assert.ThrowsAsync<EvidenceCommandException>(() =>
            LocalEvidenceOutput.RunAsync(_root, "local", _root, async _ =>
            {
                await EvidenceProcessShutdown.ConfirmAsync(
                    () =>
                    {
                        if (terminationFails)
                            throw new Win32Exception("Git termination failed.");
                    },
                    () =>
                    {
                        waitCalls++;
                        return Task.FromException(new TimeoutException("Git exit could not be observed."));
                    },
                    [Task.CompletedTask], TimeSpan.FromSeconds(1));
                return null;
            }));

        Assert.Equal(EvidenceCommandException.CleanupFailedCode, failure.Code);
        Assert.Equal(terminationFails ? 0 : 1, waitCalls);
        Assert.True(File.Exists(marker));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedGitCommandWithOpenOutputPipe_PreservesPendingCleanupMarker(bool limitExceeded)
    {
        var pipe = new Pipe();
        await using var output = pipe.Reader.AsStream();
        using var commandLifetime = new CancellationTokenSource();
        var capture = RepositoryGitChanges.ReadBoundedAsync(output, 4, commandLifetime, CancellationToken.None);
        var marker = PendingCleanupMarker(_root);

        try
        {
            if (limitExceeded)
            {
                var overflowObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = commandLifetime.Token.Register(() => overflowObserved.TrySetResult());
                await pipe.Writer.WriteAsync("excess"u8.ToArray());
                await overflowObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            else
            {
                commandLifetime.Cancel();
            }

            var failure = await Assert.ThrowsAsync<EvidenceCommandException>(() =>
                LocalEvidenceOutput.RunAsync(_root, "local", _root, async _ =>
                {
                    await EvidenceProcessShutdown.ConfirmAsync(() => { }, () => Task.CompletedTask, [capture], TimeSpan.FromMilliseconds(100));
                    return null;
                }));
            Assert.True(failure.CleanupUnconfirmed);
            Assert.True(File.Exists(marker));
            Assert.False(capture.IsCompleted);
        }
        finally
        {
            await pipe.Writer.CompleteAsync();
        }
        var result = await capture;
        if (limitExceeded)
            Assert.Null(result);
        else
            Assert.Empty(Assert.IsType<byte[]>(result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledOutputReader_PreservesPendingCleanupMarker(bool otherReaderFaulted)
    {
        var canceled = Task.FromCanceled(new CancellationToken(canceled: true));
        var other = otherReaderFaulted ? Task.FromException(new IOException("Git output reader failed.")) : Task.CompletedTask;
        var marker = PendingCleanupMarker(_root);

        var failure = await Assert.ThrowsAsync<EvidenceCommandException>(() =>
            LocalEvidenceOutput.RunAsync(_root, "local", _root, async _ =>
            {
                await EvidenceProcessShutdown.ConfirmAsync(() => { }, () => Task.CompletedTask, [canceled, other], TimeSpan.FromSeconds(1));
                return null;
            }));

        Assert.True(failure.CleanupUnconfirmed);
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public async Task OversizedGitOutput_CancelsCommandWork()
    {
        using var output = new MemoryStream("excess"u8.ToArray());
        using var commandLifetime = new CancellationTokenSource();

        var captured = await RepositoryGitChanges.ReadBoundedAsync(output, 4, commandLifetime, CancellationToken.None);

        Assert.Null(captured);
        Assert.True(commandLifetime.IsCancellationRequested);
    }

    [Fact]
    public async Task OversizedGitOutputWithClosedPipes_ClearsPendingCleanupMarker()
    {
        var fixture = await CreateFixtureAsync();
        var gitDirectory = TestPathUtils.PathUnder(fixture.Root, ".git");
        var marker = PendingCleanupMarker(gitDirectory);
        var oversized = TestPathUtils.PathUnder(_root, "oversized-git-object");
        using (var file = File.Create(oversized))
            file.SetLength(16L * 1024 * 1024 + 1);
        var blob = (await GitAsync(fixture.Root, "hash-object", "-w", oversized)).Trim();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LocalEvidenceOutput.RunAsync(fixture.Root, "local", gitDirectory, async _ =>
            {
                await RepositoryGitChanges.ReadIdentityAsync(fixture.Root, CancellationToken.None, "cat-file", "blob", blob);
                return null;
            }));

        Assert.Contains("Git output exceeded", failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task SettledGitFailure_ClearsPendingCleanupMarker()
    {
        var fixture = await CreateFixtureAsync();
        var marker = PendingCleanupMarker(TestPathUtils.PathUnder(fixture.Root, ".git"));
        var options = Options(fixture, "local", "coverage");
        options["--base"] = "refs/heads/missing-evidence-test-base";

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new LocalEvidenceWorkflow().RunAsync(options, CancellationToken.None));

        Assert.StartsWith("Git command failed:", failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task GitShutdownDuringCoverageSourceVerification_RetainsMarkerAndRunOutput()
    {
        NormalizedDiffPath[] changes = [new("src/feature.cs")];
        var identity = (await GitAsync(_repo, "rev-parse", "HEAD")).Trim();
        var marker = PendingCleanupMarker(_root);
        var cleanupFailure = new EvidenceCommandException(EvidenceCommandException.CleanupFailedCode, "Git exit was not confirmed.");
        var sourceChecks = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalEvidenceOutput.RunAsync(_root, "local", _root, async run =>
        {
            var (options, plan) = await PrepareRegisteredRunAsync(run.OutputDirectory, "coverage", changes);
            Assert.Equal("solution-coverage", Assert.Single(plan.Profile.Producers).Id);
            var patchPath = TestPathUtils.PathUnder(run.OutputDirectory, "changed.patch");
            await File.WriteAllTextAsync(patchPath, string.Empty);
            options["--diff-file"] = patchPath;
            await RepositoryEvidenceWorkflow.RunLocalAsync(options, CancellationToken.None,
                verifyLocalSourceAsync: run.TrackSourceVerification(_ =>
                {
                    sourceChecks++;
                    return Task.FromException(cleanupFailure);
                }),
                localRun: run,
                runProducerCommand: (request, _) => Task.FromResult(new EvidenceCommandResult(
                    0, request.FileName == "git" ? identity : "10.0.402", "", 1)));
            return null;
        }));

        Assert.Equal(1, sourceChecks);
        Assert.True(File.Exists(marker));
        var output = (await File.ReadAllTextAsync(marker)).TrimEnd('\r', '\n');
        Assert.True(File.Exists(TestPathUtils.PathUnder(output, "proof", "evidence-manifest.json")));
        var retained = TestPathUtils.PathUnder(_root, "TestResults", "repository-evidence", "local");
        var archived = ReadArchive(retained, "latest-failure.zip", "proof/evidence-manifest.json");
        Assert.NotNull(archived);
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(Encoding.UTF8.GetBytes(archived));
        Assert.True(manifest.Metrics.CleanupCompleted);
        Assert.Contains("Command cleanup could not be confirmed.", Assert.Single(manifest.ProducerResults).Diagnostic);
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
