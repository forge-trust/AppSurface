using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using ForgeTrust.AppSurface.Aspire;

namespace ProductReadinessLabAppHost;

/// <summary>
/// Adds the local Postgres database used for product/domain state.
/// </summary>
public sealed class ProductReadinessPostgresComponent : IAspireComponent<PostgresDatabaseResource>
{
    /// <inheritdoc />
    public IResourceBuilder<PostgresDatabaseResource> Generate(
        AspireStartupContext context,
        IDistributedApplicationBuilder appBuilder)
    {
        _ = context;
        var server = appBuilder.AddPostgres("product-readiness-postgres");
        return server.AddDatabase("ProductReadiness");
    }
}

/// <summary>
/// Adds the product-readiness lab web app and connects it to local Postgres.
/// </summary>
public sealed class ProductReadinessWebComponent : IAspireComponent<ProjectResource>
{
    private readonly ProductReadinessPostgresComponent _postgres;

    /// <summary>
    /// Creates the web component.
    /// </summary>
    /// <param name="postgres">Postgres component that supplies product/domain state.</param>
    public ProductReadinessWebComponent(ProductReadinessPostgresComponent postgres)
    {
        _postgres = postgres;
    }

    /// <inheritdoc />
    public IResourceBuilder<ProjectResource> Generate(
        AspireStartupContext context,
        IDistributedApplicationBuilder appBuilder)
    {
        var database = context.Resolve(_postgres);
        return appBuilder
            .AddProject<Projects.ProductReadinessLab>("product-readiness-lab")
            .WithReference(database)
            .WaitFor(database)
            .WithEnvironment("PRODUCT_READINESS_ENABLE_PROOF_AUTH", "true");
    }
}
