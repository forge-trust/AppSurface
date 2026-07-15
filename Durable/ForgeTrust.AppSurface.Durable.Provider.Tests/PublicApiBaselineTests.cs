using ForgeTrust.AppSurface.Durable.Provider;

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
        var actual = typeof(IDurableRuntimePump).Assembly
            .GetExportedTypes()
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Provider.PublicAPI.Shipped.txt"));

        Assert.Equal(expected, actual);
    }
}
