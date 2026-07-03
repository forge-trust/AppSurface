using ForgeTrust.AppSurface.Auth;
using ForgeTrust.RazorWire.Auth;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.RazorWire.TagHelpers;

/// <summary>
/// Projects a passive AppSurface auth result into one server-rendered RazorWire auth state.
/// </summary>
/// <remarks>
/// The helper evaluates a host-owned policy through <see cref="IRazorWireAuthResultProvider"/> and renders only the
/// matching child slot. It never challenges, forbids, redirects, signs callers in or out, mutates cookies, chooses
/// authentication schemes, or grants access. Protected endpoints and actions must still enforce host-owned auth.
/// </remarks>
[HtmlTargetElement("rw:auth-view", Attributes = "policy")]
[RestrictChildren(
    "rw:auth-allowed",
    "rw:auth-anonymous",
    "rw:auth-forbidden",
    "rw:auth-setup-failure",
    "rw:auth-unsafe-navigation",
    "rw:auth-stale",
    "rw:auth-unknown")]
public sealed class AuthViewTagHelper : TagHelper
{
    internal static readonly object SlotContextKey = new();

    /// <summary>
    /// Attribute emitted on static-export auth projection output.
    /// </summary>
    public const string StaticAttributeName = "data-rw-auth-static";

    /// <summary>
    /// Attribute emitted with a safe reason label when static export must fail before writing the artifact.
    /// </summary>
    public const string StaticViolationAttributeName = "data-rw-auth-export-violation";

    /// <summary>
    /// Reason label for an auth view that lacks an explicit static anonymous fallback.
    /// </summary>
    public const string StaticMissingFallbackReason = "auth-missing-fallback";

    /// <summary>
    /// Reason label for protected auth UI that has no v0 static export contract.
    /// </summary>
    public const string StaticPrivateContentReason = "auth-private-content";

    /// <summary>
    /// Reason label for login/auth metadata that is unsafe for static output.
    /// </summary>
    public const string StaticUnsafeMetadataReason = "auth-unsafe-metadata";

    /// <summary>
    /// Gets or sets the host-owned policy name to project.
    /// </summary>
    [HtmlAttributeName("policy")]
    public string? Policy { get; set; }

    /// <summary>
    /// Gets or sets an optional host-owned authorization resource.
    /// </summary>
    [HtmlAttributeName("resource")]
    public object? Resource { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether safe policy and reason diagnostics should be emitted.
    /// </summary>
    /// <remarks>
    /// Diagnostics are off by default so rendered HTML does not expose policy names, failure reasons, or setup details
    /// unless the host explicitly asks for them.
    /// </remarks>
    [HtmlAttributeName("include-diagnostics")]
    public bool IncludeDiagnostics { get; set; }

    /// <summary>
    /// Gets or sets the current MVC view context.
    /// </summary>
    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = default!;

    /// <inheritdoc />
    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var staticExportContext = RazorWireStaticExportContext.Resolve(ViewContext.HttpContext);
        var projection = staticExportContext.IsStaticAuthProjection
            ? RazorWireAuthProjection.StaticAnonymous()
            : RazorWireAuthProjection.FromResult(await ResolveResultAsync(ViewContext.HttpContext, Policy, Resource));
        var policy = string.IsNullOrWhiteSpace(Policy) ? null : Policy.Trim();
        var slotContext = new AuthSlotContext(projection.State);

        context.Items[SlotContextKey] = slotContext;

        var childContent = await output.GetChildContentAsync();
        output.TagName = "div";
        output.Attributes.RemoveAll("policy");
        output.Attributes.RemoveAll("resource");
        output.Attributes.RemoveAll("include-diagnostics");
        ApplyProjectionAttributes(
            output,
            projection,
            "auth-view",
            policy,
            IncludeDiagnostics && !staticExportContext.IsStaticAuthProjection,
            staticExportContext.IsStaticAuthProjection);

        if (staticExportContext.IsStaticAuthProjection
            && (!slotContext.MatchedExplicitSlot || string.IsNullOrWhiteSpace(childContent.GetContent())))
        {
            output.Attributes.SetAttribute(StaticViolationAttributeName, StaticMissingFallbackReason);
            output.Content.Clear();
            return;
        }

        if (string.IsNullOrWhiteSpace(childContent.GetContent()))
        {
            output.Content.SetContent(DefaultContent(projection.State));
        }
        else
        {
            output.Content.SetHtmlContent(childContent);
        }
    }

    internal static async Task<AppSurfaceAuthResult> ResolveResultAsync(
        HttpContext httpContext,
        string? policy,
        object? resource)
    {
        if (string.IsNullOrWhiteSpace(policy))
        {
            return AppSurfaceAuthResult.MissingPolicy(
                AppSurfaceAuthContext.Anonymous,
                "RazorWire auth projection requires a non-empty host policy name.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [RazorWireAuthDiagnostics.DiagnosticCodeMetadataKey] = RazorWireAuthDiagnostics.MissingPolicy,
                });
        }

        var provider = httpContext.RequestServices.GetService<IRazorWireAuthResultProvider>();
        if (provider is null)
        {
            return AppSurfaceAuthResult.MissingServices(
                AppSurfaceAuthContext.Anonymous,
                "RazorWire auth projection provider is not registered.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [RazorWireAuthDiagnostics.DiagnosticCodeMetadataKey] = RazorWireAuthDiagnostics.MissingProvider,
                });
        }

        return await provider.AuthorizeAsync(
            RazorWireAuthRequest.Create(policy.Trim(), resource),
            httpContext.RequestAborted);
    }

    internal static void ApplyProjectionAttributes(
        TagHelperOutput output,
        RazorWireAuthProjection projection,
        string helper,
        string? policy,
        bool includeDiagnostics,
        bool staticExport = false)
    {
        output.Attributes.SetAttribute("data-rw-auth-state", projection.StateToken);
        output.Attributes.SetAttribute("data-rw-auth-helper", helper);
        if (staticExport)
        {
            output.Attributes.SetAttribute(StaticAttributeName, "true");
        }

        if (!staticExport && projection.Outcome is not null)
        {
            output.Attributes.SetAttribute("data-rw-auth-outcome", projection.Outcome.Value.ToString());
        }

        if (includeDiagnostics)
        {
            if (!string.IsNullOrWhiteSpace(policy))
            {
                output.Attributes.SetAttribute("data-rw-auth-policy", policy);
            }

            if (projection.Reason is not null)
            {
                output.Attributes.SetAttribute("data-rw-auth-reason", projection.Reason.Value.ToString());
            }
        }
    }

    internal static string DefaultContent(RazorWireAuthProjectionState state)
    {
        return state switch
        {
            RazorWireAuthProjectionState.Allowed => string.Empty,
            RazorWireAuthProjectionState.Anonymous => "Sign in to continue.",
            RazorWireAuthProjectionState.Forbidden => "You do not have permission.",
            RazorWireAuthProjectionState.UnsafeNavigation => "Return target was not allowed.",
            RazorWireAuthProjectionState.StaleOrUnknownSession => "Your session may have expired.",
            RazorWireAuthProjectionState.SetupFailure => "This action is unavailable right now.",
            _ => "Authorization is not available yet.",
        };
    }

    /// <summary>
    /// Carries per-render slot resolution state for an <c>rw:auth-view</c>.
    /// </summary>
    /// <remarks>
    /// Slot helpers mutate <see cref="MatchedExplicitSlot"/> during child-content execution. Static export reads that flag
    /// after slot resolution to decide whether a protected view supplied an explicit anonymous fallback before any artifact
    /// is written.
    /// </remarks>
    internal sealed class AuthSlotContext
    {
        /// <summary>
        /// Creates a slot context for the projected auth state.
        /// </summary>
        /// <param name="state">Projected RazorWire auth state for the current render.</param>
        public AuthSlotContext(RazorWireAuthProjectionState state)
        {
            State = state;
        }

        /// <summary>
        /// Gets the projected RazorWire auth state that slot helpers compare against.
        /// </summary>
        public RazorWireAuthProjectionState State { get; }

        /// <summary>
        /// Gets or sets a value indicating whether an explicit slot matching <see cref="State"/> rendered.
        /// </summary>
        /// <remarks>
        /// Only the slot resolution path should set this flag. It must be set before <c>rw:auth-view</c> evaluates the
        /// static-export missing-fallback check.
        /// </remarks>
        public bool MatchedExplicitSlot { get; set; }
    }
}

/// <summary>
/// Renders child content when the surrounding auth view is allowed.
/// </summary>
[HtmlTargetElement("rw:auth-allowed", ParentTag = "rw:auth-view")]
public sealed class AuthAllowedSlotTagHelper : AuthSlotTagHelper
{
    /// <inheritdoc />
    protected override RazorWireAuthProjectionState State => RazorWireAuthProjectionState.Allowed;
}

/// <summary>
/// Renders child content when the surrounding auth view requires authentication.
/// </summary>
[HtmlTargetElement("rw:auth-anonymous", ParentTag = "rw:auth-view")]
public sealed class AuthAnonymousSlotTagHelper : AuthSlotTagHelper
{
    /// <inheritdoc />
    protected override RazorWireAuthProjectionState State => RazorWireAuthProjectionState.Anonymous;
}

/// <summary>
/// Renders child content when the surrounding auth view is forbidden.
/// </summary>
[HtmlTargetElement("rw:auth-forbidden", ParentTag = "rw:auth-view")]
public sealed class AuthForbiddenSlotTagHelper : AuthSlotTagHelper
{
    /// <inheritdoc />
    protected override RazorWireAuthProjectionState State => RazorWireAuthProjectionState.Forbidden;
}

/// <summary>
/// Renders child content when host auth setup is missing or failed.
/// </summary>
[HtmlTargetElement("rw:auth-setup-failure", ParentTag = "rw:auth-view")]
public sealed class AuthSetupFailureSlotTagHelper : AuthSlotTagHelper
{
    /// <inheritdoc />
    protected override RazorWireAuthProjectionState State => RazorWireAuthProjectionState.SetupFailure;
}

/// <summary>
/// Renders child content when a return or navigation target was unsafe.
/// </summary>
[HtmlTargetElement("rw:auth-unsafe-navigation", ParentTag = "rw:auth-view")]
public sealed class AuthUnsafeNavigationSlotTagHelper : AuthSlotTagHelper
{
    /// <inheritdoc />
    protected override RazorWireAuthProjectionState State => RazorWireAuthProjectionState.UnsafeNavigation;
}

/// <summary>
/// Renders child content when the surrounding auth view has a stale or unknown session.
/// </summary>
[HtmlTargetElement("rw:auth-stale", ParentTag = "rw:auth-view")]
public sealed class AuthStaleSlotTagHelper : AuthSlotTagHelper
{
    /// <inheritdoc />
    protected override RazorWireAuthProjectionState State => RazorWireAuthProjectionState.StaleOrUnknownSession;
}

/// <summary>
/// Renders child content when the surrounding auth view has no evaluated result.
/// </summary>
[HtmlTargetElement("rw:auth-unknown", ParentTag = "rw:auth-view")]
public sealed class AuthUnknownSlotTagHelper : AuthSlotTagHelper
{
    /// <inheritdoc />
    protected override RazorWireAuthProjectionState State => RazorWireAuthProjectionState.Unknown;
}

/// <summary>
/// Conditionally renders child content when the projected auth state matches.
/// </summary>
[HtmlTargetElement("rw:auth-gate", Attributes = "policy")]
public class AuthGateTagHelper : TagHelper
{
    /// <summary>
    /// Gets or sets the host-owned policy name to project.
    /// </summary>
    [HtmlAttributeName("policy")]
    public string? Policy { get; set; }

    /// <summary>
    /// Gets or sets an optional host-owned authorization resource.
    /// </summary>
    [HtmlAttributeName("resource")]
    public object? Resource { get; set; }

    /// <summary>
    /// Gets or sets the state that should render the child content. Defaults to <c>allowed</c>.
    /// </summary>
    [HtmlAttributeName("state")]
    public string? State { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether safe policy and reason diagnostics should be emitted.
    /// </summary>
    [HtmlAttributeName("include-diagnostics")]
    public bool IncludeDiagnostics { get; set; }

    /// <summary>
    /// Gets or sets the current MVC view context.
    /// </summary>
    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = default!;

    /// <inheritdoc />
    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var staticExportContext = RazorWireStaticExportContext.Resolve(ViewContext.HttpContext);
        var projection = staticExportContext.IsStaticAuthProjection
            ? RazorWireAuthProjection.StaticAnonymous()
            : RazorWireAuthProjection.FromResult(await AuthViewTagHelper.ResolveResultAsync(ViewContext.HttpContext, Policy, Resource));
        if (staticExportContext.IsStaticAuthProjection && TargetState == RazorWireAuthProjectionState.Allowed)
        {
            output.TagName = "div";
            output.Attributes.RemoveAll("policy");
            output.Attributes.RemoveAll("resource");
            output.Attributes.RemoveAll("state");
            output.Attributes.RemoveAll("include-diagnostics");
            AuthViewTagHelper.ApplyProjectionAttributes(
                output,
                projection,
                HelperName,
                policy: null,
                includeDiagnostics: false,
                staticExport: true);
            output.Attributes.SetAttribute(
                AuthViewTagHelper.StaticViolationAttributeName,
                AuthViewTagHelper.StaticPrivateContentReason);
            output.Content.Clear();
            return;
        }

        if (projection.State != TargetState)
        {
            output.SuppressOutput();
            return;
        }

        output.TagName = "div";
        output.Attributes.RemoveAll("policy");
        output.Attributes.RemoveAll("resource");
        output.Attributes.RemoveAll("state");
        output.Attributes.RemoveAll("include-diagnostics");
        AuthViewTagHelper.ApplyProjectionAttributes(
            output,
            projection,
            HelperName,
            string.IsNullOrWhiteSpace(Policy) ? null : Policy.Trim(),
            IncludeDiagnostics && !staticExportContext.IsStaticAuthProjection,
            staticExportContext.IsStaticAuthProjection);
    }

    /// <summary>
    /// Gets the helper name emitted in <c>data-rw-auth-helper</c>.
    /// </summary>
    protected virtual string HelperName => "auth-gate";

    /// <summary>
    /// Gets the projected state required for rendering.
    /// </summary>
    protected virtual RazorWireAuthProjectionState TargetState => ParseState(State);

    private static RazorWireAuthProjectionState ParseState(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            null or "" or "allowed" => RazorWireAuthProjectionState.Allowed,
            "anonymous" or "unauthenticated" => RazorWireAuthProjectionState.Anonymous,
            "forbidden" => RazorWireAuthProjectionState.Forbidden,
            "setup-failure" or "setupfailure" => RazorWireAuthProjectionState.SetupFailure,
            "unsafe-navigation" or "unsafenavigation" => RazorWireAuthProjectionState.UnsafeNavigation,
            "stale" or "stale-or-unknown-session" => RazorWireAuthProjectionState.StaleOrUnknownSession,
            "unknown" or "loading" => RazorWireAuthProjectionState.Unknown,
            _ => RazorWireAuthProjectionState.Unknown,
        };
    }
}

/// <summary>
/// Alias over <see cref="AuthGateTagHelper"/> for policy-oriented allowed-state rendering.
/// </summary>
[HtmlTargetElement("rw:permission-gate", Attributes = "policy")]
public sealed class PermissionGateTagHelper : AuthGateTagHelper
{
    /// <summary>
    /// Hides the general gate state selector because permission gates are allowed-only aliases.
    /// </summary>
    [HtmlAttributeNotBound]
    public new string? State { get; set; }

    /// <inheritdoc />
    protected override string HelperName => "permission-gate";

    /// <inheritdoc />
    protected override RazorWireAuthProjectionState TargetState => RazorWireAuthProjectionState.Allowed;
}

/// <summary>
/// Renders a host-owned login link without initiating authentication.
/// </summary>
/// <remarks>
/// The helper emits a normal anchor. The host owns the target URL and authentication behavior.
/// </remarks>
[HtmlTargetElement("rw:login-link", Attributes = "href")]
public sealed class LoginLinkTagHelper : TagHelper
{
    /// <summary>
    /// Gets or sets the host-owned login URL.
    /// </summary>
    [HtmlAttributeName("href")]
    public string? Href { get; set; }

    /// <summary>
    /// Gets or sets the return-url policy. Use <c>current-path</c> to append the current local path as
    /// <c>returnUrl</c>.
    /// </summary>
    [HtmlAttributeName("return-url-policy")]
    public string? ReturnUrlPolicy { get; set; }

    /// <summary>
    /// Gets or sets the current MVC view context.
    /// </summary>
    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = default!;

    /// <inheritdoc />
    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var childContent = await output.GetChildContentAsync();
        var childHtml = childContent.GetContent();
        var staticExportContext = RazorWireStaticExportContext.Resolve(ViewContext.HttpContext);

        if (staticExportContext.IsStaticAuthProjection && !IsSafeStaticHref(ViewContext.HttpContext, Href))
        {
            output.TagName = "span";
            output.Attributes.RemoveAll("href");
            output.Attributes.RemoveAll("return-url-policy");
            output.Attributes.SetAttribute("data-rw-auth-helper", "login-link");
            output.Attributes.SetAttribute(AuthViewTagHelper.StaticAttributeName, "true");
            output.Attributes.SetAttribute(
                AuthViewTagHelper.StaticViolationAttributeName,
                AuthViewTagHelper.StaticUnsafeMetadataReason);
            output.Content.Clear();
            return;
        }

        output.TagName = "a";
        output.Attributes.RemoveAll("return-url-policy");
        output.Attributes.SetAttribute("href", ResolveHref(ViewContext.HttpContext, Href, ReturnUrlPolicy));
        output.Attributes.SetAttribute("data-rw-auth-helper", "login-link");
        if (string.IsNullOrWhiteSpace(childHtml))
        {
            output.Content.SetContent("Sign in");
        }
        else
        {
            output.Content.SetHtmlContent(childContent);
        }
    }

    private static string ResolveHref(HttpContext httpContext, string? href, string? returnUrlPolicy)
    {
        var target = string.IsNullOrWhiteSpace(href) ? "#" : href.Trim();
        if (!string.Equals(returnUrlPolicy?.Trim(), "current-path", StringComparison.OrdinalIgnoreCase))
        {
            return target;
        }

        var returnUrl = httpContext.Request.PathBase.Add(httpContext.Request.Path).Value ?? "/";
        returnUrl += httpContext.Request.QueryString.Value;
        var fragmentStart = target.IndexOf('#', StringComparison.Ordinal);
        var targetWithoutFragment = fragmentStart < 0 ? target : target[..fragmentStart];
        var fragment = fragmentStart < 0 ? string.Empty : target[fragmentStart..];
        var separator = targetWithoutFragment.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return targetWithoutFragment + separator + "returnUrl=" + Uri.EscapeDataString(returnUrl) + fragment;
    }

    private static bool IsSafeStaticHref(HttpContext httpContext, string? href)
    {
        if (string.IsNullOrWhiteSpace(href) || href.Any(char.IsControl))
        {
            return false;
        }

        var target = href.Trim();
        if (!Uri.TryCreate(target, UriKind.RelativeOrAbsolute, out var uri))
        {
            return false;
        }

        if (!uri.IsAbsoluteUri)
        {
            return !target.StartsWith("\\", StringComparison.Ordinal)
                   && !HasNetworkPathPrefix(target)
                   && !target.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase);
        }

        return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && string.Equals(uri.Scheme, httpContext.Request.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(uri.Authority, httpContext.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasNetworkPathPrefix(string value)
    {
        return value.Length >= 2
               && IsSlashOrBackslash(value[0])
               && IsSlashOrBackslash(value[1]);
    }

    private static bool IsSlashOrBackslash(char value)
    {
        return value is '/' or '\\';
    }
}

/// <summary>
/// Renders a host-owned logout form without signing callers out.
/// </summary>
[HtmlTargetElement("rw:logout-button", Attributes = "action")]
public sealed class LogoutButtonTagHelper : TagHelper
{
    private readonly IAntiforgery? _antiforgery;

    /// <summary>
    /// Creates a logout button helper.
    /// </summary>
    /// <param name="antiforgery">Optional ASP.NET Core anti-forgery service used to protect the generated POST form.</param>
    public LogoutButtonTagHelper(IAntiforgery? antiforgery = null)
    {
        _antiforgery = antiforgery;
    }

    /// <summary>
    /// Gets or sets the host-owned logout form action.
    /// </summary>
    [HtmlAttributeName("action")]
    public string? Action { get; set; }

    /// <summary>
    /// Gets or sets the return-url policy. Use <c>current-path</c> to include the current local path as a hidden
    /// <c>returnUrl</c> input.
    /// </summary>
    [HtmlAttributeName("return-url-policy")]
    public string? ReturnUrlPolicy { get; set; }

    /// <summary>
    /// Gets or sets the current MVC view context.
    /// </summary>
    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = default!;

    /// <inheritdoc />
    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var childContent = await output.GetChildContentAsync();
        if (RazorWireStaticExportContext.Resolve(ViewContext.HttpContext).IsStaticAuthProjection)
        {
            output.TagName = "div";
            output.Attributes.RemoveAll("action");
            output.Attributes.RemoveAll("return-url-policy");
            output.Attributes.SetAttribute("data-rw-auth-helper", "logout-button");
            output.Attributes.SetAttribute(AuthViewTagHelper.StaticAttributeName, "true");
            output.Attributes.SetAttribute(
                AuthViewTagHelper.StaticViolationAttributeName,
                AuthViewTagHelper.StaticPrivateContentReason);
            output.Content.Clear();
            return;
        }

        var buttonText = string.IsNullOrWhiteSpace(childContent.GetContent()) ? "Sign out" : childContent.GetContent();

        var action = string.IsNullOrWhiteSpace(Action) ? "#" : Action.Trim();

        output.TagName = "form";
        output.Attributes.RemoveAll("return-url-policy");
        output.Attributes.SetAttribute("method", "post");
        output.Attributes.SetAttribute("action", action);
        output.Attributes.SetAttribute("data-rw-auth-helper", "logout-button");

        var content = $"<button type=\"submit\">{System.Net.WebUtility.HtmlEncode(buttonText)}</button>";
        if (_antiforgery is not null && IsLocalFormAction(ViewContext.HttpContext, action))
        {
            var tokenSet = _antiforgery.GetAndStoreTokens(ViewContext.HttpContext);
            if (!string.IsNullOrWhiteSpace(tokenSet.FormFieldName) && !string.IsNullOrWhiteSpace(tokenSet.RequestToken))
            {
                content += $"<input type=\"hidden\" name=\"{System.Net.WebUtility.HtmlEncode(tokenSet.FormFieldName)}\" value=\"{System.Net.WebUtility.HtmlEncode(tokenSet.RequestToken)}\">";
            }
        }

        if (string.Equals(ReturnUrlPolicy?.Trim(), "current-path", StringComparison.OrdinalIgnoreCase))
        {
            var returnUrl = ViewContext.HttpContext.Request.PathBase.Add(ViewContext.HttpContext.Request.Path).Value ?? "/";
            returnUrl += ViewContext.HttpContext.Request.QueryString.Value;
            content += $"<input type=\"hidden\" name=\"returnUrl\" value=\"{System.Net.WebUtility.HtmlEncode(returnUrl)}\">";
        }

        output.Content.SetHtmlContent(content);
    }

    private static bool IsLocalFormAction(HttpContext httpContext, string action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return true;
        }

        if (action.Any(char.IsControl))
        {
            return false;
        }

        if (!Uri.TryCreate(action, UriKind.RelativeOrAbsolute, out var uri))
        {
            return false;
        }

        if (!uri.IsAbsoluteUri)
        {
            return !action.StartsWith("\\", StringComparison.Ordinal)
                   && !HasNetworkPathPrefix(action);
        }

        return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && string.Equals(uri.Scheme, httpContext.Request.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(uri.Authority, httpContext.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasNetworkPathPrefix(string value)
    {
        return value.Length >= 2
               && IsSlashOrBackslash(value[0])
               && IsSlashOrBackslash(value[1]);
    }

    private static bool IsSlashOrBackslash(char value)
    {
        return value is '/' or '\\';
    }
}

/// <summary>
/// Base class for auth-view slot helpers.
/// </summary>
public abstract class AuthSlotTagHelper : TagHelper
{
    /// <summary>
    /// Gets the state that renders this slot.
    /// </summary>
    protected abstract RazorWireAuthProjectionState State { get; }

    /// <inheritdoc />
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (context.Items.TryGetValue(AuthViewTagHelper.SlotContextKey, out var value)
            && value is AuthViewTagHelper.AuthSlotContext slotContext
            && slotContext.State == State)
        {
            slotContext.MatchedExplicitSlot = true;
            output.TagName = null;
            output.Attributes.Clear();
            return;
        }

        output.SuppressOutput();
    }
}
