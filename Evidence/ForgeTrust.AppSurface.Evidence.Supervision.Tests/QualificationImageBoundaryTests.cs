using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Inspects built image metadata without loading or accessing private native owners.</summary>
public sealed class QualificationImageBoundaryTests
{
    /// <summary>Shared async operations keep the same compiler identities across private image selections.</summary>
    /// <remarks>This inspects actual emitted metadata; it neither invokes an owner nor establishes native authority.</remarks>
    [Fact]
    public void CommonAsyncOperationsPrecedeSelectorOnlyMembersInEmittedMetadata()
    {
        RequireAsyncIdentities("LinuxWorkerProcess",
        [
            "JoinPhysicalCleanupAsync", "StartCoreAsync", "MonitorExitAsync", "ContainAsync",
            "StopSelectedUnitAsync", "FinalizeAsync", "DisposeCoreAsync", "JoinOriginalExitMonitorAsync",
        ]);
        RequireAsyncIdentities("LinuxEmptyObservationControlServer",
        [
            "RunOwnedAsync", "RunCoreAsync", "HandleRegisteredAsync", "HandleAsync", "StopEmptyDescendantsAsync",
        ]);
        RequireAsyncIdentities("LinuxEmptyObservationExecution", ["RunAsync"]);
#if EVIDENCE_PRIVATE_ACCEPTED_WORK
        RequireAsyncIdentities("LinuxEmptyObservationControlServer",
            ["StopAndJoinN16WorkAsync", "RunN16BlockedWorkAsync"], firstOrdinal: 5);
#endif
#if EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09
        RequireAsyncIdentities("LinuxEmptyObservationControlServer", ["SignalCancellationOwnedAsync"], firstOrdinal: 5);
#endif
        // Framing identities are common across images; accepted-work follows them only in its selected image.
#if EVIDENCE_PRIVATE_ACCEPTED_WORK
        RequireAsyncIdentities("SupervisionControlLineFraming",
        [
            "ReadRequestAsync", "WritePhaseAsync", "WriteResponseAsync", "DisposeAsync",
            "WriteAcceptedBlockedWorkAsync",
        ]);
#else
        RequireAsyncIdentities("SupervisionControlLineFraming",
        [
            "ReadRequestAsync", "WritePhaseAsync", "WriteResponseAsync", "DisposeAsync",
        ]);
#endif
    }

    private static void RequireAsyncIdentities(string owner, IReadOnlyList<string> methods, int firstOrdinal = 0)
    {
        using var file = File.OpenRead(Path.Join(AppContext.BaseDirectory, "ForgeTrust.AppSurface.Evidence.Supervision.dll"));
        using var image = new PEReader(file);
        var metadata = image.GetMetadataReader();
        var names = new List<string>();
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            var declaring = type.GetDeclaringType();
            if (!declaring.IsNil && metadata.GetString(metadata.GetTypeDefinition(declaring).Name) == owner)
                names.Add(metadata.GetString(type.Name));
        }

        for (var index = 0; index < methods.Count; index++)
        {
            var prefix = $"<{methods[index]}>d__";
            Assert.Equal([prefix + (firstOrdinal + index)], names.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)));
        }
    }

    /// <summary>Only the selected native rendezvous implementation is physically present in the image.</summary>
    [Fact]
    public void NativeRendezvousImplementationsMatchTheCompiledSelector()
    {
        var names = ReadTypeNames();
        var references = ReadMethodReferences();
#if EVIDENCE_PRIVATE_N04
        Assert.Contains("LinuxN04CheckpointOwner", names);
        Assert.Contains("get_PeerReplacementEnabled", references);
#else
        Assert.DoesNotContain("LinuxN04CheckpointOwner", names);
        Assert.DoesNotContain("get_PeerReplacementEnabled", references);
#endif
#if EVIDENCE_PRIVATE_N07
        Assert.Contains("LinuxN07CheckpointOwner", names);
        Assert.Contains("get_ParentReplacementEnabled", references);
#else
        Assert.DoesNotContain("LinuxN07CheckpointOwner", names);
        Assert.DoesNotContain("get_ParentReplacementEnabled", references);
#endif
    }

    /// <summary>Private allocation mutation and retained-allocation fixtures are absent from other images.</summary>
    [Fact]
    public void PrivateSlotHoldersMatchTheirSelectedImages()
    {
        var names = ReadTypeNames();
#if EVIDENCE_PRIVATE_N05 || EVIDENCE_PRIVATE_N06
        Assert.Contains("SlotNegativeFixture", names);
#else
        Assert.DoesNotContain("SlotNegativeFixture", names);
#endif
#if EVIDENCE_PRIVATE_N09
        Assert.Contains("N09AllocationSlot", names);
#else
        Assert.DoesNotContain("N09AllocationSlot", names);
#endif
        Assert.Contains("LinuxN09AllocationObservation", names);
#if EVIDENCE_PRIVATE_N05 || EVIDENCE_PRIVATE_N06
        Assert.Contains("LinuxSlotNegativeObservation", names);
#else
        Assert.DoesNotContain("LinuxSlotNegativeObservation", names);
#endif
    }

    /// <summary>Private pending-start and descendant implementations follow their exact image selectors.</summary>
    [Fact]
    public void N10AndN12ImplementationsMatchTheirCompiledSelectors()
    {
        var types = ReadTypeNames();
        var methods = ReadMethodDefinitions();
#if EVIDENCE_PRIVATE_N10
        Assert.Contains("LinuxN10PendingStartCheckpoint", types);
        Assert.Contains("LinuxN10PendingStartCheckpoint.WaitAfterStartReplyAsync", methods);
        Assert.Contains("LinuxN10PendingStartCheckpoint.RecordStartReply", methods);
        Assert.Contains("LinuxN10PendingStartCheckpoint.EncodeAfterJoin", methods);
        Assert.Contains("LinuxWorkerProcess.CaptureN10PendingStartObservation", methods);
#else
        Assert.DoesNotContain("LinuxN10PendingStartCheckpoint", types);
        Assert.DoesNotContain("LinuxN10PendingStartCheckpoint.WaitAfterStartReplyAsync", methods);
        Assert.DoesNotContain("LinuxN10PendingStartCheckpoint.RecordStartReply", methods);
        Assert.DoesNotContain("LinuxN10PendingStartCheckpoint.EncodeAfterJoin", methods);
        Assert.DoesNotContain("LinuxWorkerProcess.CaptureN10PendingStartObservation", methods);
#endif
#if EVIDENCE_PRIVATE_N12
        Assert.Contains("LinuxN12Descendant.StartAfterWorkerAsync", methods);
        Assert.Contains("LinuxN12Descendant.StartCoreAsync", methods);
        Assert.Contains("LinuxN12Descendant.HoldOutputAsync", methods);
        Assert.Contains("LinuxN12Descendant.HoldCoreAsync", methods);
        Assert.Contains("LinuxN12Descendant.RequirePrivatePlatform", methods);
        Assert.Contains("LinuxWorkerProcess.ObserveLeaderExitWithDescendantAsync", methods);
        Assert.Contains("LinuxWorkerProcess.CaptureJoinedDescendantObservation", methods);
#else
        Assert.DoesNotContain("LinuxN12Descendant.StartAfterWorkerAsync", methods);
        Assert.DoesNotContain("LinuxN12Descendant.StartCoreAsync", methods);
        Assert.DoesNotContain("LinuxN12Descendant.HoldOutputAsync", methods);
        Assert.DoesNotContain("LinuxN12Descendant.HoldCoreAsync", methods);
        Assert.DoesNotContain("LinuxN12Descendant.RequirePrivatePlatform", methods);
        Assert.DoesNotContain("LinuxWorkerProcess.ObserveLeaderExitWithDescendantAsync", methods);
        Assert.DoesNotContain("LinuxWorkerProcess.CaptureJoinedDescendantObservation", methods);
#endif
        Assert.Contains("LinuxN12Descendant.ParsePid", methods);
        Assert.Contains("LinuxN12Descendant.CreateStartInfo", methods);
        Assert.Contains("LinuxN12Descendant.ReadReadyAsync", methods);
        Assert.Contains("LinuxN12Descendant.ReadReadyCoreAsync", methods);
        Assert.Contains("SupervisionDescendantObservation.Feed", methods);
        Assert.Contains("SupervisionDescendantObservation.Complete", methods);
        Assert.Contains("LinuxN12LeaderExitObservation", types);
    }

    /// <summary>Native cancellation and failed-settlement capture procedures exist only in their private images.</summary>
    [Fact]
    public void PrivateCancellationSignalAndObservationImplementationsMatchTheirImages()
    {
        var methods = ReadMethodDefinitions();
        var types = ReadTypeNames();
#if EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09
        Assert.Contains("LinuxOriginalCancellationSignal", types);
        Assert.Contains("LinuxEmptyObservationControlServer.SignalCancellationOwnedAsync", methods);
        Assert.Contains("LinuxEmptyObservationControlServer.CaptureCancellationSignal", methods);
        Assert.Contains("LinuxWorkerProcess.CaptureCancellationJoinedObservation", methods);
        Assert.Contains("LinuxWorkerProcess.CaptureJoinedOutputDiagnostic", methods);
#else
        Assert.DoesNotContain("LinuxOriginalCancellationSignal", types);
        Assert.DoesNotContain("LinuxEmptyObservationControlServer.SignalCancellationOwnedAsync", methods);
        Assert.DoesNotContain("LinuxEmptyObservationControlServer.CaptureCancellationSignal", methods);
        Assert.DoesNotContain("LinuxWorkerProcess.CaptureCancellationJoinedObservation", methods);
        Assert.DoesNotContain("LinuxWorkerProcess.CaptureJoinedOutputDiagnostic", methods);
#endif
#if EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09 || EVIDENCE_PRIVATE_N11
        Assert.Contains("LinuxWorkerProcess.CaptureFailedSettlementObservation", methods);
#else
        Assert.DoesNotContain("LinuxWorkerProcess.CaptureFailedSettlementObservation", methods);
#endif
        Assert.Contains("Qualification.ObserveStopJoinAsync", methods);
#if EVIDENCE_PRIVATE_N11
        Assert.Contains("Qualification.ObserveStopJoinActualAsync", methods);
#else
        Assert.DoesNotContain("Qualification.ObserveStopJoinActualAsync", methods);
#endif
#if EVIDENCE_PRIVATE_N04 || EVIDENCE_PRIVATE_N05 || EVIDENCE_PRIVATE_N06 || EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09 || EVIDENCE_PRIVATE_ACCEPTED_WORK
        Assert.Contains("LinuxWorkerProcess.CaptureNegativeObservation", methods);
#else
        Assert.DoesNotContain("LinuxWorkerProcess.CaptureNegativeObservation", methods);
#endif
#if EVIDENCE_PRIVATE_N07 || EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09 || EVIDENCE_PRIVATE_N11
        Assert.Contains("LinuxFailedSettlementObservation", types);
        Assert.Contains("LinuxWorkerStopTiming", types);
#else
        Assert.DoesNotContain("LinuxFailedSettlementObservation", types);
        Assert.DoesNotContain("LinuxWorkerStopTiming", types);
#endif
#if EVIDENCE_PRIVATE_N04 || EVIDENCE_PRIVATE_N05 || EVIDENCE_PRIVATE_N06 || EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09 || EVIDENCE_PRIVATE_ACCEPTED_WORK
        Assert.Contains("LinuxNegativeKernelObservation", types);
#else
        Assert.DoesNotContain("LinuxNegativeKernelObservation", types);
#endif
#if EVIDENCE_PRIVATE_N08 || EVIDENCE_PRIVATE_N09
        Assert.Contains("LinuxJoinedWorkerOutputDiagnostic", types);
#else
        Assert.DoesNotContain("LinuxJoinedWorkerOutputDiagnostic", types);
#endif
    }

    /// <summary>Shared owners remain in every image; private ordering data exists only in its selected runtime image.</summary>
    [Fact]
    public void SharedOwnersRemainAndPrivateOrderingDataMatchesItsImage()
    {
        var names = ReadTypeNames();
#if EVIDENCE_PRIVATE_N04
        Assert.Contains("LinuxN04CheckpointOrder", names);
#else
        Assert.DoesNotContain("LinuxN04CheckpointOrder", names);
#endif
#if EVIDENCE_PRIVATE_N07
        Assert.Contains("LinuxN07CheckpointOrder", names);
#else
        Assert.DoesNotContain("LinuxN07CheckpointOrder", names);
#endif
        Assert.Contains("LinuxEmptyObservationControlServer", names);
        Assert.Contains("LinuxWorkerProcess", names);
    }

    /// <summary>Raw tracker capture is validation-only and cannot ship in the ordinary C# owner.</summary>
    [Fact]
    public void PrivateWorkerMeasurementIsAbsentFromOrdinaryImages()
    {
        var types = ReadTypeNames();
#if EVIDENCE_PRIVATE_C3
        Assert.Contains("LinuxWorkerMeasurement", types);
        Assert.Contains("LinuxWorkerMeasurement.CheckOwnerReferenceGuards", ReadMethodDefinitions());
        Assert.Equal(new[] { "ForgeTrust.AppSurface.Cli", "ForgeTrust.AppSurface.Evidence.Contracts",
            "ForgeTrust.AppSurface.Evidence.Planner", "ForgeTrust.AppSurface.Evidence.Coverage",
            "ForgeTrust.AppSurface.Evidence.Aspire", "ForgeTrust.AppSurface.Evidence.Supervision",
            "ForgeTrust.AppSurface.Evidence.Cli" }, LinuxWorkerMeasurement.Modules);
#else
        Assert.DoesNotContain("LinuxWorkerMeasurement", types);
        Assert.DoesNotContain("LinuxWorkerMeasurement.CheckOwnerReferenceGuards", ReadMethodDefinitions());
#endif
    }

    /// <summary>The account fixtures and their collision hooks are absent from every ordinary product image.</summary>
    [Fact]
    public void PrivateAccountMechanismTypesAndHooksMatchOnlyTheirCompiledImage()
    {
        var types = ReadTypeNames();
        var methods = ReadMethodDefinitions();
#if EVIDENCE_PRIVATE_C4_ACCOUNTS
        Assert.Contains("LinuxAccountMeasurement", types);
        Assert.Contains("LinuxAccountMeasurementState", types);
        Assert.Contains("LinuxRunAccounts.CaptureAccountMeasurementState", methods);
        Assert.Contains("LinuxRunAccountNss.RequireAccountMeasurementNumbersAbsentAsync", methods);
#else
        Assert.DoesNotContain("LinuxAccountMeasurement", types);
        Assert.DoesNotContain("LinuxAccountMeasurementState", types);
        Assert.DoesNotContain("LinuxRunAccounts.CaptureAccountMeasurementState", methods);
        Assert.DoesNotContain("LinuxRunAccountNss.RequireAccountMeasurementNumbersAbsentAsync", methods);
#endif
#if EVIDENCE_PRIVATE_C4_ACCOUNT_ROLLBACK
        Assert.Contains("LinuxRunAccounts.PrepareAccountMeasurementCollisionAsync", methods);
        Assert.Contains("LinuxRunAccounts.RecordAccountMeasurementRollback", methods);
#else
        Assert.DoesNotContain("LinuxRunAccounts.PrepareAccountMeasurementCollisionAsync", methods);
        Assert.DoesNotContain("LinuxRunAccounts.RecordAccountMeasurementRollback", methods);
#endif
    }

    private static HashSet<string> ReadMethodReferences()
    {
        using var file = File.OpenRead(Path.Join(AppContext.BaseDirectory, "ForgeTrust.AppSurface.Evidence.Supervision.dll"));
        using var image = new PEReader(file);
        var metadata = image.GetMetadataReader();
        return metadata.MemberReferences.Select(handle => metadata.GetString(metadata.GetMemberReference(handle).Name))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> ReadTypeNames()
    {
        using var file = File.OpenRead(Path.Join(AppContext.BaseDirectory, "ForgeTrust.AppSurface.Evidence.Supervision.dll"));
        using var image = new PEReader(file);
        var metadata = image.GetMetadataReader();
        return metadata.TypeDefinitions.Select(handle => metadata.GetString(metadata.GetTypeDefinition(handle).Name))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> ReadMethodDefinitions()
    {
        using var file = File.OpenRead(Path.Join(AppContext.BaseDirectory, "ForgeTrust.AppSurface.Evidence.Supervision.dll"));
        using var image = new PEReader(file);
        var metadata = image.GetMetadataReader();
        var methods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var typeHandle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(typeHandle);
            var typeName = metadata.GetString(type.Name);
            foreach (var methodHandle in type.GetMethods())
                methods.Add($"{typeName}.{metadata.GetString(metadata.GetMethodDefinition(methodHandle).Name)}");
        }
        return methods;
    }
}
