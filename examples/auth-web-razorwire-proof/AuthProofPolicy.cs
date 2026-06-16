namespace AuthWebRazorWireProofExample;

/// <summary>
/// Shared constants for the local proof policy.
/// </summary>
internal static class AuthProofPolicy
{
    public const string Name = "OperatorsOnly";
    public const string RoleClaimType = "role";
    public const string OperatorRole = "operator";
    public const string SubjectClaimType = "sub";
}
