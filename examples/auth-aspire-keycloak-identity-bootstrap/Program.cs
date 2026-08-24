using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AuthAspireKeycloakLocalSeedStore;

namespace AuthAspireKeycloakIdentityBootstrap;

/// <summary>
/// Runs one bounded local identity bootstrap operation.
/// </summary>
public static class Program
{
    /// <summary>
    /// Obtains a master token, converges the local broker alias, and writes the deterministic subject mapping.
    /// </summary>
    /// <returns>Zero on success and a nonzero process code for every failure.</returns>
    public static async Task<int> Main()
    {
        try
        {
            var configuration = IdentityBootstrapConfiguration.Read(Environment.GetEnvironmentVariable);
            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
            var token = await KeycloakAdminClient.GetMasterTokenAsync(httpClient, configuration).ConfigureAwait(false);
            await KeycloakAdminClient.UpsertBrokerAliasAsync(httpClient, configuration, token).ConfigureAwait(false);

            var store = new LocalSeedStore(configuration.StorePath);
            store.UpsertBrokerAlias("local-broker", configuration.Authority.ToString(), configuration.PublicClientId);
            store.UpsertIdentitySubjectMap("founder", "subject-founder-001");
            var snapshot = store.ReadSnapshot();
            if (snapshot.BrokerAliases.Count != 1
                || !snapshot.BrokerAliases.Any(alias => alias.Alias == "local-broker")
                || snapshot.IdentitySubjectMaps.Count != 1
                || snapshot.IdentitySubjectMaps[0] != new IdentitySubjectMapRecord("founder", "subject-founder-001"))
            {
                return 1;
            }

            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }
}

internal sealed record IdentityBootstrapConfiguration(
    Uri Authority,
    string RealmName,
    string PublicClientId,
    string AdminUsername,
    string AdminPassword,
    string StorePath)
{
    internal static IdentityBootstrapConfiguration Read(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        return new(
            ParseAuthority(Required(getEnvironmentVariable, "APPSURFACE_KEYCLOAK_LOCAL_SEED_AUTHORITY")),
            Required(getEnvironmentVariable, "APPSURFACE_KEYCLOAK_LOCAL_SEED_REALM_NAME"),
            Required(getEnvironmentVariable, "APPSURFACE_KEYCLOAK_LOCAL_SEED_PUBLIC_CLIENT_ID"),
            Required(getEnvironmentVariable, "LOCAL_SEED_ADMIN_USERNAME"),
            Required(getEnvironmentVariable, "LOCAL_SEED_ADMIN_PASSWORD"),
            Required(getEnvironmentVariable, "LOCAL_SEED_STORE_PATH"));
    }

    private static Uri ParseAuthority(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var authority)
            || (authority.Scheme != Uri.UriSchemeHttp && authority.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(authority.UserInfo)
            || !string.IsNullOrEmpty(authority.Query)
            || !string.IsNullOrEmpty(authority.Fragment))
        {
            throw new InvalidDataException("The local seed authority is invalid.");
        }

        return authority;
    }

    private static string Required(Func<string, string?> getEnvironmentVariable, string name) =>
        string.IsNullOrWhiteSpace(getEnvironmentVariable(name))
            ? throw new InvalidDataException("A required local seed value is missing.")
            : getEnvironmentVariable(name)!;
}

internal static class KeycloakAdminClient
{
    internal static async Task<string> GetMasterTokenAsync(
        HttpClient httpClient,
        IdentityBootstrapConfiguration configuration)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "admin-cli",
            ["username"] = configuration.AdminUsername,
            ["password"] = configuration.AdminPassword,
        });
        using var response = await httpClient.PostAsync(
            BuildMasterTokenUri(configuration.Authority),
            content).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("The Keycloak administrator token request failed.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("access_token", out var token)
            || token.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(token.GetString()))
        {
            throw new InvalidDataException("The Keycloak administrator token response was invalid.");
        }

        return token.GetString()!;
    }

    internal static async Task UpsertBrokerAliasAsync(
        HttpClient httpClient,
        IdentityBootstrapConfiguration configuration,
        string token)
    {
        var endpoint = BuildBrokerEndpoint(configuration);
        using var get = new HttpRequestMessage(HttpMethod.Get, endpoint);
        get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var getResponse = await httpClient.SendAsync(get).ConfigureAwait(false);
        if (getResponse.StatusCode != HttpStatusCode.NotFound && !getResponse.IsSuccessStatusCode)
        {
            throw new HttpRequestException("The Keycloak broker lookup failed.");
        }

        var payload = JsonSerializer.Serialize(new
        {
            alias = "local-broker",
            displayName = "Local broker",
            providerId = "oidc",
            enabled = true,
            trustEmail = true,
            storeToken = false,
            config = new Dictionary<string, string>
            {
                ["issuer"] = configuration.Authority.ToString().TrimEnd('/'),
                ["authorizationUrl"] = BuildRealmEndpoint(configuration.Authority, "protocol/openid-connect/auth").ToString(),
                ["tokenUrl"] = BuildRealmEndpoint(configuration.Authority, "protocol/openid-connect/token").ToString(),
                ["jwksUrl"] = BuildRealmEndpoint(configuration.Authority, "protocol/openid-connect/certs").ToString(),
                ["clientId"] = configuration.PublicClientId,
            },
        });

        using var request = new HttpRequestMessage(
            getResponse.StatusCode == HttpStatusCode.NotFound ? HttpMethod.Post : HttpMethod.Put,
            getResponse.StatusCode == HttpStatusCode.NotFound
                ? new Uri(configuration.Authority, "../admin/realms/" + Uri.EscapeDataString(configuration.RealmName) + "/identity-provider/instances")
                : endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("The Keycloak broker upsert failed.");
        }
    }

    private static Uri BuildMasterTokenUri(Uri authority)
    {
        return new Uri(
            new Uri(authority.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute),
            "realms/master/protocol/openid-connect/token");
    }

    private static Uri BuildBrokerEndpoint(IdentityBootstrapConfiguration configuration)
    {
        var root = new Uri(configuration.Authority.GetLeftPart(UriPartial.Authority));
        return new Uri(root, "admin/realms/" + Uri.EscapeDataString(configuration.RealmName)
            + "/identity-provider/instances/local-broker");
    }

    private static Uri BuildRealmEndpoint(Uri authority, string suffix) =>
        new(new Uri(authority.ToString().TrimEnd('/') + "/", UriKind.Absolute), suffix);
}
