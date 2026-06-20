namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

public sealed class PlatformAppSurfaceLocalSecretStoreTests
{
    private static readonly AppSurfaceLocalSecretIdentity Identity = new(
        "MyApp",
        "Development",
        null,
        "Stripe:ApiKey",
        "appsurface:MyApp:Development:Stripe:ApiKey");

    [Fact]
    public void MacOsStatusMapper_Should_ReturnMissing_WhenKeychainReportsItemNotFound()
    {
        var store = new PlatformAppSurfaceLocalSecretStore.MacOsKeychainLocalSecretStore();

        var result = store.MapMacOsStatus(-25300, "read");

        Assert.Equal(LocalSecretResultStatus.Missing, result.Status);
    }

    [Fact]
    public void MacOsStatusMapper_Should_ReturnLocked_WhenKeychainRequiresInteraction()
    {
        var store = new PlatformAppSurfaceLocalSecretStore.MacOsKeychainLocalSecretStore();

        var result = store.MapMacOsStatus(-25308, "read");

        Assert.Equal(LocalSecretResultStatus.Locked, result.Status);
        Assert.Equal("local-secret-store-locked", result.Diagnostic?.Code);
    }

    [Fact]
    public void LinuxGet_Should_ReturnMissing_WhenSecretToolLookupHasNoOutputOrError()
    {
        var store = new PlatformAppSurfaceLocalSecretStore.LinuxSecretServiceLocalSecretStore(
            "/usr/bin/secret-tool",
            new FixedCommandRunner(new PlatformAppSurfaceLocalSecretStore.PlatformSecretCommandResult(
                1,
                string.Empty,
                string.Empty)));

        var result = store.Get(Identity);

        Assert.Equal(LocalSecretResultStatus.Missing, result.Status);
    }

    [Fact]
    public void LinuxGet_Should_ReturnTerminalFailure_WhenSecretServiceIsUnavailable()
    {
        var store = new PlatformAppSurfaceLocalSecretStore.LinuxSecretServiceLocalSecretStore(
            "/usr/bin/secret-tool",
            new FixedCommandRunner(new PlatformAppSurfaceLocalSecretStore.PlatformSecretCommandResult(
                1,
                string.Empty,
                "Cannot autolaunch D-Bus without X11 $DISPLAY.")));

        var result = store.Get(Identity);

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal("local-secret-store-unavailable", result.Diagnostic?.Code);
    }

    [Fact]
    public void LinuxGet_Should_ReturnUnavailable_WhenSecretToolTimesOut()
    {
        var store = new PlatformAppSurfaceLocalSecretStore.LinuxSecretServiceLocalSecretStore(
            "/usr/bin/secret-tool",
            new FixedCommandRunner(PlatformAppSurfaceLocalSecretStore.PlatformSecretCommandResult.TimedOut));

        var result = store.Get(Identity);

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal("local-secret-store-unavailable", result.Diagnostic?.Code);
        Assert.True(result.Diagnostic?.Retryable);
    }

    [Fact]
    public void DefaultCommandRunner_Should_RejectNonPositiveTimeout()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new PlatformAppSurfaceLocalSecretStore.DefaultPlatformSecretCommandRunner(TimeSpan.Zero));

        Assert.Equal("commandTimeout", error.ParamName);
    }

    [Fact]
    public void DefaultCommandRunner_Should_ReturnTimedOutResult_WhenCommandDoesNotExit()
    {
        var runner = new PlatformAppSurfaceLocalSecretStore.DefaultPlatformSecretCommandRunner(TimeSpan.FromMilliseconds(50));
        var (fileName, arguments) = SlowCommand();

        var result = runner.Run(fileName, arguments, null);

        Assert.Equal(PlatformAppSurfaceLocalSecretStore.PlatformSecretCommandResult.TimedOutExitCode, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("Timed out", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void MacOsAccount_Should_IncludePrefixToAvoidNamespaceCollisions()
    {
        var prefixed = Identity with
        {
            KeyPrefix = "Payments",
            StorageName = "appsurface:MyApp:Development:Payments:Stripe:ApiKey"
        };

        Assert.Equal("Stripe:ApiKey", PlatformAppSurfaceLocalSecretStore.MacOsKeychainLocalSecretStore.Account(Identity));
        Assert.Equal("Payments:Stripe:ApiKey", PlatformAppSurfaceLocalSecretStore.MacOsKeychainLocalSecretStore.Account(prefixed));
    }

    [Fact]
    public void MacOsKeychainName_Should_UseUtf8ByteLengthsForUnicodeKeys()
    {
        var unicode = Identity with
        {
            Key = "Stripe:雪",
            StorageName = "appsurface:MyApp:Development:Stripe:雪"
        };
        var account = PlatformAppSurfaceLocalSecretStore.MacOsKeychainLocalSecretStore.Account(unicode);

        var names = PlatformAppSurfaceLocalSecretStore.MacOsKeychainLocalSecretStore.BuildKeychainName(unicode);

        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(account), names.Account.Length);
        Assert.True(names.Account.Length > account.Length);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(names.Account), account);
    }

    [Fact]
    public void LinuxArguments_Should_IncludePrefixAttributeToAvoidNamespaceCollisions()
    {
        var prefixed = Identity with
        {
            KeyPrefix = "Payments",
            StorageName = "appsurface:MyApp:Development:Payments:Stripe:ApiKey"
        };

        var arguments = PlatformAppSurfaceLocalSecretStore.LinuxSecretServiceLocalSecretStore.BuildArguments("lookup", prefixed);

        Assert.Equal(
            [
                "lookup",
                "appsurface",
                "local-secrets",
                "application",
                "MyApp",
                "environment",
                "Development",
                "prefix",
                "Payments",
                "key",
                "Stripe:ApiKey"
            ],
            arguments);
    }

    [Fact]
    public void IndexedStore_Should_PreserveCaseVariantKeysInListAndDelete()
    {
        var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
        var store = new IndexedMemoryStore();
        var upper = normalizer.Normalize("MyApp", "Development", null, "Stripe:ApiKey").Identity!;
        var lower = normalizer.Normalize("MyApp", "Development", null, "stripe:apikey").Identity!;

        store.Set(upper, "upper-secret");
        store.Set(lower, "lower-secret");
        var beforeDelete = store.List("MyApp", "Development", null);
        store.Delete(lower);
        var afterDelete = store.List("MyApp", "Development", null);

        Assert.Equal(LocalSecretResultStatus.Found, beforeDelete.Status);
        Assert.Contains("Stripe:ApiKey", beforeDelete.Keys);
        Assert.Contains("stripe:apikey", beforeDelete.Keys);
        Assert.Contains("Stripe:ApiKey", afterDelete.Keys);
        Assert.DoesNotContain("stripe:apikey", afterDelete.Keys);
        Assert.Equal("upper-secret", store.Get(upper).Value);
    }

    [Fact]
    public void IndexedStoreList_Should_PruneStaleIndexedKeys()
    {
        var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
        var store = new IndexedMemoryStore();
        var live = normalizer.Normalize("MyApp", "Development", null, "Stripe:ApiKey").Identity!;
        var stale = normalizer.Normalize("MyApp", "Development", null, "SendGrid:ApiKey").Identity!;
        store.SeedStoredValue(live, "live-secret");
        store.SeedIndex("MyApp", "Development", null, live.Key, stale.Key);

        var result = store.List("MyApp", "Development", null);

        Assert.Equal(LocalSecretResultStatus.Found, result.Status);
        Assert.Equal(["Stripe:ApiKey"], result.Keys);
        Assert.Equal(["Stripe:ApiKey"], store.ReadIndexKeys("MyApp", "Development", null));
    }

    [Fact]
    public void IndexedStoreDelete_Should_RemoveStaleIndexedKey_WhenValueIsMissing()
    {
        var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
        var store = new IndexedMemoryStore();
        var stale = normalizer.Normalize("MyApp", "Development", null, "Stripe:ApiKey").Identity!;
        store.SeedIndex("MyApp", "Development", null, stale.Key);

        var result = store.Delete(stale);

        Assert.Equal(LocalSecretResultStatus.Found, result.Status);
        Assert.Empty(store.ReadIndexKeys("MyApp", "Development", null));
    }

    [Fact]
    public void IndexedStoreDelete_Should_PreserveMissing_WhenValueAndIndexEntryAreMissing()
    {
        var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
        var store = new IndexedMemoryStore();
        var missing = normalizer.Normalize("MyApp", "Development", null, "Stripe:ApiKey").Identity!;

        var result = store.Delete(missing);

        Assert.Equal(LocalSecretResultStatus.Missing, result.Status);
        Assert.False(store.HasIndex("MyApp", "Development", null));
    }

    [Fact]
    public void IndexedStoreList_Should_NotRewriteIndex_WhenIndexedValueReadFails()
    {
        var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
        var store = new IndexedMemoryStore();
        var live = normalizer.Normalize("MyApp", "Development", null, "Stripe:ApiKey").Identity!;
        var locked = normalizer.Normalize("MyApp", "Development", null, "SendGrid:ApiKey").Identity!;
        store.SeedStoredValue(live, "live-secret");
        store.SeedStoredValue(locked, "locked-secret");
        store.SeedIndex("MyApp", "Development", null, live.Key, locked.Key);
        store.FailRead(locked, LocalSecretResultStatus.Locked);

        var result = store.List("MyApp", "Development", null);

        Assert.Equal(LocalSecretResultStatus.Locked, result.Status);
        Assert.Equal(["Stripe:ApiKey", "SendGrid:ApiKey"], store.ReadIndexKeys("MyApp", "Development", null));
    }

    [Fact]
    public void IndexedStoreList_Should_ReturnProviderFailedAndNotRepair_WhenIndexIsCorrupt()
    {
        var store = new IndexedMemoryStore();
        store.SeedRawIndex("MyApp", "Development", null, "not json");

        var result = store.List("MyApp", "Development", null);

        Assert.Equal(LocalSecretResultStatus.ProviderFailed, result.Status);
        Assert.Equal("local-secret-index-invalid", result.Diagnostic?.Code);
        Assert.Contains("Remove the invalid platform index entry", result.Diagnostic?.Fix, StringComparison.Ordinal);
        Assert.Equal("not json", store.ReadRawIndex("MyApp", "Development", null));
    }

    [Fact]
    public void IndexedStoreDelete_Should_ReturnProviderFailedAndNotRepair_WhenIndexIsCorrupt()
    {
        var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
        var store = new IndexedMemoryStore();
        var stale = normalizer.Normalize("MyApp", "Development", null, "Stripe:ApiKey").Identity!;
        store.SeedRawIndex("MyApp", "Development", null, "not json");

        var result = store.Delete(stale);

        Assert.Equal(LocalSecretResultStatus.ProviderFailed, result.Status);
        Assert.Equal("local-secret-index-invalid", result.Diagnostic?.Code);
        Assert.Contains("Remove the invalid platform index entry", result.Diagnostic?.Fix, StringComparison.Ordinal);
        Assert.Equal("not json", store.ReadRawIndex("MyApp", "Development", null));
    }

    [Fact]
    public void IndexedStoreList_Should_Fail_WhenRepairWriteFails()
    {
        var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
        var store = new IndexedMemoryStore();
        var stale = normalizer.Normalize("MyApp", "Development", null, "Stripe:ApiKey").Identity!;
        store.SeedIndex("MyApp", "Development", null, stale.Key);
        store.FailNextWrite(LocalSecretResultStatus.Unavailable);

        var result = store.List("MyApp", "Development", null);

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal(["Stripe:ApiKey"], store.ReadIndexKeys("MyApp", "Development", null));
    }

    [Fact]
    public void IndexedStoreList_Should_PruneDuplicateAndReservedIndexEntries()
    {
        var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
        var store = new IndexedMemoryStore();
        var live = normalizer.Normalize("MyApp", "Development", null, "Stripe:ApiKey").Identity!;
        store.SeedStoredValue(live, "live-secret");
        store.SeedIndex("MyApp", "Development", null, live.Key, live.Key, "__appsurface_index__", null, " ");

        var result = store.List("MyApp", "Development", null);

        Assert.Equal(LocalSecretResultStatus.Found, result.Status);
        Assert.Equal(["Stripe:ApiKey"], result.Keys);
        Assert.Equal(["Stripe:ApiKey"], store.ReadIndexKeys("MyApp", "Development", null));
    }

    private sealed class FixedCommandRunner(PlatformAppSurfaceLocalSecretStore.PlatformSecretCommandResult result)
        : PlatformAppSurfaceLocalSecretStore.IPlatformSecretCommandRunner
    {
        public PlatformAppSurfaceLocalSecretStore.PlatformSecretCommandResult Run(
            string fileName,
            IReadOnlyList<string> arguments,
            string? standardInput) =>
            result;
    }

    private static (string FileName, IReadOnlyList<string> Arguments) SlowCommand()
    {
        if (OperatingSystem.IsWindows())
        {
            return ("cmd.exe", ["/c", "ping -n 6 127.0.0.1 > NUL"]);
        }

        return ("/bin/sh", ["-c", "sleep 5"]);
    }

    private sealed class IndexedMemoryStore : PlatformAppSurfaceLocalSecretStore.IndexedLocalSecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AppSurfaceLocalSecretResult> _readFailures = new(StringComparer.Ordinal);
        private LocalSecretResultStatus? _nextWriteFailure;

        public override string Name => nameof(IndexedMemoryStore);

        public void SeedStoredValue(AppSurfaceLocalSecretIdentity identity, string value) => _values[identity.StorageName] = value;

        public void SeedIndex(string applicationName, string environment, string? keyPrefix, params string?[] keys) =>
            SeedRawIndex(applicationName, environment, keyPrefix, System.Text.Json.JsonSerializer.Serialize(keys));

        public void SeedRawIndex(string applicationName, string environment, string? keyPrefix, string value) =>
            _values[IndexStorageName(applicationName, environment, keyPrefix)] = value;

        public string? ReadRawIndex(string applicationName, string environment, string? keyPrefix) =>
            _values.GetValueOrDefault(IndexStorageName(applicationName, environment, keyPrefix));

        public string[] ReadIndexKeys(string applicationName, string environment, string? keyPrefix) =>
            System.Text.Json.JsonSerializer.Deserialize<string[]>(ReadRawIndex(applicationName, environment, keyPrefix) ?? "[]") ?? [];

        public bool HasIndex(string applicationName, string environment, string? keyPrefix) =>
            _values.ContainsKey(IndexStorageName(applicationName, environment, keyPrefix));

        public void FailRead(AppSurfaceLocalSecretIdentity identity, LocalSecretResultStatus status) =>
            _readFailures[identity.StorageName] = Failure(status);

        public void FailNextWrite(LocalSecretResultStatus status) => _nextWriteFailure = status;

        protected override AppSurfaceLocalSecretResult ReadStoredValue(AppSurfaceLocalSecretIdentity identity)
        {
            if (_readFailures.TryGetValue(identity.StorageName, out var failure))
            {
                return failure;
            }

            return _values.TryGetValue(identity.StorageName, out var value)
                ? AppSurfaceLocalSecretResult.Found(value, Name)
                : AppSurfaceLocalSecretResult.Missing(Name);
        }

        protected override AppSurfaceLocalSecretResult WriteStoredValue(AppSurfaceLocalSecretIdentity identity, string value)
        {
            if (_nextWriteFailure is { } status)
            {
                _nextWriteFailure = null;
                return Failure(status);
            }

            _values[identity.StorageName] = value;
            return AppSurfaceLocalSecretResult.Found(string.Empty, Name);
        }

        protected override AppSurfaceLocalSecretResult DeleteStoredValue(AppSurfaceLocalSecretIdentity identity)
        {
            if (!_values.Remove(identity.StorageName))
            {
                return AppSurfaceLocalSecretResult.Missing(Name);
            }

            return AppSurfaceLocalSecretResult.Found(string.Empty, Name);
        }

        protected override AppSurfaceLocalSecretResult DoctorStore(string applicationName, string environment, string? keyPrefix) =>
            AppSurfaceLocalSecretResult.NotFound(
                LocalSecretResultStatus.Missing,
                new AppSurfaceLocalSecretDiagnostic(
                    "local-secret-store-ready",
                    "Indexed memory store is ready.",
                    "The fake store is available.",
                    "No action required."),
                Name);

        private static string IndexStorageName(string applicationName, string environment, string? keyPrefix) =>
            $"appsurface:{applicationName}:{environment}:{keyPrefix}:{IndexKey}";

        private AppSurfaceLocalSecretResult Failure(LocalSecretResultStatus status) =>
            AppSurfaceLocalSecretResult.NotFound(
                status,
                new AppSurfaceLocalSecretDiagnostic(
                    $"test-{status.ToString().ToLowerInvariant()}",
                    "Injected local secret failure.",
                    "The indexed memory store was configured to fail this operation.",
                    "Clear the injected failure."),
                Name);
    }
}
