namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

public sealed class FileAppSurfaceLocalSecretStoreTests
{
    private const UnixFileMode SecretDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode SecretFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    [Fact]
    public void SetGetListDelete_Should_WorkWithoutPrintingSecretInResults()
    {
        using var temp = TempDirectory.Create();
        var store = new FileAppSurfaceLocalSecretStore(Path.Join(temp.Path, "secrets.json"));
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var set = store.Set(identity, "sk_test_secret");
        var get = store.Get(identity);
        var list = store.List("MyApp", "Development", null);
        var delete = store.Delete(identity);
        var missing = store.Get(identity);

        Assert.Equal(LocalSecretResultStatus.Found, set.Status);
        Assert.Equal("sk_test_secret", get.Value);
        Assert.Contains("Stripe:ApiKey", list.Keys);
        Assert.Equal(LocalSecretResultStatus.Found, delete.Status);
        Assert.Equal(LocalSecretResultStatus.Missing, missing.Status);
        Assert.DoesNotContain("sk_test_secret", set.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sk_test_secret", delete.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Delete_Should_ReturnMissing_WhenKeyDoesNotExist()
    {
        using var temp = TempDirectory.Create();
        var store = new FileAppSurfaceLocalSecretStore(Path.Join(temp.Path, "secrets.json"));
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var result = store.Delete(identity);

        Assert.Equal(LocalSecretResultStatus.Missing, result.Status);
    }

    [Fact]
    public void Doctor_Should_CreateFileAndReturnReadinessDiagnostic()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Join(temp.Path, "nested", "secrets.json");
        var store = new FileAppSurfaceLocalSecretStore(path);

        var result = store.Doctor("MyApp", "Development", null);

        Assert.Equal(LocalSecretResultStatus.Missing, result.Status);
        Assert.Equal(IsUnix() ? "local-secret-store-ready" : "local-secret-file-posture-degraded", result.Diagnostic?.Code);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Doctor_Should_RejectExistingLooseUnixDirectoryWithoutChangingMode()
    {
        if (!IsUnix())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var directory = Path.Join(temp.Path, "nested");
        var path = Path.Join(directory, "secrets.json");
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, "{}");
        new DirectoryInfo(directory).UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        new FileInfo(path).UnixFileMode = SecretFileMode;
        var store = new FileAppSurfaceLocalSecretStore(path);

        var result = store.Doctor("MyApp", "Development", null);

        Assert.Equal(LocalSecretResultStatus.UnsupportedPlatform, result.Status);
        Assert.Equal("local-secret-file-posture-degraded", result.Diagnostic?.Code);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead, new DirectoryInfo(directory).UnixFileMode);
        Assert.Equal(SecretFileMode, new FileInfo(path).UnixFileMode);
    }

    [Fact]
    public void Doctor_Should_RepairExistingLooseUnixFileInSecureDirectory()
    {
        if (!IsUnix())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var directory = Path.Join(temp.Path, "nested");
        var path = Path.Join(directory, "secrets.json");
        Directory.CreateDirectory(directory, SecretDirectoryMode);
        File.WriteAllText(path, "{}");
        new FileInfo(path).UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var store = new FileAppSurfaceLocalSecretStore(path);

        var result = store.Doctor("MyApp", "Development", null);

        Assert.Equal(LocalSecretResultStatus.Missing, result.Status);
        Assert.Equal("local-secret-file-posture-repaired", result.Diagnostic?.Code);
        Assert.Equal(SecretDirectoryMode, new DirectoryInfo(directory).UnixFileMode);
        Assert.Equal(SecretFileMode, new FileInfo(path).UnixFileMode);
    }

    [Fact]
    public void Set_Should_CreateUnixFileAndDirectoryWithRestrictiveModes()
    {
        if (!IsUnix())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var directory = Path.Join(temp.Path, "nested");
        var path = Path.Join(directory, "secrets.json");
        var store = new FileAppSurfaceLocalSecretStore(path);
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var result = store.Set(identity, "sk_test_secret");

        Assert.Equal(LocalSecretResultStatus.Found, result.Status);
        Assert.Equal(SecretDirectoryMode, new DirectoryInfo(directory).UnixFileMode);
        Assert.Equal(SecretFileMode, new FileInfo(path).UnixFileMode);
    }

    [Fact]
    public void Set_Should_RejectExistingLooseParentDirectoryWithoutChangingMode()
    {
        if (!IsUnix())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var directory = Path.Join(temp.Path, "shared");
        Directory.CreateDirectory(directory);
        var looseMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        new DirectoryInfo(directory).UnixFileMode = looseMode;
        var path = Path.Join(directory, "secrets.json");
        var store = new FileAppSurfaceLocalSecretStore(path);
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var result = store.Set(identity, "sk_test_secret");

        Assert.Equal(LocalSecretResultStatus.UnsupportedPlatform, result.Status);
        Assert.Equal("local-secret-file-posture-degraded", result.Diagnostic?.Code);
        Assert.Equal(looseMode, new DirectoryInfo(directory).UnixFileMode);
        Assert.False(File.Exists(path));
        Assert.DoesNotContain("sk_test_secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GetDefaultPath_Should_ReturnAppSurfaceLocalSecretsPath()
    {
        var path = FileAppSurfaceLocalSecretStore.GetDefaultPath();

        Assert.EndsWith(Path.Join("AppSurface", "local-secrets.json"), path, StringComparison.Ordinal);
    }

    [Fact]
    public void List_Should_FilterByPrefixAndReturnEmptyForNullJsonDocument()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Join(temp.Path, "secrets.json");
        var store = new FileAppSurfaceLocalSecretStore(path);
        var normalizer = new AppSurfaceLocalSecretIdentityNormalizer();
        store.Set(normalizer.Normalize("MyApp", "Development", "Payments", "Stripe:ApiKey").Identity!, "stripe");
        store.Set(normalizer.Normalize("MyApp", "Development", null, "SendGrid:ApiKey").Identity!, "sendgrid");

        var prefixed = store.List("MyApp", "Development", "Payments");
        var unprefixed = store.List("MyApp", "Development", null);
        File.WriteAllText(path, "null");
        var empty = store.List("MyApp", "Development", null);

        Assert.Equal(["Stripe:ApiKey"], prefixed.Keys);
        Assert.Equal(["SendGrid:ApiKey"], unprefixed.Keys);
        Assert.Empty(empty.Keys);
    }

    [Fact]
    public void ReadOperations_Should_ReturnPasteSafeDiagnostic_WhenFileContainsInvalidJson()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Join(temp.Path, "secrets.json");
        File.WriteAllText(path, "{not-json: raw-secret}");
        if (IsUnix())
        {
            new FileInfo(path).UnixFileMode = SecretFileMode;
        }

        var store = new FileAppSurfaceLocalSecretStore(path);
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var get = store.Get(identity);
        var list = store.List("MyApp", "Development", null);

        Assert.Equal(LocalSecretResultStatus.ProviderFailed, get.Status);
        Assert.Equal(LocalSecretResultStatus.ProviderFailed, list.Status);
        Assert.Equal("local-secret-store-invalid", get.Diagnostic?.Code);
        Assert.Equal("local-secret-store-invalid", list.Diagnostic?.Code);
        Assert.DoesNotContain("raw-secret", get.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("raw-secret", list.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Get_Should_ReturnPostureDiagnostic_WhenExistingUnixFileModeIsLoose()
    {
        if (!IsUnix())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        new DirectoryInfo(temp.Path).UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var path = Path.Join(temp.Path, "secrets.json");
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;
        File.WriteAllText(
            path,
            """
            {
              "appsurface:MyApp:Development:Stripe:ApiKey": {
                "ApplicationName": "MyApp",
                "Environment": "Development",
                "KeyPrefix": null,
                "Key": "Stripe:ApiKey",
                "Value": "sk_test_secret"
              }
            }
            """);
        new FileInfo(path).UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        var store = new FileAppSurfaceLocalSecretStore(path);

        var result = store.Get(identity);

        Assert.Equal(LocalSecretResultStatus.UnsupportedPlatform, result.Status);
        Assert.Equal("local-secret-file-posture-degraded", result.Diagnostic?.Code);
        Assert.Null(result.Value);
        Assert.DoesNotContain("sk_test_secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Get_Should_ReturnPostureDiagnostic_WhenExistingUnixFileModeHasOwnerExecute()
    {
        if (!IsUnix())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var path = Path.Join(temp.Path, "secrets.json");
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;
        File.WriteAllText(path, ToSecretJson("sk_test_secret"));
        new FileInfo(path).UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var store = new FileAppSurfaceLocalSecretStore(path);

        var result = store.Get(identity);

        Assert.Equal(LocalSecretResultStatus.UnsupportedPlatform, result.Status);
        Assert.Equal("local-secret-file-posture-degraded", result.Diagnostic?.Code);
        Assert.Null(result.Value);
        Assert.DoesNotContain("sk_test_secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Set_Should_RejectSymlinkPathWithoutChangingTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var target = Path.Join(temp.Path, "target.json");
        var link = Path.Join(temp.Path, "linked-secrets.json");
        File.WriteAllText(target, "{}");
        File.CreateSymbolicLink(link, target);
        var store = new FileAppSurfaceLocalSecretStore(link);
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var result = store.Set(identity, "sk_test_secret");

        Assert.Equal(LocalSecretResultStatus.UnsupportedPlatform, result.Status);
        Assert.Equal("local-secret-file-posture-unsupported", result.Diagnostic?.Code);
        Assert.Equal("{}", File.ReadAllText(target));
        Assert.DoesNotContain("sk_test_secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Get_Should_RejectSymlinkPathWithoutReturningTargetValue()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var target = Path.Join(temp.Path, "target.json");
        var link = Path.Join(temp.Path, "linked-secrets.json");
        File.WriteAllText(target, ToSecretJson("sk_test_secret"));
        if (IsUnix())
        {
            new FileInfo(target).UnixFileMode = SecretFileMode;
        }

        File.CreateSymbolicLink(link, target);
        var store = new FileAppSurfaceLocalSecretStore(link);
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var result = store.Get(identity);

        Assert.Equal(LocalSecretResultStatus.UnsupportedPlatform, result.Status);
        Assert.Equal("local-secret-file-posture-unsupported", result.Diagnostic?.Code);
        Assert.Null(result.Value);
        Assert.DoesNotContain("sk_test_secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Set_Should_RejectSymlinkDirectoryWithoutChangingTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var targetDirectory = Path.Join(temp.Path, "target");
        var nestedDirectory = Path.Join(targetDirectory, "nested");
        var linkDirectory = Path.Join(temp.Path, "linked-directory");
        Directory.CreateDirectory(nestedDirectory);
        Directory.CreateSymbolicLink(linkDirectory, targetDirectory);
        var path = Path.Join(linkDirectory, "nested", "secrets.json");
        var store = new FileAppSurfaceLocalSecretStore(path);
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var result = store.Set(identity, "sk_test_secret");

        Assert.Equal(LocalSecretResultStatus.UnsupportedPlatform, result.Status);
        Assert.Equal("local-secret-file-posture-unsupported", result.Diagnostic?.Code);
        Assert.False(File.Exists(Path.Join(nestedDirectory, "secrets.json")));
        Assert.DoesNotContain("sk_test_secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Get_Should_NotRead_WhenExistingPostureRejectsPath()
    {
        var readCalled = false;
        var store = new FileAppSurfaceLocalSecretStore(
            "secrets.json",
            new ThrowingFileSystem(
                read: () =>
                {
                    readCalled = true;
                    return ToSecretJson("sk_test_secret");
                },
                existingFilePosture: () => FileSecretPostureResult.Unsupported(
                    "local-secret-file-posture-degraded",
                    "Local secret file posture is degraded.",
                    "The fallback path is not safe to read.",
                    "Choose a normal per-user file path.")));
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var result = store.Get(identity);

        Assert.Equal(LocalSecretResultStatus.UnsupportedPlatform, result.Status);
        Assert.False(readCalled);
        Assert.DoesNotContain("sk_test_secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Set_Should_NotRead_WhenWritePreflightRejectsPath()
    {
        var readCalled = false;
        var store = new FileAppSurfaceLocalSecretStore(
            "secrets.json",
            new ThrowingFileSystem(
                read: () =>
                {
                    readCalled = true;
                    return "{}";
                },
                prepareWrite: () => FileSecretPostureResult.Unsupported(
                    "local-secret-file-posture-unsupported",
                    "Local secret file path is unsupported.",
                    "The fallback path is not safe to read or write.",
                    "Choose a normal per-user file path.")));
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var result = store.Set(identity, "sk_test_secret");

        Assert.Equal(LocalSecretResultStatus.UnsupportedPlatform, result.Status);
        Assert.False(readCalled);
        Assert.DoesNotContain("sk_test_secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Get_Should_ReturnPostureFailure_WhenPathBecomesUnsafeAfterRead()
    {
        var postureChecks = 0;
        var store = new FileAppSurfaceLocalSecretStore(
            "secrets.json",
            new ThrowingFileSystem(
                read: () => ToSecretJson("sk_test_secret"),
                existingFilePosture: () => ++postureChecks == 1
                    ? FileSecretPostureResult.Ready()
                    : FileSecretPostureResult.Unsupported(
                        "local-secret-file-posture-degraded",
                        "Local secret file posture is degraded.",
                        "The fallback path became unsafe before returning the value.",
                        "Choose a normal per-user file path.")));
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var result = store.Get(identity);

        Assert.Equal(LocalSecretResultStatus.UnsupportedPlatform, result.Status);
        Assert.Equal("local-secret-file-posture-degraded", result.Diagnostic?.Code);
        Assert.Equal(2, postureChecks);
        Assert.Null(result.Value);
        Assert.DoesNotContain("sk_test_secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void List_Should_ReturnPostureFailure_WhenPathBecomesUnsafeAfterRead()
    {
        var postureChecks = 0;
        var store = new FileAppSurfaceLocalSecretStore(
            "secrets.json",
            new ThrowingFileSystem(
                read: () => ToSecretJson("sk_test_secret"),
                existingFilePosture: () => ++postureChecks == 1
                    ? FileSecretPostureResult.Ready()
                    : FileSecretPostureResult.Unsupported(
                        "local-secret-file-posture-degraded",
                        "Local secret file posture is degraded.",
                        "The fallback path became unsafe before returning key names.",
                        "Choose a normal per-user file path.")));

        var result = store.List("MyApp", "Development", null);

        Assert.Equal(LocalSecretResultStatus.UnsupportedPlatform, result.Status);
        Assert.Equal("local-secret-file-posture-degraded", result.Diagnostic?.Code);
        Assert.Equal(2, postureChecks);
        Assert.Empty(result.Keys);
    }

    [Fact]
    public void Set_Should_ReturnPostureFailure_WhenWriteRejectsAfterRead()
    {
        var writeCalled = false;
        var store = new FileAppSurfaceLocalSecretStore(
            "secrets.json",
            new ThrowingFileSystem(
                write: _ => writeCalled = true,
                writePosture: () => FileSecretPostureResult.Unsupported(
                    "local-secret-file-posture-unsupported",
                    "Local secret file path is unsupported.",
                    "The fallback path became unsafe before replacing the file.",
                    "Choose a normal per-user file path.")));
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var result = store.Set(identity, "sk_test_secret");

        Assert.Equal(LocalSecretResultStatus.UnsupportedPlatform, result.Status);
        Assert.Equal("local-secret-file-posture-unsupported", result.Diagnostic?.Code);
        Assert.True(writeCalled);
        Assert.DoesNotContain("sk_test_secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Set_Should_ReturnLockedDiagnostic_WhenWriteIsUnauthorized()
    {
        var store = new FileAppSurfaceLocalSecretStore(
            "secrets.json",
            new ThrowingFileSystem(write: _ => throw new UnauthorizedAccessException()));
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var result = store.Set(identity, "sk_test_secret");

        Assert.Equal(LocalSecretResultStatus.Locked, result.Status);
        Assert.Equal("local-secret-store-locked", result.Diagnostic?.Code);
        Assert.DoesNotContain("sk_test_secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Set_Should_RejectExistingNonDirectoryAncestor()
    {
        using var temp = TempDirectory.Create();
        var ancestor = Path.Join(temp.Path, "not-a-directory");
        File.WriteAllText(ancestor, "not a directory");
        var path = Path.Join(ancestor, "nested", "secrets.json");
        var store = new FileAppSurfaceLocalSecretStore(path);
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var result = store.Set(identity, "sk_test_secret");

        Assert.Equal(LocalSecretResultStatus.UnsupportedPlatform, result.Status);
        Assert.Equal("local-secret-file-posture-unsupported", result.Diagnostic?.Code);
        Assert.False(Directory.Exists(ancestor));
        Assert.DoesNotContain("sk_test_secret", File.ReadAllText(ancestor), StringComparison.Ordinal);
        Assert.DoesNotContain("sk_test_secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Set_Should_ReturnPasteSafeDiagnostic_WhenStorePathIsDirectory()
    {
        using var temp = TempDirectory.Create();
        var store = new FileAppSurfaceLocalSecretStore(temp.Path);
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var result = store.Set(identity, "sk_test_secret");

        Assert.Equal(LocalSecretResultStatus.UnsupportedPlatform, result.Status);
        Assert.Equal("local-secret-file-posture-unsupported", result.Diagnostic?.Code);
        Assert.DoesNotContain("sk_test_secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Get_Should_ReturnLockedDiagnostic_WhenReadIsUnauthorized()
    {
        var store = new FileAppSurfaceLocalSecretStore(
            "secrets.json",
            new ThrowingFileSystem(read: () => throw new UnauthorizedAccessException()));
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var result = store.Get(identity);

        Assert.Equal(LocalSecretResultStatus.Locked, result.Status);
        Assert.Equal("local-secret-store-locked", result.Diagnostic?.Code);
    }

    [Fact]
    public void List_Should_ReturnUnavailableDiagnostic_WhenReadFailsWithIoException()
    {
        var store = new FileAppSurfaceLocalSecretStore(
            "secrets.json",
            new ThrowingFileSystem(read: () => throw new IOException()));

        var result = store.List("MyApp", "Development", null);

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal("local-secret-store-unavailable", result.Diagnostic?.Code);
        Assert.True(result.Diagnostic?.Retryable);
    }

    [Fact]
    public void Set_Should_ReturnLockedDiagnostic_WhenReadIsUnauthorized()
    {
        var store = new FileAppSurfaceLocalSecretStore(
            "secrets.json",
            new ThrowingFileSystem(read: () => throw new UnauthorizedAccessException()));
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var result = store.Set(identity, "sk_test_secret");

        Assert.Equal(LocalSecretResultStatus.Locked, result.Status);
        Assert.Equal("local-secret-store-locked", result.Diagnostic?.Code);
        Assert.DoesNotContain("sk_test_secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Delete_Should_ReturnUnavailableDiagnostic_WhenReadFailsWithIoException()
    {
        var store = new FileAppSurfaceLocalSecretStore(
            "secrets.json",
            new ThrowingFileSystem(read: () => throw new IOException()));
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var result = store.Delete(identity);

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal("local-secret-store-unavailable", result.Diagnostic?.Code);
        Assert.True(result.Diagnostic?.Retryable);
    }

    [Fact]
    public void Delete_Should_ReturnUnavailableDiagnostic_WhenWriteFailsWithIoException()
    {
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;
        var entry = """
            {
              "appsurface:MyApp:Development:Stripe:ApiKey": {
                "ApplicationName": "MyApp",
                "Environment": "Development",
                "KeyPrefix": null,
                "Key": "Stripe:ApiKey",
                "Value": "sk_test_secret"
              }
            }
            """;
        var store = new FileAppSurfaceLocalSecretStore(
            "secrets.json",
            new ThrowingFileSystem(read: () => entry, write: _ => throw new IOException()));

        var result = store.Delete(identity);

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal("local-secret-store-unavailable", result.Diagnostic?.Code);
        Assert.DoesNotContain("sk_test_secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Delete_Should_RepairLooseUnixFileModeInSecureDirectory()
    {
        if (!IsUnix())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var path = Path.Join(temp.Path, "secrets.json");
        File.WriteAllText(path, ToSecretJson("sk_test_secret"));
        new FileInfo(path).UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        var store = new FileAppSurfaceLocalSecretStore(path);
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var result = store.Delete(identity);

        Assert.Equal(LocalSecretResultStatus.Found, result.Status);
        Assert.Equal(SecretDirectoryMode, new DirectoryInfo(temp.Path).UnixFileMode);
        Assert.Equal(SecretFileMode, new FileInfo(path).UnixFileMode);
        Assert.Empty(store.List("MyApp", "Development", null).Keys);
        Assert.DoesNotContain("sk_test_secret", result.ToString(), StringComparison.Ordinal);
    }

    private static string ToSecretJson(string value) =>
        $$"""
          {
            "appsurface:MyApp:Development:Stripe:ApiKey": {
              "ApplicationName": "MyApp",
              "Environment": "Development",
              "KeyPrefix": null,
              "Key": "Stripe:ApiKey",
              "Value": "{{value}}"
            }
          }
          """;

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Join(System.IO.Path.GetTempPath(), $"appsurface-local-secrets-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            if (IsUnix())
            {
                new DirectoryInfo(path).UnixFileMode = SecretDirectoryMode;
            }

            return new TempDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatformGuard("windows")]
    private static bool IsUnix() => !OperatingSystem.IsWindows();

    private sealed class ThrowingFileSystem(
        Func<string>? read = null,
        Action<string>? write = null,
        Func<FileSecretPostureResult>? readPath = null,
        Func<FileSecretPostureResult>? existingFilePosture = null,
        Func<FileSecretPostureResult>? prepareWrite = null,
        Func<FileSecretPostureResult>? writePosture = null,
        Func<FileSecretPostureResult>? doctor = null) : IFileAppSurfaceLocalSecretStoreFileSystem
    {
        public bool FileExists(string path) => true;

        public string ReadAllText(string path) => read?.Invoke() ?? "{}";

        public FileSecretPostureResult InspectReadPath(string path) => readPath?.Invoke() ?? FileSecretPostureResult.Ready();

        public FileSecretPostureResult InspectExistingFilePosture(string path) =>
            existingFilePosture?.Invoke() ?? FileSecretPostureResult.Ready();

        public FileSecretPostureResult PrepareWrite(string path) => prepareWrite?.Invoke() ?? FileSecretPostureResult.Ready();

        public FileSecretPostureResult WriteAllTextWithPosture(string path, string contents)
        {
            (write ?? (_ => { }))(contents);
            return writePosture?.Invoke() ?? FileSecretPostureResult.Ready();
        }

        public FileSecretPostureResult Doctor(string path) => doctor?.Invoke() ?? FileSecretPostureResult.Ready();
    }
}
