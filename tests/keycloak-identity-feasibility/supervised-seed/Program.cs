using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length != 3) return 64;

var stage = "configuration";
var receiptPath = Path.GetFullPath(args[2]);
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal =>
{
    signal.Cancel = true;
    stop.Cancel();
});
using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, signal =>
{
    signal.Cancel = true;
    stop.Cancel();
});

try
{
    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    var config = JsonSerializer.Deserialize<PublicConfiguration>(await File.ReadAllBytesAsync(Path.GetFullPath(args[0]), stop.Token), jsonOptions)
        ?? throw new InvalidDataException();
    var input = JsonSerializer.Deserialize<PrivateInput>(await File.ReadAllBytesAsync(Path.GetFullPath(args[1]), stop.Token), jsonOptions)
        ?? throw new InvalidDataException();

    var authority = Required(config.Authority);
    var adminAuthority = Required(config.AdminAuthority);
    var clientId = Required(config.ClientId);
    var realm = Required(config.Realm);
    const string username = "seeded-user";
    var runId = Required(config.ExpectedRunId);
    var keycloakHost = Required(config.KeycloakHost);
    var keycloakPort = config.KeycloakPort;
    var certificatePath = Required(config.CertificatePath);
    var workerReceiptPath = config.WorkerReceiptPath ?? "/proof/state/worker-result.json";
    var seedMode = string.IsNullOrWhiteSpace(config.SeedMode) ? "success" : config.SeedMode;
    var fixture = (realm, clientId) switch
    {
        ("sample", "sample-web") => "sample",
        ("appsurface-dev", "appsurface-web") => "appsurface",
        ("skoolit", "skoolit-web") => "skoolit",
        _ => throw new InvalidDataException()
    };
    var expectedAppSubject = fixture == "skoolit" ? "skoolit-proof-app-user" : null;

    if (!Guid.TryParseExact(runId, "N", out _)
        || config.Username is not null && config.Username != username
        || keycloakHost != "localhost" || keycloakPort is < 1024 or > 65535
        || !IsAuthority(authority, keycloakPort, realm)
        || !IsAdminAuthority(adminAuthority, keycloakPort, realm)
        || seedMode is not ("success" or "fail" or "drift-client" or "drift-mapper" or "drift-broker")
        || fixture != "sample" && seedMode.StartsWith("drift-", StringComparison.Ordinal)
        || string.IsNullOrWhiteSpace(input.AdminUsername) || string.IsNullOrWhiteSpace(input.AdminPassword)
        || input.Username is not null && input.Username != username
        || string.IsNullOrWhiteSpace(input.UserPassword))
        throw new InvalidDataException();

    stage = "required-worker-readiness";
    using (var workerReceipt = JsonDocument.Parse(await File.ReadAllBytesAsync(workerReceiptPath, stop.Token)))
    {
        var root = workerReceipt.RootElement;
        var ready = root.TryGetProperty("Ready", out var topReady) && topReady.ValueKind == JsonValueKind.True;
        if (!ready && root.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("Ready", out var nestedReady) && nestedReady.ValueKind == JsonValueKind.True)
            ready = true;
        if (!ready) throw new InvalidDataException();
    }

    if (seedMode == "fail")
    {
        stage = "injected-required-seed-failure";
        await WriteReceiptAsync(receiptPath, new SeedReceipt(false, null, "InjectedRequiredSeedFailure",
            runId, username, clientId, realm, authority, [], null), stop.Token);
        return 31;
    }

    stage = "certificate-pin";
    using var expectedCertificate = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(certificatePath, stop.Token));
    var certificatePin = expectedCertificate.GetCertHash(HashAlgorithmName.SHA256);
    var authorityUri = new Uri($"https://localhost:{keycloakPort}/", UriKind.Absolute);

    using var handler = new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        ConnectCallback = async (connection, cancellationToken) =>
        {
            if (!string.Equals(connection.DnsEndPoint.Host, authorityUri.Host, StringComparison.OrdinalIgnoreCase)
                || connection.DnsEndPoint.Port != authorityUri.Port)
                throw new IOException("Admin API target rejected.");

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
    };
    using var http = new HttpClient(handler)
    {
        BaseAddress = new Uri(authorityUri.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute),
        Timeout = TimeSpan.FromSeconds(8),
        MaxResponseContentBufferSize = 1024 * 1024
    };

    stage = "admin-token";
    using var tokenResponse = await http.PostAsync("realms/master/protocol/openid-connect/token",
        new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "admin-cli",
            ["username"] = input.AdminUsername,
            ["password"] = input.AdminPassword
        }), stop.Token);
    tokenResponse.EnsureSuccessStatusCode();
    using var token = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(stop.Token));
    var accessToken = token.RootElement.GetProperty("access_token").GetString();
    if (string.IsNullOrWhiteSpace(accessToken)) throw new InvalidDataException();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    var realmPath = "admin/realms/" + Uri.EscapeDataString(realm);
    if (fixture == "skoolit")
    {
        stage = "skoolit-profile-schema";
        await EnsureSkoolitProfileAsync(http, realmPath, stop.Token);
    }

    stage = "user-precondition";
    using var users = await GetJsonAsync(http, realmPath + "/users?exact=true&username=" + Uri.EscapeDataString(username), stop.Token);
    var userCount = users.RootElement.GetArrayLength();
    if (userCount > 1) throw new InvalidDataException();

    string subject;
    var created = userCount == 0;
    if (created)
    {
        stage = "user-create";
        using var create = await http.PostAsync(realmPath + "/users", JsonContent(new
        {
            username,
            enabled = true,
            firstName = "Synthetic",
            lastName = "Seed Fixture",
            email = username + "@example.invalid",
            emailVerified = true,
            requiredActions = Array.Empty<string>()
        }), stop.Token);
        if (create.StatusCode != HttpStatusCode.Created) throw new InvalidDataException();
    }
    else if (!IsFixtureUser(users.RootElement[0], username))
    {
        throw new InvalidDataException();
    }

    stage = "user-readback";
    using var actualUsers = await GetJsonAsync(http, realmPath + "/users?exact=true&username=" + Uri.EscapeDataString(username), stop.Token);
    if (actualUsers.RootElement.GetArrayLength() != 1) throw new InvalidDataException();
    var user = actualUsers.RootElement[0];
    if (!IsFixtureUser(user, username)) throw new InvalidDataException();
    subject = user.GetProperty("id").GetString() ?? throw new InvalidDataException();

    if (expectedAppSubject is not null)
    {
        stage = "skoolit-application-identity";
        await EnsureSkoolitApplicationIdentityAsync(http, realmPath, subject, user, expectedAppSubject, stop.Token);
    }

    stage = "password-set";
    using (var reset = await http.PutAsync(realmPath + "/users/" + Uri.EscapeDataString(subject) + "/reset-password",
        JsonContent(new { type = "password", temporary = false, value = input.UserPassword }), stop.Token))
        reset.EnsureSuccessStatusCode();

    stage = "realm-role";
    const string roleName = "viewer";
    using var roleReply = await http.GetAsync(realmPath + "/roles/" + Uri.EscapeDataString(roleName), stop.Token);
    JsonDocument role;
    if (roleReply.StatusCode == HttpStatusCode.NotFound)
    {
        using var createRole = await http.PostAsync(realmPath + "/roles", JsonContent(new { name = roleName }), stop.Token);
        if (createRole.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.Conflict))
            throw new InvalidDataException();
        role = await GetJsonAsync(http, realmPath + "/roles/" + Uri.EscapeDataString(roleName), stop.Token);
    }
    else
    {
        roleReply.EnsureSuccessStatusCode();
        role = JsonDocument.Parse(await roleReply.Content.ReadAsStringAsync(stop.Token));
    }
    using (role)
    {
        var roleId = role.RootElement.GetProperty("id").GetString() ?? throw new InvalidDataException();
        stage = "role-assignment";
        using var currentRoles = await GetJsonAsync(http,
            realmPath + "/users/" + Uri.EscapeDataString(subject) + "/role-mappings/realm", stop.Token);
        var alreadyAssigned = currentRoles.RootElement.EnumerateArray().Any(item =>
            item.TryGetProperty("id", out var id) && id.GetString() == roleId);
        if (!alreadyAssigned)
        {
            using var assignment = await http.PostAsync(
                realmPath + "/users/" + Uri.EscapeDataString(subject) + "/role-mappings/realm",
                JsonContent(new[] { new { id = roleId, name = roleName } }), stop.Token);
            assignment.EnsureSuccessStatusCode();
        }
        using var observedRoles = await GetJsonAsync(http,
            realmPath + "/users/" + Uri.EscapeDataString(subject) + "/role-mappings/realm", stop.Token);
        if (!observedRoles.RootElement.EnumerateArray().Any(item =>
                item.TryGetProperty("id", out var id) && id.GetString() == roleId
                && item.TryGetProperty("name", out var name) && name.GetString() == roleName))
            throw new InvalidDataException();
    }

    if (seedMode.StartsWith("drift-", StringComparison.Ordinal))
    {
        stage = "fixture-managed-drift";
        await InjectManagedDriftAsync(http, realmPath, clientId, seedMode, stop.Token);
    }

    stage = "receipt";
    await WriteReceiptAsync(receiptPath, new SeedReceipt(true, subject, seedMode == "success" ? "Seeded" : "FixtureDriftWritten", runId,
        username, clientId, realm, authority, [roleName], expectedAppSubject), stop.Token);
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "cancelled", stage }));
    return 124;
}
catch (Exception error)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "failed", stage, errorType = error.GetType().Name }));
    return 32;
}

static string Required(string? value) => string.IsNullOrWhiteSpace(value) ? throw new InvalidDataException() : value;

static bool IsAuthority(string authority, int port, string realm)
{
    if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri)) return false;
    var local = uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host == "localhost" || uri.Host == "127.0.0.1") && uri.Port == port;
    var publicAuthority = uri.Scheme == Uri.UriSchemeHttps && uri.Host == "auth-dev.getskoolit.com"
        && uri.IsDefaultPort && (realm is "appsurface-dev" or "skoolit");
    return (local || publicAuthority)
        && uri.AbsolutePath == "/realms/" + realm && uri.UserInfo.Length == 0
        && uri.Query.Length == 0 && uri.Fragment.Length == 0;
}

static bool IsAdminAuthority(string authority, int port, string realm)
{
    if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri)) return false;
    return uri.Scheme == Uri.UriSchemeHttps && uri.Host == "localhost" && uri.Port == port
        && uri.AbsolutePath == "/realms/" + realm && uri.UserInfo.Length == 0
        && uri.Query.Length == 0 && uri.Fragment.Length == 0;
}

static bool IsFixtureUser(JsonElement user, string username) =>
    user.TryGetProperty("username", out var actualUsername) && actualUsername.GetString() == username
    && user.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True
    && user.TryGetProperty("firstName", out var firstName) && firstName.GetString() == "Synthetic"
    && user.TryGetProperty("lastName", out var lastName) && lastName.GetString() == "Seed Fixture"
    && user.TryGetProperty("email", out var email) && email.GetString() == username + "@example.invalid";

static async Task<JsonDocument> GetJsonAsync(HttpClient http, string path, CancellationToken cancellationToken)
{
    using var response = await http.GetAsync(path, cancellationToken);
    response.EnsureSuccessStatusCode();
    return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
}

static async Task EnsureSkoolitProfileAsync(HttpClient http, string realmPath, CancellationToken cancellationToken)
{
    using var currentProfile = await GetJsonAsync(http, realmPath + "/users/profile", cancellationToken);
    var profile = JsonNode.Parse(currentProfile.RootElement.GetRawText()) as JsonObject ?? throw new InvalidDataException();
    var attributes = profile["attributes"] as JsonArray ?? throw new InvalidDataException();
    var matches = attributes.Where(attribute => attribute is JsonObject item
        && item["name"]?.GetValue<string>() == "skoolit_app_user_id").ToArray();
    if (matches.Length > 1) throw new InvalidDataException();

    var changed = false;
    JsonObject appAttribute;
    if (matches.Length == 0)
    {
        appAttribute = new JsonObject { ["name"] = "skoolit_app_user_id" };
        attributes.Add(appAttribute);
        changed = true;
    }
    else
    {
        appAttribute = matches[0] as JsonObject ?? throw new InvalidDataException();
    }

    if (appAttribute["multivalued"]?.GetValueKind() != JsonValueKind.False)
    {
        appAttribute["multivalued"] = false;
        changed = true;
    }
    if (!HasAdminOnlyProfilePermissions(appAttribute))
    {
        appAttribute["permissions"] = CreateAdminOnlyPermissions();
        changed = true;
    }

    if (changed)
    {
        using var update = await http.PutAsync(realmPath + "/users/profile",
            JsonContent(profile), cancellationToken);
        update.EnsureSuccessStatusCode();
    }

    using var observedProfile = await GetJsonAsync(http, realmPath + "/users/profile", cancellationToken);
    var observedAttribute = observedProfile.RootElement.GetProperty("attributes").EnumerateArray()
        .SingleOrDefault(attribute => attribute.TryGetProperty("name", out var name)
            && name.GetString() == "skoolit_app_user_id");
    if (observedAttribute.ValueKind == JsonValueKind.Undefined || !HasAdminOnlyProfilePermissionsElement(observedAttribute))
        throw new InvalidDataException();
}

static bool HasAdminOnlyProfilePermissions(JsonObject attribute)
{
    if (attribute["multivalued"]?.GetValueKind() != JsonValueKind.False
        || attribute["permissions"] is not JsonObject permissions)
        return false;

    return IsAdminOnlyRoleList(permissions["view"]) && IsAdminOnlyRoleList(permissions["edit"]);
}

static bool IsAdminOnlyRoleList(JsonNode? value) => value is JsonArray roles && roles.Count == 1
    && roles[0] is JsonValue role && role.TryGetValue<string>(out var roleName) && roleName == "admin";

static JsonObject CreateAdminOnlyPermissions() => new()
{
    ["view"] = new JsonArray("admin"),
    ["edit"] = new JsonArray("admin")
};

static bool HasAdminOnlyProfilePermissionsElement(JsonElement attribute)
{
    if (!attribute.TryGetProperty("multivalued", out var multivalued) || multivalued.ValueKind != JsonValueKind.False
        || !attribute.TryGetProperty("permissions", out var permissions)
        || !permissions.TryGetProperty("view", out var view) || !IsAdminOnlyRoleListElement(view)
        || !permissions.TryGetProperty("edit", out var edit) || !IsAdminOnlyRoleListElement(edit))
        return false;

    return true;
}

static bool IsAdminOnlyRoleListElement(JsonElement value) => value.ValueKind == JsonValueKind.Array
    && value.GetArrayLength() == 1 && value[0].ValueKind == JsonValueKind.String && value[0].GetString() == "admin";

static async Task EnsureSkoolitApplicationIdentityAsync(HttpClient http, string realmPath, string subject,
    JsonElement user, string expectedAppSubject, CancellationToken cancellationToken)
{
    var userNode = JsonNode.Parse(user.GetRawText()) as JsonObject ?? throw new InvalidDataException();
    JsonObject attributes;
    if (userNode["attributes"] is null)
    {
        attributes = new JsonObject();
        userNode["attributes"] = attributes;
    }
    else
    {
        attributes = userNode["attributes"] as JsonObject ?? throw new InvalidDataException();
    }

    var changed = false;
    if (attributes["skoolit_app_user_id"] is JsonNode currentValue)
    {
        if (!IsExpectedApplicationIdentity(currentValue, expectedAppSubject)) throw new InvalidDataException();
    }
    else
    {
        attributes["skoolit_app_user_id"] = new JsonArray(expectedAppSubject);
        changed = true;
    }

    var userPath = realmPath + "/users/" + Uri.EscapeDataString(subject);
    if (changed)
    {
        using var update = await http.PutAsync(userPath, JsonContent(userNode), cancellationToken);
        update.EnsureSuccessStatusCode();
    }

    using var observedUser = await GetJsonAsync(http, userPath, cancellationToken);
    if (!observedUser.RootElement.TryGetProperty("attributes", out var observedAttributes)
        || !observedAttributes.TryGetProperty("skoolit_app_user_id", out var observedValue)
        || observedValue.ValueKind != JsonValueKind.Array || observedValue.GetArrayLength() != 1
        || observedValue[0].ValueKind != JsonValueKind.String || observedValue[0].GetString() != expectedAppSubject)
        throw new InvalidDataException();
}

static bool IsExpectedApplicationIdentity(JsonNode value, string expectedAppSubject) =>
    value is JsonArray values && values.Count == 1 && values[0] is JsonValue appSubject
    && appSubject.TryGetValue<string>(out var actual) && actual == expectedAppSubject;

static StringContent JsonContent<T>(T value) =>
    new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

// The adversarial seed deliberately violates the fixture's seed contract using synthetic local state.
static async Task InjectManagedDriftAsync(HttpClient http, string realmPath, string clientId, string mode,
    CancellationToken ct)
{
    string resource;
    Action<JsonNode> mutate;
    Func<JsonElement, bool> changed;
    if (mode == "drift-broker")
    {
        resource = realmPath + "/identity-provider/instances/google";
        mutate = broker => broker["enabled"] = false;
        changed = broker => !broker.GetProperty("enabled").GetBoolean();
    }
    else
    {
        using var clients = await GetJsonAsync(http, realmPath + "/clients?clientId=" + Uri.EscapeDataString(clientId), ct);
        var client = clients.RootElement.EnumerateArray().Single(x => x.GetProperty("clientId").GetString() == clientId);
        resource = realmPath + "/clients/" + Uri.EscapeDataString(client.GetProperty("id").GetString()!);
        if (mode == "drift-mapper")
        {
            resource += "/protocol-mappers/models";
            using var mappers = await GetJsonAsync(http, resource, ct);
            var mapper = mappers.RootElement.EnumerateArray().Single(x => x.GetProperty("name").GetString() == "managed-role");
            resource += "/" + Uri.EscapeDataString(mapper.GetProperty("id").GetString()!);
            mutate = node => node["config"]!["claim.value"] = "adversarial";
            changed = mapper => mapper.GetProperty("config").GetProperty("claim.value").GetString() == "adversarial";
        }
        else
        {
            const string driftCallback = "http://127.0.0.1:19999/adversarial-callback";
            mutate = node => node["redirectUris"] = new JsonArray(driftCallback);
            changed = node => node.GetProperty("redirectUris").EnumerateArray().Single().GetString() == driftCallback;
        }
    }

    using var before = await GetJsonAsync(http, resource, ct);
    if (changed(before.RootElement)) throw new InvalidDataException();
    var update = JsonNode.Parse(before.RootElement.GetRawText()) ?? throw new InvalidDataException();
    mutate(update);
    using var put = await http.PutAsync(resource, JsonContent(update), ct);
    put.EnsureSuccessStatusCode();
    using var after = await GetJsonAsync(http, resource, ct);
    if (!changed(after.RootElement)) throw new InvalidDataException();
}

static async Task WriteReceiptAsync(string path, SeedReceipt receipt, CancellationToken cancellationToken)
{
    var directory = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(receipt,
        new JsonSerializerOptions(JsonSerializerDefaults.Web)), cancellationToken);
}

sealed class PublicConfiguration
{
    public string? Authority { get; init; }
    public string? AdminAuthority { get; init; }
    public string? ClientId { get; init; }
    public string? Realm { get; init; }
    public string? Username { get; init; }
    public string? ExpectedRunId { get; init; }
    public string? KeycloakHost { get; init; }
    public int KeycloakPort { get; init; }
    public string? CertificatePath { get; init; }
    public string? WorkerReceiptPath { get; init; }
    public string? SeedMode { get; init; }
}

sealed class PrivateInput
{
    public string? AdminUsername { get; init; }
    public string? AdminPassword { get; init; }
    public string? Username { get; init; }
    public string? UserPassword { get; init; }
}

sealed record SeedReceipt(bool CreatedOrPreserved, string? ExpectedSubject, string Code, string ExpectedRunId,
    string Username, string ClientId, string Realm, string Issuer, string[] Roles, string? ExpectedAppSubject);
