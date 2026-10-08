using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Pure diagnostic projection/latching controls; none acquires a native owner or proves root execution.</summary>
public sealed class EvidenceNativeObservationFailureTests
{
    [Fact]
    public void ActualListenerPolicyFailureSurvivesTheOuterAcceptWrapperAsClosedData()
    {
        // This policy rejection is sampled data only; no listener, worker or native ownership is created.
        var error = Assert.Throws<EvidenceAdmissionException>(() =>
            LinuxControlListenerPolicy.RequireSealed(default, 123));
        var first = LinuxControlFailure.Capture(LinuxControlFailureStage.ListenerSocketMetadata, null, error);
        var server = new LinuxControlFailureLatch();
        server.Capture(LinuxControlFailureStage.Accept, null, new IOException("outer-canary"), first);
        Assert.Same(first, server.First);
        Assert.Equal(LinuxControlFailureStage.ListenerSocketMetadata, server.First!.Stage);
        Assert.Equal(EvidenceNativeObservationErrorKind.Admission, server.First.ErrorKind);
        Assert.Equal("ASEVD402", server.First.DiagnosticCode);
        var attempt = new SupervisionSingleAttempt(); attempt.Claim();
        var wrapper = Assert.Throws<EvidenceAdmissionException>(attempt.Claim);
        var outer = EvidenceNativeObservationFailure.Capture(EvidenceNativeObservationPhase.ServerRun, wrapper, server.First);
        Assert.Same(first, outer.ControlFailure);
        Assert.Equal("ASEVD410", outer.DiagnosticCode);
        Assert.DoesNotContain("outer-canary", outer.ToJson());
        Assert.True(Encoding.UTF8.GetByteCount(outer.ToJson()) + 1 <= 1024);
    }

    [Fact]
    public void OriginalListenerCancellationFamilyIsRetainedBeforeWrapperAndLaterCleanup()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = Assert.Throws<OperationCanceledException>(cancellation.Token.ThrowIfCancellationRequested);
        var first = LinuxControlFailure.Capture(LinuxControlFailureStage.ListenerCancellation, null, error);
        var server = new LinuxControlFailureLatch();
        var attempt = new SupervisionSingleAttempt(); attempt.Claim();
        var wrapper = Assert.Throws<EvidenceAdmissionException>(attempt.Claim);
        server.Capture(LinuxControlFailureStage.AcceptJoin, null, wrapper, first);
        server.Capture(LinuxControlFailureStage.ListenerClose, null, new IOException("cleanup-canary"));
        Assert.Same(first, server.First);
        Assert.Equal(EvidenceNativeObservationErrorKind.Cancelled, first.ErrorKind);
        Assert.Null(first.DiagnosticCode);
        Assert.Null(first.Operation);
        Assert.DoesNotContain("canary", first.ToJson());
    }

    [Fact]
    public void EarlierServerFaultIsNotReplacedByLaterListenerProjectionOrConcurrentCleanup()
    {
        var server = new LinuxControlFailureLatch();
        server.Capture(LinuxControlFailureStage.RequestRead, EvidenceControlOperation.Ready,
            new IOException("first-canary"));
        var reserved = server.First;
        var error = Assert.Throws<EvidenceAdmissionException>(() =>
            LinuxControlListenerPolicy.RequireSealed(default, 123));
        var later = LinuxControlFailure.Capture(LinuxControlFailureStage.ListenerWorkerIdentity, null, error);
        Parallel.For(0, 32, _ => server.Capture(LinuxControlFailureStage.AcceptJoin, null,
            new InvalidOperationException("last-canary"), later));
        Assert.Same(reserved, server.First);
        Assert.Equal(LinuxControlFailureStage.RequestRead, server.First!.Stage);
        Assert.Equal(EvidenceControlOperation.Ready, server.First.Operation);
        Assert.Equal(EvidenceNativeObservationErrorKind.Io, server.First.ErrorKind);
        Assert.DoesNotContain("canary", server.First.ToJson());
    }

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
    public void EveryClosedPhaseProducesExactlySixBoundedFields()
    {
        foreach (var phase in Enum.GetValues<EvidenceNativeObservationPhase>())
        {
            var failure = EvidenceNativeObservationFailure.Capture(phase, new IOException("private-canary"));
            var json = failure.ToJson();
            Assert.True(Encoding.UTF8.GetByteCount(json) <= 1024);
            Assert.DoesNotContain("private-canary", json);
            using var parsed = JsonDocument.Parse(json);
            Assert.Equal(new[] { "schema", "phase", "error_kind", "diagnostic_code", "account_failure", "control_failure" },
                parsed.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal("evidence-native-observation-failure-v3", parsed.RootElement.GetProperty("schema").GetString());
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

    [Fact]
    public void AllClosedControlStagesAndOperationsHaveExactlyFourBoundedFields()
    {
        foreach (var stage in Enum.GetValues<LinuxControlFailureStage>())
        foreach (var operation in Enum.GetValues<EvidenceControlOperation>())
        {
            var detail = LinuxControlFailure.Capture(stage, operation, new IOException("canary"));
            using var parsed = JsonDocument.Parse(detail.ToJson());
            Assert.Equal(new[] { "stage", "operation", "error_kind", "diagnostic_code" },
                parsed.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal(stage.ToString(), parsed.RootElement.GetProperty("stage").GetString());
            Assert.Equal(operation.ToString(), parsed.RootElement.GetProperty("operation").GetString());
            Assert.DoesNotContain("canary", detail.ToJson());
        }
    }

    [Fact]
    public void InvalidControlStageOperationAndMessageCodesClampWithoutLeaking()
    {
        var detail = LinuxControlFailure.Capture((LinuxControlFailureStage)int.MaxValue,
            (EvidenceControlOperation)int.MaxValue, new CanaryException("ASEVD410-private-canary", new IOException("inner")));
        Assert.Equal(LinuxControlFailureStage.Unknown, detail.Stage);
        Assert.Null(detail.Operation);
        Assert.Equal(EvidenceNativeObservationErrorKind.Unknown, detail.ErrorKind);
        Assert.Null(detail.DiagnosticCode);
        Assert.DoesNotContain("canary", detail.ToJson());
        Assert.DoesNotContain("inner", detail.ToJson());
    }

    [Fact]
    public void ActualAdmissionCodeAndControlLineFamilyUseExistingProjection()
    {
        var attempt = new SupervisionSingleAttempt(); attempt.Claim();
        var admission = Assert.Throws<EvidenceAdmissionException>(attempt.Claim);
        var detail = LinuxControlFailure.Capture(LinuxControlFailureStage.ReadyClaim, EvidenceControlOperation.Ready, admission);
        Assert.Equal("ASEVD410", detail.DiagnosticCode);
        Assert.Equal(EvidenceNativeObservationErrorKind.Admission, detail.ErrorKind);
        foreach (var failure in Enum.GetValues<ControlLineFailure>())
        {
            var line = LinuxControlFailure.Capture(LinuxControlFailureStage.RequestRead, null, new ControlLineException(failure));
            Assert.Equal(EvidenceNativeObservationErrorKind.ControlLine, line.ErrorKind);
            Assert.Null(line.DiagnosticCode);
            Assert.Null(line.Operation);
        }
    }

    [Fact]
    public void FirstControlFaultSurvivesCleanupAndConcurrentLaterCapture()
    {
        var latch = new LinuxControlFailureLatch();
        latch.Capture(LinuxControlFailureStage.RequestRead, null, new IOException("first"));
        var first = latch.First;
        Parallel.For(0, 64, _ => latch.Capture(LinuxControlFailureStage.ConnectionRelease,
            EvidenceControlOperation.Exit, new ObjectDisposedException("later")));
        Assert.Same(first, latch.First);
        Assert.Equal(LinuxControlFailureStage.RequestRead, latch.First!.Stage);
        Assert.Equal(EvidenceNativeObservationErrorKind.Io, latch.First.ErrorKind);
    }

    [Fact]
    public void ConcurrentFirstControlCapturePublishesOneConsistentProjection()
    {
        var latch = new LinuxControlFailureLatch();
        Parallel.For(0, 64, index => latch.Capture(index % 2 == 0 ? LinuxControlFailureStage.RequestRead : LinuxControlFailureStage.Stop,
            index % 2 == 0 ? null : EvidenceControlOperation.Stop, index % 2 == 0 ? new IOException("private") : new TimeoutException("private")));
        var first = latch.First!;
        if (first.Stage == LinuxControlFailureStage.RequestRead)
        { Assert.Null(first.Operation); Assert.Equal(EvidenceNativeObservationErrorKind.Io, first.ErrorKind); }
        else
        { Assert.Equal(LinuxControlFailureStage.Stop, first.Stage); Assert.Equal(EvidenceControlOperation.Stop, first.Operation); Assert.Equal(EvidenceNativeObservationErrorKind.Timeout, first.ErrorKind); }
        latch.Capture(LinuxControlFailureStage.ListenerClose, null, new Exception("later"));
        Assert.Same(first, latch.First);
    }

    [Fact]
    public void ControlDetailIsNullableAndAttachedOnlyToActualServerPhases()
    {
        var detail = LinuxControlFailure.Capture(LinuxControlFailureStage.ResponseWrite, EvidenceControlOperation.Ready, new IOException("private"));
        foreach (var phase in Enum.GetValues<EvidenceNativeObservationPhase>())
        {
            var root = EvidenceNativeObservationFailure.Capture(phase, new IOException("private"), detail);
            if (phase is EvidenceNativeObservationPhase.ServerRun or EvidenceNativeObservationPhase.ServerCompletion)
                Assert.Same(detail, root.ControlFailure);
            else Assert.Null(root.ControlFailure);
            Assert.True(Encoding.UTF8.GetByteCount(root.ToJson()) + 1 <= 1024);
        }
        Assert.Null(EvidenceNativeObservationFailure.Capture(EvidenceNativeObservationPhase.ServerRun, null).ControlFailure);
    }

    [Fact]
    public void RootFirstFaultRetainsControlDetailDespiteLaterCleanup()
    {
        var detail = LinuxControlFailure.Capture(LinuxControlFailureStage.ReadyData, EvidenceControlOperation.Ready, new IOException("private"));
        var latch = new EvidenceNativeObservationFailureLatch();
        latch.Capture(EvidenceNativeObservationPhase.ServerRun, new IOException("outer"), detail);
        latch.Capture(EvidenceNativeObservationPhase.ListenerClose, new ObjectDisposedException("later"));
        Assert.Same(detail, latch.Rejected().Failure.ControlFailure);
        using var parsed = JsonDocument.Parse(latch.Rejected().Failure.ToJson());
        Assert.Equal("evidence-native-observation-failure-v3", parsed.RootElement.GetProperty("schema").GetString());
        Assert.Equal("ReadyData", parsed.RootElement.GetProperty("control_failure").GetProperty("stage").GetString());
    }

    [Fact]
    public void CompletedWorkerTaskCategoryDoesNotInventErrorCodeStatusOrOperation()
    {
        var detail = LinuxControlFailure.Capture(LinuxControlFailureStage.WorkerTerminalTaskCompleted, null, null);
        Assert.Equal(EvidenceNativeObservationErrorKind.Unknown, detail.ErrorKind);
        Assert.Null(detail.DiagnosticCode);
        Assert.Null(detail.Operation);
        Assert.DoesNotContain("pid", detail.ToJson());
        Assert.DoesNotContain("status", detail.ToJson());
    }

    [Fact]
    public void ClosureOnlyControlFaultIsFirstWithoutClearingOtherRootFailure()
    {
        var controls = new LinuxControlFailureLatch();
        Assert.Null(controls.First);
        controls.Capture(LinuxControlFailureStage.ListenerClose, null, new ObjectDisposedException("private"));
        Assert.Equal(EvidenceNativeObservationErrorKind.Disposed, controls.First!.ErrorKind);
        var roots = new EvidenceNativeObservationFailureLatch();
        roots.Capture(EvidenceNativeObservationPhase.Plan, new IOException("first"));
        roots.Capture(EvidenceNativeObservationPhase.ServerCompletion, new Exception("later"), controls.First);
        Assert.Equal(EvidenceNativeObservationPhase.Plan, roots.First!.Phase);
        Assert.Null(roots.First.ControlFailure);
    }

    private sealed class CanaryException(string message, Exception inner) : Exception(message, inner);
}
