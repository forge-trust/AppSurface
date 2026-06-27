namespace ForgeTrust.AppSurface.Auth.Testing;

/// <summary>
/// Stable diagnostic codes emitted by the AppSurface auth testing harness.
/// </summary>
/// <remarks>
/// Codes are safe to show in test output, troubleshooting documentation, and CI logs. They identify setup mistakes in
/// the harness rather than production authentication decisions.
/// </remarks>
public static class AppSurfaceTestAuthDiagnosticCodes
{
    /// <summary>
    /// A persona name was null, empty, or whitespace.
    /// </summary>
    public const string BlankPersonaName = "ASTAUTH001";

    /// <summary>
    /// Two configured personas used the same ordinal name.
    /// </summary>
    public const string DuplicatePersona = "ASTAUTH002";

    /// <summary>
    /// A public helper was asked to select a persona that is not in the immutable registry.
    /// </summary>
    public const string UnknownPersona = "ASTAUTH003";

    /// <summary>
    /// Test authentication was started in a production-like environment without the explicit override.
    /// </summary>
    public const string ProductionEnvironmentBlocked = "ASTAUTH004";

    /// <summary>
    /// A scheme name was null, empty, or whitespace.
    /// </summary>
    public const string BlankSchemeName = "ASTAUTH005";

    /// <summary>
    /// An assertion helper observed an unexpected AppSurface auth outcome, reason, status, or extension value.
    /// </summary>
    public const string AssertionFailed = "ASTAUTH006";

    /// <summary>
    /// A subject claim type was empty or whitespace.
    /// </summary>
    public const string BlankSubjectClaimType = "ASTAUTH007";
}
