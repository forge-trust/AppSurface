using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

[Collection(RazorWireIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RazorWireNestedCollectionServerCommandTests(RazorWireMvcPlaywrightFixture fixture)
{
    [Theory]
    [InlineData("missing", 2, false, StatusCodes.Status422UnprocessableEntity)]
    [InlineData("4", 2, true, StatusCodes.Status422UnprocessableEntity)]
    [InlineData("4", 8, false, StatusCodes.Status422UnprocessableEntity)]
    [InlineData("4", 7, false, StatusCodes.Status200OK)]
    public async Task AddDateCommand_ReportsRejectedOwnerAndCapacityWithoutChangingRows(
        string owner, int dateCount, bool deleted, int expectedStatus)
    {
        await using var context = await fixture.Browser.NewContextAsync(new BrowserNewContextOptions { JavaScriptEnabled = false });
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{fixture.BaseUrl}/Reactivity/NestedCollections");
        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", await page.Locator("input[name='__RequestVerificationToken']").InputValueAsync()),
            new("Actions.index", "4"),
            new("Actions[4].Title", "Keep this title"),
            new("Actions[4].Id", "action-4"),
            new("Actions[4].Delete", deleted ? "true" : "false"),
            new("RemoveMode", "mark"),
            new("addDateFor", owner)
        };
        for (var i = 0; i < dateCount; i++)
        {
            var key = (i * 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
            fields.Add(new("Actions[4].Dates.index", key));
            fields.Add(new($"Actions[4].Dates[{key}].Kind", "Scheduled"));
            fields.Add(new($"Actions[4].Dates[{key}].Date", i == 0 ? "attempted-invalid-date" : "2026-10-14"));
        }

        var response = await context.APIRequest.PostAsync($"{fixture.BaseUrl}/Reactivity/NestedCollections", new APIRequestContextOptions
        {
            Data = string.Join("&", fields.Select(field => $"{WebUtility.UrlEncode(field.Key)}={WebUtility.UrlEncode(field.Value)}")),
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/x-www-form-urlencoded" }
        });
        Assert.Equal(expectedStatus, response.Status);
        await page.SetContentAsync(await response.TextAsync());
        Assert.Equal("Keep this title", await page.Locator("input[name='Actions[4].Title']").InputValueAsync());
        Assert.Equal("action-4", await page.Locator("input[name='Actions[4].Id']").InputValueAsync());
        Assert.Equal("4", await page.Locator("input[name='Actions.index']").InputValueAsync());
        var markers = page.Locator("input[name='Actions[4].Dates.index']");
        Assert.Equal(dateCount + (expectedStatus == StatusCodes.Status200OK ? 1 : 0), await markers.CountAsync());
        for (var i = 0; i < dateCount; i++)
        {
            var key = (i * 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(key, await markers.Nth(i).InputValueAsync());
            Assert.Equal("Scheduled", await page.Locator($"input[name='Actions[4].Dates[{key}].Kind']").InputValueAsync());
            Assert.Equal(i == 0 ? "attempted-invalid-date" : "2026-10-14", await page.Locator($"input[name='Actions[4].Dates[{key}].Date']").InputValueAsync());
        }
        var error = page.GetByText("The date could not be added to that action.", new PageGetByTextOptions { Exact = true });
        Assert.Equal(expectedStatus == StatusCodes.Status422UnprocessableEntity ? 1 : 0, await error.CountAsync());
    }

    [Fact]
    public async Task V4Editor_RendersOneStandaloneDocumentWithoutSharedLayout()
    {
        await using var context = await fixture.Browser.NewContextAsync();
        var document = await context.APIRequest.GetAsync($"{fixture.BaseUrl}/Reactivity/SkoolitV4Collections");
        Assert.Equal(StatusCodes.Status200OK, document.Status);
        var html = await document.TextAsync();
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<!doctype html>", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<html(?:\\s|>)", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<body(?:\\s|>)", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        Assert.DoesNotContain("class=\"navbar", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Unexpected")]
    public async Task V4Editor_RejectsUnknownDateShapeOnItsSparseFieldPath(string shape)
    {
        await using var context = await fixture.Browser.NewContextAsync(new BrowserNewContextOptions { JavaScriptEnabled = false });
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{fixture.BaseUrl}/Reactivity/SkoolitV4Collections");
        var token = await page.Locator("input[name='__RequestVerificationToken']").InputValueAsync();
        var response = await context.APIRequest.PostAsync($"{fixture.BaseUrl}/Reactivity/SkoolitV4Collections", new APIRequestContextOptions
        {
            Data = string.Join("&", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Actions.index"] = "9",
                ["Actions[9].Title"] = "Keep action",
                ["Actions[9].Dates.index"] = "7",
                ["Actions[9].Dates[7].Shape"] = shape,
                ["Actions[9].Dates[7].EndDay"] = "retained end"
            }.Select(field => $"{WebUtility.UrlEncode(field.Key)}={WebUtility.UrlEncode(field.Value)}")),
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/x-www-form-urlencoded" }
        });
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, response.Status);
        await page.SetContentAsync(await response.TextAsync());
        Assert.Equal("7", await page.Locator("input[name='Actions[9].Dates.index']").InputValueAsync());
        Assert.Equal("retained end", await page.Locator("input[name='Actions[9].Dates[7].EndDay']").InputValueAsync());
        Assert.Contains("Choose Discrete or Range.", await page.Locator("[data-valmsg-for='Actions[9].Dates[7].Shape']").InnerTextAsync());
    }
}
