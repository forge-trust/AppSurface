namespace ForgeTrust.RazorWire.Auth;

/// <summary>
/// Describes the host-owned auth decision RazorWire should project.
/// </summary>
/// <param name="PolicyName">The non-empty host policy name to evaluate.</param>
/// <param name="Resource">Optional host-owned authorization resource.</param>
/// <remarks>
/// RazorWire treats this record as projection input only. The provider decides how to evaluate the policy; RazorWire
/// only maps the resulting passive auth result into stable HTML states.
/// </remarks>
public sealed record RazorWireAuthRequest(string PolicyName, object? Resource = null)
{
    /// <summary>
    /// Creates a request after validating that the policy name is present.
    /// </summary>
    /// <param name="policyName">The non-empty host policy name.</param>
    /// <param name="resource">Optional host-owned authorization resource.</param>
    /// <returns>A validated RazorWire auth request.</returns>
    /// <exception cref="ArgumentException"><paramref name="policyName"/> is null, empty, or whitespace.</exception>
    public static RazorWireAuthRequest Create(string policyName, object? resource = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        return new RazorWireAuthRequest(policyName, resource);
    }
}
