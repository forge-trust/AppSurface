using ForgeTrust.AppSurface.Durable.Tests.Support;

namespace ForgeTrust.AppSurface.Durable.Testing.Tests;

public sealed class PublicApiBaselineTests
{
    [Fact]
    public void Testing_public_types_match_reviewed_baseline()
    {
        PublicApiSnapshot.AssertMatches(
            typeof(DurableHostScenario).Assembly,
            "Testing.PublicAPI.Shipped.txt",
            "Durable/ForgeTrust.AppSurface.Durable.Testing/PublicAPI.Shipped.txt");
    }
}
