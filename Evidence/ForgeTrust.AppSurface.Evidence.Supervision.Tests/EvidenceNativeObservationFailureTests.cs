using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Pure diagnostic projection/latching controls; none acquires a native owner or proves root execution.</summary>
public sealed class EvidenceNativeObservationFailureTests
{
    [Fact]
    public void ActualProcedureCategoryKeepsTheOriginalCancellationFamilyWithoutReclassifyingCleanup()
    {
        var close = LinuxCustodyFailure.Capture(SupervisionCustodyFailure.LocalCloseFailed, null,
            LinuxCustodyOperation.OriginalOwnerClose, new OperationCanceledException("canary"));
        var swallowed = LinuxCustodyFailure.FromTransfer(SupervisionCustodyFailure.LocalCloseFailed, close,
            new InvalidOperationException("wrapper-canary"));
        Assert.Equal(SupervisionCustodyFailure.LocalCloseFailed, swallowed.Procedure);
        Assert.Equal(EvidenceNativeObservationErrorKind.Cancelled, swallowed.ErrorKind);
        var read = LinuxCustodyFailure.Capture(SupervisionCustodyFailure.PreflightFailed,
            LinuxCustodyNodeKind.Descriptor, LinuxCustodyOperation.HashRead, new OperationCanceledException("canary"));
        var propagated = LinuxCustodyFailure.FromTransfer(SupervisionCustodyFailure.Cancelled, read, null);
        Assert.Equal(SupervisionCustodyFailure.Cancelled, propagated.Procedure);
        Assert.Equal(LinuxCustodyNodeKind.Descriptor, propagated.NodeKind);
        Assert.Equal(LinuxCustodyOperation.HashRead, propagated.Operation);
        Assert.Equal(EvidenceNativeObservationErrorKind.Cancelled, propagated.ErrorKind);
    }

    [Theory]
    [InlineData((int)SupervisionCustodyFailure.Cancelled)]
    [InlineData((int)SupervisionCustodyFailure.SettlementValidationFailed)]
    [InlineData((int)SupervisionCustodyFailure.PreflightFailed)]
    [InlineData((int)SupervisionCustodyFailure.MutationFailed)]
    [InlineData((int)SupervisionCustodyFailure.LocalCloseFailed)]
    [InlineData((int)SupervisionCustodyFailure.FinalNativeRecheckFailed)]
    public void OriginalCustodyCategoryAndErrorSurviveLaterNormalizationAndCleanup(int procedure)
    {
        var category = (SupervisionCustodyFailure)procedure;
        Exception original = category == SupervisionCustodyFailure.Cancelled
            ? new OperationCanceledException("original-canary") : new IOException("original-canary");
        var callbacks = new LinuxCustodyFailureLatch();
        callbacks.Capture(category, LinuxCustodyNodeKind.Descriptor, LinuxCustodyOperation.HashRead, original);
        var first = callbacks.First;
        callbacks.Capture(SupervisionCustodyFailure.LocalCloseFailed, null, LinuxCustodyOperation.OriginalOwnerClose,
            new InvalidOperationException("cleanup-canary"));
        Assert.Same(first, callbacks.First);
        var normalized = Assert.Throws<EvidenceAdmissionException>(() =>
            LinuxCustodyData.RequireInventory(LinuxCustodyNodeKind.Descriptor, []));
        var detail = LinuxCustodyFailure.FromTransfer(category, first, normalized);
        Assert.Equal(category, detail.Procedure);
        Assert.Equal(LinuxCustodyNodeKind.Descriptor, detail.NodeKind);
        Assert.Equal(LinuxCustodyOperation.HashRead, detail.Operation);
        Assert.Equal(category == SupervisionCustodyFailure.Cancelled
            ? EvidenceNativeObservationErrorKind.Cancelled : EvidenceNativeObservationErrorKind.Io, detail.ErrorKind);
        Assert.Null(detail.DiagnosticCode);
        var roots = new EvidenceNativeObservationFailureLatch();
        roots.Capture(EvidenceNativeObservationPhase.Custody, normalized, custody: detail);
        roots.Capture(EvidenceNativeObservationPhase.CleanupCustody, new Exception("later-canary"));
        Assert.Same(detail, roots.First!.CustodyFailure);
        Assert.Equal("ASEVD402", roots.First.DiagnosticCode);
        Assert.DoesNotContain("canary", roots.Rejected().Failure.ToJson());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationBeforeForwardDispatchDoesNotInventLaterCleanupAsItsCause(bool cancelledCleanup)
    {
        var later = LinuxCustodyFailure.Capture(SupervisionCustodyFailure.LocalCloseFailed,
            LinuxCustodyNodeKind.Socket, LinuxCustodyOperation.Chmod, cancelledCleanup
                ? new OperationCanceledException("later-canary") : new IOException("later-canary"));
        var detail = LinuxCustodyFailure.FromTransfer(SupervisionCustodyFailure.Cancelled, later,
            new InvalidOperationException("wrapper-canary"));
        Assert.Equal(SupervisionCustodyFailure.Cancelled, detail.Procedure);
        Assert.Equal(EvidenceNativeObservationErrorKind.Cancelled, detail.ErrorKind);
        Assert.Null(detail.NodeKind);
        Assert.Equal(LinuxCustodyOperation.Unknown, detail.Operation);
        Assert.Null(detail.DiagnosticCode);
        Assert.DoesNotContain("canary", detail.ToJson());
    }

    [Fact]
    public void MismatchedProcedureAndUnknownDataCannotBeCreditedAsOriginalNodeFailure()
    {
        var later = LinuxCustodyFailure.Capture(SupervisionCustodyFailure.LocalCloseFailed,
            LinuxCustodyNodeKind.Plan, LinuxCustodyOperation.HashRead, new IOException("canary"));
        var detail = LinuxCustodyFailure.FromTransfer(SupervisionCustodyFailure.PreflightFailed, later,
            new InvalidOperationException("normalized-canary"));
        Assert.Equal(SupervisionCustodyFailure.PreflightFailed, detail.Procedure);
        Assert.Null(detail.NodeKind);
        Assert.Equal(LinuxCustodyOperation.Unknown, detail.Operation);
        Assert.Equal(EvidenceNativeObservationErrorKind.InvalidOperation, detail.ErrorKind);
        var unknown = LinuxCustodyFailure.Capture((SupervisionCustodyFailure)(-1),
            (LinuxCustodyNodeKind)int.MaxValue, (LinuxCustodyOperation)(-1), new CanaryException("/private/canary", new Exception("inner-canary")));
        Assert.Equal(SupervisionCustodyFailure.None, unknown.Procedure);
        Assert.Null(unknown.NodeKind);
        Assert.Equal(LinuxCustodyOperation.Unknown, unknown.Operation);
        Assert.Equal(EvidenceNativeObservationErrorKind.Unknown, unknown.ErrorKind);
        Assert.Null(unknown.DiagnosticCode);
        Assert.DoesNotContain("canary", unknown.ToJson());
    }

    [Fact]
    public void EveryCustodyOperationAndNodeHasOnlyFiveClosedDataFields()
    {
        foreach (var operation in Enum.GetValues<LinuxCustodyOperation>())
        foreach (var node in Enum.GetValues<LinuxCustodyNodeKind>())
        {
            var detail = LinuxCustodyFailure.Capture(SupervisionCustodyFailure.PreflightFailed,
                node, operation, new IOException("/private/canary", new Exception("inner-canary")));
            using var parsed = JsonDocument.Parse(detail.ToJson());
            Assert.Equal(new[] { "procedure", "node_kind", "operation", "error_kind", "diagnostic_code" },
                parsed.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
            Assert.Equal(operation.ToString(), parsed.RootElement.GetProperty("operation").GetString());
            Assert.Equal(node.ToString(), parsed.RootElement.GetProperty("node_kind").GetString());
            Assert.Equal("Io", parsed.RootElement.GetProperty("error_kind").GetString());
            Assert.Equal(JsonValueKind.Null, parsed.RootElement.GetProperty("diagnostic_code").ValueKind);
            Assert.DoesNotContain("canary", detail.ToJson());
        }
    }

    [Fact]
    public void CustodyAttachmentIsNullableAndRestrictedToTheActualCustodyPhases()
    {
        var custody = LinuxCustodyFailure.Capture(SupervisionCustodyFailure.MutationFailed,
            LinuxCustodyNodeKind.Socket, LinuxCustodyOperation.Chown, new IOException("canary"));
        var control = LinuxControlFailure.Capture(LinuxControlFailureStage.ListenerClose, null, new IOException("canary"));
        foreach (var phase in Enum.GetValues<EvidenceNativeObservationPhase>())
        {
            var root = EvidenceNativeObservationFailure.Capture(phase, new IOException("canary"), control, custody);
            if (phase is EvidenceNativeObservationPhase.Custody or EvidenceNativeObservationPhase.CleanupCustody
                    or EvidenceNativeObservationPhase.FileVerification or EvidenceNativeObservationPhase.AccountsClose)
                Assert.Same(custody, root.CustodyFailure);
            else Assert.Null(root.CustodyFailure);
            if (phase is EvidenceNativeObservationPhase.ServerRun or EvidenceNativeObservationPhase.ServerCompletion)
                Assert.Same(control, root.ControlFailure);
            else Assert.Null(root.ControlFailure);
            using var parsed = JsonDocument.Parse(root.ToJson());
            Assert.Equal(7, parsed.RootElement.EnumerateObject().Count());
            Assert.Equal("evidence-native-observation-failure-v4", parsed.RootElement.GetProperty("schema").GetString());
            Assert.True(Encoding.UTF8.GetByteCount(root.ToJson()) + 1 <= 1024);
            Assert.DoesNotContain("canary", root.ToJson());
        }
        Assert.Null(EvidenceNativeObservationFailure.Capture(EvidenceNativeObservationPhase.Custody, null).CustodyFailure);
    }

    [Fact]
    public void ConcurrentCustodyFirstFaultIsCoherentAndCannotBeReplacedByCleanup()
    {
        var latch = new LinuxCustodyFailureLatch();
        Parallel.For(0, 32, index => latch.Capture(index % 2 == 0
            ? SupervisionCustodyFailure.PreflightFailed : SupervisionCustodyFailure.MutationFailed,
            index % 2 == 0 ? LinuxCustodyNodeKind.Descriptor : LinuxCustodyNodeKind.Socket,
            index % 2 == 0 ? LinuxCustodyOperation.HashRead : LinuxCustodyOperation.Chmod,
            index % 2 == 0 ? new IOException("canary") : new UnauthorizedAccessException("canary")));
        var first = latch.First!;
        if (first.Procedure == SupervisionCustodyFailure.PreflightFailed)
        {
            Assert.Equal(LinuxCustodyNodeKind.Descriptor, first.NodeKind);
            Assert.Equal(LinuxCustodyOperation.HashRead, first.Operation);
            Assert.Equal(EvidenceNativeObservationErrorKind.Io, first.ErrorKind);
        }
        else
        {
            Assert.Equal(SupervisionCustodyFailure.MutationFailed, first.Procedure);
            Assert.Equal(LinuxCustodyNodeKind.Socket, first.NodeKind);
            Assert.Equal(LinuxCustodyOperation.Chmod, first.Operation);
            Assert.Equal(EvidenceNativeObservationErrorKind.AccessDenied, first.ErrorKind);
        }
        latch.Capture(SupervisionCustodyFailure.FinalNativeRecheckFailed, null, LinuxCustodyOperation.RootRecheck,
            new Exception("later-canary"));
        Assert.Same(first, latch.First);
    }

    [Fact]
    public void FailureBeforeTheProcedureStartsRetainsNoInventedProcedureCategory()
    {
        var detail = LinuxCustodyFailure.FromTransfer(SupervisionCustodyFailure.None, null,
            new PlatformNotSupportedException("canary"));
        Assert.Equal(SupervisionCustodyFailure.None, detail.Procedure);
        Assert.Null(detail.NodeKind);
        Assert.Equal(LinuxCustodyOperation.Unknown, detail.Operation);
        Assert.Equal(EvidenceNativeObservationErrorKind.Unsupported, detail.ErrorKind);
        Assert.Null(detail.DiagnosticCode);
    }

    [Theory]
    [InlineData((int)SocketError.OperationAborted, (int)LinuxControlFailureStage.ListenerNativeAcceptOperationAborted)]
    [InlineData((int)SocketError.Interrupted, (int)LinuxControlFailureStage.ListenerNativeAcceptInterrupted)]
    [InlineData((int)SocketError.ConnectionAborted, (int)LinuxControlFailureStage.ListenerNativeAcceptConnectionAborted)]
    [InlineData((int)SocketError.ConnectionReset, (int)LinuxControlFailureStage.ListenerNativeAcceptSocketOther)]
    public void NativeAcceptSocketErrorsProjectOnlyFiniteStages(int error, int expected)
    {
        var stage = LinuxControlFailure.NativeAcceptStage((SocketError)error);
        Assert.Equal((LinuxControlFailureStage)expected, stage);
        var detail = LinuxControlFailure.Capture(stage, null, new SocketException(error));
        Assert.Equal(stage, detail.Stage);
        Assert.Equal(EvidenceNativeObservationErrorKind.Unknown, detail.ErrorKind);
        Assert.Null(detail.DiagnosticCode);
        Assert.Null(detail.Operation);
    }

    [Theory]
    [InlineData((int)LinuxControlFailureStage.ListenerAdmissionDrain)]
    [InlineData((int)LinuxControlFailureStage.ListenerSocketClose)]
    [InlineData((int)LinuxControlFailureStage.ListenerNamedSocketClose)]
    [InlineData((int)LinuxControlFailureStage.ListenerParentClose)]
    [InlineData((int)LinuxControlFailureStage.ListenerNativeAcceptOperationAborted)]
    [InlineData((int)LinuxControlFailureStage.ListenerNativeAcceptInterrupted)]
    [InlineData((int)LinuxControlFailureStage.ListenerNativeAcceptConnectionAborted)]
    [InlineData((int)LinuxControlFailureStage.ListenerNativeAcceptSocketOther)]
    [InlineData((int)LinuxControlFailureStage.ListenerAcceptedClose)]
    public void ListenerClosureStagesKeepFourFieldsAndBoundedCanarySafeRootData(int stage)
    {
        var detail = LinuxControlFailure.Capture((LinuxControlFailureStage)stage, null,
            new IOException("/private/canary/ASEVD402", new InvalidOperationException("inner-canary")));
        Assert.Equal((LinuxControlFailureStage)stage, detail.Stage);
        Assert.Equal(EvidenceNativeObservationErrorKind.Io, detail.ErrorKind);
        Assert.Null(detail.DiagnosticCode);
        using var parsed = JsonDocument.Parse(detail.ToJson());
        Assert.Equal(new[] { "stage", "operation", "error_kind", "diagnostic_code" },
            parsed.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(JsonValueKind.Null, parsed.RootElement.GetProperty("operation").ValueKind);
        Assert.Equal(JsonValueKind.Null, parsed.RootElement.GetProperty("diagnostic_code").ValueKind);
        var root = EvidenceNativeObservationFailure.Capture(EvidenceNativeObservationPhase.ServerRun,
            new InvalidOperationException("outer-canary"), detail);
        Assert.True(Encoding.UTF8.GetByteCount(root.ToJson()) + 1 <= 1024);
        Assert.DoesNotContain("canary", root.ToJson());
        Assert.DoesNotContain("/private/", root.ToJson());
    }

    [Fact]
    public void UnknownSocketErrorValuesClampWithoutBecomingShutdownOrRawNumbers()
    {
        foreach (var error in new[] { -1, int.MaxValue })
        {
            var stage = LinuxControlFailure.NativeAcceptStage((SocketError)error);
            Assert.Equal(LinuxControlFailureStage.ListenerNativeAcceptSocketOther, stage);
            var detail = LinuxControlFailure.Capture(stage, (EvidenceControlOperation)(-1),
                new InvalidOperationException("ASEVD402-private-canary"));
            Assert.Null(detail.Operation);
            Assert.Null(detail.DiagnosticCode);
            Assert.DoesNotContain("canary", detail.ToJson());
            Assert.DoesNotContain(error.ToString(System.Globalization.CultureInfo.InvariantCulture), detail.ToJson());
        }
    }

    [Fact]
    public void PreciseListenerFaultSurvivesAdmissionAndRootClosureWrappers()
    {
        var listener = new LinuxControlFailureLatch();
        var original = new SocketException((int)SocketError.ConnectionAborted);
        listener.Capture(LinuxControlFailure.NativeAcceptStage(original.SocketErrorCode), null, original);
        var first = listener.First;
        var wrapped = Assert.Throws<EvidenceAdmissionException>(() =>
            LinuxControlListenerPolicy.RequireSealed(default, 123));
        listener.Capture(LinuxControlFailureStage.ListenerAdmissionDrain, null, wrapped);
        Assert.Same(first, listener.First);
        var server = new LinuxControlFailureLatch();
        server.Capture(LinuxControlFailureStage.ListenerClose, EvidenceControlOperation.Exit, wrapped, listener.First);
        Assert.Same(first, server.First);
        var attempt = new SupervisionSingleAttempt(); attempt.Claim();
        var outerError = Assert.Throws<EvidenceAdmissionException>(attempt.Claim);
        var root = EvidenceNativeObservationFailure.Capture(EvidenceNativeObservationPhase.ServerRun, outerError, server.First);
        Assert.Equal("ASEVD410", root.DiagnosticCode);
        Assert.Equal(LinuxControlFailureStage.ListenerNativeAcceptConnectionAborted, root.ControlFailure!.Stage);
        Assert.Null(root.ControlFailure.Operation);
        Assert.Null(root.ControlFailure.DiagnosticCode);
        Assert.True(Encoding.UTF8.GetByteCount(root.ToJson()) + 1 <= 1024);
    }

    [Fact]
    public void EarlierServerFaultStillWinsAgainstListenerClosureAndConcurrentLaterFailures()
    {
        var server = new LinuxControlFailureLatch();
        server.Capture(LinuxControlFailureStage.ResponseWrite, EvidenceControlOperation.Ready,
            new IOException("first-canary"));
        var first = server.First;
        var listener = new LinuxControlFailureLatch();
        listener.Capture(LinuxControlFailureStage.ListenerSocketClose, null,
            new InvalidOperationException("later-canary"));
        Parallel.For(0, 16, _ => server.Capture(LinuxControlFailureStage.ListenerClose, null,
            new IOException("wrapper-canary"), listener.First));
        Assert.Same(first, server.First);
        Assert.Equal(LinuxControlFailureStage.ResponseWrite, server.First!.Stage);
        Assert.Equal(EvidenceControlOperation.Ready, server.First.Operation);
        Assert.DoesNotContain("canary", server.First.ToJson());
    }

    [Theory]
    [InlineData(-1, 0x9fa0, (int)LinuxControlFailureStage.ProcessFileSystemInspect)]
    [InlineData(1, 0x9fa0, (int)LinuxControlFailureStage.ProcessFileSystemInspect)]
    [InlineData(0, 0, (int)LinuxControlFailureStage.ProcessFileSystemType)]
    [InlineData(0, 0x9fa1, (int)LinuxControlFailureStage.ProcessFileSystemType)]
    public void RetainedDirectoryFileSystemDataRejectsAtTheOriginalPredicate(int result, int type, int expected)
    {
        var stage = LinuxControlFailureStage.Unknown;
        var error = Assert.Throws<EvidenceAdmissionException>(() =>
            LinuxProcessData.RequireProcFileSystem(result, type, ref stage));
        Assert.Equal((LinuxControlFailureStage)expected, stage);
        var failure = LinuxControlFailure.Capture(stage, null, error);
        Assert.Equal("ASEVD402", failure.DiagnosticCode);
        Assert.Equal(EvidenceNativeObservationErrorKind.Admission, failure.ErrorKind);
        Assert.Null(failure.Operation);
        LinuxProcessData.RequireProcFileSystem(0, 0x9fa0, ref stage);
        Assert.Equal(LinuxControlFailureStage.ProcessFileSystemType, stage);
    }

    [Theory]
    [InlineData(0, 0x416d, (int)LinuxControlFailureStage.ProcessDirectoryInode)]
    [InlineData(0, 0x8000, (int)LinuxControlFailureStage.ProcessDirectoryInode)]
    [InlineData(1, 0x81a4, (int)LinuxControlFailureStage.ProcessDirectoryType)]
    [InlineData(1, 0xc180, (int)LinuxControlFailureStage.ProcessDirectoryType)]
    public void RetainedDirectoryStatDataKeepsNonzeroInodeThenExactType(int inode, int mode, int expected)
    {
        var stage = LinuxControlFailureStage.Unknown;
        var error = Assert.Throws<EvidenceAdmissionException>(() =>
            LinuxProcessData.RequireProcessDirectory((ulong)inode, (ushort)mode, ref stage));
        Assert.Equal((LinuxControlFailureStage)expected, stage);
        Assert.Equal("ASEVD402", LinuxControlFailure.Capture(stage, null, error).DiagnosticCode);
        LinuxProcessData.RequireProcessDirectory(1, 0x416d, ref stage);
        Assert.Equal(LinuxControlFailureStage.ProcessDirectoryType, stage);
        // Permissions are compared with the original metadata later, not newly constrained here.
        LinuxProcessData.RequireProcessDirectory(1, 0x41c0, ref stage);
        Assert.Equal(LinuxControlFailureStage.ProcessDirectoryType, stage);
    }

    [Theory]
    [InlineData(0, (int)LinuxControlFailureStage.ProcessRetainedProcessDeviceMajor)]
    [InlineData(1, (int)LinuxControlFailureStage.ProcessRetainedProcessDeviceMinor)]
    [InlineData(2, (int)LinuxControlFailureStage.ProcessRetainedProcessInode)]
    [InlineData(3, (int)LinuxControlFailureStage.ProcessRetainedProcessUid)]
    [InlineData(4, (int)LinuxControlFailureStage.ProcessRetainedProcessGid)]
    [InlineData(5, (int)LinuxControlFailureStage.ProcessRetainedProcessMode)]
    public void RetainedDirectoryMetadataRejectsEachChangedFieldWithAnExactNeighbor(int variant, int expectedStage)
    {
        var expected = new LinuxProcessIdentity.ProcNodeMetadata(0, 5, 123, 65010, 65011, 0x416d);
        var actual = variant switch
        {
            0 => expected with { Major = 1 },
            1 => expected with { Minor = 6 },
            2 => expected with { Inode = 124 },
            3 => expected with { Uid = 65012 },
            4 => expected with { Gid = 65012 },
            _ => expected with { Mode = 0x41c0 },
        };
        var stage = LinuxControlFailureStage.Unknown;
        var error = Assert.Throws<EvidenceAdmissionException>(() =>
            LinuxProcessData.RequireRetainedProcessMetadata(actual, expected, ref stage));
        Assert.Equal((LinuxControlFailureStage)expectedStage, stage);
        Assert.Equal("ASEVD402", LinuxControlFailure.Capture(stage, null, error).DiagnosticCode);
        LinuxProcessData.RequireRetainedProcessMetadata(expected, expected, ref stage);
        Assert.Equal(LinuxControlFailureStage.ProcessRetainedProcessMetadata, stage);
    }

    [Fact]
    public void FirstRetainedDirectoryFieldAndFaultSurviveLaterWrappersAndCleanup()
    {
        var expected = new LinuxProcessIdentity.ProcNodeMetadata(0, 5, 123, 65010, 65011, 0x416d);
        var actual = new LinuxProcessIdentity.ProcNodeMetadata(1, 6, 124, 65012, 65013, 0x41c0);
        var stage = LinuxControlFailureStage.Unknown;
        var error = Assert.Throws<EvidenceAdmissionException>(() =>
            LinuxProcessData.RequireRetainedProcessMetadata(actual, expected, ref stage));
        Assert.Equal(LinuxControlFailureStage.ProcessRetainedProcessDeviceMajor, stage);
        var process = new LinuxControlFailureLatch();
        process.Capture(stage, null, error);
        var first = process.First;
        var listener = new LinuxControlFailureLatch();
        listener.Capture(LinuxControlFailureStage.ListenerWorkerIdentity, null, new IOException("wrapper-canary"), first);
        Parallel.For(0, 16, _ => process.Capture(LinuxControlFailureStage.ProcessDirectoryStat,
            null, new IOException("cleanup-canary")));
        Assert.Same(first, process.First);
        Assert.Same(first, listener.First);
        var root = EvidenceNativeObservationFailure.Capture(EvidenceNativeObservationPhase.ServerRun,
            new IOException("root-canary"), listener.First);
        Assert.Same(first, root.ControlFailure);
        Assert.DoesNotContain("canary", root.ToJson());
        Assert.True(Encoding.UTF8.GetByteCount(root.ToJson()) + 1 <= 1024);
    }

    [Fact]
    public void RetainedDirectorySubchecksKeepClosedShapeAndNeverRetainNativeValuesOrMessages()
    {
        var stages = new[]
        {
            LinuxControlFailureStage.ProcessFileSystemInspect, LinuxControlFailureStage.ProcessFileSystemType,
            LinuxControlFailureStage.ProcessDirectoryStat, LinuxControlFailureStage.ProcessDirectoryInode,
            LinuxControlFailureStage.ProcessDirectoryType, LinuxControlFailureStage.ProcessRetainedProcessDeviceMajor,
            LinuxControlFailureStage.ProcessRetainedProcessDeviceMinor, LinuxControlFailureStage.ProcessRetainedProcessInode,
            LinuxControlFailureStage.ProcessRetainedProcessUid, LinuxControlFailureStage.ProcessRetainedProcessGid,
            LinuxControlFailureStage.ProcessRetainedProcessMode, LinuxControlFailureStage.ProcessRetainedProcessMetadata,
        };
        var errors = new Exception[]
        {
            new IOException("/proc/private-canary"), new PlatformNotSupportedException("native-canary"),
            new CanaryException("private-canary", new IOException("inner-canary")),
        };
        foreach (var stage in stages)
        foreach (var error in errors)
        {
            var detail = LinuxControlFailure.Capture(stage, null, error);
            using var parsed = JsonDocument.Parse(detail.ToJson());
            Assert.Equal(new[] { "stage", "operation", "error_kind", "diagnostic_code" },
                parsed.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
            Assert.Equal(stage.ToString(), parsed.RootElement.GetProperty("stage").GetString());
            Assert.Null(detail.Operation);
            Assert.Null(detail.DiagnosticCode);
            var root = EvidenceNativeObservationFailure.Capture(EvidenceNativeObservationPhase.ServerRun, error, detail);
            Assert.True(Encoding.UTF8.GetByteCount(root.ToJson()) + 1 <= 1024);
            Assert.DoesNotContain("canary", root.ToJson());
            Assert.DoesNotContain("/proc/", root.ToJson());
        }
    }

    [Theory]
    [InlineData(0, (int)LinuxControlFailureStage.ProcessExpectedSample)]
    [InlineData(1, (int)LinuxControlFailureStage.ProcessExpectedPid)]
    [InlineData(2, (int)LinuxControlFailureStage.ProcessExpectedStartTime)]
    [InlineData(3, (int)LinuxControlFailureStage.ProcessExpectedLiveState)]
    [InlineData(4, (int)LinuxControlFailureStage.ProcessExpectedUid)]
    [InlineData(5, (int)LinuxControlFailureStage.ProcessExpectedGid)]
    [InlineData(6, (int)LinuxControlFailureStage.ProcessExpectedCgroup)]
    public void ActualExpectedSampleGuardRetainsItsExactProcessCheckpoint(int variant, int expected)
    {
        var unit = LinuxUnitName.Create(LinuxUnitRole.Worker, Guid.Parse("5e5d63d7-2ff6-4e21-922f-51c53ef18236"));
        var valid = new LinuxProcessSample(new(123, 456, 'S'), new(65010, 65010, 65010, 65010),
            new(65011, 65011, 65011, 65011), "/system.slice/" + unit.Value);
        var sample = variant switch
        {
            0 => null!,
            1 => valid with { Stat = valid.Stat with { Pid = 124 } },
            2 => valid with { Stat = valid.Stat with { StartTimeTicks = 0 } },
            3 => valid with { Stat = valid.Stat with { State = 'Z' } },
            4 => valid with { Uids = valid.Uids with { FileSystem = 65012 } },
            5 => valid with { Gids = valid.Gids with { Saved = 65012 } },
            _ => valid with { ControlGroup = "/private/canary-ASEVD410" },
        };
        var stage = LinuxControlFailureStage.Unknown;
        var error = Assert.Throws<EvidenceAdmissionException>(() =>
            LinuxProcessData.RequireExpected(sample, 123, 65010, 65011, unit, LinuxProcessSamplingRole.Worker, ref stage));
        Assert.Equal((LinuxControlFailureStage)expected, stage);
        var first = LinuxControlFailure.Capture(stage, null, error);
        Assert.Equal("ASEVD402", first.DiagnosticCode);
        Assert.Null(first.Operation);
        Assert.Equal(EvidenceNativeObservationErrorKind.Admission, first.ErrorKind);
        Assert.DoesNotContain("canary", first.ToJson());
        stage = LinuxControlFailureStage.Unknown;
        LinuxProcessData.RequireExpected(valid, 123, 65010, 65011, unit, LinuxProcessSamplingRole.Worker, ref stage);
        Assert.Equal(LinuxControlFailureStage.ProcessExpectedCgroup, stage);
        LinuxProcessData.RequireExpected(valid, 123, 65010, 65011, unit, LinuxProcessSamplingRole.Worker);
    }

    [Fact]
    public void ProcessSelectionRejectsBeforeSampleAndRetainsExistingOwnerRolePolicy()
    {
        var unit = LinuxUnitName.Create(LinuxUnitRole.Worker, Guid.Parse("5e5d63d7-2ff6-4e21-922f-51c53ef18236"));
        var stage = LinuxControlFailureStage.Unknown;
        Assert.Throws<EvidenceAdmissionException>(() =>
            LinuxProcessData.RequireExpected(null!, 123, 0, 0, unit, LinuxProcessSamplingRole.Worker, ref stage));
        Assert.Equal(LinuxControlFailureStage.ProcessSelection, stage);
        var owner = LinuxUnitName.Create(LinuxUnitRole.Owner, Guid.Parse("5e5d63d7-2ff6-4e21-922f-51c53ef18236"));
        var sample = new LinuxProcessSample(new(123, 456, 'R'), new(0, 0, 0, 0), new(0, 0, 0, 0), "/system.slice/" + owner.Value);
        LinuxProcessData.RequireExpected(sample, 123, 0, 0, owner, LinuxProcessSamplingRole.Owner, ref stage);
        Assert.Equal(LinuxControlFailureStage.ProcessExpectedCgroup, stage);
    }

    [Fact]
    public void FirstProcessFaultSurvivesListenerServerWrappersAndLaterConcurrentCleanup()
    {
        var error = Assert.Throws<EvidenceAdmissionException>(() => LinuxProcessData.ParseCgroup("canary"u8));
        var process = new LinuxControlFailureLatch();
        process.Capture(LinuxControlFailureStage.ProcessCgroupParse, null, error);
        var first = process.First;
        var listener = new LinuxControlFailureLatch();
        listener.Capture(LinuxControlFailureStage.ListenerWorkerIdentity, null, new IOException("wrapper-canary"), first);
        var server = new LinuxControlFailureLatch();
        server.Capture(LinuxControlFailureStage.Accept, null, new IOException("outer-canary"), listener.First);
        Parallel.For(0, 32, _ => process.Capture(LinuxControlFailureStage.ProcessState, null, new Exception("later-canary")));
        Assert.Same(first, process.First);
        Assert.Same(first, listener.First);
        Assert.Same(first, server.First);
        var root = EvidenceNativeObservationFailure.Capture(EvidenceNativeObservationPhase.ServerRun,
            new IOException("root-canary"), server.First);
        Assert.Same(first, root.ControlFailure);
        Assert.True(Encoding.UTF8.GetByteCount(root.ToJson()) + 1 <= 1024);
        Assert.DoesNotContain("canary", root.ToJson());
        var earlier = new LinuxControlFailureLatch();
        earlier.Capture(LinuxControlFailureStage.RequestRead, EvidenceControlOperation.Ready, new IOException("earlier"));
        var reserved = earlier.First;
        earlier.Capture(LinuxControlFailureStage.Accept, null, error, first);
        Assert.Same(reserved, earlier.First);
    }

    [Fact]
    public void EveryProcessCheckpointKeepsTheExistingFourFieldSchemaAndBoundedRootProjection()
    {
        var error = Assert.Throws<EvidenceAdmissionException>(() => LinuxProcessData.ParseCgroup("private-canary"u8));
        foreach (var stage in Enum.GetValues<LinuxControlFailureStage>().Where(value => value.ToString().StartsWith("Process", StringComparison.Ordinal)))
        {
            var detail = LinuxControlFailure.Capture(stage, null, error);
            using var parsed = JsonDocument.Parse(detail.ToJson());
            Assert.Equal(new[] { "stage", "operation", "error_kind", "diagnostic_code" },
                parsed.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
            Assert.Equal(stage.ToString(), parsed.RootElement.GetProperty("stage").GetString());
            Assert.Null(detail.Operation);
            var root = EvidenceNativeObservationFailure.Capture(EvidenceNativeObservationPhase.ServerRun, error, detail);
            Assert.True(Encoding.UTF8.GetByteCount(root.ToJson()) + 1 <= 1024);
            Assert.DoesNotContain("private-canary", root.ToJson());
        }
    }

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
    public void EveryClosedPhaseProducesExactlySevenBoundedFields()
    {
        foreach (var phase in Enum.GetValues<EvidenceNativeObservationPhase>())
        {
            var failure = EvidenceNativeObservationFailure.Capture(phase, new IOException("private-canary"));
            var json = failure.ToJson();
            Assert.True(Encoding.UTF8.GetByteCount(json) <= 1024);
            Assert.DoesNotContain("private-canary", json);
            using var parsed = JsonDocument.Parse(json);
            Assert.Equal(new[] { "schema", "phase", "error_kind", "diagnostic_code", "account_failure", "control_failure", "custody_failure" },
                parsed.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal("evidence-native-observation-failure-v4", parsed.RootElement.GetProperty("schema").GetString());
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
        Assert.Equal("evidence-native-observation-failure-v4", parsed.RootElement.GetProperty("schema").GetString());
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
