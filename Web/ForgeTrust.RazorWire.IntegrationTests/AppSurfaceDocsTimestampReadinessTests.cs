using System.Text.Json;

namespace ForgeTrust.RazorWire.IntegrationTests;

[Collection(AppSurfaceDocsIntegrationCollection.Name)]
public sealed class AppSurfaceDocsTimestampReadinessTests(AppSurfaceDocsPlaywrightFixture fixture)
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Readiness_rejects_nonpositive_allowances_instead_of_disabling_timeout(int allowance)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            AppSurfaceDocsTimestampReadiness.WaitAsync(null!, allowance));
    }

    [Fact]
    public async Task Missing_git_freshness_keeps_links_but_still_fails_timestamp_readiness()
    {
        await using var host = await AppSurfaceDocsContributorTimestampFixture.StartAsync(includeTimestampOverride: false);
        await using var context = await fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(host.PageUrl);
        await page.WaitForSelectorAsync(".docs-provenance-strip");

        Assert.Equal(2, await page.Locator(".docs-provenance-strip a").CountAsync());
        Assert.Equal(0, await page.Locator(".docs-provenance-strip time").CountAsync());
        var failure = await Assert.ThrowsAsync<TimeoutException>(() =>
            AppSurfaceDocsTimestampReadiness.WaitAsync(page, timeoutMilliseconds: 100));

        Assert.Contains("\"TimeCount\":0", failure.Message, StringComparison.Ordinal);
        Assert.Contains("\"FormatterStarted\":true", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explicit_timestamp_still_fails_readiness_when_packaged_formatter_is_blocked()
    {
        await using var host = await AppSurfaceDocsContributorTimestampFixture.StartAsync();
        await using var context = await fixture.Browser.NewContextAsync();
        await context.RouteAsync("**/_content/ForgeTrust.RazorWire/razorwire/razorwire.js*", route => route.AbortAsync());
        var page = await context.NewPageAsync();
        await page.GotoAsync(host.PageUrl);
        await page.WaitForSelectorAsync(".docs-provenance-strip");

        Assert.Equal(1, await page.Locator(".docs-provenance-strip time").CountAsync());
        var failure = await Assert.ThrowsAsync<TimeoutException>(() =>
            AppSurfaceDocsTimestampReadiness.WaitAsync(page, timeoutMilliseconds: 100));

        Assert.Contains("\"HasUtcFallback\":true", failure.Message, StringComparison.Ordinal);
        Assert.Contains("\"HasValidDatetime\":true", failure.Message, StringComparison.Ordinal);
        Assert.Contains("\"HasFormatter\":false", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Readiness_failure_distinguishes_missing_timestamp_invalid_data_and_unstarted_formatter(
        bool includeTimestamp, bool validDatetime)
    {
        await using var context = await fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var time = includeTimestamp
            ? $"<time data-rw-time data-rw-time-display='relative' datetime='{(validDatetime ? "2026-04-22T23:19:00Z" : "invalid")}'>2026-04-22 UTC</time>"
            : string.Empty;
        await page.SetContentAsync($"<section class='docs-provenance-strip'>{time}</section>");

        var failure = await Assert.ThrowsAsync<TimeoutException>(() =>
            AppSurfaceDocsTimestampReadiness.WaitAsync(page, timeoutMilliseconds: 100));

        Assert.IsType<TimeoutException>(failure.InnerException);
        using var snapshot = JsonDocument.Parse(failure.Message.Split("State: ")[1]);
        Assert.Equal(includeTimestamp ? 1 : 0, snapshot.RootElement.GetProperty("TimeCount").GetInt32());
        Assert.Equal(includeTimestamp, snapshot.RootElement.GetProperty("HasUtcFallback").GetBoolean());
        Assert.Equal(includeTimestamp, snapshot.RootElement.GetProperty("HasTimeMarker").GetBoolean());
        Assert.Equal(includeTimestamp && validDatetime, snapshot.RootElement.GetProperty("HasValidDatetime").GetBoolean());
        Assert.False(snapshot.RootElement.GetProperty("RuntimeInitialized").GetBoolean());
        Assert.False(snapshot.RootElement.GetProperty("HasFormatter").GetBoolean());
        Assert.DoesNotContain("2026-04-22", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Readiness_preserves_success_for_formatted_timestamp_text()
    {
        await using var context = await fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.SetContentAsync("<section class='docs-provenance-strip'><time>one minute ago</time></section>");

        await AppSurfaceDocsTimestampReadiness.WaitAsync(page, timeoutMilliseconds: 100);
    }
}
