using System.Text;
using System.Text.Json;
using static ForgeTrust.AppSurface.Config.LocalSecrets.PlatformAppSurfaceLocalSecretStore;

namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

public sealed class LocalSecretPlatformMigrationTests
{
    [Fact]
    public void MacLegacyMigrationAndIndexRepair_ShouldShareTheWriterLease()
    {
        var app = "App" + Guid.NewGuid().ToString("N");
        var probe = new LeaseProbe(app);
        var raw = new NativeMemoryStore(probe);
        var legacy = new IndexedFake(raw);
        var source = new AppSurfaceLocalSecretIdentityNormalizer().Normalize(app, "Development", null, "Legacy.Key").Identity!;
        Assert.Equal(LocalSecretResultStatus.Found, legacy.Set(source, "retained-marker").Status);
        var mac = new MacOsV2CompatibilityLocalSecretStore(legacy, new MacInterop(probe));
        Assert.Equal(LocalSecretResultStatus.Found, mac.Migrate(app, "Development", null).Status);
        var result = mac.MigrateKey(app, "Development", null, source.StorageName, AppSurfaceConfigKey.Parse("Moved:Key"));
        Assert.Equal(LocalSecretResultStatus.Found, result.Status);
        Assert.Equal(LocalSecretResultStatus.Missing, legacy.Get(source).Status);
        Assert.Equal(new[] { "Legacy.Key", "Moved:Key" }, mac.List(app, "Development", null).Keys);
    }

    [Fact]
    public void UnavailableMigrationStateDirectory_ShouldReturnStructuredFailureBeforePrepared()
    {
        var raw = new NativeMemoryStore(new LeaseProbe("App"));
        var store = new BadStateStore(raw);
        var result = store.MigrateKey("App", "Development", null, "appsurface:App:Development:Legacy.Key", AppSurfaceConfigKey.Parse("Moved:Key"));
        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal("local-secret-migration-io-failure", result.Diagnostic?.Code);
        Assert.Equal(0, raw.Mutations);
    }

    private sealed class BadStateStore(NativeMemoryStore raw) : IndexedLocalSecretStore
    {
        public override string Name => "BadState";
        internal override string MigrationStateDirectory => throw new IOException("No current user state directory");
        protected override AppSurfaceLocalSecretResult ReadStoredValue(AppSurfaceLocalSecretIdentity identity) => raw.Get(identity);
        protected override AppSurfaceLocalSecretResult WriteStoredValue(AppSurfaceLocalSecretIdentity identity, string value) => raw.Set(identity, value);
        protected override AppSurfaceLocalSecretResult DeleteStoredValue(AppSurfaceLocalSecretIdentity identity) => raw.Delete(identity);
        protected override AppSurfaceLocalSecretResult DoctorStore(string applicationName, string environment, string? keyPrefix) => raw.Doctor(applicationName, environment, keyPrefix);
    }

    public static IEnumerable<object[]> Boundaries => new[] { "linux", "windows", "mac" }
        .SelectMany(platform => Enumerable.Range(0, 17).SelectMany(position => new[]
        { new object[] { platform, position, false }, new object[] { platform, position, true } }));

    [Theory]
    [MemberData(nameof(Boundaries))]
    public void NativeAdapter_ReopenedJournalRecoversEveryBeforeAndAfterBoundary(string platform, int position, bool after)
    {
        var app = "App" + Guid.NewGuid().ToString("N");
        var probe = new LeaseProbe(app);
        var native = new NativeMemoryStore(probe);
        var linux = new LinuxRunner(probe);
        var mac = new MacInterop(probe);
        IAppSurfaceLocalSecretStore Create() => platform switch
        {
            "windows" => new WindowsCredentialManagerLocalSecretStore(native),
            "linux" => new LinuxSecretServiceLocalSecretStore("/fake/secret-tool", linux),
            _ => new MacOsV2CompatibilityLocalSecretStore(native, mac)
        };
        var store = Create();
        var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
        var source = normalizer.Normalize(app, "Development", null, "Legacy.Key").Identity!;
        var destination = normalizer.Normalize(app, "Development", null, "Payments:ApiKey").Identity!;
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(source, "retained-marker").Status);
        if (platform == "mac")
        {
            source = source with { StorageName = $"appsurface:v2:{app}:Development::Legacy.Key" };
            destination = destination with { StorageName = $"appsurface:v2:{app}:Development::Payments:ApiKey" };
        }
        IAppSurfaceLocalSecretMigrationBackend backend = platform == "mac"
            ? new MacOsV2MigrationBackend((MacOsV2CompatibilityLocalSecretStore)store, source, destination, true)
            : new PlatformLocalSecretMigrationBackend((IndexedLocalSecretStore)store, source, destination);
        var fault = new LocalSecretFileMigrationPersistenceTests.FaultBackend(backend, position, after);
        var first = AppSurfaceLocalSecretMigrationCoordinator.Run(fault, app, "Development", null,
            source.StorageName, destination.StorageName, destination.Key, platform);
        Assert.Equal(LocalSecretResultStatus.Unavailable, first.Status);
        var persisted = backend.ReadJournal();
        var reopened = Create();
        var retry = ((IAppSurfaceLocalSecretMigrationStore)reopened).MigrateKey(app, "Development", null, source.StorageName, destination.Key);
        Assert.Equal(LocalSecretResultStatus.Found, retry.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.Complete, retry.State);
        if (persisted is not null) Assert.Equal(persisted.MigrationId, retry.MigrationId);
        Assert.Equal("retained-marker", reopened.Get(destination).Value);
        Assert.Equal(LocalSecretResultStatus.Missing, reopened.Get(source).Status);
        Assert.Equal(new[] { destination.Key.Value }, reopened.List(app, "Development", null).Keys);
        Assert.DoesNotContain("retained-marker", JsonSerializer.Serialize(backend.ReadJournal()));
    }

    [Theory]
    [InlineData("linux")]
    [InlineData("windows")]
    [InlineData("mac")]
    public void NativeAdapters_ShouldMigrateExactHistoricalKeysWithRealJournalAndSharedMutationLease(string platform)
    {
        var app = "App" + Guid.NewGuid().ToString("N");
        var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
        var source = normalizer.Normalize(app, "Development", null, "Legacy.Key").Identity!;
        var destination = normalizer.Normalize(app, "Development", null, "Payments:ApiKey").Identity!;
        var probe = new LeaseProbe(app);
        IAppSurfaceLocalSecretStore store;
        switch (platform)
        {
            case "linux": store = new LinuxSecretServiceLocalSecretStore("/isolated/secret-tool", new LinuxRunner(probe)); break;
            case "windows": store = new WindowsCredentialManagerLocalSecretStore(new NativeMemoryStore(probe)); break;
            default: store = new MacOsV2CompatibilityLocalSecretStore(new NativeMemoryStore(probe), new MacInterop(probe)); break;
        }
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(source, "migration-marker").Status);
        // Overwrite path proves SecItem.Update also holds the package lease.
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(source, "migration-marker").Status);
        var exact = platform == "mac" ? $"appsurface:v2:{app}:Development::Legacy.Key" : source.StorageName;
        var result = ((IAppSurfaceLocalSecretMigrationStore)store).MigrateKey(app, "Development", null, exact, destination.Key);
        Assert.Equal(LocalSecretResultStatus.Found, result.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationState.Complete, result.State);
        Assert.Equal("migration-marker", store.Get(destination).Value);
        Assert.Equal(LocalSecretResultStatus.Missing, store.Get(source).Status);
        Assert.Equal(new[] { destination.Key.Value }, store.List(app, "Development", null).Keys);
        Assert.Equal(LocalSecretResultStatus.Found, store.Delete(destination).Status);
        store.Doctor(app, "Development", null);
        Assert.True(probe.Mutations >= 5);
        var retry = ((IAppSurfaceLocalSecretMigrationStore)store).MigrateKey(app, "Development", null, exact, destination.Key);
        Assert.Equal(result.MigrationId, retry.MigrationId);
        Assert.Equal(LocalSecretResultStatus.Found, retry.Status);
    }

    [Theory]
    [InlineData("linux")]
    [InlineData("windows")]
    public void IndexedRepair_ShouldHoldLeaseForItsReadSnapshotAndWrite(string platform)
    {
        var app = "App" + Guid.NewGuid().ToString("N");
        var probe = new LeaseProbe(app);
        var raw = new NativeMemoryStore(probe);
        IAppSurfaceLocalSecretStore store;
        if (platform == "windows") store = new WindowsCredentialManagerLocalSecretStore(raw);
        else store = new IndexedFake(raw);
        var index = new AppSurfaceLocalSecretIdentity(app, "Development", null, AppSurfaceConfigKey.Parse("__appsurface_index__"), $"appsurface:{app}:Development::__appsurface_index__");
        raw.Values[index.StorageName] = "[\"stale\"]";
        raw.CheckReads = true;
        var result = store.List(app, "Development", null);
        Assert.Equal(LocalSecretResultStatus.Found, result.Status);
        Assert.Empty(result.Keys);
        Assert.Equal("[]", raw.Values[index.StorageName]);
    }

    [Fact]
    public void WindowsNativeCaseAlias_ShouldStopBeforeJournalOrDelete()
    {
        var app = "App" + Guid.NewGuid().ToString("N");
        var raw = new NativeMemoryStore(new LeaseProbe(app));
        var store = new WindowsCredentialManagerLocalSecretStore(raw);
        var source = new AppSurfaceLocalSecretIdentityNormalizer().Normalize(app, "Development", null, "Payments:ApiKey").Identity!;
        store.Set(source, "marker");
        var before = raw.Mutations;
        var result = store.MigrateKey(app, "Development", null, source.StorageName, AppSurfaceConfigKey.Parse("payments:apikey"));
        Assert.Equal("local-secret-migration-same-key", result.Diagnostic?.Code);
        Assert.Equal(before, raw.Mutations);
    }

    [Theory]
    [InlineData("linux")]
    [InlineData("windows")]
    [InlineData("mac")]
    public void NativeSet_CaseVariantUpdatesExistingExactSpellingUnderWriterLease(string platform)
    {
        var app = "App" + Guid.NewGuid().ToString("N");
        var probe = new LeaseProbe(app);
        var store = CreateCaseStore(platform, probe);
        var original = new AppSurfaceLocalSecretIdentityNormalizer().Normalize(app, "Development", null, "Payments:ApiKey").Identity!;
        var variant = new AppSurfaceLocalSecretIdentityNormalizer().Normalize(app, "Development", null, "payments:apikey").Identity!;
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(original, "first-marker").Status);
        Assert.Equal(LocalSecretResultStatus.Found, store.Set(variant, "updated-marker").Status);
        Assert.Equal("updated-marker", ReadCaseRaw(store, original).Value);
        Assert.Equal(LocalSecretResultStatus.Missing, ReadCaseRaw(store, variant).Status);
        Assert.Equal(new[] { original.Key.Value }, store.List(app, "Development", null).Keys);
        Assert.Equal("updated-marker", store.Get(variant).Value);
        Assert.Equal(LocalSecretResultStatus.Found, store.Delete(variant).Status);
        Assert.Equal(LocalSecretResultStatus.Missing, ReadCaseRaw(store, original).Status);
        Assert.Empty(store.List(app, "Development", null).Keys);
        Assert.Equal(LocalSecretResultStatus.Missing, store.Delete(variant).Status);
    }

    [Theory]
    [InlineData("linux")]
    [InlineData("windows")]
    [InlineData("mac")]
    public void NativeSet_ExistingEqualPayloadCaseCollisionIsTerminalBeforeAnyMutation(string platform)
    {
        var app = "App" + Guid.NewGuid().ToString("N");
        var probe = new LeaseProbe(app);
        var store = CreateCaseStore(platform, probe);
        var original = new AppSurfaceLocalSecretIdentityNormalizer().Normalize(app, "Development", null, "Payments:ApiKey").Identity!;
        var variant = new AppSurfaceLocalSecretIdentityNormalizer().Normalize(app, "Development", null, "payments:apikey").Identity!;
        using (PlatformLocalSecretMaintenanceLease.Acquire(PlatformLocalSecretStatePaths.DefaultDirectory, app, "Development", null, TimeSpan.FromSeconds(1), CancellationToken.None))
        {
            if (store is IndexedLocalSecretStore indexed)
            {
                Assert.Equal(LocalSecretResultStatus.Found, indexed.WriteRaw(original, "same-marker").Status);
                Assert.Equal(LocalSecretResultStatus.Found, indexed.WriteRaw(variant, "same-marker").Status);
                Assert.Equal(LocalSecretResultStatus.Found, indexed.WriteIndexForMigration(app, "Development", null, [original.Key.Value, variant.Key.Value]).Status);
            }
            else
            {
                var mac = (MacOsV2CompatibilityLocalSecretStore)store;
                Assert.Equal(LocalSecretResultStatus.Found, mac.WriteMigrationValue(original, "same-marker").Status);
                Assert.Equal(LocalSecretResultStatus.Found, mac.WriteMigrationValue(variant, "same-marker").Status);
                Assert.Equal(LocalSecretResultStatus.Found, mac.WriteMigrationIndex(app, "Development", null, [original.Key.Value, variant.Key.Value]).Status);
            }
        }
        var mutations = probe.Mutations;
        var result = store.Set(variant, "attempted-change-marker");
        Assert.Equal(LocalSecretResultStatus.ProviderFailed, result.Status);
        Assert.Equal("config-key-collision", result.Diagnostic?.Code);
        Assert.Equal("config-key-collision", store.Get(original).Diagnostic?.Code);
        Assert.Equal("config-key-collision", store.Delete(variant).Diagnostic?.Code);
        Assert.Equal(mutations, probe.Mutations);
        Assert.Equal("same-marker", ReadCaseRaw(store, original).Value);
        Assert.Equal("same-marker", ReadCaseRaw(store, variant).Value);
        Assert.Equal(new[] { original.Key.Value, variant.Key.Value }, store.List(app, "Development", null).Keys);
    }

    [Theory]
    [InlineData("linux")]
    [InlineData("windows")]
    [InlineData("mac")]
    public void ExactKeyMigration_ExistingCaseVariantDestinationRetainsBothRecords(string platform)
    {
        var app = "App" + Guid.NewGuid().ToString("N");
        var probe = new LeaseProbe(app);
        var store = CreateCaseStore(platform, probe);
        var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
        var source = normalizer.Normalize(app, "Development", null, "Legacy.Key").Identity!;
        var variant = normalizer.Normalize(app, "Development", null, "payments:apikey").Identity!;
        var destination = normalizer.Normalize(app, "Development", null, "Payments:ApiKey").Identity!;

        using (PlatformLocalSecretMaintenanceLease.Acquire(PlatformLocalSecretStatePaths.DefaultDirectory, app, "Development", null, TimeSpan.FromSeconds(1), CancellationToken.None))
        {
            if (store is IndexedLocalSecretStore indexed)
            {
                Assert.Equal(LocalSecretResultStatus.Found, indexed.WriteRaw(source, "source-marker").Status);
                Assert.Equal(LocalSecretResultStatus.Found, indexed.WriteRaw(variant, "existing-marker").Status);
                Assert.Equal(LocalSecretResultStatus.Found,
                    indexed.WriteIndexForMigration(app, "Development", null, [source.Key.Value, variant.Key.Value]).Status);
            }
            else
            {
                var mac = (MacOsV2CompatibilityLocalSecretStore)store;
                source = source with { StorageName = $"appsurface:v2:{app}:Development::Legacy.Key" };
                variant = variant with { StorageName = $"appsurface:v2:{app}:Development::payments:apikey" };
                destination = destination with { StorageName = $"appsurface:v2:{app}:Development::Payments:ApiKey" };
                Assert.Equal(LocalSecretResultStatus.Found, mac.WriteMigrationValue(source, "source-marker").Status);
                Assert.Equal(LocalSecretResultStatus.Found, mac.WriteMigrationValue(variant, "existing-marker").Status);
                Assert.Equal(LocalSecretResultStatus.Found,
                    mac.WriteMigrationIndex(app, "Development", null, [source.Key.Value, variant.Key.Value]).Status);
            }
        }

        var mutations = probe.Mutations;
        var result = ((IAppSurfaceLocalSecretMigrationStore)store)
            .MigrateKey(app, "Development", null, source.StorageName, destination.Key);

        Assert.Equal(LocalSecretResultStatus.ProviderFailed, result.Status);
        Assert.Equal("config-key-collision", result.Diagnostic?.Code);
        Assert.Equal(mutations, probe.Mutations);
        Assert.Equal("source-marker", ReadCaseRaw(store, source).Value);
        Assert.Equal("existing-marker", ReadCaseRaw(store, variant).Value);
        Assert.Equal(LocalSecretResultStatus.Missing, ReadCaseRaw(store, destination).Status);
    }

    [Fact]
    public void MacLegacyToV2Migration_ExistingV2CaseVariantRetainsBothIndexesAndRecords()
    {
        var app = "App" + Guid.NewGuid().ToString("N");
        var probe = new LeaseProbe(app);
        var raw = new NativeMemoryStore(probe);
        var legacy = new IndexedFake(raw);
        var mac = new MacOsV2CompatibilityLocalSecretStore(legacy, new MacInterop(probe));
        var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
        var source = normalizer.Normalize(app, "Development", null, "Legacy.Key").Identity!;
        var variant = normalizer.Normalize(app, "Development", null, "payments:apikey").Identity!
            with
        { StorageName = $"appsurface:v2:{app}:Development::payments:apikey" };
        var destination = normalizer.Normalize(app, "Development", null, "Payments:ApiKey").Identity!
            with
        { StorageName = $"appsurface:v2:{app}:Development::Payments:ApiKey" };

        using (PlatformLocalSecretMaintenanceLease.Acquire(PlatformLocalSecretStatePaths.DefaultDirectory, app, "Development", null, TimeSpan.FromSeconds(1), CancellationToken.None))
        {
            Assert.Equal(LocalSecretResultStatus.Found, legacy.WriteRaw(source, "source-marker").Status);
            Assert.Equal(LocalSecretResultStatus.Found,
                legacy.WriteIndexForMigration(app, "Development", null, [source.Key.Value]).Status);
            Assert.Equal(LocalSecretResultStatus.Found, mac.WriteMigrationValue(variant, "existing-marker").Status);
            Assert.Equal(LocalSecretResultStatus.Found,
                mac.WriteMigrationIndex(app, "Development", null, [variant.Key.Value]).Status);
        }

        var mutations = probe.Mutations;
        var result = mac.MigrateKey(app, "Development", null, source.StorageName, destination.Key);

        Assert.Equal(LocalSecretResultStatus.ProviderFailed, result.Status);
        Assert.Equal("config-key-collision", result.Diagnostic?.Code);
        Assert.Equal(mutations, probe.Mutations);
        Assert.Equal("source-marker", legacy.ReadRaw(source).Value);
        Assert.Equal("existing-marker", mac.ReadMigrationValue(variant, true).Value);
        Assert.Equal(LocalSecretResultStatus.Missing, mac.ReadMigrationValue(destination, true).Status);
    }

    [Theory]
    [InlineData("v2", false)]
    [InlineData("v2", true)]
    [InlineData("legacy", false)]
    [InlineData("legacy", true)]
    public void MacLogicalDelete_PreflightsBothIndexesBeforeDeletingEitherVersion(string version, bool corrupt)
    {
        var app = "App" + Guid.NewGuid().ToString("N");
        var probe = new LeaseProbe(app);
        var raw = new NativeMemoryStore(probe);
        var legacy = new IndexedFake(raw);
        var mac = new MacOsV2CompatibilityLocalSecretStore(legacy, new MacInterop(probe));
        var identity = new AppSurfaceLocalSecretIdentityNormalizer().Normalize(app, "Development", null, "Payments:ApiKey").Identity!;
        Assert.Equal(LocalSecretResultStatus.Found, legacy.Set(identity, "legacy-marker").Status);
        Assert.Equal(LocalSecretResultStatus.Found, mac.Set(identity, "v2-marker").Status);
        var indexValue = corrupt ? "{" : "[\"Payments:ApiKey\",\"payments:apikey\"]";
        using (PlatformLocalSecretMaintenanceLease.Acquire(PlatformLocalSecretStatePaths.DefaultDirectory, app, "Development", null, TimeSpan.FromSeconds(1), CancellationToken.None))
        {
            if (version == "legacy") raw.Values[$"appsurface:{app}:Development::__appsurface_index__"] = indexValue;
            else
            {
                var indexIdentity = identity with { Key = AppSurfaceConfigKey.Parse("__appsurface_index__") };
                Assert.Equal(LocalSecretResultStatus.Found, mac.WriteMigrationValue(indexIdentity, indexValue).Status);
            }
        }
        var mutations = probe.Mutations;
        var result = mac.Delete(identity);
        Assert.Equal(LocalSecretResultStatus.ProviderFailed, result.Status);
        if (!corrupt) Assert.Equal("config-key-collision", result.Diagnostic?.Code);
        else Assert.Contains("index", result.Diagnostic!.Code, StringComparison.Ordinal);
        Assert.Equal(mutations, probe.Mutations);
        Assert.Equal("legacy-marker", legacy.ReadRaw(identity).Value);
        Assert.Equal("v2-marker", mac.ReadMigrationValue(identity, true).Value);
    }

    [Fact]
    public void MacLogicalDelete_ResolvesEachVersionSpellingUnderTheSharedLease()
    {
        var app = "App" + Guid.NewGuid().ToString("N");
        var probe = new LeaseProbe(app);
        var legacy = new IndexedFake(new NativeMemoryStore(probe));
        var mac = new MacOsV2CompatibilityLocalSecretStore(legacy, new MacInterop(probe));
        var upper = new AppSurfaceLocalSecretIdentityNormalizer().Normalize(app, "Development", null, "Payments:ApiKey").Identity!;
        var lower = new AppSurfaceLocalSecretIdentityNormalizer().Normalize(app, "Development", null, "payments:apikey").Identity!;
        Assert.Equal(LocalSecretResultStatus.Found, legacy.Set(lower, "legacy-marker").Status);
        Assert.Equal(LocalSecretResultStatus.Found, mac.Set(upper, "v2-marker").Status);
        Assert.Equal(LocalSecretResultStatus.Found, mac.Delete(lower).Status);
        Assert.Equal(LocalSecretResultStatus.Missing, legacy.ReadRaw(lower).Status);
        Assert.Equal(LocalSecretResultStatus.Missing, mac.ReadMigrationValue(upper, true).Status);
        Assert.Empty(legacy.List(app, "Development", null).Keys);
        Assert.Empty(mac.List(app, "Development", null).Keys);
    }

    private static IAppSurfaceLocalSecretStore CreateCaseStore(string platform, LeaseProbe probe) => platform switch
    {
        "linux" => new LinuxSecretServiceLocalSecretStore("/fake/secret-tool", new LinuxRunner(probe)),
        "windows" => new WindowsCredentialManagerLocalSecretStore(new NativeMemoryStore(probe)),
        _ => new MacOsV2CompatibilityLocalSecretStore(new NativeMemoryStore(probe), new MacInterop(probe))
    };

    private static AppSurfaceLocalSecretResult ReadCaseRaw(IAppSurfaceLocalSecretStore store, AppSurfaceLocalSecretIdentity identity) =>
        store is IndexedLocalSecretStore indexed ? indexed.ReadRaw(identity) : ((MacOsV2CompatibilityLocalSecretStore)store).ReadMigrationValue(identity, true);

    private sealed class LeaseProbe(string app)
    {
        internal int Mutations { get; private set; }
        internal void AssertHeld()
        {
            Exception? failure = null;
            var contender = new Thread(() => failure = Record.Exception(() =>
            {
                using var lease = PlatformLocalSecretMaintenanceLease.Acquire(PlatformLocalSecretStatePaths.DefaultDirectory,
                    app, "Development", "different-prefix", TimeSpan.Zero, CancellationToken.None);
            }));
            contender.Start();
            Assert.True(contender.Join(TimeSpan.FromSeconds(2)));
            Assert.IsType<IOException>(failure);
            Mutations++;
        }
    }

    private sealed class NativeMemoryStore(LeaseProbe probe) : IAppSurfaceLocalSecretStore
    {
        internal Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        internal bool CheckReads { get; set; }
        internal int Mutations { get; private set; }
        public string Name => "FakeNative";
        public AppSurfaceLocalSecretResult Get(AppSurfaceLocalSecretIdentity identity)
        {
            if (CheckReads) probe.AssertHeld();
            return Values.TryGetValue(identity.StorageName, out var value) ? AppSurfaceLocalSecretResult.Found(value, Name) : AppSurfaceLocalSecretResult.Missing(Name);
        }
        public AppSurfaceLocalSecretResult Set(AppSurfaceLocalSecretIdentity identity, string value)
        {
            probe.AssertHeld(); Mutations++; Values[identity.StorageName] = value;
            return AppSurfaceLocalSecretResult.Found(string.Empty, Name);
        }
        public AppSurfaceLocalSecretResult Delete(AppSurfaceLocalSecretIdentity identity)
        {
            probe.AssertHeld(); Mutations++;
            return Values.Remove(identity.StorageName) ? AppSurfaceLocalSecretResult.Found(string.Empty, Name) : AppSurfaceLocalSecretResult.Missing(Name);
        }
        public AppSurfaceLocalSecretListResult List(string applicationName, string environment, string? keyPrefix) => AppSurfaceLocalSecretListResult.Found([], Name);
        public AppSurfaceLocalSecretResult Doctor(string applicationName, string environment, string? keyPrefix) => AppSurfaceLocalSecretResult.Missing(Name);
    }

    private sealed class IndexedFake(NativeMemoryStore raw) : IndexedLocalSecretStore
    {
        public override string Name => "IndexedFake";
        protected override AppSurfaceLocalSecretResult ReadStoredValue(AppSurfaceLocalSecretIdentity identity) => raw.Get(identity);
        protected override AppSurfaceLocalSecretResult WriteStoredValue(AppSurfaceLocalSecretIdentity identity, string value) => raw.Set(identity, value);
        protected override AppSurfaceLocalSecretResult DeleteStoredValue(AppSurfaceLocalSecretIdentity identity) => raw.Delete(identity);
        protected override AppSurfaceLocalSecretResult DoctorStore(string applicationName, string environment, string? keyPrefix) => raw.Doctor(applicationName, environment, keyPrefix);
    }

    private sealed class LinuxRunner(LeaseProbe probe) : IPlatformSecretCommandRunner
    {
        private readonly Dictionary<string, string> _values = new();
        public PlatformSecretCommandResult Run(string fileName, IReadOnlyList<string> arguments, string? standardInput)
        {
            var key = arguments[^1];
            switch (arguments[0])
            {
                case "store": probe.AssertHeld(); _values[key] = standardInput!; return Result(0, "");
                case "clear": probe.AssertHeld(); return Result(_values.Remove(key) ? 0 : 1, "");
                case "search": return Result(0, "");
                default: return _values.TryGetValue(key, out var value) ? Result(0, value) : Result(1, "");
            }
        }
        private static PlatformSecretCommandResult Result(int code, string text) => new(code, text, string.Empty, PlatformSecretCommandResultKind.ProcessExited);
    }

    private sealed class MacInterop(LeaseProbe probe) : IMacOsSecItemInterop
    {
        private readonly Dictionary<MacOsSecItemQuery, byte[]> _values = new();
        public MacOsSecItemReadResult Read(MacOsSecItemQuery query) => _values.TryGetValue(query, out var value) ? new(0, value.ToArray()) : new(-25300, null);
        public int Exists(MacOsSecItemQuery query) => _values.ContainsKey(query) ? 0 : -25300;
        public int Add(MacOsSecItemQuery query, byte[] value) { probe.AssertHeld(); return _values.TryAdd(query, value.ToArray()) ? 0 : -25299; }
        public int Update(MacOsSecItemQuery query, byte[] value) { probe.AssertHeld(); _values[query] = value.ToArray(); return 0; }
        public int Delete(MacOsSecItemQuery query) { probe.AssertHeld(); return _values.Remove(query) ? 0 : -25300; }
    }
}
