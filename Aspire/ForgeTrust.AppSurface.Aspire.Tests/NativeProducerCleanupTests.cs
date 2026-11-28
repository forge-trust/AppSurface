using System.IO.Compression;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Validation;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class NativeProducerCleanupTests
{
    [Fact]
    public async Task WorkspaceCleanup_StaysUnconfirmedAcrossLaterCommandFailures()
    {
        var parent = Directory.CreateTempSubdirectory("native-producer-settlement-").FullName;
        try
        {
            var workspace = NativeProducerWorkspace.Create(parent, new(MinimumFreeBytes: 0));
            var failure = new EvidenceCommandException(EvidenceCommandException.CleanupFailedCode, "unconfirmed command cleanup");
            workspace.RecordCommandFailure(failure);
            workspace.RecordCommandFailure(failure);
            workspace.RecordCommandFailure(new EvidenceCommandException("start_failed", "a later command did not start"));

            var cleanup = await Assert.ThrowsAsync<EvidenceCommandException>(() => workspace.DisposeAsync().AsTask());
            Assert.True(cleanup.CleanupUnconfirmed);
            Assert.True(Directory.Exists(workspace.RootPath));
        }
        finally
        {
            NativeEvidenceTestDirectory.Delete(parent);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstalledConsumer_OptionalRevisionProbeIsMissingOnlyAfterConfirmedCleanup(bool cleanupUnconfirmed)
    {
        var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var parent = Directory.CreateTempSubdirectory("native-consumer-probe-cleanup-").FullName;
        var scratch = TestPathUtils.PathUnder(parent, "scratch");
        Directory.CreateDirectory(scratch);

        try
        {
            var calls = 0;
            Task<EvidenceCommandResult> Run(EvidenceCommandRequest request, CancellationToken _)
            {
                calls++;
                if (request.Arguments.SequenceEqual(["rev-parse", "HEAD"]))
                {
                    throw new EvidenceCommandException(
                        EvidenceCommandException.CleanupFailedCode,
                        "Synthetic Git probe failure.",
                        cleanupUnconfirmed);
                }

                return Task.FromResult(new EvidenceCommandResult(0, "", "", 0));
            }

            var settings = new ValidationSettings(
                repository,
                TestPathUtils.PathUnder(parent, "missing.nupkg"),
                "1.2.3",
                "expected-commit",
                "/missing/dotnet",
                scratch,
                TestPathUtils.PathUnder(parent, "evidence"),
                MinimumFreeBytes: 0);
            var producer = new InstalledConsumerProducer(settings, Run);
            var result = await producer.ProduceAsync(Context(producer.Id), CancellationToken.None);

            Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
            Assert.Empty(result.SatisfiedAssertionIds);
            var workspace = Assert.Single(Directory.EnumerateDirectories(scratch, "evidence-workspace-*"));

            if (cleanupUnconfirmed)
            {
                Assert.Equal(2, calls);
                Assert.Contains(EvidenceCommandException.CleanupFailedCode, result.Diagnostic, StringComparison.Ordinal);
                var disposal = await Assert.ThrowsAsync<EvidenceCommandException>(() => producer.DisposeAsync().AsTask());
                Assert.Equal(EvidenceCommandException.CleanupFailedCode, disposal.Code);
                Assert.True(Directory.Exists(workspace));
            }
            else
            {
                Assert.True(calls >= 2);
                Assert.Contains("at package", result.Diagnostic, StringComparison.Ordinal);
                Assert.DoesNotContain(EvidenceCommandException.CleanupFailedCode, result.Diagnostic, StringComparison.Ordinal);
                await producer.DisposeAsync();
                Assert.False(Directory.Exists(workspace));
            }
        }
        finally
        {
            NativeEvidenceTestDirectory.Delete(parent);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PackedStorage_RetainsOwnedWorkspaceOnlyWhenCommandCleanupIsUnconfirmed(bool cleanupUnconfirmed)
    {
        var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var parent = Directory.CreateTempSubdirectory("native-packed-command-cleanup-").FullName;
        var scratch = TestPathUtils.PathUnder(parent, "scratch");
        Directory.CreateDirectory(scratch);
        var packagePath = TestPathUtils.PathUnder(parent, "candidate.nupkg");
        CreateCandidatePackage(packagePath);

        try
        {
            var calls = 0;
            Task<EvidenceCommandResult> FailCommand(EvidenceCommandRequest _, CancellationToken __)
            {
                calls++;
                throw new EvidenceCommandException(
                    EvidenceCommandException.CleanupFailedCode,
                    "Synthetic packed command failure.",
                    cleanupUnconfirmed);
            }

            var settings = new ValidationSettings(
                repository,
                packagePath,
                "1.2.3-cleanup-test",
                "cleanup-test-commit",
                "/missing/dotnet",
                scratch,
                TestPathUtils.PathUnder(parent, "evidence"),
                MinimumFreeBytes: 0);
            var producer = new PackedStorageProducer(settings, FailCommand);
            var result = await producer.ProduceAsync(Context(producer.Id), CancellationToken.None);

            Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
            Assert.Empty(result.SatisfiedAssertionIds);
            Assert.Equal(1, calls);

            if (cleanupUnconfirmed)
            {
                Assert.Contains(EvidenceCommandException.CleanupFailedCode, result.Diagnostic, StringComparison.Ordinal);
                var workspace = Assert.Single(Directory.EnumerateDirectories(scratch, "evidence-workspace-*"));
                var disposal = await Assert.ThrowsAsync<EvidenceCommandException>(() => producer.DisposeAsync().AsTask());
                Assert.Equal(EvidenceCommandException.CleanupFailedCode, disposal.Code);
                Assert.True(Directory.Exists(workspace));
            }
            else
            {
                Assert.Empty(Directory.EnumerateDirectories(scratch, "evidence-workspace-*"));
                await producer.DisposeAsync();
            }
        }
        finally
        {
            NativeEvidenceTestDirectory.Delete(parent);
        }
    }

    private static EvidenceProducerContext Context(string producerId)
    {
        var declaration = new EvidenceProducerDeclaration(producerId, "native-validation", "1", [], [], [], 900);
        var plan = new EvidencePlan(
            "1",
            "native-cleanup",
            "native-cleanup-digest",
            "diff-digest",
            new EvidenceProfile("native-cleanup", EvidenceProfileScope.Targeted, [], [declaration], []),
            [],
            [],
            "input-digest");
        return new EvidenceProducerContext(plan, declaration, TimeProvider.System);
    }

    private static void CreateCandidatePackage(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("candidate.nuspec").Open()))
        {
            writer.Write("<package><metadata><id>ForgeTrust.AppSurface.Cli</id><version>1.2.3-cleanup-test</version><repository commit='cleanup-test-commit' /></metadata></package>");
        }

        using var assembly = archive.CreateEntry("tools/net10.0/any/ForgeTrust.AppSurface.Cli.dll").Open();
        assembly.WriteByte(0);
    }
}
