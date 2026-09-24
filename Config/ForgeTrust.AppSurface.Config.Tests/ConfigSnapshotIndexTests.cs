using System.Diagnostics;
using ForgeTrust.AppSurface.Core;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ConfigSnapshotIndexTests
{
    [Fact]
    public void PrefixSearchRetainsEveryCaseCollisionAtTheLowerBoundary()
    {
        var environment = new SnapshotProvider(new Dictionary<string, string>
        {
            ["B__"] = "one",
            ["b__"] = "two",
            ["B__CHILD"] = "three",
            ["A"] = "before",
            ["C"] = "after"
        });
        using var scope = new ConfigResolutionScope();
        var snapshot = scope.GetEnvironmentSnapshot(environment, new ConfigResourceOptions());
        Assert.Equal(new[] { "B__", "b__", "B__CHILD" }, snapshot.GetDescendantNames("b__"));
        Assert.Equal(new[] { "A", "B__", "b__", "B__CHILD", "C" }, snapshot.GetDescendantNames(""));
        Assert.Empty(snapshot.GetDescendantNames("Z__"));
        Assert.Equal(new[] { "B__", "b__" }, snapshot.GetMatchingNames("b__"));
    }

    [Fact]
    public async Task ConcurrentFirstAccessCapturesOnceAndOwnsItsSnapshot()
    {
        var values = new Dictionary<string, string> { ["A"] = "original" };
        var environment = new SnapshotProvider(values);
        using var scope = new ConfigResolutionScope();
        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
            scope.GetEnvironmentSnapshot(environment, new ConfigResourceOptions()))));
        Assert.Equal(1, environment.Captures);
        Assert.All(results, result => Assert.Same(results[0], result));
        values["A"] = "changed";
        Assert.Equal("original", results[0].Entries["A"]);
        using var next = new ConfigResolutionScope();
        Assert.Equal("changed", next.GetEnvironmentSnapshot(environment, new ConfigResourceOptions()).Entries["A"]);
    }

    [Fact]
    public void CaptureFailureIsRetainedOnlyForTheOperation()
    {
        var environment = new SnapshotProvider(new Dictionary<string, string> { ["A"] = "one", ["B"] = "two" });
        var limits = new ConfigResourceOptions { MaxEnvironmentEntries = 1 };
        using var scope = new ConfigResolutionScope();
        var first = Assert.Throws<ConfigResourceLimitException>(() => scope.GetEnvironmentSnapshot(environment, limits));
        var second = Assert.Throws<ConfigResourceLimitException>(() => scope.GetEnvironmentSnapshot(environment, limits));
        Assert.Same(first, second);
        Assert.Equal("config-environment-entry-limit", first.Code);
        Assert.Equal(1, environment.Captures);
        limits.MaxEnvironmentEntries = 2;
        using var next = new ConfigResolutionScope();
        Assert.Equal(2, next.GetEnvironmentSnapshot(environment, limits).Entries.Count);
        Assert.Equal(2, environment.Captures);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(10000)]
    public void PrefixLookupAllocationDependsOnMatchesNotUnrelatedEntries(int count)
    {
        var values = Enumerable.Range(0, count).ToDictionary(index => $"UNRELATED__{index:D6}", _ => "marker");
        values.Add("TARGET__1000000000", "marker");
        using var scope = new ConfigResolutionScope();
        var snapshot = scope.GetEnvironmentSnapshot(new SnapshotProvider(values), new ConfigResourceOptions());
        Assert.Single(snapshot.GetDescendantNames("TARGET__"));
        var totalMatches = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        for (var iteration = 0; iteration < 1000; iteration++)
        {
            foreach (var name in snapshot.GetDescendantNames("TARGET__"))
            {
                totalMatches++;
            }
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(1000, totalMatches);
        Assert.True(allocated < 512_000, $"Prefix lookup allocated {allocated} bytes for {count} source entries.");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "Indexed prefix lookup exceeded its generous regression budget.");
    }

    private sealed class SnapshotProvider(IReadOnlyDictionary<string, string> values) : IEnvironmentProvider
    {
        private int _captures;
        internal int Captures => Volatile.Read(ref _captures);
        public string Environment => "Production";
        public bool IsDevelopment => false;
        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => throw new InvalidOperationException("Point reads are not used.");
        public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables()
        {
            Interlocked.Increment(ref _captures);
            return values;
        }
    }
}
