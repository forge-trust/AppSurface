using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ForgeTrust.AppSurface.Auth.Keycloak;

// Internal coordinated-worker experiment. These names do not freeze the proposed public identity API.
internal sealed record RealmReconcileMapper(string Name, string Claim, string Value, string? UserAttribute = null, string? SessionNote = null);
internal sealed record RealmReconcileBroker(string Alias, bool Enabled, string ProviderId = "oidc", string? ClientId = null)
{
    public string? DisplayName { get; init; }
}
internal sealed record RealmReconcileDesired(string Realm, string ClientId, string Authority, bool Confidential,
    bool AllowPublicDowngrade, ImmutableArray<string> RedirectUris, ImmutableArray<string> PostLogoutRedirectUris,
    ImmutableArray<string> WebOrigins, ImmutableArray<RealmReconcileMapper> Mappers,
    ImmutableArray<RealmReconcileBroker> Brokers, ImmutableArray<string> RemoveMappers, ImmutableArray<string> RemoveBrokers)
{
    public string? TransportOrigin { get; init; }
    public ImmutableArray<string> ApprovedHttpsOrigins { get; init; } = [];
    public string? LoginThemeName { get; init; }
}
internal sealed record RealmReconcileCredentials(string AdminUsername, string AdminPassword, string? ClientSecret)
{
    public ImmutableDictionary<string, string>? BrokerSecrets { get; init; }
}
internal sealed record RealmReconcileResult(bool Ready, string Code, string Stage, int Writes, int Retries,
    int UnknownWrites, int Readbacks, string? ManagedDigest, string? RealmId, string? ClientInternalId,
    string Effect, string Recovery)
{
    public string ProviderAssurance { get; init; } = "NotRequested";
}

/// <summary>Internal finite generic reconciliation/readback core shared only with its coordinated worker.</summary>
/// <remarks>Owns declared generic fields, never application users, volume ownership or downstream readiness.</remarks>
internal static class RealmReconciler
{
    private const int MaximumDeclarationBytes = 1024 * 1024;

    /// <summary>Validates the immutable nonsecret declaration without transport or resolving credentials.</summary>
    internal static void ValidateDesired(RealmReconcileDesired desired)
    {
        try
        {
            _ = ValidateDeclaration(desired);
        }
        catch (Exception) { throw new ArgumentException("Invalid generic realm declaration."); }
    }

    /// <summary>Validates declaration-only semantics and returns the permitted HTTPS loopback transport origin.</summary>
    private static Uri ValidateDeclaration(RealmReconcileDesired desired)
    {
        if (desired.ApprovedHttpsOrigins.IsDefault
            || !Id(desired.Realm) || !Id(desired.ClientId) || !Uri.TryCreate(desired.Authority, UriKind.Absolute, out var authority)
            || !ValidAuthority(authority, desired.Realm, desired.ApprovedHttpsOrigins)
            || !TryOrigin(desired.TransportOrigin ?? authority.GetLeftPart(UriPartial.Authority), out var expectedTransport)
            || !ValidOrigin(expectedTransport, true)
            || authority.IsLoopback && !SameOrigin(authority, expectedTransport)
            || desired.ApprovedHttpsOrigins.Length > 32
            || desired.ApprovedHttpsOrigins.Any(x => !TryOrigin(x, out var approved) || approved.Scheme != "https" || approved.IsLoopback || approved.Port != 443)
            || desired.LoginThemeName is { } theme && !Id(theme)
            || !Values(desired.RedirectUris, 32, Endpoint) || !Values(desired.PostLogoutRedirectUris, 32, Endpoint)
            || desired.RedirectUris.Concat(desired.PostLogoutRedirectUris).Any(value => Uri.TryCreate(value, UriKind.Absolute, out var callback)
                && callback.Scheme == "https" && !callback.IsLoopback && !desired.ApprovedHttpsOrigins.Any(x => TryOrigin(x, out var origin) && SameOrigin(callback, origin)))
            || !Values(desired.WebOrigins, 32, Origin)
            || desired.WebOrigins.Any(value => Uri.TryCreate(value, UriKind.Absolute, out var webOrigin)
                && !webOrigin.IsLoopback && !desired.ApprovedHttpsOrigins.Any(x => TryOrigin(x, out var origin) && SameOrigin(webOrigin, origin)))
            || desired.RedirectUris.IsEmpty || desired.PostLogoutRedirectUris.IsEmpty
            || desired.Mappers.IsDefault || desired.Brokers.IsDefault || desired.RemoveMappers.IsDefault || desired.RemoveBrokers.IsDefault
            || desired.Mappers.Length > 128 || desired.Brokers.Length > 16
            || desired.Mappers.Any(mapper => !MapperName(mapper.Name) || !Id(mapper.Claim) || mapper.Value is null || mapper.Value.Length > 256
                || (mapper.UserAttribute is not null && !Id(mapper.UserAttribute)) || (mapper.SessionNote is not null && !Id(mapper.SessionNote))
                || (mapper.UserAttribute is not null && mapper.SessionNote is not null))
            || desired.Brokers.Any(broker => !Id(broker.Alias) || broker.ProviderId is not ("oidc" or "google")
                || broker.DisplayName is { } display && (string.IsNullOrWhiteSpace(display) || display.Length > 128 || display.Any(char.IsControl))
                || broker.ProviderId == "oidc" && (broker.Enabled || broker.ClientId is not null)
                || broker.ProviderId == "google" && (!broker.Enabled || !ClientIdentifier(broker.ClientId)))
            || !Values(desired.RemoveMappers, 128, MapperName) || !Values(desired.RemoveBrokers, 16, Id)
            || desired.Mappers.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != desired.Mappers.Length
            || desired.Brokers.Select(x => x.Alias).Distinct(StringComparer.Ordinal).Count() != desired.Brokers.Length
            || desired.Mappers.Any(x => desired.RemoveMappers.Contains(x.Name)) || desired.Brokers.Any(x => desired.RemoveBrokers.Contains(x.Alias))
            || JsonSerializer.SerializeToUtf8Bytes(desired).Length > MaximumDeclarationBytes)
            throw new Failure("InvalidConfiguration");

        return expectedTransport;
    }

    private static bool Values(ImmutableArray<string> values, int maximum, Func<string, bool> valid) =>
        !values.IsDefault && values.Length <= maximum && values.All(valid) && values.Distinct(StringComparer.Ordinal).Count() == values.Length;
    private static bool Id(string? value) => value is { Length: > 0 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    private static bool MapperName(string? value) => value is { Length: > 0 and <= 128 } && value == value.Trim() && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ' ');
    private static bool ClientIdentifier(string? value) => value is { Length: > 0 and <= 512 } && !string.IsNullOrWhiteSpace(value) && value.All(c => !char.IsControl(c));
    private static bool TryOrigin(string value, out Uri origin) => Uri.TryCreate(value, UriKind.Absolute, out origin!) && !value.Contains('*') && ValidOrigin(origin, false);
    private static bool ValidOrigin(Uri uri, bool transport) => (uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback)
        && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && (!transport || uri.Scheme == "https" && uri.IsLoopback);
    private static bool SameOrigin(Uri left, Uri right) => left.Scheme == right.Scheme && left.IdnHost.Equals(right.IdnHost, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;
    private static bool ValidAuthority(Uri uri, string realm, ImmutableArray<string> approved) => uri.Scheme == "https" && uri.UserInfo.Length == 0
        && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.AbsolutePath == "/realms/" + realm
        && (uri.IsLoopback || approved.Any(x => TryOrigin(x, out var origin) && SameOrigin(uri, origin)));
    private static bool Endpoint(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.UserInfo.Length == 0
        && uri.Fragment.Length == 0 && uri.Query.Length == 0 && !value.Contains('*') && (uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback);
    private static bool Origin(string value) => Endpoint(value) && new Uri(value).AbsolutePath == "/";

    internal static async Task<RealmReconcileResult> ReconcileAsync(HttpClient http, RealmReconcileDesired desired,
        RealmReconcileCredentials credentials, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var session = new Session(http, desired, credentials, stop.Token);
        try
        {
            session.Validate();
            return await session.RunAsync().ConfigureAwait(false);
        }
        catch (Failure error)
        {
            return session.Result(false, error.Code, error.Unknown ? "Unknown" : session.Writes == 0 ? "None" : "Observed", error.Unknown ? "InspectBeforeRetry" : "CorrectInput");
        }
        catch (OperationCanceledException)
        {
            return session.Result(false, "Cancelled", session.UnknownPending ? "Unknown" : session.Writes == 0 ? "None" : "Observed", session.UnknownPending ? "InspectBeforeRetry" : "NoRetry");
        }
        catch (Exception)
        {
            return session.Result(false, "Unexpected", session.UnknownPending ? "Unknown" : session.Writes == 0 ? "None" : "Observed", "OwnerAction");
        }
    }

    private sealed class Failure(string code, bool unknown = false) : Exception
    {
        internal string Code { get; } = code;
        internal bool Unknown { get; } = unknown;
    }

    private sealed class Session(HttpClient http, RealmReconcileDesired desired, RealmReconcileCredentials credentials, CancellationToken token)
    {
        private const string OwnerKey = "appsurface.issue888.managed";
        private const string Owner = "generic-feasibility";
        private const int MaximumResponseBytes = 1024 * 1024;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private string stage = "validation";
        private string? bearer, realmId, clientId, managedDigest;
        private JsonObject clientAttributes = new();
        private readonly HashSet<string> acknowledgedGoogleWrites = new(StringComparer.Ordinal);
        private string Admin => "admin/realms/" + Uri.EscapeDataString(desired.Realm);
        internal int Writes { get; private set; }
        private int retries, unknownWrites, readbacks;
        internal bool UnknownPending { get; private set; }

        internal RealmReconcileResult Result(bool ready, string code, string effect, string recovery) =>
            new RealmReconcileResult(ready, code, stage, Writes, retries, unknownWrites, readbacks, managedDigest, realmId, clientId, effect, recovery)
            { ProviderAssurance = ready && desired.Brokers.Any(x => x.ProviderId == "google" && x.Enabled) ? "ConfigurationObservedProviderUnverified" : "NotRequested" };

        internal void Validate()
        {
            if (http.BaseAddress is not { } address || !ValidOrigin(address, true))
                throw new Failure("InvalidConfiguration");
            var expectedTransport = ValidateDeclaration(desired);
            if (!SameOrigin(address, expectedTransport))
                throw new Failure("InvalidConfiguration");
            var googleAliases = desired.Brokers.Where(x => x.ProviderId == "google").Select(x => x.Alias).ToHashSet(StringComparer.Ordinal);
            if ((credentials.BrokerSecrets?.Keys.Any(x => !googleAliases.Contains(x)) ?? false)
                || googleAliases.Any(alias => credentials.BrokerSecrets is null || !credentials.BrokerSecrets.TryGetValue(alias, out var secret) || string.IsNullOrWhiteSpace(secret) || secret.Length > 4096))
                throw new Failure("InvalidBinding");
            if (string.IsNullOrWhiteSpace(credentials.AdminUsername) || string.IsNullOrEmpty(credentials.AdminPassword)
                || credentials.AdminUsername.Length > 256 || credentials.AdminPassword.Length > 4096
                || desired.Confidential != !string.IsNullOrEmpty(credentials.ClientSecret)
                || credentials.ClientSecret?.Length > 4096)
                throw new Failure("InvalidBinding");
        }

        internal async Task<RealmReconcileResult> RunAsync()
        {
            token.ThrowIfCancellationRequested();
            stage = "administrator-authentication";
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "admin-cli",
                ["username"] = credentials.AdminUsername,
                ["password"] = credentials.AdminPassword
            });
            using var authentication = new HttpRequestMessage(HttpMethod.Post, "realms/master/protocol/openid-connect/token") { Content = form };
            using (var reply = await http.SendAsync(authentication, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                if (reply.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new Failure("AuthorizationDenied");
                if (!reply.IsSuccessStatusCode) throw new Failure("AuthenticationRejected");
                bearer = Text(await JsonAsync(reply).ConfigureAwait(false), "access_token");
                if (string.IsNullOrEmpty(bearer)) throw new Failure("AuthenticationRejected");
            }
            stage = "realm-reconcile";
            var realm = await ReadAsync(Admin, true).ConfigureAwait(false);
            if (realm is null)
            {
                await WriteAsync(HttpMethod.Post, "admin/realms", RealmPatch(), async () => RealmMatches(await ReadAsync(Admin, true).ConfigureAwait(false))).ConfigureAwait(false);
                realm = await ReadAsync(Admin).ConfigureAwait(false);
            }
            realmId = realm!["id"]?.GetValue<string>();
            if (!RealmMatches(realm))
                await WriteAsync(HttpMethod.Put, Admin, RealmPatch(), async () => RealmMatches(await ReadAsync(Admin, true).ConfigureAwait(false))).ConfigureAwait(false);
            stage = "client-reconcile";
            var client = await FindClientAsync().ConfigureAwait(false);
            if (client is not null && client["publicClient"]?.GetValue<bool>() == false && !desired.Confidential && !desired.AllowPublicDowngrade)
                throw new Failure("DowngradeDenied");
            if (client is null)
            {
                await WriteAsync(HttpMethod.Post, Admin + "/clients", ClientPatch(), ClientConfirmedAsync).ConfigureAwait(false);
                client = await FindClientAsync().ConfigureAwait(false) ?? throw new Failure("ReadbackMismatch");
            }
            clientId = client["id"]?.GetValue<string>() ?? throw new Failure("ReadbackMismatch");
            clientAttributes = client["attributes"]?.DeepClone() as JsonObject ?? new JsonObject();
            if (!ClientMatches(client) || desired.Confidential && !await SecretMatchesAsync().ConfigureAwait(false))
                await WriteAsync(HttpMethod.Put, Admin + "/clients/" + Uri.EscapeDataString(clientId), ClientPatch(), ClientConfirmedAsync).ConfigureAwait(false);
            await ReconcileMappersAsync().ConfigureAwait(false);
            await ReconcileBrokersAsync().ConfigureAwait(false);
            stage = "effective-readback";
            var verifiedRealm = await ReadAsync(Admin).ConfigureAwait(false);
            var verifiedClient = await FindClientAsync().ConfigureAwait(false);
            if (!RealmMatches(verifiedRealm) || !ClientMatches(verifiedClient) || desired.Confidential && !await SecretMatchesAsync().ConfigureAwait(false))
                throw new Failure("ReadbackMismatch");
            await VerifyManagedAsync().ConfigureAwait(false);
            stage = "metadata-challenge";
            await VerifyChallengeAsync().ConfigureAwait(false);
            managedDigest = Hash(JsonSerializer.SerializeToUtf8Bytes(desired with { AllowPublicDowngrade = false }));
            stage = "ready";
            return Result(true, "Ready", Writes == 0 ? "None" : "Observed", "NoRetry");
        }

        private JsonObject RealmPatch()
        {
            var patch = new JsonObject { ["realm"] = desired.Realm, ["enabled"] = true, ["registrationAllowed"] = false, ["sslRequired"] = "external" };
            if (desired.LoginThemeName is not null) patch["loginTheme"] = desired.LoginThemeName;
            return patch;
        }
        private JsonObject ClientPatch()
        {
            var attributes = (JsonObject)clientAttributes.DeepClone();
            attributes["post.logout.redirect.uris"] = string.Join("##", desired.PostLogoutRedirectUris);
            var result = new JsonObject
            {
                ["clientId"] = desired.ClientId,
                ["enabled"] = true,
                ["protocol"] = "openid-connect",
                ["publicClient"] = !desired.Confidential,
                ["clientAuthenticatorType"] = "client-secret",
                ["standardFlowEnabled"] = true,
                ["implicitFlowEnabled"] = false,
                ["directAccessGrantsEnabled"] = false,
                ["serviceAccountsEnabled"] = false,
                ["redirectUris"] = Array(desired.RedirectUris),
                ["webOrigins"] = Array(desired.WebOrigins),
                ["attributes"] = attributes
            };
            if (desired.Confidential) result["secret"] = credentials.ClientSecret;
            return result;
        }
        private static JsonArray Array(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        private static string? Text(JsonNode? node, string key) => node?[key]?.GetValue<string>();
        private static bool Bool(JsonNode? node, string key, bool expected) => node?[key]?.GetValue<bool>() == expected;
        private bool RealmMatches(JsonNode? node) => Text(node, "realm") == desired.Realm && Bool(node, "enabled", true)
            && Bool(node, "registrationAllowed", false) && Text(node, "sslRequired") == "external"
            && (desired.LoginThemeName is null || Text(node, "loginTheme") == desired.LoginThemeName);
        private bool ClientMatches(JsonNode? node) => Text(node, "clientId") == desired.ClientId
            && Bool(node, "enabled", true) && Bool(node, "publicClient", !desired.Confidential) && Text(node, "protocol") == "openid-connect"
            && Bool(node, "standardFlowEnabled", true) && Bool(node, "implicitFlowEnabled", false)
            && Bool(node, "directAccessGrantsEnabled", false) && Bool(node, "serviceAccountsEnabled", false)
            && StringsMatch(node?["redirectUris"], desired.RedirectUris) && StringsMatch(node?["webOrigins"], desired.WebOrigins)
            && Text(node?["attributes"], "post.logout.redirect.uris") == string.Join("##", desired.PostLogoutRedirectUris)
            && (!desired.Confidential || Text(node, "clientAuthenticatorType") == "client-secret");
        private static bool StringsMatch(JsonNode? node, IEnumerable<string> expected) => node is JsonArray array
            && array.Select(value => value!.GetValue<string>()).Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal));
        private async Task<JsonNode?> FindClientAsync()
        {
            var clients = (await ReadAsync(Admin + "/clients?clientId=" + Uri.EscapeDataString(desired.ClientId)).ConfigureAwait(false))!.AsArray();
            if (clients.Count > 1) throw new Failure("AmbiguousIdentity");
            return clients.SingleOrDefault();
        }
        private async Task<bool> ClientConfirmedAsync()
        {
            var found = await FindClientAsync().ConfigureAwait(false);
            if (!ClientMatches(found)) return false;
            clientId = found!["id"]!.GetValue<string>();
            return !desired.Confidential || await SecretMatchesAsync().ConfigureAwait(false);
        }
        private async Task<bool> SecretMatchesAsync()
        {
            var value = await ReadAsync(Admin + "/clients/" + Uri.EscapeDataString(clientId!) + "/client-secret").ConfigureAwait(false);
            var actual = Text(value, "value");
            if (string.IsNullOrEmpty(actual) || actual.All(c => c == '*')) throw new Failure("CredentialReadbackUnavailable", UnknownPending);
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(actual)), SHA256.HashData(Encoding.UTF8.GetBytes(credentials.ClientSecret!)));
        }

        private JsonObject MapperPatch(RealmReconcileMapper mapper)
        {
            var config = new JsonObject
            {
                [OwnerKey] = Owner,
                ["claim.name"] = mapper.Claim,
                ["jsonType.label"] = "String",
                ["id.token.claim"] = "true",
                ["access.token.claim"] = "true",
                ["userinfo.token.claim"] = "true"
            };
            string type;
            if (mapper.UserAttribute is not null) { type = "oidc-usermodel-attribute-mapper"; config["user.attribute"] = mapper.UserAttribute; }
            else if (mapper.SessionNote is not null) { type = "oidc-usersessionmodel-note-mapper"; config["user.session.note"] = mapper.SessionNote; }
            else { type = "oidc-hardcoded-claim-mapper"; config["claim.value"] = mapper.Value; }
            return new JsonObject { ["name"] = mapper.Name, ["protocol"] = "openid-connect", ["protocolMapper"] = type, ["config"] = config };
        }
        private static bool Owned(JsonNode? node) => Text(node?["config"], OwnerKey) == Owner;

        private bool MapperMatches(JsonNode? node, RealmReconcileMapper mapper)
        {
            var config = node?["config"];
            return Text(config, OwnerKey) == Owner
                && Text(node, "name") == mapper.Name
                && Text(node, "protocol") == "openid-connect"
                && Text(config, "jsonType.label") == "String"
                && Text(node, "protocolMapper") == MapperType(mapper)
                && Text(config, "claim.name") == mapper.Claim
                && OptionalTextMatches(config, "user.attribute", mapper.UserAttribute)
                && OptionalTextMatches(config, "user.session.note", mapper.SessionNote)
                && (mapper.UserAttribute is not null || mapper.SessionNote is not null
                    ? config?["claim.value"] is null : Text(config, "claim.value") == mapper.Value)
                && new[] { "id.token.claim", "access.token.claim", "userinfo.token.claim" }.All(key => Text(config, key) == "true");
        }

        private static bool OptionalTextMatches(JsonNode? config, string key, string? expected) =>
            expected is null ? config?[key] is null : Text(config, key) == expected;
        private static string MapperType(RealmReconcileMapper mapper) => mapper.UserAttribute is not null ? "oidc-usermodel-attribute-mapper"
            : mapper.SessionNote is not null ? "oidc-usersessionmodel-note-mapper" : "oidc-hardcoded-claim-mapper";
        private string MapperRoot => Admin + "/clients/" + Uri.EscapeDataString(clientId!) + "/protocol-mappers/models";
        private string BrokerRoot => Admin + "/identity-provider/instances";
        private JsonObject BrokerPatch(RealmReconcileBroker broker) => broker.ProviderId == "google" ? new JsonObject
        {
            ["alias"] = broker.Alias,
            ["displayName"] = broker.DisplayName,
            ["providerId"] = "google",
            ["enabled"] = true,
            ["trustEmail"] = false,
            ["storeToken"] = false,
            ["linkOnly"] = false,
            ["config"] = new JsonObject
            {
                [OwnerKey] = Owner,
                ["clientId"] = broker.ClientId,
                ["clientSecret"] = credentials.BrokerSecrets![broker.Alias],
                ["defaultScope"] = "openid profile email",
                ["syncMode"] = "IMPORT"
            }
        } : new()
        {
            ["alias"] = broker.Alias,
            ["providerId"] = "oidc",
            ["enabled"] = false,
            ["trustEmail"] = false,
            ["storeToken"] = false,
            ["linkOnly"] = false,
            ["config"] = new JsonObject
            {
                [OwnerKey] = Owner,
                ["clientId"] = "synthetic-disabled-provider",
                ["authorizationUrl"] = "https://provider.invalid/authorize",
                ["tokenUrl"] = "https://provider.invalid/token",
                ["userInfoUrl"] = "https://provider.invalid/userinfo",
                ["syncMode"] = "IMPORT"
            }
        };
        private bool BrokerMatches(JsonNode? node, RealmReconcileBroker broker)
        {
            var config = node?["config"];
            if (Text(config, OwnerKey) != Owner || Text(node, "alias") != broker.Alias)
                return false;

            return broker.ProviderId == "google"
                ? Text(node, "providerId") == "google"
                    && (broker.DisplayName is null || Text(node, "displayName") == broker.DisplayName)
                    && Bool(node, "enabled", true) && Bool(node, "trustEmail", false) && Bool(node, "storeToken", false)
                    && Bool(node, "linkOnly", false) && Text(config, "clientId") == broker.ClientId
                    && Text(config, "defaultScope") == "openid profile email" && Text(config, "syncMode") == "IMPORT"
                    && GoogleSecretIsMaskedOrMatches(node, broker)
                : Text(node, "providerId") == "oidc"
                    && Bool(node, "enabled", false) && Bool(node, "trustEmail", false) && Bool(node, "storeToken", false)
                    && Bool(node, "linkOnly", false) && Text(config, "clientId") == "synthetic-disabled-provider"
                    && Text(config, "authorizationUrl") == "https://provider.invalid/authorize"
                    && Text(config, "tokenUrl") == "https://provider.invalid/token"
                    && Text(config, "userInfoUrl") == "https://provider.invalid/userinfo"
                    && Text(config, "syncMode") == "IMPORT";
        }
        private bool GoogleSecretMatches(JsonNode? node, RealmReconcileBroker broker)
        {
            var actual = Text(node?["config"], "clientSecret");
            if (string.IsNullOrEmpty(actual) || actual.All(c => c == '*')) throw new Failure("UnknownWriteOutcome", true);
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(actual)),
                SHA256.HashData(Encoding.UTF8.GetBytes(credentials.BrokerSecrets![broker.Alias])));
        }
        private bool GoogleSecretIsMaskedOrMatches(JsonNode? node, RealmReconcileBroker broker)
        {
            var actual = Text(node?["config"], "clientSecret");
            return string.IsNullOrEmpty(actual) || actual.All(c => c == '*') || CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(actual)), SHA256.HashData(Encoding.UTF8.GetBytes(credentials.BrokerSecrets![broker.Alias])));
        }
        private static JsonNode? Find(JsonArray nodes, string key, string value)
        {
            var matching = nodes.Where(node => Text(node, key) == value).ToArray();
            if (matching.Length > 1) throw new Failure("AmbiguousIdentity");
            return matching.SingleOrDefault();
        }
        private async Task ReconcileMappersAsync()
        {
            stage = "mapper-reconcile";
            var nodes = (await ReadAsync(MapperRoot).ConfigureAwait(false))!.AsArray();
            foreach (var mapper in desired.Mappers)
            {
                var current = Find(nodes, "name", mapper.Name);
                if (current is not null && !Owned(current)) throw new Failure("UnmanagedCollision");
                if (MapperMatches(current, mapper)) continue;
                var patch = MapperPatch(mapper);
                if (current?["config"] is JsonObject existingConfig)
                {
                    var merged = (JsonObject)existingConfig.DeepClone();
                    merged.Remove("claim.value"); merged.Remove("user.attribute"); merged.Remove("user.session.note");
                    foreach (var pair in patch["config"]!.AsObject()) merged[pair.Key] = pair.Value?.DeepClone();
                    patch["config"] = merged;
                }
                if (current is not null) patch["id"] = current["id"]!.GetValue<string>();
                await WriteAsync(current is null ? HttpMethod.Post : HttpMethod.Put,
                    current is null ? MapperRoot : MapperRoot + "/" + Uri.EscapeDataString(current["id"]!.GetValue<string>()), patch,
                    async () => MapperMatches(Find((await ReadAsync(MapperRoot).ConfigureAwait(false))!.AsArray(), "name", mapper.Name), mapper)).ConfigureAwait(false);
            }
            foreach (var name in desired.RemoveMappers)
            {
                var current = Find(nodes, "name", name);
                if (current is null) continue;
                if (!Owned(current)) throw new Failure("UnmanagedCollision");
                await WriteAsync(HttpMethod.Delete, MapperRoot + "/" + Uri.EscapeDataString(current["id"]!.GetValue<string>()), null,
                    async () => Find((await ReadAsync(MapperRoot).ConfigureAwait(false))!.AsArray(), "name", name) is null).ConfigureAwait(false);
            }
        }
        private async Task ReconcileBrokersAsync()
        {
            stage = "broker-reconcile";
            var nodes = (await ReadAsync(BrokerRoot).ConfigureAwait(false))!.AsArray();
            foreach (var broker in desired.Brokers)
            {
                var current = Find(nodes, "alias", broker.Alias);
                if (current is not null && !Owned(current)) throw new Failure("UnmanagedCollision");
                if (BrokerMatches(current, broker) && (broker.ProviderId != "google" || acknowledgedGoogleWrites.Contains(broker.Alias))) continue;
                var patch = BrokerPatch(broker);
                if (broker.ProviderId == "google" && broker.DisplayName is null)
                {
                    patch.Remove("displayName");
                    if (current?["displayName"] is { } existingDisplayName)
                        patch["displayName"] = existingDisplayName.DeepClone();
                }
                if (current?["config"] is JsonObject existingConfig)
                {
                    var merged = (JsonObject)existingConfig.DeepClone();
                    if (broker.ProviderId == "google")
                    {
                        merged.Remove("authorizationUrl"); merged.Remove("tokenUrl"); merged.Remove("userInfoUrl");
                        merged.Remove("logoutUrl"); merged.Remove("issuer");
                    }
                    foreach (var pair in patch["config"]!.AsObject()) merged[pair.Key] = pair.Value?.DeepClone();
                    patch["config"] = merged;
                }
                await WriteAsync(current is null ? HttpMethod.Post : HttpMethod.Put, current is null ? BrokerRoot : BrokerRoot + "/" + Uri.EscapeDataString(broker.Alias),
                    patch, async () =>
                    {
                        var observed = Find((await ReadAsync(BrokerRoot).ConfigureAwait(false))!.AsArray(), "alias", broker.Alias);
                        if (!BrokerMatches(observed, broker)) return false;
                        if (broker.ProviderId == "google" && !GoogleSecretMatches(observed, broker)) return false;
                        return true;
                    }).ConfigureAwait(false);
                if (broker.ProviderId == "google") acknowledgedGoogleWrites.Add(broker.Alias);
            }
            foreach (var alias in desired.RemoveBrokers)
            {
                var current = Find(nodes, "alias", alias);
                if (current is null) continue;
                if (!Owned(current)) throw new Failure("UnmanagedCollision");
                await WriteAsync(HttpMethod.Delete, BrokerRoot + "/" + Uri.EscapeDataString(alias), null,
                    async () => Find((await ReadAsync(BrokerRoot).ConfigureAwait(false))!.AsArray(), "alias", alias) is null).ConfigureAwait(false);
            }
        }
        private async Task VerifyManagedAsync()
        {
            var mappers = (await ReadAsync(MapperRoot).ConfigureAwait(false))!.AsArray();
            var brokers = (await ReadAsync(BrokerRoot).ConfigureAwait(false))!.AsArray();
            if (desired.Mappers.Any(mapper => !MapperMatches(Find(mappers, "name", mapper.Name), mapper))
                || desired.Brokers.Any(broker => !BrokerMatches(Find(brokers, "alias", broker.Alias), broker))
                || desired.RemoveMappers.Any(name => Find(mappers, "name", name) is not null)
                || desired.RemoveBrokers.Any(alias => Find(brokers, "alias", alias) is not null)) throw new Failure("ReadbackMismatch");
        }
        private async Task VerifyChallengeAsync()
        {
            using var metadataRequest = new HttpRequestMessage(HttpMethod.Get, "realms/" + desired.Realm + "/.well-known/openid-configuration");
            using var metadata = await http.SendAsync(metadataRequest, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!metadata.IsSuccessStatusCode || Text(await JsonAsync(metadata).ConfigureAwait(false), "issuer") != desired.Authority) throw new Failure("IssuerMismatch");
            var path = "realms/" + desired.Realm + "/protocol/openid-connect/auth?client_id=" + Uri.EscapeDataString(desired.ClientId)
                + "&response_type=code&scope=openid&redirect_uri=" + Uri.EscapeDataString(desired.RedirectUris[0]) + "&state=issue888&nonce=issue888";
            using var challenge = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (challenge.StatusCode != HttpStatusCode.OK || !(await BodyAsync(challenge, 16 * 1024).ConfigureAwait(false)).Contains("kc-form-login", StringComparison.Ordinal)) throw new Failure("ChallengeRejected");
        }

        private async Task<JsonNode?> ReadAsync(string path, bool absent = false)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                using var request = Request(HttpMethod.Get, path, null);
                HttpResponseMessage? reply = null;
                try { reply = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); }
                catch (HttpRequestException) { await RetryAsync(null).ConfigureAwait(false); continue; }
                using (reply)
                {
                    readbacks++;
                    if (absent && reply.StatusCode == HttpStatusCode.NotFound) return null;
                    if (reply.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new Failure("AuthorizationDenied", UnknownPending);
                    if (Transient(reply.StatusCode)) { await RetryAsync(reply).ConfigureAwait(false); continue; }
                    if (!reply.IsSuccessStatusCode) throw new Failure("ReadbackRejected", UnknownPending);
                    return await JsonAsync(reply).ConfigureAwait(false);
                }
            }
        }
        private HttpRequestMessage Request(HttpMethod method, string path, JsonNode? body)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            return request;
        }
        private async Task WriteAsync(HttpMethod method, string path, JsonNode? body, Func<Task<bool>> confirm)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                using var request = Request(method, path, body);
                HttpResponseMessage? reply = null;
                Writes++;
                UnknownPending = true;
                try { reply = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); }
                catch (HttpRequestException) { }
                using (reply)
                {
                    if (reply?.IsSuccessStatusCode == true) { UnknownPending = false; return; }
                    if (reply?.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { UnknownPending = false; throw new Failure("AuthorizationDenied"); }
                    if (reply is not null && !Transient(reply.StatusCode)) { UnknownPending = false; throw new Failure("WriteRejected"); }
                    unknownWrites++;
                    // Every uncertain write is observed before any repeat. A failed/masked observation keeps uncertainty.
                    bool observed;
                    try { observed = await confirm().ConfigureAwait(false); }
                    catch (Exception) { throw new Failure("UnknownWriteOutcome", true); }
                    UnknownPending = false;
                    if (observed) return;
                    await RetryAsync(reply).ConfigureAwait(false);
                }
            }
        }
        private async Task RetryAsync(HttpResponseMessage? response)
        {
            if (retries >= 2) throw new Failure("RetryExhausted", UnknownPending);
            var delay = TimeSpan.FromMilliseconds(retries == 0 ? 500 : 1000);
            var requested = response?.Headers.RetryAfter?.Delta;
            if (response?.Headers.RetryAfter?.Date is { } date) requested = date - DateTimeOffset.UtcNow;
            if (requested > delay || clock.Elapsed + delay >= TimeSpan.FromSeconds(120)) throw new Failure("RetryBudgetRejected", UnknownPending);
            retries++;
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
        private static bool Transient(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
        private async Task<JsonNode> JsonAsync(HttpResponseMessage response) =>
            JsonNode.Parse(await BodyAsync(response, MaximumResponseBytes).ConfigureAwait(false))
                ?? throw new Failure("InvalidResponse", UnknownPending);
        private async Task<string> BodyAsync(HttpResponseMessage response, int limit)
        {
            if (response.Content.Headers.ContentLength > limit) throw new Failure("ResponseLimit", UnknownPending);
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > limit) throw new Failure("ResponseLimit", UnknownPending);
                output.Write(buffer, 0, read);
            }
            return Encoding.UTF8.GetString(output.ToArray());
        }
        private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
