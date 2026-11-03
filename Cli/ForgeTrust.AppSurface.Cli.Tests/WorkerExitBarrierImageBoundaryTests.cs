#if !EVIDENCE_PRIVATE_C3_SDK
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Inspects ordinary CLI metadata without loading any measurement helper or native owner.</summary>
public sealed class WorkerExitBarrierImageBoundaryTests
{
    /// <summary>The ordinary CLI cannot reference or contain the optional SDK exit implementation.</summary>
    [Fact]
    public void OrdinaryCliDoesNotReferenceOrContainWorkerExitSupport()
    {
        using var file = File.OpenRead(Path.Join(AppContext.BaseDirectory, "ForgeTrust.AppSurface.Cli.dll"));
        using var image = new PEReader(file);
        var reader = image.GetMetadataReader();
        Assert.DoesNotContain(reader.AssemblyReferences, handle => reader.GetString(reader.GetAssemblyReference(handle).Name)
            == "ForgeTrust.AppSurface.Evidence.ExitBarrier.Support");
        Assert.DoesNotContain(reader.TypeReferences, handle => reader.GetString(reader.GetTypeReference(handle).Namespace)
            == "ForgeTrust.AppSurface.Evidence.ExitBarrier.Support");
        Assert.DoesNotContain(reader.TypeDefinitions, handle => reader.GetString(reader.GetTypeDefinition(handle).Name)
            == "WorkerExitBarrier");
    }
}
#endif
