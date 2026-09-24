using ForgeTrust.AppSurface.Config;
using Xunit;

namespace ForgeTrust.AppSurface.Config.GoogleSecretManager.Tests;

public sealed class GoogleSecretManagerMigrationInventoryLegacyPrefixTests
{
    [Fact]
    public void Inventory_Should_PreserveTrailingColonBoundaryAndAllowEmptyLegacyIdPrefix()
    {
        var options = new AppSurfaceGoogleSecretManagerOptions { ProjectId = "project" };
        options.EnableConventionResolver("TenantA", "current-");

        var entries = AppSurfaceGoogleSecretMigrationInventory.Inventory(options,
            [AppSurfaceConfigKey.Parse("TenantA:Key"), AppSurfaceConfigKey.Parse("TenantAB:Key")], "TenantA:", string.Empty);

        var entry = Assert.Single(entries);
        Assert.Equal("TenantA:Key", entry.LogicalKey.Value);
        Assert.Equal("key", entry.LegacySecretId);
        Assert.Equal("current-tenanta--key", entry.NewSecretId);
    }

    [Fact]
    public void Inventory_Should_ScopeLexicalLegacyPrefixToCurrentConventionBoundary()
    {
        var options = new AppSurfaceGoogleSecretManagerOptions { ProjectId = "project" };
        options.EnableConventionResolver("TenantA", "current-");

        var entries = AppSurfaceGoogleSecretMigrationInventory.Inventory(options,
            [AppSurfaceConfigKey.Parse("TenantA:Key"), AppSurfaceConfigKey.Parse("TenantAB:Key")], "TenantA", "old-");

        var entry = Assert.Single(entries);
        Assert.Equal("TenantA:Key", entry.LogicalKey.Value);
        Assert.Equal("old--key", entry.LegacySecretId);
    }

    [Fact]
    public void Inventory_Should_SelectCurrentConventionBeforeApplyingRawLegacyPrefix()
    {
        var options = new AppSurfaceGoogleSecretManagerOptions { ProjectId = "project" };
        options.EnableConventionResolver("TenantA", "current-", "5");
        options.EnableConventionResolver("TenantB", "current-", "7");

        var entries = AppSurfaceGoogleSecretMigrationInventory.Inventory(options,
            [AppSurfaceConfigKey.Parse("TenantB:Key")], "Tenant", "old-");

        var entry = Assert.Single(entries);
        Assert.Equal("TenantB:Key", entry.LogicalKey.Value);
        Assert.Equal("7", entry.Version);
        Assert.Equal("current-tenantb--key", entry.NewSecretId);
    }

    [Fact]
    public void Inventory_Should_RejectEmptyRawLegacyKeyPrefix()
    {
        var options = new AppSurfaceGoogleSecretManagerOptions { ProjectId = "project" };
        options.EnableConventionResolver("TenantA", "current-");

        Assert.Throws<ArgumentException>(() => AppSurfaceGoogleSecretMigrationInventory.Inventory(options,
            [AppSurfaceConfigKey.Parse("TenantA:Key")], string.Empty, string.Empty));
    }
}
