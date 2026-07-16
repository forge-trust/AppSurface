namespace ForgeTrust.AppSurface.Web;

/// <summary>Controls HTTP status mapping for completed named-canary evaluations.</summary>
public enum AppSurfaceCanaryCompletedResponseMode
{
    /// <summary>Return 200 for pass and 503 for every completed non-pass status. This is the default.</summary>
    StatusCode = 0,

    /// <summary>Return 200 for every completed status; diagnostic callers must parse the JSON status.</summary>
    AlwaysOk = 1,
}
