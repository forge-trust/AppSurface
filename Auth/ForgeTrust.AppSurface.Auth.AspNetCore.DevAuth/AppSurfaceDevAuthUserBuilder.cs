using System.Security.Claims;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;

/// <summary>
/// Builds a seeded local-development persona for AppSurface DevAuth.
/// </summary>
public sealed class AppSurfaceDevAuthUserBuilder
{
    private readonly List<Claim> _claims = [];
    private string? _displayName;
    private string _subjectClaimType = AppSurfaceDevAuthDefaults.SubjectClaimType;
    private string? _subject;

    internal AppSurfaceDevAuthUserBuilder(string id)
    {
        Id = NormalizePersonaId(id);
    }

    /// <summary>
    /// Gets the local persona id.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Sets the display name shown on the DevAuth control page.
    /// </summary>
    /// <param name="displayName">Non-blank display name.</param>
    /// <returns>The same builder for chaining.</returns>
    public AppSurfaceDevAuthUserBuilder DisplayName(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        _displayName = displayName.Trim();
        return this;
    }

    /// <summary>
    /// Sets the stable subject claim for the local persona.
    /// </summary>
    /// <param name="subject">Non-blank subject value.</param>
    /// <param name="claimType">Non-blank subject claim type. Defaults to <c>sub</c>.</param>
    /// <returns>The same builder for chaining.</returns>
    public AppSurfaceDevAuthUserBuilder Subject(
        string subject,
        string claimType = AppSurfaceDevAuthDefaults.SubjectClaimType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimType);

        _subject = subject.Trim();
        _subjectClaimType = claimType.Trim();
        return this;
    }

    /// <summary>
    /// Adds a safe local-development claim to the persona.
    /// </summary>
    /// <param name="type">Non-blank claim type.</param>
    /// <param name="value">Non-blank claim value.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <remarks>
    /// Claims added here are local test inputs. They are not durable identity, tenant authority, or permission truth.
    /// Keep secrets, tokens, passwords, raw emails, and production identity payloads out of DevAuth personas.
    /// </remarks>
    public AppSurfaceDevAuthUserBuilder Claim(string type, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        _claims.Add(new Claim(type.Trim(), value.Trim()));
        return this;
    }

    internal AppSurfaceDevAuthPersona Build()
    {
        var hasSubject = !string.IsNullOrWhiteSpace(_subject);
        var subject = hasSubject ? _subject! : string.Empty;
        var displayName = string.IsNullOrWhiteSpace(_displayName) ? Id : _displayName;
        var claims = _claims
            .Where(claim => !string.Equals(claim.Type, _subjectClaimType, StringComparison.Ordinal));

        if (hasSubject)
        {
            claims = claims.Prepend(new Claim(_subjectClaimType, subject));
        }

        return new AppSurfaceDevAuthPersona(Id, displayName, _subjectClaimType, subject, claims.ToArray());
    }

    internal static string NormalizePersonaId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw CreateInvalidPersonaIdException();
        }

        if (IsInvalidPersonaId(id))
        {
            throw CreateInvalidPersonaIdException();
        }

        return id;
    }

    private static AppSurfaceDevAuthException CreateInvalidPersonaIdException()
    {
        return new AppSurfaceDevAuthException(
            AppSurfaceDevAuthDiagnostics.InvalidPersonaId,
            "ASDEV006 Problem: DevAuth persona ids must be URL-safe local identifiers. Cause: a persona id was blank, a URI dot segment, looked sensitive, or contained a character outside letters, digits, '.', '_', or '-'. Fix: use ids such as 'admin', 'viewer', or 'anonymous'. Docs: Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md#diagnostics.");
    }

    private static bool IsInvalidPersonaId(string value)
    {
        return value is "." or ".." || ContainsSensitiveToken(value) || !value.All(IsPersonaIdCharacter);
    }

    private static bool IsPersonaIdCharacter(char value)
    {
        return value is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-';
    }

    private static bool ContainsSensitiveToken(string value)
    {
        return value.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("email", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("mail", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("key", StringComparison.OrdinalIgnoreCase) ||
            value.Contains('@', StringComparison.Ordinal);
    }
}
