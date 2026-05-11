namespace ForgeTrust.RazorWire.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RazorWireIntegrationCollection : ICollectionFixture<RazorWireMvcPlaywrightFixture>
{
    public const string Name = "RazorWireIntegrationCollection";
}
