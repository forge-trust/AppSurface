using System.Text;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactWarningsTests
{
    private const long Mebibyte = 1024L * 1024;

    [Fact]
    public async Task Emit_PersistsCooldownAcrossInstances_ExpiresAt24Hours_AndAllowsANewCondition()
    {
        using var fixture = new WarningFixture(repositoryWarningBytes: 1_000_000, domainWarningBytes: 1_000_000);
        var diagnostic = Diagnostic("persistent warning");
        using var firstErrors = new StringWriter();
        using (var firstStore = fixture.OpenStore())
        {
            var first = new CoverageArtifactWarnings(fixture.RepositoryPath, firstStore, fixture.Clock, firstErrors);
            await first.EmitAsync(diagnostic, "same-condition", 1024);
        }

        using var secondErrors = new StringWriter();
        using (var secondStore = fixture.OpenStore())
        {
            var second = new CoverageArtifactWarnings(fixture.RepositoryPath, secondStore, fixture.Clock, secondErrors);
            await second.EmitAsync(diagnostic, "same-condition", 1024);
            Assert.Empty(secondErrors.ToString());

            await second.EmitAsync(Diagnostic("new condition"), "new-condition", 1024);
            Assert.Equal(1, CountCode(secondErrors.ToString(), "ASCOV210"));

            fixture.Clock.Advance(TimeSpan.FromHours(24));
            await second.EmitAsync(diagnostic, "same-condition", 1024);
        }

        Assert.Equal(1, CountCode(firstErrors.ToString(), "ASCOV210"));
        Assert.Equal(2, CountCode(secondErrors.ToString(), "ASCOV210"));
        Assert.Contains("new condition", secondErrors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Emit_RequiresBoth25PercentAnd256MebibyteGrowthDuringCooldown()
    {
        using var fixture = new WarningFixture(repositoryWarningBytes: 1_000_000, domainWarningBytes: 1_000_000);
        using var store = fixture.OpenStore();
        using var errors = new StringWriter();
        var warnings = new CoverageArtifactWarnings(fixture.RepositoryPath, store, fixture.Clock, errors);

        await warnings.EmitAsync(Diagnostic("byte-threshold", 2_000 * Mebibyte), "byte-threshold", 2_000 * Mebibyte);
        await warnings.EmitAsync(Diagnostic("percent-threshold", 512 * Mebibyte), "percent-threshold", 512 * Mebibyte);
        fixture.Clock.Advance(TimeSpan.FromHours(1));

        // 256 MiB is enough absolute growth from 2,000 MiB, but less than 25%.
        await warnings.EmitAsync(Diagnostic("byte-threshold", 2_256 * Mebibyte), "byte-threshold", 2_256 * Mebibyte);
        // 25% is enough relative growth from 512 MiB, but less than 256 MiB.
        await warnings.EmitAsync(Diagnostic("percent-threshold", 640 * Mebibyte), "percent-threshold", 640 * Mebibyte);
        Assert.Equal(2, CountCode(errors.ToString(), "ASCOV210"));

        await warnings.EmitAsync(Diagnostic("byte-threshold", 2_500 * Mebibyte), "byte-threshold", 2_500 * Mebibyte);
        await warnings.EmitAsync(Diagnostic("percent-threshold", 768 * Mebibyte), "percent-threshold", 768 * Mebibyte);

        Assert.Equal(4, CountCode(errors.ToString(), "ASCOV210"));
        Assert.Contains("observedBytes=2621440000", errors.ToString(), StringComparison.Ordinal);
        Assert.Contains("observedBytes=805306368", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Emit_InvalidWarningStateIsPreserved_AndReportsStateAndOriginalWarnings()
    {
        using var fixture = new WarningFixture(repositoryWarningBytes: 1_000_000, domainWarningBytes: 1_000_000);
        using var store = fixture.OpenStore();
        var statePath = fixture.CreateInvalidWarningState(store);
        var originalState = File.ReadAllBytes(statePath);
        using var errors = new StringWriter();
        var warnings = new CoverageArtifactWarnings(fixture.RepositoryPath, store, fixture.Clock, errors);

        await warnings.EmitAsync(Diagnostic("original growth warning"), "growth", 2048);

        var output = errors.ToString();
        Assert.Contains("ASCOV201 warning-state:", output, StringComparison.Ordinal);
        Assert.Contains("ASCOV210 storage-growth: original growth warning", output, StringComparison.Ordinal);
        Assert.Equal(originalState, File.ReadAllBytes(statePath));
    }

    [Fact]
    public async Task ObserveAsync_SmallHealthyRepositoryIsQuiet_AndPrivatePolicyControlsDomainThreshold()
    {
        using var fixture = new WarningFixture(repositoryWarningBytes: 1024, domainWarningBytes: 1_000_000);
        Directory.CreateDirectory(Path.Join(fixture.RepositoryPath, "TestResults"));
        File.WriteAllText(Path.Join(fixture.RepositoryPath, "TestResults", "coverage.xml"), "small");
        using var store = fixture.OpenStore();
        using var errors = new StringWriter();
        var warnings = new CoverageArtifactWarnings(fixture.RepositoryPath, store, fixture.Clock, errors);

        await warnings.ObserveAsync(terminal: true, inventory: null, CancellationToken.None);

        Assert.Empty(errors.ToString());

        using var thresholdFixture = new WarningFixture(repositoryWarningBytes: 1_000_000, domainWarningBytes: 1);
        using var thresholdStore = thresholdFixture.OpenStore();
        using var thresholdErrors = new StringWriter();
        var thresholdWarnings = new CoverageArtifactWarnings(thresholdFixture.RepositoryPath, thresholdStore, thresholdFixture.Clock, thresholdErrors);
        await thresholdWarnings.ObserveAsync(terminal: true, inventory: null, CancellationToken.None);

        Assert.Contains("ASCOV210 storage-growth: Registered domain contains", thresholdErrors.ToString(), StringComparison.Ordinal);
        Assert.Contains("allowedBytes=1", thresholdErrors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObserveAsync_IncompleteInspectionReportsInventoryAsLowerBound()
    {
        using var fixture = new WarningFixture(repositoryWarningBytes: 1, domainWarningBytes: 1_000_000);
        var inventory = new CoverageArtifactInventoryEntry("coverage.xml", 512, new string('a', 64));
        using var store = fixture.OpenStore();
        using var errors = new StringWriter();
        var warnings = new CoverageArtifactWarnings(Path.Join(fixture.RepositoryPath, "missing"), store, fixture.Clock, errors);

        await warnings.ObserveAsync(terminal: true, inventory: [inventory], CancellationToken.None);

        var output = errors.ToString();
        Assert.Contains("ASCOV203 inspect:", output, StringComparison.Ordinal);
        Assert.Contains("at least 512 bytes in 1 files", output, StringComparison.Ordinal);
        Assert.Contains("ASCOV210 storage-growth:", output, StringComparison.Ordinal);
        Assert.Contains("observedBytes=512", output, StringComparison.Ordinal);
    }

    private static CoverageArtifactDiagnostic Diagnostic(string cause, long? observedBytes = null)
        => new("ASCOV210", "storage-growth", cause, "Inspect coverage storage status.", ObservedBytes: observedBytes);

    private static int CountCode(string text, string code)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(line => line.StartsWith(code + " ", StringComparison.Ordinal));

    private sealed class WarningFixture : IDisposable
    {
        private readonly TestDirectory _temp = TestDirectory.Create();
        private readonly long _repositoryWarningBytes;
        private readonly long _domainWarningBytes;

        internal WarningFixture(long repositoryWarningBytes, long domainWarningBytes)
        {
            _repositoryWarningBytes = repositoryWarningBytes;
            _domainWarningBytes = domainWarningBytes;
            StorePath = Path.Join(_temp.Path, "private-store");
            RepositoryPath = _temp.CreateDirectory("repository");
            Clock = new WarningClock(DateTimeOffset.Parse("2026-10-21T12:00:00Z"));
            using (CoverageArtifactStore.Open(StorePath, Clock)) { }
            WritePolicy();
        }

        internal string StorePath { get; }
        internal string RepositoryPath { get; }
        internal WarningClock Clock { get; }

        internal CoverageArtifactStore OpenStore() => CoverageArtifactStore.Open(StorePath, Clock);

        internal string CreateInvalidWarningState(CoverageArtifactStore store)
        {
            var stagingPath = Path.Join(store.RootPath, "staging");
            using var staging = CoverageRunOutputLease.AcquireArtifactDirectory(stagingPath, false, true)!;
            var warningPath = Path.Join(stagingPath, "warnings");
            using (staging.CreateArtifactDirectory("warnings")) { }
            using var warningState = CoverageRunOutputLease.AcquireArtifactDirectory(warningPath, false, true)!;
            using (var stream = warningState.OpenArtifactFile("state.json", createNew: true))
            {
                stream.Write("{invalid warning state"u8);
                stream.Flush(true);
            }

            return Path.Join(warningPath, "state.json");
        }

        private void WritePolicy()
        {
            var path = Path.Join(StorePath, "policy.json");
            var json = $"{{\"schemaVersion\":1,\"repositoryWarningBytes\":{_repositoryWarningBytes},\"domainWarningBytes\":{_domainWarningBytes},\"warningCooldownHours\":24}}";
            TestFileSystem.WritePrivateFile(path, json);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        public void Dispose() => _temp.Dispose();
    }

    private sealed class WarningClock(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtc;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        internal void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
