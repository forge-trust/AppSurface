using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Web.Push;

/// <summary>Maps the explicit protected AppSurface Web Push subscription rail.</summary>
public static class AppSurfaceWebPushEndpointRouteBuilderExtensions
{
    private const int MaximumBodyBytes = 16 * 1024;

    /// <summary>Maps cookie-authenticated subscription endpoints with package-owned antiforgery validation.</summary>
    /// <param name="endpoints">The application-root endpoint route builder. Route groups are rejected because they would move the package's fixed client asset.</param>
    /// <param name="path">The literal app-root-relative base path for configuration, PUT, and DELETE. Route parameters, catch-alls, and traversal segments are not supported.</param>
    /// <param name="authorizationPolicy">The nonblank host-owned named policy, including exactly one cookie authentication scheme, evaluated directly inside every handler.</param>
    /// <param name="rateLimiterPolicy">An optional host-owned named rate-limiter policy applied to every protected endpoint.</param>
    /// <remarks>
    /// Mapping is explicit and returns no convention builder, so callers cannot disable the package-owned security
    /// contract. Authorization is evaluated before VAPID configuration, antiforgery, parsing, or custody access.
    /// </remarks>
    /// <exception cref="ArgumentException">The builder is a route group, or the path, authorization policy, or supplied rate-limiter policy is blank, unsafe, or inside AppSurface's reserved route space.</exception>
    /// <exception cref="InvalidOperationException">The same package base path was already mapped.</exception>
    public static void MapAppSurfaceWebPushSubscriptions(
        this IEndpointRouteBuilder endpoints,
        string path,
        string authorizationPolicy,
        string? rateLimiterPolicy = null) =>
        Map(endpoints, path, authorizationPolicy, authenticationScheme: null, rateLimiterPolicy);

    /// <summary>Maps token-only subscription endpoints using one explicit bearer authentication scheme.</summary>
    /// <param name="endpoints">The application-root endpoint route builder. Route groups are rejected because they would move the package's fixed client asset.</param>
    /// <param name="path">The literal app-root-relative base path for configuration, PUT, and DELETE. Route parameters, catch-alls, and traversal segments are not supported.</param>
    /// <param name="authorizationPolicy">The nonblank host-owned named policy evaluated directly inside every handler.</param>
    /// <param name="authenticationScheme">The exact token authentication scheme. Sign-in-capable schemes such as cookies fail closed; ambient identities are ignored.</param>
    /// <param name="rateLimiterPolicy">An optional host-owned named rate-limiter policy applied to every protected endpoint.</param>
    /// <exception cref="ArgumentException">The builder is a route group, or the path, authorization policy, scheme, or supplied rate-limiter policy is blank, unsafe, or inside AppSurface's reserved route space.</exception>
    /// <exception cref="InvalidOperationException">The same package base path was already mapped.</exception>
    public static void MapAppSurfaceWebPushBearerSubscriptions(
        this IEndpointRouteBuilder endpoints,
        string path,
        string authorizationPolicy,
        string authenticationScheme,
        string? rateLimiterPolicy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationScheme);
        Map(endpoints, path, authorizationPolicy, authenticationScheme, rateLimiterPolicy);
    }

    private static void Map(
        IEndpointRouteBuilder endpoints,
        string path,
        string authorizationPolicy,
        string? authenticationScheme,
        string? rateLimiterPolicy)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints is RouteGroupBuilder)
        {
            throw new ArgumentException(
                "AppSurface Web Push must be mapped on the application-root endpoint builder so its fixed client asset remains at the documented path.",
                nameof(endpoints));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationPolicy);
        if (rateLimiterPolicy is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rateLimiterPolicy);
        }

        ValidateBasePath(path);

        var registry = endpoints.ServiceProvider.GetRequiredService<AppSurfaceWebPushRouteRegistry>();
        var mapAsset = registry.Claim(path);
        var configurationEndpoint = endpoints.MapGet(
                path + "/configuration",
                (Func<HttpContext, Task<IResult>>)(async context =>
                    await ConfigurationAsync(context, authorizationPolicy, authenticationScheme).ConfigureAwait(false)))
            .ExcludeFromDescription();

        var putEndpoint = endpoints.MapPut(
                path,
                (Func<HttpContext, Task<IResult>>)(async context =>
                    await PutAsync(context, authorizationPolicy, authenticationScheme).ConfigureAwait(false)))
            .ExcludeFromDescription();

        var deleteEndpoint = endpoints.MapDelete(
                path,
                (Func<HttpContext, Task<IResult>>)(async context =>
                    await DeleteAsync(context, authorizationPolicy, authenticationScheme).ConfigureAwait(false)))
            .ExcludeFromDescription();

        if (rateLimiterPolicy is not null)
        {
            configurationEndpoint.RequireRateLimiting(rateLimiterPolicy);
            putEndpoint.RequireRateLimiting(rateLimiterPolicy);
            deleteEndpoint.RequireRateLimiting(rateLimiterPolicy);
        }

        if (mapAsset)
        {
            endpoints.MapMethods(
                    AppSurfaceWebPushClientAsset.Path,
                    [HttpMethods.Get, HttpMethods.Head],
                    AppSurfaceWebPushClientAsset.WriteAsync)
                .ExcludeFromDescription();
        }
    }

    private static async Task<IResult> ConfigurationAsync(
        HttpContext context,
        string policyName,
        string? authenticationScheme)
    {
        var authorization = await AuthorizeAsync(context, policyName, authenticationScheme).ConfigureAwait(false);
        if (authorization.Failure is not null)
        {
            return authorization.Failure;
        }

        var options = context.RequestServices.GetRequiredService<IOptions<AppSurfaceWebPushOptions>>().Value;
        if (!options.VapidKeys.TryGetValue(options.ActiveVapidKeyId!, out var activeKey))
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "ASPUSH108", "Web Push configuration is unavailable.");
        }

        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (authenticationScheme is null)
        {
            try
            {
                var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
                var tokens = antiforgery.GetAndStoreTokens(context);
                return Results.Json(new
                {
                    schemaVersion = 1,
                    vapidKeyId = options.ActiveVapidKeyId,
                    applicationServerKey = activeKey.PublicKey,
                    requestProtection = "antiforgery",
                    antiforgery = new { headerName = tokens.HeaderName, requestToken = tokens.RequestToken },
                });
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return Problem(StatusCodes.Status503ServiceUnavailable, "ASPUSH104", "Antiforgery protection is unavailable.");
            }
        }

        return Results.Json(new
        {
            schemaVersion = 1,
            vapidKeyId = options.ActiveVapidKeyId,
            applicationServerKey = activeKey.PublicKey,
            requestProtection = "bearer",
        });
    }

    private static async Task<IResult> PutAsync(
        HttpContext context,
        string policyName,
        string? authenticationScheme)
    {
        var authorization = await AuthorizeAsync(context, policyName, authenticationScheme).ConfigureAwait(false);
        if (authorization.Failure is not null)
        {
            return authorization.Failure;
        }

        var protectionFailure = await ValidateWriteProtectionAsync(context, authenticationScheme).ConfigureAwait(false);
        if (protectionFailure is not null)
        {
            return protectionFailure;
        }

        var body = await ReadJsonBodyAsync(context).ConfigureAwait(false);
        if (body is JsonBodyFailure bodyFailure)
        {
            return bodyFailure.Result;
        }

        using var document = ((JsonBodySuccess)body).Document;

        AppSurfaceWebPushSubscription subscription;
        try
        {
            if (!HasExactProperties(document.RootElement, "schemaVersion", "endpoint", "keys", "vapidKeyId")
                || document.RootElement.GetProperty("schemaVersion").GetInt32() != 1
                || !HasExactProperties(document.RootElement.GetProperty("keys"), "p256dh", "auth"))
            {
                return Problem(StatusCodes.Status400BadRequest, "ASPUSH100", "The subscription schema is invalid.");
            }

            subscription = new AppSurfaceWebPushSubscription(
                document.RootElement.GetProperty("endpoint").GetString()!,
                document.RootElement.GetProperty("keys").GetProperty("p256dh").GetString()!,
                document.RootElement.GetProperty("keys").GetProperty("auth").GetString()!,
                document.RootElement.GetProperty("vapidKeyId").GetString()!);
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or ArgumentNullException or FormatException)
        {
            return Problem(StatusCodes.Status400BadRequest, "ASPUSH100", "The subscription schema is invalid.");
        }

        var options = context.RequestServices.GetRequiredService<IOptions<AppSurfaceWebPushOptions>>().Value;
        if (!string.Equals(subscription.VapidKeyId, options.ActiveVapidKeyId, StringComparison.Ordinal))
        {
            return Problem(StatusCodes.Status409Conflict, "ASPUSH109", "The active VAPID key changed; prepare again.");
        }

        if (!AppSurfaceWebPushValidation.TryValidateEndpoint(subscription.Endpoint, options.AllowedPushServiceOrigins, out _)
            || !AppSurfaceWebPushValidation.IsValidP256PublicKey(subscription.P256Dh)
            || !AppSurfaceWebPushValidation.TryDecodeCanonicalBase64Url(subscription.Auth, 16, out _)
            || !AppSurfaceWebPushValidation.IsSafeKeyId(subscription.VapidKeyId))
        {
            return Problem(StatusCodes.Status400BadRequest, "ASPUSH101", "The subscription is invalid.");
        }

        var custody = context.RequestServices.GetService<IAppSurfaceWebPushSubscriptionCustody>();
        if (custody is null)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "ASPUSH107", "Subscription custody is unavailable.");
        }

        try
        {
            var disposition = await custody.RegisterAsync(
                new AppSurfaceWebPushSubscriptionWriteContext(authorization.Principal!),
                subscription,
                context.RequestAborted).ConfigureAwait(false);
            return disposition switch
            {
                AppSurfaceWebPushRegistrationDisposition.Created => Results.NoContent(),
                AppSurfaceWebPushRegistrationDisposition.Updated => Results.NoContent(),
                AppSurfaceWebPushRegistrationDisposition.Unchanged => Results.NoContent(),
                AppSurfaceWebPushRegistrationDisposition.Conflict =>
                    Problem(StatusCodes.Status409Conflict, "ASPUSH106", "The subscription is owned by another principal."),
                AppSurfaceWebPushRegistrationDisposition.Rejected =>
                    Problem(StatusCodes.Status403Forbidden, "ASPUSH105", "Subscription custody rejected the request."),
                _ => Problem(StatusCodes.Status503ServiceUnavailable, "ASPUSH107", "Subscription custody returned an unsupported result."),
            };
        }
        catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "ASPUSH107", "Subscription custody is unavailable.");
        }
    }

    private static async Task<IResult> DeleteAsync(
        HttpContext context,
        string policyName,
        string? authenticationScheme)
    {
        var authorization = await AuthorizeAsync(context, policyName, authenticationScheme).ConfigureAwait(false);
        if (authorization.Failure is not null)
        {
            return authorization.Failure;
        }

        var protectionFailure = await ValidateWriteProtectionAsync(context, authenticationScheme).ConfigureAwait(false);
        if (protectionFailure is not null)
        {
            return protectionFailure;
        }

        var body = await ReadJsonBodyAsync(context).ConfigureAwait(false);
        if (body is JsonBodyFailure bodyFailure)
        {
            return bodyFailure.Result;
        }

        using var document = ((JsonBodySuccess)body).Document;

        AppSurfaceWebPushSubscriptionReference reference;
        try
        {
            if (!HasExactProperties(document.RootElement, "schemaVersion", "endpoint")
                || document.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
            {
                return Problem(StatusCodes.Status400BadRequest, "ASPUSH100", "The unregister schema is invalid.");
            }

            reference = new AppSurfaceWebPushSubscriptionReference(
                document.RootElement.GetProperty("endpoint").GetString()!);
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or ArgumentNullException or FormatException)
        {
            return Problem(StatusCodes.Status400BadRequest, "ASPUSH100", "The unregister schema is invalid.");
        }

        var options = context.RequestServices.GetRequiredService<IOptions<AppSurfaceWebPushOptions>>().Value;
        if (!AppSurfaceWebPushValidation.TryValidateEndpoint(reference.Endpoint, options.AllowedPushServiceOrigins, out _))
        {
            return Problem(StatusCodes.Status400BadRequest, "ASPUSH101", "The subscription is invalid.");
        }

        var custody = context.RequestServices.GetService<IAppSurfaceWebPushSubscriptionCustody>();
        if (custody is null)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "ASPUSH107", "Subscription custody is unavailable.");
        }

        try
        {
            var disposition = await custody.UnregisterAsync(
                new AppSurfaceWebPushSubscriptionWriteContext(authorization.Principal!),
                reference,
                context.RequestAborted).ConfigureAwait(false);
            return disposition switch
            {
                AppSurfaceWebPushUnregistrationDisposition.Removed => Results.NoContent(),
                AppSurfaceWebPushUnregistrationDisposition.NotFound => Results.NoContent(),
                AppSurfaceWebPushUnregistrationDisposition.Conflict =>
                    Problem(StatusCodes.Status409Conflict, "ASPUSH106", "The subscription is owned by another principal."),
                AppSurfaceWebPushUnregistrationDisposition.Rejected =>
                    Problem(StatusCodes.Status403Forbidden, "ASPUSH105", "Subscription custody rejected the request."),
                _ => Problem(StatusCodes.Status503ServiceUnavailable, "ASPUSH107", "Subscription custody returned an unsupported result."),
            };
        }
        catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "ASPUSH107", "Subscription custody is unavailable.");
        }
    }

    private static async ValueTask<AuthorizationResult> AuthorizeAsync(
        HttpContext context,
        string policyName,
        string? authenticationScheme)
    {
        var provider = context.RequestServices.GetService<IAuthorizationPolicyProvider>();
        var authorization = context.RequestServices.GetService<IAuthorizationService>();
        if (provider is null || authorization is null)
        {
            return new(null, Problem(StatusCodes.Status503ServiceUnavailable, "ASPUSH108", "Authorization services are unavailable."));
        }

        try
        {
            var policy = await provider.GetPolicyAsync(policyName).ConfigureAwait(false);
            if (policy is null)
            {
                return new(null, Problem(StatusCodes.Status503ServiceUnavailable, "ASPUSH108", "The authorization policy is unavailable."));
            }

            ClaimsPrincipal? principal;
            if (authenticationScheme is null)
            {
                if (policy.AuthenticationSchemes.Count != 1)
                {
                    return new(null, Problem(StatusCodes.Status503ServiceUnavailable, "ASPUSH108", "The cookie authorization policy must declare exactly one authentication scheme."));
                }

                var evaluator = context.RequestServices.GetService<IPolicyEvaluator>();
                if (evaluator is null)
                {
                    return new(null, Problem(StatusCodes.Status503ServiceUnavailable, "ASPUSH108", "Authentication services are unavailable."));
                }

                var authentication = await evaluator.AuthenticateAsync(policy, context).ConfigureAwait(false);
                principal = authentication.Succeeded ? authentication.Principal : null;
            }
            else
            {
                var schemeProvider = context.RequestServices.GetService<IAuthenticationSchemeProvider>();
                var scheme = schemeProvider is null
                    ? null
                    : await schemeProvider.GetSchemeAsync(authenticationScheme).ConfigureAwait(false);
                if (scheme is null || typeof(IAuthenticationSignInHandler).IsAssignableFrom(scheme.HandlerType))
                {
                    return new(null, Problem(StatusCodes.Status503ServiceUnavailable, "ASPUSH108", "The token authentication scheme is unavailable."));
                }

                var authentication = await context.AuthenticateAsync(authenticationScheme).ConfigureAwait(false);
                principal = authentication.Succeeded ? authentication.Principal : null;
            }

            if (principal?.Identity?.IsAuthenticated != true)
            {
                return new(null, Results.Unauthorized());
            }

            if (authenticationScheme is not null)
            {
                // Resource-aware handlers must observe the same explicit bearer principal
                // that is evaluated below, never an ambient cookie identity.
                context.User = principal;
            }

            var result = await authorization.AuthorizeAsync(principal, context, policy).ConfigureAwait(false);
            return result.Succeeded
                ? new(principal, null)
                : new(null, Results.Forbid(
                    authenticationSchemes: authenticationScheme is null
                        ? policy.AuthenticationSchemes.ToArray()
                        : [authenticationScheme]));
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(null, Problem(StatusCodes.Status503ServiceUnavailable, "ASPUSH108", "Authentication or authorization is unavailable."));
        }
    }

    private static async ValueTask<IResult?> ValidateWriteProtectionAsync(
        HttpContext context,
        string? authenticationScheme)
    {
        if (authenticationScheme is not null)
        {
            return null;
        }

        try
        {
            await context.RequestServices.GetRequiredService<IAntiforgery>()
                .ValidateRequestAsync(context).ConfigureAwait(false);
            return null;
        }
        catch (AntiforgeryValidationException)
        {
            return Problem(StatusCodes.Status400BadRequest, "ASPUSH104", "Antiforgery validation failed.");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "ASPUSH104", "Antiforgery protection is unavailable.");
        }
    }

    private static async ValueTask<JsonBodyResult> ReadJsonBodyAsync(HttpContext context)
    {
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType)
            || !string.Equals(contentType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonBodyFailure(
                Problem(StatusCodes.Status415UnsupportedMediaType, "ASPUSH102", "Content-Type must be application/json."));
        }

        if (context.Request.ContentLength > MaximumBodyBytes)
        {
            return new JsonBodyFailure(
                Problem(StatusCodes.Status413PayloadTooLarge, "ASPUSH103", "The request body exceeds 16 KiB."));
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var count = await context.Request.Body.ReadAsync(chunk, context.RequestAborted).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            if (buffer.Length + count > MaximumBodyBytes)
            {
                return new JsonBodyFailure(
                    Problem(StatusCodes.Status413PayloadTooLarge, "ASPUSH103", "The request body exceeds 16 KiB."));
            }

            buffer.Write(chunk, 0, count);
        }

        try
        {
            return new JsonBodySuccess(JsonDocument.Parse(buffer.ToArray()));
        }
        catch (JsonException)
        {
            return new JsonBodyFailure(
                Problem(StatusCodes.Status400BadRequest, "ASPUSH100", "The JSON body is invalid."));
        }
    }

    private static bool HasExactProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        return actual.Length == names.Length && names.All(name => actual.Contains(name, StringComparer.Ordinal));
    }

    private static IResult Problem(int statusCode, string code, string title) =>
        Results.Problem(
            statusCode: statusCode,
            title: title,
            type: $"https://appsurface.dev/problems/{code.ToLowerInvariant()}",
            extensions: new Dictionary<string, object?> { ["code"] = code });

    private static void ValidateBasePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path != path.Trim()
            || path.Length > 1024
            || path == "/"
            || path.Equals("/_appsurface", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/_appsurface/", StringComparison.OrdinalIgnoreCase)
            || !path.StartsWith('/')
            || path.StartsWith("//", StringComparison.Ordinal)
            || path.EndsWith('/')
            || path.Contains('%')
            || path.Contains('?')
            || path.Contains('#')
            || path.Contains('\\')
            || path.Contains('{')
            || path.Contains('}')
            || path.Contains('*')
            || path.Split('/').Any(segment => segment is "." or "..")
            || path.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException("The Web Push base path must be a literal app-root-relative endpoint path outside AppSurface's reserved route space and without route parameters, traversal segments, escapes, query, or fragment.", nameof(path));
        }
    }

    private sealed record AuthorizationResult(ClaimsPrincipal? Principal, IResult? Failure);
    private abstract record JsonBodyResult;
    private sealed record JsonBodySuccess(JsonDocument Document) : JsonBodyResult;
    private sealed record JsonBodyFailure(IResult Result) : JsonBodyResult;
}

/// <summary>Coordinates route claims across repeated package mapping calls in one application.</summary>
/// <remarks>Claims are synchronized and compared case-insensitively to match ASP.NET Core route behavior.</remarks>
internal sealed class AppSurfaceWebPushRouteRegistry
{
    private readonly object gate = new();
    private readonly HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
    private bool assetMapped;

    /// <summary>Claims one base path and reports whether this caller owns the one-time client asset mapping.</summary>
    /// <param name="path">The previously validated literal base path.</param>
    /// <returns><see langword="true"/> only for the first successful claim in the application.</returns>
    /// <exception cref="InvalidOperationException">The path was already claimed, including with different casing.</exception>
    public bool Claim(string path)
    {
        lock (gate)
        {
            if (!paths.Add(path))
            {
                throw new InvalidOperationException($"The AppSurface Web Push route '{path}' is already mapped.");
            }

            var shouldMapAsset = !assetMapped;
            assetMapped = true;
            return shouldMapAsset;
        }
    }
}
