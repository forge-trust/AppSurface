using System.Text;

namespace ForgeTrust.AppSurface.Auth.Keycloak;
/// <summary>Creates bounded, secret-safe failures at each supported public boundary.</summary>
internal static class KeycloakDiagnostics
{
    internal const string Reference = "https://github.com/forge-trust/AppSurface/blob/main/Auth/ForgeTrust.AppSurface.Auth.Keycloak/docs/diagnostics.md";
    internal static KeycloakSecurityException Failure(KeycloakFailureCode code, KeycloakSafePhase phase, string operation, KeycloakEffectState effect, string action, Exception? cause = null, KeycloakCorrelationId? correlationId = null, KeycloakRecoveryClass? recoveryOverride = null)
    {
        var transient = cause is MaterialAcquisitionException { Transient: true } && effect == KeycloakEffectState.None;
        var recovery = recoveryOverride ?? (effect == KeycloakEffectState.Unknown ? KeycloakRecoveryClass.InspectBeforeRetry : transient ? KeycloakRecoveryClass.ExplicitTransientRetryOnce : code is KeycloakFailureCode.Unsupported or KeycloakFailureCode.Unexpected ? KeycloakRecoveryClass.OwnerAction : code == KeycloakFailureCode.Cancelled ? KeycloakRecoveryClass.NoRetry : KeycloakRecoveryClass.CorrectInput);
        var safe = Safe(operation);
        var correlation = correlationId?.Value ?? Guid.NewGuid().ToString("N");
        var trace = code == KeycloakFailureCode.Unexpected ? $"\nTrace: {phase}/public-boundary/{CauseClass(cause)}; correlation={correlation}." : string.Empty;
        var next = Safe(action, 512);
        var message = $"REJECTED {code}. Next: {next}\nOperation: {safe}. Stage: {phase}. Effects: {effect}. Recovery: {recovery}.\nCorrelation: {correlation}.\nDocs: {Reference}#{code.ToString().ToLowerInvariant()}.{trace}\nNext: {next}";
        return new(code, phase, safe, effect, recovery, message, next, new(correlation));
    }

    internal static string Safe(string? text, int maximum = 128)
    {
        if (string.IsNullOrEmpty(text))
            return "unspecified";
        var output = new StringBuilder(Math.Min(text.Length, maximum));
        foreach (var c in text.Take(maximum))
            output.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '/' or ' ' or ',' ? c : '_');
        return output.ToString();
    }

    private static string CauseClass(Exception? ex) => ex switch
    {
        IOException => "io",
        UnauthorizedAccessException => "permission",
        OutOfMemoryException => "allocation",
        TimeoutException => "deadline",
        _ => "unexpected"
    };
    internal static KeycloakSecurityException Map(Exception ex, KeycloakSafePhase phase, string operation, KeycloakEffectState effect, string? correlationId = null)
    {
        if (ex is KeycloakSecurityException known)
        {
            if (correlationId is null || known.CorrelationId.Value == correlationId)
                return known;
            var rebound = Failure(known.Code, known.Phase, known.OperationId, known.Effect, known.NextAction, correlationId: new KeycloakCorrelationId(correlationId), recoveryOverride: known.Recovery);
            return new KeycloakSecurityException(known.Code, known.Phase, known.OperationId, known.Effect, known.Recovery, rebound.Message, rebound.NextAction, rebound.CorrelationId);
        }

        var code = ex switch
        {
            OperationCanceledException => KeycloakFailureCode.Cancelled,
            ObjectDisposedException => KeycloakFailureCode.CaptureInvalid,
            InvalidDataException => phase == KeycloakSafePhase.Inspection ? KeycloakFailureCode.InspectionFailed : KeycloakFailureCode.DigestMismatch,
            PlatformNotSupportedException or NotSupportedException => KeycloakFailureCode.Unsupported,
            ArgumentException => KeycloakFailureCode.Invalid,
            MaterialAcquisitionException => KeycloakFailureCode.MaterialUnavailable,
            IOException or UnauthorizedAccessException => phase == KeycloakSafePhase.Publication ? KeycloakFailureCode.PublicationFailed : KeycloakFailureCode.CaptureInvalid,
            TimeoutException => phase is KeycloakSafePhase.Probe or KeycloakSafePhase.Inspection ? KeycloakFailureCode.ProbeFailed : KeycloakFailureCode.ArchiveLimit,
            BoundedProcessException bounded => bounded.Reason == ProcessStopReason.Cancelled ? KeycloakFailureCode.Cancelled : phase == KeycloakSafePhase.Probe ? KeycloakFailureCode.ProbeFailed : KeycloakFailureCode.InspectionFailed,
            _ => KeycloakFailureCode.Unexpected
        };
        var action = effect == KeycloakEffectState.Unknown ? "Inspect the exact requested destination and image before any retry." : code == KeycloakFailureCode.Cancelled ? "Inspect effects and start a separately authorized fresh invocation." : ex is MaterialAcquisitionException { Transient: true } && effect == KeycloakEffectState.None ? "Request at most one fresh unchanged invocation; contact the material owner after a second failure." : code == KeycloakFailureCode.Unsupported ? "Use the documented supported environment or request a coordinated profile upgrade from the maintainer." : "Correct the declared input or contact the profile owner, then capture a fresh plan.";
        return Failure(code, phase, operation, effect, action, ex, correlationId is null ? null : new KeycloakCorrelationId(correlationId));
    }
}
