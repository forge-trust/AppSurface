using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

[Collection(RazorWireIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RazorWireDialogPlaywrightTests
{
    private const string DialogPagePath = "/Reactivity/DialogResponses";
    private const string DialogStatusPath = "/Reactivity/DialogStatus";
    private const string SaveDialogPath = "/Reactivity/SaveDialog";
    private const string CompleteDialogPath = "/Reactivity/CompleteDialog";
    private const string StatusLinkSelector = "a[href*='/Reactivity/DialogStatus']";
    private const string StreamContentType = "text/vnd.turbo-stream.html; charset=utf-8";
    private const long MaximumJavaScriptSafeInteger = 9_007_199_254_740_991L;

    private readonly RazorWireMvcPlaywrightFixture _fixture;

    public RazorWireDialogPlaywrightTests(RazorWireMvcPlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DialogStatus_StreamOpensOneAccessibleDialogAndHandled422ThenSuccessStayInTheFlow()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}{DialogPagePath}");

        var statusResponse = await ClickAndWaitForResponseAsync(
            page,
            StatusLinkSelector,
            DialogStatusPath,
            HttpMethod.Get.Method);
        var statusRequest = await ReadCorrelationAsync(statusResponse.Request);
        Assert.Null(statusRequest.Flow);
        await AssertCorrelationAttributesAsync(statusResponse, statusRequest, "origin");

        await WaitForDialogTitleAsync(page, "Service status");
        Assert.Equal(1, await page.Locator("[data-rw-dialog]").CountAsync());
        Assert.Equal(
            await page.Locator("[data-rw-dialog-title]").GetAttributeAsync("id"),
            await page.Locator("[data-rw-dialog]").GetAttributeAsync("aria-labelledby"));
        Assert.Equal(1, await page.Locator("[data-rw-dialog][open]").CountAsync());
        Assert.Equal("Ordered update applied", (await page.Locator("#dialog-proof-after").InnerTextAsync()).Trim());
        Assert.Equal(
            "true",
            await page.EvaluateAsync<string>("() => document.activeElement?.matches('[data-rw-dialog-title]') ? 'true' : 'false'"));

        var statusFlow = await ReadLiveFlowAsync(page);
        Assert.NotEqual(statusRequest.Request, statusFlow);

        await page.Locator("[data-rw-dialog-close]").ClickAsync();
        await page.Locator("[data-rw-dialog][open]").WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
        Assert.Equal(
            "true",
            await page.EvaluateAsync<string>("() => document.activeElement?.matches(\"a[href*='/Reactivity/DialogStatus']\") ? 'true' : 'false'"));

        var saveResponse = await OpenSaveDialogAsync(page);
        var saveRequest = await ReadCorrelationAsync(saveResponse.Request);
        Assert.Null(saveRequest.Flow);
        Assert.True(saveRequest.Order > statusRequest.Order);
        await AssertCorrelationAttributesAsync(saveResponse, saveRequest, "origin");
        var saveStream = await saveResponse.TextAsync();
        Assert.True(
            saveStream.IndexOf("target=\"dialog-result\"", StringComparison.OrdinalIgnoreCase)
            < saveStream.IndexOf("action=\"rw-dialog\"", StringComparison.OrdinalIgnoreCase),
            "The SaveDialog response must update the page before opening the follow-up form.");
        await WaitForDialogTitleAsync(page, "Complete the save");
        await WaitForTextAsync(page, "#dialog-result", "A follow-up is ready");
        Assert.Equal("Name", await page.EvaluateAsync<string>("() => document.activeElement?.getAttribute('name') || ''"));

        var liveFlow = await ReadLiveFlowAsync(page);
        Assert.NotEqual(statusFlow, liveFlow);

        await page.Locator("#Name").FillAsync("   ");
        var validationResponse = await SubmitAndWaitForResponseAsync(
            page,
            "#dialog-form",
            CompleteDialogPath,
            StatusCodes.Status422UnprocessableEntity);
        var validationRequest = await ReadCorrelationAsync(validationResponse.Request);

        Assert.Equal(liveFlow.ToString("D"), validationRequest.Flow?.ToString("D"));
        Assert.True(validationRequest.Order > saveRequest.Order);
        Assert.Equal(
            validationRequest.Request.ToString("D"),
            await validationResponse.HeaderValueAsync("X-RazorWire-Request"));
        Assert.Equal("true", await validationResponse.HeaderValueAsync("X-RazorWire-Form-Handled"));
        await AssertCorrelationAttributesAsync(validationResponse, validationRequest, "origin");
        await WaitForTextAsync(page, "#dialog-errors", "required");
        Assert.Equal("   ", await page.InputValueAsync("#Name"));
        Assert.Equal("Name", await page.EvaluateAsync<string>("() => document.activeElement?.id || ''"));
        Assert.True(await page.Locator("[data-rw-dialog][open]").CountAsync() == 1);

        await page.Locator("#Name").FillAsync("Ada Lovelace");
        var successResponse = await SubmitAndWaitForResponseAsync(
            page,
            "#dialog-form",
            CompleteDialogPath,
            StatusCodes.Status200OK);
        var successRequest = await ReadCorrelationAsync(successResponse.Request);

        Assert.Equal(liveFlow.ToString("D"), successRequest.Flow?.ToString("D"));
        Assert.True(successRequest.Order > validationRequest.Order);
        await AssertCorrelationAttributesAsync(successResponse, successRequest, "origin");
        await WaitForTextAsync(page, "#dialog-result", "Ada Lovelace");
        await page.Locator("[data-rw-dialog][open]").WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
        Assert.Equal(1, await page.Locator("[data-rw-dialog]").CountAsync());
    }

    [Fact]
    public async Task SaveDialog_StreamPostUpdatesPageAndOpensDialog()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}{DialogPagePath}");

        var response = await OpenSaveDialogAsync(page);
        var request = await ReadCorrelationAsync(response.Request);

        Assert.Null(request.Flow);
        Assert.True(request.Order > 0);
        await AssertCorrelationAttributesAsync(response, request, "origin");
        await WaitForDialogTitleAsync(page, "Complete the save");
        await WaitForTextAsync(page, "#dialog-result", "A follow-up is ready");
        Assert.Equal("Name", await page.EvaluateAsync<string>("() => document.activeElement?.getAttribute('name') || ''"));
        Assert.Equal(1, await page.Locator("[data-rw-dialog]").CountAsync());
        Assert.Equal(DialogPagePath, await page.EvaluateAsync<string>("() => location.pathname"));
    }

    [Fact]
    public async Task StreamGetFromDialogLink_KeepsCapturedFlowReusesBodyTargetAndReturnsFocusToPageOpener()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}{DialogPagePath}");

        var opening = await ClickAndWaitForResponseAsync(
            page,
            StatusLinkSelector,
            DialogStatusPath,
            HttpMethod.Get.Method);
        var openingRequest = await ReadCorrelationAsync(opening.Request);
        await WaitForDialogTitleAsync(page, "Service status");
        var originalFlow = await ReadLiveFlowAsync(page);

        await page.Locator("[data-rw-dialog-body]").EvaluateAsync(
            "body => { const link = document.createElement('a'); link.id = 'dialog-inside-status-link'; " +
            "link.href = '/Reactivity/DialogStatus?inside=1'; link.setAttribute('data-turbo-stream', ''); " +
            "link.target = '_self'; link.textContent = 'Refresh status from dialog'; body.append(link); }");
        await page.EvaluateAsync(
            "() => { window.__dialogBodyBeforeInsideGet = document.querySelector('[data-rw-dialog-body]'); " +
            "document.querySelector('#dialog-proof-after').textContent = 'Old generation'; }");
        var insideResponse = await page.RunAndWaitForResponseAsync(
            () => page.Locator("#dialog-inside-status-link").ClickAsync(),
            response => response.Url.Contains(DialogStatusPath, StringComparison.OrdinalIgnoreCase)
                        && response.Request.Method.Equals(HttpMethod.Get.Method, StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions { Timeout = 45_000 });

        var insideRequest = await ReadCorrelationAsync(insideResponse.Request);
        Assert.Equal(originalFlow.ToString("D"), insideRequest.Flow?.ToString("D"));
        Assert.True(insideRequest.Order > openingRequest.Order);
        await AssertCorrelationAttributesAsync(insideResponse, insideRequest, "origin");
        await WaitForDialogTitleAsync(page, "Service status");
        await WaitForTextAsync(page, "#dialog-proof-after", "Ordered update applied");
        Assert.Equal("Ordered update applied", (await page.Locator("#dialog-proof-after").InnerTextAsync()).Trim());
        Assert.True(await page.EvaluateAsync<bool>(
            "() => document.querySelector('[data-rw-dialog-body]') === window.__dialogBodyBeforeInsideGet"));
        var replacementFlow = await ReadLiveFlowAsync(page);
        Assert.NotEqual(originalFlow, replacementFlow);
        Assert.Equal(1, await page.Locator("[data-rw-dialog]").CountAsync());

        await page.Locator("[data-rw-dialog-close]").ClickAsync();
        Assert.Equal(
            "true",
            await page.EvaluateAsync<string>("() => document.activeElement?.matches(\"a[href*='/Reactivity/DialogStatus']\") ? 'true' : 'false'"));
    }

    [Fact]
    public async Task RequestSubmitInsideDialog_UsesFormFlowAndSerializesOriginalSubmitter()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}{DialogPagePath}");
        var opening = await OpenSaveDialogAsync(page);
        var openingRequest = await ReadCorrelationAsync(opening.Request);
        await WaitForDialogTitleAsync(page, "Complete the save");
        var liveFlow = await ReadLiveFlowAsync(page);
        await page.Locator("#Name").FillAsync("Request Submit");
        await page.Locator("#dialog-form button[type='submit']").EvaluateAsync(
            "button => { button.name = 'submitMode'; button.value = 'original-button'; }");
        await page.Locator("[data-rw-dialog-close]").FocusAsync();
        Assert.Equal(
            "true",
            await page.EvaluateAsync<string>("() => document.activeElement?.matches('[data-rw-dialog-close]') ? 'true' : 'false'"));

        await using var delayedSubmit = await ControlledStreamRoute.InstallAsync(
            page,
            "**/Reactivity/CompleteDialog");
        await page.Locator("#dialog-form").EvaluateAsync(
            "form => form.requestSubmit(form.querySelector('button[type=submit]'))");
        var submitted = await delayedSubmit.NextRequestAsync();
        Assert.Equal(liveFlow.ToString("D"), submitted.Correlation.Flow?.ToString("D"));
        Assert.True(submitted.Correlation.Order > openingRequest.Order);
        Assert.Contains("submitMode=original-button", submitted.Route.Request.PostData ?? string.Empty, StringComparison.Ordinal);

        var validationContents = "<input id=\"Name\" name=\"Name\" value=\"Request Submit\" aria-invalid=\"true\">" +
                                 "<div id=\"dialog-errors\" data-rw-form-error-kind=\"validation\" data-rw-form-error-field=\"Name\">Name needs another check.</div>";
        submitted.Respond(
            UpdateCommand(submitted.Correlation, "dialog-form", validationContents, "origin"),
            StatusCodes.Status422UnprocessableEntity,
            StreamContentType,
            HandledValidationHeaders(submitted.Correlation));
        await WaitForTextAsync(page, "#dialog-errors", "Name needs another check");
        Assert.Equal("Name", await page.EvaluateAsync<string>("() => document.activeElement?.id || ''"));
        Assert.Equal(1, await page.Locator("[data-rw-dialog][open]").CountAsync());
    }

    [Fact]
    public async Task ProgrammaticRequestSubmitOutsideDialog_DoesNotInheritFlowFromFocusedDialog()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}{DialogPagePath}");
        var opening = await OpenSaveDialogAsync(page);
        var openingCorrelation = await ReadCorrelationAsync(opening.Request);
        await WaitForDialogTitleAsync(page, "Complete the save");
        await page.Locator("#Name").FillAsync("Keep focused");
        var liveFlow = await ReadLiveFlowAsync(page);

        var outsideResponse = await page.RunAndWaitForResponseAsync(
            () => page.EvaluateAsync(
                "() => { const form = document.querySelector(\"form[action*='/Reactivity/SaveDialog']\"); " +
                "const clone = form.cloneNode(true); clone.id = 'outside-save-clone'; document.querySelector('main').append(clone); " +
                "document.querySelector('#Name').focus(); clone.requestSubmit(); }"),
            response => response.Url.Contains(SaveDialogPath, StringComparison.OrdinalIgnoreCase)
                        && response.Request.Method.Equals(HttpMethod.Post.Method, StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions { Timeout = 45_000 });
        var outsideCorrelation = await ReadCorrelationAsync(outsideResponse.Request);
        Assert.Null(outsideCorrelation.Flow);
        Assert.True(outsideCorrelation.Order > openingCorrelation.Order);
        Assert.NotEqual(liveFlow.ToString("D"), outsideCorrelation.Flow?.ToString("D"));
        await page.WaitForFunctionAsync(
            "oldFlow => document.querySelector('[data-rw-dialog-body]')?.getAttribute('data-rw-dialog-flow') !== oldFlow",
            liveFlow.ToString("D"));
        var replacementFlow = await ReadLiveFlowAsync(page);
        Assert.NotEqual(liveFlow, replacementFlow);
        Assert.Equal(string.Empty, await page.InputValueAsync("#Name"));
        Assert.Equal("Name", await page.EvaluateAsync<string>("() => document.activeElement?.id || ''"));
        await WaitForTextAsync(page, "#dialog-result", "A follow-up is ready");
        Assert.Equal("Complete the save", await page.Locator("[data-rw-dialog-title]").InnerTextAsync());
    }

    [Fact]
    public async Task OutsideProgrammaticRequestSubmit_DoesNotInheritFocusedDialogFlow()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}{DialogPagePath}");

        await using var delayedSave = await ControlledStreamRoute.InstallAsync(
            page,
            "**/Reactivity/SaveDialog");
        await page.Locator($"form[action*='{SaveDialogPath}'] button").ClickAsync();
        var opening = await delayedSave.NextRequestAsync();
        opening.Respond(string.Concat(
            UpdateCommand(opening.Correlation, "dialog-result", "Initial outside result", "origin"),
            OpenCommand(opening.Correlation, "Focused dialog", DialogFormHtml("Initial error target"))));
        await WaitForDialogTitleAsync(page, "Focused dialog");
        await page.Locator("#Name").FocusAsync();
        Assert.Equal("Name", await page.EvaluateAsync<string>("() => document.activeElement?.id || ''"));

        await page.EvaluateAsync(
            "() => { const source = document.querySelector(\"form[action*='/Reactivity/SaveDialog']\"); " +
            "const clone = source.cloneNode(true); clone.id = 'outside-save-clone'; document.querySelector('main').append(clone); " +
            "clone.requestSubmit(); }");
        var outside = await delayedSave.NextRequestAsync();
        Assert.Null(outside.Correlation.Flow);
        Assert.True(outside.Correlation.Order > opening.Correlation.Order);
        outside.Respond(UpdateCommand(
            outside.Correlation,
            "dialog-result",
            "Outside programmatic update applied",
            "origin"));

        await WaitForTextAsync(page, "#dialog-result", "Outside programmatic update applied");
        Assert.Equal("Name", await page.EvaluateAsync<string>("() => document.activeElement?.id || ''"));
        Assert.Equal(1, await page.Locator("[data-rw-dialog][open]").CountAsync());
        Assert.Equal("Initial error target", (await page.Locator("#dialog-errors").InnerTextAsync()).Trim());
    }

    [Fact]
    public async Task DialogStatusAndCompleteDialog_UseFullHtmlFallbackWhenJavaScriptIsDisabled()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            JavaScriptEnabled = false
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}{DialogPagePath}");

        var statusResponse = await ClickAndWaitForResponseAsync(
            page,
            StatusLinkSelector,
            DialogStatusPath,
            HttpMethod.Get.Method);

        Assert.Equal(StatusCodes.Status200OK, statusResponse.Status);
        Assert.Contains("Accept", await statusResponse.HeaderValueAsync("Vary") ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-store", await statusResponse.HeaderValueAsync("Cache-Control") ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await page.Locator("turbo-stream, [data-rw-dialog]").CountAsync());
        Assert.Equal(0, await page.Locator("#dialog-form").CountAsync());
        Assert.Contains("HTML fallback displays this status inline", await page.Locator("#dialog-proof-after").InnerTextAsync());

        await page.GotoAsync($"{_fixture.BaseUrl}{DialogPagePath}");
        var saveResponse = await SubmitAndWaitForResponseAsync(
            page,
            $"form[action*='{SaveDialogPath}']",
            SaveDialogPath,
            StatusCodes.Status200OK);
        Assert.Equal(StatusCodes.Status200OK, saveResponse.Status);
        await page.WaitForURLAsync($"**{SaveDialogPath}");
        Assert.Equal(1, await page.Locator("#dialog-form").CountAsync());
        await page.Locator("#Name").FillAsync("Ada Lovelace");
        var submitResponse = await SubmitAndWaitForResponseAsync(
            page,
            "#dialog-form",
            CompleteDialogPath,
            expectedStatus: null);

        Assert.Equal(StatusCodes.Status200OK, submitResponse.Status);
        await page.WaitForURLAsync($"**{CompleteDialogPath}");
        await WaitForTextAsync(page, "#dialog-result", "Dialog completed for Ada Lovelace");
        Assert.Equal(0, await page.Locator("turbo-stream, [data-rw-dialog]").CountAsync());
        Assert.DoesNotContain("action=\"rw-dialog\"", await page.ContentAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task CompleteDialog_MissingOrInvalidAntiforgeryIsRejectedForStreamAndHtmlPosts(
        bool javascriptEnabled,
        bool invalidToken)
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            JavaScriptEnabled = javascriptEnabled
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}{DialogPagePath}");
        await SubmitAndWaitForResponseAsync(
            page,
            $"form[action*='{SaveDialogPath}']",
            SaveDialogPath,
            StatusCodes.Status200OK);
        await page.Locator("#Name").FillAsync("Antiforgery check");

        var token = page.Locator("#dialog-form input[name='__RequestVerificationToken']");
        Assert.Equal(1, await token.CountAsync());
        if (invalidToken)
        {
            await token.EvaluateAsync("input => input.value = 'invalid-antiforgery-token'");
        }
        else
        {
            await token.EvaluateAsync("input => input.remove()");
        }

        var response = await SubmitAndWaitForResponseAsync(
            page,
            "#dialog-form",
            CompleteDialogPath,
            StatusCodes.Status400BadRequest);

        Assert.Equal(StatusCodes.Status400BadRequest, response.Status);
        var responseBody = await response.TextAsync();
        Assert.DoesNotContain("action=\"rw-dialog\"", responseBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("target=\"dialog-result\"", responseBody, StringComparison.OrdinalIgnoreCase);
        if (javascriptEnabled)
        {
            Assert.Contains("data-rw-form-error-kind=\"antiforgery\"", responseBody, StringComparison.OrdinalIgnoreCase);
            await WaitForTextAsync(page, "#dialog-errors", "Antiforgery token validation failed");
            Assert.Equal(1, await page.Locator("[data-rw-dialog][open]").CountAsync());
        }
        else
        {
            Assert.DoesNotContain("<turbo-stream", responseBody, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, await page.Locator("turbo-stream, [data-rw-dialog]").CountAsync());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OutsideStreamOpens_AreOrderedByRequestStartForEitherCompletionOrder(bool newerResponseArrivesFirst)
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}{DialogPagePath}");
        await AddRaceProgressTargetAsync(page);

        await using var delayedStatus = await ControlledStreamRoute.InstallAsync(
            page,
            "**/Reactivity/DialogStatus*");
        await AddRepeatedStatusLinksAsync(page);

        await page.Locator("#dialog-race-open-one").EvaluateAsync("link => link.click()");
        var first = await delayedStatus.NextRequestAsync();
        await page.Locator("#dialog-race-open-two").EvaluateAsync("link => link.click()");
        var second = await delayedStatus.NextRequestAsync();
        var older = first.Correlation.Order < second.Correlation.Order ? first : second;
        var newer = ReferenceEquals(older, first) ? second : first;

        Assert.Null(older.Correlation.Flow);
        Assert.Null(newer.Correlation.Flow);
        Assert.True(newer.Correlation.Order > older.Correlation.Order);
        Assert.NotEqual(older.Correlation.Request, newer.Correlation.Request);

        if (newerResponseArrivesFirst)
        {
            newer.Respond(OpenWithPageProgress(newer.Correlation, "Newer request", "newer body", "newer"));
            await WaitForDialogTitleAsync(page, "Newer request");
            await WaitForProgressAsync(page, "newer");

            older.Respond(OpenWithPageProgress(older.Correlation, "Older request", "older body", "older"));
            await WaitForProgressAsync(page, "older");
            Assert.Equal("Newer request", await page.Locator("[data-rw-dialog-title]").InnerTextAsync());
        }
        else
        {
            older.Respond(OpenWithPageProgress(older.Correlation, "Older request", "older body", "older"));
            await WaitForDialogTitleAsync(page, "Older request");
            await WaitForProgressAsync(page, "older");

            newer.Respond(OpenWithPageProgress(newer.Correlation, "Newer request", "newer body", "newer"));
            await WaitForDialogTitleAsync(page, "Newer request");
            await WaitForProgressAsync(page, "newer");
            Assert.Equal("Newer request", await page.Locator("[data-rw-dialog-title]").InnerTextAsync());
        }

        Assert.Equal(1, await page.Locator("[data-rw-dialog]").CountAsync());
        // UC828-1 and UC828-2 intentionally add no freshness barrier for outside
        // requests after manual dismissal or a newer server close. This test only
        // locks the approved opener-order rule for two open responses.
    }

    [Fact]
    public async Task SameResponse_OpenThenUpdate_TargetsTheNewDialogBodyAfterItExists()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}{DialogPagePath}");

        await using var delayedStatus = await ControlledStreamRoute.InstallAsync(
            page,
            "**/Reactivity/DialogStatus*");
        await page.Locator(StatusLinkSelector).EvaluateAsync("link => link.click()");
        var request = await delayedStatus.NextRequestAsync();
        var stream = string.Concat(
            OpenCommand(request.Correlation, "Ordered shell", "<div id=\"dialog-order-target\">before</div>"),
            UpdateCommand(request.Correlation, "dialog-order-target", "after shell insertion", "new"));
        request.Respond(stream);

        await WaitForDialogTitleAsync(page, "Ordered shell");
        await WaitForTextAsync(page, "#dialog-order-target", "after shell insertion");
        Assert.Equal("after shell insertion", (await page.Locator("#dialog-order-target").InnerTextAsync()).Trim());
    }

    [Fact]
    public async Task LateInsideCloseAfterReplacement_CannotCloseOrRewriteReusedDialogTargetsButKeepsPageUpdate()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}{DialogPagePath}");
        await AddRaceProgressTargetAsync(page);
        var initialStatus = await OpenSaveDialogAsync(page);
        var initialCorrelation = await ReadCorrelationAsync(initialStatus.Request);
        var liveFlow = await ReadLiveFlowAsync(page);

        await using var delayedSubmit = await ControlledStreamRoute.InstallAsync(
            page,
            "**/Reactivity/CompleteDialog");
        await page.Locator("#Name").FillAsync("Old flow request");
        await page.Locator("#dialog-form").EvaluateAsync("form => { form.setAttribute('data-turbo-frame','dialog-inside-frame'); form.requestSubmit(); }");
        var oldInside = await delayedSubmit.NextRequestAsync();
        Assert.Equal(liveFlow, oldInside.Correlation.Flow);

        await using var delayedReplacement = await ControlledStreamRoute.InstallAsync(
            page,
            "**/Reactivity/DialogStatus*");
        // Native modal hit testing can block an ordinary Playwright click on the
        // obscured page trigger; dispatching the real DOM click preserves the
        // Turbo link's capture-phase origin mapping and starts an outside flow.
        await page.Locator(StatusLinkSelector).EvaluateAsync("link => link.click()");
        var replacementRequest = await delayedReplacement.NextRequestAsync();
        Assert.Null(replacementRequest.Correlation.Flow);
        Assert.True(replacementRequest.Correlation.Order > oldInside.Correlation.Order);
        replacementRequest.Respond(OpenCommand(
            replacementRequest.Correlation,
            "Replacement flow",
            DialogFormHtml("Replacement error target")));
        await WaitForDialogTitleAsync(page, "Replacement flow");
        Assert.Equal("Replacement error target", (await page.Locator("#dialog-errors").InnerTextAsync()).Trim());

        var staleResponse = string.Concat(
            CloseCommand(oldInside.Correlation),
            UpdateCommand(oldInside.Correlation, "dialog-form", "stale replacement", "origin"),
            ProgressCommand(oldInside.Correlation, "late-inside-close"));
        oldInside.Respond(staleResponse);

        await WaitForProgressAsync(page, "late-inside-close");
        Assert.Equal("Replacement flow", await page.Locator("[data-rw-dialog-title]").InnerTextAsync());
        Assert.Equal("Replacement error target", (await page.Locator("#dialog-errors").InnerTextAsync()).Trim());
        Assert.Equal(1, await page.Locator("[data-rw-dialog][open]").CountAsync());
    }

    [Fact]
    public async Task LateHandledValidationAfterDismissal_DoesNotResurrectDialogOrApplyOldErrors()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}{DialogPagePath}");
        await AddRaceProgressTargetAsync(page);
        var initialStatus = await OpenSaveDialogAsync(page);
        var initialCorrelation = await ReadCorrelationAsync(initialStatus.Request);
        var liveFlow = await ReadLiveFlowAsync(page);

        await using var delayedSubmit = await ControlledStreamRoute.InstallAsync(
            page,
            "**/Reactivity/CompleteDialog");
        await page.Locator("#Name").FillAsync(" ");
        await page.Locator("#dialog-form").EvaluateAsync("form => { form.setAttribute('data-turbo-frame','dialog-inside-frame'); form.requestSubmit(); }");
        var oldInside = await delayedSubmit.NextRequestAsync();
        Assert.Equal(liveFlow, oldInside.Correlation.Flow);

        await page.Locator("[data-rw-dialog-close]").ClickAsync();
        await page.Locator("[data-rw-dialog][open]").WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });

        var handledHeaders = HandledValidationHeaders(oldInside.Correlation);
        var staleValidation = string.Concat(
            UpdateCommand(oldInside.Correlation, "dialog-errors", "late validation", "origin"),
            ProgressCommand(oldInside.Correlation, "late-validation"));
        oldInside.Respond(
            staleValidation,
            StatusCodes.Status422UnprocessableEntity,
            StreamContentType,
            handledHeaders);

        await WaitForProgressAsync(page, "late-validation");
        Assert.Equal(0, await page.Locator("[data-rw-dialog][open]").CountAsync());
        Assert.Equal(1, await page.Locator("[data-rw-dialog]").CountAsync());
        Assert.Equal("", (await page.Locator("[data-rw-dialog-body]").InnerTextAsync()).Trim());
    }

    [Fact]
    public async Task NewestConcurrentInsideSubmission_WinsDialogTargetWhileBothPageTargetsRun()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}{DialogPagePath}");
        await AddRaceProgressTargetAsync(page);

        await using var delayedStatus = await ControlledStreamRoute.InstallAsync(
            page,
            "**/Reactivity/DialogStatus*");
        await page.Locator(StatusLinkSelector).EvaluateAsync("link => link.click()");
        var opening = await delayedStatus.NextRequestAsync();
        opening.Respond(OpenCommand(opening.Correlation, "Concurrent form flow", DialogFormHtml("initial errors")));
        await WaitForDialogTitleAsync(page, "Concurrent form flow");
        var liveFlow = await ReadLiveFlowAsync(page);

        await using var delayedSubmissions = await ControlledStreamRoute.InstallAsync(
            page,
            "**/Reactivity/CompleteDialog");
        await page.Locator("[data-rw-dialog-body]").EvaluateAsync(
            "body => { const copy = document.querySelector('#dialog-form').cloneNode(true); copy.id = 'dialog-form-newer'; copy.querySelector('#dialog-errors').id = 'dialog-errors-newer'; copy.querySelector('#Name').id = 'Name-newer'; body.append(copy); " +
            "document.querySelector('#dialog-form').setAttribute('data-turbo-frame','dialog-inside-frame'); copy.setAttribute('data-turbo-frame','dialog-newer-frame'); document.querySelector('#dialog-form').requestSubmit(); copy.requestSubmit(); }");
        var first = await delayedSubmissions.NextRequestAsync();
        var second = await delayedSubmissions.NextRequestAsync();
        var older = first.Correlation.Order < second.Correlation.Order ? first : second;
        var newer = ReferenceEquals(older, first) ? second : first;

        Assert.Equal(liveFlow.ToString("D"), older.Correlation.Flow?.ToString("D"));
        Assert.Equal(liveFlow.ToString("D"), newer.Correlation.Flow?.ToString("D"));
        Assert.True(newer.Correlation.Order > older.Correlation.Order);
        newer.Respond(string.Concat(
                UpdateCommand(newer.Correlation, "dialog-errors", "newest validation", "origin"),
                ProgressCommand(newer.Correlation, "newer-form")),
            StatusCodes.Status422UnprocessableEntity,
            StreamContentType,
            HandledValidationHeaders(newer.Correlation));
        await WaitForTextAsync(page, "#dialog-errors", "newest validation");
        await WaitForProgressAsync(page, "newer-form");

        older.Respond(string.Concat(
                UpdateCommand(older.Correlation, "dialog-errors", "stale validation", "origin"),
                ProgressCommand(older.Correlation, "older-form")),
            StatusCodes.Status422UnprocessableEntity,
            StreamContentType,
            HandledValidationHeaders(older.Correlation));
        await WaitForProgressAsync(page, "older-form");

        Assert.Equal("newest validation", (await page.Locator("#dialog-errors").InnerTextAsync()).Trim());
        Assert.Equal("Concurrent form flow", await page.Locator("[data-rw-dialog-title]").InnerTextAsync());
        Assert.Equal(1, await page.Locator("[data-rw-dialog][open]").CountAsync());
    }

    [Fact]
    public async Task UnknownRequestTokenIsIgnoredAndDuplicateDialogCommandIsOneUse()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}{DialogPagePath}");
        await AddRaceProgressTargetAsync(page);

        await using var delayedStatus = await ControlledStreamRoute.InstallAsync(
            page,
            "**/Reactivity/DialogStatus*");
        await page.Locator(StatusLinkSelector).EvaluateAsync("link => link.click()");
        var unknownRequest = await delayedStatus.NextRequestAsync();
        var unknownToken = Guid.NewGuid();
        unknownRequest.Respond(string.Concat(
            OpenCommand(unknownRequest.Correlation, "Unknown token", "unknown body", requestOverride: unknownToken),
            ProgressCommand(unknownRequest.Correlation, "unknown-token", requestOverride: unknownToken)));

        await WaitForProgressAsync(page, "unknown-token");
        Assert.Equal(0, await page.Locator("[data-rw-dialog]").CountAsync());

        await page.Locator(StatusLinkSelector).EvaluateAsync("link => link.click()");
        var duplicateRequest = await delayedStatus.NextRequestAsync();
        duplicateRequest.Respond(string.Concat(
            OpenCommand(duplicateRequest.Correlation, "First command", "first body"),
            OpenCommand(duplicateRequest.Correlation, "Duplicate command", "duplicate body")));

        await WaitForDialogTitleAsync(page, "First command");
        Assert.Equal("First command", await page.Locator("[data-rw-dialog-title]").InnerTextAsync());
        Assert.Equal("first body", (await page.Locator("[data-rw-dialog-body]").InnerTextAsync()).Trim());
        Assert.Equal(1, await page.Locator("[data-rw-dialog]").CountAsync());
    }

    [Fact]
    public async Task UnhandledOldHttpOrNetworkFailure_CannotInjectGeneratedFallbackIntoReplacementErrorTarget()
    {
        await RunStaleFormFailureReplacementAsync(networkFailure: false);
        await RunStaleFormFailureReplacementAsync(networkFailure: true);
    }

    [Fact]
    public async Task EscapeCloseAndDisconnectedTrigger_RestoreFocusSafely()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}{DialogPagePath}");

        await ClickAndWaitForResponseAsync(page, StatusLinkSelector, DialogStatusPath, HttpMethod.Get.Method);
        await WaitForDialogTitleAsync(page, "Service status");
        Assert.Equal(
            "true",
            await page.EvaluateAsync<string>("() => document.activeElement?.matches('[data-rw-dialog-title]') ? 'true' : 'false'"));

        await page.Keyboard.PressAsync("Escape");
        await page.Locator("[data-rw-dialog][open]").WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
        Assert.Equal(
            "true",
            await page.EvaluateAsync<string>("() => document.activeElement?.matches(\"a[href*='/Reactivity/DialogStatus']\") ? 'true' : 'false'"));

        await ClickAndWaitForResponseAsync(page, StatusLinkSelector, DialogStatusPath, HttpMethod.Get.Method);
        await page.Locator("[data-rw-dialog-close]").ClickAsync();
        Assert.Equal(
            "true",
            await page.EvaluateAsync<string>("() => document.activeElement?.matches(\"a[href*='/Reactivity/DialogStatus']\") ? 'true' : 'false'"));

        await ClickAndWaitForResponseAsync(page, StatusLinkSelector, DialogStatusPath, HttpMethod.Get.Method);
        await page.Locator(StatusLinkSelector).EvaluateAsync("link => link.remove()");
        await page.Locator("[data-rw-dialog-close]").ClickAsync();
        Assert.Equal("main", await page.EvaluateAsync<string>("() => document.activeElement?.tagName.toLowerCase() || ''"));
        Assert.Equal(1, await page.Locator("[data-rw-dialog]").CountAsync());
    }

    [Fact]
    public async Task TurboNavigationAndCacheRestore_DoNotRestoreAnOpenDialog()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}{DialogPagePath}");
        await ClickAndWaitForResponseAsync(page, StatusLinkSelector, DialogStatusPath, HttpMethod.Get.Method);
        await WaitForDialogTitleAsync(page, "Service status");

        await page.EvaluateAsync(
            "() => { const link = document.createElement('a'); link.id = 'dialog-cache-navigation'; " +
            "link.href = '/Reactivity'; link.textContent = 'Navigate away'; document.body.append(link); link.click(); }");
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath.Equals("/Reactivity", StringComparison.Ordinal));
        Assert.Equal(0, await page.Locator("[data-rw-dialog]").CountAsync());

        await page.GoBackAsync();
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath.Equals(DialogPagePath, StringComparison.Ordinal));
        Assert.Equal(0, await page.Locator("[data-rw-dialog][open]").CountAsync());
        Assert.Equal(0, await page.Locator("[data-rw-dialog]").CountAsync());
    }

    [Fact]
    public async Task DialogShell_FitsNarrowViewportsScrollsLongBodyAndUsesThemeVariablesAndReducedMotion()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ReducedMotion = ReducedMotion.Reduce,
            ViewportSize = new ViewportSize { Width = 320, Height = 640 }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}{DialogPagePath}");

        await page.EvaluateAsync(
            "() => { document.documentElement.style.setProperty('--rw-dialog-width', '28rem'); " +
            "document.documentElement.style.setProperty('--rw-dialog-spacing', '7px'); " +
            "document.documentElement.style.setProperty('--rw-dialog-surface', 'rgb(1, 2, 3)'); }");
        await using var delayedStatus = await ControlledStreamRoute.InstallAsync(
            page,
            "**/Reactivity/DialogStatus*");
        await page.Locator(StatusLinkSelector).EvaluateAsync("link => link.click()");
        var opening = await delayedStatus.NextRequestAsync();
        var longBody = "<div id=\"dialog-long-body\">" +
                       string.Concat(Enumerable.Repeat("A response body that remains readable and scrollable at a narrow viewport. ", 140)) +
                       "</div>";
        opening.Respond(OpenCommand(opening.Correlation, "Responsive dialog", longBody));
        await WaitForDialogTitleAsync(page, "Responsive dialog");

        Assert.True(await IsDialogWithinViewportAsync(page, 320));
        Assert.True(await page.Locator("[data-rw-dialog-body]").EvaluateAsync<bool>(
            "body => body.scrollHeight > body.clientHeight && getComputedStyle(body).overflowY === 'auto'"));
        Assert.Equal("rgb(1, 2, 3)", await page.Locator("[data-rw-dialog]").EvaluateAsync<string>(
            "dialog => getComputedStyle(dialog).backgroundColor"));
        Assert.Equal("7px", await page.Locator("[data-rw-dialog-body]").EvaluateAsync<string>(
            "body => getComputedStyle(body).paddingTop"));
        Assert.True(await page.EvaluateAsync<bool>("() => matchMedia('(prefers-reduced-motion: reduce)').matches"));

        await page.SetViewportSizeAsync(375, 700);
        Assert.True(await IsDialogWithinViewportAsync(page, 375));

        await page.SetViewportSizeAsync(1280, 720);
        await page.Locator("[data-rw-dialog-body]").EvaluateAsync(
            "body => { body.replaceChildren(document.createTextNode('Short centered response.')); body.style.overflow = 'visible'; }");
        var centering = await page.Locator("[data-rw-dialog]").EvaluateAsync<DialogBounds>(
            "dialog => { const rect = dialog.getBoundingClientRect(); return { Left: rect.left, Top: rect.top, Width: rect.width, Height: rect.height }; }");
        Assert.InRange(Math.Abs(centering.Left + centering.Width / 2 - 640), 0, 1);
        Assert.InRange(Math.Abs(centering.Top + centering.Height / 2 - 360), 0, 1);
    }

    [Fact]
    public async Task DialogStylesheetWorksUnderStyleRestrictedCspWithoutInlineStyles()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.RouteAsync(
            $"**{DialogPagePath}",
            async route =>
            {
                var response = await route.FetchAsync();
                var html = await response.TextAsync();
                const string policy = "default-src 'self'; script-src 'self' 'unsafe-inline'; " +
                                      "style-src 'self'; style-src-attr 'none'; style-src-elem 'self'; " +
                                      "connect-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'self'";
                var policyMeta = $"<meta http-equiv=\"Content-Security-Policy\" content=\"{policy}\">";
                html = Regex.Replace(
                    html,
                    "<head(?<attributes>[^>]*)>",
                    match => $"{match.Value}{policyMeta}",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1));
                var headers = response.Headers.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase);
                headers.Remove("content-length");
                headers.Remove("content-encoding");
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = response.Status,
                    Body = html,
                    Headers = headers
                });
            });

        await page.GotoAsync($"{_fixture.BaseUrl}{DialogPagePath}");
        await page.WaitForFunctionAsync(
            "() => [...document.styleSheets].some(sheet => sheet.href?.includes('/_content/ForgeTrust.RazorWire/razorwire/razorwire-dialog.css'))");
        await ClickAndWaitForResponseAsync(page, StatusLinkSelector, DialogStatusPath, HttpMethod.Get.Method);
        await WaitForDialogTitleAsync(page, "Service status");

        var contract = await page.EvaluateAsync<DialogCspProbe>(
            "() => { const dialog = document.querySelector('[data-rw-dialog]'); " +
            "const link = [...document.querySelectorAll('link[rel=stylesheet]')].find(item => item.href.includes('razorwire-dialog.css')); " +
            "return { HasSameOriginStylesheet: !!link && new URL(link.href).origin === location.origin, " +
            "HasInlineDialogStyle: !!dialog?.hasAttribute('style') || !!dialog?.querySelector('[style]'), " +
            "CspActive: !!document.querySelector('meta[http-equiv=\"Content-Security-Policy\"]') }; }");

        Assert.True(contract.CspActive);
        Assert.True(contract.HasSameOriginStylesheet);
        Assert.False(contract.HasInlineDialogStyle);
    }

    [Fact]
    public async Task CancelledInsideLinkConfirmation_DoesNotPoisonTheNextOutsideRequestToTheSameUrl()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}{DialogPagePath}");
        await ClickAndWaitForResponseAsync(page, StatusLinkSelector, DialogStatusPath, HttpMethod.Get.Method);
        await WaitForDialogTitleAsync(page, "Service status");
        await page.Locator("[data-rw-dialog-body]").EvaluateAsync(
            "body => { const link = document.createElement('a'); link.id='cancelled-dialog-link'; " +
            "link.href='/Reactivity/DialogStatus'; link.dataset.turboStream=''; link.dataset.turboConfirm='Refresh?'; body.append(link); }");
        var confirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.Dialog += async (_, dialog) => { await dialog.DismissAsync(); confirmation.TrySetResult(); };
        await page.Locator("#cancelled-dialog-link").EvaluateAsync("link => link.click()");
        await confirmation.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await page.Locator("[data-rw-dialog-close]").ClickAsync();
        var response = await ClickAndWaitForResponseAsync(page, StatusLinkSelector, DialogStatusPath, HttpMethod.Get.Method);
        Assert.Null((await ReadCorrelationAsync(response.Request)).Flow);
        await WaitForDialogTitleAsync(page, "Service status");
        await page.Locator("[data-rw-dialog-close]").ClickAsync();
        Assert.True(await page.Locator(StatusLinkSelector).EvaluateAsync<bool>("link => document.activeElement === link"));
    }

    [Fact]
    public async Task RetiredShellCloseEvent_DoesNotDismissTheNewShellAfterCacheCleanup()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}{DialogPagePath}");
        await ClickAndWaitForResponseAsync(page, StatusLinkSelector, DialogStatusPath, HttpMethod.Get.Method);
        await WaitForDialogTitleAsync(page, "Service status");
        await page.EvaluateAsync(
            "() => { window.retiredDialogShell = document.querySelector('[data-rw-dialog]'); " +
            "document.dispatchEvent(new CustomEvent('turbo:before-cache')); }");
        await ClickAndWaitForResponseAsync(page, StatusLinkSelector, DialogStatusPath, HttpMethod.Get.Method);
        await WaitForDialogTitleAsync(page, "Service status");
        var flow = await ReadLiveFlowAsync(page);
        await page.EvaluateAsync("() => window.retiredDialogShell.dispatchEvent(new Event('close'))");
        Assert.Equal(flow, await ReadLiveFlowAsync(page));
        Assert.Equal(1, await page.Locator("[data-rw-dialog][open]").CountAsync());
    }

    [Fact]
    public async Task LaterTurboRenderExtension_DoesNotStrandDialogRenderQueue()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}{DialogPagePath}");
        await page.EvaluateAsync(
            "() => document.addEventListener('turbo:before-stream-render', event => { " +
            "if (event.detail.newStream.getAttribute('action') !== 'rw-dialog') return; " +
            "const original = event.detail.render; event.detail.render = async stream => { " +
            "await new Promise(resolve => setTimeout(resolve, 10)); await original(stream); }; })");
        await using var route = await ControlledStreamRoute.InstallAsync(page, "**/Reactivity/DialogStatus*");
        await page.Locator(StatusLinkSelector).ClickAsync();
        var request = await route.NextRequestAsync();
        request.Respond(string.Concat(
            OpenCommand(request.Correlation, "Extension ordered shell", "<div id=\"extension-target\">Before</div>"),
            UpdateCommand(request.Correlation, "extension-target", "After extension", "new")));
        await WaitForDialogTitleAsync(page, "Extension ordered shell");
        await WaitForTextAsync(page, "#extension-target", "After extension");
    }

    [Fact]
    public async Task SuccessfulDialogUpdate_DoesNotRefocusAnOldValidationMarker()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}{DialogPagePath}");
        await OpenSaveDialogAsync(page);
        await WaitForDialogTitleAsync(page, "Complete the save");
        await SubmitAndWaitForResponseAsync(page, "#dialog-form", CompleteDialogPath, StatusCodes.Status422UnprocessableEntity);
        await WaitForTextAsync(page, "#dialog-errors", "required");
        await page.Locator("[data-rw-dialog-body]").EvaluateAsync(
            "body => { const target = document.createElement('div'); target.id='successful-dialog-update'; body.append(target); }");
        await using var route = await ControlledStreamRoute.InstallAsync(page, "**/Reactivity/CompleteDialog");
        await page.Locator("[data-rw-dialog-close]").FocusAsync();
        await page.Locator("#dialog-form").EvaluateAsync("form => form.requestSubmit()");
        var request = await route.NextRequestAsync();
        request.Respond(UpdateCommand(request.Correlation, "successful-dialog-update", "Success without new validation", "origin"));
        await WaitForTextAsync(page, "#successful-dialog-update", "Success without new validation");
        Assert.True(await page.Locator("[data-rw-dialog-close]").EvaluateAsync<bool>("button => document.activeElement === button"));
    }

    private async Task RunStaleFormFailureReplacementAsync(bool networkFailure)
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}{DialogPagePath}");
        await AddRaceProgressTargetAsync(page);
        await OpenSaveDialogAsync(page);

        await using var delayedFailure = await ControlledStreamRoute.InstallAsync(
            page,
            "**/Reactivity/CompleteDialog");
        await page.Locator("#dialog-form").EvaluateAsync(
            "form => { form.setAttribute('data-turbo-frame','dialog-inside-frame'); form.dataset.testStaleFailure = 'true'; " +
            "document.addEventListener('turbo:submit-end', event => { if (event.detail.formSubmission.formElement === form) window.__dialogStaleFailureFinished = true; }); " +
            "form.requestSubmit(); }");
        var staleFormRequest = await delayedFailure.NextRequestAsync();

        await using var delayedReplacement = await ControlledStreamRoute.InstallAsync(
            page,
            "**/Reactivity/DialogStatus*");
        await page.Locator(StatusLinkSelector).EvaluateAsync("link => link.click()");
        var replacementRequest = await delayedReplacement.NextRequestAsync();
        replacementRequest.Respond(OpenCommand(
            replacementRequest.Correlation,
            "Replacement after failed submit",
            DialogFormHtml("Current error target")));
        await WaitForDialogTitleAsync(page, "Replacement after failed submit");
        Assert.Equal("Current error target", (await page.Locator("#dialog-errors").InnerTextAsync()).Trim());

        if (networkFailure)
        {
            staleFormRequest.Abort();
        }
        else
        {
            staleFormRequest.Respond("upstream failure", StatusCodes.Status500InternalServerError, "text/plain; charset=utf-8");
        }

        await page.WaitForFunctionAsync(
            "() => window.__dialogStaleFailureFinished === true",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
        Assert.Equal("Current error target", (await page.Locator("#dialog-errors").InnerTextAsync()).Trim());
        Assert.Equal(0, await page.Locator("[data-rw-dialog] [data-rw-form-error-generated='true']").CountAsync());
        Assert.Equal("Replacement after failed submit", await page.Locator("[data-rw-dialog-title]").InnerTextAsync());
    }

    private static async Task<IResponse> ClickAndWaitForResponseAsync(
        IPage page,
        string triggerSelector,
        string path,
        string method)
    {
        return await page.RunAndWaitForResponseAsync(
            () => page.Locator(triggerSelector).ClickAsync(),
            response => response.Url.Contains(path, StringComparison.OrdinalIgnoreCase)
                        && response.Request.Method.Equals(method, StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions { Timeout = 45_000 });
    }

    private static async Task<IResponse> SubmitAndWaitForResponseAsync(
        IPage page,
        string formSelector,
        string path,
        int? expectedStatus)
    {
        return await page.RunAndWaitForResponseAsync(
            () => page.Locator(formSelector).Locator("button[type=submit]").ClickAsync(),
            response => response.Url.Contains(path, StringComparison.OrdinalIgnoreCase)
                        && response.Request.Method.Equals(HttpMethod.Post.Method, StringComparison.OrdinalIgnoreCase)
                        && (!expectedStatus.HasValue || response.Status == expectedStatus.Value),
            new PageRunAndWaitForResponseOptions { Timeout = 45_000 });
    }

    private static async Task<RequestCorrelation> ReadCorrelationAsync(IRequest request)
    {
        var headers = await request.AllHeadersAsync();
        var requestValue = HeaderValue(headers, "X-RazorWire-Request");
        var orderValue = HeaderValue(headers, "X-RazorWire-Order");
        if (!Guid.TryParse(requestValue, out var requestId))
        {
            throw new InvalidOperationException($"Expected a UUID X-RazorWire-Request header, received '{requestValue ?? "<missing>"}'.");
        }

        if (!long.TryParse(orderValue, NumberStyles.None, CultureInfo.InvariantCulture, out var order)
            || order <= 0
            || order > MaximumJavaScriptSafeInteger)
        {
            throw new InvalidOperationException($"Expected a positive JavaScript-safe X-RazorWire-Order, received '{orderValue ?? "<missing>"}'.");
        }

        var flowValue = HeaderValue(headers, "X-RazorWire-Flow");
        Guid? flow = null;
        if (!string.IsNullOrWhiteSpace(flowValue))
        {
            if (!Guid.TryParse(flowValue, out var parsedFlow))
            {
                throw new InvalidOperationException($"Expected an optional UUID X-RazorWire-Flow header, received '{flowValue}'.");
            }

            flow = parsedFlow;
        }

        return new RequestCorrelation(requestId, order, flow);
    }

    private static string? HeaderValue(IReadOnlyDictionary<string, string> headers, string name)
    {
        return headers.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static async Task AssertCorrelationAttributesAsync(
        IResponse response,
        RequestCorrelation correlation,
        string phase)
    {
        var body = await response.TextAsync();
        Assert.Contains($"data-rw-request=\"{correlation.Request:D}\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"data-rw-order=\"{correlation.Order.ToString(CultureInfo.InvariantCulture)}\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"data-rw-dialog-phase=\"{phase}\"", body, StringComparison.OrdinalIgnoreCase);
        if (correlation.Flow is { } flow)
        {
            Assert.Contains($"data-rw-flow=\"{flow:D}\"", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task<Guid> ReadLiveFlowAsync(IPage page)
    {
        var value = await page.Locator("[data-rw-dialog-body]").GetAttributeAsync("data-rw-dialog-flow");
        if (!Guid.TryParse(value, out var flow))
        {
            throw new InvalidOperationException($"Expected the live dialog to expose a flow UUID, received '{value ?? "<missing>"}'.");
        }

        return flow;
    }

    private static Dictionary<string, string> HandledValidationHeaders(RequestCorrelation correlation)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-RazorWire-Request"] = correlation.Request.ToString("D"),
            ["X-RazorWire-Form-Handled"] = "true"
        };
    }

    private static async Task<IResponse> OpenSaveDialogAsync(IPage page)
    {
        return await SubmitAndWaitForResponseAsync(
            page,
            $"form[action*='{SaveDialogPath}']",
            SaveDialogPath,
            StatusCodes.Status200OK);
    }

    private static Task AddRaceProgressTargetAsync(IPage page)
    {
        return page.EvaluateAsync(
            "() => { if (!document.getElementById('dialog-race-progress')) { " +
            "const target = document.createElement('div'); target.id = 'dialog-race-progress'; document.body.append(target); } for (const id of ['dialog-outside-frame','dialog-inside-frame','dialog-newer-frame']) { const frame = document.createElement('turbo-frame'); frame.id=id; document.body.append(frame); } document.querySelector(\"a[href*='/Reactivity/DialogStatus']\").setAttribute('data-turbo-frame','dialog-outside-frame'); }");
    }

    private static Task AddRepeatedStatusLinksAsync(IPage page)
    {
        return page.EvaluateAsync(
            "() => { for (const id of ['dialog-race-open-one', 'dialog-race-open-two']) { " +
            "const link = document.createElement('a'); link.id = id; link.href = '/Reactivity/DialogStatus?race=outside'; " +
            "link.setAttribute('data-turbo-stream', ''); const frame = document.createElement('turbo-frame'); frame.id = id + '-frame'; document.body.append(frame); link.setAttribute('data-turbo-frame', frame.id); link.textContent = id; document.body.append(link); } }");
    }

    private static string OpenWithPageProgress(
        RequestCorrelation correlation,
        string title,
        string body,
        string progress)
    {
        return string.Concat(
            OpenCommand(correlation, title, body),
            ProgressCommand(correlation, progress));
    }

    private static string OpenCommand(
        RequestCorrelation correlation,
        string title,
        string body,
        Guid? requestOverride = null)
    {
        return $"<turbo-stream action=\"rw-dialog\" dialog-command=\"open\" dialog-title=\"{WebUtility.HtmlEncode(title)}\"{MetadataAttributes(correlation, "origin", requestOverride)}><template>{body}</template></turbo-stream>";
    }

    private static string CloseCommand(RequestCorrelation correlation)
    {
        return $"<turbo-stream action=\"rw-dialog\" dialog-command=\"close\"{MetadataAttributes(correlation, "closed")}><template></template></turbo-stream>";
    }

    private static string UpdateCommand(
        RequestCorrelation correlation,
        string target,
        string body,
        string phase,
        Guid? requestOverride = null)
    {
        return $"<turbo-stream action=\"update\" target=\"{WebUtility.HtmlEncode(target)}\"{MetadataAttributes(correlation, phase, requestOverride)}><template>{body}</template></turbo-stream>";
    }

    private static string ProgressCommand(
        RequestCorrelation correlation,
        string value,
        Guid? requestOverride = null)
    {
        var encoded = WebUtility.HtmlEncode(value);
        return UpdateCommand(
            correlation,
            "dialog-race-progress",
            $"<span data-response-order=\"{encoded}\">{encoded}</span>",
            "origin",
            requestOverride);
    }

    private static string MetadataAttributes(
        RequestCorrelation correlation,
        string phase,
        Guid? requestOverride = null)
    {
        var request = (requestOverride ?? correlation.Request).ToString("D");
        var flow = correlation.Flow is { } flowId ? $" data-rw-flow=\"{flowId:D}\"" : string.Empty;
        return $" data-rw-request=\"{request}\" data-rw-order=\"{correlation.Order.ToString(CultureInfo.InvariantCulture)}\"{flow} data-rw-dialog-phase=\"{phase}\"";
    }

    private static string DialogFormHtml(string errorText)
    {
        return "<form id=\"dialog-form\" method=\"post\" action=\"/Reactivity/CompleteDialog\" data-rw-form=\"true\" data-rw-loading=\"true\" data-rw-loading-lock=\"false\" data-rw-disable-submit=\"false\" data-rw-form-failure-target=\"dialog-errors\">" +
               "<input id=\"Name\" name=\"Name\" value=\"old flow\">" +
               "<input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"mock-token\">" +
               $"<div id=\"dialog-errors\">{WebUtility.HtmlEncode(errorText)}</div>" +
               "<button type=\"submit\">Complete request</button></form>";
    }

    private static async Task WaitForDialogTitleAsync(IPage page, string title)
    {
        await page.WaitForFunctionAsync(
            "title => { const shell = document.querySelector('[data-rw-dialog][open]'); " +
            "return !!shell && shell.querySelector('[data-rw-dialog-title]')?.textContent === title; }",
            title,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
    }

    private static Task WaitForTextAsync(IPage page, string selector, string text)
    {
        return page.WaitForFunctionAsync(
            "expected => { const target = document.querySelector(" + JavaScriptString(selector) + "); " +
            "return !!target && target.textContent?.toLowerCase().includes(expected.toLowerCase()); }",
            text,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
    }

    private static Task WaitForProgressAsync(IPage page, string value)
    {
        return page.WaitForFunctionAsync(
            "expected => document.querySelector('#dialog-race-progress [data-response-order]')?.getAttribute('data-response-order') === expected",
            value,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
    }

    private static string JavaScriptString(string value)
    {
        return System.Text.Json.JsonSerializer.Serialize(value);
    }

    private static Task<bool> IsDialogWithinViewportAsync(IPage page, int viewportWidth)
    {
        return page.EvaluateAsync<bool>(
            "width => { const dialog = document.querySelector('[data-rw-dialog][open]'); if (!dialog) return false; " +
            "const rect = dialog.getBoundingClientRect(); return rect.left >= 0 && rect.right <= width && " +
            "rect.width <= width && dialog.scrollWidth <= dialog.clientWidth; }",
            viewportWidth);
    }

    private sealed class DialogCspProbe
    {
        public bool HasSameOriginStylesheet { get; set; }

        public bool HasInlineDialogStyle { get; set; }

        public bool CspActive { get; set; }
    }

    private sealed class DialogBounds
    {
        public double Left { get; set; }

        public double Top { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }
    }

    private sealed record RequestCorrelation(Guid Request, long Order, Guid? Flow);

    private sealed class ControlledStreamRoute : IAsyncDisposable
    {
        private readonly IPage _page;
        private readonly string _urlPattern;
        private readonly Channel<PendingStreamRequest> _arrivals = Channel.CreateUnbounded<PendingStreamRequest>();
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Func<IRoute, Task> _handler;

        private ControlledStreamRoute(IPage page, string urlPattern)
        {
            _page = page;
            _urlPattern = urlPattern;
            _handler = HandleAsync;
        }

        public static async Task<ControlledStreamRoute> InstallAsync(IPage page, string urlPattern)
        {
            var controlled = new ControlledStreamRoute(page, urlPattern);
            await page.RouteAsync(urlPattern, controlled._handler);
            return controlled;
        }

        public async Task<PendingStreamRequest> NextRequestAsync()
        {
            return await _arrivals.Reader.ReadAsync(_shutdown.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            try
            {
                await _page.UnrouteAsync(_urlPattern, _handler);
            }
            catch (PlaywrightException)
            {
                // The owning browser context may already be closing after a failed assertion.
            }

            _shutdown.Dispose();
        }

        private async Task HandleAsync(IRoute route)
        {
            try
            {
                var correlation = await ReadCorrelationAsync(route.Request);
                var pending = new PendingStreamRequest(route, correlation);
                await _arrivals.Writer.WriteAsync(pending, _shutdown.Token);
                var reply = await pending.Reply.Task.WaitAsync(_shutdown.Token);
                if (reply.Abort)
                {
                    await route.AbortAsync();
                    return;
                }

                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = reply.Status,
                    ContentType = reply.ContentType,
                    Body = reply.Body,
                    Headers = reply.Headers
                });
            }
            catch (OperationCanceledException)
            {
                try
                {
                    await route.AbortAsync();
                }
                catch (PlaywrightException)
                {
                    // The request was already completed or its browser context closed.
                }
            }
        }
    }

    private sealed class PendingStreamRequest
    {
        public PendingStreamRequest(IRoute route, RequestCorrelation correlation)
        {
            Route = route;
            Correlation = correlation;
        }

        internal IRoute Route { get; }

        public RequestCorrelation Correlation { get; }

        internal TaskCompletionSource<RouteReply> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Respond(
            string body,
            int status = StatusCodes.Status200OK,
            string contentType = StreamContentType,
            Dictionary<string, string>? headers = null)
        {
            Reply.TrySetResult(new RouteReply(body, status, contentType, headers ?? new Dictionary<string, string>(), false));
        }

        public void Abort()
        {
            Reply.TrySetResult(new RouteReply(string.Empty, StatusCodes.Status200OK, StreamContentType, new Dictionary<string, string>(), true));
        }
    }

    private sealed record RouteReply(
        string Body,
        int Status,
        string ContentType,
        Dictionary<string, string> Headers,
        bool Abort);
}
