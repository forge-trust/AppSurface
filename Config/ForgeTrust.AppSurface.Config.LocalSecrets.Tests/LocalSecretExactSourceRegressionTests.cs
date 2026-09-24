using static ForgeTrust.AppSurface.Config.LocalSecrets.PlatformAppSurfaceLocalSecretStore;

namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

public sealed class LocalSecretExactSourceRegressionTests
{
    [Fact]
    public void MacLegacyMigration_WithIndexedPlaceholderKey_CopiesOnlyTheExactSource()
    {
        var application = "ExactSource" + Guid.NewGuid().ToString("N");
        var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
        var source = normalizer.Normalize(application, "Development", null, "Legacy.Key").Identity!;
        var unrelated = normalizer.Normalize(application, "Development", null, "__migration_source__").Identity!;
        var destination = normalizer.Normalize(application, "Development", null, "Moved:Key").Identity!;
        const string sourceValue = "synthetic-exact-source-value";
        const string unrelatedValue = "synthetic-unrelated-value";
        var legacy = new ExactIndexedLegacyStore();

        // Exercise the production indexed lookup: the placeholder is an ordinary, independently stored user key.
        Assert.Equal(LocalSecretResultStatus.Found, legacy.Set(source, sourceValue).Status);
        Assert.Equal(LocalSecretResultStatus.Found, legacy.Set(unrelated, unrelatedValue).Status);
        Assert.Equal(sourceValue, legacy.Get(source).Value);
        Assert.Equal(unrelatedValue, legacy.Get(unrelated).Value);
        var store = new MacOsV2CompatibilityLocalSecretStore(legacy, new IsolatedMacInterop());

        var result = store.MigrateKey(application, "Development", null, source.StorageName, destination.Key);

        var copied = store.Get(destination);
        var remainingSource = legacy.Get(source);
        var remainingUnrelated = legacy.Get(unrelated);
        Assert.True(copied.Value == sourceValue,
            $"The destination must contain the exact source value. Matched unrelated indexed key: {copied.Value == unrelatedValue}; "
            + $"requested source status: {remainingSource.Status}; migration status: {result.Status}; diagnostic: {result.Diagnostic?.Code}.");
        Assert.Equal(LocalSecretResultStatus.Found, result.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.Complete, result.State);
        Assert.Equal(LocalSecretResultStatus.Missing, remainingSource.Status);
        Assert.Equal(unrelatedValue, remainingUnrelated.Value);
        Assert.Equal(new[] { unrelated.Key.Value }, legacy.List(application, "Development", null).Keys);
    }

    /// <summary>Retains the production indexed policy while isolating exact native records in memory.</summary>
    private sealed class ExactIndexedLegacyStore : IndexedLocalSecretStore
    {
        private readonly Dictionary<string, string> _records = new(StringComparer.Ordinal);

        public override string Name => "Exact indexed legacy test store";

        protected override AppSurfaceLocalSecretResult ReadStoredValue(AppSurfaceLocalSecretIdentity identity) =>
            _records.TryGetValue(identity.StorageName, out var value)
                ? AppSurfaceLocalSecretResult.Found(value, Name)
                : AppSurfaceLocalSecretResult.Missing(Name);

        protected override AppSurfaceLocalSecretResult WriteStoredValue(AppSurfaceLocalSecretIdentity identity, string value)
        {
            _records[identity.StorageName] = value;
            return AppSurfaceLocalSecretResult.Found(string.Empty, Name);
        }

        protected override AppSurfaceLocalSecretResult DeleteStoredValue(AppSurfaceLocalSecretIdentity identity) =>
            _records.Remove(identity.StorageName)
                ? AppSurfaceLocalSecretResult.Found(string.Empty, Name)
                : AppSurfaceLocalSecretResult.Missing(Name);

        protected override AppSurfaceLocalSecretResult DoctorStore(string applicationName, string environment, string? keyPrefix) =>
            AppSurfaceLocalSecretResult.Found(string.Empty, Name);
    }

    /// <summary>Models native item identity without invoking Security.framework or accessing OS credentials.</summary>
    private sealed class IsolatedMacInterop : IMacOsSecItemInterop
    {
        private readonly Dictionary<MacOsSecItemQuery, byte[]> _records = new();

        public MacOsSecItemReadResult Read(MacOsSecItemQuery query) =>
            _records.TryGetValue(query, out var value) ? new(0, value.ToArray()) : new(-25300, null);

        public int Exists(MacOsSecItemQuery query) => _records.ContainsKey(query) ? 0 : -25300;

        public int Add(MacOsSecItemQuery query, byte[] value) =>
            _records.TryAdd(query, value.ToArray()) ? 0 : -25299;

        public int Update(MacOsSecItemQuery query, byte[] value)
        {
            if (!_records.ContainsKey(query)) return -25300;
            _records[query] = value.ToArray();
            return 0;
        }

        public int Delete(MacOsSecItemQuery query) => _records.Remove(query) ? 0 : -25300;
    }
}
