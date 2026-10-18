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
}
