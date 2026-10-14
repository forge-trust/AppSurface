using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

[Collection(RazorWireIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RazorWireNestedCollectionScalePlaywrightTests(RazorWireMvcPlaywrightFixture fixture)
{
    [Fact]
    public async Task RapidSequentialAddsAllocateUniqueKeysIndependentlyForEachParent()
    {
        await using var context = await fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{fixture.BaseUrl}/Reactivity/NestedCollections");
        await page.WaitForSelectorAsync("[data-rw-form-interactions-enhanced]");

        foreach (var actionIndex in new[] { "4", "9" })
        {
            var dateRoot = page.Locator($"section[data-rw-form-collection='Actions[{actionIndex}].Dates']");
            var add = dateRoot.Locator(":scope > button[data-rw-form-collection-add]");
            for (var i = 0; i < 4; i++) await add.ClickAsync();

            var keys = await dateRoot.EvaluateAsync<string[]>(
                "root => [...root.querySelectorAll(':scope > fieldset[data-rw-form-collection-row] input[type=hidden]')].filter(input => input.name === root.dataset.rwFormCollection + '.index').map(input => input.value)");
            Assert.Equal(new[] { "0", "7", "8", "9", "10", "11" }, keys);
            Assert.Equal($"Actions[{actionIndex}].Dates[11].Kind", await page.EvaluateAsync<string>("() => document.activeElement?.name ?? ''"));
            Assert.Equal("Added date.", await dateRoot.Locator(":scope > [data-rw-form-collection-status]").InnerTextAsync());
        }

        Assert.Equal(new[] { "4", "9" }, await page.Locator("section[data-rw-form-collection='Actions']")
            .EvaluateAsync<string[]>("root => [...root.querySelectorAll(':scope > fieldset[data-rw-form-collection-row] input[name=\"Actions.index\"]')].map(input => input.value)"));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    public async Task Duplicate_ReservesDocumentIdsPerOperationRatherThanPerField(int parentCount)
    {
        await using var context = await fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{fixture.BaseUrl}/Reactivity/NestedCollections");
        await page.WaitForSelectorAsync("[data-rw-form-interactions-enhanced]");

        // The fixture authors a larger valid form from the application's real templates.
        // The oracle below observes a public command, its payload and query work.
        await page.EvaluateAsync("""
            count => {
              const root = document.querySelector('section[data-rw-form-collection="Actions"]');
              const template = root.querySelector(':scope > template');
              const add = root.querySelector(':scope > button[data-rw-form-collection-add]');
              for (const row of root.querySelectorAll(':scope > [data-rw-form-collection-row]')) row.remove();
              for (let key = 0; key < count; key++) {
                const holder = document.createElement('template');
                holder.innerHTML = template.innerHTML.replaceAll('__index__', String(key));
                const row = holder.content.firstElementChild;
                row.dataset.rwFormIndex = String(key);
                row.querySelector('input[name="Actions.index"]').value = String(key);
                row.querySelector('input[name$=".Title"]').value = `Scale action ${key}`;
                const dates = row.querySelector('[data-rw-form-collection]');
                const dateTemplate = dates.querySelector('template');
                for (let dateKey = 0; dateKey < 5; dateKey++) {
                  const childHolder = document.createElement('template');
                  childHolder.innerHTML = dateTemplate.innerHTML.replaceAll('__date_index__', String(dateKey));
                  const date = childHolder.content.firstElementChild;
                  date.dataset.rwFormIndex = String(dateKey);
                  date.querySelector('input[name$=".Dates.index"]').value = String(dateKey);
                  date.querySelector('input[name$=".Kind"]').value = `Kind ${dateKey}`;
                  date.querySelector('input[name$=".Date"]').value = '2026-10-14';
                  dates.insertBefore(date, dateTemplate);
                }
                root.insertBefore(row, template);
              }
              window.RazorWire.formInteractionsManager.scan();
              window.__scaleIdScans = 0;
              const original = document.querySelectorAll.bind(document);
              document.querySelectorAll = selector => {
                if (selector === '[id]') window.__scaleIdScans++;
                return original(selector);
              };
              window.__scaleStart = performance.now();
            }
            """, parentCount);

        await page.Locator("section[data-rw-form-collection='Actions'] > [data-rw-form-collection-row]")
            .First.Locator(":scope > button[data-rw-form-collection-duplicate]").ClickAsync();

        var probe = await page.EvaluateAsync<ScaleProbe>("""
            () => {
              const root = document.querySelector('section[data-rw-form-collection="Actions"]');
              const rows = [...root.querySelectorAll(':scope > [data-rw-form-collection-row]')];
              const copy = rows[1];
              const child = copy.querySelector('[data-rw-form-collection]');
              const ids = [...document.querySelectorAll('[id]')].map(element => element.id);
              const data = new FormData(root.closest('form'));
              return {
                ParentKeys: data.getAll('Actions.index').map(String),
                ChildKeys: data.getAll(`${child.dataset.rwFormCollection}.index`).map(String),
                SourceChildKeys: data.getAll('Actions[0].Dates.index').map(String),
                CopiedKinds: [...copy.querySelectorAll('input[name$=".Kind"]')].map(input => input.value),
                DocumentIdScans: window.__scaleIdScans - 1,
                IdCount: ids.length,
                UniqueIdCount: new Set(ids).size,
                ElapsedMilliseconds: performance.now() - window.__scaleStart
              };
            }
            """);

        Assert.Equal(parentCount + 1, probe.ParentKeys.Length);
        Assert.Equal(parentCount.ToString(System.Globalization.CultureInfo.InvariantCulture), probe.ParentKeys[1]);
        Assert.Equal(new[] { "0", "1", "2", "3", "4" }, probe.SourceChildKeys);
        Assert.Equal(new[] { "5", "6", "7", "8", "9" }, probe.ChildKeys);
        Assert.Equal(new[] { "Kind 0", "Kind 1", "Kind 2", "Kind 3", "Kind 4" }, probe.CopiedKinds);
        Assert.Equal(probe.IdCount, probe.UniqueIdCount);
        Assert.InRange(probe.DocumentIdScans, 1, 3);
        Assert.True(double.IsFinite(probe.ElapsedMilliseconds));
        // Timing is observational; query counts provide the deterministic regression guard.
        Console.WriteLine($"Nested scale: parents={parentCount}, dates={parentCount * 5}, document-id-scans={probe.DocumentIdScans}, elapsed-ms={probe.ElapsedMilliseconds:F2}");
    }

    private sealed class ScaleProbe
    {
        public string[] ParentKeys { get; set; } = [];
        public string[] ChildKeys { get; set; } = [];
        public string[] SourceChildKeys { get; set; } = [];
        public string[] CopiedKinds { get; set; } = [];
        public int DocumentIdScans { get; set; }
        public int IdCount { get; set; }
        public int UniqueIdCount { get; set; }
        public double ElapsedMilliseconds { get; set; }
    }
}
