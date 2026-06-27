using ForgeTrust.AppSurface.Auth;
using ForgeTrust.RazorWire.Auth;
using ForgeTrust.RazorWire.TagHelpers;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.RazorWire.Tests;

public sealed class AuthProjectionTests
{
    [Theory]
    [MemberData(nameof(ProjectionCases))]
    public void Projection_FromResult_MapsEveryAuthOutcome(AppSurfaceAuthResult result, RazorWireAuthProjectionState state, string token)
    {
        var projection = RazorWireAuthProjection.FromResult(result);

        Assert.Equal(state, projection.State);
        Assert.Equal(token, projection.StateToken);
        Assert.Equal(result.Outcome, projection.Outcome);
        Assert.Equal(result.Reason, projection.Reason);
    }

    [Fact]
    public async Task AuthView_ProcessAsync_RendersSafeAllowedAttributes()
    {
        var output = CreateOutput("rw:auth-view", string.Empty, "policy", "docs.publish");
        var provider = new StubAuthResultProvider(AppSurfaceAuthResult.Allowed(message: "secret-ish"));

        await new AuthViewTagHelper
        {
            Policy = "docs.publish",
            ViewContext = CreateViewContext(provider),
        }.ProcessAsync(CreateContext("rw:auth-view"), output);

        Assert.Equal("div", output.TagName);
        Assert.Equal("allowed", output.Attributes["data-rw-auth-state"].Value);
        Assert.Equal("Allowed", output.Attributes["data-rw-auth-outcome"].Value);
        Assert.Equal("auth-view", output.Attributes["data-rw-auth-helper"].Value);
        Assert.False(output.Attributes.ContainsName("data-rw-auth-policy"));
        Assert.False(output.Attributes.ContainsName("data-rw-auth-reason"));
        Assert.DoesNotContain("secret-ish", output.Content.GetContent(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthView_ProcessAsync_WhenDiagnosticsIncluded_EmitsPolicyAndReason()
    {
        var output = CreateOutput("rw:auth-view", string.Empty, "policy", "docs.publish");
        var provider = new StubAuthResultProvider(AppSurfaceAuthResult.Forbidden());

        await new AuthViewTagHelper
        {
            Policy = " docs.publish ",
            IncludeDiagnostics = true,
            ViewContext = CreateViewContext(provider),
        }.ProcessAsync(CreateContext("rw:auth-view"), output);

        Assert.Equal("docs.publish", output.Attributes["data-rw-auth-policy"].Value);
        Assert.Equal("Forbidden", output.Attributes["data-rw-auth-reason"].Value);
    }

    [Fact]
    public async Task AuthView_ProcessAsync_WhenProviderMissing_RendersSetupFailure()
    {
        var output = CreateOutput("rw:auth-view", string.Empty, "policy", "docs.publish");

        await new AuthViewTagHelper
        {
            Policy = "docs.publish",
            ViewContext = CreateViewContext(provider: null),
        }.ProcessAsync(CreateContext("rw:auth-view"), output);

        Assert.Equal("setup-failure", output.Attributes["data-rw-auth-state"].Value);
        Assert.Equal("This action is unavailable right now.", output.Content.GetContent());
    }

    [Fact]
    public void Slot_Process_WhenStateMatches_RendersChildWithoutWrapper()
    {
        var context = CreateContext("rw:auth-allowed");
        context.Items[AuthViewTagHelper.SlotContextKey] = new AuthViewTagHelper.AuthSlotContext(RazorWireAuthProjectionState.Allowed);
        var output = CreateOutput("rw:auth-allowed", "Publish");

        new AuthAllowedSlotTagHelper().Process(context, output);

        Assert.Null(output.TagName);
        Assert.False(output.IsContentModified);
    }

    [Fact]
    public void Slot_Process_WhenStateDiffers_SuppressesOutput()
    {
        var context = CreateContext("rw:auth-allowed");
        context.Items[AuthViewTagHelper.SlotContextKey] = new AuthViewTagHelper.AuthSlotContext(RazorWireAuthProjectionState.Forbidden);
        var output = CreateOutput("rw:auth-allowed", "Publish");

        new AuthAllowedSlotTagHelper().Process(context, output);

        Assert.True(output.TagName is null);
        Assert.True(output.IsContentModified);
        Assert.Equal(string.Empty, output.Content.GetContent());
    }

    [Fact]
    public async Task AuthGate_ProcessAsync_WhenStateMatches_Renders()
    {
        var output = CreateOutput("rw:auth-gate", "Publish", "policy", "docs.publish");
        var provider = new StubAuthResultProvider(AppSurfaceAuthResult.Allowed());

        await new AuthGateTagHelper
        {
            Policy = "docs.publish",
            ViewContext = CreateViewContext(provider),
        }.ProcessAsync(CreateContext("rw:auth-gate"), output);

        Assert.Equal("div", output.TagName);
        Assert.Equal("auth-gate", output.Attributes["data-rw-auth-helper"].Value);
    }

    [Fact]
    public async Task AuthGate_ProcessAsync_WhenStateDiffers_Suppresses()
    {
        var output = CreateOutput("rw:auth-gate", "Publish", "policy", "docs.publish");
        var provider = new StubAuthResultProvider(AppSurfaceAuthResult.Forbidden());

        await new AuthGateTagHelper
        {
            Policy = "docs.publish",
            ViewContext = CreateViewContext(provider),
        }.ProcessAsync(CreateContext("rw:auth-gate"), output);

        Assert.Null(output.TagName);
        Assert.Equal(string.Empty, output.Content.GetContent());
    }

    [Fact]
    public void LoginLink_Process_AppendsCurrentPathReturnUrlWithoutExecutingAuth()
    {
        var output = CreateOutput("rw:login-link", "Sign in", "href", "/login");
        var viewContext = CreateViewContext(provider: null, "/docs", "?tab=auth");

        new LoginLinkTagHelper
        {
            Href = "/login",
            ReturnUrlPolicy = "current-path",
            ViewContext = viewContext,
        }.Process(CreateContext("rw:login-link"), output);

        Assert.Equal("a", output.TagName);
        Assert.Equal("/login?returnUrl=%2Fdocs%3Ftab%3Dauth", output.Attributes["href"].Value);
        Assert.Equal("login-link", output.Attributes["data-rw-auth-helper"].Value);
    }

    [Theory]
    [InlineData("/login#signin", "/login?returnUrl=%2Fdocs%3Ftab%3Dauth#signin")]
    [InlineData("/login?tenant=docs#signin", "/login?tenant=docs&returnUrl=%2Fdocs%3Ftab%3Dauth#signin")]
    public void LoginLink_Process_AppendsCurrentPathReturnUrlBeforeFragment(string href, string expectedHref)
    {
        var output = CreateOutput("rw:login-link", "Sign in", "href", href);
        var viewContext = CreateViewContext(provider: null, "/docs", "?tab=auth");

        new LoginLinkTagHelper
        {
            Href = href,
            ReturnUrlPolicy = "current-path",
            ViewContext = viewContext,
        }.Process(CreateContext("rw:login-link"), output);

        Assert.Equal("a", output.TagName);
        Assert.Equal(expectedHref, output.Attributes["href"].Value);
        Assert.Equal("login-link", output.Attributes["data-rw-auth-helper"].Value);
    }

    [Fact]
    public async Task LogoutButton_ProcessAsync_RendersHostOwnedPostForm()
    {
        var output = CreateOutput("rw:logout-button", "Sign out", "action", "/logout");

        await new LogoutButtonTagHelper
        {
            Action = "/logout",
            ReturnUrlPolicy = "current-path",
            ViewContext = CreateViewContext(provider: null, "/docs", "?tab=auth"),
        }.ProcessAsync(CreateContext("rw:logout-button"), output);

        Assert.Equal("form", output.TagName);
        Assert.Equal("post", output.Attributes["method"].Value);
        Assert.Equal("/logout", output.Attributes["action"].Value);
        Assert.Contains("<button type=\"submit\">Sign out</button>", output.Content.GetContent(), StringComparison.Ordinal);
        Assert.Contains("name=\"returnUrl\" value=\"/docs?tab=auth\"", output.Content.GetContent(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogoutButton_ProcessAsync_WhenAntiforgeryAvailable_RendersRequestToken()
    {
        var output = CreateOutput("rw:logout-button", "Sign out", "action", "/logout");

        await new LogoutButtonTagHelper(new StubAntiforgery())
        {
            Action = "/logout",
            ViewContext = CreateViewContext(provider: null),
        }.ProcessAsync(CreateContext("rw:logout-button"), output);

        Assert.Contains(
            "name=\"__RequestVerificationToken\" value=\"request-token\"",
            output.Content.GetContent(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogoutButton_ProcessAsync_WhenActionIsExternal_DoesNotRenderRequestToken()
    {
        var output = CreateOutput("rw:logout-button", "Sign out", "action", "https://idp.example/logout");

        await new LogoutButtonTagHelper(new StubAntiforgery())
        {
            Action = "https://idp.example/logout",
            ViewContext = CreateViewContext(provider: null),
        }.ProcessAsync(CreateContext("rw:logout-button"), output);

        Assert.Equal("https://idp.example/logout", output.Attributes["action"].Value);
        Assert.DoesNotContain("__RequestVerificationToken", output.Content.GetContent(), StringComparison.Ordinal);
        Assert.DoesNotContain("request-token", output.Content.GetContent(), StringComparison.Ordinal);
    }

    public static TheoryData<AppSurfaceAuthResult, RazorWireAuthProjectionState, string> ProjectionCases()
    {
        return new TheoryData<AppSurfaceAuthResult, RazorWireAuthProjectionState, string>
        {
            { AppSurfaceAuthResult.Allowed(), RazorWireAuthProjectionState.Allowed, "allowed" },
            { AppSurfaceAuthResult.Challenge(), RazorWireAuthProjectionState.Anonymous, "anonymous" },
            { AppSurfaceAuthResult.Forbidden(), RazorWireAuthProjectionState.Forbidden, "forbidden" },
            { AppSurfaceAuthResult.MissingServices(), RazorWireAuthProjectionState.SetupFailure, "setup-failure" },
            { AppSurfaceAuthResult.UnsafeReturnUrl(), RazorWireAuthProjectionState.UnsafeNavigation, "unsafe-navigation" },
            { AppSurfaceAuthResult.StaleOrUnknownSession(), RazorWireAuthProjectionState.StaleOrUnknownSession, "stale-or-unknown-session" },
        };
    }

    private static ViewContext CreateViewContext(IRazorWireAuthResultProvider? provider, string path = "/", string query = "")
    {
        var services = new ServiceCollection();
        if (provider is not null)
        {
            services.AddSingleton(provider);
        }

        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        httpContext.Request.Path = path;
        httpContext.Request.QueryString = new QueryString(query);

        return new ViewContext(
            new ActionContext(httpContext, new Microsoft.AspNetCore.Routing.RouteData(), new ActionDescriptor()),
            NullView.Instance,
            new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()),
            new TempDataDictionary(httpContext, new NullTempDataProvider()),
            TextWriter.Null,
            new HtmlHelperOptions());
    }

    private static TagHelperContext CreateContext(string tagName)
    {
        return new TagHelperContext(
            tagName,
            new TagHelperAttributeList(),
            new Dictionary<object, object>(),
            Guid.NewGuid().ToString("N"));
    }

    private static TagHelperOutput CreateOutput(string tagName, string childContent, params string[] nameValuePairs)
    {
        var attributes = new TagHelperAttributeList();
        for (var i = 0; i + 1 < nameValuePairs.Length; i += 2)
        {
            attributes.SetAttribute(nameValuePairs[i], nameValuePairs[i + 1]);
        }

        return new TagHelperOutput(
            tagName,
            attributes,
            (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent().SetHtmlContent(childContent)));
    }

    private sealed class StubAuthResultProvider : IRazorWireAuthResultProvider
    {
        private readonly AppSurfaceAuthResult _result;

        public StubAuthResultProvider(AppSurfaceAuthResult result)
        {
            _result = result;
        }

        public Task<AppSurfaceAuthResult> AuthorizeAsync(
            RazorWireAuthRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_result);
        }
    }

    private sealed class NullView : IView
    {
        public static readonly NullView Instance = new();

        public string Path => string.Empty;

        public Task RenderAsync(ViewContext context)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal);
        }

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }

    private sealed class StubAntiforgery : IAntiforgery
    {
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext)
        {
            return new AntiforgeryTokenSet(
                "request-token",
                "cookie-token",
                "__RequestVerificationToken",
                "RequestVerificationToken");
        }

        public AntiforgeryTokenSet GetTokens(HttpContext httpContext)
        {
            return GetAndStoreTokens(httpContext);
        }

        public Task<bool> IsRequestValidAsync(HttpContext httpContext)
        {
            return Task.FromResult(true);
        }

        public Task ValidateRequestAsync(HttpContext httpContext)
        {
            return Task.CompletedTask;
        }

        public void SetCookieTokenAndHeader(HttpContext httpContext)
        {
        }
    }
}
