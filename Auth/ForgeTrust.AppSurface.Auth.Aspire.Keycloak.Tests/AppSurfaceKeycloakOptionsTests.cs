using System.Text.Json;
using ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class AppSurfaceKeycloakOptionsTests
{
    [Fact]
    public void Validate_WhenDefaults_AcceptsAndAddsDeterministicUris()
    {
        var options = new AppSurfaceKeycloakOptions();

        options.Validate();

        Assert.Equal(AppSurfaceKeycloakDefaults.Realm, options.Realm);
        Assert.Equal(AppSurfaceKeycloakDefaults.ClientId, options.ClientId);
        Assert.Equal(new Uri("http://localhost:5059/signin-appsurface-oidc"), options.RedirectUris.Single());
        Assert.Equal(new Uri("http://localhost:5059/signout-callback-appsurface-oidc"), options.PostLogoutRedirectUris.Single());
        Assert.False(options.UsePersistentDataVolume);
    }

    [Fact]
    public void RealmImportDirectory_DefaultUsesSafeResourceNameSegment()
    {
        var options = new AppSurfaceKeycloakOptions();

        var expectedSuffix = Path.Join(
            "appsurface-keycloak-realms",
            Path.GetFileName(AppSurfaceKeycloakDefaults.ResourceName));

        Assert.EndsWith(expectedSuffix, options.RealmImportDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void RealmImportPaths_CreateDirectoryUsesFileNameSegment()
    {
        var directory = AppSurfaceKeycloakRealmImportPaths.CreateDirectory("/tmp/appsurface", "nested/keycloak-proof");

        Assert.Equal(Path.Join("/tmp/appsurface", "appsurface-keycloak-realms", "keycloak-proof"), directory);
    }

    [Fact]
    public void RealmImportPaths_GetRealmImportFilePathUsesFileNameSegment()
    {
        var path = AppSurfaceKeycloakRealmImportPaths.GetRealmImportFilePath("/tmp/appsurface/realms", "/appsurface-dev");

        Assert.Equal(Path.Join("/tmp/appsurface/realms", "appsurface-dev-realm.json"), path);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void RealmImportPaths_WhenDirectoryBlank_ThrowsArgumentException(string directory)
    {
        Assert.Throws<ArgumentException>(() => AppSurfaceKeycloakRealmImportPaths.CreateDirectory(directory, "keycloak-proof"));
        Assert.Throws<ArgumentException>(() => AppSurfaceKeycloakRealmImportPaths.GetRealmImportFilePath(directory, "appsurface-dev"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("nested\\keycloak-proof")]
    public void RealmImportPaths_WhenSegmentUnsafe_ThrowsArgumentException(string segment)
    {
        Assert.Throws<ArgumentException>(() => AppSurfaceKeycloakRealmImportPaths.CreateDirectory("/tmp/appsurface", segment));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Arealm")]
    [InlineData("ab")]
    [InlineData("realm_underscore")]
    public void Validate_WhenRealmInvalid_ThrowsSafeDiagnostic(string realm)
    {
        var options = new AppSurfaceKeycloakOptions { Realm = realm };

        var exception = Assert.Throws<AppSurfaceKeycloakException>(options.Validate);

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.InvalidOptions, exception.Code);
        Assert.DoesNotContain("password", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("AB")]
    [InlineData("client/slash")]
    [InlineData("..")]
    public void Validate_WhenClientIdInvalid_ThrowsSafeDiagnostic(string clientId)
    {
        var options = new AppSurfaceKeycloakOptions { ClientId = clientId };

        var exception = Assert.Throws<AppSurfaceKeycloakException>(options.Validate);

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.InvalidOptions, exception.Code);
        Assert.Contains(nameof(AppSurfaceKeycloakOptions.ClientId), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_WhenClientDisplayNameBlank_ThrowsSafeDiagnostic()
    {
        var options = new AppSurfaceKeycloakOptions { ClientDisplayName = " " };

        var exception = Assert.Throws<AppSurfaceKeycloakException>(options.Validate);

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.InvalidOptions, exception.Code);
        Assert.Contains(nameof(AppSurfaceKeycloakOptions.ClientDisplayName), exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("signin-appsurface-oidc")]
    [InlineData("//signin-appsurface-oidc")]
    [InlineData("/signin\\appsurface-oidc")]
    [InlineData("/signin-appsurface-oidc?x=1")]
    [InlineData("/signin-appsurface-oidc#fragment")]
    [InlineData("/signin%2fappsurface-oidc")]
    public void Validate_WhenCallbackPathInvalid_ThrowsSafeDiagnostic(string callbackPath)
    {
        var options = new AppSurfaceKeycloakOptions { CallbackPath = callbackPath };

        var exception = Assert.Throws<AppSurfaceKeycloakException>(options.Validate);

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.InvalidOptions, exception.Code);
        Assert.Contains(nameof(AppSurfaceKeycloakOptions.CallbackPath), exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Validate_WhenFixedPortInvalid_ThrowsSafeDiagnostic(int port)
    {
        var options = new AppSurfaceKeycloakOptions { KeycloakPort = port };

        var exception = Assert.Throws<AppSurfaceKeycloakException>(options.Validate);

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.InvalidOptions, exception.Code);
        Assert.Contains(nameof(AppSurfaceKeycloakOptions.KeycloakPort), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_WhenRealmImportDirectoryBlank_ThrowsSafeDiagnostic()
    {
        var options = new AppSurfaceKeycloakOptions { RealmImportDirectory = " " };

        var exception = Assert.Throws<AppSurfaceKeycloakException>(options.Validate);

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.InvalidOptions, exception.Code);
        Assert.Contains(nameof(AppSurfaceKeycloakOptions.RealmImportDirectory), exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.com/signin-appsurface-oidc")]
    [InlineData("ftp://localhost/signin-appsurface-oidc")]
    [InlineData("http://localhost/signin-appsurface-oidc?x=1")]
    [InlineData("http://localhost/signin-appsurface-oidc#x")]
    [InlineData("http://user@localhost/signin-appsurface-oidc")]
    [InlineData("http://localhost/other")]
    [InlineData("http://localhost/signin%2fappsurface-oidc")]
    [InlineData("http://localhost/signin%5cappsurface-oidc")]
    public void Validate_WhenRedirectUnsafe_Throws(string redirect)
    {
        var options = new AppSurfaceKeycloakOptions();
        options.RedirectUris.Add(new Uri(redirect, UriKind.Absolute));

        var exception = Assert.Throws<AppSurfaceKeycloakException>(options.Validate);

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.InvalidOptions, exception.Code);
    }

    [Fact]
    public void Validate_WhenRedirectRelative_Throws()
    {
        var options = new AppSurfaceKeycloakOptions();
        options.RedirectUris.Add(new Uri("/signin-appsurface-oidc", UriKind.Relative));

        var exception = Assert.Throws<AppSurfaceKeycloakException>(options.Validate);

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.InvalidOptions, exception.Code);
    }

    [Fact]
    public void Validate_WhenLoopbackHttpsRedirects_Accepts()
    {
        var options = new AppSurfaceKeycloakOptions();
        options.RedirectUris.Add(new Uri("https://127.0.0.1:5059/signin-appsurface-oidc", UriKind.Absolute));
        options.PostLogoutRedirectUris.Add(new Uri("https://localhost:5059/signout-callback-appsurface-oidc", UriKind.Absolute));

        options.Validate();

        Assert.Equal("https", options.RedirectUris.Single().Scheme);
        Assert.Equal("127.0.0.1", options.RedirectUris.Single().Host);
    }

    [Theory]
    [InlineData("https://example.com/signout-callback-appsurface-oidc")]
    [InlineData("http://localhost/signout-callback-appsurface-oidc#x")]
    [InlineData("http://localhost/other")]
    [InlineData("http://user@localhost/signout-callback-appsurface-oidc")]
    public void Validate_WhenPostLogoutRedirectUnsafe_Throws(string redirect)
    {
        var options = new AppSurfaceKeycloakOptions();
        options.PostLogoutRedirectUris.Add(new Uri(redirect, UriKind.Absolute));

        var exception = Assert.Throws<AppSurfaceKeycloakException>(options.Validate);

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.InvalidOptions, exception.Code);
    }

    [Fact]
    public void Validate_WhenDuplicateUsers_Throws()
    {
        var options = new AppSurfaceKeycloakOptions();
        options.SeededUsers.Add(new AppSurfaceKeycloakUserOptions("admin", "local-password", "other-subject", "Duplicate"));

        var exception = Assert.Throws<AppSurfaceKeycloakException>(options.Validate);

        Assert.Contains("duplicate username", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_WhenDuplicateSubjects_Throws()
    {
        var options = new AppSurfaceKeycloakOptions();
        options.SeededUsers.Add(new AppSurfaceKeycloakUserOptions("other", "local-password", "appsurface-admin", "Duplicate"));

        var exception = Assert.Throws<AppSurfaceKeycloakException>(options.Validate);

        Assert.Contains("duplicate subject", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_WhenNoSeededUsers_Throws()
    {
        var options = new AppSurfaceKeycloakOptions();
        options.SeededUsers.Clear();

        var exception = Assert.Throws<AppSurfaceKeycloakException>(options.Validate);

        Assert.Contains("at least one local proof user", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Auser", "local-password", "valid-subject", "Valid Name", nameof(AppSurfaceKeycloakUserOptions.Username))]
    [InlineData(".", "local-password", "valid-subject", "Valid Name", nameof(AppSurfaceKeycloakUserOptions.Username))]
    [InlineData("..", "local-password", "valid-subject", "Valid Name", nameof(AppSurfaceKeycloakUserOptions.Username))]
    [InlineData("valid-user", "", "valid-subject", "Valid Name", nameof(AppSurfaceKeycloakUserOptions.Password))]
    [InlineData("valid-user", "local-password", "InvalidSubject", "Valid Name", nameof(AppSurfaceKeycloakUserOptions.Subject))]
    [InlineData("valid-user", "local-password", ".", "Valid Name", nameof(AppSurfaceKeycloakUserOptions.Subject))]
    [InlineData("valid-user", "local-password", "..", "Valid Name", nameof(AppSurfaceKeycloakUserOptions.Subject))]
    [InlineData("valid-user", "local-password", "valid-subject", " ", nameof(AppSurfaceKeycloakUserOptions.DisplayName))]
    public void Validate_WhenSeededUserInvalid_ThrowsSafeDiagnostic(
        string username,
        string password,
        string subject,
        string displayName,
        string optionName)
    {
        var options = new AppSurfaceKeycloakOptions();
        options.SeededUsers.Clear();
        options.SeededUsers.Add(new AppSurfaceKeycloakUserOptions(username, password, subject, displayName));

        var exception = Assert.Throws<AppSurfaceKeycloakException>(options.Validate);

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.InvalidOptions, exception.Code);
        Assert.Contains(optionName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_WhenSeededUserClaimBlank_ThrowsSafeDiagnostic()
    {
        var options = new AppSurfaceKeycloakOptions();
        options.SeededUsers[0].Claims[""] = "role";

        var exception = Assert.Throws<AppSurfaceKeycloakException>(options.Validate);

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.InvalidOptions, exception.Code);
        Assert.Contains(nameof(AppSurfaceKeycloakUserOptions.Claims), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Projection_ContainsOnlyAllowlistedValues()
    {
        var projection = new AppSurfaceKeycloakOptions().CreateConfigurationProjection();

        Assert.Equal(
            [
                "Authentication__Oidc__Authority",
                "Authentication__Oidc__CallbackPath",
                "Authentication__Oidc__ClientId",
                "Authentication__Oidc__RequireClientSecret",
                "Authentication__Oidc__SignedOutCallbackPath",
            ],
            projection.EnvironmentVariables.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(projection.EnvironmentVariables, pair => pair.Key.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(projection.EnvironmentVariables, pair => pair.Value.Contains("local-only", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RealmGenerator_EmitsExpectedPublicClientUsersAndNoAdminCredentials()
    {
        var options = new AppSurfaceKeycloakOptions();

        var json = AppSurfaceKeycloakRealmGenerator.Generate(options);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(AppSurfaceKeycloakDefaults.Realm, root.GetProperty("realm").GetString());
        var client = root.GetProperty("clients").EnumerateArray().Single();
        Assert.True(client.GetProperty("publicClient").GetBoolean());
        Assert.Equal(AppSurfaceKeycloakDefaults.ClientId, client.GetProperty("clientId").GetString());
        Assert.Equal("http://localhost:5059/signin-appsurface-oidc", client.GetProperty("redirectUris").EnumerateArray().Single().GetString());
        Assert.Equal("http://localhost:5059/signout-callback-appsurface-oidc", client.GetProperty("attributes").GetProperty("post.logout.redirect.uris").GetString());
        var usernames = root.GetProperty("users").EnumerateArray()
            .Select(user => user.GetProperty("username").GetString() ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["admin", "viewer"], usernames);
        var subjects = root.GetProperty("users").EnumerateArray()
            .Select(user => user.GetProperty("id").GetString() ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["appsurface-admin", "appsurface-viewer"], subjects);
        Assert.DoesNotContain(AppSurfaceKeycloakDefaults.AdminPasswordParameterName, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clientSecret", json, StringComparison.OrdinalIgnoreCase);
    }
}
