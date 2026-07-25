using System.Net;
using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

[Collection(RazorWireIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RazorWireFormInteractionsQaRegressionTests
{
    private readonly RazorWireMvcPlaywrightFixture _fixture;

    public RazorWireFormInteractionsQaRegressionTests(RazorWireMvcPlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DuplicateAddAndMarkedDelete_SubmitsActiveRowsWithoutBindingFailure()
    {
        // Regression: ISSUE-001 — duplicated delete fields submitted an empty Boolean value.
        // Found by /qa on 2026-07-18.
        // Report: .gstack/qa-reports/qa-report-127-0-0-1-6196-2026-07-18.md
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.BaseUrl}/Reactivity/FormInteractions");
        await page.ClickAsync("[data-rw-form-collection-duplicate]");
        await page.WaitForSelectorAsync("input[name='Actions[1].Title']");
        await page.ClickAsync("[data-rw-form-collection-add]");
        await page.FillAsync("input[name='Actions[2].Title']", "Email teacher");
        await page.ClickAsync("button[data-rw-form-collection-remove='mark']");

        var submitResponse = await page.RunAndWaitForResponseAsync(
            () => page.ClickAsync("button[type='submit']"),
            response => response.Url.Contains("/Reactivity/SubmitFormInteractions", StringComparison.Ordinal));

        Assert.True(
            (HttpStatusCode)submitResponse.Status is HttpStatusCode.OK or HttpStatusCode.Found,
            $"Expected accepted POST or redirect response, got {submitResponse.Status}.");
        await page.WaitForSelectorAsync("text=Saved 2 action row(s).");
    }
}
