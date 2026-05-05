using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using ForgeTrust.Runnable.Core;
using Microsoft.Playwright;

namespace ForgeTrust.Runnable.Web.RazorWire.IntegrationTests;

[Collection(RazorWireIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RazorWireMvcPlaywrightTests
{
    private readonly RazorWireMvcPlaywrightFixture _fixture;

    public RazorWireMvcPlaywrightTests(RazorWireMvcPlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PublishMessage_BroadcastsToOtherSession()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var username = $"playwright-{unique}";
        var message = $"hello from playwright {unique}";

        await using var senderContext = await _fixture.Browser.NewContextAsync();
        await using var receiverContext = await _fixture.Browser.NewContextAsync();

        var senderPage = await senderContext.NewPageAsync();
        var receiverPage = await receiverContext.NewPageAsync();

        await senderPage.GotoAsync(_fixture.ReactivityUrl);
        await receiverPage.GotoAsync(_fixture.ReactivityUrl);

        await WaitForStreamConnectedAsync(senderPage);
        await WaitForStreamConnectedAsync(receiverPage);
        await WaitForUserListReadyAsync(senderPage);
        await WaitForUserListReadyAsync(receiverPage);

        var registerResponse = await RegisterUserAndWaitForPostAsync(senderPage, username);
        Assert.True(registerResponse.Ok, $"RegisterUser POST failed with status {(int)registerResponse.Status}.");

        await WaitForUserInListAsync(receiverPage, username);

        var publishResponse = await PublishMessageAndWaitForPostAsync(senderPage, message);
        Assert.True(publishResponse.Ok, $"PublishMessage POST failed with status {(int)publishResponse.Status}.");

        await WaitForMessageAsync(receiverPage, unique);
        await WaitForMessageAsync(senderPage, unique);
    }

    [Fact]
    public async Task PublishMessage_PersistsAfterNavigatingAwayAndBack()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var message = $"persist after nav {unique}";

        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.ReactivityUrl);
        await WaitForStreamConnectedAsync(page);

        var publishResponse = await PublishMessageAndWaitForPostAsync(page, message);
        Assert.True(publishResponse.Ok, $"PublishMessage POST failed with status {(int)publishResponse.Status}.");
        await WaitForMessageAsync(page, unique);

        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Home", Exact = true }).First.ClickAsync();
        await WaitForPathAsync(page, "/");

        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Reactivity", Exact = true }).First.ClickAsync();
        await WaitForPathAsync(page, "/Reactivity");
        await WaitForStreamConnectedAsync(page);
        await WaitForMessageAsync(page, unique);
    }

    [Fact]
    public async Task PublishMessage_PersistsAfterFullReload()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var message = $"persist after reload {unique}";

        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.ReactivityUrl);
        await WaitForStreamConnectedAsync(page);

        var publishResponse = await PublishMessageAndWaitForPostAsync(page, message);
        Assert.True(publishResponse.Ok, $"PublishMessage POST failed with status {(int)publishResponse.Status}.");
        await WaitForMessageAsync(page, unique);

        await page.ReloadAsync();
        await WaitForPathAsync(page, "/Reactivity");
        await WaitForStreamConnectedAsync(page);
        await WaitForMessageAsync(page, unique);
    }

    [Fact]
    public async Task RegisterTwoUsers_FromSingleSession_WithoutRefresh_AntiforgeryAllowsBothPosts()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var userOne = $"user-a-{unique}";
        var userTwo = $"user-b-{unique}";

        await using var actorContext = await _fixture.Browser.NewContextAsync();
        await using var observerContext = await _fixture.Browser.NewContextAsync();

        var actorPage = await actorContext.NewPageAsync();
        var observerPage = await observerContext.NewPageAsync();

        await actorPage.GotoAsync(_fixture.ReactivityUrl);
        await observerPage.GotoAsync(_fixture.ReactivityUrl);

        await WaitForStreamConnectedAsync(actorPage);
        await WaitForStreamConnectedAsync(observerPage);
        await WaitForUserListReadyAsync(actorPage);
        await WaitForUserListReadyAsync(observerPage);

        await PlantNoRefreshMarkerAsync(actorPage);

        var registerOneResponse = await RegisterUserAndWaitForPostAsync(actorPage, userOne);
        Assert.True(registerOneResponse.Ok, $"First RegisterUser POST failed with status {(int)registerOneResponse.Status}.");
        await WaitForUserInListAsync(observerPage, userOne);

        var registerTwoResponse = await RegisterUserAndWaitForPostAsync(actorPage, userTwo);
        Assert.True(registerTwoResponse.Ok, $"Second RegisterUser POST failed with status {(int)registerTwoResponse.Status}.");
        await WaitForUserInListAsync(observerPage, userTwo);

        await AssertNoPageRefreshAsync(actorPage, _fixture.ReactivityUrl);
    }

    [Fact]
    public async Task RegisterUser_WithoutAntiforgeryToken_IsRejected()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.ReactivityUrl);
        await WaitForStreamConnectedAsync(page);

        var response = await page.EvaluateAsync<int>(
            @"async () => {
                const body = new URLSearchParams({ username: 'invalid-no-token' });
                const res = await fetch('/Reactivity/RegisterUser', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                    body
                });
                return res.status;
            }");

        Assert.Equal(400, response);
    }

    [Fact]
    public async Task IncrementCounter_ButtonHasAccessibleName()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.ReactivityUrl);
        await WaitForCounterReadyAsync(page);

        var incrementButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = "Increment counter",
            Exact = true
        });

        await incrementButton.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000
        });
    }

    [Fact]
    public async Task ReadmeQuickstart_ReactivityCounter_UpdatesScoresWithoutRefresh()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.ReactivityUrl);
        await WaitForStreamConnectedAsync(page);
        await WaitForCounterReadyAsync(page);

        await PlantNoRefreshMarkerAsync(page);
        var initialInstance = await GetIntTextAsync(page, "#instance-score-value");
        var initialSession = await GetIntTextAsync(page, "#session-score-value");
        var initialClientCount = await GetIntInputValueAsync(page, "#client-count-input");

        var incrementButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = "Increment counter",
            Exact = true
        });
        var incrementResponse = await page.RunAndWaitForResponseAsync(
            () => incrementButton.ClickAsync(),
            response => response.Url.Contains("/Reactivity/IncrementCounter", StringComparison.OrdinalIgnoreCase)
                        && response.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions { Timeout = 45_000 });

        Assert.True(incrementResponse.Ok, $"README quickstart IncrementCounter POST failed with status {(int)incrementResponse.Status}.");
        await ExpectCounterValuesAsync(
            page,
            expectedInstance: initialInstance + 1,
            expectedSession: initialSession + 1,
            expectedClientCount: initialClientCount + 1);
        await AssertNoPageRefreshAsync(page, _fixture.ReactivityUrl);
    }

    [Fact]
    public async Task IncrementCounter_SingleSession_UpdatesValuesWithoutRefresh()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.ReactivityUrl);
        await WaitForStreamConnectedAsync(page);
        await WaitForCounterReadyAsync(page);

        await PlantNoRefreshMarkerAsync(page);
        var initialInstance = await GetIntTextAsync(page, "#instance-score-value");
        var initialSession = await GetIntTextAsync(page, "#session-score-value");
        var initialClientCount = await GetIntInputValueAsync(page, "#client-count-input");

        var firstResponse = await SubmitAndWaitForPostAsync(page, "[data-counter-form]", "/Reactivity/IncrementCounter");
        Assert.True(firstResponse.Ok, $"First IncrementCounter POST failed with status {(int)firstResponse.Status}.");
        await ExpectCounterValuesAsync(
            page,
            expectedInstance: initialInstance + 1,
            expectedSession: initialSession + 1,
            expectedClientCount: initialClientCount + 1);

        var secondResponse = await SubmitAndWaitForPostAsync(page, "[data-counter-form]", "/Reactivity/IncrementCounter");
        Assert.True(secondResponse.Ok, $"Second IncrementCounter POST failed with status {(int)secondResponse.Status}.");
        await ExpectCounterValuesAsync(
            page,
            expectedInstance: initialInstance + 2,
            expectedSession: initialSession + 2,
            expectedClientCount: initialClientCount + 2);

        await AssertNoPageRefreshAsync(page, _fixture.ReactivityUrl);
    }

    [Fact]
    public async Task IncrementCounter_MultiSession_TracksSessionIndependentlyAndInstanceGlobally()
    {
        await using var firstContext = await _fixture.Browser.NewContextAsync();
        await using var secondContext = await _fixture.Browser.NewContextAsync();

        var firstPage = await firstContext.NewPageAsync();
        var secondPage = await secondContext.NewPageAsync();

        await firstPage.GotoAsync(_fixture.ReactivityUrl);
        await secondPage.GotoAsync(_fixture.ReactivityUrl);

        await WaitForStreamConnectedAsync(firstPage);
        await WaitForStreamConnectedAsync(secondPage);
        await WaitForCounterReadyAsync(firstPage);
        await WaitForCounterReadyAsync(secondPage);

        var firstInitialInstance = await GetIntTextAsync(firstPage, "#instance-score-value");
        var secondInitialInstance = await GetIntTextAsync(secondPage, "#instance-score-value");
        var globalBaseline = Math.Max(firstInitialInstance, secondInitialInstance);

        var firstIncrementResponse = await SubmitAndWaitForPostAsync(firstPage, "[data-counter-form]", "/Reactivity/IncrementCounter");
        Assert.True(firstIncrementResponse.Ok, $"First session IncrementCounter POST failed with status {(int)firstIncrementResponse.Status}.");
        await ExpectCounterValuesAsync(firstPage, expectedInstance: globalBaseline + 1, expectedSession: 1, expectedClientCount: 1);

        var secondIncrementResponse = await SubmitAndWaitForPostAsync(secondPage, "[data-counter-form]", "/Reactivity/IncrementCounter");
        Assert.True(secondIncrementResponse.Ok, $"Second session IncrementCounter POST failed with status {(int)secondIncrementResponse.Status}.");
        await ExpectCounterValuesAsync(secondPage, expectedInstance: globalBaseline + 2, expectedSession: 1, expectedClientCount: 1);
    }

    [Fact]
    public async Task IncrementCounter_SessionScorePersists_AcrossAllPageNavigation()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.ReactivityUrl);
        await WaitForStreamConnectedAsync(page);
        await WaitForCounterReadyAsync(page);

        var incrementResponse = await SubmitAndWaitForPostAsync(page, "[data-counter-form]", "/Reactivity/IncrementCounter");
        Assert.True(incrementResponse.Ok, $"IncrementCounter POST failed with status {(int)incrementResponse.Status}.");
        await ExpectCounterValuesAsync(page, expectedSession: 1);

        await NavigateViaHeaderAndAssertSessionScoreAsync(page, linkText: "Home", expectedPath: "/", expectedSessionScore: 1);
        await NavigateViaHeaderAndAssertSessionScoreAsync(page, linkText: "Navigation", expectedPath: "/Navigation", expectedSessionScore: 1);
        await NavigateViaHeaderAndAssertSessionScoreAsync(page, linkText: "Reactivity", expectedPath: "/Reactivity", expectedSessionScore: 1);
    }

    [Fact]
    public async Task UnknownRoute_ShowsBrandedNotFoundPage_With404Status()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        var missingUrl = $"{_fixture.BaseUrl}/missing-playwright-route";
        var response = await page.GotoAsync(missingUrl);

        Assert.NotNull(response);
        Assert.Equal(404, response!.Status);
        Assert.Equal(missingUrl, page.Url);

        await page.WaitForSelectorAsync("h1", new PageWaitForSelectorOptions
        {
            Timeout = 15_000
        });

        var heading = await page.TextContentAsync("h1");
        Assert.NotNull(heading);
        Assert.Contains("drifted out of orbit", heading, StringComparison.OrdinalIgnoreCase);

        var snapshot = await page.TextContentAsync("main");
        Assert.NotNull(snapshot);
        Assert.Contains("/missing-playwright-route", snapshot, StringComparison.Ordinal);

        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Return Home", Exact = true }).ClickAsync();
        await WaitForPathAsync(page, "/");
    }

    [Fact]
    public async Task FormFailures_ServerValidation_RendersHandledLocalErrorWithoutRefresh()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.FormFailuresUrl);
        await PlantNoRefreshMarkerAsync(page);

        var response = await SubmitAndWaitForPostAsync(
            page,
            "form[action*='SubmitValidationFailure']",
            "/Reactivity/SubmitValidationFailure");

        Assert.Equal(422, response.Status);
        Assert.Equal("true", await response.HeaderValueAsync("X-RazorWire-Form-Handled"));
        await WaitForTextAsync(page, "#validation-errors", "Display name is required.");
        await AssertNoPageRefreshAsync(page, _fixture.FormFailuresUrl);
    }

    [Fact]
    public async Task FormFailures_ServerValidation_TrimsDisplayNameBeforeLengthValidation()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.FormFailuresUrl);
        await PlantNoRefreshMarkerAsync(page);
        await page.FillAsync("#failure-display-name", "  12345678901234567890  ");

        var response = await SubmitAndWaitForPostAsync(
            page,
            "form[action*='SubmitValidationFailure']",
            "/Reactivity/SubmitValidationFailure");

        Assert.Equal(200, response.Status);
        await WaitForTextAsync(page, "#validation-result", "Saved 12345678901234567890.");
        var validationErrors = await page.Locator("#validation-errors").InnerTextAsync();
        Assert.DoesNotContain("Display name must be 20 characters or fewer.", validationErrors);
        await AssertNoPageRefreshAsync(page, _fixture.FormFailuresUrl);
    }

    [Fact]
    public async Task FormFailures_MissingAntiforgery_RendersDevelopmentDiagnosticInLocalTarget()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.FormFailuresUrl);
        await PlantNoRefreshMarkerAsync(page);

        var response = await SubmitAndWaitForPostAsync(
            page,
            "form[action*='AntiforgeryFailureDemo']",
            "/Reactivity/AntiforgeryFailureDemo");

        Assert.Equal(400, response.Status);
        Assert.Equal("true", await response.HeaderValueAsync("X-RazorWire-Form-Handled"));
        await WaitForTextAsync(page, "#antiforgery-errors", "Antiforgery token validation failed");
        await AssertNoPageRefreshAsync(page, _fixture.FormFailuresUrl);
    }

    [Fact]
    public async Task FormFailures_AuthorizationFailure_RendersRuntimeFallbackWithoutRefresh()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.FormFailuresUrl);
        await PlantNoRefreshMarkerAsync(page);

        var response = await SubmitAndWaitForPostAsync(
            page,
            "form[data-rw-form-failure-target='authorization-errors']",
            "/Reactivity/AuthorizationFailureDemo");

        Assert.Equal(403, response.Status);
        Assert.Null(await response.HeaderValueAsync("X-RazorWire-Form-Handled"));
        await WaitForTextAsync(page, "#authorization-errors", "Session may have expired");
        await WaitForTextAsync(page, "#authorization-errors", "You may need to refresh or sign in again before submitting this form.");
        await AssertNoPageRefreshAsync(page, _fixture.FormFailuresUrl);
    }

    [Fact]
    public async Task FormFailures_MalformedFailure_RendersRuntimeFallbackWithoutRefresh()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.FormFailuresUrl);
        await PlantNoRefreshMarkerAsync(page);

        var response = await SubmitAndWaitForPostAsync(
            page,
            "form[data-rw-form-failure-target='custom-errors']",
            "/Reactivity/MalformedFailureDemo");

        Assert.Equal(400, response.Status);
        Assert.Null(await response.HeaderValueAsync("X-RazorWire-Form-Handled"));
        await WaitForTextAsync(page, "#custom-errors", "We could not submit this form");
        await AssertNoPageRefreshAsync(page, _fixture.FormFailuresUrl);
    }

    [Fact]
    public async Task FormFailures_UnhandledServerFailure_RendersRuntimeFallbackWithoutHandledHeader()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(_fixture.FormFailuresUrl);
        await PlantNoRefreshMarkerAsync(page);

        var response = await SubmitAndWaitForPostAsync(
            page,
            "form[data-rw-form-failure-target='server-errors']",
            "/Reactivity/ServerFailureDemo");

        Assert.Equal(500, response.Status);
        Assert.Null(await response.HeaderValueAsync("X-RazorWire-Form-Handled"));
        await WaitForTextAsync(page, "#server-errors", "Something went wrong");
        await AssertNoPageRefreshAsync(page, _fixture.FormFailuresUrl);
    }

    private static async Task WaitForStreamConnectedAsync(IPage page)
    {
        await page.WaitForFunctionAsync(
            "() => document.body.getAttribute('data-rw-stream-reactivity') === 'connected'",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
    }

    private static async Task WaitForMessageAsync(IPage page, string token)
    {
        await page.WaitForFunctionAsync(
            "args => document.querySelector('#messages')?.innerText?.includes(args.token) === true",
            new { token },
            new PageWaitForFunctionOptions { Timeout = 30_000 });
    }

    private static async Task WaitForTextAsync(IPage page, string selector, string text)
    {
        await page.WaitForFunctionAsync(
            "args => document.querySelector(args.selector)?.innerText?.includes(args.text) === true",
            new { selector, text },
            new PageWaitForFunctionOptions { Timeout = 30_000 });
    }

    private static async Task WaitForUserListReadyAsync(IPage page)
    {
        await page.WaitForSelectorAsync("#active-user-list", new PageWaitForSelectorOptions
        {
            Timeout = 30_000
        });
        await page.WaitForSelectorAsync("#user-list-items", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Attached
        });
        await page.WaitForFunctionAsync(
            @"() => {
                const list = document.querySelector('#active-user-list');
                return list instanceof HTMLElement
                    && !list.innerText.includes('Connecting to presence...');
            }",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
        await page.WaitForFunctionAsync(
            @"() => {
                const count = document.querySelector('#user-count');
                return count instanceof HTMLElement
                    && /^\d+\s+ONLINE$/i.test(count.innerText.trim());
            }",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
    }

    private static async Task WaitForRegisterFormReadyAsync(IPage page)
    {
        const string formSelector = "#register-form";

        await page.WaitForSelectorAsync(formSelector, new PageWaitForSelectorOptions
        {
            State = WaitForSelectorState.Attached,
            Timeout = 30_000
        });
        await page.WaitForFunctionAsync(
            @"args => {
                const form = document.querySelector(args.formSelector);
                if (!(form instanceof HTMLFormElement)) {
                    return false;
                }

                const token = form.querySelector(""input[name='__RequestVerificationToken']"");
                const input = form.querySelector('#register-username');
                const submit = form.querySelector(""button[type='submit']"");

                return token instanceof HTMLInputElement
                    && token.value.trim().length > 0
                    && input instanceof HTMLInputElement
                    && !input.disabled
                    && submit instanceof HTMLButtonElement
                    && !submit.disabled;
            }",
            new { formSelector },
            new PageWaitForFunctionOptions { Timeout = 30_000 });
    }

    private static async Task WaitForUserInListAsync(IPage page, string username)
    {
        await page.WaitForFunctionAsync(
            @"args => Array
                .from(document.querySelectorAll('#user-list-items li'))
                .some(item => item.textContent?.includes(args.username) === true)",
            new { username },
            new PageWaitForFunctionOptions { Timeout = 30_000 });
    }

    private static async Task<IResponse> RegisterUserAndWaitForPostAsync(IPage page, string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("Username cannot be null or whitespace.", nameof(username));
        }

        const string formSelector = "#register-form";
        const string inputSelector = "#register-username";
        const string registerPath = "/Reactivity/RegisterUser";

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await WaitForRegisterFormReadyAsync(page);

                return await page.RunAndWaitForResponseAsync(
                    () => page.EvaluateAsync(
                        @"args => {
                            const form = document.querySelector(args.formSelector);
                            if (!(form instanceof HTMLFormElement)) {
                                throw new Error('Register form was not found.');
                            }

                            const input = document.querySelector(args.inputSelector);
                            if (!(input instanceof HTMLInputElement)) {
                                throw new Error('Register username input was not found.');
                            }

                            input.value = args.username;
                            input.dispatchEvent(new Event('input', { bubbles: true }));
                            form.requestSubmit();
                        }",
                        new { formSelector, inputSelector, username }),
                    response => response.Url.Contains(registerPath, StringComparison.OrdinalIgnoreCase)
                                && response.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase),
                    new PageRunAndWaitForResponseOptions { Timeout = 45_000 });
            }
            catch (PlaywrightException ex) when (
                attempt == 1 &&
                IsRetryableFormReplacementFailure(
                    ex,
                    "Register form was not found.",
                    "Register username input was not found."))
            {
                // Retry once to absorb in-flight register form replacement races on slower runners.
                await page.WaitForTimeoutAsync(300);
            }
        }

        throw new InvalidOperationException("RegisterUser request did not complete after retry.");
    }

    private static async Task<IResponse> PublishMessageAndWaitForPostAsync(IPage page, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Message cannot be null or whitespace.", nameof(message));
        }

        const string formSelector = "#message-form";
        const string publishPath = "/Reactivity/PublishMessage";

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await page.WaitForSelectorAsync(formSelector, new PageWaitForSelectorOptions
                {
                    State = WaitForSelectorState.Attached,
                    Timeout = 30_000
                });

                return await page.RunAndWaitForResponseAsync(
                    () => page.EvaluateAsync(
                        @"args => {
                            const form = document.querySelector(args.formSelector);
                            if (!(form instanceof HTMLFormElement)) {
                                throw new Error('Publish form was not found.');
                            }

                            const input = form.querySelector(""input[name='message']"");
                            if (!(input instanceof HTMLInputElement)) {
                                throw new Error('Publish message input was not found.');
                            }

                            input.value = args.message;
                            input.dispatchEvent(new Event('input', { bubbles: true }));
                            form.requestSubmit();
                        }",
                        new { formSelector, message }),
                    response => response.Url.Contains(publishPath, StringComparison.OrdinalIgnoreCase)
                                && response.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase),
                    new PageRunAndWaitForResponseOptions { Timeout = 45_000 });
            }
            catch (PlaywrightException ex) when (
                attempt == 1 &&
                IsRetryableFormReplacementFailure(
                    ex,
                    "Publish form was not found.",
                    "Publish message input was not found."))
            {
                // Retry once to absorb in-flight form replacement races on slower CI runners.
                await page.WaitForTimeoutAsync(300);
            }
        }

        throw new InvalidOperationException("PublishMessage request did not complete after retry.");
    }

    private static bool IsRetryableFormReplacementFailure(PlaywrightException ex, params string[] expectedMarkers)
    {
        if (ex.Message.Contains("Execution context was destroyed", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var marker in expectedMarkers)
        {
            if (ex.Message.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task WaitForCounterReadyAsync(IPage page)
    {
        var selectorOptions = new PageWaitForSelectorOptions { Timeout = 30_000 };
        await page.WaitForSelectorAsync("[data-counter-form]", selectorOptions);
        await page.WaitForSelectorAsync("#instance-score-value", selectorOptions);
        await page.WaitForSelectorAsync("#session-score-value", selectorOptions);
        await page.WaitForSelectorAsync("#client-count-input", new PageWaitForSelectorOptions
        {
            Timeout = 30_000,
            State = WaitForSelectorState.Attached
        });
    }

    private static async Task<IResponse> SubmitAndWaitForPostAsync(IPage page, string formSelector, string path)
    {
        await page.WaitForSelectorAsync(formSelector, new PageWaitForSelectorOptions
        {
            State = WaitForSelectorState.Attached,
            Timeout = 15_000
        });

        return await page.RunAndWaitForResponseAsync(
            () => page.Locator(formSelector).EvaluateAsync(
                @"form => {
                    if (!(form instanceof HTMLFormElement)) {
                        throw new Error('Target element is not an HTML form.');
                    }
                    form.requestSubmit();
                }"),
            response => response.Url.Contains(path, StringComparison.OrdinalIgnoreCase)
                        && response.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions { Timeout = 45_000 });
    }

    private static async Task<int> GetIntTextAsync(IPage page, string selector)
    {
        var text = await page.Locator(selector).InnerTextAsync();
        var trimmed = text.Trim();
        if (int.TryParse(trimmed, out var parsed))
        {
            return parsed;
        }

        throw new FormatException($"GetIntTextAsync could not parse an integer from selector '{selector}'. Raw value: '{trimmed}'.");
    }

    private static async Task<int> GetIntInputValueAsync(IPage page, string selector)
    {
        var value = await page.EvaluateAsync<string>("selector => document.querySelector(selector)?.getAttribute('value') ?? ''", selector);
        var trimmed = value.Trim();
        if (int.TryParse(trimmed, out var parsed))
        {
            return parsed;
        }

        throw new FormatException($"GetIntInputValueAsync could not parse an integer from selector '{selector}'. Raw value: '{trimmed}'.");
    }

    private static async Task ExpectCounterValuesAsync(IPage page, int expectedInstance, int expectedSession, int expectedClientCount)
    {
        await page.WaitForFunctionAsync(
            @"args => {
                const instance = document.querySelector('#instance-score-value')?.textContent?.trim();
                const session = document.querySelector('#session-score-value')?.textContent?.trim();
                const clientCount = document.querySelector('#client-count-input')?.getAttribute('value')?.trim();
                return instance === String(args.expectedInstance)
                    && session === String(args.expectedSession)
                    && clientCount === String(args.expectedClientCount);
            }",
            new { expectedInstance, expectedSession, expectedClientCount },
            new PageWaitForFunctionOptions { Timeout = 15_000 });
    }

    private static async Task ExpectCounterValuesAsync(IPage page, int expectedSession)
    {
        await page.WaitForFunctionAsync(
            "args => document.querySelector('#session-score-value')?.textContent?.trim() === String(args.expectedSession)",
            new { expectedSession },
            new PageWaitForFunctionOptions { Timeout = 15_000 });
    }

    private static async Task NavigateViaHeaderAndAssertSessionScoreAsync(IPage page, string linkText, string expectedPath, int expectedSessionScore)
    {
        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = linkText, Exact = true }).First.ClickAsync();
        await WaitForPathAsync(page, expectedPath);
        await WaitForCounterReadyAsync(page);
        await ExpectCounterValuesAsync(page, expectedSessionScore);
    }

    private static async Task WaitForPathAsync(IPage page, string expectedPath)
    {
        await page.WaitForFunctionAsync(
            "path => window.location.pathname === path",
            expectedPath,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
    }

    private static async Task PlantNoRefreshMarkerAsync(IPage page)
    {
        await page.EvaluateAsync("() => { window.__noRefreshMarker = Date.now().toString(); }");
    }

    private static async Task AssertNoPageRefreshAsync(IPage page, string expectedUrl)
    {
        var markerExists = await page.EvaluateAsync<bool>("() => !!window.__noRefreshMarker");
        Assert.True(markerExists, "Expected window.__noRefreshMarker to persist, but it was missing (likely full page refresh).");
        Assert.Equal(expectedUrl.TrimEnd('/'), page.Url.TrimEnd('/'));
    }
}

public sealed class RazorWireMvcPlaywrightFixture : IAsyncLifetime
{
    private static readonly SemaphoreSlim PlaywrightInstallLock = new(1, 1);
    private static bool _playwrightInstalled;
    private static readonly Regex ListeningUrlRegex = new(@"Now listening on:\s*(https?://\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EmptyUserCountRegex = new(
        "id=\"user-count\"[^>]*>\\s*0(?:\\s|<)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly ConcurrentQueue<string> _appLogs = new();
    private readonly TaskCompletionSource<string> _boundBaseUrlSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Process? _appProcess;
    private IPlaywright? _playwright;

    public IBrowser Browser { get; private set; } = null!;
    public string BaseUrl { get; private set; } = string.Empty;
    public string ReactivityUrl { get; private set; } = string.Empty;
    public string FormFailuresUrl { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await EnsurePlaywrightInstalledAsync();

        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });

        _appProcess = StartExampleApp("http://127.0.0.1:0");
        var baseUrl = await WaitForBoundBaseUrlAsync(TimeSpan.FromSeconds(60));
        BaseUrl = baseUrl;
        ReactivityUrl = $"{baseUrl}/Reactivity";
        FormFailuresUrl = $"{baseUrl}/Reactivity/FormFailures";

        await WaitForAppReadyAsync(baseUrl, TimeSpan.FromSeconds(60));
        await WarmReactivitySurfaceAsync(baseUrl, TimeSpan.FromSeconds(60));
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (Browser is not null)
            {
                await Browser.DisposeAsync();
            }

            _playwright?.Dispose();
        }
        finally
        {
            if (_appProcess is not null && !_appProcess.HasExited)
            {
                _appProcess.Kill(entireProcessTree: true);
                await _appProcess.WaitForExitAsync();
            }

            _appProcess?.Dispose();
        }
    }

    private static async Task EnsurePlaywrightInstalledAsync()
    {
        if (_playwrightInstalled)
        {
            return;
        }

        await PlaywrightInstallLock.WaitAsync();
        try
        {
            if (_playwrightInstalled)
            {
                return;
            }

            var exitCode = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"Playwright browser install failed with exit code {exitCode}.");
            }

            _playwrightInstalled = true;
        }
        finally
        {
            PlaywrightInstallLock.Release();
        }
    }

    private Process StartExampleApp(string baseUrl)
    {
        var repoRoot = PathUtils.FindRepositoryRoot(AppContext.BaseDirectory);
        var projectPath = Path.Combine(repoRoot, "examples", "razorwire-mvc", "RazorWireWebExample.csproj");

        if (!File.Exists(projectPath))
        {
            throw new FileNotFoundException("Could not find RazorWire MVC example project.", projectPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{projectPath}\" --no-launch-profile",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.Environment["ASPNETCORE_URLS"] = baseUrl;
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Development";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) => CaptureAppLog(args.Data);
        process.ErrorDataReceived += (_, args) => CaptureAppLog(args.Data);
        process.Exited += (_, _) =>
        {
            _boundBaseUrlSource.TrySetException(
                new InvalidOperationException($"RazorWire MVC example exited before publishing a listening URL.{Environment.NewLine}{GetRecentLogs()}"));
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return process;
    }

    private async Task<string> WaitForBoundBaseUrlAsync(TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var registration = timeoutCts.Token.Register(
            () => _boundBaseUrlSource.TrySetException(
                new TimeoutException($"RazorWire MVC example did not publish a listening URL within {timeout.TotalSeconds} seconds.{Environment.NewLine}{GetRecentLogs()}")));

        return await _boundBaseUrlSource.Task;
    }

    private async Task WaitForAppReadyAsync(string baseUrl, TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var client = new HttpClient();

        while (!timeoutCts.Token.IsCancellationRequested)
        {
            if (_appProcess is null || _appProcess.HasExited)
            {
                throw new InvalidOperationException($"RazorWire MVC example exited before it became ready.{Environment.NewLine}{GetRecentLogs()}");
            }

            try
            {
                using var response = await client.GetAsync(baseUrl, timeoutCts.Token);
                if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Application is still starting.
            }
            catch (TaskCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException($"RazorWire MVC example did not become ready within {timeout.TotalSeconds} seconds.{Environment.NewLine}{GetRecentLogs()}");
            }
            catch (TaskCanceledException) when (!timeoutCts.IsCancellationRequested)
            {
                // Retry until global timeout.
            }

            try
            {
                await Task.Delay(250, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException($"RazorWire MVC example did not become ready within {timeout.TotalSeconds} seconds.{Environment.NewLine}{GetRecentLogs()}");
            }
        }

        throw new TimeoutException($"RazorWire MVC example did not become ready within {timeout.TotalSeconds} seconds.{Environment.NewLine}{GetRecentLogs()}");
    }

    private async Task WarmReactivitySurfaceAsync(string baseUrl, TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl, UriKind.Absolute)
        };

        using var _ = await WaitForOkAsync(client, "/Reactivity", timeoutCts.Token);
        await WaitForEmptyUserListAsync(client, "/Reactivity/UserList", timeoutCts.Token);
        using var __ = await WaitForOkAsync(client, "/Reactivity/FormFailures", timeoutCts.Token);
    }

    private async Task<HttpResponseMessage> WaitForOkAsync(HttpClient client, string path, CancellationToken token)
    {
        while (true)
        {
            try
            {
                var response = await client.GetAsync(path, token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return response;
                }

                response.Dispose();
            }
            catch (HttpRequestException)
            {
                // Application is still starting.
            }
            catch (TaskCanceledException) when (!token.IsCancellationRequested)
            {
                // Retry until the overall warm-up timeout expires.
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }

            await DelayWarmupRetryAsync(token);
        }

        throw new TimeoutException($"RazorWire MVC warm-up timed out waiting for '{path}'.{Environment.NewLine}{GetRecentLogs()}");
    }

    private async Task WaitForEmptyUserListAsync(HttpClient client, string path, CancellationToken token)
    {
        while (true)
        {
            try
            {
                using var response = await client.GetAsync(path, token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var html = await response.Content.ReadAsStringAsync(token);
                    if (IsEmptyUserListMarkup(html))
                    {
                        return;
                    }
                }
            }
            catch (HttpRequestException)
            {
                // Application is still starting.
            }
            catch (TaskCanceledException) when (!token.IsCancellationRequested)
            {
                // Retry until the overall warm-up timeout expires.
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }

            await DelayWarmupRetryAsync(token);
        }

        throw new TimeoutException($"RazorWire MVC warm-up timed out waiting for an empty '{path}' surface.{Environment.NewLine}{GetRecentLogs()}");
    }

    private static bool IsEmptyUserListMarkup(string html)
    {
        return html.Contains("id=\"active-user-list\"", StringComparison.Ordinal)
               && html.Contains("id=\"user-list-items\"", StringComparison.Ordinal)
               && html.Contains("id=\"user-list-empty\"", StringComparison.Ordinal)
               && EmptyUserCountRegex.IsMatch(html);
    }

    private async Task DelayWarmupRetryAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(250, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Caller converts cancellation into a timeout with recent logs.
        }
    }

    private void CaptureAppLog(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var match = ListeningUrlRegex.Match(message);
        if (match.Success && Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var uri))
        {
            var normalizedBaseUrl = $"{uri.Scheme}://{uri.Host}:{uri.Port}";
            _boundBaseUrlSource.TrySetResult(normalizedBaseUrl);
        }

        _appLogs.Enqueue(message);
        while (_appLogs.Count > 200)
        {
            _appLogs.TryDequeue(out _);
        }
    }

    private string GetRecentLogs()
    {
        return string.Join(Environment.NewLine, _appLogs);
    }

}
