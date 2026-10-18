using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Inspects built image metadata without loading or accessing private native owners.</summary>
public sealed class QualificationImageBoundaryTests
{
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
        Assert.Contains("LinuxSlotNegativeObservation", names);
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

    /// <summary>Portable procedure controls stay available independently of any native fixture implementation.</summary>
    [Fact]
    public void PortableOrderingDataRemainsInEveryImage()
    {
        var names = ReadTypeNames();
        Assert.Contains("LinuxN04CheckpointOrder", names);
        Assert.Contains("LinuxN07CheckpointOrder", names);
        Assert.Contains("LinuxEmptyObservationControlServer", names);
        Assert.Contains("LinuxWorkerProcess", names);
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
