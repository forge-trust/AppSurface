namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

public sealed class PlatformAppSurfaceLocalSecretStoreTests
{
    private const string UsrSecretTool = "/usr/bin/secret-tool";
    private const string BinSecretTool = "/bin/secret-tool";

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
            new FixedCommandRunner(PlatformAppSurfaceLocalSecretStore.PlatformSecretCommandResult.FromProcess(
                1,
                string.Empty,
                "Cannot autolaunch D-Bus without X11 $DISPLAY.")));

        var result = store.Get(Identity);

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal("local-secret-store-unavailable", result.Diagnostic?.Code);
    }

    [Fact]
    public void LinuxGet_Should_ReturnLocked_WhenRealSecretToolReportsInteractiveLock()
    {
        var store = new PlatformAppSurfaceLocalSecretStore.LinuxSecretServiceLocalSecretStore(
            UsrSecretTool,
            new FixedCommandRunner(PlatformAppSurfaceLocalSecretStore.PlatformSecretCommandResult.FromProcess(
                1,
                string.Empty,
                "User interaction is not allowed while the collection is locked.")));

        var result = store.Get(Identity);

        Assert.Equal(LocalSecretResultStatus.Locked, result.Status);
        Assert.Equal("local-secret-store-locked", result.Diagnostic?.Code);
    }

    [Fact]
    public void LinuxGet_Should_ReturnUnavailable_WhenSecretToolTimesOut()
    {
        var store = new PlatformAppSurfaceLocalSecretStore.LinuxSecretServiceLocalSecretStore(
            UsrSecretTool,
            new FixedCommandRunner(PlatformAppSurfaceLocalSecretStore.PlatformSecretCommandResult.TimedOut));

        var result = store.Get(Identity);

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal("local-secret-store-unavailable", result.Diagnostic?.Code);
        Assert.True(result.Diagnostic?.Retryable);
    }

    [Theory]
    [InlineData("permission denied before launch")]
    [InlineData("the local collection is locked before launch")]
    [InlineData("/tmp/appsurface/secret-tool could not be executed")]
    public void LinuxGet_Should_ReturnUnavailable_WhenSecretToolCannotStart(string message)
    {
        var exception = new StartFailureException(message, unchecked((int)0x80070005));
        var store = new PlatformAppSurfaceLocalSecretStore.LinuxSecretServiceLocalSecretStore(
            UsrSecretTool,
            new FixedCommandRunner(PlatformAppSurfaceLocalSecretStore.PlatformSecretCommandResult.StartFailed(exception)));

        var result = store.Get(Identity);

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal("local-secret-store-unavailable", result.Diagnostic?.Code);
        Assert.Contains("ExitCode=-2", result.Diagnostic?.Cause, StringComparison.Ordinal);
        Assert.Contains(typeof(StartFailureException).FullName!, result.Diagnostic?.Cause, StringComparison.Ordinal);
        Assert.DoesNotContain(message, result.Diagnostic?.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxGet_Should_ReturnUnavailable_WhenSyntheticStartFailureHasNoError()
    {
        var store = new PlatformAppSurfaceLocalSecretStore.LinuxSecretServiceLocalSecretStore(
            UsrSecretTool,
            new FixedCommandRunner(new PlatformAppSurfaceLocalSecretStore.PlatformSecretCommandResult(
                PlatformAppSurfaceLocalSecretStore.PlatformSecretCommandResult.StartFailedExitCode,
                string.Empty,
                string.Empty,
                PlatformAppSurfaceLocalSecretStore.PlatformSecretCommandResultKind.StartFailed)));

        var result = store.Get(Identity);

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal("local-secret-store-unavailable", result.Diagnostic?.Code);
        Assert.Contains("No additional startup detail was reported; ExitCode=-2.", result.Diagnostic?.Cause, StringComparison.Ordinal);
        Assert.DoesNotContain(". ;", result.Diagnostic?.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxDelete_Should_ReturnUnavailable_WhenSyntheticStartFailureHasNoError()
    {
        var store = new PlatformAppSurfaceLocalSecretStore.LinuxSecretServiceLocalSecretStore(
            UsrSecretTool,
            new FixedCommandRunner(new PlatformAppSurfaceLocalSecretStore.PlatformSecretCommandResult(
                PlatformAppSurfaceLocalSecretStore.PlatformSecretCommandResult.StartFailedExitCode,
                string.Empty,
                string.Empty,
                PlatformAppSurfaceLocalSecretStore.PlatformSecretCommandResultKind.StartFailed)));

        var result = store.Delete(Identity);

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal("local-secret-store-unavailable", result.Diagnostic?.Code);
    }

    [Fact]
    public void PlatformSecretCommandResult_Should_SanitizeStartFailureException()
    {
        var rawMessage = $"permission denied at {Path.Join(Path.GetTempPath(), "secret-tool")}";
        var exception = new StartFailureException(rawMessage, unchecked((int)0x80070005));

        var result = PlatformAppSurfaceLocalSecretStore.PlatformSecretCommandResult.StartFailed(exception);

        Assert.Equal(PlatformAppSurfaceLocalSecretStore.PlatformSecretCommandResultKind.StartFailed, result.Kind);
        Assert.Equal(PlatformAppSurfaceLocalSecretStore.PlatformSecretCommandResult.StartFailedExitCode, result.ExitCode);
        Assert.Contains(typeof(StartFailureException).FullName!, result.Error, StringComparison.Ordinal);
        Assert.Contains($"HResult={exception.HResult}", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(rawMessage, result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetTempPath(), result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void PlatformSecretCommandResult_Should_RejectSyntheticExitCodesForProcessOutput()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => PlatformAppSurfaceLocalSecretStore.PlatformSecretCommandResult.FromProcess(
                PlatformAppSurfaceLocalSecretStore.PlatformSecretCommandResult.StartFailedExitCode,
                string.Empty,
                string.Empty));

        Assert.Equal("exitCode", error.ParamName);
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
        Assert.Equal(PlatformAppSurfaceLocalSecretStore.PlatformSecretCommandResultKind.TimedOut, result.Kind);
        Assert.Empty(result.Output);
        Assert.Contains("Timed out", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultCommandRunner_Should_ReturnStartFailedResult_WhenCommandCannotStart()
    {
        var runner = new PlatformAppSurfaceLocalSecretStore.DefaultPlatformSecretCommandRunner(TimeSpan.FromSeconds(1));

        var result = runner.Run(Path.Join(Path.GetTempPath(), $"appsurface-missing-command-{Guid.NewGuid():N}"), [], null);

        Assert.Equal(PlatformAppSurfaceLocalSecretStore.PlatformSecretCommandResult.StartFailedExitCode, result.ExitCode);
        Assert.Equal(PlatformAppSurfaceLocalSecretStore.PlatformSecretCommandResultKind.StartFailed, result.Kind);
        Assert.Empty(result.Output);
        Assert.Contains("ExceptionType=", result.Error, StringComparison.Ordinal);
        Assert.Contains("HResult=", result.Error, StringComparison.Ordinal);
        Assert.Contains("Could not start", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxSecretToolResolver_Should_SelectUsrBinBeforeBin_WhenBothTrustedCandidatesExist()
    {
        var resolver = Resolver(new Dictionary<string, PlatformAppSurfaceLocalSecretStore.LinuxSecretToolPathState>
        {
            [UsrSecretTool] = PlatformAppSurfaceLocalSecretStore.LinuxSecretToolPathState.ExecutableFile,
            [BinSecretTool] = PlatformAppSurfaceLocalSecretStore.LinuxSecretToolPathState.ExecutableFile
        });

        var result = resolver.Resolve(null);

        Assert.True(result.Succeeded);
        Assert.Equal(UsrSecretTool, result.Path);
    }

    [Fact]
    public void LinuxSecretToolResolver_Should_FallBackToBin_WhenUsrBinCandidateIsMissing()
    {
        var resolver = Resolver(new Dictionary<string, PlatformAppSurfaceLocalSecretStore.LinuxSecretToolPathState>
        {
            [UsrSecretTool] = PlatformAppSurfaceLocalSecretStore.LinuxSecretToolPathState.Missing,
            [BinSecretTool] = PlatformAppSurfaceLocalSecretStore.LinuxSecretToolPathState.ExecutableFile
        });

        var result = resolver.Resolve(null);

        Assert.True(result.Succeeded);
        Assert.Equal(BinSecretTool, result.Path);
    }

    [Fact]
    public void LinuxSecretToolResolver_Should_IgnorePathCandidateAndExplainWhy()
    {
        var fakePath = "/tmp/appsurface-fake/secret-tool";
        var resolver = Resolver(
            new Dictionary<string, PlatformAppSurfaceLocalSecretStore.LinuxSecretToolPathState>
            {
                [UsrSecretTool] = PlatformAppSurfaceLocalSecretStore.LinuxSecretToolPathState.Missing,
                [BinSecretTool] = PlatformAppSurfaceLocalSecretStore.LinuxSecretToolPathState.Missing,
                [fakePath] = PlatformAppSurfaceLocalSecretStore.LinuxSecretToolPathState.ExecutableFile
            },
            "/tmp/appsurface-fake");

        var result = resolver.Resolve(null);

        Assert.False(result.Succeeded);
        Assert.Equal(LocalSecretResultStatus.UnsupportedPlatform, result.Status);
        Assert.Equal("local-secret-store-command-untrusted", result.Diagnostic?.Code);
        Assert.Contains(fakePath, result.Diagnostic?.Cause, StringComparison.Ordinal);
        Assert.Contains("ignores PATH", result.Diagnostic?.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxSecretToolResolver_Should_ExplainTrustedCandidates_WhenPathHasNoSecretTool()
    {
        var resolver = Resolver(new Dictionary<string, PlatformAppSurfaceLocalSecretStore.LinuxSecretToolPathState>
        {
            [UsrSecretTool] = PlatformAppSurfaceLocalSecretStore.LinuxSecretToolPathState.Missing,
            [BinSecretTool] = PlatformAppSurfaceLocalSecretStore.LinuxSecretToolPathState.Missing
        });

        var result = resolver.Resolve(null);

        Assert.False(result.Succeeded);
        Assert.Equal(LocalSecretResultStatus.UnsupportedPlatform, result.Status);
        Assert.Equal("local-secret-store-command-untrusted", result.Diagnostic?.Code);
        Assert.Contains("/usr/bin/secret-tool", result.Diagnostic?.Cause, StringComparison.Ordinal);
        Assert.Contains("/bin/secret-tool", result.Diagnostic?.Cause, StringComparison.Ordinal);
        Assert.Contains("does not search PATH", result.Diagnostic?.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxSecretToolResolver_Should_AcceptAbsoluteExecutableOverride()
    {
        var overridePath = "/nix/store/appsurface-secret-tool/bin/secret-tool";
        var resolver = Resolver(new Dictionary<string, PlatformAppSurfaceLocalSecretStore.LinuxSecretToolPathState>
        {
            [overridePath] = PlatformAppSurfaceLocalSecretStore.LinuxSecretToolPathState.ExecutableFile
        });

        var result = resolver.Resolve(overridePath);

        Assert.True(result.Succeeded);
        Assert.Equal(overridePath, result.Path);
    }

    [Fact]
    public void LinuxSecretToolResolverDefault_Should_InspectOverridePathState()
    {
        var tempPath = Path.Join(Path.GetTempPath(), $"appsurface-secret-tool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempPath);
        try
        {
            var executablePath = Path.Join(tempPath, "secret-tool");
            var notExecutablePath = Path.Join(tempPath, "secret-tool-not-executable");
            var directoryPath = Path.Join(tempPath, "secret-tool-dir");
            File.WriteAllText(executablePath, "#!/bin/sh\nexit 0\n");
            File.WriteAllText(notExecutablePath, "#!/bin/sh\nexit 0\n");
            Directory.CreateDirectory(directoryPath);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    executablePath,
                    UnixFileMode.UserRead | UnixFileMode.UserExecute);
                File.SetUnixFileMode(notExecutablePath, UnixFileMode.UserRead);
            }

            var executable = PlatformAppSurfaceLocalSecretStore.LinuxSecretToolResolver.Default.Resolve(executablePath);
            var missing = PlatformAppSurfaceLocalSecretStore.LinuxSecretToolResolver.Default.Resolve(Path.Join(tempPath, "missing-secret-tool"));
            var directory = PlatformAppSurfaceLocalSecretStore.LinuxSecretToolResolver.Default.Resolve(directoryPath);
            var notExecutable = PlatformAppSurfaceLocalSecretStore.LinuxSecretToolResolver.Default.Resolve(notExecutablePath);

            Assert.True(executable.Succeeded);
            Assert.Equal(executablePath, executable.Path);
            Assert.Equal("local-secret-store-command-invalid", missing.Diagnostic?.Code);
            Assert.Contains("does not exist", missing.Diagnostic?.Cause, StringComparison.Ordinal);
            Assert.Contains("directory", directory.Diagnostic?.Cause, StringComparison.Ordinal);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Contains("not executable", notExecutable.Diagnostic?.Cause, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(tempPath, recursive: true);
        }
    }

    [Fact]
    public void CreateInnerStoreForTests_Should_PassConfiguredLinuxSecretToolPathToResolver_WhenLinux()
    {
        var overridePath = "/nix/store/appsurface-secret-tool/bin/secret-tool";
        string? inspectedPath = null;
        var resolver = new PlatformAppSurfaceLocalSecretStore.LinuxSecretToolResolver(
            [UsrSecretTool, BinSecretTool],
            path =>
            {
                inspectedPath = path;
                return PlatformAppSurfaceLocalSecretStore.LinuxSecretToolPathState.ExecutableFile;
            },
            () => null);

        var store = PlatformAppSurfaceLocalSecretStore.CreateInnerStoreForTests(
            new AppSurfaceLocalSecretsOptions
            {
                LinuxSecretToolPath = overridePath
            },
            resolver,
            PlatformAppSurfaceLocalSecretStore.LocalSecretsPlatform.Linux);

        Assert.Equal("Linux Secret Service", store.Name);
        Assert.Equal(overridePath, inspectedPath);
    }

    [Fact]
    public void CreateInnerStoreForTests_Should_ReturnDiagnosticStore_WhenLinuxResolverFails()
    {
        var resolver = Resolver(new Dictionary<string, PlatformAppSurfaceLocalSecretStore.LinuxSecretToolPathState>
        {
            [UsrSecretTool] = PlatformAppSurfaceLocalSecretStore.LinuxSecretToolPathState.Missing,
            [BinSecretTool] = PlatformAppSurfaceLocalSecretStore.LinuxSecretToolPathState.Missing
        });

        var store = PlatformAppSurfaceLocalSecretStore.CreateInnerStoreForTests(
            new AppSurfaceLocalSecretsOptions(),
            resolver,
            PlatformAppSurfaceLocalSecretStore.LocalSecretsPlatform.Linux);

        var result = store.Get(Identity);

        Assert.Equal(LocalSecretResultStatus.UnsupportedPlatform, result.Status);
        Assert.Equal("local-secret-store-command-untrusted", result.Diagnostic?.Code);
        Assert.Equal(store.Name, result.Source);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("relative/secret-tool", "not absolute")]
    [InlineData("/missing/secret-tool", "does not exist")]
    [InlineData("/opt/secret-tool-dir", "directory")]
    [InlineData("/opt/secret-tool-not-executable", "not executable")]
    public void LinuxSecretToolResolver_Should_RejectInvalidOverrides(string overridePath, string expectedCause)
    {
        var resolver = Resolver(new Dictionary<string, PlatformAppSurfaceLocalSecretStore.LinuxSecretToolPathState>
        {
            ["/opt/secret-tool-dir"] = PlatformAppSurfaceLocalSecretStore.LinuxSecretToolPathState.Directory,
            ["/opt/secret-tool-not-executable"] = PlatformAppSurfaceLocalSecretStore.LinuxSecretToolPathState.NotExecutableFile
        });

        var result = resolver.Resolve(overridePath);

        Assert.False(result.Succeeded);
        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal("local-secret-store-command-invalid", result.Diagnostic?.Code);
        Assert.Contains(expectedCause, result.Diagnostic?.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxSecretToolResolver_Should_ReturnUnsupportedDiagnostic_WhenOverrideIsConfiguredOffLinux()
    {
        var result = PlatformAppSurfaceLocalSecretStore.LinuxSecretToolResolver.UnsupportedPlatformOverride("/usr/local/bin/secret-tool");

        Assert.False(result.Succeeded);
        Assert.Equal(LocalSecretResultStatus.UnsupportedPlatform, result.Status);
        Assert.Equal("local-secret-store-command-unsupported", result.Diagnostic?.Code);
        Assert.Contains("Linux-only", result.Diagnostic?.Cause, StringComparison.Ordinal);
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
    public void IndexedStoreSet_Should_WriteCaseVariantIndexInDeterministicOrder()
    {
        var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
        var store = new IndexedMemoryStore();
        var lower = normalizer.Normalize("MyApp", "Development", null, "stripe:apikey").Identity!;
        var upper = normalizer.Normalize("MyApp", "Development", null, "Stripe:ApiKey").Identity!;

        store.Set(lower, "lower-secret");
        store.Set(upper, "upper-secret");

        Assert.Equal(["Stripe:ApiKey", "stripe:apikey"], store.ReadIndexKeys("MyApp", "Development", null));
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

    private sealed class StartFailureException : Exception
    {
        public StartFailureException(string message, int hResult)
            : base(message)
        {
            HResult = hResult;
        }
    }

    private static PlatformAppSurfaceLocalSecretStore.LinuxSecretToolResolver Resolver(
        IReadOnlyDictionary<string, PlatformAppSurfaceLocalSecretStore.LinuxSecretToolPathState> paths,
        string? pathEnvironment = null) =>
        new(
            [UsrSecretTool, BinSecretTool],
            path => paths.GetValueOrDefault(path, PlatformAppSurfaceLocalSecretStore.LinuxSecretToolPathState.Missing),
            () => pathEnvironment);

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
