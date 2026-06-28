namespace ForgeTrust.RazorWire.Auth;

/// <summary>
/// Safe RazorWire auth projection diagnostic metadata keys and codes.
/// </summary>
public static class RazorWireAuthDiagnostics
{
    /// <summary>
    /// Metadata key containing a safe RazorWire auth projection diagnostic code.
    /// </summary>
    public const string DiagnosticCodeMetadataKey = "razorwire.auth.diagnostic_code";

    /// <summary>
    /// No <see cref="IRazorWireAuthResultProvider"/> was registered for the current request.
    /// </summary>
    public const string MissingProvider = "RWAUTH001";

    /// <summary>
    /// The auth projection helper did not receive a non-empty policy name.
    /// </summary>
    public const string MissingPolicy = "RWAUTH002";

    /// <summary>
    /// The ASP.NET Core adapter was registered without the AppSurface policy evaluator.
    /// </summary>
    public const string MissingAspNetCorePolicyEvaluator = "RWAUTH003";
}
