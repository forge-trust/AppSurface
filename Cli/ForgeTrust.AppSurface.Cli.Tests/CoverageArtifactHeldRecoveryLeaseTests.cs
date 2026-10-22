using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageArtifactHeldRecoveryLeaseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaintenanceReusesVerifiedExclusiveSourceWithoutReleasingIt(bool published)
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        await fixture.PrepareAsync(store, published);
        using var source = CoverageArtifactOperationLease.Acquire(fixture.Output, true);
        Assert.True(source.IsExclusive);
        Assert.Empty(await store.MaintainAsync(true, default, heldSourceLease: source));
        Assert.Equal(0, store.GetStatus().StagingReservedBytes);
        Assert.Equal(published ? 1 : 0, store.GetStatus().ArchivedRuns);
        source.VerifyBinding();
        Assert.Throws<CoverageExecutionException>(() => CoverageArtifactOperationLease.Acquire(fixture.Output, false));
        Assert.Equal("original", File.ReadAllText(Path.Join(fixture.Output, "coverage.xml")));
    }

    [Fact]
    public async Task SharedAuthorityCannotBeReusedForRecovery()
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        await fixture.PrepareAsync(store, false);
        var before = File.ReadAllBytes(fixture.RegistryPath);
        using var source = CoverageArtifactOperationLease.Acquire(fixture.Output, false);
        Assert.False(source.IsExclusive);
        await Assert.ThrowsAsync<ArgumentException>(() => store.MaintainAsync(true, default, heldSourceLease: source));
        Assert.Equal(before, File.ReadAllBytes(fixture.RegistryPath));
        source.VerifyBinding();
    }

    [Fact]
    public async Task DifferentHeldOutputDoesNotAuthorizeBusySourceRecovery()
    {
        using var fixture = new Fixture();
        using var store = fixture.Open();
        await fixture.PrepareAsync(store, false);
        var before = File.ReadAllBytes(fixture.RegistryPath);
        using var source = CoverageArtifactOperationLease.Acquire(fixture.Output, true);
        var other = fixture.Temp.CreateDirectory("other");
        using var otherLease = CoverageArtifactOperationLease.Acquire(other, true);
        Assert.Empty(await store.MaintainAsync(true, default, heldSourceLease: otherLease));
        Assert.Equal(before, File.ReadAllBytes(fixture.RegistryPath));
        source.VerifyBinding();
        otherLease.VerifyBinding();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalSessionRecoversInterruptedPriorPublicationBeforeReplacement(bool published)
    {
        using var fixture = new Fixture();
        using (var store = fixture.Open()) await fixture.PrepareAsync(store, published);
        var prior = Environment.GetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME");
        Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", fixture.Domain);
        try
        {
            using var errors = new StringWriter();
            using var session = await CoverageArtifactSession.BeginAsync(fixture.Output, fixture.Repository, true,
                CoverageTextWriters.Create(TextWriter.Null, errors), TimeProvider.System, default);
            using var store = fixture.Open();
            Assert.Equal(0, store.GetStatus().StagingReservedBytes);
            Assert.Equal(1, store.GetStatus().ArchivedRuns);
            Assert.DoesNotContain("ASCOV200 snapshot", errors.ToString(), StringComparison.Ordinal);
            Assert.Equal("original", File.ReadAllText(Path.Join(fixture.Output, "coverage.xml")));
            Assert.Throws<CoverageExecutionException>(() => CoverageArtifactOperationLease.Acquire(fixture.Output, false));
        }
        finally { Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", prior); }
    }

    [Fact]
    public async Task SessionRejectsSharedOutputAuthorityBeforePublishing()
    {
        using var temp = TestDirectory.Create();
        var repository = temp.CreateDirectory("repository");
        var output = temp.CreateDirectory("repository/output");
        using var shared = CoverageArtifactOperationLease.Acquire(output, false);
        await Assert.ThrowsAsync<ArgumentException>(() => CoverageArtifactSession.BeginAsync(output, repository, false,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), TimeProvider.System, default, shared));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output).Where(path => !path.EndsWith(".appsurface-coverage-operation.lock", StringComparison.Ordinal)));
        using var replacement = CoverageArtifactOperationLease.Acquire(output, true);
        replacement.VerifyBinding();
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly TestDirectory Temp = TestDirectory.Create();
        internal string Output => Path.Join(Temp.Path, "repository", "output");
        internal string Repository => Path.Join(Temp.Path, "repository");
        internal string Domain => Path.Join(Temp.Path, "domain");
        internal string RegistryPath => Path.Join(Domain, "registry.json");
        internal CoverageArtifactStore Open() => CoverageArtifactStore.Open(Domain);
        internal async Task PrepareAsync(CoverageArtifactStore store, bool published)
        {
            Directory.CreateDirectory(Output);
            File.WriteAllText(Path.Join(Output, "coverage.xml"), "original");
            var priorHome = Environment.GetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME");
            Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", Domain);
            try
            {
                using var session = await CoverageArtifactSession.BeginAsync(Output, Repository, false,
                    CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), TimeProvider.System, default);
                session.Inventory.RecordFile(Path.Join(Output, "coverage.xml"));
                await session.CompleteAsync("success", new("revision", "clean", "configuration"));
            }
            finally { Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", priorHome); }
            // Register against this fixture's isolated domain with matching sidecar provenance.
            var sidecarPath = Path.Join(Output, CoverageArtifactSession.SidecarName);
            var sidecar = JsonNode.Parse(File.ReadAllBytes(sidecarPath))!;
            var manifest = System.Text.Json.JsonSerializer.Deserialize<CoverageArtifactRunManifest>(sidecar["manifestBase"]!.ToJsonString(),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            var run = new CoverageArtifactCurrentRun(manifest,
                [new("coverage.xml", 8, Convert.ToHexString(SHA256.HashData("original"u8)).ToLowerInvariant()),
                 new(CoverageArtifactSession.SidecarName, new FileInfo(sidecarPath).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sidecarPath))).ToLowerInvariant())],
                Output, Repository);
            await store.RegisterAsync(run, default);
            if (published) await store.SnapshotAsync(run, false, false, default);
            var registry = JsonNode.Parse(File.ReadAllBytes(RegistryPath))!;
            var registration = registry["Registrations"]![0]!;
            registration["Archived"] = false;
            registration["ArchiveBytes"] = 0;
            registration["ArchivedAtUtc"] = null;
            registry["Reservations"]!.AsArray().Add(new JsonObject
            {
                ["Id"] = Guid.NewGuid().ToString("N"),
                ["RunId"] = run.ManifestBase.RunId,
                ["Bytes"] = CoverageArtifactLimits.DomainStagingMaxBytes,
                ["StartedAtUtc"] = DateTimeOffset.UtcNow,
                ["Pin"] = false
            });
            File.WriteAllText(RegistryPath, registry.ToJsonString());
        }
        public void Dispose() => Temp.Dispose();
    }
}
