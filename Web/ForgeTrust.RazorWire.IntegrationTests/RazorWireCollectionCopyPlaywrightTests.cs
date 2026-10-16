using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

[Collection(RazorWireIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RazorWireCollectionCopyPlaywrightTests(RazorWireMvcPlaywrightFixture fixture)
{
    [Fact]
    public async Task CollectionAddAndDuplicate_PreserveLiveMultiSelectValuesAndValidationPlaceholderMetadata()
    {
        await using var context = await fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.SetContentAsync(
            """
            <!doctype html>
            <form id="collection-form">
              <section data-rw-form-collection="Items" data-rw-form-collection-label="item">
                <fieldset data-rw-form-collection-row data-rw-form-index="0">
                  <input type="hidden" name="Items.index" value="0">
                  <input name="Items[0].Name" value="Source">
                  <span data-valmsg-for="Items[0].Name" data-valmsg-replace="true" aria-invalid="true">Old error</span>
                  <select name="Items[0].Tags" multiple>
                    <option value="red">Red</option>
                    <option value="green">Green</option>
                    <option value="blue">Blue</option>
                  </select>
                  <select name="Items[0].EmptyChoice"><option value="one">One</option><option value="two">Two</option></select>
                  <button type="button" data-rw-form-collection-duplicate="true">Duplicate</button>
                </fieldset>
                <button type="button" data-rw-form-collection-add="true">Add</button>
                <template data-rw-form-collection-template>
                  <fieldset data-rw-form-collection-row>
                    <input type="hidden" name="Items.index" value="__index__">
                    <input name="Items[__index__].Name" value="Template">
                    <span data-valmsg-for="Items[__index__].Name" data-valmsg-replace="true" aria-invalid="true">Template error</span>
                    <select name="Items[__index__].Tags" multiple>
                      <option value="red">Red</option>
                      <option value="green">Green</option>
                      <option value="blue">Blue</option>
                    </select>
                    <button type="button" data-rw-form-collection-duplicate="true">Duplicate</button>
                  </fieldset>
                </template>
              </section>
            </form>
            """);

        var repositoryRoot = ForgeTrust.AppSurface.Core.PathUtils.FindRepositoryRoot(AppContext.BaseDirectory);
        var runtimePath = TestPathUtils.PathUnder(
            repositoryRoot,
            "Web",
            "ForgeTrust.RazorWire",
            "wwwroot",
            "razorwire",
            "form-interactions.js");
        await page.AddScriptTagAsync(new PageAddScriptTagOptions
        {
            Content = await File.ReadAllTextAsync(runtimePath)
        });
        await page.WaitForFunctionAsync("() => document.querySelector('form')?.dataset.rwFormInteractionsEnhanced === 'true'");

        await page.Locator("select[name='Items[0].Tags']").EvaluateAsync(
            "select => { select.options[0].selected = true; select.options[2].selected = true; }");
        await page.Locator("select[name='Items[0].EmptyChoice']").EvaluateAsync("select => select.selectedIndex = -1");
        await page.Locator("[data-rw-form-collection-duplicate]").First.ClickAsync();

        var duplicate = page.Locator("[data-rw-form-collection-row]").Nth(1);
        Assert.Equal(-1, await duplicate.Locator("select[name='Items[1].EmptyChoice']").EvaluateAsync<int>("select => select.selectedIndex"));
        Assert.Empty(await page.EvaluateAsync<string[]>("() => new FormData(document.querySelector('#collection-form')).getAll('Items[1].EmptyChoice').map(String)"));
        Assert.Equal("true", await duplicate.Locator("[data-valmsg-for]").GetAttributeAsync("data-valmsg-replace"));
        Assert.Equal(string.Empty, (await duplicate.Locator("[data-valmsg-for]").InnerTextAsync()).Trim());
        Assert.Equal(
            new[] { "red", "blue" },
            await page.EvaluateAsync<string[]>(
                "() => new FormData(document.querySelector('#collection-form')).getAll('Items[1].Tags').map(String)"));
        Assert.Equal(
            new[] { "red", "blue" },
            await page.EvaluateAsync<string[]>(
                "() => new FormData(document.querySelector('#collection-form')).getAll('Items[0].Tags').map(String)"));

        await page.Locator("[data-rw-form-collection-add]").ClickAsync();
        var added = page.Locator("[data-rw-form-collection-row]").Nth(2);
        Assert.Equal("true", await added.Locator("[data-valmsg-for]").GetAttributeAsync("data-valmsg-replace"));
        Assert.Equal(string.Empty, (await added.Locator("[data-valmsg-for]").InnerTextAsync()).Trim());
    }
}
