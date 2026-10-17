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

    [Theory]
    [InlineData("NestedCollections", false, "a", StatusCodes.Status400BadRequest)]
    [InlineData("NestedCollections", true, "a", StatusCodes.Status400BadRequest)]
    [InlineData("SkoolitV4Collections", false, "a", StatusCodes.Status400BadRequest)]
    [InlineData("SkoolitV4Collections", true, "a", StatusCodes.Status400BadRequest)]
    [InlineData("NestedCollections", false, "B", StatusCodes.Status200OK)]
    [InlineData("NestedCollections", true, "B", StatusCodes.Status200OK)]
    [InlineData("SkoolitV4Collections", false, "B", StatusCodes.Status200OK)]
    [InlineData("SkoolitV4Collections", true, "B", StatusCodes.Status200OK)]
    public async Task Editor_RejectsCaseCollidingKeysWithinEachCollection(
        string route, bool childKeys, string secondKey, int expectedStatus)
    {
        await using var context = await fixture.Browser.NewContextAsync(new BrowserNewContextOptions { JavaScriptEnabled = false });
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{fixture.BaseUrl}/Reactivity/{route}");
        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", await page.Locator("input[name='__RequestVerificationToken']").InputValueAsync())
        };
        if (childKeys)
        {
            fields.Add(new("Actions.index", "4"));
            fields.Add(new("Actions[4].Title", "Keep parent"));
        }
        foreach (var key in new[] { "A", secondKey })
        {
            var collection = childKeys ? "Actions[4].Dates" : "Actions";
            var prefix = $"{collection}[{key}]";
            fields.Add(new($"{collection}.index", key));
            fields.Add(new($"{prefix}.Id", $"identity-{key}"));
            if (childKeys)
            {
                fields.Add(new($"{prefix}.Kind", key == "A" ? "eventStart" : "dueBy"));
                fields.Add(new($"{prefix}.Date", "2026-10-14"));
            }
            else
            {
                fields.Add(new($"{prefix}.Title", $"Title {key}"));
            }
        }
        var response = await context.APIRequest.PostAsync($"{fixture.BaseUrl}/Reactivity/{route}", new APIRequestContextOptions
        {
            Data = string.Join("&", fields.Select(field => $"{WebUtility.UrlEncode(field.Key)}={WebUtility.UrlEncode(field.Value)}")),
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/x-www-form-urlencoded" }
        });
        Assert.Equal(expectedStatus, response.Status);
        if (expectedStatus == StatusCodes.Status400BadRequest) return;

        await page.SetContentAsync(await response.TextAsync());
        var collectionName = childKeys ? "Actions[4].Dates" : "Actions";
        Assert.Equal(new[] { "A", secondKey }, await page.Locator($"input[name='{collectionName}.index']").EvaluateAllAsync<string[]>("inputs => inputs.map(input => input.value)"));
        foreach (var key in new[] { "A", secondKey })
        {
            var prefix = $"{collectionName}[{key}]";
            Assert.Equal($"identity-{key}", await page.Locator($"input[name='{prefix}.Id']").InputValueAsync());
            Assert.Equal(childKeys ? key == "A" ? "eventStart" : "dueBy" : $"Title {key}", await page.Locator($"[name='{prefix}.{(childKeys ? "Kind" : "Title")}']").InputValueAsync());
        }
    }

    [Theory]
    [InlineData("NestedCollections")]
    [InlineData("SkoolitV4Collections")]
    public async Task Editor_PreservesIndependentCaseVariantChildKeysAcrossParents(string route)
    {
        await using var context = await fixture.Browser.NewContextAsync(new BrowserNewContextOptions { JavaScriptEnabled = false });
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{fixture.BaseUrl}/Reactivity/{route}");
        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", await page.Locator("input[name='__RequestVerificationToken']").InputValueAsync())
        };
        foreach (var (parent, child) in new[] { ("4", "A"), ("9", "a") })
        {
            fields.Add(new("Actions.index", parent));
            fields.Add(new($"Actions[{parent}].Title", $"Title {parent}"));
            fields.Add(new($"Actions[{parent}].Dates.index", child));
            fields.Add(new($"Actions[{parent}].Dates[{child}].Id", $"date-{parent}"));
            fields.Add(new($"Actions[{parent}].Dates[{child}].Kind", parent == "4" ? "eventStart" : "dueBy"));
            fields.Add(new($"Actions[{parent}].Dates[{child}].Date", "2026-10-14"));
        }
        var response = await context.APIRequest.PostAsync($"{fixture.BaseUrl}/Reactivity/{route}", new APIRequestContextOptions
        {
            Data = string.Join("&", fields.Select(field => $"{WebUtility.UrlEncode(field.Key)}={WebUtility.UrlEncode(field.Value)}")),
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/x-www-form-urlencoded" }
        });
        Assert.Equal(StatusCodes.Status200OK, response.Status);
        await page.SetContentAsync(await response.TextAsync());
        foreach (var (parent, child) in new[] { ("4", "A"), ("9", "a") })
        {
            Assert.Equal(child, await page.Locator($"input[name='Actions[{parent}].Dates.index']").InputValueAsync());
            Assert.Equal($"date-{parent}", await page.Locator($"input[name='Actions[{parent}].Dates[{child}].Id']").InputValueAsync());
            Assert.Equal(parent == "4" ? "eventStart" : "dueBy", await page.Locator($"[name='Actions[{parent}].Dates[{child}].Kind']").InputValueAsync());
        }
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
