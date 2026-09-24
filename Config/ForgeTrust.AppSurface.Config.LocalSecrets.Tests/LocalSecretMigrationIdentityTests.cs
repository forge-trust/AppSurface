namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

public sealed class LocalSecretMigrationIdentityTests
{
    [Theory]
    [InlineData(false, null)]
    [InlineData(false, "")]
    [InlineData(false, "Prefix")]
    [InlineData(true, null)]
    [InlineData(true, "")]
    [InlineData(true, "Prefix")]
    public void Resolve_PreservesExactHistoricalSuffixWithoutLogicalParsing(bool v2, string? prefix)
    {
        const string suffix = " legacy..key__\\component: ";
        var envelope = Envelope(v2, prefix);
        var identity = LocalSecretMigrationIdentity.Resolve("App", "Development", prefix, envelope + suffix, v2);
        Assert.NotNull(identity);
        Assert.Equal(envelope + suffix, identity.StorageName);
        Assert.Equal(suffix, identity.StoredKey);
        Assert.Equal("__migration_source__", identity.Key.Value);
        Assert.False(AppSurfaceConfigKey.TryParse(suffix, out _));
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(false, "__appsurface_index__")]
    [InlineData(false, "__appsurface_doctor__")]
    [InlineData(true, "")]
    [InlineData(true, "__appsurface_index__")]
    [InlineData(true, "__appsurface_doctor__")]
    public void Resolve_RejectsEmptyAndReservedNativeSuffixes(bool v2, string suffix) =>
        Assert.Null(LocalSecretMigrationIdentity.Resolve("App", "Development", "Prefix", Envelope(v2, "Prefix") + suffix, v2));

    [Theory]
    [InlineData(false, "OtherApp", "Development", "Prefix")]
    [InlineData(false, "App", "Production", "Prefix")]
    [InlineData(false, "App", "Development", "OtherPrefix")]
    [InlineData(true, "OtherApp", "Development", "Prefix")]
    [InlineData(true, "App", "Production", "Prefix")]
    [InlineData(true, "App", "Development", "OtherPrefix")]
    public void Resolve_RejectsAnIdentifierOutsideTheExactNamespace(bool v2, string app, string environment, string prefix) =>
        Assert.Null(LocalSecretMigrationIdentity.Resolve(app, environment, prefix, Envelope(v2, "Prefix") + "Key", v2));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_RejectsTheOtherStorageVersion(bool v2) =>
        Assert.Null(LocalSecretMigrationIdentity.Resolve("App", "Development", null, Envelope(!v2, null) + "Key", v2));

    private static string Envelope(bool v2, string? prefix) => v2
        ? $"appsurface:v2:App:Development:{prefix}:"
        : "appsurface:App:Development:" + (string.IsNullOrEmpty(prefix) ? "" : prefix + ":");
}
