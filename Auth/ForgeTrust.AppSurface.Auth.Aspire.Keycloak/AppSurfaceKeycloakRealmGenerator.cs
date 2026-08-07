using System.Text.Json;
using System.Text.Json.Nodes;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Generates deterministic Keycloak realm import JSON for the AppSurface local proof.
/// </summary>
public static class AppSurfaceKeycloakRealmGenerator
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly AsyncLocal<Action<string>?> BeforeMoveForTestingSlot = new();

    internal static Action<string>? BeforeMoveForTesting
    {
        get => BeforeMoveForTestingSlot.Value;
        set => BeforeMoveForTestingSlot.Value = value;
    }

    /// <summary>
    /// Generates deterministic realm import JSON from validated options.
    /// </summary>
    /// <param name="options">The Keycloak proof options.</param>
    /// <returns>A JSON document suitable for Keycloak startup realm import.</returns>
    public static string Generate(AppSurfaceKeycloakOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var realm = new JsonObject
        {
            ["realm"] = options.Realm,
            ["enabled"] = true,
            ["clients"] = new JsonArray(CreateClient(options)),
            ["users"] = new JsonArray(options.SeededUsers.Select(CreateUser).ToArray<JsonNode?>()),
        };
        if (options.LoginTheme is not null)
        {
            realm["loginTheme"] = options.LoginTheme.Name;
        }

        return realm.ToJsonString(SerializerOptions);
    }

    /// <summary>
    /// Writes deterministic realm import JSON into the configured import directory.
    /// </summary>
    /// <param name="options">The Keycloak proof options.</param>
    /// <returns>The written realm import file path.</returns>
    public static string WriteRealmImport(AppSurfaceKeycloakOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var path = AppSurfaceKeycloakRealmImportPaths.GetRealmImportFilePath(options.RealmImportDirectory, options.Realm);
        var temporaryPath = Path.Join(options.RealmImportDirectory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(options.RealmImportDirectory);
            File.WriteAllText(temporaryPath, Generate(options));
            BeforeMoveForTesting?.Invoke(path);
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw RealmEvidenceInvalid($"the realm import could not be materialized safely ({exception.GetType().Name}).");
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Preserve the primary write or replace failure; the next startup can remove the owned temporary file.
            }
        }

        return path;
    }

    private static JsonObject CreateClient(AppSurfaceKeycloakOptions options) =>
        new()
        {
            ["clientId"] = options.ClientId,
            ["name"] = options.ClientDisplayName,
            ["enabled"] = true,
            ["publicClient"] = true,
            ["protocol"] = "openid-connect",
            ["redirectUris"] = new JsonArray(options.RedirectUris.Select(uri => JsonValue.Create(uri.ToString())).ToArray<JsonNode?>()),
            ["webOrigins"] = new JsonArray(JsonValue.Create("+")),
            ["attributes"] = new JsonObject
            {
                ["post.logout.redirect.uris"] = string.Join("##", options.PostLogoutRedirectUris.Select(uri => uri.ToString())),
            },
            ["protocolMappers"] = new JsonArray(CreateRoleMapper()),
        };

    private static AppSurfaceKeycloakException RealmEvidenceInvalid(string detail) =>
        new(
            AppSurfaceKeycloakDiagnosticCodes.RealmEvidenceInvalid,
            $"Problem: AppSurface Keycloak realm import evidence is invalid. Cause: {detail} Fix: use an application-owned writable local import directory and regenerate disposable proof data. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md#persistent-data-recovery. Code: {AppSurfaceKeycloakDiagnosticCodes.RealmEvidenceInvalid}.");

    private static JsonObject CreateRoleMapper() =>
        new()
        {
            ["name"] = "appsurface_role",
            ["protocol"] = "openid-connect",
            ["protocolMapper"] = "oidc-usermodel-attribute-mapper",
            ["consentRequired"] = false,
            ["config"] = new JsonObject
            {
                ["user.attribute"] = "appsurface_role",
                ["claim.name"] = "appsurface_role",
                ["jsonType.label"] = "String",
                ["id.token.claim"] = "true",
                ["access.token.claim"] = "true",
                ["userinfo.token.claim"] = "true",
            },
        };

    private static JsonObject CreateUser(AppSurfaceKeycloakUserOptions user) =>
        new()
        {
            ["id"] = user.Subject,
            ["username"] = user.Username,
            ["enabled"] = true,
            ["firstName"] = user.DisplayName,
            ["lastName"] = "Local User",
            ["email"] = $"{user.Username}@appsurface.local",
            ["emailVerified"] = true,
            ["attributes"] = CreateAttributes(user),
            ["credentials"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "password",
                    ["value"] = user.Password,
                    ["temporary"] = false,
                }),
        };

    private static JsonObject CreateAttributes(AppSurfaceKeycloakUserOptions user)
    {
        var attributes = new JsonObject
        {
            ["sub"] = new JsonArray(JsonValue.Create(user.Subject)),
        };

        foreach (var claim in user.Claims.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            attributes[claim.Key] = new JsonArray(JsonValue.Create(claim.Value));
        }

        return attributes;
    }
}
