using System.Security.Claims;

namespace ForgeTrust.AppSurface.Auth.Testing;

/// <summary>
/// Describes one authenticated persona that the AppSurface auth testing harness can place on a request.
/// </summary>
/// <remarks>
/// Persona names are matched with ordinal comparison. The subject is added with the configured subject claim type, and
/// additional claims are copied into the generated principal. Do not put secrets, bearer tokens, cookies, or provider
/// credentials in persona claims; this type models the already-authenticated principal a real ASP.NET Core handler
/// would have produced.
/// </remarks>
public sealed class AppSurfaceTestPersona
{
    private readonly Claim[] _claims;

    /// <summary>
    /// Creates a test persona.
    /// </summary>
    /// <param name="name">Stable ordinal persona name used by public test helpers.</param>
    /// <param name="subjectId">Stable host-owned subject identifier to expose through the configured subject claim.</param>
    /// <param name="claims">Additional claims copied into the generated authenticated principal.</param>
    public AppSurfaceTestPersona(string name, string subjectId, IEnumerable<Claim>? claims = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);

        Name = name.Trim();
        SubjectId = subjectId.Trim();
        _claims = claims?.Select(CloneClaim).ToArray() ?? [];
    }

    /// <summary>
    /// Gets the ordinal persona name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the subject identifier added to the generated principal.
    /// </summary>
    public string SubjectId { get; }

    /// <summary>
    /// Gets additional claim snapshots copied into the generated principal.
    /// </summary>
    public IReadOnlyList<Claim> Claims => Array.AsReadOnly(_claims.Select(CloneClaim).ToArray());

    internal IEnumerable<Claim> CreateClaims(string subjectClaimType)
    {
        yield return new Claim(subjectClaimType, SubjectId);

        foreach (var claim in _claims)
        {
            if (string.Equals(claim.Type, subjectClaimType, StringComparison.Ordinal))
            {
                continue;
            }

            yield return CloneClaim(claim);
        }
    }

    private static Claim CloneClaim(Claim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);

        var clone = new Claim(
            claim.Type,
            claim.Value,
            claim.ValueType,
            claim.Issuer,
            claim.OriginalIssuer);

        foreach (var property in claim.Properties)
        {
            clone.Properties[property.Key] = property.Value;
        }

        return clone;
    }
}
