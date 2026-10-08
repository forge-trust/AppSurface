using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Pure diagnostic projection/latching controls; none acquires a native owner or proves root execution.</summary>
public sealed class EvidenceNativeObservationFailureTests
{
    [Theory]
    [InlineData("ASEVD402")]
    [InlineData("ASEVD404")]
    [InlineData("ASEVD407")]
    [InlineData("ASEVD409")]
    [InlineData("ASEVD410")]
    [InlineData("ASEVD420")]
    [InlineData("ASEVD421")]
    public void ExactlyExistingCodesPassTheDataFilter(string code) =>
        Assert.Equal(code, EvidenceNativeObservationFailure.FilterCode(code));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ASEVD403")]
    [InlineData("asevd410")]
    [InlineData("ASEVD410\n")]
    [InlineData("private-canary-ASEVD410")]
    public void UnknownAliasAndCanaryCodesAreNull(string? code) =>
        Assert.Null(EvidenceNativeObservationFailure.FilterCode(code));

    [Fact]
    public void EveryClosedPhaseProducesExactlyFourBoundedFields()
    {
        foreach (var phase in Enum.GetValues<EvidenceNativeObservationPhase>())
        {
            var failure = EvidenceNativeObservationFailure.Capture(phase, new IOException("private-canary"));
            var json = failure.ToJson();
            Assert.True(Encoding.UTF8.GetByteCount(json) <= 1024);
            Assert.DoesNotContain("private-canary", json);
            using var parsed = JsonDocument.Parse(json);
            Assert.Equal(new[] { "schema", "phase", "error_kind", "diagnostic_code" },
                parsed.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal("evidence-native-observation-failure-v1", parsed.RootElement.GetProperty("schema").GetString());
            Assert.Equal(phase.ToString(), parsed.RootElement.GetProperty("phase").GetString());
            Assert.Equal("Io", parsed.RootElement.GetProperty("error_kind").GetString());
            Assert.Equal(JsonValueKind.Null, parsed.RootElement.GetProperty("diagnostic_code").ValueKind);
        }
    }

    [Fact]
    public void InvalidPhaseAndUnknownErrorDiscardTypeMessageDataAndInnerException()
    {
        var error = new CanaryException("/private/canary-ASEVD402", new IOException("inner-ASEVD410"));
        error.Data["secret"] = "raw-content";
        var failure = EvidenceNativeObservationFailure.Capture((EvidenceNativeObservationPhase)int.MaxValue, error);
        Assert.Equal(EvidenceNativeObservationPhase.Unknown, failure.Phase);
        Assert.Equal(EvidenceNativeObservationErrorKind.Unknown, failure.ErrorKind);
        Assert.Null(failure.DiagnosticCode);
        Assert.DoesNotContain("CanaryException", failure.ToJson());
        Assert.DoesNotContain("canary", failure.ToJson());
        Assert.DoesNotContain("inner", failure.ToJson());
        Assert.DoesNotContain("raw-content", failure.ToJson());
    }

    [Fact]
    public void KnownFamiliesAreClassifiedWithoutEchoingExceptionText()
    {
        (Exception Error, EvidenceNativeObservationErrorKind Kind)[] rows =
        [
            (new LinuxRunAccountException(LinuxRunAccountFailure.OperationFailed), EvidenceNativeObservationErrorKind.Accounts),
            (new SupervisionOutputPipeException(SupervisionOutputPipeFailure.CloseFailed), EvidenceNativeObservationErrorKind.OutputPipe),
            (new ControlLineException(ControlLineFailure.InvalidFrame), EvidenceNativeObservationErrorKind.ControlLine),
            (new OperationCanceledException("canary"), EvidenceNativeObservationErrorKind.Cancelled),
            (new TimeoutException("canary"), EvidenceNativeObservationErrorKind.Timeout),
            (new IOException("canary"), EvidenceNativeObservationErrorKind.Io),
            (new UnauthorizedAccessException("canary"), EvidenceNativeObservationErrorKind.AccessDenied),
            (new PlatformNotSupportedException("canary"), EvidenceNativeObservationErrorKind.Unsupported),
            (new ObjectDisposedException("canary"), EvidenceNativeObservationErrorKind.Disposed),
            (new ArgumentException("canary"), EvidenceNativeObservationErrorKind.Argument),
            (new JsonException("canary"), EvidenceNativeObservationErrorKind.InvalidData),
            (new FormatException("canary"), EvidenceNativeObservationErrorKind.InvalidData),
            (new InvalidOperationException("canary"), EvidenceNativeObservationErrorKind.InvalidOperation),
        ];
        foreach (var row in rows)
        {
            var failure = EvidenceNativeObservationFailure.Capture(EvidenceNativeObservationPhase.WorkerStart, row.Error);
            Assert.Equal(row.Kind, failure.ErrorKind);
            Assert.Null(failure.DiagnosticCode);
            Assert.DoesNotContain("canary", failure.ToJson());
        }
    }

    [Fact]
    public void ActualClosedAdmissionErrorIsProjectedWithoutCreatingAnAdmissionFactory()
    {
        var attempt = new SupervisionSingleAttempt();
        attempt.Claim();
        var error = Assert.Throws<EvidenceAdmissionException>(attempt.Claim);
        var failure = EvidenceNativeObservationFailure.Capture(EvidenceNativeObservationPhase.OwnerActivation, error);
        Assert.Equal(EvidenceNativeObservationErrorKind.Admission, failure.ErrorKind);
        Assert.Equal("ASEVD410", failure.DiagnosticCode);
        Assert.DoesNotContain("single-use", failure.ToJson());
    }

    [Fact]
    public void MessageCodeAndInnerAdmissionCannotSpoofAnAdmissionDiagnostic()
    {
        var attempt = new SupervisionSingleAttempt(); attempt.Claim();
        var inner = Assert.Throws<EvidenceAdmissionException>(attempt.Claim);
        var failure = EvidenceNativeObservationFailure.Capture(EvidenceNativeObservationPhase.Custody,
            new AggregateException("ASEVD402-canary", inner));
        Assert.Equal(EvidenceNativeObservationErrorKind.Unknown, failure.ErrorKind);
        Assert.Null(failure.DiagnosticCode);
    }

    [Fact]
    public void ExecutionFaultSurvivesAllLaterCleanupFaults()
    {
        var latch = new EvidenceNativeObservationFailureLatch();
        latch.Capture(EvidenceNativeObservationPhase.WorkerStart, new IOException("first-canary"));
        var first = latch.First;
        latch.Capture(EvidenceNativeObservationPhase.AccountsClose, new TimeoutException("later-canary"));
        latch.Capture(EvidenceNativeObservationPhase.FinalDeadline, new OperationCanceledException("last-canary"));
        Assert.Same(first, latch.First);
        Assert.Equal(EvidenceNativeObservationPhase.WorkerStart, latch.Rejected().Failure.Phase);
        Assert.Equal(EvidenceNativeObservationErrorKind.Io, latch.Rejected().Failure.ErrorKind);
    }

    [Fact]
    public void ClosureOnlyFaultKeepsItsFirstActualPhase()
    {
        var latch = new EvidenceNativeObservationFailureLatch();
        Assert.Null(latch.First); // No failure data is not a native success.
        latch.Capture(EvidenceNativeObservationPhase.ListenerClose, new ObjectDisposedException("first-canary"));
        latch.Capture(EvidenceNativeObservationPhase.BackendClose, new IOException("later-canary"));
        Assert.Equal(EvidenceNativeObservationPhase.ListenerClose, latch.First!.Phase);
        Assert.Equal(EvidenceNativeObservationErrorKind.Disposed, latch.First.ErrorKind);
    }

    [Fact]
    public void NegativeWrapperHasFixed410NoInnerAndNoRawFault()
    {
        var latch = new EvidenceNativeObservationFailureLatch();
        latch.Capture(EvidenceNativeObservationPhase.WorkspaceClose, new IOException("private-canary"));
        var error = latch.Rejected();
        Assert.StartsWith("ASEVD410: The protected empty Observation execution or final cleanup could not be established.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-canary", error.ToString());
        Assert.DoesNotContain("private-canary", error.Failure.ToJson());
    }

    [Fact]
    public void MissingFaultFallbackIsExplicitUnknownResultDataNotNativeProof()
    {
        var latch = new EvidenceNativeObservationFailureLatch();
        var error = latch.Rejected();
        Assert.Null(latch.First);
        Assert.Equal(EvidenceNativeObservationPhase.ResultCheck, error.Failure.Phase);
        Assert.Equal(EvidenceNativeObservationErrorKind.Unknown, error.Failure.ErrorKind);
        Assert.Null(error.Failure.DiagnosticCode);
    }

    [Fact]
    public void ConcurrentLaterReportsCannotReplaceAReservedFirstFault()
    {
        var latch = new EvidenceNativeObservationFailureLatch();
        latch.Capture(EvidenceNativeObservationPhase.ProtectedInput, new IOException("first"));
        var first = latch.First;
        Parallel.For(0, 32, _ => latch.Capture(EvidenceNativeObservationPhase.WorkerJoin, new Exception("later")));
        Assert.Same(first, latch.First);
    }

    private sealed class CanaryException(string message, Exception inner) : Exception(message, inner);
}
