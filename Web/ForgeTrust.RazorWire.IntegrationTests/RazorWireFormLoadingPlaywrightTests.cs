using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

[Collection(RazorWireIntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RazorWireFormLoadingPlaywrightTests
{
    private readonly RazorWireMvcPlaywrightFixture _fixture;

    public RazorWireFormLoadingPlaywrightTests(RazorWireMvcPlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task LazyTokenFailure_ShowsImmediateStatus_SendsNoPost_AndAllowsRetry()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var posts = new ConcurrentQueue<string>();
        var requests = new ConcurrentQueue<string>();
        var tokenStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailedToken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenRequests = 0;

        page.Request += (_, request) =>
        {
            requests.Enqueue($"{request.Method} {request.Url}");
            if (request.Method == "POST" && request.Url.Contains("/Reactivity/SubmitFormLoading", StringComparison.Ordinal))
            {
                posts.Enqueue(request.Url);
            }
        };
        await page.RouteAsync("**/_rw/antiforgery/token", async route =>
        {
            if (Interlocked.Increment(ref tokenRequests) == 1)
            {
                tokenStarted.TrySetResult();
                await releaseFailedToken.Task;
                await route.FulfillAsync(new RouteFulfillOptions { Status = 503, Body = "Unavailable" });
            }
            else
            {
                await route.ContinueAsync();
            }
        });

        await page.GotoAsync($"{_fixture.BaseUrl}/Reactivity/FormLoading");
        Assert.Equal(0, await page.Locator("#loading-local-form input[name='__RequestVerificationToken']").CountAsync());
        await page.EvaluateAsync(
            """
            () => {
                window.__loadingEvents = [];
                window.__loadingBeforeFetchPrevented = false;
                window.__loadingFailureEvents = 0;
                document.addEventListener('turbo:before-fetch-request', event => {
                    if (event.target?.id === 'loading-local-form') {
                        window.__loadingEvents.push('before-fetch');
                        window.__loadingBeforeFetchPrevented = event.defaultPrevented;
                    }
                });
                document.addEventListener('turbo:submit-start', event => {
                    if (event.target?.id === 'loading-local-form') window.__loadingEvents.push('submit-start');
                });
                document.addEventListener('turbo:submit-end', event => {
                    if (event.target?.id === 'loading-local-form') window.__loadingEvents.push('submit-end');
                });
                document.addEventListener('razorwire:form:failure', event => {
                    if (event.target?.id === 'loading-local-form') window.__loadingFailureEvents++;
                });
            }
            """);

        var visibleAtNextFrame = await page.EvaluateAsync<bool>(
            """
            () => new Promise(resolve => {
                document.querySelector('#loading-local-primary').click();
                requestAnimationFrame(() => {
                    const indicator = document.querySelector('#loading-local-form [data-rw-loading-indicator]');
                    const style = getComputedStyle(indicator);
                    resolve(!indicator.hidden && style.display !== 'none' && style.visibility !== 'hidden');
                });
            })
            """);
        var eventsAtFirstFrame = await page.EvaluateAsync<string[]>("() => window.__loadingEvents");
        Assert.True(
            visibleAtNextFrame,
            $"Expected local status by first frame. Events: {string.Join(", ", eventsAtFirstFrame)}. Requests: {string.Join(" | ", requests)}.");
        var preparedFetchPrevented = await page.EvaluateAsync<bool>("() => window.__loadingBeforeFetchPrevented");
        Assert.True(
            preparedFetchPrevented,
            $"Expected Turbo's form fetch to pause for a lazy token. Markers: form={await page.Locator("#loading-local-form").GetAttributeAsync("data-rw-form")}, antiforgery={await page.Locator("#loading-local-form").GetAttributeAsync("data-rw-antiforgery")}. Requests: {string.Join(" | ", requests)}.");
        var tokenObserved = await Task.WhenAny(tokenStarted.Task, Task.Delay(TimeSpan.FromSeconds(5))) == tokenStarted.Task;
        Assert.True(tokenObserved, $"Expected token fetch. Events: {string.Join(", ", eventsAtFirstFrame)}. Requests: {string.Join(" | ", requests)}.");
        Assert.Empty(posts);

        releaseFailedToken.TrySetResult();
        await page.WaitForFunctionAsync("() => document.querySelector('#loading-local-form')?.getAttribute('data-rw-antiforgery-state') === 'failed'");
        await page.WaitForFunctionAsync("() => document.querySelector('#loading-local-form [data-rw-loading-indicator]')?.hidden === true");
        await Assertions.Expect(page.Locator("#loading-local-form [data-rw-form-error-generated]")).ToContainTextAsync("We could not prepare this form");
        Assert.Empty(posts);
        Assert.Equal(1, await page.EvaluateAsync<int>("() => window.__loadingFailureEvents"));

        var events = await page.EvaluateAsync<string[]>("() => window.__loadingEvents");
        Assert.NotNull(events);
        Assert.Equal("before-fetch", events[0]);
        Assert.Contains("submit-end", events);

        await page.Locator("#loading-local-primary").ClickAsync();
        await Assertions.Expect(page.Locator("#form-loading-result")).ToContainTextAsync("Saved local form.");
        Assert.Single(posts);
        Assert.True(tokenRequests >= 2);
    }

    [Fact]
    public async Task AbortedLazyTokenRequest_ResumesWithoutPostingOrLeavingLoadingActive()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var tokenStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseToken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var posts = 0;
        page.Request += (_, request) =>
        {
            if (request.Method == "POST" && request.Url.Contains("/Reactivity/SubmitFormLoading", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref posts);
            }
        };
        await page.RouteAsync("**/_rw/antiforgery/token", async route =>
        {
            tokenStarted.TrySetResult();
            await releaseToken.Task;
            try
            {
                await route.ContinueAsync();
            }
            catch (PlaywrightException)
            {
                // The page may cancel the held token fetch before this route is released.
            }
        });

        try
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/Reactivity/FormLoading");
            await page.Locator("#loading-local-form").EvaluateAsync(
                """
                form => {
                    window.__loadingResumeCount = 0;
                    window.__loadingFailureCount = 0;
                    window.__loadingSubmitEndCount = 0;
                    form.addEventListener('razorwire:form:failure', () => window.__loadingFailureCount++);
                    form.addEventListener('turbo:submit-end', () => window.__loadingSubmitEndCount++);
                    form.addEventListener('turbo:before-fetch-request', event => {
                        const controller = new AbortController();
                        event.detail.fetchOptions.signal = controller.signal;
                        window.__loadingAbortController = controller;
                        const resume = event.detail.resume;
                        event.detail.resume = () => {
                            window.__loadingResumeCount++;
                            resume();
                        };
                    });
                }
                """);
            await page.Locator("#loading-local-primary").ClickAsync();
            await tokenStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Assertions.Expect(page.Locator("#loading-local-form [data-rw-loading-indicator]")).ToBeVisibleAsync();

            await page.EvaluateAsync("() => window.__loadingAbortController.abort()");
            await page.WaitForFunctionAsync("() => window.__loadingResumeCount === 1");
            await page.WaitForFunctionAsync("() => window.__loadingSubmitEndCount === 1");
            await Assertions.Expect(page.Locator("#loading-local-form [data-rw-loading-indicator]")).ToBeHiddenAsync();
            Assert.Null(await page.Locator("#loading-local-form").GetAttributeAsync("data-rw-loading-state"));
            Assert.False(await page.Locator("#loading-local-primary").IsDisabledAsync());
            Assert.Equal(0, await page.EvaluateAsync<int>("() => window.__loadingFailureCount"));
            Assert.Equal(0, await page.Locator("#loading-local-form [data-rw-form-error-generated]").CountAsync());
            Assert.Equal(0, posts);

            releaseToken.TrySetResult();
            await page.WaitForTimeoutAsync(100);
            Assert.Equal(1, await page.EvaluateAsync<int>("() => window.__loadingResumeCount"));
            Assert.Equal(0, posts);
        }
        finally
        {
            releaseToken.TrySetResult();
        }
    }

    [Fact]
    public async Task LocalSiteAndFallbackIndicators_FollowBoundaryPrecedence()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}/Reactivity/FormLoading");

        await page.Locator("#loading-site-form button").ClickAsync();
        await Assertions.Expect(page.Locator("section[data-rw-loading-boundary]:has(#loading-site-form) > [data-rw-loading-indicator]")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("#loading-local-form [data-rw-loading-indicator]")).ToBeHiddenAsync();
        await Assertions.Expect(page.Locator("#form-loading-result")).ToContainTextAsync("Saved site form.");

        Assert.Null(await page.Locator("#loading-fallback-form").GetAttributeAsync("data-rw-form"));
        Assert.Equal("true", await page.Locator("#loading-fallback-form").GetAttributeAsync("data-rw-loading"));
        var postStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/Reactivity/SubmitFormLoading**", async route =>
        {
            postStarted.TrySetResult();
            await releasePost.Task;
            await route.ContinueAsync();
        });
        var pageContentTop = await page.EvaluateAsync<double>(
            "() => document.querySelector('#form-loading-result').getBoundingClientRect().top + scrollY");
        try
        {
            await page.Locator("#loading-fallback-form button").ClickAsync();
            await postStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var fallbackVisibleAtNextFrame = await page.EvaluateAsync<bool>(
                """
                () => new Promise(resolve => requestAnimationFrame(() => {
                    const fallback = document.querySelector('[data-rw-loading-fallback]');
                    const style = fallback && getComputedStyle(fallback);
                    resolve(Boolean(fallback && !fallback.hidden && style.display !== 'none' && style.visibility !== 'hidden'));
                }))
                """);
            Assert.True(fallbackVisibleAtNextFrame);
            var activeContentTop = await page.EvaluateAsync<double>(
                "() => document.querySelector('#form-loading-result').getBoundingClientRect().top + scrollY");
            Assert.InRange(Math.Abs(pageContentTop - activeContentTop), 0, 1);
            await Task.Delay(600);
            Assert.Equal("pending", await page.Locator("#loading-fallback-form").GetAttributeAsync("data-rw-loading-state"));
            var turboBarVisible = await page.EvaluateAsync<bool>(
                """
                () => {
                    const bar = document.querySelector('.turbo-progress-bar');
                    return Boolean(bar && getComputedStyle(bar).display !== 'none' && getComputedStyle(bar).visibility !== 'hidden');
                }
                """);
            Assert.False(turboBarVisible);
            releasePost.TrySetResult();
            await Assertions.Expect(page.Locator("#form-loading-result")).ToContainTextAsync("Saved fallback form.");
            await Assertions.Expect(page.Locator("[data-rw-loading-fallback]")).ToBeHiddenAsync();
        }
        finally
        {
            releasePost.TrySetResult();
        }
    }

    [Fact]
    public async Task NativeSubmission_WorksWithJavaScriptDisabled()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions { JavaScriptEnabled = false });
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}/Reactivity/FormLoading");

        var response = await page.RunAndWaitForResponseAsync(
            () => page.Locator("#loading-fallback-form button").ClickAsync(),
            item => item.Url.Contains("/Reactivity/SubmitFormLoading", StringComparison.Ordinal) && item.Request.Method == "POST");

        Assert.True(response.Status is 302 or 303, $"Expected a native post redirect, got {response.Status}.");
    }

    [Fact]
    public async Task FrameSubmission_ShowsStatusBeforeResponse_WithoutTurboPageBar()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}/Reactivity/FormLoading");

        var postStarted = new TaskCompletionSource<IRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/Reactivity/SubmitFormLoading**", async route =>
        {
            postStarted.TrySetResult(route.Request);
            await releasePost.Task;
            await route.ContinueAsync();
        });

        try
        {
            await page.Locator("#loading-frame-form button").ClickAsync();
            var request = await postStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("loading-sample-frame", await request.HeaderValueAsync("Turbo-Frame"));
            var visibleAtNextFrame = await page.EvaluateAsync<bool>(
                """
                () => new Promise(resolve => requestAnimationFrame(() => {
                    const indicator = document.querySelector('#loading-sample-frame [data-rw-loading-indicator]');
                    const style = getComputedStyle(indicator);
                    resolve(!indicator.hidden && style.display !== 'none' && style.visibility !== 'hidden');
                }))
                """);
            Assert.True(visibleAtNextFrame);
            await Task.Delay(600);
            Assert.Equal("pending", await page.Locator("#loading-frame-form").GetAttributeAsync("data-rw-loading-state"));
            var turboBarVisible = await page.EvaluateAsync<bool>(
                """
                () => {
                    const bar = document.querySelector('.turbo-progress-bar');
                    return Boolean(bar && getComputedStyle(bar).display !== 'none' && getComputedStyle(bar).visibility !== 'hidden');
                }
                """);
            Assert.False(turboBarVisible);
            releasePost.TrySetResult();
            await Assertions.Expect(page.Locator("#form-loading-result")).ToContainTextAsync(
                "Saved frame form.",
                new LocatorAssertionsToContainTextOptions { Timeout = 15_000 });
            await Assertions.Expect(page.Locator("#loading-sample-frame [data-rw-loading-indicator]")).ToBeHiddenAsync();
        }
        finally
        {
            releasePost.TrySetResult();
        }
    }

    [Fact]
    public async Task SiteIndicator_RemainsVisibleAfterTenSecondColdResponse()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var postStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/Reactivity/SubmitFormLoading**", async route =>
        {
            postStarted.TrySetResult();
            await releasePost.Task;
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "text/vnd.turbo-stream.html",
                Body = "<turbo-stream action=\"update\" target=\"form-loading-result\"><template>Saved slow site form.</template></turbo-stream>",
            });
        });

        try
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/Reactivity/FormLoading");
            await page.Locator("#loading-site-form button").ClickAsync();
            await postStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var indicator = page.Locator("section[data-rw-loading-boundary]:has(#loading-site-form) > [data-rw-loading-indicator]");
            await Assertions.Expect(indicator).ToBeVisibleAsync();

            await Task.Delay(TimeSpan.FromSeconds(10));
            await Assertions.Expect(indicator).ToBeVisibleAsync();
            Assert.Equal("pending", await page.Locator("#loading-site-form").GetAttributeAsync("data-rw-loading-state"));
            Assert.True(await page.Locator("#loading-site-form button").IsDisabledAsync());

            releasePost.TrySetResult();
            await Assertions.Expect(page.Locator("#form-loading-result")).ToContainTextAsync("Saved slow site form.");
            await Assertions.Expect(indicator).ToBeHiddenAsync();
        }
        finally
        {
            releasePost.TrySetResult();
        }
    }

    [Fact]
    public async Task NetworkFailureAfterPostStarts_ClearsIndicatorStateAndSubmitLock()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var postStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAbort = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/Reactivity/SubmitFormLoading**", async route =>
        {
            postStarted.TrySetResult();
            await releaseAbort.Task;
            await route.AbortAsync();
        });

        try
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/Reactivity/FormLoading");
            await page.Locator("#loading-site-form button").ClickAsync();
            await postStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Assertions.Expect(page.Locator("section[data-rw-loading-boundary]:has(#loading-site-form) > [data-rw-loading-indicator]")).ToBeVisibleAsync();
            Assert.Equal("pending", await page.Locator("#loading-site-form").GetAttributeAsync("data-rw-loading-state"));
            Assert.True(await page.Locator("#loading-site-form button").IsDisabledAsync());

            releaseAbort.TrySetResult();
            await Assertions.Expect(page.Locator("section[data-rw-loading-boundary]:has(#loading-site-form) > [data-rw-loading-indicator]")).ToBeHiddenAsync();
            await page.WaitForFunctionAsync("() => !document.querySelector('#loading-site-form')?.hasAttribute('data-rw-loading-state')");
            Assert.False(await page.Locator("#loading-site-form button").IsDisabledAsync());
            await Assertions.Expect(page.Locator("#loading-site-form [data-rw-form-error-generated]")).ToBeVisibleAsync();
        }
        finally
        {
            releaseAbort.TrySetResult();
        }
    }

    [Fact]
    public async Task InvalidFormAndDismissedConfirmation_DoNotStartLoading()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var posts = 0;
        var confirmations = 0;
        page.Request += (_, request) =>
        {
            if (request.Method == "POST" && request.Url.Contains("/Reactivity/SubmitFormLoading", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref posts);
            }
        };
        page.Dialog += async (_, dialog) =>
        {
            Interlocked.Increment(ref confirmations);
            await dialog.DismissAsync();
        };

        await page.GotoAsync($"{_fixture.BaseUrl}/Reactivity/FormLoading");
        await page.Locator("#loading-site-form").EvaluateAsync(
            """
            form => {
                const required = document.createElement('input');
                required.required = true;
                required.name = 'requiredProbe';
                form.appendChild(required);
            }
            """);
        await page.Locator("#loading-site-form button").ClickAsync();
        Assert.Equal(0, posts);
        await Assertions.Expect(page.Locator("section[data-rw-loading-boundary]:has(#loading-site-form) > [data-rw-loading-indicator]")).ToBeHiddenAsync();
        Assert.Null(await page.Locator("#loading-site-form").GetAttributeAsync("data-rw-loading-state"));

        await page.Locator("#loading-site-form").EvaluateAsync(
            "form => { form.querySelector('[name=requiredProbe]').remove(); form.setAttribute('data-turbo-confirm', 'Cancel this test?'); }");
        await page.Locator("#loading-site-form button").ClickAsync();
        Assert.Equal(1, confirmations);
        Assert.Equal(0, posts);
        await Assertions.Expect(page.Locator("section[data-rw-loading-boundary]:has(#loading-site-form) > [data-rw-loading-indicator]")).ToBeHiddenAsync();
        Assert.Null(await page.Locator("#loading-site-form").GetAttributeAsync("data-rw-loading-state"));
    }

    [Fact]
    public async Task PendingForm_BlocksDuplicateSubmittersButAllowsAnotherForm()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var tokenStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseToken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var posts = 0;
        page.Request += (_, request) =>
        {
            if (request.Method == "POST" && request.Url.Contains("/Reactivity/SubmitFormLoading", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref posts);
            }
        };
        await page.RouteAsync("**/_rw/antiforgery/token", async route =>
        {
            tokenStarted.TrySetResult();
            await releaseToken.Task;
            await route.ContinueAsync();
        });

        await page.GotoAsync($"{_fixture.BaseUrl}/Reactivity/FormLoading");
        await page.EvaluateAsync(
            """
            () => {
                const preDisabled = document.createElement('button');
                preDisabled.id = 'loading-local-pre-disabled';
                preDisabled.type = 'submit';
                preDisabled.setAttribute('form', 'loading-local-form');
                preDisabled.disabled = true;
                document.body.appendChild(preDisabled);
                window.__localAccepted = 0;
                document.addEventListener('turbo:before-fetch-request', event => {
                    if (event.target?.id === 'loading-local-form') window.__localAccepted++;
                });
            }
            """);
        var primaryBounds = await page.Locator("#loading-local-primary").BoundingBoxAsync();
        Assert.NotNull(primaryBounds);
        await page.Mouse.DblClickAsync(
            primaryBounds.X + primaryBounds.Width / 2,
            primaryBounds.Y + primaryBounds.Height / 2);
        await tokenStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(await page.Locator("#loading-local-primary").IsDisabledAsync());
        Assert.True(await page.Locator("#loading-local-secondary").IsDisabledAsync());
        Assert.True(await page.Locator("#loading-local-external").IsDisabledAsync());
        await page.EvaluateAsync(
            """
            () => {
                const form = document.querySelector('#loading-local-form');
                form.requestSubmit();
                form.requestSubmit(document.querySelector('#loading-local-external'));
                document.querySelector('#loading-local-secondary').click();
            }
            """);
        Assert.Equal(1, await page.EvaluateAsync<int>("() => window.__localAccepted"));
        Assert.Equal(0, posts);

        await page.Locator("#loading-site-form button").ClickAsync();
        await Assertions.Expect(page.Locator("#form-loading-result")).ToContainTextAsync("Saved site form.");
        Assert.Equal(1, posts);
        // Turbo cancels a pending page submission when another page form navigates.
        // The second form must still be allowed to submit, and the first must unlock.
        releaseToken.TrySetResult();
        await page.WaitForFunctionAsync(
            "() => !document.querySelector('#loading-local-form')?.hasAttribute('data-rw-loading-state')");
        Assert.Equal(1, posts);
        Assert.False(await page.Locator("#loading-local-primary").IsDisabledAsync());
        Assert.False(await page.Locator("#loading-local-secondary").IsDisabledAsync());
        Assert.False(await page.Locator("#loading-local-external").IsDisabledAsync());
        Assert.True(await page.Locator("#loading-local-pre-disabled").IsDisabledAsync());
    }

    [Fact]
    public async Task LockOff_LeavesOtherSubmittersAvailableWhileShowingFeedback()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var tokenStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseToken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/_rw/antiforgery/token", async route =>
        {
            tokenStarted.TrySetResult();
            await releaseToken.Task;
            await route.ContinueAsync();
        });

        try
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/Reactivity/FormLoading");
            await page.Locator("#loading-local-form").EvaluateAsync(
                "form => form.setAttribute('data-rw-loading-lock', 'false')");
            await page.Locator("#loading-local-primary").ClickAsync();
            await tokenStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));

            await Assertions.Expect(page.Locator("#loading-local-form [data-rw-loading-indicator]")).ToBeVisibleAsync();
            Assert.Equal("pending", await page.Locator("#loading-local-form").GetAttributeAsync("data-rw-loading-state"));
            Assert.False(await page.Locator("#loading-local-secondary").IsDisabledAsync());
            Assert.False(await page.Locator("#loading-local-external").IsDisabledAsync());

            releaseToken.TrySetResult();
            await Assertions.Expect(page.Locator("#form-loading-result")).ToContainTextAsync("Saved local form.");
            await Assertions.Expect(page.Locator("#loading-local-form [data-rw-loading-indicator]")).ToBeHiddenAsync();
        }
        finally
        {
            releaseToken.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeparateVisit_ArbitratesTurboBarWithPendingFrameForm(bool useFallback)
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var postStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var visitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseVisit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/Reactivity/SubmitFormLoading**", async route =>
        {
            postStarted.TrySetResult();
            await releasePost.Task;
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "text/vnd.turbo-stream.html",
                Body = "<turbo-stream action=\"update\" target=\"form-loading-result\"><template>Saved overlapping form.</template></turbo-stream>",
            });
        });
        await page.RouteAsync("**/Navigation", async route =>
        {
            visitStarted.TrySetResult();
            await releaseVisit.Task;
            await route.ContinueAsync();
        });

        try
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/Reactivity/FormLoading");
            var formId = useFallback ? "loading-fallback-form" : "loading-frame-form";
            if (useFallback)
            {
                await page.Locator("#loading-fallback-form").EvaluateAsync(
                    "form => form.setAttribute('data-turbo-frame', 'loading-sample-frame')");
            }

            await page.Locator($"#{formId} button").ClickAsync();
            await postStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var indicator = useFallback
                ? page.Locator("[data-rw-loading-fallback]")
                : page.Locator("#loading-sample-frame [data-rw-loading-indicator]");
            await Assertions.Expect(indicator).ToBeVisibleAsync();

            await page.Locator("a[href='/Navigation']").First.ClickAsync();
            await visitStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Task.Delay(650);
            await Assertions.Expect(indicator).ToBeVisibleAsync();
            var turboBarVisible = await page.EvaluateAsync<bool>(
                "() => { const bar = document.querySelector('.turbo-progress-bar'); return Boolean(bar && getComputedStyle(bar).display !== 'none' && getComputedStyle(bar).visibility !== 'hidden'); }");
            Assert.Equal(!useFallback, turboBarVisible);

            releasePost.TrySetResult();
            await Assertions.Expect(page.Locator("#form-loading-result")).ToContainTextAsync("Saved overlapping form.");
            if (!useFallback) await Assertions.Expect(indicator).ToBeHiddenAsync();
            await Assertions.Expect(page.Locator("[data-rw-loading-fallback]")).ToBeVisibleAsync();
            Assert.Equal("suppress", await page.Locator("html").GetAttributeAsync("data-rw-loading-turbo-bar"));

            releaseVisit.TrySetResult();
            await page.WaitForURLAsync("**/Navigation");
        }
        finally
        {
            releasePost.TrySetResult();
            releaseVisit.TrySetResult();
        }
    }

    [Fact]
    public async Task CanceledVisit_DoesNotTakeTheFormProgressBar()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var postStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/Reactivity/SubmitFormLoading**", async route =>
        {
            postStarted.TrySetResult();
            await releasePost.Task;
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "text/vnd.turbo-stream.html",
                Body = "<turbo-stream action=\"update\" target=\"form-loading-result\"><template>Saved after canceled visit.</template></turbo-stream>",
            });
        });

        try
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/Reactivity/FormLoading");
            await page.EvaluateAsync(
                "() => { window.__canceledVisit = 0; document.addEventListener('turbo:before-visit', event => { window.__canceledVisit++; event.preventDefault(); }, { once: true }); }");
            await page.Locator("#loading-frame-form button").ClickAsync();
            await postStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await page.Locator("a[href='/Navigation']").First.ClickAsync();
            await Task.Delay(650);

            Assert.Equal(1, await page.EvaluateAsync<int>("() => window.__canceledVisit"));
            await Assertions.Expect(page.Locator("#loading-sample-frame [data-rw-loading-indicator]")).ToBeVisibleAsync();
            Assert.Equal("suppress", await page.Locator("html").GetAttributeAsync("data-rw-loading-turbo-bar"));
            Assert.Equal($"{_fixture.BaseUrl}/Reactivity/FormLoading", page.Url);

            releasePost.TrySetResult();
            await Assertions.Expect(page.Locator("#form-loading-result")).ToContainTextAsync("Saved after canceled visit.");
        }
        finally
        {
            releasePost.TrySetResult();
        }
    }

    [Fact]
    public async Task FallbackStyles_LoadUnderStrictStyleCsp_AndRespectReducedMotion()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ReducedMotion = ReducedMotion.Reduce,
        });
        var page = await context.NewPageAsync();
        await page.RouteAsync("**/Reactivity/FormLoading", async route =>
        {
            var response = await route.FetchAsync();
            var headers = new Dictionary<string, string>(response.Headers, StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Security-Policy"] = "style-src 'self'; style-src-attr 'none'",
            };
            await route.FulfillAsync(new RouteFulfillOptions { Response = response, Headers = headers });
        });

        var cssResponse = await page.RunAndWaitForResponseAsync(
            () => page.GotoAsync($"{_fixture.BaseUrl}/Reactivity/FormLoading"),
            response => response.Url.Contains("/razorwire/razorwire.loading.css", StringComparison.Ordinal));
        Assert.Equal(200, cssResponse.Status);
        await page.Locator("#loading-fallback-form button").ClickAsync();
        var style = await page.EvaluateAsync<string[]>(
            """
            () => {
                const fallback = document.querySelector('[data-rw-loading-fallback]');
                return [
                    getComputedStyle(fallback).position,
                    getComputedStyle(fallback, '::after').animationName,
                    getComputedStyle(fallback).pointerEvents,
                    fallback.hasAttribute('style') ? 'inline' : 'external',
                ];
            }
            """);
        Assert.Equal(new[] { "fixed", "none", "none", "external" }, style);
        await Assertions.Expect(page.Locator("#form-loading-result")).ToContainTextAsync("Saved fallback form.");
    }

    [Fact]
    public async Task SharedBoundary_RemainsPendingUntilBothFormsSettle()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/Reactivity/SubmitFormLoading**", async route =>
        {
            if (route.Request.Url.Contains("delayMs=2000", StringComparison.Ordinal))
            {
                secondStarted.TrySetResult();
                await releaseSecond.Task;
            }
            else
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            }

            await route.ContinueAsync();
        });

        try
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/Reactivity/FormLoading");
            await page.EvaluateAsync(
                """
                () => {
                    for (const [formId, frameId] of [
                        ['loading-site-form', 'loading-shared-first-frame'],
                        ['loading-site-form-secondary', 'loading-shared-second-frame']
                    ]) {
                        const form = document.getElementById(formId);
                        const frame = document.createElement('turbo-frame');
                        frame.id = frameId;
                        form.replaceWith(frame);
                        frame.append(form);
                    }
                }
                """);
            await page.Locator("#loading-site-form button").ClickAsync();
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await page.Locator("#loading-site-form-secondary button").ClickAsync();
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var boundary = page.Locator("section[data-rw-loading-boundary]:has(#loading-site-form)");
            var indicator = page.Locator("section[data-rw-loading-boundary]:has(#loading-site-form) > [data-rw-loading-indicator]");
            await Assertions.Expect(indicator).ToBeVisibleAsync();
            Assert.Equal("pending", await boundary.GetAttributeAsync("data-rw-loading-state"));

            releaseFirst.TrySetResult();
            await Assertions.Expect(page.Locator("#form-loading-result")).ToContainTextAsync("Saved site form.");
            await Assertions.Expect(indicator).ToBeVisibleAsync();
            Assert.Equal("pending", await boundary.GetAttributeAsync("data-rw-loading-state"));

            releaseSecond.TrySetResult();
            await Assertions.Expect(page.Locator("#form-loading-result")).ToContainTextAsync("Saved second site form.");
            await Assertions.Expect(indicator).ToBeHiddenAsync();
            Assert.Null(await boundary.GetAttributeAsync("data-rw-loading-state"));
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseSecond.TrySetResult();
        }
    }

    [Fact]
    public async Task RemovedLocalIndicator_RebindsPendingFormToOuterBoundary()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var tokenStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseToken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/_rw/antiforgery/token", async route =>
        {
            tokenStarted.TrySetResult();
            await releaseToken.Task;
            await route.ContinueAsync();
        });

        try
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/Reactivity/FormLoading");
            await page.Locator("#loading-local-primary").ClickAsync();
            await tokenStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Assertions.Expect(page.Locator("#loading-local-form [data-rw-loading-indicator]")).ToBeVisibleAsync();
            await page.Locator("#loading-local-form [data-rw-loading-indicator]").EvaluateAsync("indicator => indicator.remove()");
            var outerIndicator = page.Locator("section[data-rw-loading-boundary]:has(#loading-site-form) > [data-rw-loading-indicator]");
            await Assertions.Expect(outerIndicator).ToBeVisibleAsync();
            Assert.Equal("pending", await page.Locator("#loading-local-form").GetAttributeAsync("data-rw-loading-state"));

            releaseToken.TrySetResult();
            await Assertions.Expect(page.Locator("#form-loading-result")).ToContainTextAsync("Saved local form.");
            await Assertions.Expect(outerIndicator).ToBeHiddenAsync();
            Assert.Null(await page.Locator("#loading-local-form").GetAttributeAsync("data-rw-loading-state"));
        }
        finally
        {
            releaseToken.TrySetResult();
        }
    }

    [Fact]
    public async Task RemovedPendingForm_CancelsTokenHoldAndRestoresExternalSubmitter()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var tokenStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseToken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var posts = 0;
        page.Request += (_, request) =>
        {
            if (request.Method == "POST" && request.Url.Contains("/Reactivity/SubmitFormLoading", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref posts);
            }
        };
        await page.RouteAsync("**/_rw/antiforgery/token", async route =>
        {
            tokenStarted.TrySetResult();
            await releaseToken.Task;
            await route.ContinueAsync();
        });

        try
        {
            await page.GotoAsync($"{_fixture.BaseUrl}/Reactivity/FormLoading");
            await page.Locator("#loading-local-primary").ClickAsync();
            await tokenStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Assertions.Expect(page.Locator("#loading-local-form [data-rw-loading-indicator]")).ToBeVisibleAsync();
            Assert.True(await page.Locator("#loading-local-external").IsDisabledAsync());

            await page.Locator("#loading-local-form").EvaluateAsync("form => form.remove()");
            await page.WaitForFunctionAsync(
                "() => !document.documentElement.hasAttribute('data-rw-loading-form-state')");
            Assert.False(await page.Locator("#loading-local-external").IsDisabledAsync());
            await Assertions.Expect(page.Locator("section[data-rw-loading-boundary]:has(#loading-site-form) > [data-rw-loading-indicator]")).ToBeHiddenAsync();

            releaseToken.TrySetResult();
            await page.WaitForTimeoutAsync(250);
            Assert.Equal(0, posts);
        }
        finally
        {
            releaseToken.TrySetResult();
        }
    }
}
