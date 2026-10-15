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
    [InlineData(2, true)]
    [InlineData(10, false)]
    [InlineData(100, false)]
    public async Task Duplicate_ReservesDocumentIdsPerOperationRatherThanPerField(int parentCount, bool thirdLevel)
    {
        await using var context = await fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{fixture.BaseUrl}/Reactivity/NestedCollections");
        await page.WaitForSelectorAsync("[data-rw-form-interactions-enhanced]");

        // The fixture authors a larger valid form from the application's real templates.
        // The oracle below observes a public command, its payload and query work.
        await page.EvaluateAsync("""
            ({ count, thirdLevel }) => {
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
                  if (thirdLevel && dateKey === 0) {
                    const prefix = `Actions[${key}].Dates[0].Reminders`;
                    date.insertAdjacentHTML('beforeend', `<section data-rw-form-collection="${prefix}" data-rw-form-collection-index-token="__reminder_index__">
                      <fieldset data-rw-form-collection-row="true" data-rw-form-index="3">
                        <input type="hidden" name="${prefix}.index" value="3">
                        <input name="${prefix}[3].Message" value="Source reminder">
                      </fieldset>
                      <template data-rw-form-collection-template="true">
                        <fieldset data-rw-form-collection-row="true" data-rw-form-index="__reminder_index__">
                          <input type="hidden" name="${prefix}.index" value="__reminder_index__">
                          <input name="${prefix}[__reminder_index__].Message" value="">
                        </fieldset>
                      </template>
                      <button type="button" data-rw-form-collection-add="true">Add reminder</button>
                    </section>`);
                  }
                  dates.insertBefore(date, dateTemplate);
                }
                root.insertBefore(row, template);
              }
              window.RazorWire.formInteractionsManager.scan();
            }
            """, new { count = parentCount, thirdLevel });

        // Value: protects=recursive payload isolation and bounded document scans;
        // fails_when=duplication scans document IDs per field or reuses descendant keys;
        // why_new=prior scale probe omitted third-level work and command-local measurements;
        // seam=none; no_claim=heap bytes or a latency SLO (public DOM and constructor counts only).
        // Counts observe public DOM/constructor work, split at the documented before/after events.
        // Timing spans the synchronous command only. No heap-byte or latency/SLO claim is made.
        // Finally restores every instrumented API before payload and accessibility assertions.
        var probe = await page.EvaluateAsync<ScaleProbe>("""
            () => {
              const root = document.querySelector('section[data-rw-form-collection="Actions"]');
              const source = root.querySelector(':scope > [data-rw-form-collection-row]');
              const button = source.querySelector(':scope > button[data-rw-form-collection-duplicate]');
              const original = {
                documentQuery: Document.prototype.querySelectorAll,
                elementQuery: Element.prototype.querySelectorAll,
                fragmentQuery: DocumentFragment.prototype.querySelectorAll,
                clone: Node.prototype.cloneNode,
                create: Document.prototype.createElement,
                map: window.Map,
                set: window.Set
              };
              const counts = { DocumentIdScans: 0, SubtreeQueries: 0, Clones: 0, CreatedElements: 0, Maps: 0, Sets: 0 };
              const stages = [];
              const snapshot = stage => stages.push({ Stage: stage, ...counts, ElapsedMilliseconds: performance.now() - start });
              const before = () => snapshot('prepared');
              const after = () => snapshot('inserted');
              const inventory = node => {
                let elements = 0, attributes = 0, rows = 0;
                const visit = current => {
                  if (current.nodeType === Node.ELEMENT_NODE) {
                    elements++;
                    attributes += current.attributes.length;
                    if (current.hasAttribute('data-rw-form-collection-row')) rows++;
                  }
                  for (const child of current.children ?? []) visit(child);
                  if (current instanceof HTMLTemplateElement) visit(current.content);
                };
                visit(node);
                return { Elements: elements, Attributes: attributes, Rows: rows };
              };
              const sourceInventory = inventory(source);
              const formInventory = inventory(root);
              let start = 0, elapsed = 0;
              root.addEventListener('razorwire:form-collection:before-duplicate', before);
              root.addEventListener('razorwire:form-collection:duplicate', after);
              try {
                Document.prototype.querySelectorAll = function(selector) {
                  if (selector === '[id]') counts.DocumentIdScans++;
                  return original.documentQuery.call(this, selector);
                };
                Element.prototype.querySelectorAll = function(selector) {
                  counts.SubtreeQueries++;
                  return original.elementQuery.call(this, selector);
                };
                DocumentFragment.prototype.querySelectorAll = function(selector) {
                  counts.SubtreeQueries++;
                  return original.fragmentQuery.call(this, selector);
                };
                Node.prototype.cloneNode = function(deep) {
                  counts.Clones++;
                  return original.clone.call(this, deep);
                };
                Document.prototype.createElement = function(...args) {
                  counts.CreatedElements++;
                  return original.create.apply(this, args);
                };
                window.Map = new Proxy(original.map, { construct(target, args, ctor) {
                  counts.Maps++;
                  return Reflect.construct(target, args, ctor);
                }});
                window.Set = new Proxy(original.set, { construct(target, args, ctor) {
                  counts.Sets++;
                  return Reflect.construct(target, args, ctor);
                }});
                start = performance.now();
                button.click();
                elapsed = performance.now() - start;
                snapshot('complete');
              } finally {
                Document.prototype.querySelectorAll = original.documentQuery;
                Element.prototype.querySelectorAll = original.elementQuery;
                DocumentFragment.prototype.querySelectorAll = original.fragmentQuery;
                Node.prototype.cloneNode = original.clone;
                Document.prototype.createElement = original.create;
                window.Map = original.map;
                window.Set = original.set;
                root.removeEventListener('razorwire:form-collection:before-duplicate', before);
                root.removeEventListener('razorwire:form-collection:duplicate', after);
              }
              const rows = [...root.querySelectorAll(':scope > [data-rw-form-collection-row]')];
              const copy = rows[1];
              const child = copy.querySelector('[data-rw-form-collection]');
              const ids = [...document.querySelectorAll('[id]')].map(element => element.id);
              const data = new FormData(root.closest('form'));
              const reminder = copy.querySelector('[data-rw-form-collection$=".Reminders"]');
              return {
                ParentKeys: data.getAll('Actions.index').map(String),
                ChildKeys: data.getAll(`${child.dataset.rwFormCollection}.index`).map(String),
                SourceChildKeys: data.getAll('Actions[0].Dates.index').map(String),
                ReminderKeys: reminder ? data.getAll(`${reminder.dataset.rwFormCollection}.index`).map(String) : [],
                SourceReminderKeys: data.getAll('Actions[0].Dates[0].Reminders.index').map(String),
                ReminderValue: reminder ? data.get(`${reminder.dataset.rwFormCollection}[4].Message`) : null,
                CopiedKinds: [...copy.querySelectorAll('input[name$=".Kind"]')].map(input => input.value),
                ...counts,
                Stages: stages,
                SourceElements: sourceInventory.Elements,
                SourceAttributes: sourceInventory.Attributes,
                SourceRows: sourceInventory.Rows,
                FormElements: formInventory.Elements,
                FormAttributes: formInventory.Attributes,
                FormRows: formInventory.Rows,
                ClonedElements: inventory(copy).Elements,
                IdCount: ids.length,
                UniqueIdCount: new Set(ids).size,
                ElapsedMilliseconds: elapsed,
                Engine: navigator.userAgent,
                APIsRestored: Document.prototype.querySelectorAll === original.documentQuery
                  && Element.prototype.querySelectorAll === original.elementQuery
                  && DocumentFragment.prototype.querySelectorAll === original.fragmentQuery
                  && Node.prototype.cloneNode === original.clone && Document.prototype.createElement === original.create
                  && window.Map === original.map && window.Set === original.set
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
        Assert.Equal(new[] { "prepared", "inserted", "complete" }, probe.Stages.Select(stage => stage.Stage));
        Assert.Equal(1, probe.Stages[0].DocumentIdScans);
        Assert.Equal(2, probe.Stages[1].DocumentIdScans);
        Assert.Equal(2, probe.Stages[2].DocumentIdScans);
        Assert.True(probe.APIsRestored);
        Assert.True(probe.SourceElements > 0 && probe.SourceAttributes > probe.SourceElements);
        // Includes both the live reminder and its inert future row.
        Assert.Equal(thirdLevel ? 9 : 7, probe.SourceRows);
        Assert.True(probe.FormRows >= parentCount * (thirdLevel ? 9 : 7));
        Assert.Equal(probe.SourceElements, probe.ClonedElements);
        Assert.InRange(probe.Clones, 1, 3);
        Assert.InRange(probe.SubtreeQueries, 1, parentCount * 30 + probe.SourceElements * 10);
        Assert.InRange(probe.Maps + probe.Sets, 1, probe.SourceElements * 4 + parentCount * 2);
        if (thirdLevel)
        {
            Assert.Equal(new[] { "3" }, probe.SourceReminderKeys);
            Assert.Equal(new[] { "4" }, probe.ReminderKeys);
            Assert.Equal("Source reminder", probe.ReminderValue);
        }
        Assert.True(double.IsFinite(probe.ElapsedMilliseconds) && probe.ElapsedMilliseconds >= 0);
        Assert.All(probe.Stages, stage => Assert.True(double.IsFinite(stage.ElapsedMilliseconds)));
        // Timing is observational; query counts provide the deterministic regression guard.
        Console.WriteLine($"Nested scale profile: parents={parentCount}, third-level={thirdLevel}, "
            + System.Text.Json.JsonSerializer.Serialize(probe));
    }

    private sealed class StageProbe
    {
        public string Stage { get; set; } = string.Empty;
        public int DocumentIdScans { get; set; }
        public int SubtreeQueries { get; set; }
        public int Clones { get; set; }
        public int CreatedElements { get; set; }
        public int Maps { get; set; }
        public int Sets { get; set; }
        public double ElapsedMilliseconds { get; set; }
    }

    private sealed class ScaleProbe
    {
        public string[] ParentKeys { get; set; } = [];
        public string[] ChildKeys { get; set; } = [];
        public string[] SourceChildKeys { get; set; } = [];
        public string[] ReminderKeys { get; set; } = [];
        public string[] SourceReminderKeys { get; set; } = [];
        public string? ReminderValue { get; set; }
        public StageProbe[] Stages { get; set; } = [];
        public int SourceElements { get; set; }
        public int SourceAttributes { get; set; }
        public int SourceRows { get; set; }
        public int FormElements { get; set; }
        public int FormAttributes { get; set; }
        public int FormRows { get; set; }
        public int ClonedElements { get; set; }
        public int SubtreeQueries { get; set; }
        public int Clones { get; set; }
        public int CreatedElements { get; set; }
        public int Maps { get; set; }
        public int Sets { get; set; }
        public bool APIsRestored { get; set; }
        public string Engine { get; set; } = string.Empty;
        public string[] CopiedKinds { get; set; } = [];
        public int DocumentIdScans { get; set; }
        public int IdCount { get; set; }
        public int UniqueIdCount { get; set; }
        public double ElapsedMilliseconds { get; set; }
    }
}
