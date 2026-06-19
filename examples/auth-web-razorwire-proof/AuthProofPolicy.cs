namespace AuthWebRazorWireProofExample;

/// <summary>
/// Shared constants for the local proof policy.
/// </summary>
/// <remarks>
/// These values are sample-local and intentionally mirror the host-owned ASP.NET Core policy configured in
/// <c>Program.cs</c>. Keep the claim type and role strings case-sensitive so the fake proof principal and
/// authorization policy continue to describe the same contract.
/// </remarks>
internal static class AuthProofPolicy
{
    /// <summary>
    /// Name of the ASP.NET Core authorization policy evaluated by the API and browser proof surfaces.
    /// </summary>
    public const string Name = "OperatorsOnly";

    /// <summary>
    /// Claim type used by the proof policy to read the sample role claim.
    /// </summary>
    public const string RoleClaimType = "role";

    /// <summary>
    /// Case-sensitive role value required by <see cref="Name"/> for an allowed authorization result.
    /// </summary>
    public const string OperatorRole = "operator";

    /// <summary>
    /// Claim type mapped to the AppSurface subject through <c>MapSubjectClaim("sub")</c>.
    /// </summary>
    public const string SubjectClaimType = "sub";
}
