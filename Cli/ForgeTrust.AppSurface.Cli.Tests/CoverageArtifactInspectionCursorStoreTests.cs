using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactInspectionCursorStoreTests
{
    [Fact]
    public void WriteAndRead_RoundTripsOrderedPathsAcrossScopes()
    {
        using var temp = TempDirectory.Create();
        using var store = CoverageArtifactStore.Open(Path.Join(temp.Path, "store"));
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-21T12:00:00Z"));
        const string firstScope = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string secondScope = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        string[] firstPaths = ["z-last", "a-first", ".", "z-last"];

        CoverageArtifactInspectionCursorStore.Write(store, firstScope, firstPaths, clock);
        CoverageArtifactInspectionCursorStore.Write(store, secondScope, ["pending/child", "pending"], clock);

        Assert.Equal(["z-last", "a-first", "."], CoverageArtifactInspectionCursorStore.Read(store, firstScope, clock));
        Assert.Equal(["pending/child", "pending"], CoverageArtifactInspectionCursorStore.Read(store, secondScope, clock));
    }

    [Fact]
    public void Read_WhenInspectionStateIsAbsent_DoesNotCreateEntries()
    {
        using var temp = TempDirectory.Create();
        using var store = CoverageArtifactStore.Open(Path.Join(temp.Path, "store"));
        using var before = CoverageRunOutputLease.AcquireArtifactDirectory(Path.Join(store.RootPath, "staging"), false, true)!;
        var names = before.EnumerateArtifactEntries(16).Select(entry => entry.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Null(CoverageArtifactInspectionCursorStore.Read(store, new string('a', 64), TimeProvider.System));

        Assert.Equal(names, before.EnumerateArtifactEntries(16).Select(entry => entry.Name).Order(StringComparer.Ordinal));
        Assert.False(Directory.Exists(Path.Join(store.RootPath, "staging", "inspection")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/../escape")]
    [InlineData("/rooted")]
    [InlineData("a\\b")]
    [InlineData("")]
    public void Write_RejectsInvalidPaths(string path)
    {
        using var temp = TempDirectory.Create();
        using var store = CoverageArtifactStore.Open(Path.Join(temp.Path, "store"));
        Assert.Throws<InvalidDataException>(() => CoverageArtifactInspectionCursorStore.Write(
            store, new string('a', 64), [path], TimeProvider.System));
    }

    [Fact]
    public void Write_RejectsExcessivePathLengthAndScopeCount()
    {
        using var temp = TempDirectory.Create();
        using var store = CoverageArtifactStore.Open(Path.Join(temp.Path, "store"));
        Assert.Throws<InvalidDataException>(() => CoverageArtifactInspectionCursorStore.Write(
            store, new string('a', 64), [new string('x', 4097)], TimeProvider.System));
        Assert.Throws<InvalidDataException>(() => CoverageArtifactInspectionCursorStore.Write(
            store, "bad-scope", [], TimeProvider.System));
    }

    [Fact]
    public void Read_UnknownSchemaPreservesStateAndThrowsInvalidData()
    {
        using var temp = TempDirectory.Create();
        using var store = CoverageArtifactStore.Open(Path.Join(temp.Path, "store"));
        const string scope = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        CoverageArtifactInspectionCursorStore.Write(store, scope, ["keep"], TimeProvider.System);
        var statePath = Path.Join(store.RootPath, "staging", "inspection", "state.json");
        const string unknown = "{\"schemaVersion\":2,\"scopes\":[]}";
        File.WriteAllText(statePath, unknown);

        Assert.Throws<InvalidDataException>(() => CoverageArtifactInspectionCursorStore.Read(store, scope, TimeProvider.System));
        Assert.Equal(unknown, File.ReadAllText(statePath));
    }

    [Fact]
    public void Write_CreatesPrivateInspectionDirectoryAndStateOnUnix()
    {
        using var temp = TempDirectory.Create();
        using var store = CoverageArtifactStore.Open(Path.Join(temp.Path, "store"));
        CoverageArtifactInspectionCursorStore.Write(store, new string('c', 64), ["pending"], TimeProvider.System);
        if (OperatingSystem.IsWindows()) return;

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.Join(store.RootPath, "staging", "inspection")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(Path.Join(store.RootPath, "staging", "inspection", "state.json")));
    }

    [Fact]
    public void Write_BoundsRecentScopesAndExpiresObservationsAfterSevenDays()
    {
        using var temp = TempDirectory.Create();
        using var store = CoverageArtifactStore.Open(Path.Join(temp.Path, "store"));
        var start = DateTimeOffset.Parse("2026-10-21T12:00:00Z");
        for (var index = 0; index < 34; index++)
            CoverageArtifactInspectionCursorStore.Write(store, index.ToString("x64"), ["TestResults"], new FixedTimeProvider(start.AddMinutes(index)));
        Assert.Null(CoverageArtifactInspectionCursorStore.Read(store, 0.ToString("x64"), new FixedTimeProvider(start.AddHours(1))));
        Assert.Equal(["TestResults"], CoverageArtifactInspectionCursorStore.Read(store, 33.ToString("x64"), new FixedTimeProvider(start.AddHours(1))));
        Assert.Null(CoverageArtifactInspectionCursorStore.Read(store, 33.ToString("x64"), new FixedTimeProvider(start.AddDays(8))));
        var statePath = Path.Join(store.RootPath, "staging", "inspection", "state.json");
        Assert.InRange(new FileInfo(statePath).Length, 1, 64 * 1024);
        Assert.True(store.GetStatus().MetadataBytes >= new FileInfo(statePath).Length);
    }

    [Fact]
    public void Write_DropsExpiredScopeWhenPublishingCurrentScope()
    {
        using var temp = TempDirectory.Create();
        using var store = CoverageArtifactStore.Open(Path.Join(temp.Path, "store"));
        var start = DateTimeOffset.Parse("2026-10-01T12:00:00Z");
        var initialClock = new FixedTimeProvider(start);
        var currentClock = new FixedTimeProvider(start.AddDays(8));
        var expiredScope = new string('a', 64);
        var currentScope = new string('b', 64);

        CoverageArtifactInspectionCursorStore.Write(store, expiredScope, ["old/pending"], initialClock);
        CoverageArtifactInspectionCursorStore.Write(store, currentScope, ["new/first", "new/second"], currentClock);

        var statePath = Path.Join(store.RootPath, "staging", "inspection", "state.json");
        using var state = System.Text.Json.JsonDocument.Parse(File.ReadAllText(statePath));
        Assert.DoesNotContain(state.RootElement.GetProperty("Scopes").EnumerateArray(), scope =>
            scope.GetProperty("ScopeId").GetString() == expiredScope);
        Assert.Null(CoverageArtifactInspectionCursorStore.Read(store, expiredScope, currentClock));
        Assert.Equal(["new/first", "new/second"],
            CoverageArtifactInspectionCursorStore.Read(store, currentScope, currentClock));
    }

    [Fact]
    public void Write_DropsFutureDatedScopeWhenPublishingCurrentScope()
    {
        using var temp = TempDirectory.Create();
        using var store = CoverageArtifactStore.Open(Path.Join(temp.Path, "store"));
        var now = DateTimeOffset.Parse("2026-10-01T12:00:00Z");
        var currentClock = new FixedTimeProvider(now);
        var futureClock = new FixedTimeProvider(now.AddDays(1));
        var futureScope = new string('c', 64);
        var currentScope = new string('d', 64);

        CoverageArtifactInspectionCursorStore.Write(store, futureScope, ["future/pending"], futureClock);
        CoverageArtifactInspectionCursorStore.Write(store, currentScope, ["current/first", "current/second"], currentClock);

        Assert.Null(CoverageArtifactInspectionCursorStore.Read(store, futureScope, currentClock));
        Assert.Equal(["current/first", "current/second"],
            CoverageArtifactInspectionCursorStore.Read(store, currentScope, currentClock));
    }

    [Fact]
    public void Write_BoundsEncodedCursorWithoutReorderingRetainedPaths()
    {
        using var temp = TempDirectory.Create();
        using var store = CoverageArtifactStore.Open(Path.Join(temp.Path, "store"));
        var paths = Enumerable.Range(0, 128).Select(index => index.ToString("d3") + new string('x', 1000)).ToArray();
        CoverageArtifactInspectionCursorStore.Write(store, new string('c', 64), paths, TimeProvider.System);
        var retained = CoverageArtifactInspectionCursorStore.Read(store, new string('c', 64), TimeProvider.System)!;
        Assert.InRange(retained.Count, 1, 127);
        Assert.Equal(paths.Take(retained.Count), retained);
        Assert.InRange(new FileInfo(Path.Join(store.RootPath, "staging", "inspection", "state.json")).Length, 1, 64 * 1024);
    }

    [Fact]
    public void NullStore_IsNoOp()
    {
        CoverageArtifactInspectionCursorStore.Write(null, new string('a', 64), ["."], TimeProvider.System);
        Assert.Null(CoverageArtifactInspectionCursorStore.Read(null, new string('a', 64), TimeProvider.System));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;
        internal string Path { get; }
        internal static TempDirectory Create()
        {
            var path = System.IO.Path.Join(System.IO.Path.GetTempPath(), "coverage-cursor-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
