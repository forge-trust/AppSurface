using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceOrdinaryDeliveryContractTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task IncompleteExecutionCannotCreateTheDeliveryRoot(bool successful, bool hasGate)
    {
        using var fixture = await Fixture.CreateAsync();
        var execution = new CoverageEvidenceExecutionResult(
            new CoverageRunResult(successful, fixture.Source, fixture.Coverage), hasGate ? fixture.Execution.Gate : null);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.DeliverAsync(execution));

        Assert.False(Directory.Exists(fixture.Destination));
        await fixture.AssertSourcesUnchangedAsync();
    }

    [Theory]
    [InlineData("negative-length")]
    [InlineData("short-digest")]
    [InlineData("uppercase-digest")]
    [InlineData("nonhex-digest")]
    [InlineData("duplicate")]
    [InlineData("case-collision")]
    [InlineData("missing-report")]
    public async Task InvalidClosedPublicationCannotCreateTheDeliveryRoot(string defect)
    {
        using var fixture = await Fixture.CreateAsync();
        var publications = fixture.Publications.ToList();
        publications[0] = defect switch
        {
            "negative-length" => publications[0] with { Length = -1 },
            "short-digest" => publications[0] with { Sha256 = "abc" },
            "uppercase-digest" => publications[0] with { Sha256 = new string('A', 64) },
            "nonhex-digest" => publications[0] with { Sha256 = new string('g', 64) },
            _ => publications[0]
        };
        if (defect == "duplicate") publications.Add(publications[0]);
        if (defect == "case-collision") publications.Add(publications.Single(file => file.Path == "coverage-gate.json")
            with
        { Path = "COVERAGE-GATE.JSON" });
        if (defect == "missing-report") publications.RemoveAll(file => file.Path == "summary.txt");

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.DeliverAsync(publications: publications));

        Assert.False(Directory.Exists(fixture.Destination));
        await fixture.AssertSourcesUnchangedAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceMutationRefusesDeliveryAndPreservesTheChangedSource(bool sameLength)
    {
        using var fixture = await Fixture.CreateAsync();
        var first = fixture.Publications[0];
        var changed = sameLength ? Enumerable.Repeat((byte)'x', checked((int)first.Length)).ToArray() : "longer changed bytes"u8.ToArray();
        await File.WriteAllBytesAsync(TestPathUtils.PathUnder(fixture.Source, "coverage", first.Path), changed);

        await Assert.ThrowsAsync<IOException>(() => fixture.DeliverAsync());

        Assert.Equal(changed, await File.ReadAllBytesAsync(TestPathUtils.PathUnder(fixture.Source, "coverage", first.Path)));
        Assert.Equal("caller-owned sentinel", await File.ReadAllTextAsync(fixture.Sentinel));
    }

    [Fact]
    public async Task IdenticalDeliveryIsIdempotentAndNeverOverwritesDifferingDestinationBytes()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.DeliverAsync();
        foreach (var file in fixture.Publications)
            File.SetLastWriteTimeUtc(TestPathUtils.PathUnder(fixture.Destination, "coverage", file.Path),
                new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        var originalTimes = fixture.Publications.ToDictionary(file => file.Path,
            file => File.GetLastWriteTimeUtc(TestPathUtils.PathUnder(fixture.Destination, "coverage", file.Path)));

        await fixture.DeliverAsync();

        foreach (var file in fixture.Publications)
        {
            var destination = TestPathUtils.PathUnder(fixture.Destination, "coverage", file.Path);
            Assert.Equal(originalTimes[file.Path], File.GetLastWriteTimeUtc(destination));
            Assert.Equal(fixture.Bytes[file.Path], await File.ReadAllBytesAsync(destination));
        }
        var occupied = TestPathUtils.PathUnder(fixture.Destination, "coverage", fixture.Publications[0].Path);
        var foreign = Enumerable.Repeat((byte)'z', checked((int)fixture.Publications[0].Length)).ToArray();
        await File.WriteAllBytesAsync(occupied, foreign);

        await Assert.ThrowsAsync<IOException>(() => fixture.DeliverAsync());

        Assert.Equal(foreign, await File.ReadAllBytesAsync(occupied));
        await fixture.AssertSourcesUnchangedAsync();
    }

    [Fact]
    public async Task CancellationAfterCopyDoesNotOccupyCanonicalReportPathsAndCanBeRetried()
    {
        using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CoverageEvidenceProducer.PreserveOrdinaryReportsAsync(fixture.Source, fixture.Destination,
                fixture.Execution, fixture.Publications, cancellation.Token, cancellation.Cancel));

        Assert.Empty(Directory.EnumerateFiles(fixture.Destination, "*", SearchOption.AllDirectories));
        await fixture.AssertSourcesUnchangedAsync();
        await fixture.DeliverAsync();
        foreach (var file in fixture.Publications)
            Assert.Equal(fixture.Bytes[file.Path], await File.ReadAllBytesAsync(
                TestPathUtils.PathUnder(fixture.Destination, "coverage", file.Path)));
        Assert.Empty(Directory.EnumerateFiles(fixture.Destination, ".report-*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DestinationClaimedDuringCopyIsPreservedAndOnlyOwnedStagingIsRemoved()
    {
        using var fixture = await Fixture.CreateAsync();
        var firstPath = TestPathUtils.PathUnder(fixture.Destination, "coverage", fixture.Publications[0].Path);
        const string foreign = "another writer owns this destination";

        await Assert.ThrowsAsync<IOException>(() =>
            CoverageEvidenceProducer.PreserveOrdinaryReportsAsync(fixture.Source, fixture.Destination,
                fixture.Execution, fixture.Publications, CancellationToken.None,
                () => File.WriteAllText(firstPath, foreign)));

        Assert.Equal(foreign, await File.ReadAllTextAsync(firstPath));
        Assert.Equal(firstPath, Assert.Single(Directory.EnumerateFiles(fixture.Destination, "*", SearchOption.AllDirectories)));
        await fixture.AssertSourcesUnchangedAsync();
    }

    [Fact]
    public async Task CancelledDeliveryDoesNotCreateDestinationOrChangeAnySourceBytes()
    {
        using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.DeliverAsync(token: cancellation.Token));

        Assert.False(Directory.Exists(fixture.Destination));
        await fixture.AssertSourcesUnchangedAsync();
    }

    private sealed class Fixture(TestDirectory directory, IReadOnlyDictionary<string, byte[]> bytes,
        IReadOnlyList<CoverageArtifactInventoryEntry> publications, CoverageEvidenceExecutionResult execution) : IDisposable
    {
        internal string Source { get; } = TestPathUtils.PathUnder(directory.Path, "execution");
        internal string Destination { get; } = TestPathUtils.PathUnder(directory.Path, "delivered");
        internal string Sentinel { get; } = TestPathUtils.PathUnder(directory.Path, "sentinel.txt");
        internal string Coverage => TestPathUtils.PathUnder(Source, "coverage", "coverage.cobertura.xml");
        internal IReadOnlyDictionary<string, byte[]> Bytes { get; } = bytes;
        internal IReadOnlyList<CoverageArtifactInventoryEntry> Publications { get; } = publications;
        internal CoverageEvidenceExecutionResult Execution { get; } = execution;

        internal static async Task<Fixture> CreateAsync()
        {
            var directory = TestDirectory.Create();
            try
            {
                var source = TestPathUtils.PathUnder(directory.Path, "execution");
                var bytes = new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    ["coverage-gate.json"] = "gate json"u8.ToArray(),
                    ["coverage-gate.md"] = "gate markdown"u8.ToArray(),
                    ["coverage.cobertura.xml"] = "<coverage />"u8.ToArray(),
                    ["timings.json"] = Encoding.UTF8.GetBytes("""{"projects":[{"slug":"tests","testResults":[{"path":"projects/tests/test-results.xml"}]}]}"""),
                    ["summary.txt"] = "summary"u8.ToArray(),
                    ["projects/tests/coverage-project.json"] = Encoding.UTF8.GetBytes("""{"schemaVersion":1,"slug":"tests"}"""),
                    ["projects/tests/coverage.cobertura.xml"] = "<coverage />"u8.ToArray(),
                    ["projects/tests/test-results.xml"] = "<testsuites />"u8.ToArray()
                };
                foreach (var (path, content) in bytes)
                {
                    var destination = TestPathUtils.PathUnder(source, "coverage", path);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await File.WriteAllBytesAsync(destination, content);
                }
                await File.WriteAllTextAsync(TestPathUtils.PathUnder(directory.Path, "sentinel.txt"), "caller-owned sentinel");
                var coverage = TestPathUtils.PathUnder(source, "coverage", "coverage.cobertura.xml");
                var gate = new CoverageGateResult(coverage, new(1, 1, 100), new(1, 1, 100), 95, 85, true,
                    TestPathUtils.PathUnder(source, "coverage", "coverage-gate.json"),
                    TestPathUtils.PathUnder(source, "coverage", "coverage-gate.md"));
                return new(directory, bytes, bytes.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new CoverageArtifactInventoryEntry(pair.Key,
                    pair.Value.Length, Convert.ToHexStringLower(SHA256.HashData(pair.Value)))).ToArray(),
                    new(new CoverageRunResult(true, source, coverage), gate));
            }
            catch { directory.Dispose(); throw; }
        }

        internal Task DeliverAsync(CoverageEvidenceExecutionResult? execution = null,
            IReadOnlyList<CoverageArtifactInventoryEntry>? publications = null, CancellationToken token = default)
            => CoverageEvidenceProducer.PreserveOrdinaryReportsAsync(Source, Destination,
                execution ?? Execution, publications ?? Publications, token);

        internal async Task AssertSourcesUnchangedAsync()
        {
            foreach (var (path, content) in Bytes)
                Assert.Equal(content, await File.ReadAllBytesAsync(TestPathUtils.PathUnder(Source, "coverage", path)));
            Assert.Equal("caller-owned sentinel", await File.ReadAllTextAsync(Sentinel));
        }

        public void Dispose() => directory.Dispose();
    }
}
