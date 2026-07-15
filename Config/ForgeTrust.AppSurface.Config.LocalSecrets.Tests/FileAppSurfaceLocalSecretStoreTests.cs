namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FileAppSurfaceLocalSecretStoreCollection
{
    public const string Name = "FileAppSurfaceLocalSecretStore process state";
}

[Collection(FileAppSurfaceLocalSecretStoreCollection.Name)]
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
    public void Probe_Should_ReportExistenceWithoutReturningSecretValue()
    {
        using var temp = TempDirectory.Create();
        var store = new FileAppSurfaceLocalSecretStore(Path.Join(temp.Path, "secrets.json"));
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var set = store.Set(identity, "sk_test_secret");
        var probe = store.Probe(identity);

        Assert.Equal(LocalSecretResultStatus.Found, set.Status);
        Assert.Equal(LocalSecretResultStatus.Found, probe.Status);
        Assert.Equal(string.Empty, probe.Value);
        Assert.DoesNotContain("sk_test_secret", probe.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_Should_FindStorageNamesWithJsonEscapedCharacters()
    {
        using var temp = TempDirectory.Create();
        var store = new FileAppSurfaceLocalSecretStore(Path.Join(temp.Path, "secrets.json"));
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:\"Snow-雪")
            .Identity!;

        Assert.Equal(LocalSecretResultStatus.Found, store.Set(identity, "sentinel-local-secret").Status);

        var probe = store.Probe(identity);

        Assert.Equal(LocalSecretResultStatus.Found, probe.Status);
        Assert.Equal(string.Empty, probe.Value);
        Assert.DoesNotContain("sentinel-local-secret", probe.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_Should_NotReadStoredValueObject_WhenStorageNameMatches()
    {
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;
        var json = "{\"" + identity.StorageName + "\":{\"ApplicationName\":\"MyApp\",\"Environment\":\"Development\",\"KeyPrefix\":null,\"Key\":\"Stripe:ApiKey\",\"Value\":\"sk_test_secret\"}}";
        var valueObjectOffset = System.Text.Encoding.UTF8.GetByteCount(json[..json.IndexOf("{\"ApplicationName\"", StringComparison.Ordinal)]);
        var store = new FileAppSurfaceLocalSecretStore(
            "/tmp/appsurface-test-secrets.json",
            new ThrowingFileSystem(openRead: () => new ThrowAfterPositionStream(System.Text.Encoding.UTF8.GetBytes(json), valueObjectOffset + 1)));

        var probe = store.Probe(identity);

        Assert.Equal(LocalSecretResultStatus.Found, probe.Status);
        Assert.DoesNotContain("sk_test_secret", probe.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Probe_Should_ReturnMissing_WhenOnlyDifferentPropertyExists(bool matchesStorageNamePrefix)
    {
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;
        var propertyName = matchesStorageNamePrefix ? identity.StorageName + "-suffix" : "Ignored";
        var store = CreateProbeStore("{\"" + propertyName + "\":{}}");

        var probe = store.Probe(identity);

        Assert.Equal(LocalSecretResultStatus.Missing, probe.Status);
        Assert.Null(probe.Value);
    }

    [Fact]
    public void Probe_Should_ReturnInvalidStore_WhenMatchedValueIsMissing()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Join(temp.Path, "secrets.json");
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;
        File.WriteAllText(path, "{\"" + identity.StorageName + "\":");
        if (IsUnix())
        {
            new FileInfo(path).UnixFileMode = SecretFileMode;
        }

        var store = new FileAppSurfaceLocalSecretStore(path);

        var probe = store.Probe(identity);

        Assert.Equal(LocalSecretResultStatus.ProviderFailed, probe.Status);
        Assert.Equal("local-secret-store-invalid", probe.Diagnostic?.Code);
    }

    [Fact]
    public void Probe_Should_ReturnInvalidStore_WhenMatchedValueIsNotObject()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Join(temp.Path, "secrets.json");
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;
        File.WriteAllText(path, "{\"" + identity.StorageName + "\":true}");
        if (IsUnix())
        {
            new FileInfo(path).UnixFileMode = SecretFileMode;
        }

        var store = new FileAppSurfaceLocalSecretStore(path);

        var probe = store.Probe(identity);

        Assert.Equal(LocalSecretResultStatus.ProviderFailed, probe.Status);
        Assert.Equal("local-secret-store-invalid", probe.Diagnostic?.Code);
    }

    [Fact]
    public void Probe_Should_ReturnInvalidStore_WhenEarlierValueIsNotObject()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Join(temp.Path, "secrets.json");
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;
        File.WriteAllText(
            path,
            "{\"Other\":true,\"" + identity.StorageName + "\":{\"ApplicationName\":\"MyApp\",\"Environment\":\"Development\",\"KeyPrefix\":null,\"Key\":\"Stripe:ApiKey\",\"Value\":\"raw-secret\"}}");
        if (IsUnix())
        {
            new FileInfo(path).UnixFileMode = SecretFileMode;
        }

        var store = new FileAppSurfaceLocalSecretStore(path);

        var probe = store.Probe(identity);

        Assert.Equal(LocalSecretResultStatus.ProviderFailed, probe.Status);
        Assert.Equal("local-secret-store-invalid", probe.Diagnostic?.Code);
        Assert.DoesNotContain("raw-secret", probe.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_Should_ReturnInvalidStore_WhenEarlierValueIsMalformed()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Join(temp.Path, "secrets.json");
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;
        File.WriteAllText(
            path,
            "{\"Other\":[},\"" + identity.StorageName + "\":{\"ApplicationName\":\"MyApp\",\"Environment\":\"Development\",\"KeyPrefix\":null,\"Key\":\"Stripe:ApiKey\",\"Value\":\"raw-secret\"}}");
        if (IsUnix())
        {
            new FileInfo(path).UnixFileMode = SecretFileMode;
        }

        var store = new FileAppSurfaceLocalSecretStore(path);

        var probe = store.Probe(identity);

        Assert.Equal(LocalSecretResultStatus.ProviderFailed, probe.Status);
        Assert.Equal("local-secret-store-invalid", probe.Diagnostic?.Code);
        Assert.DoesNotContain("raw-secret", probe.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_Should_SkipEveryValidNestedJsonValueShapeBeforeMatchingEntry()
    {
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;
        var json =
            "{" +
            "\"Ignored\":{" +
            "\"text\":\"escaped \\\"quote\\\" and unicode \\u0031\"," +
            "\"object\":{\"nested\":true}," +
            "\"emptyObject\":{}," +
            "\"array\":[false,null,\"text\",-1.25e+3]," +
            "\"emptyArray\":[]," +
            "\"integer\":0," +
            "\"number\":12.5E-2" +
            "}," +
            "\"" + identity.StorageName + "\":{}" +
            "}";
        var store = CreateProbeStore(json);

        var probe = store.Probe(identity);

        Assert.Equal(LocalSecretResultStatus.Found, probe.Status);
        Assert.Equal(string.Empty, probe.Value);
    }

    [Theory]
    [MemberData(nameof(MalformedSkippedValues))]
    public void Probe_Should_ReturnInvalidStore_ForMalformedSkippedValue(string value)
    {
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;
        var store = CreateProbeStore("{\"Ignored\":{\"value\":" + value + "},\"" + identity.StorageName + "\":{}}");

        var probe = store.Probe(identity);

        Assert.Equal(LocalSecretResultStatus.ProviderFailed, probe.Status);
        Assert.Equal("local-secret-store-invalid", probe.Diagnostic?.Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Probe_Should_ReturnValueSafeFailure_ForReadExceptions(bool unauthorized)
    {
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;
        var store = new FileAppSurfaceLocalSecretStore(
            "/tmp/appsurface-test-secrets.json",
            new ThrowingFileSystem(openRead: () =>
            {
                if (unauthorized)
                {
                    throw new UnauthorizedAccessException("sentinel-local-secret");
                }

                throw new IOException("sentinel-local-secret");
            }));

        var probe = store.Probe(identity);

        Assert.Equal(unauthorized ? LocalSecretResultStatus.Locked : LocalSecretResultStatus.Unavailable, probe.Status);
        Assert.DoesNotContain("sentinel-local-secret", probe.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Probe_Should_ReturnValueSafeFailure_ForPostureInspectionExceptions(bool unauthorized)
    {
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;
        var store = new FileAppSurfaceLocalSecretStore(
            "/tmp/appsurface-test-secrets.json",
            new ThrowingFileSystem(existingFilePosture: () =>
            {
                if (unauthorized)
                {
                    throw new UnauthorizedAccessException("sentinel-local-secret");
                }

                throw new IOException("sentinel-local-secret");
            }));

        var probe = store.Probe(identity);

        Assert.Equal(unauthorized ? LocalSecretResultStatus.Locked : LocalSecretResultStatus.Unavailable, probe.Status);
        Assert.DoesNotContain("sentinel-local-secret", probe.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_Should_ReturnPostureFailure_WhenExistingFileIsUnsupported()
    {
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;
        var store = new FileAppSurfaceLocalSecretStore(
            "/tmp/appsurface-test-secrets.json",
            new ThrowingFileSystem(existingFilePosture: () => FileSecretPostureResult.Unsupported(
                "local-secret-test-posture",
                "Local secret test posture failed.",
                "The test posture is unsupported.",
                "Use a supported test posture.")));

        var probe = store.Probe(identity);

        Assert.Equal(LocalSecretResultStatus.UnsupportedPlatform, probe.Status);
        Assert.Equal("local-secret-test-posture", probe.Diagnostic?.Code);
    }

    [Theory]
    [MemberData(nameof(ValidEscapedPropertyDocuments))]
    public void Probe_Should_AcceptEveryJsonPropertyEscape(string json)
    {
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var probe = CreateProbeStore(json).Probe(identity);

        Assert.Equal(LocalSecretResultStatus.Missing, probe.Status);
    }

    public static IEnumerable<object[]> ValidEscapedPropertyDocuments()
    {
        yield return ["{\"\\\"\":{}}"];
        yield return ["{\"\\\\\":{}}"];
        yield return ["{\"\\/\":{}}"];
        yield return ["{\"\\b\":{}}"];
        yield return ["{\"\\f\":{}}"];
        yield return ["{\"\\n\":{}}"];
        yield return ["{\"\\r\":{}}"];
        yield return ["{\"\\t\":{}}"];
        yield return ["{\"\\uABCD\":{}}"];
        yield return ["{\"\\uabcd\":{}}"];
    }

    [Fact]
    public void Probe_Should_SkipAllValidStringEscapesAndNumericBoundaries()
    {
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;
        var store = CreateProbeStore(
            "{\"Ignored\":{" +
            "\"escapes\":\"\\\"\\\\\\/\\b\\f\\n\\r\\t\\uABCD\"," +
            "\"fraction\":1.09," +
            "\"unsignedExponent\":1e09," +
            "\"positiveExponent\":1e+09," +
            "\"negativeExponent\":1e-09" +
            "}}");

        var probe = store.Probe(identity);

        Assert.Equal(LocalSecretResultStatus.Missing, probe.Status);
    }

    public static IEnumerable<object[]> MalformedSkippedValues()
    {
        yield return ["]"];
        yield return ["\"unterminated"];
        yield return ["\"\\q\""];
        yield return ["\"\\u0X00\""];
        yield return ["[1 2]"];
        yield return ["[1,]"];
        yield return ["{bad:1}"];
        yield return ["{\"nested\" 1}"];
        yield return ["{\"nested\":1 \"other\":2}"];
        yield return ["\"control\u0001\""];
        yield return ["truX"];
        yield return ["truex"];
        yield return ["-x"];
        yield return ["01"];
        yield return ["1."];
        yield return ["1e+"];
    }

    [Theory]
    [MemberData(nameof(MalformedRootDocuments))]
    public void Probe_Should_ReturnInvalidStore_ForMalformedRootDocument(string json)
    {
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;
        var store = CreateProbeStore(json);

        var probe = store.Probe(identity);

        Assert.Equal(LocalSecretResultStatus.ProviderFailed, probe.Status);
        Assert.Equal("local-secret-store-invalid", probe.Diagnostic?.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    [InlineData("{}")]
    public void Probe_Should_ReturnMissing_ForEmptyStoreDocuments(string json)
    {
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var probe = CreateProbeStore(json).Probe(identity);

        Assert.Equal(LocalSecretResultStatus.Missing, probe.Status);
    }

    public static IEnumerable<object[]> MalformedRootDocuments()
    {
        yield return ["[]"];
        yield return ["{Ignored:{}}"];
        yield return ["{\"Ignored\" {}}"];
        yield return ["{\"Ignored\":{} x}"];
        yield return ["{\"Ignored\":{}} trailing"];
        yield return ["{\"unterminated"];
        yield return ["{" + "\"" + "\\"];
        yield return ["{\"\\q\":{}}"];
        yield return ["{\"\\u0X00\":{}}"];
        yield return ["{\"control\u0001\":{}}"];
        yield return ["{\"Ignored\":{\"value\":-"];
        yield return ["{\"Ignored\":{\"value\":\"\\"];
        yield return ["{\"Ignored\":{\"value\":\"\\u0"];
        yield return ["{\"\\u0"];
        yield return ["{\"Ignored\":{\"value\":\"unterminated"];
        yield return ["{\"Ignored\":{\"value\":"];
        yield return ["{\"Ignored\":{\"value\":1"];
        yield return ["{\"Ignored\":{\"value\":1."];
        yield return ["{\"Ignored\":{\"value\":1e"];
    }

    private static FileAppSurfaceLocalSecretStore CreateProbeStore(string json) =>
        new(
            "/tmp/appsurface-test-secrets.json",
            new ThrowingFileSystem(openRead: () => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json))));

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
        Assert.Equal("local-secret-file-posture-unsupported", result.Diagnostic?.Code);
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
    public void Doctor_Should_ReportReadyForExistingStrictUnixFileInSecureDirectory()
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
        new FileInfo(path).UnixFileMode = SecretFileMode;
        var store = new FileAppSurfaceLocalSecretStore(path);

        var result = store.Doctor("MyApp", "Development", null);

        Assert.Equal(LocalSecretResultStatus.Missing, result.Status);
        Assert.Equal("local-secret-store-ready", result.Diagnostic?.Code);
        Assert.Equal(SecretDirectoryMode, new DirectoryInfo(directory).UnixFileMode);
        Assert.Equal(SecretFileMode, new FileInfo(path).UnixFileMode);
    }

    [Fact]
    public void Doctor_Should_ReturnLockedDiagnostic_WhenFileSystemDoctorIsUnauthorized()
    {
        var store = new FileAppSurfaceLocalSecretStore(
            "secrets.json",
            new ThrowingFileSystem(doctor: () => throw new UnauthorizedAccessException()));

        var result = store.Doctor("MyApp", "Development", null);

        Assert.Equal(LocalSecretResultStatus.Locked, result.Status);
        Assert.Equal("local-secret-store-locked", result.Diagnostic?.Code);
    }

    [Fact]
    public void Doctor_Should_ReturnUnavailableDiagnostic_WhenFileSystemDoctorFailsWithIoException()
    {
        var store = new FileAppSurfaceLocalSecretStore(
            "secrets.json",
            new ThrowingFileSystem(doctor: () => throw new IOException()));

        var result = store.Doctor("MyApp", "Development", null);

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal("local-secret-store-unavailable", result.Diagnostic?.Code);
        Assert.True(result.Diagnostic?.Retryable);
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
        Assert.Equal("local-secret-file-posture-unsupported", result.Diagnostic?.Code);
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
        Assert.Equal("local-secret-file-posture-unsupported", result.Diagnostic?.Code);
        Assert.Equal("Local secret file posture is unsupported.", result.Diagnostic?.Problem);
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
        Assert.Equal("local-secret-file-posture-unsupported", result.Diagnostic?.Code);
        Assert.Equal("Local secret file posture is unsupported.", result.Diagnostic?.Problem);
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
                    "local-secret-file-posture-unsupported",
                    "Local secret file posture is unsupported.",
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
    public void Get_Should_ReturnLockedDiagnostic_WhenInitialPostureIsUnauthorized()
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
                existingFilePosture: () => throw new UnauthorizedAccessException()));
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var result = store.Get(identity);

        Assert.Equal(LocalSecretResultStatus.Locked, result.Status);
        Assert.Equal("local-secret-store-locked", result.Diagnostic?.Code);
        Assert.Null(result.Value);
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
    public void Delete_Should_NotRead_WhenWritePreflightRejectsPath()
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
                prepareWrite: () => FileSecretPostureResult.Unsupported(
                    "local-secret-file-posture-unsupported",
                    "Local secret file path is unsupported.",
                    "The fallback path is not safe to read or write.",
                    "Choose a normal per-user file path.")));
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var result = store.Delete(identity);

        Assert.Equal(LocalSecretResultStatus.UnsupportedPlatform, result.Status);
        Assert.Equal("local-secret-file-posture-unsupported", result.Diagnostic?.Code);
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
                        "local-secret-file-posture-unsupported",
                        "Local secret file posture is unsupported.",
                        "The fallback path became unsafe before returning the value.",
                        "Choose a normal per-user file path.")));
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var result = store.Get(identity);

        Assert.Equal(LocalSecretResultStatus.UnsupportedPlatform, result.Status);
        Assert.Equal("local-secret-file-posture-unsupported", result.Diagnostic?.Code);
        Assert.Equal(2, postureChecks);
        Assert.Null(result.Value);
        Assert.DoesNotContain("sk_test_secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Get_Should_ReturnPostureFailure_WhenMissingKeyAfterReadPathBecomesUnsafe()
    {
        var postureChecks = 0;
        var store = new FileAppSurfaceLocalSecretStore(
            "secrets.json",
            new ThrowingFileSystem(
                existingFilePosture: () => ++postureChecks == 1
                    ? FileSecretPostureResult.Ready()
                    : FileSecretPostureResult.Unsupported(
                        "local-secret-file-posture-unsupported",
                        "Local secret file posture is unsupported.",
                        "The fallback path became unsafe before returning missing status.",
                        "Choose a normal per-user file path.")));
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var result = store.Get(identity);

        Assert.Equal(LocalSecretResultStatus.UnsupportedPlatform, result.Status);
        Assert.Equal("local-secret-file-posture-unsupported", result.Diagnostic?.Code);
        Assert.Equal(2, postureChecks);
    }

    [Fact]
    public void Get_Should_ReturnLockedDiagnostic_WhenPostReadPostureIsUnauthorized()
    {
        var postureChecks = 0;
        var store = new FileAppSurfaceLocalSecretStore(
            "secrets.json",
            new ThrowingFileSystem(
                read: () => ToSecretJson("sk_test_secret"),
                existingFilePosture: () =>
                {
                    if (++postureChecks == 1)
                    {
                        return FileSecretPostureResult.Ready();
                    }

                    throw new UnauthorizedAccessException();
                }));
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var result = store.Get(identity);

        Assert.Equal(LocalSecretResultStatus.Locked, result.Status);
        Assert.Equal("local-secret-store-locked", result.Diagnostic?.Code);
        Assert.Null(result.Value);
        Assert.Equal(2, postureChecks);
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
                        "local-secret-file-posture-unsupported",
                        "Local secret file posture is unsupported.",
                        "The fallback path became unsafe before returning key names.",
                        "Choose a normal per-user file path.")));

        var result = store.List("MyApp", "Development", null);

        Assert.Equal(LocalSecretResultStatus.UnsupportedPlatform, result.Status);
        Assert.Equal("local-secret-file-posture-unsupported", result.Diagnostic?.Code);
        Assert.Equal(2, postureChecks);
        Assert.Empty(result.Keys);
    }

    [Fact]
    public void List_Should_ReturnUnavailableDiagnostic_WhenPostReadPostureFailsWithIoException()
    {
        var postureChecks = 0;
        var store = new FileAppSurfaceLocalSecretStore(
            "secrets.json",
            new ThrowingFileSystem(
                read: () => ToSecretJson("sk_test_secret"),
                existingFilePosture: () =>
                {
                    if (++postureChecks == 1)
                    {
                        return FileSecretPostureResult.Ready();
                    }

                    throw new IOException();
                }));

        var result = store.List("MyApp", "Development", null);

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal("local-secret-store-unavailable", result.Diagnostic?.Code);
        Assert.True(result.Diagnostic?.Retryable);
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
    public void Set_Should_ReturnLockedDiagnostic_WhenWritePreflightIsUnauthorized()
    {
        var store = new FileAppSurfaceLocalSecretStore(
            "secrets.json",
            new ThrowingFileSystem(prepareWrite: () => throw new UnauthorizedAccessException()));
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
    public void Delete_Should_ReturnUnavailableDiagnostic_WhenWritePreflightFailsWithIoException()
    {
        var store = new FileAppSurfaceLocalSecretStore(
            "secrets.json",
            new ThrowingFileSystem(prepareWrite: () => throw new IOException()));
        var identity = new AppSurfaceLocalSecretIdentityNormalizer()
            .Normalize("MyApp", "Development", null, "Stripe:ApiKey")
            .Identity!;

        var result = store.Delete(identity);

        Assert.Equal(LocalSecretResultStatus.Unavailable, result.Status);
        Assert.Equal("local-secret-store-unavailable", result.Diagnostic?.Code);
        Assert.True(result.Diagnostic?.Retryable);
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

    [Fact]
    public void DefaultFileSystem_Should_ReportReady_WhenReadPathDoesNotExist()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Join(temp.Path, "missing.json");

        var result = DefaultFileAppSurfaceLocalSecretStoreFileSystem.Instance.InspectReadPath(path);

        Assert.Equal(FileSecretPostureKind.Ready, result.Kind);
        Assert.Equal("local-secret-store-ready", result.Code);
    }

    [Fact]
    public void DefaultFileSystem_Should_ReportReady_WhenExistingFilePosturePathDoesNotExist()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Join(temp.Path, "missing.json");

        var result = DefaultFileAppSurfaceLocalSecretStoreFileSystem.Instance.InspectExistingFilePosture(path);

        Assert.Equal(FileSecretPostureKind.Ready, result.Kind);
        Assert.Equal("local-secret-store-ready", result.Code);
    }

    [Fact]
    public void DefaultFileSystem_Should_RejectDirectoryDuringExistingFilePosture()
    {
        using var temp = TempDirectory.Create();

        var result = DefaultFileAppSurfaceLocalSecretStoreFileSystem.Instance.InspectExistingFilePosture(temp.Path);

        Assert.Equal(FileSecretPostureKind.Unsupported, result.Kind);
        Assert.Equal("local-secret-file-posture-unsupported", result.Code);
    }

    [Fact]
    public void DefaultFileSystem_Should_ReturnDegradedDiagnostics_WhenUnixModeChecksAreUnavailable()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Join(temp.Path, "nested", "secrets.json");
        var fileSystem = new DefaultFileAppSurfaceLocalSecretStoreFileSystem(() => false, () => true);

        var prepare = fileSystem.PrepareWrite(path);
        var write = fileSystem.WriteAllTextWithPosture(path, "{}");
        var inspect = fileSystem.InspectExistingFilePosture(path);
        var doctor = fileSystem.Doctor(path);

        Assert.Equal(FileSecretPostureKind.Degraded, prepare.Kind);
        Assert.Equal(FileSecretPostureKind.Degraded, write.Kind);
        Assert.Equal(FileSecretPostureKind.Ready, inspect.Kind);
        Assert.Equal(FileSecretPostureKind.Degraded, doctor.Kind);
        Assert.Equal("{}", File.ReadAllText(path));
    }

    [Fact]
    public void DefaultFileSystem_Should_ReportReady_WhenFutureDirectoryAncestorDoesNotExist()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Join(temp.Path, "missing", "nested", "secrets.json");

        var result = DefaultFileAppSurfaceLocalSecretStoreFileSystem.Instance.InspectReadPath(path);

        Assert.Equal(FileSecretPostureKind.Ready, result.Kind);
    }

    [Fact]
    public void DefaultFileSystem_Should_RejectFileAncestorDuringReadPosture()
    {
        using var temp = TempDirectory.Create();
        var fileAncestor = Path.Join(temp.Path, "not-a-directory");
        File.WriteAllText(fileAncestor, "content");
        var path = Path.Join(fileAncestor, "secrets.json");

        var result = DefaultFileAppSurfaceLocalSecretStoreFileSystem.Instance.InspectReadPath(path);

        Assert.Equal(FileSecretPostureKind.Unsupported, result.Kind);
        Assert.Equal("local-secret-file-posture-unsupported", result.Code);
    }

    [Fact]
    public void DefaultFileSystem_Should_RejectNonSystemSymlinkDirectoryDuringReadPosture()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var targetDirectory = Path.Join(temp.Path, "target");
        var linkDirectory = Path.Join(temp.Path, "linked");
        Directory.CreateDirectory(targetDirectory);
        Directory.CreateSymbolicLink(linkDirectory, targetDirectory);
        var path = Path.Join(linkDirectory, "secrets.json");
        var fileSystem = new DefaultFileAppSurfaceLocalSecretStoreFileSystem(() => true, () => true);

        var result = fileSystem.InspectReadPath(path);

        Assert.Equal(FileSecretPostureKind.Unsupported, result.Kind);
        Assert.Equal("local-secret-file-posture-unsupported", result.Code);
    }

    [Fact]
    public void DefaultFileSystem_Should_RejectSymlinkDirectoryWhenMacAliasChecksAreDisabled()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var targetDirectory = Path.Join(temp.Path, "target");
        var linkDirectory = Path.Join(temp.Path, "linked");
        Directory.CreateDirectory(targetDirectory);
        Directory.CreateSymbolicLink(linkDirectory, targetDirectory);
        var path = Path.Join(linkDirectory, "secrets.json");
        var fileSystem = new DefaultFileAppSurfaceLocalSecretStoreFileSystem(() => true, () => false);

        var result = fileSystem.InspectReadPath(path);

        Assert.Equal(FileSecretPostureKind.Unsupported, result.Kind);
        Assert.Equal("local-secret-file-posture-unsupported", result.Code);
    }

    [Fact]
    public void DefaultFileSystem_Should_RejectLooseUnixContainingDirectoryDuringReadPosture()
    {
        if (!IsUnix())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var directory = Path.Join(temp.Path, "shared");
        Directory.CreateDirectory(directory);
        var looseMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead;
        new DirectoryInfo(directory).UnixFileMode = looseMode;
        var path = Path.Join(directory, "secrets.json");
        File.WriteAllText(path, "{}");
        new FileInfo(path).UnixFileMode = SecretFileMode;

        var result = DefaultFileAppSurfaceLocalSecretStoreFileSystem.Instance.InspectExistingFilePosture(path);

        Assert.Equal(FileSecretPostureKind.Unsupported, result.Kind);
        Assert.Equal("local-secret-file-posture-unsupported", result.Code);
        Assert.Equal("Local secret directory posture is unsupported.", result.Problem);
        Assert.Equal(looseMode, new DirectoryInfo(directory).UnixFileMode);
    }

    [Fact]
    public void DefaultFileSystem_Should_RejectGroupWritableAncestorDuringReadPosture()
    {
        if (!IsUnix())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var ancestor = Path.Join(temp.Path, "shared");
        var nested = Path.Join(ancestor, "nested");
        Directory.CreateDirectory(nested);
        var looseMode = SecretDirectoryMode | UnixFileMode.GroupWrite;
        new DirectoryInfo(ancestor).UnixFileMode = looseMode;
        new DirectoryInfo(nested).UnixFileMode = SecretDirectoryMode;
        var path = Path.Join(nested, "secrets.json");

        var result = DefaultFileAppSurfaceLocalSecretStoreFileSystem.Instance.InspectReadPath(path);

        Assert.Equal(FileSecretPostureKind.Unsupported, result.Kind);
        Assert.Equal("local-secret-file-posture-unsupported", result.Code);
        Assert.Equal("Local secret directory posture is unsupported.", result.Problem);
    }

    [Fact]
    public void DefaultFileSystem_Should_RecheckCreatedDirectoryAncestorsDuringPrepareWrite()
    {
        if (!IsUnix())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var ancestor = Path.Join(temp.Path, "shared");
        var nested = Path.Join(ancestor, "nested");
        var path = Path.Join(nested, "secrets.json");
        var looseMode = SecretDirectoryMode | UnixFileMode.GroupWrite;
        var fileSystem = new DefaultFileAppSurfaceLocalSecretStoreFileSystem(
            () => true,
            OperatingSystem.IsMacOS,
            afterDirectoryCreate: _ =>
            {
                if (OperatingSystem.IsWindows())
                {
                    return;
                }

                new DirectoryInfo(ancestor).UnixFileMode = looseMode;
            });

        var result = fileSystem.PrepareWrite(path);

        Assert.Equal(FileSecretPostureKind.Unsupported, result.Kind);
        Assert.Equal("local-secret-file-posture-unsupported", result.Code);
        Assert.Equal("Local secret directory posture is unsupported.", result.Problem);
        Assert.Equal(looseMode, new DirectoryInfo(ancestor).UnixFileMode);
    }

    [Fact]
    public void DefaultFileSystem_Should_PrepareRelativeFileWithoutContainingDirectory()
    {
        using var temp = TempDirectory.Create();
        var previousCurrentDirectory = Directory.GetCurrentDirectory();
        var fileName = $"secrets-{Guid.NewGuid():N}.json";
        try
        {
            Directory.SetCurrentDirectory(temp.Path);

            var result = DefaultFileAppSurfaceLocalSecretStoreFileSystem.Instance.PrepareWrite(fileName);

            Assert.NotEqual(FileSecretPostureKind.Unsupported, result.Kind);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCurrentDirectory);
        }
    }

    [Fact]
    public void DefaultFileSystem_Should_WriteRelativeFileWithoutContainingDirectory()
    {
        using var temp = TempDirectory.Create();
        var previousCurrentDirectory = Directory.GetCurrentDirectory();
        var fileName = $"secrets-{Guid.NewGuid():N}.json";
        try
        {
            Directory.SetCurrentDirectory(temp.Path);

            var result = DefaultFileAppSurfaceLocalSecretStoreFileSystem.Instance.WriteAllTextWithPosture(fileName, "{}");

            Assert.NotEqual(FileSecretPostureKind.Unsupported, result.Kind);
            Assert.Equal("{}", File.ReadAllText(fileName));
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCurrentDirectory);
        }
    }

    [Fact]
    public void DefaultFileSystem_Should_InspectExistingRelativeFileWithoutContainingDirectory()
    {
        using var temp = TempDirectory.Create();
        var previousCurrentDirectory = Directory.GetCurrentDirectory();
        var fileName = $"secrets-{Guid.NewGuid():N}.json";
        try
        {
            Directory.SetCurrentDirectory(temp.Path);
            File.WriteAllText(fileName, "{}");
            if (IsUnix())
            {
                new FileInfo(fileName).UnixFileMode = SecretFileMode;
            }

            var result = DefaultFileAppSurfaceLocalSecretStoreFileSystem.Instance.InspectExistingFilePosture(fileName);

            Assert.Equal(FileSecretPostureKind.Ready, result.Kind);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCurrentDirectory);
        }
    }

    [Fact]
    public void DefaultFileSystem_Should_ReturnPreflightFailure_WhenWriteTargetIsDirectory()
    {
        using var temp = TempDirectory.Create();

        var result = DefaultFileAppSurfaceLocalSecretStoreFileSystem.Instance.WriteAllTextWithPosture(temp.Path, "{}");

        Assert.Equal(FileSecretPostureKind.Unsupported, result.Kind);
        Assert.Equal("local-secret-file-posture-unsupported", result.Code);
    }

    [Fact]
    public void DefaultFileSystem_Should_InspectRootDirectoryPathWithoutRejectingRootAlias()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()));
        Assert.False(string.IsNullOrEmpty(root));
        var path = Path.Join(root, $"appsurface-missing-{Guid.NewGuid():N}.json");

        var result = DefaultFileAppSurfaceLocalSecretStoreFileSystem.Instance.InspectReadPath(path);

        Assert.Equal(FileSecretPostureKind.Ready, result.Kind);
    }

    [Fact]
    public void DefaultFileSystem_Should_DeleteTempFile_WhenFinalTargetBecomesDirectoryBeforeMove()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Join(temp.Path, "secrets.json");
        var fileSystem = new DefaultFileAppSurfaceLocalSecretStoreFileSystem(
            () => IsUnix(),
            OperatingSystem.IsMacOS,
            _ => Directory.CreateDirectory(path));

        var result = fileSystem.WriteAllTextWithPosture(path, "{}");

        Assert.Equal(FileSecretPostureKind.Unsupported, result.Kind);
        Assert.Equal("local-secret-file-posture-unsupported", result.Code);
        Assert.Empty(Directory.EnumerateFiles(temp.Path, ".secrets.json.*.tmp"));
    }

    [Fact]
    public void FileSecretPostureResult_Should_CreateDegradedDiagnostic()
    {
        var result = FileSecretPostureResult.Degraded();

        Assert.Equal(FileSecretPostureKind.Degraded, result.Kind);
        Assert.Equal("local-secret-file-posture-degraded", result.Code);
        Assert.Equal("local-secrets-without-a-remote-vault", result.ToDiagnostic().Docs);
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
        Func<Stream>? openRead = null,
        Func<FileSecretPostureResult>? readPath = null,
        Func<FileSecretPostureResult>? existingFilePosture = null,
        Func<FileSecretPostureResult>? prepareWrite = null,
        Func<FileSecretPostureResult>? writePosture = null,
        Func<FileSecretPostureResult>? doctor = null) : IFileAppSurfaceLocalSecretStoreFileSystem
    {
        public bool FileExists(string path) => true;

        public string ReadAllText(string path) => read?.Invoke() ?? "{}";

        public Stream OpenRead(string path) =>
            openRead?.Invoke() ?? new MemoryStream(System.Text.Encoding.UTF8.GetBytes(ReadAllText(path)));

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

    private sealed class ThrowAfterPositionStream(byte[] data, int throwAt) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (_position >= throwAt)
            {
                throw new InvalidOperationException("Probe read into the stored value payload.");
            }

            if (_position >= data.Length)
            {
                return 0;
            }

            var availableBeforeThrow = throwAt - _position;
            var available = Math.Min(data.Length - _position, availableBeforeThrow);
            var bytesRead = Math.Min(count, available);
            Array.Copy(data, _position, buffer, offset, bytesRead);
            _position += bytesRead;
            return bytesRead;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
