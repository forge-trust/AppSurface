using System.Collections.Immutable;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Performs bounded, multi-signal readiness checks for the AppSurface local Keycloak proof.
/// </summary>
public sealed class AppSurfaceKeycloakReadinessProbe
{
    private static readonly HttpClient DefaultHttpClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = AppSurfaceKeycloakDefaults.ReadinessTimeout,
    };

    private readonly HttpClient _httpClient;
    private readonly string? _expectedLoginThemeName;
    private readonly IReadOnlyList<string> _expectedSeededUserNames;
    private readonly AppSurfaceKeycloakOptions _options;
    private readonly AppSurfaceKeycloakConfigurationProjection? _registrationProjection;
    private readonly Func<string, string> _readAllText;

    /// <summary>
    /// Creates a readiness probe for validated Keycloak proof options.
    /// </summary>
    /// <param name="options">The proof options to verify.</param>
    /// <param name="httpClient">Optional HTTP client, primarily for tests.</param>
    /// <remarks>
    /// The default client uses the bounded readiness timeout and disables automatic redirects so authorization
    /// challenge checks can inspect Keycloak's raw response. Injected clients should preserve those behaviors.
    /// </remarks>
    public AppSurfaceKeycloakReadinessProbe(AppSurfaceKeycloakOptions options, HttpClient? httpClient = null)
        : this(options, httpClient ?? DefaultHttpClient, File.ReadAllText, loginThemeName: null, seededUserNames: null)
    {
    }

    /// <summary>Creates a probe that uses the validated registration projection without re-reading the theme tree.</summary>
    internal AppSurfaceKeycloakReadinessProbe(AppSurfaceKeycloakRegistrationSnapshot registration)
        : this(registration.Options, DefaultHttpClient, File.ReadAllText, registrationProjection: registration.Projection)
    {
    }

    /// <summary>
    /// Creates a readiness probe with injectable HTTP and realm-file readers for deterministic tests.
    /// </summary>
    /// <param name="options">The proof options to verify.</param>
    /// <param name="httpClient">HTTP client used for metadata and authorization requests.</param>
    /// <param name="readAllText">Realm import file reader.</param>
    /// <param name="loginThemeName">Optional safe theme name expected in generated realm evidence.</param>
    /// <param name="seededUserNames">Optional safe user names expected in generated realm evidence.</param>
    /// <param name="registrationProjection">Projection from the same already validated registration options.</param>
    internal AppSurfaceKeycloakReadinessProbe(
        AppSurfaceKeycloakOptions options,
        HttpClient httpClient,
        Func<string, string> readAllText,
        string? loginThemeName = null,
        IReadOnlyList<string>? seededUserNames = null,
        AppSurfaceKeycloakConfigurationProjection? registrationProjection = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(readAllText);
        if (registrationProjection is null) options.Validate();
        _options = options;
        _registrationProjection = registrationProjection;
        _httpClient = httpClient;
        _readAllText = readAllText;
        _expectedLoginThemeName = loginThemeName ?? options.LoginTheme?.Name;
        _expectedSeededUserNames = seededUserNames ?? options.SeededUsers.Select(user => user.Username).ToArray();
    }

    /// <summary>
    /// Checks metadata, generated realm evidence, and authorization challenge evidence once.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for HTTP work.</param>
    /// <returns>A successful readiness result.</returns>
    public async Task<AppSurfaceKeycloakReadinessResult> CheckOnceAsync(CancellationToken cancellationToken = default)
    {
        var projection = _registrationProjection ?? _options.CreateConfigurationProjection();
        var desired = new RealmBaselineDesired(
            projection.Authority,
            projection.ClientId,
            _options.Realm,
            _options.RedirectUris.Select(uri => uri.ToString()).ToImmutableArray(),
            _options.PostLogoutRedirectUris.Select(uri => uri.ToString()).ToImmutableArray(),
            _expectedSeededUserNames.ToImmutableArray(),
            _options.RealmImportDirectory,
            _expectedLoginThemeName);

        try
        {
            await RealmBaselineVerifier
                .CheckOnceAsync(_httpClient, desired, _readAllText, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RealmBaselineException ex)
        {
            throw new AppSurfaceKeycloakException(ex.Code, ex.SafeMessage);
        }

        return new AppSurfaceKeycloakReadinessResult(desired.Authority, desired.ClientId, desired.Realm);
    }
}
