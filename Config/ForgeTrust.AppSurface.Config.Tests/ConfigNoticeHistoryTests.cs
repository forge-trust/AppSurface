using System.Diagnostics;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ConfigNoticeHistoryTests
{
    [Fact]
    public void HistoryUsesLogicalCaseIdentityAndRetainsEveryOtherIdentityComponent()
    {
        var history = new ConfigNoticeHistoryStore();
        var key = AppSurfaceConfigKey.Parse("Payments:ApiKey");
        Assert.True(history.TryRemember("code", "provider", "Production", key, null, 10));
        Assert.False(history.TryRemember("code", "provider", "Production", AppSurfaceConfigKey.Parse("payments:apikey"), "", 10));
        Assert.True(history.TryRemember("other-code", "provider", "Production", key, null, 10));
        Assert.True(history.TryRemember("code", "other-provider", "Production", key, null, 10));
        Assert.True(history.TryRemember("code", "provider", "production", key, null, 10));
        Assert.True(history.TryRemember("code", "provider", "Production", key, "native", 10));
        Assert.True(history.TryRemember("code", "provider", "Production", key, "NATIVE", 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => history.TryRemember("code", "provider", "Production", key, null, 0));
    }

    [Fact]
    public async Task SaturationNeverEvictsExistingIdentitiesAndConcurrentDuplicatesAreAdmittedOnce()
    {
        var history = new ConfigNoticeHistoryStore();
        var key = AppSurfaceConfigKey.Parse("Payments:ApiKey");
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            history.TryRemember("code", "provider", "Production", key, "same", 2))));
        Assert.Single(results, accepted => accepted);
        Assert.True(history.TryRemember("code", "provider", "Production", key, "second", 2));
        Assert.False(history.TryRemember("code", "provider", "Production", key, "third", 2));
        Assert.False(history.TryRemember("code", "provider", "Production", key, "same", 2));
        Assert.False(history.TryRemember("code", "provider", "Production", key, "same", 3));
        Assert.True(history.TryRemember("code", "provider", "Production", key, "third", 3));
    }

    [Fact]
    public void WarmHistoryAllocationDoesNotGrowWithRetainedIdentityCount()
    {
        var allocations = new List<long>();
        foreach (var count in new[] { 40, 400, 4000 })
        {
            var history = new ConfigNoticeHistoryStore();
            var key = AppSurfaceConfigKey.Parse("Payments:ApiKey");
            for (var index = 0; index < count; index++)
            {
                history.TryRemember("code", "provider", "Production", key, index.ToString(), 4096);
            }

            history.TryRemember("code", "provider", "Production", key, "0", 4096);
            var accepted = 0;
            var watch = Stopwatch.StartNew();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var iteration = 0; iteration < 1000; iteration++)
            {
                if (history.TryRemember("code", "provider", "Production", key, "0", 4096)) { accepted++; }
            }

            allocations.Add(GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.Equal(0, accepted);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        }

        Assert.True(allocations.Max() <= allocations.Min() * 2,
            "Repeated notice lookup allocation must be independent of retained identity count.");
    }
}
