using ForgeTrust.AppSurface.Durable.Provider;
using ForgeTrust.AppSurface.Durable.Tests.Support;

namespace ForgeTrust.AppSurface.Durable.Provider.Tests;

public sealed class PublicApiBaselineTests
{
    [Fact]
    public void Provider_production_assembly_references_durable_contracts()
    {
        Assert.Contains(
            typeof(IDurableRuntimePump).Assembly.GetReferencedAssemblies(),
            reference => string.Equals(reference.Name, "ForgeTrust.AppSurface.Durable", StringComparison.Ordinal));
    }

    [Fact]
    public void Provider_public_types_match_reviewed_baseline()
    {
        PublicApiSnapshot.AssertMatches(
            typeof(IDurableRuntimePump).Assembly,
            "Provider.PublicAPI.Shipped.txt",
            "Durable/ForgeTrust.AppSurface.Durable.Provider/PublicAPI.Shipped.txt");
    }
}
