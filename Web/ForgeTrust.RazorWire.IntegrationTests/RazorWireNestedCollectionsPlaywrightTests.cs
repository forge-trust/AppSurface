using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

[Collection(RazorWireIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RazorWireNestedCollectionsPlaywrightTests
{
    private readonly RazorWireMvcPlaywrightFixture _fixture;

    public RazorWireNestedCollectionsPlaywrightTests(RazorWireMvcPlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("physical")]
    [InlineData("mark")]
    public async Task NestedCollections_FullMvcRoundTrip_PreservesSparseKeysAndRetriesInvalidCopy(string mode)
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await OpenNestedCollectionsAsync(page, mode);

        var actions = ActionsRoot(page);
        var source4 = ParentRow(page, "4");
        var source9 = ParentRow(page, "9");
        Assert.Equal(new[] { "4", "9" }, await GetKeysAsync(actions));
        Assert.Equal(new[] { "0", "7" }, await GetKeysAsync(ChildCollection(source4)));
        Assert.Equal(new[] { "0", "7" }, await GetKeysAsync(ChildCollection(source9)));

        await source4.Locator("input[name='Actions[4].Title']").FillAsync("Edited source action");
        await source4.EvaluateAsync("row => row.dataset.consumerState = 'source-owned'");
        await source4.Locator("input[name='Actions[4].Dates[7].Kind']").FillAsync("Changed source kind");
        await source4.Locator("input[name='Actions[4].Dates[7].Date']").FillAsync("2026-10-20");

        var firstDate = DateRow(page, "4", "0");
        await firstDate.Locator("button[data-rw-form-collection-remove]").ClickAsync();
        if (mode == "mark")
        {
            Assert.Equal("removed", await firstDate.GetAttributeAsync("data-rw-form-collection-row-state"));
            Assert.True(await firstDate.IsHiddenAsync());
            Assert.Equal("true", await firstDate.Locator("input[name='Actions[4].Dates[0].Delete']").InputValueAsync());
            Assert.Equal("date-4-0", await firstDate.Locator("input[name='Actions[4].Dates[0].Id']").InputValueAsync());
            Assert.True(await firstDate.Locator("input[name='Actions[4].Dates[0].Date']").IsDisabledAsync());
        }
        else
        {
            Assert.Equal(0, await ActionsRoot(page).Locator("input[name='Actions[4].Dates.index'][value='0']").CountAsync());
        }

        Assert.Equal(new[] { "4", "9" }, await GetKeysAsync(actions));
        Assert.Equal(new[] { "0", "7" }, await GetKeysAsync(ChildCollection(source9)));

        await ParentDuplicate(source4).ClickAsync();

        var copiedAction = ParentRow(page, "10");
        var copiedDates = ChildCollection(copiedAction);
        var copiedDate = DateRow(page, "10", "8");
        Assert.Equal(new[] { "4", "10", "9" }, await GetKeysAsync(actions));
        Assert.Equal(ExpectedAction4DateKeys(mode), await GetKeysAsync(ChildCollection(source4)));
        Assert.Equal(new[] { "0", "7" }, await GetKeysAsync(ChildCollection(source9)));
        Assert.Equal(new[] { "8" }, await GetKeysAsync(copiedDates));
        Assert.Equal("Edited source action", await copiedAction.Locator("input[name='Actions[10].Title']").InputValueAsync());
        Assert.Equal("Changed source kind", await copiedDate.Locator("input[name='Actions[10].Dates[8].Kind']").InputValueAsync());
        Assert.Equal("2026-10-20", await copiedDate.Locator("input[name='Actions[10].Dates[8].Date']").InputValueAsync());
        Assert.Equal("source-owned", await source4.GetAttributeAsync("data-consumer-state"));
        Assert.Equal("source-owned", await copiedAction.GetAttributeAsync("data-consumer-state"));
        Assert.Equal("Actions[10].Title", await ActiveNameAsync(page));
        Assert.Equal("Duplicated action.", await OwnedStatusTextAsync(actions));

        var beforePost = await CaptureFormDataAsync(page);
        Assert.Equal(new[] { "4", "10", "9" }, beforePost.ActionKeys);
        Assert.Equal(ExpectedAction4DateKeys(mode), beforePost.Action4DateKeys);
        Assert.Equal(new[] { "0", "7" }, beforePost.Action9DateKeys);
        Assert.Equal(new[] { "8" }, beforePost.Action10DateKeys);
        Assert.Equal("action-4", beforePost.Action4Id);
        Assert.Equal("version-4", beforePost.Action4RowVersion);
        Assert.Equal("action-9", beforePost.Action9Id);
        Assert.Equal("version-9", beforePost.Action9RowVersion);
        Assert.Equal("date-4-7", beforePost.Action4Date7Id);
        Assert.Equal("date-9-0", beforePost.Action9Date0Id);
        Assert.Equal("date-9-7", beforePost.Action9Date7Id);
        Assert.Equal(string.Empty, beforePost.Action10Id);
        Assert.Equal(string.Empty, beforePost.Action10RowVersion);
        Assert.Equal(string.Empty, beforePost.Action10Date8Id);
        Assert.Null(beforePost.Action10Date8RowVersion);
        Assert.Equal(string.Empty, beforePost.Action10Date8ConcurrencyToken);
        if (mode == "mark")
        {
            Assert.Equal("true", beforePost.Action4Date0Delete);
            Assert.Equal("date-4-0", beforePost.Action4Date0Id);
        }
        else
        {
            Assert.Null(beforePost.Action4Date0Delete);
            Assert.Null(beforePost.Action4Date0Id);
        }

        await copiedDate.Locator("input[name='Actions[10].Dates[8].Date']").FillAsync("not-a-date");
        var invalidResponse = await SubmitAndWaitForResponseAsync(page);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, invalidResponse.Status);
        Assert.Equal("not-a-date", await page.Locator("input[name='Actions[10].Dates[8].Date']").InputValueAsync());
        Assert.Equal(new[] { "4", "10", "9" }, await GetKeysAsync(ActionsRoot(page)));
        Assert.Equal(ExpectedAction4DateKeys(mode), await GetKeysAsync(ChildCollection(ParentRow(page, "4"))));
        Assert.Equal(new[] { "0", "7" }, await GetKeysAsync(ChildCollection(ParentRow(page, "9"))));
        Assert.Equal(new[] { "8" }, await GetKeysAsync(ChildCollection(ParentRow(page, "10"))));

        var afterInvalidPost = await CaptureFormDataAsync(page);
        Assert.Equal(beforePost.ActionKeys, afterInvalidPost.ActionKeys);
        Assert.Equal(beforePost.Action4DateKeys, afterInvalidPost.Action4DateKeys);
        Assert.Equal(beforePost.Action9DateKeys, afterInvalidPost.Action9DateKeys);
        Assert.Equal(beforePost.Action10DateKeys, afterInvalidPost.Action10DateKeys);
        Assert.Equal("action-4", afterInvalidPost.Action4Id);
        Assert.Equal("version-4", afterInvalidPost.Action4RowVersion);
        Assert.Equal("action-9", afterInvalidPost.Action9Id);
        Assert.Equal("version-9", afterInvalidPost.Action9RowVersion);
        Assert.Equal("date-4-7", afterInvalidPost.Action4Date7Id);
        Assert.Equal("date-9-0", afterInvalidPost.Action9Date0Id);
        Assert.Equal("date-9-7", afterInvalidPost.Action9Date7Id);
        Assert.Equal(string.Empty, afterInvalidPost.Action10Id);
        Assert.Equal(string.Empty, afterInvalidPost.Action10RowVersion);
        Assert.Equal(string.Empty, afterInvalidPost.Action10Date8Id);
        Assert.Null(afterInvalidPost.Action10Date8RowVersion);
        Assert.Equal(string.Empty, afterInvalidPost.Action10Date8ConcurrencyToken);

        await page.Locator("input[name='Actions[10].Dates[8].Date']").FillAsync("2026-11-30");
        var validResponse = await SubmitAndWaitForResponseAsync(page);
        Assert.Equal((int)HttpStatusCode.OK, validResponse.Status);
        Assert.Equal("Saved 3 action row(s).", (await page.Locator("[data-nested-result]").InnerTextAsync()).Trim());
        Assert.Equal(new[] { "4", "10", "9" }, await GetKeysAsync(ActionsRoot(page)));
        Assert.Equal(new[] { "8" }, await GetKeysAsync(ChildCollection(ParentRow(page, "10"))));
    }

    [Fact]
    public async Task NestedCollections_NoJavaScript_ServerAddDateCommandsAndOrdinaryPostWork()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            JavaScriptEnabled = false
        });
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{_fixture.BaseUrl}/Reactivity/NestedCollections?mode=physical");
        Assert.Equal(0, await page.Locator("[data-rw-form-interactions-enhanced='true']").CountAsync());
        Assert.Equal(new[] { "4", "9" }, await GetKeysAsync(ActionsRoot(page)));

        var addFirstResponse = await SubmitNamedCommandAsync(page, "addDateFor", "4");
        Assert.Equal((int)HttpStatusCode.OK, addFirstResponse.Status);
        var action4Dates = ChildCollection(ParentRow(page, "4"));
        var action4Keys = await GetKeysAsync(action4Dates);
        Assert.Equal(3, action4Keys.Length);
        Assert.Equal(new[] { "0", "7" }, action4Keys.Take(2).ToArray());
        var newAction4Key = action4Keys[2];
        Assert.False(string.IsNullOrWhiteSpace(newAction4Key));
        Assert.NotEqual("__date_index__", newAction4Key);
        await page.Locator($"input[name='Actions[4].Dates[{newAction4Key}].Kind']").FillAsync("Scheduled");
        await page.Locator($"input[name='Actions[4].Dates[{newAction4Key}].Date']").FillAsync("2026-11-01");

        var addSecondResponse = await SubmitNamedCommandAsync(page, "addDateFor", "9");
        Assert.Equal((int)HttpStatusCode.OK, addSecondResponse.Status);
        Assert.Equal(new[] { "0", "7" }, (await GetKeysAsync(ChildCollection(ParentRow(page, "4")))).Take(2).ToArray());
        var action9Dates = ChildCollection(ParentRow(page, "9"));
        var action9Keys = await GetKeysAsync(action9Dates);
        Assert.Equal(3, action9Keys.Length);
        Assert.Equal(new[] { "0", "7" }, action9Keys.Take(2).ToArray());
        var newAction9Key = action9Keys[2];
        Assert.NotEqual(newAction4Key, newAction9Key);
        await page.Locator($"input[name='Actions[9].Dates[{newAction9Key}].Kind']").FillAsync("Scheduled");
        await page.Locator($"input[name='Actions[9].Dates[{newAction9Key}].Date']").FillAsync("2026-11-02");

        var ordinaryPost = await SubmitAndWaitForResponseAsync(page);
        Assert.Equal((int)HttpStatusCode.OK, ordinaryPost.Status);
        Assert.Equal("Saved 2 action row(s).", (await page.Locator("[data-nested-result]").InnerTextAsync()).Trim());
    }

    [Fact]
    public async Task NestedCollections_ChildCommandsAreOwnerLocal_AndAddedParentKeepsInertLocalTemplateToken()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await OpenNestedCollectionsAsync(page, "physical");

        await page.EvaluateAsync(
            """
            () => {
              document.addEventListener('razorwire:form-collection:add', event => {
                const detail = event.detail;
                window.__nestedCollectionAddProof = {
                  formMatches: detail.form === document.querySelector('form[data-nested-proof]'),
                  rootName: detail.root.getAttribute('data-rw-form-collection'),
                  rowName: detail.row.querySelector('input:not([type=hidden])')?.name ?? '',
                  action: detail.action,
                  previousIndex: detail.previousIndex
                };
              });
            }
            """);

        var source4Dates = ChildCollection(ParentRow(page, "4"));
        await source4Dates.Locator("button[data-rw-form-collection-add]").FocusAsync();
        await page.Keyboard.PressAsync("Enter");

        Assert.Equal(new[] { "0", "7", "8" }, await GetKeysAsync(source4Dates));
        Assert.Equal(new[] { "0", "7" }, await GetKeysAsync(ChildCollection(ParentRow(page, "9"))));
        Assert.Equal("Added date.", await OwnedStatusTextAsync(source4Dates));
        Assert.Equal(string.Empty, await OwnedStatusTextAsync(ActionsRoot(page)));
        Assert.Equal(string.Empty, await OwnedStatusTextAsync(ChildCollection(ParentRow(page, "9"))));
        Assert.Equal("Actions[4].Dates[8].Kind", await ActiveNameAsync(page));
        Assert.Equal("true", await page.Locator("input[name='Actions[4].Dates[8].Date']").GetAttributeAsync("data-date-policy-ready"));

        var addEvent = await page.EvaluateAsync<CollectionEventProbe>("() => window.__nestedCollectionAddProof");
        Assert.True(addEvent.FormMatches);
        Assert.Equal("Actions[4].Dates", addEvent.RootName);
        Assert.Equal("Actions[4].Dates[8].Kind", addEvent.RowName);
        Assert.Equal("add", addEvent.Action);
        Assert.Null(addEvent.PreviousIndex);

        await page.EvaluateAsync(
            """
            () => {
              const template = document.querySelector("section[data-rw-form-collection='Actions'] > template[data-rw-form-collection-template]");
              const title = template?.content.querySelector("input[name='Actions[__index__].Title']");
              if (!(title instanceof HTMLInputElement)) throw new Error('Missing action template title.');
              title.readOnly = true;
              const childAdd = template.content.querySelector('section[data-rw-form-collection] [data-rw-form-collection-add]');
              if (!(childAdd instanceof HTMLElement)) throw new Error('Missing child Add command.');
              childAdd.inert = true;
            }
            """);

        var actionAdd = ActionsRoot(page).Locator(":scope > button[data-rw-form-collection-add]");
        await actionAdd.FocusAsync();
        await page.Keyboard.PressAsync("Space");
        var addedAction = ParentRow(page, "10");
        Assert.Equal(new[] { "4", "9", "10" }, await GetKeysAsync(ActionsRoot(page)));
        Assert.Equal("Added action.", await OwnedStatusTextAsync(ActionsRoot(page)));
        Assert.Equal(string.Empty, await OwnedStatusTextAsync(ChildCollection(addedAction)));
        Assert.Equal("Actions[10].Dates", await ChildCollection(addedAction).GetAttributeAsync("data-rw-form-collection"));
        Assert.True(await page.EvaluateAsync<bool>(
            """
            () => document.activeElement instanceof HTMLButtonElement
              && document.activeElement.hasAttribute('data-rw-form-collection-duplicate')
              && document.activeElement.closest('[data-rw-form-collection-row]') === document.querySelector("input[name='Actions.index'][value='10']")?.parentElement
            """));

        var newParentDates = ChildCollection(addedAction);
        var inertTemplate = await newParentDates.EvaluateAsync<InertTemplateProbe>(
            """
            root => {
              const template = root.querySelector('template[data-rw-form-collection-template]');
              return {
                Collection: root.getAttribute('data-rw-form-collection') ?? '',
                Token: root.getAttribute('data-rw-form-collection-index-token') ?? '',
                TemplateCollectionName: template?.content.querySelector('[name$=".Kind"]')?.getAttribute('name') ?? '',
                TemplateHasLocalToken: template?.innerHTML.includes('__date_index__') ?? false
              };
            }
            """);
        Assert.Equal("Actions[10].Dates", inertTemplate.Collection);
        Assert.Equal("__date_index__", inertTemplate.Token);
        Assert.Contains("Actions[10].Dates[__date_index__]", inertTemplate.TemplateCollectionName, StringComparison.Ordinal);
        Assert.True(inertTemplate.TemplateHasLocalToken);

        await newParentDates.Locator(":scope > button[data-rw-form-collection-add]").EvaluateAsync("button => button.inert = false");
        await newParentDates.Locator(":scope > button[data-rw-form-collection-add]").ClickAsync();
        var newParentDateKeys = await GetKeysAsync(newParentDates);
        Assert.Single(newParentDateKeys);
        Assert.False(string.IsNullOrWhiteSpace(newParentDateKeys[0]));
        Assert.NotEqual("__date_index__", newParentDateKeys[0]);
        Assert.Equal("Actions[10].Dates", await newParentDates.GetAttributeAsync("data-rw-form-collection"));
        Assert.Equal("Added action.", await OwnedStatusTextAsync(ActionsRoot(page)));
        Assert.Equal("Added date.", await OwnedStatusTextAsync(newParentDates));
        Assert.Equal($"Actions[10].Dates[{newParentDateKeys[0]}].Kind", await ActiveNameAsync(page));
    }

    [Fact]
    public async Task NestedCollections_AddDateAfterDuplicateUsesRebasedOwnerAndFreshChildKey()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await OpenNestedCollectionsAsync(page, "physical");

        await DateRow(page, "4", "0").Locator("button[data-rw-form-collection-remove]").ClickAsync();
        await ParentDuplicate(ParentRow(page, "4")).ClickAsync();

        var copiedDates = ChildCollection(ParentRow(page, "10"));
        Assert.Equal(new[] { "8" }, await GetKeysAsync(copiedDates));
        Assert.Equal(new[] { "7" }, await GetKeysAsync(ChildCollection(ParentRow(page, "4"))));
        Assert.Equal(new[] { "0", "7" }, await GetKeysAsync(ChildCollection(ParentRow(page, "9"))));

        await copiedDates.Locator("button[data-rw-form-collection-add]").ClickAsync();

        await page.Locator("input[name='Actions[10].Dates[9].Kind']").FillAsync("Scheduled");
        var probe = await page.EvaluateAsync<DuplicateChildProbe>(
            """
            () => {
              const data = new FormData(document.querySelector('form[data-nested-proof]'));
              const values = name => data.getAll(name).map(String);
              return {
                ParentKeys: values('Actions.index'),
                SourceDateKeys: values('Actions[4].Dates.index'),
                CopiedDateKeys: values('Actions[10].Dates.index'),
                OtherDateKeys: values('Actions[9].Dates.index'),
                AddedDateValue: data.get('Actions[10].Dates[9].Date'),
                AddedDateId: data.get('Actions[10].Dates[9].Id'),
                AddedDateRowVersion: data.get('Actions[10].Dates[9].RowVersion'),
                AddedDateConcurrencyToken: data.get('Actions[10].Dates[9].ConcurrencyToken')
              };
            }
            """);
        Assert.Equal(new[] { "4", "10", "9" }, probe.ParentKeys);
        Assert.Equal(new[] { "7" }, probe.SourceDateKeys);
        Assert.Equal(new[] { "8", "9" }, probe.CopiedDateKeys);
        Assert.Equal(new[] { "0", "7" }, probe.OtherDateKeys);
        Assert.Equal(string.Empty, probe.AddedDateValue);
        Assert.Equal(string.Empty, probe.AddedDateId);
        Assert.Null(probe.AddedDateRowVersion);
        Assert.Equal(string.Empty, probe.AddedDateConcurrencyToken);
        Assert.Equal("Actions[10].Dates[9].Kind", await ActiveNameAsync(page));
        Assert.Equal("Added date.", await OwnedStatusTextAsync(copiedDates));
        Assert.Equal("Duplicated action.", await OwnedStatusTextAsync(ActionsRoot(page)));

        var emptyDateResponse = await SubmitAndWaitForResponseAsync(page);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, emptyDateResponse.Status);
        Assert.Equal(new[] { "4", "10", "9" }, await GetKeysAsync(ActionsRoot(page)));
        Assert.Equal(new[] { "8", "9" }, await GetKeysAsync(ChildCollection(ParentRow(page, "10"))));
        Assert.Equal(string.Empty, await page.Locator("input[name='Actions[10].Dates[9].Date']").InputValueAsync());
        var invalidEmptyDate = await CaptureFormDataAsync(page);
        Assert.Equal("action-4", invalidEmptyDate.Action4Id);
        Assert.Equal("action-9", invalidEmptyDate.Action9Id);
        Assert.Equal(string.Empty, invalidEmptyDate.Action10Date9Id);
        Assert.Null(invalidEmptyDate.Action10Date9RowVersion);
        Assert.Equal(string.Empty, invalidEmptyDate.Action10Date9ConcurrencyToken);

        await page.Locator("input[name='Actions[10].Dates[9].Date']").FillAsync("2026-11-15");
        var validDateResponse = await SubmitAndWaitForResponseAsync(page);
        Assert.Equal((int)HttpStatusCode.OK, validDateResponse.Status);
        Assert.Equal("Saved 3 action row(s).", (await page.Locator("[data-nested-result]").InnerTextAsync()).Trim());
    }

    [Theory]
    [InlineData("tenant:α/4", "date:β/7")]
    [InlineData("tenant'\"\\4", "date'\"\\7")]
    [InlineData("__index__", "__date_index__")]
    [InlineData("tenant].slot", "date].slot")]
    public async Task NestedCollections_MvcBoundaryAcceptsOpaqueExplicitIndexKeysWithoutRekeying(string opaqueActionKey, string opaqueDateKey)
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await OpenNestedCollectionsAsync(page, "physical");

        await page.EvaluateAsync(
            """
            ({ opaqueActionKey, opaqueDateKey }) => {
              const form = document.querySelector('form[data-nested-proof]');
              const actionRow = form.querySelector("input[name='Actions.index'][value='4']")
                ?.closest('[data-rw-form-collection-row]');
              const dateRoot = actionRow?.querySelector(':scope > section[data-rw-form-collection]');
              const dateRow = dateRoot?.querySelector("input[name='Actions[4].Dates.index'][value='7']")
                ?.closest('[data-rw-form-collection-row]');
              if (!actionRow || !dateRoot || !dateRow) throw new Error('Missing source rows for opaque-key MVC proof.');

              const oldDatePrefix = 'Actions[4].Dates[7]';
              const newDatePrefix = `Actions[${opaqueActionKey}].Dates[${opaqueDateKey}]`;
              const oldActionPrefix = 'Actions[4]';
              const newActionPrefix = `Actions[${opaqueActionKey}]`;
              for (const element of form.querySelectorAll('[name]')) {
                element.name = element.name.replaceAll(oldDatePrefix, newDatePrefix).replaceAll(oldActionPrefix, newActionPrefix);
              }
              for (const element of form.querySelectorAll('[data-valmsg-for]')) {
                element.setAttribute('data-valmsg-for', element.getAttribute('data-valmsg-for')
                  .replaceAll(oldDatePrefix, newDatePrefix).replaceAll(oldActionPrefix, newActionPrefix));
              }
              dateRoot.setAttribute('data-rw-form-collection', `${newActionPrefix}.Dates`);
              actionRow.setAttribute('data-rw-form-index', opaqueActionKey);
              dateRow.setAttribute('data-rw-form-index', opaqueDateKey);
              const parentMarker = actionRow.querySelector("input[name='Actions.index']");
              parentMarker.value = opaqueActionKey;
              parentMarker.setAttribute('value', opaqueActionKey);
              const dateMarker = [...dateRow.querySelectorAll('input')]
                .find(input => input.name === `${newActionPrefix}.Dates.index`);
              dateMarker.value = opaqueDateKey;
              dateMarker.setAttribute('value', opaqueDateKey);
            }
            """,
            new { opaqueActionKey, opaqueDateKey });

        var response = await SubmitAndWaitForResponseAsync(page);
        var responseBody = response.Status >= StatusCodes.Status500InternalServerError
            ? await response.TextAsync()
            : string.Empty;
        Assert.True(
            response.Status == (int)HttpStatusCode.OK,
            $"Expected the MVC boundary to accept opaque explicit-index keys, received HTTP {response.Status}. Response: {responseBody}");
        Assert.Equal("Saved 2 action row(s).", (await page.Locator("[data-nested-result]").InnerTextAsync()).Trim());
        Assert.Equal(new[] { opaqueActionKey, "9" }, await GetKeysAsync(ActionsRoot(page)));
        Assert.Equal(new[] { "0", opaqueDateKey }, await GetKeysAsync(ChildCollection(ParentRow(page, opaqueActionKey))));
        Assert.Equal(new[] { "0", "7" }, await GetKeysAsync(ChildCollection(ParentRow(page, "9"))));
    }

    [Fact]
    public async Task NestedCollections_RecursiveDuplicateRebasesThirdLevelAndKeepsItsLocalToken()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await OpenNestedCollectionsAsync(page, "physical");

        await AddReminderCollectionAsync(page, "4", "7");
        var sourceReminderRoot = ReminderRoot(DateRow(page, "4", "7"));
        Assert.Equal("Actions[4].Dates[7].Reminders", await sourceReminderRoot.GetAttributeAsync("data-rw-form-collection"));
        Assert.Equal(new[] { "3" }, await GetKeysAsync(sourceReminderRoot));

        await ParentDuplicate(ParentRow(page, "4")).ClickAsync();

        Assert.Equal(new[] { "8", "9" }, await GetKeysAsync(ChildCollection(ParentRow(page, "10"))));
        var copiedReminderRoot = ReminderRoot(DateRow(page, "10", "9"));
        Assert.Equal("Actions[10].Dates[9].Reminders", await copiedReminderRoot.GetAttributeAsync("data-rw-form-collection"));
        Assert.Equal(new[] { "4" }, await GetKeysAsync(copiedReminderRoot));
        Assert.Equal("Source reminder", await copiedReminderRoot.Locator("input[name='Actions[10].Dates[9].Reminders[4].Message']").InputValueAsync());
        Assert.Equal(new[] { "3" }, await GetKeysAsync(sourceReminderRoot));

        var localTemplate = await copiedReminderRoot.EvaluateAsync<ReminderTemplateProbe>(
            """
            root => {
              const template = root.querySelector('template[data-rw-form-collection-template]');
              return {
                Root: root.getAttribute('data-rw-form-collection') ?? '',
                Token: root.getAttribute('data-rw-form-collection-index-token') ?? '',
                MessageName: template?.content.querySelector('input[name$=".Message"]')?.name ?? '',
                MessageId: template?.content.querySelector('input[name$=".Message"]')?.id ?? '',
                HelpId: template?.content.querySelector('[data-reminder-help]')?.id ?? '',
                TemplateLabelFor: template?.content.querySelector('label')?.getAttribute('for') ?? '',
                TemplateDescribedBy: template?.content.querySelector('input[name$=".Message"]')?.getAttribute('aria-describedby') ?? '',
                HasLocalToken: template?.innerHTML.includes('__reminder_index__') ?? false
              };
            }
            """);
        Assert.Equal("Actions[10].Dates[9].Reminders", localTemplate.Root);
        Assert.Equal("__reminder_index__", localTemplate.Token);
        Assert.Equal("Actions[10].Dates[9].Reminders[__reminder_index__].Message", localTemplate.MessageName);
        Assert.False(string.IsNullOrWhiteSpace(localTemplate.MessageId));
        Assert.False(string.IsNullOrWhiteSpace(localTemplate.HelpId));
        Assert.NotEqual(localTemplate.MessageId, localTemplate.HelpId);
        Assert.Equal(localTemplate.MessageId, localTemplate.TemplateLabelFor);
        Assert.Equal(localTemplate.HelpId, localTemplate.TemplateDescribedBy);
        Assert.True(localTemplate.HasLocalToken);

        var copiedLiveIds = await copiedReminderRoot.Locator("fieldset[data-rw-form-collection-row][data-rw-form-index='4']")
            .EvaluateAsync<DeepFragmentIdProbe>(
                """
                row => ({
                  MessageId: row.querySelector('input[name$=".Message"]')?.id ?? '',
                  LabelFor: row.querySelector('label')?.getAttribute('for') ?? '',
                  HelpId: row.querySelector('[data-reminder-help]')?.id ?? '',
                  DescribedBy: row.querySelector('input[name$=".Message"]')?.getAttribute('aria-describedby') ?? ''
                })
                """);
        Assert.False(string.IsNullOrWhiteSpace(copiedLiveIds.MessageId));
        Assert.Equal(copiedLiveIds.MessageId, copiedLiveIds.LabelFor);
        Assert.False(string.IsNullOrWhiteSpace(copiedLiveIds.HelpId));
        Assert.NotEqual(copiedLiveIds.MessageId, copiedLiveIds.HelpId);
        Assert.Equal(copiedLiveIds.HelpId, copiedLiveIds.DescribedBy);
        Assert.NotEqual(localTemplate.MessageId, copiedLiveIds.MessageId);
        Assert.NotEqual(localTemplate.HelpId, copiedLiveIds.HelpId);

        await copiedReminderRoot.Locator("button[data-rw-form-collection-add]").ClickAsync();
        Assert.Equal(new[] { "4", "5" }, await GetKeysAsync(copiedReminderRoot));
        Assert.Equal(string.Empty, await copiedReminderRoot.Locator("input[name='Actions[10].Dates[9].Reminders[5].Message']").InputValueAsync());
        var addedDeepFragmentIds = await copiedReminderRoot.Locator("fieldset[data-rw-form-collection-row][data-rw-form-index='5']")
            .EvaluateAsync<DeepFragmentIdProbe>(
                """
                row => ({
                  MessageId: row.querySelector('input[name$=".Message"]')?.id ?? '',
                  LabelFor: row.querySelector('label')?.getAttribute('for') ?? '',
                  HelpId: row.querySelector('[data-reminder-help]')?.id ?? '',
                  DescribedBy: row.querySelector('input[name$=".Message"]')?.getAttribute('aria-describedby') ?? ''
                })
                """);
        Assert.False(string.IsNullOrWhiteSpace(addedDeepFragmentIds.MessageId));
        Assert.Equal(addedDeepFragmentIds.MessageId, addedDeepFragmentIds.LabelFor);
        Assert.False(string.IsNullOrWhiteSpace(addedDeepFragmentIds.HelpId));
        Assert.NotEqual(addedDeepFragmentIds.MessageId, addedDeepFragmentIds.HelpId);
        Assert.Equal(addedDeepFragmentIds.HelpId, addedDeepFragmentIds.DescribedBy);
        Assert.NotEqual(copiedLiveIds.MessageId, addedDeepFragmentIds.MessageId);
        Assert.NotEqual(copiedLiveIds.HelpId, addedDeepFragmentIds.HelpId);
        Assert.Equal("Actions[10].Dates[9].Reminders[5].Message", await ActiveNameAsync(page));
    }

    [Fact]
    public async Task NestedCollections_DuplicateClearsLiveFileSelectionAndPreservesSourceFile()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await OpenNestedCollectionsAsync(page, "physical");

        await DateRow(page, "4", "7").EvaluateAsync(
            """
            row => {
              const file = document.createElement('input');
              file.type = 'file';
              file.name = 'Actions[4].Dates[7].Attachment';
              file.id = 'Actions_4__Dates_7__attachment';
              file.setAttribute('data-consumer-upload', 'source');
              row.append(file);
            }
            """);
        await page.Locator("input[name='Actions[4].Dates[7].Attachment']").SetInputFilesAsync(
        [
            new FilePayload
            {
                Name = "nested-proof.txt",
                MimeType = "text/plain",
                Buffer = Encoding.UTF8.GetBytes("selected in the source row")
            }
        ]);

        await ParentDuplicate(ParentRow(page, "4")).ClickAsync();

        var sourceFile = page.Locator("input[name='Actions[4].Dates[7].Attachment']");
        var copiedDateKeys = await GetKeysAsync(ChildCollection(ParentRow(page, "10")));
        Assert.Equal(new[] { "8", "9" }, copiedDateKeys);
        var copiedDateIndex = copiedDateKeys[^1];
        var copiedFile = DateRow(page, "10", copiedDateIndex).Locator("input[type='file']");
        Assert.Equal($"Actions[10].Dates[{copiedDateIndex}].Attachment", await copiedFile.GetAttributeAsync("name"));
        Assert.Equal(1, await sourceFile.EvaluateAsync<int>("input => input.files?.length ?? 0"));
        Assert.Equal("nested-proof.txt", await sourceFile.EvaluateAsync<string>("input => input.files?.[0]?.name ?? ''"));
        Assert.Equal(0, await copiedFile.EvaluateAsync<int>("input => input.files?.length ?? 0"));
        Assert.Equal("source", await copiedFile.GetAttributeAsync("data-consumer-upload"));
        Assert.Equal($"Actions_10__Dates_{copiedDateIndex}__attachment", await copiedFile.GetAttributeAsync("id"));
    }

    [Fact]
    public async Task NestedCollections_DuplicateRemapsSupportedIdReferencesAndReservesSecondFormIds()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await OpenNestedCollectionsAsync(page, "physical");

        await page.EvaluateAsync(
            """
            () => {
              const sourceRow = document.querySelector("input[name='Actions[4].Dates.index'][value='7']")
                ?.closest('[data-rw-form-collection-row]');
              if (!sourceRow) throw new Error('Missing source date row.');
              const prefix = 'Actions_4__Dates_7__ref-';
              const roles = {
                control: `${prefix}control`, list: `${prefix}list`, error: `${prefix}error`,
                panel: `${prefix}panel`, description: `${prefix}description`, label: `${prefix}label`,
                owned: `${prefix}owned`, output: `${prefix}output`, references: `${prefix}references`
              };
              const list = document.createElement('datalist');
              list.id = roles.list;
              const error = document.createElement('span');
              error.id = roles.error;
              const panel = document.createElement('div');
              panel.id = roles.panel;
              const description = document.createElement('span');
              description.id = roles.description;
              const label = document.createElement('label');
              label.id = roles.label;
              label.htmlFor = roles.control;
              label.textContent = 'Reference probe';
              const owned = document.createElement('span');
              owned.id = roles.owned;
              const control = document.createElement('input');
              control.id = roles.control;
              control.name = 'Actions[4].Dates[7].ReferenceProbe';
              control.setAttribute('list', roles.list);
              control.setAttribute('aria-errormessage', roles.error);
              const output = document.createElement('output');
              output.id = roles.output;
              output.setAttribute('for', `external-before ${roles.label} external-after ${roles.owned}`);
              const references = document.createElement('div');
              references.id = roles.references;
              references.setAttribute('aria-controls', `external-after ${roles.panel} external-before`);
              references.setAttribute('aria-describedby', `${roles.description} external-before ${roles.error}`);
              references.setAttribute('aria-labelledby', `external-after ${roles.label} external-before`);
              references.setAttribute('aria-owns', `${roles.label} external-before ${roles.owned}`);
              for (const node of [list, error, panel, description, label, owned, control, output, references]) {
                node.dataset.idReferenceRole = Object.keys(roles).find(key => roles[key] === node.id) ?? '';
                sourceRow.append(node);
              }

              for (const id of ['external-before', 'external-after']) {
                const external = document.createElement('span');
                external.id = id;
                document.body.append(external);
              }

              const secondForm = document.createElement('form');
              secondForm.id = 'nested-reference-collision-form';
              const collision = document.createElement('span');
              collision.id = 'Actions_10__Dates_9__ref-control';
              secondForm.append(collision);
              document.body.append(secondForm);
            }
            """);

        await ParentDuplicate(ParentRow(page, "4")).ClickAsync();

        var copiedReferences = await DateRow(page, "10", "9").EvaluateAsync<IdReferenceProbe>(
            """
            row => {
              const byRole = role => row.querySelector(`[data-id-reference-role="${role}"]`);
              const references = byRole('references');
              const output = byRole('output');
              return {
                ControlId: byRole('control')?.id ?? '',
                ListId: byRole('list')?.id ?? '',
                ErrorId: byRole('error')?.id ?? '',
                PanelId: byRole('panel')?.id ?? '',
                DescriptionId: byRole('description')?.id ?? '',
                LabelId: byRole('label')?.id ?? '',
                OwnedId: byRole('owned')?.id ?? '',
                OutputFor: output?.getAttribute('for') ?? '',
                LabelFor: byRole('label')?.getAttribute('for') ?? '',
                ListReference: byRole('control')?.getAttribute('list') ?? '',
                ErrorReference: byRole('control')?.getAttribute('aria-errormessage') ?? '',
                Controls: references?.getAttribute('aria-controls') ?? '',
                DescribedBy: references?.getAttribute('aria-describedby') ?? '',
                LabelledBy: references?.getAttribute('aria-labelledby') ?? '',
                Owns: references?.getAttribute('aria-owns') ?? '',
                SourceControlId: document.querySelector("input[name='Actions[4].Dates[7].ReferenceProbe']")
                  ?.closest('[data-rw-form-collection-row]')?.querySelector('[data-id-reference-role="control"]')?.id ?? '',
                CollisionStillPresent: document.querySelectorAll('#nested-reference-collision-form #Actions_10__Dates_9__ref-control').length === 1
              };
            }
            """);

        Assert.NotEqual("Actions_10__Dates_9__ref-control", copiedReferences.ControlId);
        Assert.True(copiedReferences.CollisionStillPresent);
        Assert.Equal(copiedReferences.ControlId, copiedReferences.LabelFor);
        Assert.Equal(copiedReferences.ListId, copiedReferences.ListReference);
        Assert.Equal(copiedReferences.ErrorId, copiedReferences.ErrorReference);
        Assert.Equal(
            $"external-before {copiedReferences.LabelId} external-after {copiedReferences.OwnedId}",
            copiedReferences.OutputFor);
        Assert.Equal("external-after " + copiedReferences.PanelId + " external-before", copiedReferences.Controls);
        Assert.Equal(copiedReferences.DescriptionId + " external-before " + copiedReferences.ErrorId, copiedReferences.DescribedBy);
        Assert.Equal("external-after " + copiedReferences.LabelId + " external-before", copiedReferences.LabelledBy);
        Assert.Equal(copiedReferences.LabelId + " external-before " + copiedReferences.OwnedId, copiedReferences.Owns);
        Assert.Equal("Actions_4__Dates_7__ref-control", copiedReferences.SourceControlId);

        var remappedIds = new[]
        {
            copiedReferences.ControlId, copiedReferences.ListId, copiedReferences.ErrorId,
            copiedReferences.PanelId, copiedReferences.DescriptionId, copiedReferences.LabelId,
            copiedReferences.OwnedId
        };
        Assert.Equal(remappedIds.Length, remappedIds.Distinct(StringComparer.Ordinal).Count());
        foreach (var id in remappedIds)
        {
            Assert.Equal(1, await page.Locator($"[id='{id}']").CountAsync());
        }
    }

    [Theory]
    [InlineData("physical", "css-hidden")]
    [InlineData("mark", "inert")]
    [InlineData("physical", "disabled-fieldset")]
    public async Task NestedCollections_RemoveSkipsIneligibleFocusTargetsAndFallsBackToOwnAdd(string mode, string blocker)
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await OpenNestedCollectionsAsync(page, mode);

        var dates = ChildCollection(ParentRow(page, "4"));
        var inaccessibleSibling = DateRow(page, "4", "7");
        await inaccessibleSibling.EvaluateAsync(
            """
            (row, blocker) => {
              if (blocker === 'css-hidden') row.style.display = 'none';
              else if (blocker === 'inert') row.inert = true;
              else row.disabled = true;
            }
            """,
            blocker);

        var firstRow = DateRow(page, "4", "0");
        await firstRow.Locator("button[data-rw-form-collection-remove]").ClickAsync();
        Assert.True(await page.EvaluateAsync<bool>(
            """
            () => document.activeElement === document.querySelector("section[data-rw-form-collection='Actions[4].Dates'] > button[data-rw-form-collection-add]")
            """));
        Assert.Equal(mode == "mark" ? "Marked date for removal." : "Removed date.", await OwnedStatusTextAsync(dates));
        Assert.Equal(string.Empty, await OwnedStatusTextAsync(ActionsRoot(page)));

        await inaccessibleSibling.EvaluateAsync(
            """
            row => {
              row.style.display = '';
              row.inert = false;
              row.disabled = false;
            }
            """);
        await inaccessibleSibling.Locator("button[data-rw-form-collection-remove]").ClickAsync();
        Assert.True(await page.EvaluateAsync<bool>(
            """
            () => document.activeElement === document.querySelector("section[data-rw-form-collection='Actions[4].Dates'] > button[data-rw-form-collection-add]")
            """));
        Assert.Equal(mode == "mark" ? "Marked date for removal." : "Removed date.", await OwnedStatusTextAsync(dates));
    }

    [Theory]
    [InlineData("physical")]
    [InlineData("mark")]
    public async Task NestedCollections_RemoveFocusesNextThenPreviousOwnedLiveRow(string mode)
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await OpenNestedCollectionsAsync(page, mode);
        var dates = ChildCollection(ParentRow(page, "4"));
        await dates.Locator(":scope > button[data-rw-form-collection-add]").ClickAsync();
        await DateRow(page, "4", "0").Locator("button[data-rw-form-collection-remove]").ClickAsync();
        Assert.True(await DateRow(page, "4", "7").EvaluateAsync<bool>("row => row.contains(document.activeElement)"));
        await DateRow(page, "4", "8").Locator("button[data-rw-form-collection-remove]").ClickAsync();
        Assert.True(await DateRow(page, "4", "7").EvaluateAsync<bool>("row => row.contains(document.activeElement)"));
        Assert.Equal(string.Empty, await OwnedStatusTextAsync(ActionsRoot(page)));
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("inert")]
    [InlineData("css-hidden")]
    [InlineData("disabled-fieldset")]
    public async Task NestedCollections_IneligibleCommandCannotMutateOwner(string blocker)
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await OpenNestedCollectionsAsync(page, "physical");
        var dates = ChildCollection(ParentRow(page, "4"));
        await dates.EvaluateAsync(
            """
            (root, blocker) => {
              const owner = root.closest('[data-rw-form-collection-row]');
              if (blocker === 'hidden') owner.hidden = true;
              else if (blocker === 'inert') owner.inert = true;
              else if (blocker === 'css-hidden') owner.style.display = 'none';
              else owner.disabled = true;
              root.querySelector(':scope > button[data-rw-form-collection-add]')
                .dispatchEvent(new MouseEvent('click', { bubbles: true }));
            }
            """, blocker);
        Assert.Equal(new[] { "0", "7" }, await GetKeysAsync(dates));
        Assert.Equal(string.Empty, await OwnedStatusTextAsync(dates));
    }

    [Fact]
    public async Task NestedCollections_MalformedChildPrefixPayloadReturnsBadRequest()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await OpenNestedCollectionsAsync(page, "physical");
        await DateRow(page, "4", "7").EvaluateAsync(
            """
            row => {
              for (const input of row.querySelectorAll('[name]')) {
                if (input.name.startsWith('Actions[4].Dates[7].'))
                  input.name = input.name.replace('Actions[4].Dates[7].', 'Actions[4].Dates[missing].');
              }
            }
            """);
        var response = await SubmitAndWaitForResponseAsync(page);
        Assert.Equal(StatusCodes.Status400BadRequest, response.Status);
        Assert.Contains("Date markers", await response.TextAsync());
    }

    [Fact]
    public async Task NestedCollections_CancelAndListenerInterferenceRespectPublicBeforeEventBoundary()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await OpenNestedCollectionsAsync(page, "physical");

        await page.EvaluateAsync(
            """
            () => {
              const root = document.querySelector("section[data-rw-form-collection='Actions']");
              root.addEventListener('razorwire:form-collection:before-duplicate', event => {
                const detail = event.detail;
                window.__nestedBeforeDuplicateProof = {
                  formMatches: detail.form === document.querySelector('form[data-nested-proof]'),
                  rootName: detail.root.getAttribute('data-rw-form-collection'),
                  action: detail.action,
                  previousIndex: detail.previousIndex,
                  cancelable: event.cancelable,
                  rowDetachedBeforeCommit: !detail.row.isConnected
                };
                event.preventDefault();
              }, { once: true });
            }
            """);

        var source4 = ParentRow(page, "4");
        await ParentDuplicate(source4).ClickAsync();
        Assert.Equal(new[] { "4", "9" }, await GetKeysAsync(ActionsRoot(page)));
        var cancelProof = await page.EvaluateAsync<BeforeEventProbe>("() => window.__nestedBeforeDuplicateProof");
        Assert.True(cancelProof.FormMatches);
        Assert.Equal("Actions", cancelProof.RootName);
        Assert.Equal("duplicate", cancelProof.Action);
        Assert.Equal("4", cancelProof.PreviousIndex);
        Assert.True(cancelProof.Cancelable);
        Assert.True(cancelProof.RowDetachedBeforeCommit);
        Assert.Equal(string.Empty, await OwnedStatusTextAsync(ActionsRoot(page)));

        await page.EvaluateAsync(
            """
            () => {
              const root = document.querySelector("section[data-rw-form-collection='Actions']");
              root.addEventListener('razorwire:form-collection:before-duplicate', event => {
                const detail = event.detail;
                const marker = detail.root.querySelector("input[name='Actions.index'][value='4']");
                marker.value = detail.index;
                marker.setAttribute('value', detail.index);
                marker.closest('[data-rw-form-collection-row]').setAttribute('data-rw-form-index', detail.index);
                window.__nestedInterferenceIndex = detail.index;
              }, { once: true });
            }
            """);

        await ParentDuplicate(source4).ClickAsync();
        var reservedIndex = await page.EvaluateAsync<string>("() => window.__nestedInterferenceIndex");
        Assert.Equal(new[] { reservedIndex, "9" }, await GetKeysAsync(ActionsRoot(page)));
        var movedSource = ParentRow(page, reservedIndex);
        Assert.Equal(reservedIndex, await movedSource.GetAttributeAsync("data-rw-form-index"));
        Assert.Equal(2, await ActionsRoot(page).Locator(":scope > fieldset[data-rw-form-collection-row]").CountAsync());
        var diagnostics = await page.EvaluateAsync<string[]>(
            "() => window.RazorWire.formInteractionsManager.getDiagnostics().map(diagnostic => diagnostic.message)");
        Assert.Contains(diagnostics, message => message.Contains("ownership changed", StringComparison.Ordinal));
        Assert.Equal(string.Empty, await OwnedStatusTextAsync(ActionsRoot(page)));
    }

    [Fact]
    public async Task NestedCollections_RemovedAncestorCannotBeReenabledByNestedToggleRefresh()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await OpenNestedCollectionsAsync(page, "mark");

        await ChildCollection(ParentRow(page, "4")).EvaluateAsync(
            """
            target => {
              target.setAttribute('data-rw-form-toggle-target', 'ancestor-proof');
              target.setAttribute('data-rw-form-toggle-disable-when-hidden', 'true');
              const toggle = document.createElement('input');
              toggle.type = 'checkbox';
              toggle.checked = true;
              toggle.setAttribute('data-rw-form-toggle', 'ancestor-proof');
              target.parentElement.insertBefore(toggle, target);
              window.__ancestorProofToggle = toggle;
            }
            """);
        await page.EvaluateAsync("() => window.RazorWire.formInteractionsManager.scan()");

        var childKind = page.Locator("input[name='Actions[4].Dates[0].Kind']");
        Assert.False(await childKind.IsDisabledAsync());
        var parent = ParentRow(page, "4");
        await parent.Locator(":scope > button[data-rw-form-collection-remove]").ClickAsync();
        Assert.Equal("removed", await parent.GetAttributeAsync("data-rw-form-collection-row-state"));
        Assert.True(await childKind.IsDisabledAsync());

        await page.EvaluateAsync(
            """
            () => {
              const toggle = window.__ancestorProofToggle;
              toggle.checked = true;
              toggle.dispatchEvent(new Event('change', { bubbles: true }));
            }
            """);

        Assert.True(await childKind.IsDisabledAsync());
        Assert.Equal("true", await parent.Locator("input[name='Actions[4].Delete']").InputValueAsync());
    }

    [Fact]
    public async Task NestedCollections_TurboReplacementScansNewFormAndKeepsManagerLive()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await OpenNestedCollectionsAsync(page, "physical");
        Assert.Equal(1, await page.Locator("script[src*='/_content/ForgeTrust.RazorWire/razorwire/turbo.es2017-umd.js']").CountAsync());

        await page.EvaluateAsync(
            """
            () => {
              window.__nestedOldForm = document.querySelector('form[data-nested-proof]');
              window.__nestedManager = window.RazorWire.formInteractionsManager;
              const link = document.createElement('a');
              link.id = 'nested-turbo-away';
              link.href = '/Reactivity/DeterministicRuntime?state=first';
              link.textContent = 'Visit runtime sample';
              document.body.append(link);
            }
            """);
        await page.Locator("#nested-turbo-away").ClickAsync();
        await page.WaitForFunctionAsync("() => window.location.pathname === '/Reactivity/DeterministicRuntime'");

        await page.EvaluateAsync(
            """
            () => {
              const link = document.createElement('a');
              link.id = 'nested-turbo-back';
              link.href = '/Reactivity/NestedCollections?mode=physical&replacement=second';
              link.textContent = 'Return to nested sample';
              document.body.append(link);
            }
            """);
        await page.Locator("#nested-turbo-back").ClickAsync();
        await page.WaitForFunctionAsync(
            "() => window.location.pathname === '/Reactivity/NestedCollections' && window.location.search.includes('replacement=second')");
        await page.WaitForFunctionAsync(
            """
            () => document.querySelector('form[data-nested-proof][data-rw-form-interactions-enhanced="true"]') !== null
              && window.RazorWire?.formInteractionsManager === window.__nestedManager
              && window.__nestedOldForm?.isConnected === false
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        Assert.Equal(new[] { "4", "9" }, await GetKeysAsync(ActionsRoot(page)));
        await ChildCollection(ParentRow(page, "4")).Locator("button[data-rw-form-collection-add]").ClickAsync();
        Assert.Equal(new[] { "0", "7", "8" }, await GetKeysAsync(ChildCollection(ParentRow(page, "4"))));
    }

    [Fact]
    public async Task NestedCollections_ResponsiveHostKeepsTargetsReadableAndContrastAccessible()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 }
        });
        var page = await context.NewPageAsync();
        await OpenNestedCollectionsAsync(page, "physical");

        await page.EvaluateAsync(
            """
            () => {
              const form = document.querySelector('form[data-nested-proof]');
              form.style.backgroundColor = '#ffffff';
              const label = form.querySelector('label');
              label.append(` — ${'Long action label for a nested collection '.repeat(8)}`);
              const error = form.querySelector('[data-valmsg-for]');
              error.className = 'field-validation-error';
              error.textContent = 'A very long validation message without spaces '.repeat(24) + 'x'.repeat(180);
            }
            """);

        foreach (var width in new[] { 320, 768, 1280 })
        {
            await page.SetViewportSizeAsync(width, 900);
            var layout = await page.EvaluateAsync<LayoutProbe>(
                """
                () => {
                  const form = document.querySelector('form[data-nested-proof]');
                  const error = form.querySelector('.field-validation-error');
                  const input = form.querySelector('.nested-field');
                  const rect = form.getBoundingClientRect();
                  const errorRect = error.getBoundingClientRect();
                  const visibleButtons = [...form.querySelectorAll('button')]
                    .filter(button => button.getClientRects().length > 0);
                  const contrast = (foreground, background) => {
                    const channel = value => {
                      const normalized = value / 255;
                      return normalized <= 0.04045 ? normalized / 12.92 : ((normalized + 0.055) / 1.055) ** 2.4;
                    };
                    const luminance = color => {
                      const values = color.match(/[0-9.]+/g).slice(0, 3).map(Number);
                      return 0.2126 * channel(values[0]) + 0.7152 * channel(values[1]) + 0.0722 * channel(values[2]);
                    };
                    const first = luminance(foreground);
                    const second = luminance(background);
                    return (Math.max(first, second) + 0.05) / (Math.min(first, second) + 0.05);
                  };
                  const fieldStyle = getComputedStyle(input);
                  const errorStyle = getComputedStyle(error);
                  return {
                    ViewportWidth: window.innerWidth,
                    DocumentWidth: document.documentElement.scrollWidth,
                    FormRight: rect.right,
                    ErrorRight: errorRect.right,
                    ButtonHeights: visibleButtons.map(button => button.getBoundingClientRect().height),
                    FieldContrast: contrast(fieldStyle.color, fieldStyle.backgroundColor),
                    ErrorContrast: contrast(errorStyle.color, 'rgb(255, 255, 255)'),
                    ErrorTextLength: error.textContent.length
                  };
                }
                """);

            Assert.Equal(width, layout.ViewportWidth);
            Assert.True(layout.DocumentWidth <= width, $"The document overflows at {width}px: {layout.DocumentWidth}px.");
            Assert.True(layout.FormRight <= width, $"The nested form extends beyond the {width}px viewport.");
            Assert.True(layout.ErrorRight <= width, $"The long validation message extends beyond the {width}px viewport.");
            Assert.NotEmpty(layout.ButtonHeights);
            Assert.All(layout.ButtonHeights, height => Assert.True(height >= 44, $"A command target was only {height}px high at {width}px."));
            Assert.True(layout.FieldContrast >= 4.5, $"Field contrast was {layout.FieldContrast:0.00}:1.");
            Assert.True(layout.ErrorContrast >= 4.5, $"Validation contrast was {layout.ErrorContrast:0.00}:1.");
            Assert.True(layout.ErrorTextLength >= 180);
        }
    }

    private static ILocator ReminderRoot(ILocator dateRow) => dateRow.Locator("section[data-rw-form-collection]");

    private static async Task AddReminderCollectionAsync(IPage page, string actionIndex, string dateIndex)
    {
        await page.EvaluateAsync(
            """
            async ({ actionIndex, dateIndex }) => {
              const selector = `input[name="Actions[${actionIndex}].Dates.index"][value="${dateIndex}"]`;
              const dateRow = document.querySelector(selector)?.closest('[data-rw-form-collection-row]');
              if (!dateRow) throw new Error('Missing date owner for third-level collection.');
              const collection = `Actions[${actionIndex}].Dates[${dateIndex}].Reminders`;
              dateRow.insertAdjacentHTML('beforeend', `
                <section data-rw-form-collection="${collection}" data-rw-form-collection-index-token="__reminder_index__" data-rw-form-collection-label="reminder">
                  <fieldset data-rw-form-collection-row="true" data-rw-form-index="3">
                    <legend>Reminder</legend>
                    <input type="hidden" name="${collection}.index" value="3">
                    <input type="hidden" name="${collection}[3].ClientIndex" value="3">
                    <label for="${collection.replaceAll('[', '_').replaceAll(']', '_')}_3__Message">Reminder message</label>
                    <input id="${collection.replaceAll('[', '_').replaceAll(']', '_')}_3__Message" name="${collection}[3].Message" aria-describedby="${collection.replaceAll('[', '_').replaceAll(']', '_')}_3__Help" value="Source reminder">
                    <span data-reminder-help="true" id="${collection.replaceAll('[', '_').replaceAll(']', '_')}_3__Help">Reminder help</span>
                  </fieldset>
                  <template data-rw-form-collection-template="true">
                    <fieldset data-rw-form-collection-row="true" data-rw-form-index="__reminder_index__">
                      <legend>Reminder</legend>
                      <input type="hidden" name="${collection}.index" value="__reminder_index__">
                      <input type="hidden" name="${collection}[__reminder_index__].ClientIndex" value="__reminder_index__">
                      <label for="${collection.replaceAll('[', '_').replaceAll(']', '_')}___reminder_index____Message">Reminder message</label>
                      <input id="${collection.replaceAll('[', '_').replaceAll(']', '_')}___reminder_index____Message" name="${collection}[__reminder_index__].Message" aria-describedby="${collection.replaceAll('[', '_').replaceAll(']', '_')}___reminder_index____Help" value="">
                      <span data-reminder-help="true" id="${collection.replaceAll('[', '_').replaceAll(']', '_')}___reminder_index____Help">Reminder help</span>
                    </fieldset>
                  </template>
                  <button type="button" data-rw-form-collection-add="true">Add reminder</button>
                  <span data-rw-form-collection-status="true" role="status" aria-live="polite"></span>
                </section>`);
              await window.RazorWire.formInteractionsManager.scan();
            }
            """,
            new { actionIndex, dateIndex });
    }

    private static ILocator ActionsRoot(IPage page) => page.Locator("section[data-rw-form-collection='Actions']");

    private static ILocator ParentRow(IPage page, string index) =>
        page.Locator($"input[name='Actions.index'][value='{EscapeCssAttribute(index)}']").Locator("xpath=..");

    private static string EscapeCssAttribute(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    private static ILocator ParentDuplicate(ILocator parentRow) =>
        parentRow.Locator(":scope > button[data-rw-form-collection-duplicate]");

    private static ILocator ChildCollection(ILocator actionRow) => actionRow.Locator(":scope > section[data-rw-form-collection]");

    private static ILocator DateRow(IPage page, string actionIndex, string dateIndex) =>
        page.Locator($"input[name='Actions[{EscapeCssAttribute(actionIndex)}].Dates.index'][value='{EscapeCssAttribute(dateIndex)}']").Locator("xpath=..");

    private static string[] ExpectedAction4DateKeys(string mode) => mode == "mark" ? ["0", "7"] : ["7"];

    private static async Task<string[]> GetKeysAsync(ILocator root) => await root.EvaluateAsync<string[]>(
        """
        root => {
          const collection = root.getAttribute('data-rw-form-collection');
          return [...root.querySelectorAll(':scope > fieldset[data-rw-form-collection-row]')].map(row =>
            [...row.querySelectorAll('input')].find(input =>
              input.name === `${collection}.index` && input.closest('[data-rw-form-collection-row]') === row)?.value ?? '');
        }
        """);

    private async Task OpenNestedCollectionsAsync(IPage page, string mode)
    {
        await page.GotoAsync($"{_fixture.BaseUrl}/Reactivity/NestedCollections?mode={mode}");
        await page.WaitForFunctionAsync(
            """
            () => Boolean(window.RazorWire?.formInteractionsManager)
              && document.querySelector('form[data-nested-proof][data-rw-form-interactions-enhanced="true"]') !== null
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
        Assert.Equal(mode, await page.Locator("input[name='RemoveMode']").InputValueAsync());
    }

    private static async Task<string> OwnedStatusTextAsync(ILocator root) => await root.EvaluateAsync<string>(
        """
        root => [...root.querySelectorAll('[data-rw-form-collection-status]')]
          .find(status => status.closest('[data-rw-form-collection]') === root)?.textContent?.trim() ?? ''
        """);

    private static Task<string> ActiveNameAsync(IPage page) =>
        page.EvaluateAsync<string>("() => document.activeElement?.getAttribute('name') ?? ''");

    private static async Task<FormDataProbe> CaptureFormDataAsync(IPage page) => await page.EvaluateAsync<FormDataProbe>(
        """
        () => {
          const data = new FormData(document.querySelector('form[data-nested-proof]'));
          const get = name => data.get(name);
          const all = name => data.getAll(name).map(String);
          return {
            ActionKeys: all('Actions.index'),
            Action4DateKeys: all('Actions[4].Dates.index'),
            Action9DateKeys: all('Actions[9].Dates.index'),
            Action10DateKeys: all('Actions[10].Dates.index'),
            Action4Id: get('Actions[4].Id'),
            Action4RowVersion: get('Actions[4].RowVersion'),
            Action9Id: get('Actions[9].Id'),
            Action9RowVersion: get('Actions[9].RowVersion'),
            Action10Id: get('Actions[10].Id'),
            Action10RowVersion: get('Actions[10].RowVersion'),
            Action4Date0Delete: get('Actions[4].Dates[0].Delete'),
            Action4Date0Id: get('Actions[4].Dates[0].Id'),
            Action4Date7Id: get('Actions[4].Dates[7].Id'),
            Action9Date0Id: get('Actions[9].Dates[0].Id'),
            Action9Date7Id: get('Actions[9].Dates[7].Id'),
            Action10Date8Id: get('Actions[10].Dates[8].Id'),
            Action10Date8RowVersion: get('Actions[10].Dates[8].RowVersion'),
            Action10Date8ConcurrencyToken: get('Actions[10].Dates[8].ConcurrencyToken'),
            Action10Date9Id: get('Actions[10].Dates[9].Id'),
            Action10Date9RowVersion: get('Actions[10].Dates[9].RowVersion'),
            Action10Date9ConcurrencyToken: get('Actions[10].Dates[9].ConcurrencyToken')
          };
        }
        """);

    private async Task<IResponse> SubmitAndWaitForResponseAsync(IPage page)
    {
        return await page.RunAndWaitForResponseAsync(
            () => page.Locator("form[data-nested-proof] > button[type='submit']").ClickAsync(),
            response => response.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
                && response.Url.Contains("/Reactivity/NestedCollections", StringComparison.Ordinal),
            new PageRunAndWaitForResponseOptions { Timeout = 30_000 });
    }

    private async Task<IResponse> SubmitNamedCommandAsync(IPage page, string name, string value)
    {
        return await page.RunAndWaitForResponseAsync(
            () => page.Locator($"form[data-nested-proof] button[name='{name}'][value='{value}']").ClickAsync(),
            response => response.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
                && response.Url.Contains("/Reactivity/NestedCollections", StringComparison.Ordinal),
            new PageRunAndWaitForResponseOptions { Timeout = 30_000 });
    }

    private sealed class FormDataProbe
    {
        public string[] ActionKeys { get; set; } = [];
        public string[] Action4DateKeys { get; set; } = [];
        public string[] Action9DateKeys { get; set; } = [];
        public string[] Action10DateKeys { get; set; } = [];
        public string? Action4Id { get; set; }
        public string? Action4RowVersion { get; set; }
        public string? Action9Id { get; set; }
        public string? Action9RowVersion { get; set; }
        public string? Action10Id { get; set; }
        public string? Action10RowVersion { get; set; }
        public string? Action4Date0Delete { get; set; }
        public string? Action4Date0Id { get; set; }
        public string? Action4Date7Id { get; set; }
        public string? Action9Date0Id { get; set; }
        public string? Action9Date7Id { get; set; }
        public string? Action10Date8Id { get; set; }
        public string? Action10Date8RowVersion { get; set; }
        public string? Action10Date8ConcurrencyToken { get; set; }
        public string? Action10Date9Id { get; set; }
        public string? Action10Date9RowVersion { get; set; }
        public string? Action10Date9ConcurrencyToken { get; set; }
    }

    private sealed class DuplicateChildProbe
    {
        public string[] ParentKeys { get; set; } = [];
        public string[] SourceDateKeys { get; set; } = [];
        public string[] CopiedDateKeys { get; set; } = [];
        public string[] OtherDateKeys { get; set; } = [];
        public string? AddedDateValue { get; set; }
        public string? AddedDateId { get; set; }
        public string? AddedDateRowVersion { get; set; }
        public string? AddedDateConcurrencyToken { get; set; }
    }

    private sealed class DeepFragmentIdProbe
    {
        public string MessageId { get; set; } = string.Empty;
        public string LabelFor { get; set; } = string.Empty;
        public string HelpId { get; set; } = string.Empty;
        public string DescribedBy { get; set; } = string.Empty;
    }

    private sealed class ReminderTemplateProbe
    {
        public string Root { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public string MessageName { get; set; } = string.Empty;
        public string MessageId { get; set; } = string.Empty;
        public string HelpId { get; set; } = string.Empty;
        public string TemplateLabelFor { get; set; } = string.Empty;
        public string TemplateDescribedBy { get; set; } = string.Empty;
        public bool HasLocalToken { get; set; }
    }

    private sealed class IdReferenceProbe
    {
        public string ControlId { get; set; } = string.Empty;
        public string ListId { get; set; } = string.Empty;
        public string ErrorId { get; set; } = string.Empty;
        public string PanelId { get; set; } = string.Empty;
        public string DescriptionId { get; set; } = string.Empty;
        public string LabelId { get; set; } = string.Empty;
        public string OwnedId { get; set; } = string.Empty;
        public string OutputFor { get; set; } = string.Empty;
        public string LabelFor { get; set; } = string.Empty;
        public string ListReference { get; set; } = string.Empty;
        public string ErrorReference { get; set; } = string.Empty;
        public string Controls { get; set; } = string.Empty;
        public string DescribedBy { get; set; } = string.Empty;
        public string LabelledBy { get; set; } = string.Empty;
        public string Owns { get; set; } = string.Empty;
        public string SourceControlId { get; set; } = string.Empty;
        public bool CollisionStillPresent { get; set; }
    }

    private sealed class BeforeEventProbe
    {
        public bool FormMatches { get; set; }
        public string RootName { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string? PreviousIndex { get; set; }
        public bool Cancelable { get; set; }
        public bool RowDetachedBeforeCommit { get; set; }
    }

    private sealed class LayoutProbe
    {
        public int ViewportWidth { get; set; }
        public int DocumentWidth { get; set; }
        public double FormRight { get; set; }
        public double ErrorRight { get; set; }
        public double[] ButtonHeights { get; set; } = [];
        public double FieldContrast { get; set; }
        public double ErrorContrast { get; set; }
        public int ErrorTextLength { get; set; }
    }

    private sealed class CollectionEventProbe
    {
        public bool FormMatches { get; set; }
        public string RootName { get; set; } = string.Empty;
        public string RowName { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string? PreviousIndex { get; set; }
    }

    private sealed class InertTemplateProbe
    {
        public string Collection { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public string TemplateCollectionName { get; set; } = string.Empty;
        public bool TemplateHasLocalToken { get; set; }
    }
}
