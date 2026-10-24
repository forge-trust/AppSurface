using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

// testcard protects: stable typed diagnostic fields and bounded retry guidance.
// fails_when: causes or effects produce unsafe context, unstable codes, or automatic retry.
// why_new: diagnostics are now part of the promoted production contract.
// seam: the intentionally exposed internal diagnostics mapper, without reflection.
// no_claim: this does not validate external logging sinks or deployment behavior.
public sealed class DiagnosticsTests
{
    [Fact]
    // protects: diagnostics expose stable references and redact unsafe operation context.
    public void FailureHasStableSafeReferenceFields()
    {
        var failure = KeycloakDiagnostics.Failure(KeycloakFailureCode.Invalid, KeycloakSafePhase.Validation, "profile\nsecret", KeycloakEffectState.None, "Correct input");
        Assert.Equal("profile_secret", failure.ProfileKey);
        Assert.EndsWith("#invalid", failure.DiagnosticReference, StringComparison.Ordinal);
        Assert.Equal(KeycloakRetryMetadata.None, failure.RetryMetadata);
        Assert.DoesNotContain("\n", failure.ProfileKey, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(KeycloakEffectState.Unknown, false, false, 0, true)]
    [InlineData(KeycloakEffectState.None, true, false, 1, false)]
    [InlineData(KeycloakEffectState.Committed, true, false, 0, false)]
    [InlineData(KeycloakEffectState.Staged, true, false, 0, false)]
    [InlineData(KeycloakEffectState.Staged, true, true, 1, false)]
    public void RetryGuidanceNeverAutomaticAndRespectsEffects(KeycloakEffectState effect, bool transient, bool cleaned, int attempts, bool inspect)
    {
        var retry = KeycloakRecipeValidation.RetryFor(effect, transient, cleaned, attempts);
        Assert.False(retry.AutomaticRetry);
        Assert.Equal(inspect, retry.RequiresInspectionFirst);
        Assert.InRange(retry.MaximumAdditionalAttempts, 0, 1);
    }

    [Fact]
    public void DiagnosticMapperProducesSafeTypedFailures()
    {
        var failure = KeycloakDiagnostics.Map(new InvalidDataException("/private/secret"), KeycloakSafePhase.Inspection, "/private/path", KeycloakEffectState.None);
        Assert.Equal(KeycloakFailureCode.InspectionFailed, failure.Code);
        Assert.Equal(KeycloakSafePhase.Inspection, failure.Phase);
        Assert.Equal("/private/path", failure.ProfileKey);
        Assert.Equal(KeycloakRetryMetadata.None, failure.RetryMetadata);
    }

    [Theory]
    [InlineData("cancelled", KeycloakFailureCode.Cancelled, KeycloakSafePhase.Capture)]
    [InlineData("unsupported", KeycloakFailureCode.Unsupported, KeycloakSafePhase.Build)]
    [InlineData("argument", KeycloakFailureCode.Invalid, KeycloakSafePhase.Validation)]
    [InlineData("io", KeycloakFailureCode.CaptureInvalid, KeycloakSafePhase.Capture)]
    [InlineData("timeout", KeycloakFailureCode.ProbeFailed, KeycloakSafePhase.Probe)]
    public void DiagnosticMapperCoversTypedBoundaryFailures(string exceptionKind, KeycloakFailureCode code, KeycloakSafePhase phase)
    {
        var exception = exceptionKind switch
        {
            "cancelled" => new OperationCanceledException("safe-cause") as Exception,
            "unsupported" => new NotSupportedException("safe-cause"),
            "argument" => new ArgumentException("safe-cause"),
            "io" => new IOException("safe-cause"),
            _ => new TimeoutException("safe-cause")
        };
        var failure = KeycloakDiagnostics.Map(exception, phase, "caller", KeycloakEffectState.None);
        Assert.Equal(code, failure.Code);
        Assert.Equal(phase, failure.Phase);
    }

    [Theory]
    [InlineData("archive-timeout", KeycloakSafePhase.Archive, KeycloakFailureCode.ArchiveLimit)]
    [InlineData("publication-io", KeycloakSafePhase.Publication, KeycloakFailureCode.PublicationFailed)]
    [InlineData("permission", KeycloakSafePhase.Build, KeycloakFailureCode.CaptureInvalid)]
    [InlineData("probe-process", KeycloakSafePhase.Probe, KeycloakFailureCode.ProbeFailed)]
    [InlineData("inspect-process", KeycloakSafePhase.Inspection, KeycloakFailureCode.InspectionFailed)]
    [InlineData("disposed", KeycloakSafePhase.Materialization, KeycloakFailureCode.CaptureInvalid)]
    public void EachBoundaryRetainsItsSafeFailureClassification(string kind, KeycloakSafePhase phase, KeycloakFailureCode expected)
    {
        Exception error = kind switch
        {
            "archive-timeout" => new TimeoutException("private details"),
            "publication-io" => new IOException("private details"),
            "permission" => new UnauthorizedAccessException("private details"),
            "disposed" => new ObjectDisposedException("private details"),
            _ => new BoundedProcessException(ProcessStopReason.Start)
        };
        var mapped = KeycloakDiagnostics.Map(error, phase, "fixture", KeycloakEffectState.None);
        Assert.Equal(expected, mapped.Code);
        Assert.DoesNotContain("private details", mapped.ToString(), StringComparison.Ordinal);
        Assert.Null(mapped.InnerException);
        Assert.False(mapped.RetryMetadata.AutomaticRetry);
    }

    [Theory]
    [InlineData("io", "io")]
    [InlineData("permission", "permission")]
    [InlineData("allocation", "allocation")]
    [InlineData("deadline", "deadline")]
    public void UnexpectedFailureTraceExposesOnlyClosedCauseClass(string kind, string expected)
    {
        Exception error = kind switch
        {
            "io" => new IOException("password=sentinel"),
            "permission" => new UnauthorizedAccessException("password=sentinel"),
            "allocation" => new OutOfMemoryException("password=sentinel"),
            _ => new TimeoutException("password=sentinel")
        };
        var failure = KeycloakDiagnostics.Failure(KeycloakFailureCode.Unexpected, KeycloakSafePhase.Build, "build", KeycloakEffectState.Unknown, "Inspect image", error);
        Assert.Contains("/public-boundary/" + expected + ";", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sentinel", failure.ToString(), StringComparison.Ordinal);
        Assert.Equal("unspecified", KeycloakDiagnostics.Safe(null));
        Assert.Equal(3, KeycloakDiagnostics.Safe("long-value", 3).Length);
    }

    [Fact]
    public void KnownCorrelationAndSafeContextAreStableAcrossRepeatedMapping()
    {
        var known = KeycloakDiagnostics.Failure(KeycloakFailureCode.Cancelled, KeycloakSafePhase.Capture, "capture", KeycloakEffectState.None, "fresh invocation");
        Assert.Same(known, KeycloakDiagnostics.Map(known, KeycloakSafePhase.Build, "ignored", KeycloakEffectState.Unknown));
        Assert.Same(known, KeycloakDiagnostics.Map(known, KeycloakSafePhase.Build, "ignored", KeycloakEffectState.Unknown, known.CorrelationId.Value));
        Assert.Equal(KeycloakRecoveryClass.NoRetry, known.Recovery);
        var cancelled = KeycloakDiagnostics.Map(new OperationCanceledException(), KeycloakSafePhase.Capture, "capture", KeycloakEffectState.None);
        Assert.Equal(KeycloakRecoveryClass.NoRetry, cancelled.Recovery);
        var persistent = KeycloakDiagnostics.Map(new MaterialAcquisitionException(false, "private"), KeycloakSafePhase.Acquisition, "material", KeycloakEffectState.None);
        Assert.Equal(KeycloakRecoveryClass.CorrectInput, persistent.Recovery);
        var ambiguous = KeycloakDiagnostics.Map(new MaterialAcquisitionException(true, "private"), KeycloakSafePhase.Acquisition, "material", KeycloakEffectState.Staged);
        Assert.Equal(KeycloakRecoveryClass.CorrectInput, ambiguous.Recovery);
        var inspectionDeadline = KeycloakDiagnostics.Map(new TimeoutException(), KeycloakSafePhase.Inspection, "inspection", KeycloakEffectState.None);
        Assert.Equal(KeycloakFailureCode.ProbeFailed, inspectionDeadline.Code);
    }

    [Fact]
    public void DiagnosticMapperCoversTransientAndProcessFailures()
    {
        var transient = KeycloakDiagnostics.Map(new MaterialAcquisitionException(true, "network"), KeycloakSafePhase.Acquisition, "material", KeycloakEffectState.None);
        Assert.Equal(KeycloakFailureCode.MaterialUnavailable, transient.Code);
        Assert.Equal(KeycloakRetryMetadata.ExplicitTransientOnce, transient.RetryMetadata);
        var process = KeycloakDiagnostics.Map(new BoundedProcessException(ProcessStopReason.Cancelled), KeycloakSafePhase.Probe, "probe", KeycloakEffectState.None);
        Assert.Equal(KeycloakFailureCode.Cancelled, process.Code);
        var unexpected = KeycloakDiagnostics.Map(new InvalidOperationException("secret output"), KeycloakSafePhase.Build, "build", KeycloakEffectState.Unknown);
        Assert.Equal(KeycloakFailureCode.Unexpected, unexpected.Code);
        Assert.Equal(KeycloakRetryMetadata.InspectBeforeRetry, unexpected.RetryMetadata);
    }
}
