using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceResourceGraphTests
{
    [Fact]
    public void GetOrderedResources_PreservesOrdinalRootsAndDeclaredDependencyOrder()
    {
        var graph = new EvidenceResourceGraph(
        [
            new("z", "completion", 30, []),
            new("a", "completion", 30, ["c", "b"]),
            new("b", "completion", 30, ["d"]),
            new("c", "completion", 30, ["d"]),
            new("d", "completion", 30, []),
        ]);

        Assert.Equal(["d", "c", "b", "a", "z"], graph.GetOrderedResources().Select(static resource => resource.Id));
    }

    [Fact]
    public void ValidatePreparation_RejectsMissingReferenceInUnusedResource()
    {
        var graph = new EvidenceResourceGraph(
        [new EvidenceResourceDeclaration("unused", "completion", 30, ["missing"])]);

        var failure = Assert.Throws<EvidenceHostException>(() => graph.ValidatePreparation([]));

        Assert.Equal("ASEVD304", failure.Code);
        Assert.Contains("missing a dependency or contains a cycle", failure.Message, StringComparison.Ordinal);
    }
}
