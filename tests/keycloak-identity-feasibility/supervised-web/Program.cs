using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Auth.AspNetCore.Oidc;
using ForgeTrust.AppSurface.Auth.Keycloak;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

if (args.Length != 1) return 64;

var stage = "configuration";
try
{
    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    var configPath = Path.GetFullPath(args[0]);
    var config = JsonSerializer.Deserialize<PublicConfiguration>(await File.ReadAllBytesAsync(configPath), jsonOptions)
        ?? throw new InvalidDataException();

    var authority = Required(config.Authority);
    var clientId = Required(config.ClientId);
    var realm = Required(config.Realm);
    var expectedRunId = Required(config.ExpectedRunId);
    var keycloakHost = Required(config.KeycloakHost);
    var keycloakPort = config.KeycloakPort;
    var certificatePath = Required(config.CertificatePath);
    var fixture = (realm, clientId) switch
    {
        ("sample", "sample-web") => "sample",
        ("appsurface-dev", "appsurface-web") => "appsurface",
        ("skoolit", "skoolit-web") => "skoolit",
        _ => throw new InvalidDataException()
    };
    var expectedClientMode = fixture == "appsurface" ? "Public" : "Confidential";
    var clientMode = config.ClientMode ?? (fixture == "sample" ? "Confidential" : null);
    const string skoolitApplicationSubject = "skoolit-proof-app-user";
    var expectedAppSubject = fixture == "skoolit" ? skoolitApplicationSubject : null;
    var webBaseAddress = Required(config.WebBaseAddress);
    var publicWeb = config.PublicWebOrigin;
    var listenUrl = Required(config.ListenUrl);
    var seedReceiptPath = Required(config.SeedReceiptPath);
    var expectedCallbackPath = fixture == "appsurface" ? "/signin-appsurface-oidc" : "/signin-oidc";
    var expectedSignedOutCallbackPath = fixture == "appsurface"
        ? "/signout-callback-appsurface-oidc"
        : "/signout-callback-oidc";
    var callbackPath = config.CallbackPath ?? expectedCallbackPath;
    var signedOutCallbackPath = config.SignedOutCallbackPath ?? expectedSignedOutCallbackPath;

    if (!Guid.TryParseExact(expectedRunId, "N", out _)
        || keycloakHost != "localhost"
        || keycloakPort is < 1024 or > 65535
        || !IsAuthority(authority, keycloakPort, realm)
        || !IsLoopbackWebAddress(webBaseAddress, keycloakPort)
        || !IsListenAddress(listenUrl, webBaseAddress)
        || (publicWeb is null
            ? !IsLocalAuthority(authority, keycloakPort, realm)
            : publicWeb != "https://app-dev.getskoolit.com" || !IsPublicAuthority(authority, realm))
        || !IsSafeCallbackPath(callbackPath) || !IsSafeCallbackPath(signedOutCallbackPath)
        || clientMode != expectedClientMode
        || clientMode == "Public" && config.ReferencePath is not null
        || clientMode == "Confidential" && string.IsNullOrWhiteSpace(config.ReferencePath)
        || callbackPath != expectedCallbackPath || signedOutCallbackPath != expectedSignedOutCallbackPath
        || string.Equals(callbackPath, signedOutCallbackPath, StringComparison.Ordinal))
        throw new InvalidDataException();

    stage = "required-worker-and-seed";
    var seed = JsonSerializer.Deserialize<SeedReceipt>(await File.ReadAllBytesAsync(seedReceiptPath), jsonOptions)
        ?? throw new InvalidDataException();
    if (!seed.CreatedOrPreserved || string.IsNullOrWhiteSpace(seed.ExpectedSubject)
        || seed.ExpectedRunId != expectedRunId || seed.Username != "seeded-user" || seed.ClientId != clientId
        || seed.Realm != realm || seed.Issuer != authority || seed.ExpectedAppSubject != expectedAppSubject)
        throw new InvalidDataException();

    string? clientSecret = null;
    if (clientMode == "Confidential")
    {
        stage = "credential-reference";
        var referenceBytes = await File.ReadAllBytesAsync(Required(config.ReferencePath));
        var reference = JsonSerializer.Deserialize<KeycloakClientCredentialReference>(referenceBytes, jsonOptions)
            ?? throw new InvalidDataException();
        if (reference.RunId != expectedRunId || reference.ClientId != clientId
            || !string.Equals(reference.Authority.AbsoluteUri, authority, StringComparison.Ordinal))
            throw new InvalidDataException();
        clientSecret = reference.Read();
    }

    stage = "certificate-pin";
    using var expectedCertificate = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(certificatePath));
    var certificatePin = expectedCertificate.GetCertHash(HashAlgorithmName.SHA256);
    var authorityUri = new Uri(authority, UriKind.Absolute);
    var webUri = new Uri(webBaseAddress, UriKind.Absolute);

    stage = "oidc-registration";
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = [],
        ContentRootPath = Path.GetDirectoryName(configPath)!,
        EnvironmentName = "Production"
    });
    stage = "oidc-services";
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls(listenUrl);
    builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = AppSurfaceOidcAuthOptions.DefaultCookieScheme;
        options.DefaultChallengeScheme = AppSurfaceOidcAuthOptions.DefaultOidcScheme;
        options.DefaultSignOutScheme = AppSurfaceOidcAuthOptions.DefaultOidcScheme;
    });
    stage = "appsurface-oidc-services";
    builder.Services.AddAppSurfaceOidcAuth(options =>
    {
        options.CallbackPath = callbackPath;
        options.SignedOutCallbackPath = signedOutCallbackPath;
        options.RequireClientSecret = clientMode == "Confidential";
        options.ConfigureCookie(cookie =>
        {
            cookie.Cookie.SameSite = SameSiteMode.Lax;
            cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });
        options.ConfigureOpenIdConnect(oidc =>
        {
            oidc.Authority = authority;
            oidc.ClientId = clientId;
            oidc.ClientSecret = clientSecret;
            oidc.RequireHttpsMetadata = true;
            oidc.ResponseMode = OpenIdConnectResponseMode.Query;
            oidc.UsePkce = true;
            oidc.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
            oidc.MapInboundClaims = false;
            oidc.SaveTokens = false;
            oidc.TokenValidationParameters.NameClaimType = "name";
            oidc.TokenValidationParameters.RoleClaimType = "roles";
            oidc.CorrelationCookie.SameSite = SameSiteMode.Lax;
            oidc.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            oidc.NonceCookie.SameSite = SameSiteMode.Lax;
            oidc.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            oidc.Backchannel = new HttpClient(new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                ConnectCallback = async (connection, cancellationToken) =>
                {
                    if (!string.Equals(connection.DnsEndPoint.Host, authorityUri.Host, StringComparison.OrdinalIgnoreCase)
                        || connection.DnsEndPoint.Port != authorityUri.Port)
                        throw new IOException("OIDC backchannel target rejected.");

                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(IPAddress.Loopback, keycloakPort, cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, certificate, _, errors) => certificate is X509Certificate2 leaf
                        && (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0
                        && DateTime.UtcNow >= leaf.NotBefore.ToUniversalTime()
                        && DateTime.UtcNow <= leaf.NotAfter.ToUniversalTime()
                        && CryptographicOperations.FixedTimeEquals(
                            certificate.GetCertHash(HashAlgorithmName.SHA256), certificatePin)
                }
            })
            {
                Timeout = TimeSpan.FromSeconds(8),
                MaxResponseContentBufferSize = 1024 * 1024
            };
            oidc.Events.OnRedirectToIdentityProviderForSignOut = context =>
            {
                context.ProtocolMessage.ClientId = clientId;
                return Task.CompletedTask;
            };
            oidc.Events.OnTokenValidated = context =>
            {
                var validatedIssuer = context.SecurityToken.Issuer;
                var validatedSubject = context.Principal?.FindFirst("sub")?.Value;
                if (!string.Equals(validatedIssuer, seed.Issuer, StringComparison.Ordinal)
                    || !string.Equals(validatedSubject, seed.ExpectedSubject, StringComparison.Ordinal)
                    || context.Properties is null
                    || context.Principal?.Identity is not ClaimsIdentity identity)
                    throw new InvalidDataException();

                context.Properties.Items["supervised.validated-issuer"] = validatedIssuer;
                context.Properties.Items["supervised.validated-subject"] = validatedSubject;
                foreach (var role in ExtractRoles(context.Principal))
                {
                    if (!identity.HasClaim("roles", role)) identity.AddClaim(new Claim("roles", role));
                }
                return Task.CompletedTask;
            };
            oidc.Events.OnRemoteFailure = async context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("OIDC sign-in failed.");
            };
        });
    });
    builder.Services.AddAuthorization();

    stage = "handler-validation";
    var app = builder.Build();
    var schemes = await app.Services.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync();
    if (schemes.Count() != 2
        || !schemes.Any(scheme => scheme.Name == AppSurfaceOidcAuthOptions.DefaultCookieScheme
            && scheme.HandlerType == typeof(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationHandler))
        || !schemes.Any(scheme => scheme.Name == AppSurfaceOidcAuthOptions.DefaultOidcScheme
            && scheme.HandlerType == typeof(OpenIdConnectHandler)))
        throw new InvalidDataException();

    app.Use(async (context, next) =>
    {
        try { await next(context); }
        catch
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsync("Request failed.");
            }
        }
    });
    if (publicWeb is not null)
    {
        var appHost = new Uri(publicWeb).Host;
        // The copied browser fixture terminates HTTPS on a task-owned loopback port and sets the exact host.
        // The HTTP /state probe remains available on the separate loopback transport.
        app.Use(async (context, next) =>
        {
            if (context.Request.Host == new HostString(appHost)) context.Request.Scheme = Uri.UriSchemeHttps;
            else if (context.Request.Path != "/state")
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            await next(context);
        });
    }
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapGet("/state", async (HttpContext context) =>
    {
        var ticket = await context.AuthenticateAsync(AppSurfaceOidcAuthOptions.DefaultCookieScheme);
        var identityMatchesSeed = IsValidatedSeedIdentity(ticket, context.User, seed);
        var applicationSubjectMatched = expectedAppSubject is not null && identityMatchesSeed
            && ProjectedApplicationSubject(context.User) == expectedAppSubject;
        return Results.Json(ReadState(context.User, applicationSubjectMatched));
    });
    app.MapGet("/favicon.ico", () => Results.NoContent());
    app.MapGet("/", (HttpContext context) => Results.Content(RenderHome(ReadState(context.User)), "text/html; charset=utf-8"));
    app.MapGet("/login", () => Results.Challenge(
        new AuthenticationProperties { RedirectUri = "/proof" }, [AppSurfaceOidcAuthOptions.DefaultOidcScheme]));
    app.MapGet("/logout", () => Results.SignOut(
        new AuthenticationProperties { RedirectUri = "/" },
        [AppSurfaceOidcAuthOptions.DefaultCookieScheme, AppSurfaceOidcAuthOptions.DefaultOidcScheme]));
    app.MapGet("/proof", async (HttpContext context) =>
    {
        var ticket = await context.AuthenticateAsync(AppSurfaceOidcAuthOptions.DefaultCookieScheme);
        if (!IsValidatedSeedIdentity(ticket, context.User, seed))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var applicationSubjectMatched = expectedAppSubject is not null
            && ProjectedApplicationSubject(context.User) == expectedAppSubject;
        if (expectedAppSubject is not null && !applicationSubjectMatched)
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var state = ReadState(context.User, applicationSubjectMatched);
        return Results.Content(RenderProof(state), "text/html; charset=utf-8");
    }).RequireAuthorization();

    using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal =>
    {
        signal.Cancel = true;
        app.Lifetime.StopApplication();
    });
    using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, signal =>
    {
        signal.Cancel = true;
        app.Lifetime.StopApplication();
    });

    stage = "web-run";
    await app.RunAsync();
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "failed", stage, errorType = error.GetType().Name }));
    return 2;
}

static string Required(string? value) => string.IsNullOrWhiteSpace(value) ? throw new InvalidDataException() : value;

static bool IsAuthority(string authority, int port, string realm)
{
    if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri)) return false;
    return IsLocalAuthority(authority, port, realm) || IsPublicAuthority(authority, realm);
}

static bool IsLocalAuthority(string authority, int port, string realm)
{
    if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri)) return false;
    return uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host == "localhost" || uri.Host == "127.0.0.1") && uri.Port == port
        && uri.AbsolutePath == "/realms/" + realm && uri.UserInfo.Length == 0
        && uri.Query.Length == 0 && uri.Fragment.Length == 0;
}

static bool IsPublicAuthority(string authority, string realm)
{
    if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri)) return false;
    return uri.Scheme == Uri.UriSchemeHttps && uri.Host == "auth-dev.getskoolit.com" && uri.IsDefaultPort
        && (realm is "appsurface-dev" or "skoolit")
        && uri.AbsolutePath == "/realms/" + realm && uri.UserInfo.Length == 0
        && uri.Query.Length == 0 && uri.Fragment.Length == 0;
}

static bool IsLoopbackWebAddress(string address, int? keycloakPort)
{
    if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return false;
    return uri.Scheme == Uri.UriSchemeHttp && uri.Host == "localhost" && uri.Port is >= 1024 and <= 65535
        && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0
        && (!keycloakPort.HasValue || uri.Port != keycloakPort.Value);
}

static bool IsListenAddress(string listenUrl, string webBaseAddress)
{
    if (!Uri.TryCreate(listenUrl, UriKind.Absolute, out var listen)
        || !Uri.TryCreate(webBaseAddress, UriKind.Absolute, out var web)) return false;
    return listen.Scheme == Uri.UriSchemeHttp && listen.Host == "0.0.0.0" && listen.Port == web.Port
        && listen.AbsolutePath == "/" && listen.Query.Length == 0 && listen.Fragment.Length == 0;
}

static bool IsSafeCallbackPath(string path) => path.Length is > 1 and <= 128
    && path[0] == '/' && path[1] != '/' && !path.Contains('\\') && !path.Contains('?') && !path.Contains('#')
    && !path.Any(char.IsControl) && Uri.TryCreate(path, UriKind.Relative, out _);

static bool IsValidatedSeedIdentity(AuthenticateResult ticket, ClaimsPrincipal principal, SeedReceipt seed)
{
    var properties = ticket.Properties;
    return ticket.Succeeded && properties is not null
        && properties.Items.TryGetValue("supervised.validated-issuer", out var issuer)
        && string.Equals(issuer, seed.Issuer, StringComparison.Ordinal)
        && properties.Items.TryGetValue("supervised.validated-subject", out var subject)
        && string.Equals(subject, seed.ExpectedSubject, StringComparison.Ordinal)
        && string.Equals(principal.FindFirst("sub")?.Value, seed.ExpectedSubject, StringComparison.Ordinal);
}

static string? ProjectedApplicationSubject(ClaimsPrincipal principal)
{
    var literal = principal.FindAll("appsurface.subject_id").ToArray();
    var nested = principal.FindAll("appsurface").ToArray();
    if (literal.Length > 1 || nested.Length > 1 || literal.Length != 0 && nested.Length != 0) return null;
    if (literal.Length == 1) return literal[0].Value;
    if (nested.Length != 1) return null;
    try
    {
        using var json = JsonDocument.Parse(nested[0].Value);
        return json.RootElement.TryGetProperty("subject_id", out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
    catch (JsonException) { return null; }
}

static WebState ReadState(ClaimsPrincipal principal, bool applicationSubjectMatched = false)
{
    if (principal.Identity?.IsAuthenticated != true) return new(false, null, [], null, false);
    var roles = ExtractRoles(principal).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    var name = principal.FindFirst("name")?.Value
        ?? principal.FindFirst("preferred_username")?.Value
        ?? principal.Identity.Name;
    return new(true, principal.FindFirst("sub")?.Value, roles, name, applicationSubjectMatched);
}

static string[] ExtractRoles(ClaimsPrincipal principal)
{
    var roles = new List<string>();
    foreach (var claim in principal.Claims)
    {
        if (claim.Type is "roles" or "role" or "fixture_role" or ClaimTypes.Role && !string.IsNullOrWhiteSpace(claim.Value))
            roles.Add(claim.Value);
        if (claim.Type != "realm_access") continue;
        try
        {
            using var document = JsonDocument.Parse(claim.Value);
            if (document.RootElement.TryGetProperty("roles", out var roleArray) && roleArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var role in roleArray.EnumerateArray())
                    if (role.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(role.GetString()))
                        roles.Add(role.GetString()!);
            }
        }
        catch (JsonException) { }
    }
    return roles.ToArray();
}

static string RenderHome(WebState state)
{
    var status = state.Authenticated ? "true" : "false";
    var action = state.Authenticated ? "<a data-testid=logout href=/logout>Sign out</a>" : "<a data-testid=login href=/login>Sign in</a>";
    var name = state.Name is null ? "" : "<p>Name: <span data-testid=name>" + HtmlEncoder.Default.Encode(state.Name) + "</span></p>";
    return "<!doctype html><html lang=en><meta charset=utf-8><title>Supervised identity fixture</title>"
        + "<main><h1>Supervised identity fixture</h1><p>Authenticated: <strong data-testid=authenticated>" + status
        + "</strong></p>" + name + action + "</main></html>";
}

static string RenderProof(WebState state)
{
    var roles = string.Join(", ", state.Roles.Select(HtmlEncoder.Default.Encode));
    return "<!doctype html><html lang=en><meta charset=utf-8><title>Signed in</title><main>"
        + "<h1>Signed in</h1><p>Authenticated: <strong data-testid=authenticated>true</strong></p>"
        + "<p>Subject matched: <strong data-testid=subject-matched>true</strong></p>"
        + "<p>Application subject matched: <strong data-testid=application-subject-matched>"
        + state.ApplicationSubjectMatched.ToString().ToLowerInvariant() + "</strong></p>"
        + "<p>Roles: <strong data-testid=roles>" + roles + "</strong></p>"
        + "<a data-testid=logout href=/logout>Sign out</a></main></html>";
}

sealed class PublicConfiguration
{
    public string? Authority { get; init; }
    public string? ClientId { get; init; }
    public string? Realm { get; init; }
    public string? ExpectedRunId { get; init; }
    public string? KeycloakHost { get; init; }
    public int KeycloakPort { get; init; }
    public string? CertificatePath { get; init; }
    public string? ClientMode { get; init; }
    public string? ReferencePath { get; init; }
    public string? WebBaseAddress { get; init; }
    public string? PublicWebOrigin { get; init; }
    public string? ListenUrl { get; init; }
    public string? SeedReceiptPath { get; init; }
    public string? CallbackPath { get; init; }
    public string? SignedOutCallbackPath { get; init; }
}

sealed class SeedReceipt
{
    [JsonPropertyName("createdOrPreserved")] public bool CreatedOrPreserved { get; init; }
    [JsonPropertyName("expectedSubject")] public string? ExpectedSubject { get; init; }
    [JsonPropertyName("code")] public string? Code { get; init; }
    [JsonPropertyName("expectedRunId")] public string? ExpectedRunId { get; init; }
    [JsonPropertyName("username")] public string? Username { get; init; }
    [JsonPropertyName("clientId")] public string? ClientId { get; init; }
    [JsonPropertyName("realm")] public string? Realm { get; init; }
    [JsonPropertyName("issuer")] public string? Issuer { get; init; }
    [JsonPropertyName("expectedAppSubject")] public string? ExpectedAppSubject { get; init; }
}

sealed record WebState(bool Authenticated, string? Subject, string[] Roles, string? Name, bool ApplicationSubjectMatched);
