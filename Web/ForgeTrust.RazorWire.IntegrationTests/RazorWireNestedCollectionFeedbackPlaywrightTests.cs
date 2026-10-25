using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

[Collection(RazorWireIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RazorWireNestedCollectionFeedbackPlaywrightTests(RazorWireMvcPlaywrightFixture fixture)
{
    [Theory]
    [InlineData("physical")]
    [InlineData("mark")]
    public async Task DateOnlyDuplicate_RespectsOwnerCapacityAndTombstones(string mode)
    {
        await using var context = await fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{fixture.BaseUrl}/Reactivity/NestedCollections?mode={mode}");
        var dates = page.Locator("[data-rw-form-collection='Actions[4].Dates']");
        var live = dates.Locator(":scope > fieldset:not([hidden])");
        for (var count = 2; count < 7; count++) await dates.GetByRole(AriaRole.Button, new() { Name = "Add date", Exact = true }).ClickAsync();
        await live.First.GetByRole(AriaRole.Button, new() { Name = "Duplicate date", Exact = true }).ClickAsync();
        Assert.Equal(8, await live.CountAsync());
        await live.First.GetByRole(AriaRole.Button, new() { Name = "Duplicate date", Exact = true }).ClickAsync();
        Assert.Equal(8, await live.CountAsync());
        Assert.Equal("Use at most eight dates per action.", await dates.Locator("[data-rw-form-collection-status]").InnerTextAsync());
        await dates.GetByRole(AriaRole.Button, new() { Name = "Add date", Exact = true }).ClickAsync();
        Assert.Equal(8, await live.CountAsync());
        Assert.Equal(2, await page.Locator("[data-rw-form-collection='Actions[9].Dates'] > fieldset:not([hidden])").CountAsync());
        await live.First.GetByRole(AriaRole.Button, new() { Name = "Remove date", Exact = true }).ClickAsync();
        Assert.Equal(7, await live.CountAsync());
        await live.First.GetByRole(AriaRole.Button, new() { Name = "Duplicate date", Exact = true }).ClickAsync();
        Assert.Equal(8, await live.CountAsync());
        Assert.Equal(mode == "mark" ? 1 : 0, await dates.Locator(":scope > fieldset[hidden]").CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V4ClientDuplicate_ShowsUnsavedFeedbackWithoutChangingSource(bool child)
    {
        await using var context = await fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{fixture.BaseUrl}/Reactivity/SkoolitV4Collections");
        var actions = page.Locator("[data-rw-form-collection='Actions'] > fieldset");
        var source = actions.First;
        var sourceFeedback = await source.Locator("[data-date-resolution]").AllTextContentsAsync();
        if (child)
            await source.Locator("[data-v4-date-row]").First.GetByRole(AriaRole.Button, new() { Name = "Duplicate date", Exact = true }).ClickAsync();
        else
            await source.GetByRole(AriaRole.Button, new() { Name = "Duplicate action", Exact = true }).ClickAsync();
        var copies = child ? source.Locator("[data-v4-date-row]").Nth(1).Locator("[data-date-resolution]") : actions.Nth(1).Locator("[data-date-resolution]");
        Assert.All(await copies.AllTextContentsAsync(), text => Assert.Equal("Unsaved date changes. Save labels to check the date and its year.", text));
        Assert.True(await copies.CountAsync() > 0);
        Assert.Equal(sourceFeedback[0], await source.Locator("[data-v4-date-row]").First.Locator("[data-date-resolution]").InnerTextAsync());
    }

    [Fact]
    public async Task V4ServerDuplicate_KeepsUnsavedFeedbackAcrossSubmitAndBlankDraftPrompt()
    {
        await using var context = await fixture.Browser.NewContextAsync(new BrowserNewContextOptions { JavaScriptEnabled = false });
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{fixture.BaseUrl}/Reactivity/SkoolitV4Collections");
        var actions = page.Locator("[data-rw-form-collection='Actions'] > fieldset");
        await actions.First.GetByRole(AriaRole.Button, new() { Name = "Duplicate action on server", Exact = true }).ClickAsync();
        var feedback = actions.Nth(1).Locator("[data-date-resolution]");
        Assert.All(await feedback.AllTextContentsAsync(), text => Assert.StartsWith("Unsaved date changes.", text));
        Assert.True(await feedback.CountAsync() > 0);
        await page.GetByRole(AriaRole.Button, new() { Name = "Save labels", Exact = true }).ClickAsync();
        Assert.All(await feedback.AllTextContentsAsync(), text => Assert.StartsWith("Unsaved date changes.", text));
        await actions.Nth(1).GetByRole(AriaRole.Button, new() { Name = "Add date on server", Exact = true }).ClickAsync();
        Assert.StartsWith("Enter the source month and day.", await actions.Nth(1).Locator("[data-date-resolution]").Last.InnerTextAsync());
        Assert.StartsWith("Last saved result:", await actions.First.Locator("[data-date-resolution]").First.InnerTextAsync());
    }
}
