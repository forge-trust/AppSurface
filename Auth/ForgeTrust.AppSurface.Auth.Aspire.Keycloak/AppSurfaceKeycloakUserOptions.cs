using System.Linq;
using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Describes a deterministic local-only user imported into the AppSurface Keycloak proof realm.
/// </summary>
public sealed class AppSurfaceKeycloakUserOptions
{
    private static readonly Regex UserOrSubjectPattern = new("^[a-z][a-z0-9._-]{1,63}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Creates a local-only seeded user.
    /// </summary>
    /// <param name="username">The local Keycloak username.</param>
    /// <param name="password">The temporary local-only password stored in the realm import file.</param>
    /// <param name="subject">The stable OIDC subject value.</param>
    /// <param name="displayName">The display name used by the proof UI.</param>
    public AppSurfaceKeycloakUserOptions(string username, string password, string subject, string displayName)
    {
        Username = username;
        Password = password;
        Subject = subject;
        DisplayName = displayName;
    }

    /// <summary>
    /// Gets the local Keycloak username.
    /// </summary>
    public string Username { get; }

    /// <summary>
    /// Gets the temporary local-only password used by Keycloak realm import.
    /// </summary>
    /// <remarks>
    /// This value is intentionally never included in runtime app configuration projection.
    /// </remarks>
    public string Password { get; }

    /// <summary>
    /// Gets the stable OIDC subject value.
    /// </summary>
    public string Subject { get; }

    /// <summary>
    /// Gets the local display name used by the proof UI.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets local-only user attributes that should be imported and optionally mapped to claims.
    /// </summary>
    public IDictionary<string, string> Claims { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Validates the seeded user's username, subject, password, display name, and claim entries.
    /// </summary>
    /// <exception cref="AppSurfaceKeycloakException">A seeded user option is invalid.</exception>
    internal void Validate()
    {
        if (!UserOrSubjectPattern.IsMatch(Username))
        {
            throw Invalid(nameof(Username), "username must match ^[a-z][a-z0-9._-]{1,63}$ and cannot be . or ..");
        }

        if (!UserOrSubjectPattern.IsMatch(Subject))
        {
            throw Invalid(nameof(Subject), "subject must match ^[a-z][a-z0-9._-]{1,63}$ and cannot be . or ..");
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            throw Invalid(nameof(Password), "seeded users require a local-only password for realm import.");
        }

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            throw Invalid(nameof(DisplayName), "display name cannot be blank.");
        }

        if (Claims.Any(claim => string.IsNullOrWhiteSpace(claim.Key) || string.IsNullOrWhiteSpace(claim.Value)))
        {
            throw Invalid(nameof(Claims), "claim keys and values cannot be blank.");
        }
    }

    private static AppSurfaceKeycloakException Invalid(string optionName, string detail) =>
        new(
            AppSurfaceKeycloakDiagnosticCodes.InvalidOptions,
            $"Problem: AppSurface Keycloak user option {optionName} is invalid. Cause: {detail} Fix: use deterministic lowercase local proof values. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md. Code: {AppSurfaceKeycloakDiagnosticCodes.InvalidOptions}.");
}
