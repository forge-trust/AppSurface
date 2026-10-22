using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

[Collection(RazorWireIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RazorWireSkoolitV4CollectionsPlaywrightTests(RazorWireMvcPlaywrightFixture fixture)
{
    private string V4TestBaseUrl =>
        Environment.GetEnvironmentVariable("RW_NESTED_COLLECTIONS_PROOF_URL")?.TrimEnd('/') is { Length: > 0 } proofBaseUrl
            ? proofBaseUrl
            : fixture.BaseUrl;

    [Theory]
    [InlineData("physical")]
    [InlineData("mark")]
    public async Task SkoolitV4_DuplicateActionRoundTripsExactSparseDateFieldsAndRetriesDiscreteValidation(string mode)
    {
        await using var context = await fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{V4TestBaseUrl}/Reactivity/SkoolitV4Collections?mode={mode}");
        await page.WaitForFunctionAsync(
            """
            () => Boolean(window.RazorWire?.formInteractionsManager)
              && document.querySelector('form[data-v4-form][data-rw-form-interactions-enhanced="true"]') !== null
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        Assert.Contains("a589f164ef3346d4ecf8bf39b8a73ec71ea1cb1e", await page.Locator(".proof-note").InnerTextAsync());
        Assert.Equal(1, await page.Locator("script[src*='/_content/ForgeTrust.RazorWire/razorwire/form-interactions.js']").CountAsync());
        Assert.Equal(mode, await page.Locator("input[name='RemoveMode']").InputValueAsync());

        var actions = ActionsRoot(page);
        var source4 = ParentRow(page, "4");
        var source9 = ParentRow(page, "9");
        Assert.Equal(["4", "9"], await GetKeysAsync(actions));
        Assert.Equal(["0", "7"], await GetKeysAsync(ChildCollection(source4)));
        Assert.Equal(["0", "7"], await GetKeysAsync(ChildCollection(source9)));

        var removedDate = DateRow(page, "4", "0");
        await removedDate.Locator(":scope > .commands > button[data-rw-form-collection-remove]").ClickAsync();
        if (mode == "mark")
        {
            Assert.Equal("removed", await removedDate.GetAttributeAsync("data-rw-form-collection-row-state"));
            Assert.True(await removedDate.IsHiddenAsync());
            Assert.Equal("true", await removedDate.Locator("input[name='Actions[4].Dates[0].Delete']").InputValueAsync());
            Assert.Equal("date-4-0", await removedDate.Locator("input[name='Actions[4].Dates[0].Id']").InputValueAsync());
            Assert.True(await removedDate.Locator("select[name='Actions[4].Dates[0].Shape']").IsDisabledAsync());
        }
        else
        {
            Assert.Equal(0, await actions.Locator("input[name='Actions[4].Dates.index'][value='0']").CountAsync());
            Assert.Equal(["7"], await GetKeysAsync(ChildCollection(source4)));
        }

        await ParentDuplicate(source4).ClickAsync();

        var copiedAction = ParentRow(page, "10");
        var copiedDates = ChildCollection(copiedAction);
        var copiedDate = DateRow(page, "10", "8");
        Assert.Equal(["4", "10", "9"], await GetKeysAsync(actions));
        Assert.Equal(mode == "mark" ? ["0", "7"] : ["7"], await GetKeysAsync(ChildCollection(source4)));
        Assert.Equal(["0", "7"], await GetKeysAsync(ChildCollection(source9)));
        Assert.Equal(["8"], await GetKeysAsync(copiedDates));
        Assert.Equal("Field day", await copiedAction.Locator("input[name='Actions[10].Title']").InputValueAsync());
        Assert.Equal("8", await copiedDate.Locator("input[name='Actions[10].Dates[8].ClientIndex']").InputValueAsync());
        Assert.Equal("dueBy", await copiedDate.Locator("select[name='Actions[10].Dates[8].Kind']").InputValueAsync());
        Assert.Equal("Range", await copiedDate.Locator("select[name='Actions[10].Dates[8].Shape']").InputValueAsync());
        Assert.Equal("6", await copiedDate.Locator("select[name='Actions[10].Dates[8].StartMonth']").InputValueAsync());
        Assert.Equal("12", await copiedDate.Locator("input[name='Actions[10].Dates[8].StartDay']").InputValueAsync());
        Assert.Equal(string.Empty, await copiedDate.Locator("input[name='Actions[10].Dates[8].StartYear']").InputValueAsync());
        Assert.Equal("6", await copiedDate.Locator("select[name='Actions[10].Dates[8].EndMonth']").InputValueAsync());
        Assert.Equal("14", await copiedDate.Locator("input[name='Actions[10].Dates[8].EndDay']").InputValueAsync());
        Assert.Equal("09:00", await copiedDate.Locator("input[name='Actions[10].Dates[8].StartTime']").InputValueAsync());
        Assert.Equal("17:00", await copiedDate.Locator("input[name='Actions[10].Dates[8].EndTime']").InputValueAsync());
        Assert.Equal("America/New_York", await copiedDate.Locator("input[name='Actions[10].Dates[8].TimeZone']").InputValueAsync());
        Assert.Equal(string.Empty, await copiedAction.Locator("input[name='Actions[10].Id']").InputValueAsync());
        Assert.Equal(string.Empty, await copiedAction.Locator("input[name='Actions[10].RowVersion']").InputValueAsync());
        Assert.Equal(string.Empty, await copiedDate.Locator("input[name='Actions[10].Dates[8].Id']").InputValueAsync());
        Assert.Equal(string.Empty, await copiedDate.Locator("input[name='Actions[10].Dates[8].ConcurrencyToken']").InputValueAsync());

        var copiedEnd = copiedDate.Locator("fieldset[data-date-endpoint='end']");
        Assert.False(await copiedEnd.IsHiddenAsync());
        Assert.False(await copiedEnd.IsDisabledAsync());
        var ids = await page.EvaluateAsync<string[]>(
            "() => [...document.querySelectorAll('form[data-v4-form] [id]')].map(element => element.id)");
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.True(await page.EvaluateAsync<bool>(
            """
            () => [...document.querySelectorAll('form[data-v4-form] label[for]')].every(label => {
              const matches = [...document.querySelectorAll('[id]')].filter(element => element.id === label.htmlFor);
              return matches.length === 1;
            })
            """));

        var copiedDateAdd = copiedDates.Locator(":scope > .commands > button[data-rw-form-collection-add]");
        await copiedDateAdd.ClickAsync();
        var blankDate = DateRow(page, "10", "9");
        Assert.Equal(["8", "9"], await GetKeysAsync(copiedDates));
        Assert.Equal("9", await blankDate.Locator("input[name='Actions[10].Dates[9].ClientIndex']").InputValueAsync());
        Assert.True(await blankDate.Locator("fieldset[data-date-endpoint='end']").IsHiddenAsync());
        Assert.True(await blankDate.Locator("select[name='Actions[10].Dates[9].EndMonth']").IsDisabledAsync());
        Assert.Equal("Duplicated action.", await OwnedStatusTextAsync(actions));
        Assert.Equal("Added date.", await OwnedStatusTextAsync(copiedDates));

        await copiedDate.Locator("select[name='Actions[10].Dates[8].Shape']").SelectOptionAsync("Discrete");
        // Value: protects=the copied V4 date's Range-to-Discrete visibility and disabled controls;
        // fails_when=the end fieldset remains visible or its child controls can submit;
        // why_new=the invalid-post journey intentionally enables the fieldset after this transition;
        // seam=none; no_claim=live Skoolit adoption, since this is the version-bound consumer fixture.
        Assert.True(await copiedEnd.IsHiddenAsync());
        Assert.True(await copiedEnd.EvaluateAsync<bool>("fieldset => fieldset.disabled"));
        Assert.True(await copiedDate.Locator("select[name='Actions[10].Dates[8].EndMonth']").IsDisabledAsync());
        Assert.True(await copiedDate.Locator("input[name='Actions[10].Dates[8].EndDay']").IsDisabledAsync());
        await copiedEnd.EvaluateAsync("fieldset => fieldset.disabled = false");
        var beforePost = await CaptureFormDataAsync(page);
        Assert.Equal(["4", "10", "9"], beforePost.ActionKeys);
        Assert.Equal(mode == "mark" ? ["0", "7"] : ["7"], beforePost.Action4DateKeys);
        Assert.Equal(["0", "7"], beforePost.Action9DateKeys);
        Assert.Equal(["8", "9"], beforePost.Action10DateKeys);
        Assert.Equal("action-4", beforePost.Action4Id);
        Assert.Equal("row-version-4", beforePost.Action4RowVersion);
        Assert.Equal("action-9", beforePost.Action9Id);
        Assert.Equal("row-version-9", beforePost.Action9RowVersion);
        Assert.Equal("date-4-7", beforePost.Action4Date7Id);
        Assert.Equal("date-9-0", beforePost.Action9Date0Id);
        Assert.Equal("date-9-7", beforePost.Action9Date7Id);
        Assert.Equal(string.Empty, beforePost.Action10Id);
        Assert.Equal(string.Empty, beforePost.Action10RowVersion);
        Assert.Equal(string.Empty, beforePost.Action10Date8Id);
        Assert.Equal(string.Empty, beforePost.Action10Date8ConcurrencyToken);
        Assert.Equal("8", beforePost.Action10Date8ClientIndex);
        Assert.Equal("Discrete", beforePost.Action10Date8Shape);
        Assert.Equal("6", beforePost.Action10Date8EndMonth);
        Assert.Equal("14", beforePost.Action10Date8EndDay);
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

        var invalidResponse = await SubmitAndWaitForResponseAsync(page);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, invalidResponse.Status);
        Assert.Contains("End date parts apply only to a Range", (await page.Locator("form[data-v4-form] .errors").InnerTextAsync()));
        Assert.Equal(["4", "10", "9"], await GetKeysAsync(ActionsRoot(page)));
        Assert.Equal(mode == "mark" ? ["0", "7"] : ["7"], await GetKeysAsync(ChildCollection(ParentRow(page, "4"))));
        Assert.Equal(["0", "7"], await GetKeysAsync(ChildCollection(ParentRow(page, "9"))));
        Assert.Equal(["8", "9"], await GetKeysAsync(ChildCollection(ParentRow(page, "10"))));
        Assert.Equal("Discrete", await page.Locator("select[name='Actions[10].Dates[8].Shape']").InputValueAsync());
        Assert.Equal("6", await page.Locator("select[name='Actions[10].Dates[8].EndMonth']").InputValueAsync());
        Assert.Equal("14", await page.Locator("input[name='Actions[10].Dates[8].EndDay']").InputValueAsync());
        Assert.Equal(string.Empty, await page.Locator("input[name='Actions[10].Id']").InputValueAsync());
        Assert.Equal(string.Empty, await page.Locator("input[name='Actions[10].Dates[8].Id']").InputValueAsync());
        Assert.Equal("action-4", await page.Locator("input[name='Actions[4].Id']").InputValueAsync());
        Assert.Equal("action-9", await page.Locator("input[name='Actions[9].Id']").InputValueAsync());

        await page.Locator("select[name='Actions[10].Dates[8].Shape']").SelectOptionAsync("Range");
        await page.Locator("select[name='Actions[10].Dates[8].EndMonth']").SelectOptionAsync("");
        await page.Locator("input[name='Actions[10].Dates[8].EndDay']").FillAsync(string.Empty);
        var validResponse = await SubmitAndWaitForResponseAsync(page);
        Assert.Equal((int)HttpStatusCode.OK, validResponse.Status);
        Assert.Equal("Form accepted for consumer persistence; this fixture has no data store.",
            (await page.Locator("[data-v4-result]").InnerTextAsync()).Trim());
        Assert.Equal(["4", "10", "9"], await GetKeysAsync(ActionsRoot(page)));
        Assert.Equal(["8", "9"], await GetKeysAsync(ChildCollection(ParentRow(page, "10"))));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SkoolitV4_RejectedDiscreteEndPartsStayEditableUntilExplicitlyCorrected(bool javaScript, bool changeToRange)
    {
        await using var context = await fixture.Browser.NewContextAsync(new BrowserNewContextOptions { JavaScriptEnabled = javaScript });
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{V4TestBaseUrl}/Reactivity/SkoolitV4Collections");
        if (javaScript)
            await page.WaitForSelectorAsync("form[data-v4-form][data-rw-form-interactions-enhanced='true']");

        const string prefix = "Actions[4].Dates[7]";
        var date = DateRow(page, "4", "7");
        var end = date.Locator("fieldset[data-date-endpoint='end']");
        await date.Locator($"input[name='{prefix}.EndYear']").FillAsync("2027");
        await date.Locator($"select[name='{prefix}.Shape']").SelectOptionAsync("Discrete");
        // Submit stale client state once. Subsequent submits use the rendered controls without intervention.
        await end.EvaluateAsync("fieldset => fieldset.disabled = false");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var rejected = await SubmitAndWaitForResponseAsync(page);
            Assert.Equal(StatusCodes.Status422UnprocessableEntity, rejected.Status);
            Assert.True(await end.IsVisibleAsync());
            Assert.False(await end.EvaluateAsync<bool>("fieldset => fieldset.disabled"));
            Assert.Equal("false", await end.GetAttributeAsync("aria-hidden"));
            foreach (var (part, value) in new[] { ("EndMonth", "6"), ("EndDay", "14"), ("EndYear", "2027") })
            {
                Assert.Equal(value, await date.Locator($"[name='{prefix}.{part}']").InputValueAsync());
                Assert.True(await date.Locator($"[name='{prefix}.{part}']").IsEnabledAsync());
                var message = date.Locator($"[data-valmsg-for='{prefix}.{part}']");
                Assert.True(await message.IsVisibleAsync());
                Assert.Contains("End date parts apply only to a Range", await message.InnerTextAsync());
            }
        }

        if (javaScript)
        {
            await date.GetByRole(AriaRole.Button, new() { Name = "Duplicate date", Exact = true }).ClickAsync();
            var copiedEnd = DateRow(page, "4", "8").Locator("fieldset[data-date-endpoint='end']");
            Assert.True(await copiedEnd.IsHiddenAsync());
            Assert.True(await copiedEnd.EvaluateAsync<bool>("fieldset => fieldset.disabled"));
            Assert.All(await copiedEnd.Locator("[data-valmsg-for]").AllTextContentsAsync(), text => Assert.Equal(string.Empty, text));
            Assert.True(await end.IsVisibleAsync());
        }

        if (changeToRange)
            await date.Locator($"select[name='{prefix}.Shape']").SelectOptionAsync("Range");
        else
        {
            await date.Locator($"select[name='{prefix}.EndMonth']").SelectOptionAsync(string.Empty);
            await date.Locator($"input[name='{prefix}.EndDay']").FillAsync(string.Empty);
            await date.Locator($"input[name='{prefix}.EndYear']").FillAsync(string.Empty);
        }
        var accepted = await SubmitAndWaitForResponseAsync(page);
        Assert.Equal(StatusCodes.Status200OK, accepted.Status);
        Assert.True(await page.Locator("[data-v4-result]").IsVisibleAsync());
        Assert.Equal(changeToRange, await end.IsVisibleAsync());
        Assert.Equal(!changeToRange, await end.EvaluateAsync<bool>("fieldset => fieldset.disabled"));
    }

    [Fact]
    public async Task SkoolitV4_NoJavaScriptAddDateCommandsAreOwnerLocalAndOrdinarySubmitWorks()
    {
        await using var context = await fixture.Browser.NewContextAsync(new BrowserNewContextOptions { JavaScriptEnabled = false });
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{V4TestBaseUrl}/Reactivity/SkoolitV4Collections?mode=physical");

        Assert.Equal(0, await page.Locator("[data-rw-form-interactions-enhanced='true']").CountAsync());
        Assert.Equal(["4", "9"], await GetKeysAsync(ActionsRoot(page)));
        Assert.Equal(["0", "7"], await GetKeysAsync(ChildCollection(ParentRow(page, "4"))));
        Assert.Equal(["0", "7"], await GetKeysAsync(ChildCollection(ParentRow(page, "9"))));

        var firstAdd = await SubmitNamedCommandAsync(page, "add-date|0");
        Assert.Equal((int)HttpStatusCode.OK, firstAdd.Status);
        var action4Keys = await GetKeysAsync(ChildCollection(ParentRow(page, "4")));
        Assert.Equal(["0", "7"], action4Keys.Take(2));
        Assert.Equal(3, action4Keys.Length);
        var firstNewKey = action4Keys[2];
        Assert.StartsWith("draft-", firstNewKey, StringComparison.Ordinal);
        Assert.Equal(["0", "7"], await GetKeysAsync(ChildCollection(ParentRow(page, "9"))));

        var secondAdd = await SubmitNamedCommandAsync(page, "add-date|1");
        Assert.Equal((int)HttpStatusCode.OK, secondAdd.Status);
        var action9Keys = await GetKeysAsync(ChildCollection(ParentRow(page, "9")));
        Assert.Equal(["0", "7"], action9Keys.Take(2));
        Assert.Equal(3, action9Keys.Length);
        var secondNewKey = action9Keys[2];
        Assert.StartsWith("draft-", secondNewKey, StringComparison.Ordinal);
        Assert.NotEqual(firstNewKey, secondNewKey);

        var submit = await SubmitAndWaitForResponseAsync(page);
        Assert.Equal((int)HttpStatusCode.OK, submit.Status);
        Assert.Equal("Form accepted for consumer persistence; this fixture has no data store.",
            (await page.Locator("[data-v4-result]").InnerTextAsync()).Trim());
        Assert.Equal(["4", "9"], await GetKeysAsync(ActionsRoot(page)));
    }

    private static ILocator ActionsRoot(IPage page) => page.Locator("section[data-rw-form-collection='Actions']");

    private static ILocator ParentRow(IPage page, string index) =>
        page.Locator($"input[name='Actions.index'][value='{index}']").Locator("xpath=..");

    private static ILocator ParentDuplicate(ILocator row) =>
        row.Locator(":scope > .commands > button[data-rw-form-collection-duplicate]");

    private static ILocator ChildCollection(ILocator row) => row.Locator(":scope > section[data-rw-form-collection]");

    private static ILocator DateRow(IPage page, string actionIndex, string dateIndex) =>
        page.Locator($"input[name='Actions[{actionIndex}].Dates.index'][value='{dateIndex}']").Locator("xpath=..");

    private static async Task<string[]> GetKeysAsync(ILocator collection) => await collection.EvaluateAsync<string[]>(
        """
        root => [...root.querySelectorAll('fieldset[data-rw-form-collection-row]')]
          .filter(row => row.closest('[data-rw-form-collection]') === root).map(row => {
          const markerName = `${root.getAttribute('data-rw-form-collection')}.index`;
          return [...row.querySelectorAll('input[type="hidden"]')]
            .find(input => input.name === markerName && input.closest('[data-rw-form-collection-row]') === row)?.value ?? '';
        })
        """);

    private static async Task<string> OwnedStatusTextAsync(ILocator root) => await root.EvaluateAsync<string>(
        """
        element => [...element.querySelectorAll('[data-rw-form-collection-status]')]
          .find(status => status.closest('[data-rw-form-collection]') === element)?.textContent?.trim() ?? ''
        """);

    private static async Task<V4PayloadProbe> CaptureFormDataAsync(IPage page) => await page.EvaluateAsync<V4PayloadProbe>(
        """
        () => {
          const data = new FormData(document.querySelector('form[data-v4-form]'));
          const get = name => data.get(name) === null ? null : String(data.get(name));
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
            Action10Date8ConcurrencyToken: get('Actions[10].Dates[8].ConcurrencyToken'),
            Action10Date8ClientIndex: get('Actions[10].Dates[8].ClientIndex'),
            Action10Date8Shape: get('Actions[10].Dates[8].Shape'),
            Action10Date8EndMonth: get('Actions[10].Dates[8].EndMonth'),
            Action10Date8EndDay: get('Actions[10].Dates[8].EndDay')
          };
        }
        """);

    private static async Task<IResponse> SubmitAndWaitForResponseAsync(IPage page) => await page.RunAndWaitForResponseAsync(
        () => page.Locator("form[data-v4-form] > button[type='submit']").ClickAsync(),
        response => response.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
            && response.Url.Contains("/Reactivity/SkoolitV4Collections", StringComparison.Ordinal),
        new PageRunAndWaitForResponseOptions { Timeout = 30_000 });

    private static async Task<IResponse> SubmitNamedCommandAsync(IPage page, string command) => await page.RunAndWaitForResponseAsync(
        () => page.Locator($"form[data-v4-form] button[name='draftIntent'][value='{command}']").ClickAsync(),
        response => response.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
            && response.Url.Contains("/Reactivity/SkoolitV4Collections", StringComparison.Ordinal),
        new PageRunAndWaitForResponseOptions { Timeout = 30_000 });

    private sealed class V4PayloadProbe
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
        public string? Action10Date8ConcurrencyToken { get; set; }
        public string? Action10Date8ClientIndex { get; set; }
        public string? Action10Date8Shape { get; set; }
        public string? Action10Date8EndMonth { get; set; }
        public string? Action10Date8EndDay { get; set; }
    }
}
