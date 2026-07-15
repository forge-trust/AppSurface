namespace ForgeTrust.AppSurface.Durable.Tests;

public sealed class PublicApiBaselineTests
{
    [Fact]
    public void Durable_production_assembly_does_not_reference_provider()
    {
        Assert.DoesNotContain(
            typeof(AppSurfaceDurableModule).Assembly.GetReferencedAssemblies(),
            reference => string.Equals(reference.Name, "ForgeTrust.AppSurface.Durable.Provider", StringComparison.Ordinal));
    }

    [Fact]
    public void Durable_public_types_match_reviewed_baseline()
    {
        var actual = typeof(AppSurfaceDurableModule).Assembly
            .GetExportedTypes()
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Durable.PublicAPI.Shipped.txt"));

        Assert.Equal(expected, actual);
    }
}
