using System.Text;

namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

public sealed class MacOsV2CompatibilityLocalSecretStoreTests
{
    private readonly AppSurfaceLocalSecretIdentityNormalizer _normalizer = new();

    [Fact]
    public void Set_Should_WriteVerifyAndIndexTheExactV2Identity()
    {
        var interop = new FakeSecItemInterop();
        var store = CreateStore(new InMemoryAppSurfaceLocalSecretStore(), interop);
        var identity = Identity("Stripe:ApiKey", "Payments");

        var result = store.Set(identity, "sentinel-v2-value");

        Assert.Equal(LocalSecretResultStatus.Found, result.Status);
        Assert.Equal("AppSurface.LocalSecrets.v2.MyApp.Development", interop.Adds[0].Service);
        Assert.Equal("Payments:Stripe:ApiKey", interop.Adds[0].Account);
        Assert.Contains(interop.Reads, query => query.Account == "Payments:Stripe:ApiKey");
        Assert.Contains(interop.Adds, query => query.Account == "Payments:__appsurface_index__");
        Assert.Equal("macOS Keychain (v2)", result.Source);
        ValueSafeAssert.DoesNotExpose("sentinel-v2-value", result.ToString());
    }

    [Fact]
    public void Get_Should_ReturnMigrationRequired_WhenOnlyLegacyValueIsReadable()
    {
        var legacy = new InMemoryAppSurfaceLocalSecretStore();
        var interop = new FakeSecItemInterop();
        var store = CreateStore(legacy, interop);
        var identity = Identity("Stripe:ApiKey");
        legacy.Set(identity, "sentinel-legacy-value");

        var result = store.Get(identity);

        Assert.Equal(LocalSecretResultStatus.MigrationRequired, result.Status);
        Assert.Equal("local-secret-migration-required", result.Diagnostic?.Code);
        Assert.Contains("appsurface secrets migrate --app MyApp --environment Development", result.Diagnostic?.Fix, StringComparison.Ordinal);
        ValueSafeAssert.DoesNotExpose("sentinel-legacy-value", result.ToString());
    }

    [Fact]
    public void Get_Should_KeepV2FoundPrecedenceOverLegacyValue()
    {
        var legacy = new InMemoryAppSurfaceLocalSecretStore();
        var interop = new FakeSecItemInterop();
        var store = CreateStore(legacy, interop);
        var identity = Identity("Stripe:ApiKey");
        legacy.Set(identity, "sentinel-legacy-value");
        store.Set(identity, "sentinel-v2-value");

        var result = store.Get(identity);

        Assert.Equal(LocalSecretResultStatus.Found, result.Status);
        Assert.Equal("sentinel-v2-value", result.Value);
    }

    [Fact]
    public void Get_Should_ReturnLegacyLockedStatus_WhenV2IsMissing()
    {
        var legacy = new FixedGetStore(
            AppSurfaceLocalSecretResult.NotFound(
                LocalSecretResultStatus.Locked,
                new AppSurfaceLocalSecretDiagnostic(
                    "local-secret-store-locked",
                    "Legacy Keychain is locked.",
                    "The retained v1 value cannot be read.",
                    "Unlock Keychain and retry.",
                    "local-secrets-macos-migration"),
                "legacy"));
        var store = CreateStore(legacy, new FakeSecItemInterop());

        var result = store.Get(Identity("Stripe:ApiKey"));

        Assert.Equal(LocalSecretResultStatus.Locked, result.Status);
        Assert.Equal("local-secret-store-locked", result.Diagnostic?.Code);
    }

    [Fact]
    public void Set_Should_UpdateAnExistingV2ValueAndKeepItIndexed()
    {
        var interop = new FakeSecItemInterop();
        var store = CreateStore(new InMemoryAppSurfaceLocalSecretStore(), interop);
        var identity = Identity("Stripe:ApiKey");
        store.Set(identity, "sentinel-first");

        var set = store.Set(identity, "sentinel-second");
        var get = store.Get(identity);

        Assert.Equal(LocalSecretResultStatus.Found, set.Status);
        Assert.Single(interop.Updates);
        Assert.Equal("sentinel-second", get.Value);
        Assert.Equal(["Stripe:ApiKey"], store.List("MyApp", "Development", null).Keys);
    }

    [Fact]
    public void Set_Should_NotWriteTheIndex_WhenV2WriteFails()
    {
        var interop = new FakeSecItemInterop { NextAddStatus = -1 };
        var store = CreateStore(new InMemoryAppSurfaceLocalSecretStore(), interop);

        var result = store.Set(Identity("Stripe:ApiKey"), "sentinel-v2-value");

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.DoesNotContain(interop.Adds, query => query.Account == "__appsurface_index__");
        ValueSafeAssert.DoesNotExpose("sentinel-v2-value", result.ToString());
    }

    [Fact]
    public void Set_Should_NotWriteTheIndex_WhenFreshV2ReadFails()
    {
        var interop = new FakeSecItemInterop();
        interop.ForceReadStatus(V2Query("Stripe:ApiKey"), -1);
        var store = CreateStore(new InMemoryAppSurfaceLocalSecretStore(), interop);

        var result = store.Set(Identity("Stripe:ApiKey"), "sentinel-v2-value");

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.DoesNotContain(interop.Adds, query => query.Account == "__appsurface_index__");
        ValueSafeAssert.DoesNotExpose("sentinel-v2-value", result.ToString());
    }

    [Fact]
    public void Set_Should_NotWriteAValue_WhenTheV2IndexCannotBeParsed()
    {
        var interop = new FakeSecItemInterop();
        interop.Seed(V2Query("__appsurface_index__"), "{");
        var store = CreateStore(new InMemoryAppSurfaceLocalSecretStore(), interop);

        var result = store.Set(Identity("Stripe:ApiKey"), "sentinel-v2-value");

        Assert.Equal(LocalSecretResultStatus.ProviderFailed, result.Status);
        Assert.Equal("local-secret-index-invalid", result.Diagnostic?.Code);
        Assert.DoesNotContain(interop.Adds, query => query.Account == "Stripe:ApiKey");
        ValueSafeAssert.DoesNotExpose("sentinel-v2-value", result.ToString());
    }

    [Fact]
    public void Set_Should_ReturnUpdateFailureWithoutChangingTheExistingV2Value()
    {
        var interop = new FakeSecItemInterop();
        var store = CreateStore(new InMemoryAppSurfaceLocalSecretStore(), interop);
        var identity = Identity("Stripe:ApiKey");
        store.Set(identity, "sentinel-first");
        interop.NextUpdateStatus = -1;

        var result = store.Set(identity, "sentinel-second");

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal("sentinel-first", store.Get(identity).Value);
    }

    [Fact]
    public void ListAndDelete_Should_MergeLegacyNamesThenRemoveBothStorageVersions()
    {
        var legacy = new InMemoryAppSurfaceLocalSecretStore();
        var interop = new FakeSecItemInterop();
        var store = CreateStore(legacy, interop);
        var v1Only = Identity("Legacy:Only");
        var v2Only = Identity("V2:Only");
        legacy.Set(v1Only, "sentinel-legacy");
        store.Set(v2Only, "sentinel-v2");

        var listed = store.List("MyApp", "Development", null);
        var deleted = store.Delete(v2Only);

        Assert.Equal(LocalSecretResultStatus.Found, listed.Status);
        Assert.Equal(["Legacy:Only", "V2:Only"], listed.Keys);
        Assert.Equal(LocalSecretResultStatus.Found, deleted.Status);
        Assert.Equal(LocalSecretResultStatus.Missing, store.Get(v2Only).Status);
    }

    [Fact]
    public void List_Should_PruneAStaleV2IndexEntry()
    {
        var interop = new FakeSecItemInterop();
        interop.Seed(
            new PlatformAppSurfaceLocalSecretStore.MacOsSecItemQuery(
                "AppSurface.LocalSecrets.v2.MyApp.Development",
                "__appsurface_index__"),
            "[\"Stale:Key\"]");
        var store = CreateStore(new InMemoryAppSurfaceLocalSecretStore(), interop);

        var result = store.List("MyApp", "Development", null);

        Assert.Equal(LocalSecretResultStatus.Found, result.Status);
        Assert.Empty(result.Keys);
        Assert.Contains(interop.Updates, query => query.Account == "__appsurface_index__");
    }

    [Fact]
    public void List_Should_KeepV1OnlyKeysOutOfTheRepairedV2Index()
    {
        var legacy = new InMemoryAppSurfaceLocalSecretStore();
        var v1Only = Identity("Legacy:Only");
        legacy.Set(v1Only, "sentinel-legacy");
        var interop = new FakeSecItemInterop();
        interop.Seed(V2Query("__appsurface_index__"), "[\"Legacy:Only\",\"Stale:Key\"]");
        var store = CreateStore(legacy, interop);

        var result = store.List("MyApp", "Development", null);
        var repaired = interop.Read(V2Query("__appsurface_index__"));

        Assert.Equal(["Legacy:Only"], result.Keys);
        Assert.Equal("[]", Encoding.UTF8.GetString(Assert.IsType<byte[]>(repaired.Data)));
    }

    [Fact]
    public void List_Should_UseDataFreeV2PresenceChecksForIndexedValues()
    {
        var interop = new FakeSecItemInterop();
        interop.Seed(V2Query("__appsurface_index__"), "[\"Stripe:ApiKey\"]");
        interop.Seed(V2Query("Stripe:ApiKey"), "sentinel-v2-value");
        var store = CreateStore(new InMemoryAppSurfaceLocalSecretStore(), interop);

        var result = store.List("MyApp", "Development", null);

        Assert.Equal(["Stripe:ApiKey"], result.Keys);
        Assert.Contains(interop.ExistsChecks, query => query.Account == "Stripe:ApiKey");
        Assert.DoesNotContain(interop.Reads, query => query.Account == "Stripe:ApiKey");
    }

    [Theory]
    [InlineData(-25308, LocalSecretResultStatus.Locked, "local-secret-store-locked")]
    [InlineData(-25293, LocalSecretResultStatus.Locked, "local-secret-store-locked")]
    [InlineData(-128, LocalSecretResultStatus.Locked, "local-secret-store-locked")]
    [InlineData(-34018, LocalSecretResultStatus.Unavailable, "local-secret-store-entitlement-unsupported")]
    [InlineData(-1, LocalSecretResultStatus.Unavailable, "local-secret-store-unavailable")]
    public void Get_Should_MapSecItemTerminalStatusesSafely(int nativeStatus, LocalSecretResultStatus expected, string diagnosticCode)
    {
        var interop = new FakeSecItemInterop { ForcedReadStatus = nativeStatus };
        var store = CreateStore(new InMemoryAppSurfaceLocalSecretStore(), interop);

        var result = store.Get(Identity("Stripe:ApiKey"));

        Assert.Equal(expected, result.Status);
        Assert.Equal(diagnosticCode, result.Diagnostic?.Code);
        Assert.Contains(nativeStatus.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Diagnostic?.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public void Get_Should_FailClosed_WhenSecItemReportsSuccessWithoutData()
    {
        var legacy = new InMemoryAppSurfaceLocalSecretStore();
        var interop = new FakeSecItemInterop { ForcedReadStatus = 0 };
        var store = CreateStore(legacy, interop);
        var identity = Identity("Stripe:ApiKey");
        legacy.Set(identity, "sentinel-legacy-value");

        var result = store.Get(identity);

        Assert.Equal(LocalSecretResultStatus.ProviderFailed, result.Status);
        Assert.Equal("local-secret-v2-read-invalid", result.Diagnostic?.Code);
        ValueSafeAssert.DoesNotExpose("sentinel-legacy-value", result.ToString());
    }

    [Fact]
    public void Migrate_Should_CopyLegacyValueWithoutDeletingIt_AndRemainIdempotent()
    {
        var legacy = new InMemoryAppSurfaceLocalSecretStore();
        var interop = new FakeSecItemInterop();
        var store = CreateStore(legacy, interop);
        var identity = Identity("Stripe:ApiKey");
        legacy.Set(identity, "sentinel-legacy-value");

        var first = store.Migrate("MyApp", "Development", null);
        var second = store.Migrate("MyApp", "Development", null);

        Assert.Equal(LocalSecretResultStatus.Found, first.Status);
        Assert.Equal(1, first.Migrated);
        Assert.Equal(0, first.Failed);
        Assert.Equal(LocalSecretResultStatus.Found, legacy.Get(identity).Status);
        Assert.Equal(LocalSecretResultStatus.Found, store.Get(identity).Status);
        Assert.Equal(1, second.AlreadyV2);
        Assert.Equal(0, second.Failed);
        ValueSafeAssert.DoesNotExpose("sentinel-legacy-value", System.Text.Json.JsonSerializer.Serialize(first));
    }

    [Fact]
    public void Migrate_Should_PreserveExistingV2ValueWhenLegacyChangesLater()
    {
        var legacy = new InMemoryAppSurfaceLocalSecretStore();
        var interop = new FakeSecItemInterop();
        var store = CreateStore(legacy, interop);
        var identity = Identity("Stripe:ApiKey");
        legacy.Set(identity, "sentinel-legacy-first");
        store.Set(identity, "sentinel-v2-current");
        legacy.Set(identity, "sentinel-legacy-later");

        var migration = store.Migrate("MyApp", "Development", null);
        var result = store.Get(identity);

        Assert.Equal(1, migration.AlreadyV2);
        Assert.Equal("sentinel-v2-current", result.Value);
    }

    [Fact]
    public void Migrate_Should_ReturnNamespaceFailure_WhenLegacyListingFails()
    {
        var diagnostic = new AppSurfaceLocalSecretDiagnostic(
            "local-secret-store-locked",
            "Legacy Keychain is locked.",
            "The retained v1 index cannot be read.",
            "Unlock Keychain and retry.",
            "local-secrets-macos-migration");
        var store = CreateStore(
            new FixedGetStore(AppSurfaceLocalSecretResult.NotFound(LocalSecretResultStatus.Locked, diagnostic, "fixed-legacy-store")),
            new FakeSecItemInterop());

        var result = store.Migrate("MyApp", "Development", null);

        Assert.Equal(LocalSecretResultStatus.Locked, result.Status);
        Assert.Empty(result.Rows);
        Assert.Same(diagnostic, result.Diagnostic);
        Assert.Equal("fixed-legacy-store", result.Source);
    }

    [Fact]
    public void Migrate_Should_NotWriteValues_WhenTheV2IndexCannotBeParsed()
    {
        var legacy = new InMemoryAppSurfaceLocalSecretStore();
        legacy.Set(Identity("Stripe:ApiKey"), "sentinel-legacy-value");
        var interop = new FakeSecItemInterop();
        interop.Seed(V2Query("__appsurface_index__"), "{");
        var store = CreateStore(legacy, interop);

        var result = store.Migrate("MyApp", "Development", null);

        Assert.Equal(LocalSecretResultStatus.ProviderFailed, result.Status);
        Assert.Empty(result.Rows);
        Assert.Equal("local-secret-index-invalid", result.Diagnostic?.Code);
        Assert.DoesNotContain(interop.Adds, query => query.Account == "Stripe:ApiKey");
        ValueSafeAssert.DoesNotExpose("sentinel-legacy-value", result.ToString());
    }

    [Fact]
    public void Migrate_Should_ReportAFailedRow_WhenLegacyFoundResultHasNoValue()
    {
        var legacy = new FixedGetStore(
            new AppSurfaceLocalSecretResult(LocalSecretResultStatus.Found, null, null, "fixed-legacy-store"),
            AppSurfaceLocalSecretListResult.Found(["Stripe:ApiKey"], "fixed-legacy-store"));
        var store = CreateStore(legacy, new FakeSecItemInterop());

        var result = store.Migrate("MyApp", "Development", null);
        var row = Assert.Single(result.Rows);

        Assert.Equal(LocalSecretResultStatus.Found, result.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationAction.Failed, row.Action);
        Assert.Equal(LocalSecretResultStatus.ProviderFailed, row.Status);
        Assert.Equal("local-secret-legacy-read-invalid", row.Diagnostic?.Code);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public void Doctor_Should_ReturnTheReadyDiagnosticForNamespacesWithIndexedValues()
    {
        var interop = new FakeSecItemInterop();
        var store = CreateStore(new InMemoryAppSurfaceLocalSecretStore(), interop);
        store.Set(Identity("Stripe:ApiKey"), "sentinel-v2-value");

        var result = store.Doctor("MyApp", "Development", null);

        Assert.Equal(LocalSecretResultStatus.Missing, result.Status);
        Assert.Equal("local-secret-store-ready", result.Diagnostic?.Code);
        Assert.Contains(interop.Reads, query => query.Account == "__appsurface_doctor__");
    }

    [Fact]
    public void Migrate_Should_ReportAValueSafeFailedRow_WhenV2CannotBeRead()
    {
        var legacy = new InMemoryAppSurfaceLocalSecretStore();
        var interop = new FakeSecItemInterop();
        interop.ForceReadStatus(V2Query("Stripe:ApiKey"), -1);
        var store = CreateStore(legacy, interop);
        var identity = Identity("Stripe:ApiKey");
        legacy.Set(identity, "sentinel-legacy-value");

        var result = store.Migrate("MyApp", "Development", null);

        var row = Assert.Single(result.Rows);
        Assert.Equal(LocalSecretResultStatus.Found, result.Status);
        Assert.Equal(AppSurfaceLocalSecretMigrationAction.Failed, row.Action);
        Assert.Equal(LocalSecretResultStatus.Unavailable, row.Status);
        Assert.Equal(1, result.Failed);
        ValueSafeAssert.DoesNotExpose("sentinel-legacy-value", System.Text.Json.JsonSerializer.Serialize(result));
    }

    private PlatformAppSurfaceLocalSecretStore.MacOsV2CompatibilityLocalSecretStore CreateStore(
        IAppSurfaceLocalSecretStore legacy,
        FakeSecItemInterop interop) =>
        new(legacy, interop, _normalizer);

    private AppSurfaceLocalSecretIdentity Identity(string key, string? prefix = null) =>
        _normalizer.Normalize("MyApp", "Development", prefix, key).Identity!;

    private static PlatformAppSurfaceLocalSecretStore.MacOsSecItemQuery V2Query(string key) =>
        new("AppSurface.LocalSecrets.v2.MyApp.Development", key);

    private sealed class FakeSecItemInterop : PlatformAppSurfaceLocalSecretStore.IMacOsSecItemInterop
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _forcedReadStatuses = new(StringComparer.Ordinal);

        public List<PlatformAppSurfaceLocalSecretStore.MacOsSecItemQuery> Reads { get; } = [];

        public List<PlatformAppSurfaceLocalSecretStore.MacOsSecItemQuery> ExistsChecks { get; } = [];

        public List<PlatformAppSurfaceLocalSecretStore.MacOsSecItemQuery> Adds { get; } = [];

        public List<PlatformAppSurfaceLocalSecretStore.MacOsSecItemQuery> Updates { get; } = [];

        public int? ForcedReadStatus { get; set; }

        public int? NextAddStatus { get; set; }

        public int? NextUpdateStatus { get; set; }

        public void Seed(PlatformAppSurfaceLocalSecretStore.MacOsSecItemQuery query, string value) =>
            _values[Key(query)] = Encoding.UTF8.GetBytes(value);

        public void ForceReadStatus(PlatformAppSurfaceLocalSecretStore.MacOsSecItemQuery query, int status) =>
            _forcedReadStatuses[Key(query)] = status;

        public PlatformAppSurfaceLocalSecretStore.MacOsSecItemReadResult Read(PlatformAppSurfaceLocalSecretStore.MacOsSecItemQuery query)
        {
            Reads.Add(query);
            if (ForcedReadStatus is { } status)
            {
                return new PlatformAppSurfaceLocalSecretStore.MacOsSecItemReadResult(status, null);
            }

            if (_forcedReadStatuses.TryGetValue(Key(query), out var forcedStatus))
            {
                return new PlatformAppSurfaceLocalSecretStore.MacOsSecItemReadResult(forcedStatus, null);
            }

            return _values.TryGetValue(Key(query), out var value)
                ? new PlatformAppSurfaceLocalSecretStore.MacOsSecItemReadResult(0, value.ToArray())
                : new PlatformAppSurfaceLocalSecretStore.MacOsSecItemReadResult(-25300, null);
        }

        public int Exists(PlatformAppSurfaceLocalSecretStore.MacOsSecItemQuery query)
        {
            ExistsChecks.Add(query);
            return _values.ContainsKey(Key(query)) ? 0 : -25300;
        }

        public int Add(PlatformAppSurfaceLocalSecretStore.MacOsSecItemQuery query, byte[] value)
        {
            Adds.Add(query);
            if (NextAddStatus is { } status)
            {
                NextAddStatus = null;
                return status;
            }

            var key = Key(query);
            if (_values.ContainsKey(key))
            {
                return -25299;
            }

            _values[key] = value.ToArray();
            return 0;
        }

        public int Update(PlatformAppSurfaceLocalSecretStore.MacOsSecItemQuery query, byte[] value)
        {
            Updates.Add(query);
            if (NextUpdateStatus is { } status)
            {
                NextUpdateStatus = null;
                return status;
            }

            _values[Key(query)] = value.ToArray();
            return 0;
        }

        public int Delete(PlatformAppSurfaceLocalSecretStore.MacOsSecItemQuery query) =>
            _values.Remove(Key(query)) ? 0 : -25300;

        private static string Key(PlatformAppSurfaceLocalSecretStore.MacOsSecItemQuery query) =>
            string.Concat(query.Service, "\0", query.Account);
    }

    private sealed class FixedGetStore(
        AppSurfaceLocalSecretResult result,
        AppSurfaceLocalSecretListResult? listResult = null) : IAppSurfaceLocalSecretStore
    {
        public string Name => "fixed-legacy-store";

        public AppSurfaceLocalSecretResult Get(AppSurfaceLocalSecretIdentity identity) => result;

        public AppSurfaceLocalSecretResult Set(AppSurfaceLocalSecretIdentity identity, string value) =>
            AppSurfaceLocalSecretResult.Missing(Name);

        public AppSurfaceLocalSecretResult Delete(AppSurfaceLocalSecretIdentity identity) =>
            AppSurfaceLocalSecretResult.Missing(Name);

        public AppSurfaceLocalSecretListResult List(string applicationName, string environment, string? keyPrefix) =>
            listResult ?? AppSurfaceLocalSecretListResult.Failed(result.Status, result.Diagnostic!, Name);

        public AppSurfaceLocalSecretResult Doctor(string applicationName, string environment, string? keyPrefix) => result;
    }
}
