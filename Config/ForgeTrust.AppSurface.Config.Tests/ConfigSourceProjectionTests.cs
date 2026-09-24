namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ConfigSourceProjectionTests
{
    [Fact]
    public void Unique_PreservesNativeIdentityAndMetadata()
    {
        var entry = Entry("Payments:ApiKey", "base", "Exact/Native-ID", 3);
        var projection = new ConfigSourceProjection<int>([entry], ["base"]);
        Assert.True(projection.TryGet(AppSurfaceConfigKey.Parse("payments:apikey"), out var result));
        Assert.Equal(ConfigSourceProjectionStatus.Unique, result!.Status);
        Assert.Same(entry, result.Winner);
        Assert.Equal(3, result.Winner!.Metadata);
        Assert.Equal("Exact/Native-ID", result.Winner.NativeIdentifier);
        Assert.False(result.IsTerminal);
        Assert.False(projection.TryGet(AppSurfaceConfigKey.Parse("missing"), out var missing));
        Assert.Null(missing);
    }

    [Fact]
    public void ExactDuplicate_InOneLayerIsTerminalEvenWhenValuesMatch()
    {
        var entry = Entry("Payments:ApiKey", "base", "native", 3);
        var projection = new ConfigSourceProjection<int>([entry, entry], ["base"]);
        var result = Assert.Single(projection.Entries);
        Assert.Equal(ConfigSourceProjectionStatus.Duplicate, result.Status);
        Assert.Null(result.Winner);
        Assert.True(result.IsTerminal);
        Assert.Equal(2, result.Entries.Count);
    }

    [Theory]
    [InlineData("base")]
    [InlineData("override")]
    public void CaseOnlySpelling_IsTerminalWithinAndAcrossLayers(string layer)
    {
        var projection = new ConfigSourceProjection<int>(
            [Entry("Payments:ApiKey", "base", "native", 1), Entry("payments:apikey", layer, "native", 1)],
            ["base", "override"]);
        var result = Assert.Single(projection.Entries);
        Assert.Equal(ConfigSourceProjectionStatus.Collision, result.Status);
        Assert.True(result.IsTerminal);
        Assert.Null(result.Winner);
    }

    [Fact]
    public void Aliases_AreOneCollisionDomain()
    {
        var projection = new ConfigSourceProjection<int>(
            [Entry("A:B", "base", "A__B", 1), Entry("A:B", "base", "A_B", 1) with { IsLegacyAlias = true }], ["base"]);
        var result = Assert.Single(projection.Entries);
        Assert.Equal(ConfigSourceProjectionStatus.Collision, result.Status);
        Assert.True(result.Entries[1].IsLegacyAlias);
    }

    [Fact]
    public void ExactOverride_UsesDeclaredLayerOrderAndRetainsOrigins()
    {
        var lower = Entry("A:B", "base", "first.json", 1);
        var upper = Entry("A:B", "override", "last.json", 2);
        var projection = new ConfigSourceProjection<int>([upper, lower], ["base", "override"]);
        var result = Assert.Single(projection.Entries);
        Assert.Equal(ConfigSourceProjectionStatus.IntentionalOverride, result.Status);
        Assert.Equal(new[] { lower, upper }, result.Entries);
        Assert.Same(upper, result.Winner);
    }

    [Fact]
    public void NativeCollision_RejectsDistinctLogicalKeysButAllowsOneIdentityAcrossLayers()
    {
        var projection = new ConfigSourceProjection<int>(
            [Entry("A:B", "base", "ID", 1), Entry("C:D", "base", "id", 2), Entry("Safe", "base", "safe", 3)],
            ["base"], StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, projection.Entries.Count(entry => entry.Status == ConfigSourceProjectionStatus.Unrepresentable));
        Assert.Single(projection.Entries, entry => !entry.IsTerminal);
        projection = new ConfigSourceProjection<int>(
            [Entry("A:B", "base", "ID", 1), Entry("C:D", "base", "id", 2)], ["base"], StringComparer.Ordinal);
        Assert.All(projection.Entries, entry => Assert.Equal(ConfigSourceProjectionStatus.Unique, entry.Status));
        projection = new ConfigSourceProjection<int>(
            [Entry("A:B", "base", "ID", 1), Entry("A:B", "upper", "ID", 2)], ["base", "upper"], StringComparer.Ordinal);
        Assert.Equal(ConfigSourceProjectionStatus.IntentionalOverride, Assert.Single(projection.Entries).Status);
    }

    [Fact]
    public void Projection_RejectsUndeclaredLayersAndSnapshotsInput()
    {
        Assert.Throws<ArgumentNullException>(() => new ConfigSourceProjection<int>(null!, ["base"]));
        Assert.Throws<ArgumentNullException>(() => new ConfigSourceProjection<int>([], null!));
        Assert.Throws<ArgumentException>(() => new ConfigSourceProjection<int>([Entry("A", "undeclared", "id", 0)], ["base"]));
        Assert.Throws<ArgumentException>(() => new ConfigSourceProjection<int>([], ["base", "base"]));
        var raw = new[] { Entry("z", "base", "z", 0), Entry("A", "base", "a", 1) };
        var projection = new ConfigSourceProjection<int>(raw, ["base"]);
        raw[0] = Entry("changed", "base", "changed", 0);
        Assert.Equal(new[] { "A", "z" }, projection.Entries.Select(entry => entry.Key.Value));
    }

    [Fact]
    public async Task ConcurrentReadsRetainEveryOriginAndNeverSelectACollisionWinner()
    {
        var projection = new ConfigSourceProjection<int>(
            [Entry("A:B", "base", "base-id", 1), Entry("A:B", "upper", "upper-id", 2),
                Entry("C", "base", "one", 3), Entry("c", "base", "two", 4)], ["base", "upper"]);
        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            Assert.True(projection.TryGet(AppSurfaceConfigKey.Parse("a:b"), out var resolved));
            Assert.True(projection.TryGet(AppSurfaceConfigKey.Parse("C"), out var collision));
            Assert.True(collision!.IsTerminal);
            Assert.Null(collision.Winner);
            return resolved!;
        })));
        Assert.All(results, result =>
        {
            Assert.Same(results[0], result);
            Assert.Equal(2, result.Entries.Count);
            Assert.Equal("upper-id", result.Winner!.NativeIdentifier);
        });
    }

    [Fact]
    public void WarmLookupAllocationDoesNotGrowWithSourceCount()
    {
        var allocations = new List<long>();
        foreach (var count in new[] { 100, 1000, 10000 })
        {
            var entries = Enumerable.Range(0, count).Select(index => Entry($"Key:{index}", "base", $"id-{index}", index));
            var projection = new ConfigSourceProjection<int>(entries, ["base"]);
            var key = AppSurfaceConfigKey.Parse("KEY:0");
            Assert.True(projection.TryGet(key, out _));
            var before = GC.GetAllocatedBytesForCurrentThread();
            var found = 0;
            for (var iteration = 0; iteration < 1000; iteration++)
            {
                if (projection.TryGet(key, out _)) { found++; }
            }
            allocations.Add(GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.Equal(1000, found);
        }

        Assert.True(allocations.Max() <= allocations.Min() * 2 + 1024,
            "Indexed reads must not allocate in proportion to unrelated source entries.");
    }

    private static ConfigSourceEntry<int> Entry(string key, string layer, string native, int metadata) =>
        new(AppSurfaceConfigKey.Parse(key), key, layer, native, metadata);
}
