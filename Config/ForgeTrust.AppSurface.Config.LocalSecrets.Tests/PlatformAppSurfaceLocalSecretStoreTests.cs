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

        public override string Name => nameof(IndexedMemoryStore);

        protected override AppSurfaceLocalSecretResult ReadValue(AppSurfaceLocalSecretIdentity identity) =>
            _values.TryGetValue(identity.StorageName, out var value)
                ? AppSurfaceLocalSecretResult.Found(value, Name)
                : AppSurfaceLocalSecretResult.Missing(Name);

        protected override AppSurfaceLocalSecretResult WriteValue(AppSurfaceLocalSecretIdentity identity, string value)
        {
            _values[identity.StorageName] = value;
            if (string.Equals(identity.Key, IndexKey, StringComparison.Ordinal))
            {
                return AppSurfaceLocalSecretResult.Found(string.Empty, Name);
            }

            return UpdateIndex(identity, add: true);
        }

        protected override AppSurfaceLocalSecretResult DeleteValue(AppSurfaceLocalSecretIdentity identity)
        {
            if (!_values.Remove(identity.StorageName))
            {
                return AppSurfaceLocalSecretResult.Missing(Name);
            }

            if (string.Equals(identity.Key, IndexKey, StringComparison.Ordinal))
            {
                return AppSurfaceLocalSecretResult.Found(string.Empty, Name);
            }

            return UpdateIndex(identity, add: false);
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
    }
}
