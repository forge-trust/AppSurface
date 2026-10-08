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
    public void EveryClosedPhaseProducesExactlyFiveBoundedFields()
    {
        foreach (var phase in Enum.GetValues<EvidenceNativeObservationPhase>())
        {
            var failure = EvidenceNativeObservationFailure.Capture(phase, new IOException("private-canary"));
            var json = failure.ToJson();
            Assert.True(Encoding.UTF8.GetByteCount(json) <= 1024);
            Assert.DoesNotContain("private-canary", json);
            using var parsed = JsonDocument.Parse(json);
            Assert.Equal(new[] { "schema", "phase", "error_kind", "diagnostic_code", "account_failure" },
                parsed.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal("evidence-native-observation-failure-v2", parsed.RootElement.GetProperty("schema").GetString());
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

    [Fact]
    public void FirstUtilityCauseSurvivesRollbackFailureAndFixedAccountWrapper()
    {
        var latch = new LinuxAccountFailureLatch();
        latch.Capture(LinuxAccountPreparationStage.UtilityExecute, LinuxAccountUtilityStage.TerminalCheck,
            LinuxRunAccountOperation.CreateUser, new IOException("first-canary"), 1, 17);
        var first = latch.First;
        latch.Capture(LinuxAccountPreparationStage.CleanupUtility, LinuxAccountUtilityStage.Stop,
            LinuxRunAccountOperation.DeleteUser, new TimeoutException("rollback-canary"));
        Assert.Same(first, latch.First);
        var error = new LinuxRunAccountException(LinuxRunAccountFailure.CleanupFailed, latch.First);
        Assert.Equal(LinuxRunAccountFailure.CleanupFailed, error.Failure);
        Assert.Null(error.InnerException);
        var root = EvidenceNativeObservationFailure.Capture(EvidenceNativeObservationPhase.AccountCreate, error);
        Assert.Same(first, root.AccountFailure);
        Assert.Equal(EvidenceNativeObservationErrorKind.Io, first!.ErrorKind);
        Assert.Equal(17, first.ExecMainStatus);
        Assert.DoesNotContain("canary", root.ToJson());
    }

    [Fact]
    public void AccountCancellationRetainsOriginalTokenAndOnlyClosedData()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var first = LinuxAccountFailure.Capture(LinuxAccountPreparationStage.NamesAbsent,
            LinuxAccountUtilityStage.Unknown, null, new OperationCanceledException(cancellation.Token));
        var error = new LinuxRunAccountCancelledException(cancellation.Token, first);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Null(error.InnerException);
        var root = EvidenceNativeObservationFailure.Capture(EvidenceNativeObservationPhase.AccountCreate, error);
        Assert.Equal(EvidenceNativeObservationErrorKind.Cancelled, root.ErrorKind);
        Assert.Same(first, root.AccountFailure);
    }

    [Fact]
    public void UnknownAccountEnumsNumbersAndCanaryInnerErrorsCannotEscape()
    {
        var data = LinuxAccountFailure.Capture((LinuxAccountPreparationStage)int.MaxValue,
            (LinuxAccountUtilityStage)int.MaxValue, (LinuxRunAccountOperation)int.MaxValue,
            new CanaryException("secret-ASEVD410", new IOException("inner-canary")),
            int.MaxValue, -1, (LinuxSystemdStartError)int.MaxValue);
        Assert.Equal(LinuxAccountPreparationStage.Unknown, data.PreparationStage);
        Assert.Equal(LinuxAccountUtilityStage.Unknown, data.UtilityStage);
        Assert.Null(data.Operation);
        Assert.Null(data.ExecMainCode);
        Assert.Null(data.ExecMainStatus);
        Assert.Null(data.DBusCategory);
        Assert.Null(data.DiagnosticCode);
        Assert.Equal(EvidenceNativeObservationErrorKind.Unknown, data.ErrorKind);
        Assert.DoesNotContain("canary", data.ToJson());
        Assert.DoesNotContain("secret", data.ToJson());
        Assert.Null(EvidenceNativeObservationFailure.Capture(EvidenceNativeObservationPhase.AccountCreate,
            new CanaryException("unknown", new LinuxRunAccountException(LinuxRunAccountFailure.NssFailed))).AccountFailure);
    }

    [Fact]
    public void NestedAccountSchemaIsClosedBoundedAndDoesNotManufactureSamples()
    {
        foreach (var preparation in Enum.GetValues<LinuxAccountPreparationStage>())
        foreach (var utility in Enum.GetValues<LinuxAccountUtilityStage>())
        {
            var data = LinuxAccountFailure.Capture(preparation, utility, LinuxRunAccountOperation.CreateResultsGroup,
                new LinuxRunAccountException(LinuxRunAccountFailure.IdentityMismatch));
            var root = EvidenceNativeObservationFailure.Capture(EvidenceNativeObservationPhase.AccountCreate,
                new LinuxRunAccountException(LinuxRunAccountFailure.OperationFailed, data));
            var json = root.ToJson();
            Assert.True(Encoding.UTF8.GetByteCount(json) + 1 <= 1024);
            using var parsed = JsonDocument.Parse(json);
            var nested = parsed.RootElement.GetProperty("account_failure");
            Assert.Equal(new[] { "preparation_stage", "utility_stage", "operation", "error_kind", "diagnostic_code",
                "account_code", "exec_main_code", "exec_main_status", "dbus_category" },
                nested.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal(JsonValueKind.Null, nested.GetProperty("exec_main_code").ValueKind);
            Assert.Equal(JsonValueKind.Null, nested.GetProperty("exec_main_status").ValueKind);
            Assert.Equal("IdentityMismatch", nested.GetProperty("account_code").GetString());
        }
    }

    [Fact]
    public void AUtilityFirstProjectionIsNotReclassifiedByTheOuterNormalized410()
    {
        var first = LinuxAccountFailure.Capture(LinuxAccountPreparationStage.UtilityExecute,
            LinuxAccountUtilityStage.Start, LinuxRunAccountOperation.CreateUser, new IOException("first"),
            dbus: LinuxSystemdStartError.AccessDenied);
        var latch = new LinuxAccountFailureLatch();
        latch.Capture(LinuxAccountPreparationStage.UtilityExecute, LinuxAccountUtilityStage.Unknown,
            LinuxRunAccountOperation.CreateUser, new LinuxRunAccountException(LinuxRunAccountFailure.OperationFailed), first: first);
        Assert.Same(first, latch.First);
        Assert.Equal(LinuxAccountUtilityStage.Start, latch.First!.UtilityStage);
        Assert.Equal(LinuxSystemdStartError.AccessDenied, latch.First.DBusCategory);
        Assert.Equal(EvidenceNativeObservationErrorKind.Io, latch.First.ErrorKind);
    }

    private sealed class CanaryException(string message, Exception inner) : Exception(message, inner);
}
