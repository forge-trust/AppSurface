using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Auth.Aspire.Keycloak;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class AppSurfaceKeycloakRealmReadyEntryPointTests
{
    [Theory]
    [InlineData("application", "application")]
    [InlineData(" \t", null)]
    [InlineData(null, null)]
    public async Task BaselineModeParsesConfiguredLocalInputsAndUsesTheInjectedVerifier(string? theme, string? expectedTheme)
    {
        using var files = new TestFiles();
        var environment = BaselineEnvironment(files.FilePath("realm.json"));
        environment["APPSURFACE_KEYCLOAK_REALM_READY_LOGIN_THEME_NAME"] = theme;
        RealmBaselineDesired? observed = null;
        using var error = new StringWriter();

        var exitCode = await AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
            ["--appsurface-keycloak-realm-ready"], environment.GetValueOrDefault, TextWriter.Null, error,
            CancellationToken.None,
            (_, desired, _, _) => { observed = desired; return Task.CompletedTask; });

        Assert.Equal(0, exitCode);
        Assert.NotNull(observed);
        Assert.Equal("demo", observed.Realm);
        Assert.Equal("public-client", observed.ClientId);
        Assert.Equal("https://localhost:8443/realms/demo", observed.Authority);
        Assert.Equal("http://localhost:5001/callback", Assert.Single(observed.RedirectUris));
        Assert.Equal("http://localhost:5001/signed-out", Assert.Single(observed.PostLogoutRedirectUris));
        Assert.Collection(observed.SeededUserNames,
            user => Assert.Equal("alice", user),
            user => Assert.Equal("bob", user));
        Assert.Equal(files.FilePath("realm.json"), observed.RealmImportDirectory);
        Assert.Equal(expectedTheme, observed.LoginThemeName);
        Assert.Contains("AppSurface Keycloak realm-ready gate completed.", error.ToString());
    }

    [Theory]
    [InlineData("REDIRECT_URIS", "not-json")]
    [InlineData("REDIRECT_URIS", "null")]
    [InlineData("REDIRECT_URIS", "[]")]
    [InlineData("REDIRECT_URIS", "[\"http://localhost:5001/wrong\"]")]
    [InlineData("POST_LOGOUT_REDIRECT_URIS", "[\"http://localhost:5001/signed-out?private=value\"]")]
    [InlineData("AUTHORITY", "http://localhost:8443/realms/demo")]
    [InlineData("AUTHORITY", "https://example.test:8443/realms/demo")]
    [InlineData("AUTHORITY", "https://localhost:8443/realms/demo#private")]
    [InlineData("AUTHORITY", "https://localhost:8443/remote/demo")]
    [InlineData("AUTHORITY", "https://localhost:8443/realms/demo%20invalid")]
    [InlineData("SEEDED_USER_NAMES", "; ; ")]
    [InlineData("SEEDED_USER_NAMES", "alice; alice")]
    [InlineData("AUTHORITY", "https://private@localhost:8443/realms/demo")]
    [InlineData("AUTHORITY", "https://localhost:8443/realms/demo?private=value")]
    [InlineData("AUTHORITY", "https://localhost:8443/realms")]
    [InlineData("REDIRECT_URIS", "[\"relative/callback\"]")]
    [InlineData("REDIRECT_URIS", "[\"ftp://localhost:5001/callback\"]")]
    [InlineData("REDIRECT_URIS", "[\"http://other.example:5001/callback\"]")]
    [InlineData("REDIRECT_URIS", "[\"http://private@localhost:5001/callback\"]")]
    [InlineData("REDIRECT_URIS", "[\"http://localhost:5001/callback#private\"]")]
    [InlineData("CLIENT_ID", "")]
    [InlineData("CLIENT_ID", " \t")]
    [InlineData("REALM_IMPORT_DIRECTORY", " \t")]
    [InlineData("AUTHORITY", null)]
    [InlineData("SEEDED_USER_NAMES", null)]
    public async Task InvalidBaselineBindingFailsWithSafeEnvelope(string key, string? value)
    {
        var environment = BaselineEnvironment("unused");
        environment["APPSURFACE_KEYCLOAK_REALM_READY_" + key] = value;
        var verified = false;
        using var error = new StringWriter();

        var exitCode = await AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
            ["--appsurface-keycloak-realm-ready"], environment.GetValueOrDefault, TextWriter.Null, error,
            CancellationToken.None,
            checkBaseline: (_, _, _, _) => { verified = true; return Task.CompletedTask; });

        Assert.Equal(1, exitCode);
        Assert.False(verified);
        Assert.Equal("AppSurface Keycloak realm-ready gate failed. Code: ASKEYC001.", error.ToString().Trim());
    }

    [Theory]
    [InlineData(true, "ASKEYC019")]
    [InlineData(false, "ASKEYC001")]
    public async Task BaselineFailurePreservesTheStableCodeWithoutPublishingDetails(bool baselineFailure, string code)
    {
        var environment = BaselineEnvironment("unused");
        using var error = new StringWriter();

        var exitCode = await AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
            ["--appsurface-keycloak-realm-ready"], environment.GetValueOrDefault, TextWriter.Null, error,
            CancellationToken.None,
            checkBaseline: (_, _, _, _) => throw (baselineFailure
                ? new RealmBaselineException("ASKEYC019", "verifier-only details")
                : new InvalidOperationException("verifier-only details")));

        Assert.Equal(1, exitCode);
        Assert.Equal($"AppSurface Keycloak realm-ready gate failed. Code: {code}.", error.ToString().Trim());
    }

    [Fact]
    public async Task CancelledBaselineReturnsTheCancellationExitCode()
    {
        var environment = BaselineEnvironment("unused");
        using var error = new StringWriter();

        var exitCode = await AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
            ["--appsurface-keycloak-realm-ready"], environment.GetValueOrDefault, TextWriter.Null, error,
            new CancellationToken(canceled: true),
            checkBaseline: (_, _, _, token) => Task.FromCanceled(token));

        Assert.Equal(124, exitCode);
        Assert.Equal("AppSurface Keycloak realm-ready gate was cancelled.", error.ToString().Trim());
    }

    [Fact]
    public async Task RejectsUnknownArgumentsWithoutStartingWorker()
    {
        using var error = new StringWriter();

        var exitCode = await AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
            ["--unexpected"], _ => null, TextWriter.Null, error, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData("--appsurface-keycloak-reconcile", new string[0])]
    [InlineData("--appsurface-keycloak-memory-anchor", new[] { "input.json" })]
    [InlineData("--appsurface-keycloak-owner-fence", new[] { "fence.json", "8080" })]
    public async Task RejectsWorkerModesWithIncorrectArgumentShape(string mode, params string[] trailingArguments)
    {
        using var error = new StringWriter();
        var called = false;

        var exitCode = await AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
            [mode, .. trailingArguments], _ => null, TextWriter.Null, error, CancellationToken.None,
            checkBaseline: (_, _, _, _) => { called = true; return Task.CompletedTask; },
            reconcile: (_, _, _, _) => { called = true; return Task.FromResult<RealmReconcileResult>(null!); });

        Assert.Equal(1, exitCode);
        Assert.False(called);
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData("--appsurface-keycloak-volume-incarnation", "AppSurface Keycloak volume identity unavailable.")]
    [InlineData("--appsurface-keycloak-volume-ownership", "AppSurface Keycloak volume ownership unavailable.")]
    public async Task VolumeMetadataCommandsFailClosedOutsideLinux(string mode, string diagnostic)
    {
        if (OperatingSystem.IsLinux()) return;

        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
            [mode], _ => null, output, error, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Empty(output.ToString());
        Assert.Equal(diagnostic, error.ToString().Trim());
    }

    [Fact]
    public void VolumeIncarnationCommandSerializesTheRootAndUsesTheSafeFailureEnvelope()
    {
        using var files = new TestFiles();
        var volumeRoot = files.FilePath("volume");
        var expected = new KeycloakVolumeIncarnation(8, 9, 10, 11, 12);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var result = AppSurfaceKeycloakRealmReadyEntryPoint.RunVolumeMetadataCommand(
            [KeycloakWorkerProtocol.VolumeIncarnation], volumeRoot, output, error,
            path => { Assert.Equal(volumeRoot, path); return expected; },
            _ => throw new InvalidOperationException("ownership reader must not run"),
            _ => throw new InvalidOperationException("database reader must not run"));

        Assert.Equal<int?>(0, result);
        Assert.Equal(expected, JsonSerializer.Deserialize<KeycloakVolumeIncarnation>(output.ToString()));
        Assert.Empty(error.ToString());

        using var failedOutput = new StringWriter();
        using var failedError = new StringWriter();
        result = AppSurfaceKeycloakRealmReadyEntryPoint.RunVolumeMetadataCommand(
            [KeycloakWorkerProtocol.VolumeIncarnation], volumeRoot, failedOutput, failedError,
            _ => throw new IOException("fixture-only failure"),
            _ => throw new InvalidOperationException("ownership reader must not run"),
            _ => throw new InvalidOperationException("database reader must not run"));

        Assert.Equal<int?>(1, result);
        Assert.Empty(failedOutput.ToString());
        Assert.Equal("AppSurface Keycloak volume identity unavailable.", failedError.ToString().Trim());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VolumeOwnershipCommandReturnsRootOwnerAndOnlyThePinnedDatabaseOwner(bool databaseExists)
    {
        using var files = new TestFiles();
        var volumeRoot = files.FilePath("volume");
        var expected = new KeycloakVolumeIncarnation(8, 9, 10, 11, 12);
        var reads = new List<string>();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var result = AppSurfaceKeycloakRealmReadyEntryPoint.RunVolumeMetadataCommand(
            [KeycloakWorkerProtocol.VolumeOwnership], volumeRoot, output, error,
            _ => throw new InvalidOperationException("identity reader must not run"),
            path =>
            {
                reads.Add("root-ownership");
                Assert.Equal(volumeRoot, path);
                return (expected, 1001u, 0u);
            },
            path =>
            {
                reads.Add("pinned-database");
                Assert.Equal(volumeRoot, path);
                return databaseExists ? (1002u, 42u) : null;
            });

        Assert.Equal<int?>(0, result);
        Assert.Equal(new[] { "root-ownership", "pinned-database" }, reads);
        Assert.Empty(error.ToString());
        var ownership = JsonSerializer.Deserialize<KeycloakWorkerVolumeOwnership>(output.ToString());
        Assert.NotNull(ownership);
        Assert.Equal(expected, ownership.Incarnation);
        Assert.Equal(1001u, ownership.Uid);
        Assert.Equal(0u, ownership.Gid);
        if (databaseExists)
            Assert.Equal(new KeycloakWorkerDatabaseOwnership(1002u, 42u), ownership.Database);
        else
            Assert.Null(ownership.Database);
    }

    [Fact]
    public void VolumeOwnershipCommandKeepsItsSafeFailureEnvelopeWhenPinnedDatabaseReadFails()
    {
        using var files = new TestFiles();
        var expected = new KeycloakVolumeIncarnation(8, 9, 10, 11, 12);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var result = AppSurfaceKeycloakRealmReadyEntryPoint.RunVolumeMetadataCommand(
            [KeycloakWorkerProtocol.VolumeOwnership], files.FilePath("volume"), output, error,
            _ => throw new InvalidOperationException("identity reader must not run"),
            _ => (expected, 1001u, 0u),
            _ => throw new IOException("fixture-only pinned-path failure"));

        Assert.Equal<int?>(1, result);
        Assert.Empty(output.ToString());
        Assert.Equal("AppSurface Keycloak volume ownership unavailable.", error.ToString().Trim());
    }

    [Fact]
    public void VolumeInitializationCoreOrdersEmptyCheckIdentityChownModeAndReadback()
    {
        using var files = new TestFiles();
        Directory.CreateDirectory(files.Root);
        var expected = new KeycloakVolumeIncarnation(8, 9, 10, 11, 12);
        var events = new List<string>();
        var reads = 0;

        var result = AppSurfaceKeycloakRealmReadyEntryPoint.InitializeVolumeCore(
            files.Root, 1001,
            root => { events.Add("empty-check"); return Directory.EnumerateFileSystemEntries(root).Any(); },
            root =>
            {
                events.Add(reads++ == 0 ? "identity-before" : "identity-after");
                return expected;
            },
            (root, uid, gid) =>
            {
                events.Add("chown");
                Assert.Equal(files.Root, root);
                Assert.Equal(1001u, uid);
                Assert.Equal(0u, gid);
                return 0;
            },
            (root, mode) =>
            {
                events.Add("mode");
                Assert.Equal(files.Root, root);
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute, mode);
            });

        Assert.Equal(0, result);
        Assert.Equal(new[] { "empty-check", "identity-before", "chown", "mode", "identity-after" }, events);
        Assert.Empty(Directory.EnumerateFileSystemEntries(files.Root));
    }

    [Fact]
    public void VolumeInitializationCoreRefusesNonemptyOrUnreadableRootsBeforeMutation()
    {
        using var files = new TestFiles();
        var marker = files.Write("preserve.txt", "fixture data");
        var events = new List<string>();

        var refused = AppSurfaceKeycloakRealmReadyEntryPoint.InitializeVolumeCore(
            files.Root, 1001,
            root => { events.Add("empty-check"); return Directory.EnumerateFileSystemEntries(root).Any(); },
            _ => { events.Add("identity"); throw new InvalidOperationException("must not read a populated root"); },
            (_, _, _) => { events.Add("chown"); return 0; },
            (_, _) => events.Add("mode"));

        Assert.Equal(1, refused);
        Assert.Equal(new[] { "empty-check" }, events);
        Assert.Equal("fixture data", File.ReadAllText(marker));

        Directory.Delete(files.Root, recursive: true);
        Directory.CreateDirectory(files.Root);
        events.Clear();
        Assert.Throws<InvalidDataException>(() => AppSurfaceKeycloakRealmReadyEntryPoint.InitializeVolumeCore(
            files.Root, 1001,
            root => { events.Add("empty-check"); return Directory.EnumerateFileSystemEntries(root).Any(); },
            _ => { events.Add("identity"); throw new InvalidDataException("fixture-only invalid metadata"); },
            (_, _, _) => { events.Add("chown"); return 0; },
            (_, _) => events.Add("mode")));

        Assert.Equal(new[] { "empty-check", "identity" }, events);
        Assert.Empty(Directory.EnumerateFileSystemEntries(files.Root));
    }

    [Fact]
    public void VolumeInitializationCoreRejectsChangedReadbackIdentityAndChownFailure()
    {
        using var files = new TestFiles();
        Directory.CreateDirectory(files.Root);
        var original = new KeycloakVolumeIncarnation(8, 9, 10, 11, 12);
        var changed = original with { Inode = 13 };
        var events = new List<string>();
        var reads = 0;

        var changedResult = AppSurfaceKeycloakRealmReadyEntryPoint.InitializeVolumeCore(
            files.Root, 1001, _ => { events.Add("empty-check"); return false; },
            _ => { events.Add(reads++ == 0 ? "identity-before" : "identity-after"); return reads == 1 ? original : changed; },
            (_, _, _) => { events.Add("chown"); return 0; },
            (_, _) => events.Add("mode"));

        Assert.Equal(1, changedResult);
        Assert.Equal(new[] { "empty-check", "identity-before", "chown", "mode", "identity-after" }, events);
        Assert.Empty(Directory.EnumerateFileSystemEntries(files.Root));

        events.Clear();
        var chownResult = AppSurfaceKeycloakRealmReadyEntryPoint.InitializeVolumeCore(
            files.Root, 1001, _ => { events.Add("empty-check"); return false; },
            _ => { events.Add("identity"); return original; },
            (_, _, _) => { events.Add("chown"); return -1; },
            (_, _) => events.Add("mode"));

        Assert.Equal(1, chownResult);
        Assert.Equal(new[] { "empty-check", "identity", "chown" }, events);
    }

    [Fact]
    public void VolumeInitializationCoreHitsTheLinuxArm64ReaderGuardBeforeMutation()
    {
        if (OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            == System.Runtime.InteropServices.Architecture.Arm64)
            return;

        using var files = new TestFiles();
        Directory.CreateDirectory(files.Root);
        var events = new List<string>();

        Assert.Throws<PlatformNotSupportedException>(() => AppSurfaceKeycloakRealmReadyEntryPoint.InitializeVolumeCore(
            files.Root, 1001,
            _ => { events.Add("empty-check"); return false; },
            root => { events.Add("identity"); return KeycloakVolumeIncarnation.Read(root); },
            (_, _, _) => { events.Add("chown"); return 0; },
            (_, _) => events.Add("mode")));

        Assert.Equal(new[] { "empty-check", "identity" }, events);
    }

    [Theory]
    [InlineData("not-a-uid")]
    [InlineData("0")]
    [InlineData("1001")]
    public async Task VolumeInitializationLeavesExistingVolumeDataIntact(string requestedUid)
    {
        using var files = new TestFiles();
        var marker = files.Write("preserve.txt", "fixture data");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var result = await AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
            [KeycloakWorkerProtocol.InitializeVolume, requestedUid], _ => null, output, error,
            CancellationToken.None, volumeRoot: files.Root);

        Assert.Equal(1, result);
        Assert.Empty(output.ToString());
        Assert.Empty(error.ToString());
        Assert.Equal("fixture data", File.ReadAllText(marker));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VolumeInitializationDispatchesTheExactUidAndRedactsNativeFailures(bool fail)
    {
        using var files = new TestFiles();
        using var error = new StringWriter();
        (string Root, uint Uid)? observed = null;

        var result = AppSurfaceKeycloakRealmReadyEntryPoint.RunInitializeVolumeCommand(files.Root, "1001", error, (root, uid) =>
        {
            observed = (root, uid);
            if (fail) throw new IOException("fixture-only native detail");
            return 0;
        });

        Assert.Equal((files.Root, 1001u), observed);
        Assert.Equal(fail ? 1 : 0, result);
        Assert.Equal(fail ? "AppSurface Keycloak new-volume initialization failed." : "", error.ToString().Trim());
    }

    [Fact]
    public void AnchorDeclarationParserReadsBoundedOwnerOnlyUnixFileWithoutCreatingNativeCustody()
    {
        if (OperatingSystem.IsWindows()) return;

        using var files = new TestFiles();
        const string runId = "aabbccddeeff00112233445566778899";
        const string fixtureSecret = "fixture-only-anchor-secret";
        var path = files.Write("anchor-input.json", JsonSerializer.Serialize(new
        {
            RunId = runId,
            Authority = "https://localhost:8443/realms/demo",
            ClientId = "fixture-client",
            ClientSecret = fixtureSecret
        }));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var declaration = AppSurfaceKeycloakRealmReadyEntryPoint.ReadAnchorDeclaration(path, runId);
        var unfencedDeclaration = AppSurfaceKeycloakRealmReadyEntryPoint.ReadAnchorDeclaration(path, expectedOwner: null);

        Assert.Equal(runId, declaration.RunId);
        Assert.Equal("https://localhost:8443/realms/demo", declaration.Authority);
        Assert.Equal("fixture-client", declaration.ClientId);
        Assert.Equal(fixtureSecret, declaration.ClientSecret);
        Assert.Equal(declaration, unfencedDeclaration);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("oversized")]
    [InlineData("malformed")]
    [InlineData("null")]
    [InlineData("unsafe-mode")]
    [InlineData("linked")]
    [InlineData("wrong-owner")]
    public void AnchorDeclarationParserRejectsUnsafeOrMismatchedUnixFiles(string scenario)
    {
        if (OperatingSystem.IsWindows()) return;

        using var files = new TestFiles();
        const string runId = "aabbccddeeff00112233445566778899";
        var validJson = JsonSerializer.Serialize(new
        {
            RunId = runId,
            Authority = "https://localhost:8443/realms/demo",
            ClientId = "fixture-client",
            ClientSecret = "fixture-only-anchor-secret"
        });
        var path = files.FilePath(scenario + ".json");
        switch (scenario)
        {
            case "missing":
                break;
            case "empty":
                path = files.Write("empty.json", string.Empty);
                break;
            case "oversized":
                path = files.Write("oversized.json", new string(' ', 16 * 1024 + 1));
                break;
            case "malformed":
                path = files.Write("malformed.json", "{");
                break;
            case "null":
                path = files.Write("null.json", "null");
                break;
            case "unsafe-mode":
                path = files.Write("unsafe-mode.json", validJson);
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
                break;
            case "linked":
                var origin = files.Write("linked-origin.json", validJson);
                File.SetUnixFileMode(origin, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.CreateSymbolicLink(path, origin);
                break;
            case "wrong-owner":
                path = files.Write("wrong-owner.json", validJson);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        if (scenario is not ("missing" or "linked" or "unsafe-mode"))
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var expectedOwner = scenario == "wrong-owner" ? Guid.Empty.ToString("N") : runId;
        if (scenario == "malformed")
            Assert.Throws<JsonException>(() => AppSurfaceKeycloakRealmReadyEntryPoint.ReadAnchorDeclaration(path, expectedOwner));
        else
            Assert.Throws<InvalidDataException>(() => AppSurfaceKeycloakRealmReadyEntryPoint.ReadAnchorDeclaration(path, expectedOwner));
    }

    [Fact]
    public async Task AnchorReferencePublicationWritesOnlyMetadataAndNeverOverwritesAnExistingFile()
    {
        if (OperatingSystem.IsWindows()) return;

        using var files = new TestFiles();
        Directory.CreateDirectory(files.Root);
        const string runId = "aabbccddeeff00112233445566778899";
        var reference = KeycloakClientCredentialReference.ForSealedMemory(
            new KeycloakClientCredentialMemoryIdentity(Environment.ProcessId, 1, 42, 10), runId,
            new Uri("https://localhost:8443/realms/demo"), "fixture-client");
        using var diagnostic = new StringWriter();
        var referencePath = files.FilePath("anchor-reference.json");

        await AppSurfaceKeycloakRealmReadyEntryPoint.WriteAnchorReferenceAsync(
            referencePath, reference, CancellationToken.None, diagnostic);

        var json = File.ReadAllText(referencePath);
        using var document = JsonDocument.Parse(json);
        var published = JsonSerializer.Deserialize<KeycloakClientCredentialReference>(json);
        Assert.NotNull(published);
        Assert.Equal(reference.RunId, published.RunId);
        Assert.Equal(reference.Authority, published.Authority);
        Assert.Equal(reference.ClientId, published.ClientId);
        Assert.Equal(reference.MemoryIdentity, published.MemoryIdentity);
        Assert.DoesNotContain(document.RootElement.EnumerateObject(), property =>
            property.Name.Equals("clientSecret", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(referencePath));
        Assert.Equal("AppSurface Keycloak sealed-memory anchor ready.", diagnostic.ToString().Trim());

        var collisionPath = files.Write("existing-reference.json", "existing-fixture-data");
        File.SetUnixFileMode(collisionPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var existingMode = File.GetUnixFileMode(collisionPath);
        using var collisionDiagnostic = new StringWriter();

        await Assert.ThrowsAsync<IOException>(() => AppSurfaceKeycloakRealmReadyEntryPoint.WriteAnchorReferenceAsync(
            collisionPath, reference, CancellationToken.None, collisionDiagnostic));

        Assert.Equal("existing-fixture-data", File.ReadAllText(collisionPath));
        Assert.Equal(existingMode, File.GetUnixFileMode(collisionPath));
        Assert.Empty(collisionDiagnostic.ToString());
    }

    [Theory]
    [InlineData("--appsurface-keycloak-owner-fence")]
    [InlineData("--appsurface-keycloak-fenced-memory-anchor")]
    public async Task InvalidFencePortReturnsSafeFailureWithoutPublishingAReadyMarker(string mode)
    {
        using var files = new TestFiles();
        Directory.CreateDirectory(files.Root);
        var ready = files.FilePath("fence.json");
        var args = mode == "--appsurface-keycloak-owner-fence"
            ? new[] { mode, ready, "not-a-port", "test-owner" }
            : new[] { mode, files.FilePath("private-input.json"), ready, "not-a-port", "test-owner" };
        using var error = new StringWriter();

        var exitCode = await AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
            args, _ => null, TextWriter.Null, error, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(files.Root));
        Assert.Equal("AppSurface Keycloak realm-ready gate failed. Code: ASKEYC001.", error.ToString().Trim());
    }

    [Fact]
    public async Task OwnerFencePublishesCaptureBeforeAcknowledgingAndDisposesAfterOwnerLoss()
    {
        if (OperatingSystem.IsWindows()) return;

        using var files = new TestFiles();
        Directory.CreateDirectory(files.Root);
        var readyPath = files.FilePath("owner-fence.json");
        var listeningPath = KeycloakWorkerProtocol.ListeningMarkerPath(readyPath);
        var owner = Guid.NewGuid().ToString("N");
        var reference = CreateTestAnchorReference(owner);
        var inputPath = WriteTestAnchorDeclaration(files, reference);
        var custody = new TestCustody();
        var port = GetAvailableOwnerPort();
        var processExitCode = 0;

        var running = AppSurfaceKeycloakRealmReadyEntryPoint.HoldAnchorCoreAsync(
            inputPath, readyPath, CancellationToken.None, port, owner,
            declaration =>
            {
                Assert.Equal(owner, declaration.RunId);
                Assert.Equal(reference.Authority.AbsoluteUri, declaration.Authority);
                Assert.Equal(reference.ClientId, declaration.ClientId);
                Assert.Equal("fixture-only-anchor-custody", declaration.ClientSecret);
                return (custody, reference);
            }, TextWriter.Null,
            exitProcess: code => { Interlocked.Exchange(ref processExitCode, code); });

        Assert.Equal(owner, File.ReadAllText(listeningPath));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        var frame = OwnerHeartbeatFrame(owner);
        Assert.Equal(33, frame.Length);
        await stream.WriteAsync(frame);
        Assert.Equal((byte)'R', await ReadOwnerAcknowledgementAsync(stream));
        var published = JsonSerializer.Deserialize<KeycloakClientCredentialReference>(File.ReadAllText(readyPath));
        Assert.NotNull(published);
        Assert.Equal(owner, published.RunId);
        Assert.Equal(0, custody.DisposeCount);

        await stream.WriteAsync(frame);
        Assert.Equal((byte)'R', await ReadOwnerAcknowledgementAsync(stream));
        client.Client.Shutdown(SocketShutdown.Send);

        Assert.Equal(71, await running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, Volatile.Read(ref processExitCode));
        Assert.Equal(1, custody.DisposeCount);
    }

    [Fact]
    public async Task OwnerFenceCollisionDisposesCaptureWithoutAcknowledgingOrReplacingFile()
    {
        if (OperatingSystem.IsWindows()) return;

        using var files = new TestFiles();
        var readyPath = files.Write("collision-reference.json", "existing-fixture-data");
        var listeningPath = KeycloakWorkerProtocol.ListeningMarkerPath(readyPath);
        var owner = Guid.NewGuid().ToString("N");
        var reference = CreateTestAnchorReference(owner);
        var inputPath = WriteTestAnchorDeclaration(files, reference);
        var custody = new TestCustody();
        var port = GetAvailableOwnerPort();
        var running = AppSurfaceKeycloakRealmReadyEntryPoint.HoldAnchorCoreAsync(
            inputPath, readyPath, CancellationToken.None, port, owner,
            _ => (custody, reference), TextWriter.Null,
            exitProcess: _ => { });

        Assert.Equal(owner, File.ReadAllText(listeningPath));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(OwnerHeartbeatFrame(owner));

        await Assert.ThrowsAsync<IOException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        var response = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(response.AsMemory()).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("existing-fixture-data", File.ReadAllText(readyPath));
        Assert.Equal(1, custody.DisposeCount);
    }

    [Fact]
    public async Task OwnerFenceRejectsAnotherOwnersDeclarationBeforeInvokingNativeCapture()
    {
        if (OperatingSystem.IsWindows()) return;

        using var files = new TestFiles();
        var readyPath = files.FilePath("unpublished-reference.json");
        var owner = Guid.NewGuid().ToString("N");
        var otherOwner = CreateTestAnchorReference(Guid.NewGuid().ToString("N"));
        var inputPath = WriteTestAnchorDeclaration(files, otherOwner);
        var port = GetAvailableOwnerPort();
        var calls = 0;

        var running = AppSurfaceKeycloakRealmReadyEntryPoint.HoldAnchorCoreAsync(
            inputPath, readyPath, CancellationToken.None, port, owner,
            _ =>
            {
                calls++;
                return (new TestCustody(), otherOwner);
            }, TextWriter.Null, exitProcess: _ => { });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(OwnerHeartbeatFrame(owner));

        await Assert.ThrowsAsync<InvalidDataException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, await stream.ReadAsync(new byte[1].AsMemory()).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, calls);
        Assert.False(File.Exists(readyPath));
    }

    [Fact]
    public async Task UnfencedAnchorCancellationAfterPublicationReturnsNormallyAndDisposesCapture()
    {
        if (OperatingSystem.IsWindows()) return;

        using var files = new TestFiles();
        Directory.CreateDirectory(files.Root);
        var readyPath = files.FilePath("unfenced-reference.json");
        var owner = Guid.NewGuid().ToString("N");
        var reference = CreateTestAnchorReference(owner);
        var inputPath = WriteTestAnchorDeclaration(files, reference);
        var custody = new TestCustody();
        using var cancellation = new CancellationTokenSource();
        using var diagnostic = new CancelOnWriteLineTextWriter(cancellation);

        var exit = await AppSurfaceKeycloakRealmReadyEntryPoint.HoldAnchorCoreAsync(
            inputPath, readyPath, cancellation.Token, ownerPort: null, expectedOwner: null,
            createCapture: _ => (custody, reference), standardError: diagnostic).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, exit);
        Assert.True(diagnostic.WroteLine);
        Assert.True(File.Exists(readyPath));
        Assert.Equal(owner, JsonSerializer.Deserialize<KeycloakClientCredentialReference>(File.ReadAllText(readyPath))?.RunId);
        Assert.Equal(1, custody.DisposeCount);
    }

    [Fact]
    public async Task FenceOnlyCoreAcknowledgesOwnerWithoutCreatingCaptureOrReference()
    {
        using var files = new TestFiles();
        Directory.CreateDirectory(files.Root);
        var readyPath = files.FilePath("fence-only.json");
        var listeningPath = KeycloakWorkerProtocol.ListeningMarkerPath(readyPath);
        var owner = Guid.NewGuid().ToString("N");
        var port = GetAvailableOwnerPort();
        var processExitCode = 0;

        var running = AppSurfaceKeycloakRealmReadyEntryPoint.HoldAnchorCoreAsync(
            null, readyPath, CancellationToken.None, port, owner,
            createCapture: _ => throw new InvalidOperationException("A fence without credentials cannot create custody."),
            standardError: TextWriter.Null,
            exitProcess: code => { Interlocked.Exchange(ref processExitCode, code); });
        Assert.Equal(owner, File.ReadAllText(listeningPath));
        Assert.False(File.Exists(readyPath));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(OwnerHeartbeatFrame(owner));
        Assert.Equal((byte)'R', await ReadOwnerAcknowledgementAsync(stream));
        client.Client.Shutdown(SocketShutdown.Send);

        Assert.Equal(71, await running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(File.Exists(readyPath));
        Assert.Equal(0, Volatile.Read(ref processExitCode));
    }

    [Fact]
    public async Task OwnerFenceCancellationClosesTheAcceptedConnection()
    {
        using var files = new TestFiles();
        Directory.CreateDirectory(files.Root);
        var owner = Guid.NewGuid().ToString("N");
        var port = GetAvailableOwnerPort();
        var processExitCode = 0;
        using var cancellation = new CancellationTokenSource();

        var running = AppSurfaceKeycloakRealmReadyEntryPoint.RunOwnerFenceAsync(
            files.FilePath("cancelled-owner-fence.json"), port, owner, cancellation.Token,
            exitProcess: code => { Interlocked.Exchange(ref processExitCode, code); });
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(OwnerHeartbeatFrame(owner));
        Assert.Equal((byte)'R', await ReadOwnerAcknowledgementAsync(stream));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, Volatile.Read(ref processExitCode));
    }

    [Fact]
    public async Task OwnerFenceEndsWhenTheSteadyHeartbeatNamesAnotherOwner()
    {
        using var files = new TestFiles();
        Directory.CreateDirectory(files.Root);
        var owner = Guid.NewGuid().ToString("N");
        var port = GetAvailableOwnerPort();
        var processExitCode = 0;

        var running = AppSurfaceKeycloakRealmReadyEntryPoint.RunOwnerFenceAsync(
            files.FilePath("wrong-steady-owner-fence.json"), port, owner, CancellationToken.None,
            exitProcess: code => { Interlocked.Exchange(ref processExitCode, code); });
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(OwnerHeartbeatFrame(owner));
        Assert.Equal((byte)'R', await ReadOwnerAcknowledgementAsync(stream));

        await stream.WriteAsync(OwnerHeartbeatFrame(Guid.NewGuid().ToString("N")));

        Assert.Equal(71, await running.WaitAsync(TimeSpan.FromSeconds(5)));
        var closed = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(closed.AsMemory()).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, Volatile.Read(ref processExitCode));
    }

    [Fact]
    public async Task OwnerFenceExpiryStopsPreparationWithoutAcknowledgingTheOwner()
    {
        using var files = new TestFiles();
        Directory.CreateDirectory(files.Root);
        var owner = Guid.NewGuid().ToString("N");
        var port = GetAvailableOwnerPort();
        var preparationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expiry = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var processExitCode = 0;

        var running = AppSurfaceKeycloakRealmReadyEntryPoint.RunOwnerFenceAsync(
            files.FilePath("expired-owner-fence.json"), port, owner, CancellationToken.None,
            async token =>
            {
                preparationStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            onExpired =>
            {
                expiry.TrySetResult(onExpired);
                return new CancellationTokenSource();
            },
            code => { Interlocked.Exchange(ref processExitCode, code); });
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(OwnerHeartbeatFrame(owner));
        await preparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var expireInitialFence = await expiry.Task.WaitAsync(TimeSpan.FromSeconds(5));
        expireInitialFence();

        Assert.Equal(71, await running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(71, Volatile.Read(ref processExitCode));
        var response = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(response.AsMemory()).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ProductionOwnerFenceRejectsUnsupportedPlatformsBeforeListening()
    {
        if (OperatingSystem.IsWindows()
            || OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.Arm64)
            return;

        using var files = new TestFiles();
        Directory.CreateDirectory(files.Root);
        var readyPath = files.FilePath("unsupported-owner-fence.json");
        var listeningPath = KeycloakWorkerProtocol.ListeningMarkerPath(readyPath);
        using var error = new StringWriter();

        var exitCode = await AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
            [KeycloakWorkerProtocol.OwnerFence, readyPath, GetAvailableOwnerPort().ToString(), Guid.NewGuid().ToString("N")],
            _ => null, TextWriter.Null, error, new CancellationToken(canceled: true));

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(listeningPath));
        Assert.Equal("AppSurface Keycloak realm-ready gate failed. Code: ASKEYC001.", error.ToString().Trim());
    }

    [Fact]
    public async Task MissingConfiguredBindingWritesRedactedFailureEnvelope()
    {
        if (OperatingSystem.IsWindows()) return;

        using var files = new TestFiles();
        Directory.CreateDirectory(files.Root);
        var output = files.FilePath("result.json");
        using var error = new StringWriter();

        var exitCode = await AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
            ["--appsurface-keycloak-reconcile", files.FilePath("desired.json"), files.FilePath("bindings.json"), output],
            _ => null, TextWriter.Null, error, CancellationToken.None);

        Assert.Equal(70, exitCode);
        using var envelope = JsonDocument.Parse(File.ReadAllText(output));
        Assert.Equal("WorkerBoundaryFailure", envelope.RootElement.GetProperty("code").GetString());
        Assert.Equal("configuration", envelope.RootElement.GetProperty("stage").GetString());
        Assert.Equal("InvalidDataException", envelope.RootElement.GetProperty("type").GetString());
        Assert.DoesNotContain(files.Root, File.ReadAllText(output));
        Assert.DoesNotContain(files.Root, error.ToString());
    }

    [Fact]
    public async Task ConfiguredFailureKeepsSafeStatusWhenReceiptParentIsMissing()
    {
        if (OperatingSystem.IsWindows()) return;

        using var files = new TestFiles();
        var missingParent = TestPathUtils.PathUnder(files.Root, "missing-parent");
        var result = TestPathUtils.PathUnder(missingParent, "result.json");
        using var error = new StringWriter();
        var reconciled = false;

        var exitCode = await AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
            ["--appsurface-keycloak-reconcile", files.FilePath("desired.json"), files.FilePath("bindings.json"), result],
            _ => null, TextWriter.Null, error, CancellationToken.None,
            reconcile: (_, _, _, _) =>
            {
                reconciled = true;
                throw new InvalidOperationException("must not reach reconciliation");
            });

        Assert.Equal(70, exitCode);
        Assert.False(reconciled);
        Assert.False(Directory.Exists(missingParent));
        Assert.False(File.Exists(result));
        Assert.Equal("AppSurface Keycloak realm-ready gate failed. Code: WorkerBoundaryFailure.", error.ToString().Trim());
        Assert.DoesNotContain(files.Root, error.ToString());
    }

    [Fact]
    public async Task ConfiguredModeRejectsUnsafeBindingPermissionsBeforeReconciliation()
    {
        if (OperatingSystem.IsWindows()) return;

        using var files = new TestFiles();
        var desired = files.Write("desired.json", DesiredJson);
        var bindings = files.Write("bindings.json", BindingsJson);
        File.SetUnixFileMode(bindings, UnixFileMode.UserRead | UnixFileMode.GroupRead);
        var output = files.FilePath("result.json");
        var called = false;
        using var error = new StringWriter();

        var exitCode = await AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
            ["--appsurface-keycloak-reconcile", desired, bindings, output], _ => null, TextWriter.Null, error,
            CancellationToken.None, reconcile: (_, _, _, _) =>
            {
                called = true;
                throw new InvalidOperationException("must not reach reconciliation");
            });

        Assert.Equal(70, exitCode);
        Assert.False(called);
        using var envelope = JsonDocument.Parse(File.ReadAllText(output));
        Assert.Equal("configuration", envelope.RootElement.GetProperty("stage").GetString());
        Assert.Equal("InvalidDataException", envelope.RootElement.GetProperty("type").GetString());
        Assert.DoesNotContain("sensitive-admin-password", File.ReadAllText(output));
        Assert.DoesNotContain("sensitive-admin-password", error.ToString());
    }

    [Fact]
    public async Task ConfiguredModeRejectsSymbolicLinkBindingBeforeReconciliation()
    {
        if (OperatingSystem.IsWindows()) return;

        using var files = new TestFiles();
        var desired = files.Write("desired.json", DesiredJson);
        var target = files.Write("real-bindings.json", BindingsJson);
        File.SetUnixFileMode(target, UnixFileMode.UserRead);
        var bindings = files.FilePath("bindings.json");
        File.CreateSymbolicLink(bindings, target);
        var output = files.FilePath("result.json");
        var called = false;
        using var error = new StringWriter();

        var exitCode = await AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
            ["--appsurface-keycloak-reconcile", desired, bindings, output], _ => null, TextWriter.Null, error,
            CancellationToken.None, reconcile: (_, _, _, _) =>
            {
                called = true;
                throw new InvalidOperationException("must not reach reconciliation");
            });

        Assert.Equal(70, exitCode);
        Assert.False(called);
        using var envelope = JsonDocument.Parse(File.ReadAllText(output));
        Assert.Equal("configuration", envelope.RootElement.GetProperty("stage").GetString());
        Assert.Equal("InvalidDataException", envelope.RootElement.GetProperty("type").GetString());
        Assert.DoesNotContain(files.Root, File.ReadAllText(output));
        Assert.DoesNotContain(files.Root, error.ToString());
    }

    [Theory]
    [InlineData("large-desired")]
    [InlineData("large-bindings")]
    [InlineData("null-desired")]
    [InlineData("null-bindings")]
    public async Task ConfiguredModeRejectsOversizeOrNullDeclarationsBeforeAnyReconciliation(string scenario)
    {
        if (OperatingSystem.IsWindows()) return;

        using var files = new TestFiles();
        var desired = files.Write("desired.json", scenario switch
        {
            "large-desired" => new string(' ', 1024 * 1024 + 1),
            "null-desired" => "null",
            _ => DesiredJson
        });
        var bindings = files.Write("bindings.json", scenario switch
        {
            "large-bindings" => new string(' ', 16 * 1024 + 1),
            "null-bindings" => "null",
            _ => BindingsJson
        });
        File.SetUnixFileMode(bindings, UnixFileMode.UserRead);
        var output = files.FilePath("result.json");
        var invoked = false;

        var exit = await AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
            ["--appsurface-keycloak-reconcile", desired, bindings, output], _ => null,
            TextWriter.Null, TextWriter.Null, CancellationToken.None,
            reconcile: (_, _, _, _) =>
            {
                invoked = true;
                throw new InvalidOperationException("The invalid declaration reached reconciliation.");
            });

        Assert.Equal(70, exit);
        Assert.False(invoked);
        using var receipt = JsonDocument.Parse(File.ReadAllText(output));
        Assert.Equal("configuration", receipt.RootElement.GetProperty("stage").GetString());
        Assert.Equal("InvalidDataException", receipt.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task ConfiguredModeWritesSafeResultEnvelopeForControlledSuccess()
    {
        if (OperatingSystem.IsWindows()) return;

        using var files = new TestFiles();
        var desired = files.Write("desired.json", DesiredJson);
        var bindings = files.Write("bindings.json", BindingsJson);
        File.SetUnixFileMode(bindings, UnixFileMode.UserRead);
        var output = files.FilePath("result.json");
        using var error = new StringWriter();

        var exitCode = await AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
            ["--appsurface-keycloak-reconcile", desired, bindings, output], _ => null, TextWriter.Null, error,
            CancellationToken.None,
            reconcile: (_, _, _, _) => Task.FromResult(new RealmReconcileResult(
                true, "Ready", "Observed", 0, 0, 0, 1, "digest", "realm-id", "client-id", "NoChange", "None")));

        Assert.Equal(0, exitCode);
        using var envelope = JsonDocument.Parse(File.ReadAllText(output));
        Assert.Equal("Ready", envelope.RootElement.GetProperty("result").GetProperty("Code").GetString());
        Assert.True(envelope.RootElement.GetProperty("result").GetProperty("Ready").GetBoolean());
        Assert.True(File.Exists(output + ".started"));
        Assert.DoesNotContain("sensitive-admin-password", File.ReadAllText(output));
        Assert.DoesNotContain("sensitive-admin-password", error.ToString());
    }

    [Fact]
    public async Task ConfiguredModeReturnsExactControlledNotReadyResult()
    {
        if (OperatingSystem.IsWindows()) return;

        using var files = new TestFiles();
        var desired = files.Write("desired.json", DesiredJson);
        var bindings = files.Write("bindings.json", BindingsJson);
        File.SetUnixFileMode(bindings, UnixFileMode.UserRead);
        var output = files.FilePath("result.json");
        using var error = new StringWriter();
        var expected = new RealmReconcileResult(
            false, "ClientMismatch", "verification", 2, 1, 0, 0, "safe-digest", "realm-safe", "client-safe", "NoChange", "None");

        var exitCode = await AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
            ["--appsurface-keycloak-reconcile", desired, bindings, output], _ => null, TextWriter.Null, error,
            CancellationToken.None, reconcile: (_, _, _, _) => Task.FromResult(expected));

        Assert.Equal(20, exitCode);
        using var envelope = JsonDocument.Parse(File.ReadAllText(output));
        var result = envelope.RootElement.GetProperty("result");
        Assert.False(result.GetProperty("Ready").GetBoolean());
        Assert.Equal("ClientMismatch", result.GetProperty("Code").GetString());
        Assert.Equal("verification", result.GetProperty("Stage").GetString());
        Assert.Equal(expected, result.Deserialize<RealmReconcileResult>());
        Assert.Equal("AppSurface Keycloak reconciliation: ClientMismatch/verification.", error.ToString().Trim());
    }

    [Theory]
    [InlineData("public-with-secret", "client-capture")]
    [InlineData("public-with-reference-file", "client-capture")]
    [InlineData("confidential-without-source", "client-capture")]
    [InlineData("capture-root-without-run", "configuration")]
    [InlineData("capture-run-without-root", "configuration")]
    [InlineData("missing-reference", "client-capture")]
    [InlineData("malformed-reference", "client-capture")]
    [InlineData("null-reference", "client-capture")]
    [InlineData("empty-reference", "client-capture")]
    [InlineData("oversized-reference", "client-capture")]
    [InlineData("linked-reference", "client-capture")]
    [InlineData("conflicting-sources", "client-capture")]
    [InlineData("conflicting-reference-forms", "client-capture")]
    [InlineData("wrong-reference-authority", "client-capture")]
    [InlineData("wrong-reference-client", "client-capture")]
    [InlineData("wrong-reference-run", "client-capture")]
    [InlineData("file-custody-reference", "client-capture")]
    public async Task CredentialBoundaryRejectsInvalidSourceCombinationsBeforeReconciliation(string scenario, string stage)
    {
        if (OperatingSystem.IsWindows()) return;

        using var files = new TestFiles();
        var desired = JsonNode.Parse(DesiredJson)!.AsObject();
        desired["confidential"] = scenario is not ("public-with-secret" or "public-with-reference-file"
            or "capture-root-without-run" or "capture-run-without-root");
        var bindings = JsonNode.Parse(BindingsJson)!.AsObject();
        const string syntheticPrivateValue = "synthetic-confidential-input";
        const string runId = "aabbccddeeff00112233445566778899";
        var reference = KeycloakClientCredentialReference.ForSealedMemory(
            new KeycloakClientCredentialMemoryIdentity(Environment.ProcessId, 1, 42, 10), runId,
            new Uri(scenario == "wrong-reference-authority"
                ? "https://localhost:8443/realms/other" : "https://localhost:8443/realms/demo"),
            scenario == "wrong-reference-client" ? "other-client" : "public-client");
        switch (scenario)
        {
            case "public-with-secret": bindings["clientSecret"] = syntheticPrivateValue; break;
            case "public-with-reference-file": bindings["clientCredentialReferenceFile"] = files.Write("reference.json", "{"); break;
            case "confidential-without-source": break;
            case "capture-root-without-run": bindings["captureRoot"] = files.Root; break;
            case "capture-run-without-root": bindings["captureRunId"] = runId; break;
            case "missing-reference": bindings["clientCredentialReferenceFile"] = files.FilePath("absent.json"); break;
            case "malformed-reference": bindings["clientCredentialReferenceFile"] = files.Write("reference.json", "{"); break;
            case "null-reference": bindings["clientCredentialReferenceFile"] = files.Write("reference.json", "null"); break;
            case "empty-reference": bindings["clientCredentialReferenceFile"] = files.Write("reference.json", ""); break;
            case "oversized-reference": bindings["clientCredentialReferenceFile"] = files.Write("reference.json", new string(' ', 16 * 1024 + 1)); break;
            case "linked-reference":
                var origin = files.Write("valid-reference.json", JsonSerializer.Serialize(reference));
                var link = files.FilePath("linked-reference.json");
                File.CreateSymbolicLink(link, origin);
                bindings["clientCredentialReferenceFile"] = link;
                break;
            case "conflicting-sources":
                bindings["clientSecret"] = syntheticPrivateValue;
                bindings["captureRoot"] = files.Root;
                bindings["captureRunId"] = runId;
                break;
            case "conflicting-reference-forms":
                bindings["clientCredentialReference"] = JsonSerializer.SerializeToNode(reference);
                bindings["clientCredentialReferenceFile"] = files.FilePath("absent.json");
                break;
            case "wrong-reference-authority":
            case "wrong-reference-client":
            case "wrong-reference-run":
            case "file-custody-reference":
                bindings["expectedCaptureRunId"] = scenario == "wrong-reference-run" ? Guid.Empty.ToString("N") : runId;
                if (scenario == "file-custody-reference")
                    reference = new KeycloakClientCredentialReference(files.Root, runId, reference.Authority, reference.ClientId);
                bindings["clientCredentialReferenceFile"] = files.Write("reference.json", JsonSerializer.Serialize(reference));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        var desiredPath = files.Write("desired.json", desired.ToJsonString());
        var bindingPath = files.Write("bindings.json", bindings.ToJsonString());
        File.SetUnixFileMode(bindingPath, UnixFileMode.UserRead);
        using var error = new StringWriter();
        var output = files.FilePath("result.json");
        var reconciled = false;

        var result = await AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
            ["--appsurface-keycloak-reconcile", desiredPath, bindingPath, output], _ => null, TextWriter.Null, error,
            CancellationToken.None, reconcile: (_, _, _, _) =>
            {
                reconciled = true;
                throw new InvalidOperationException("must not run");
            });

        Assert.Equal(70, result);
        Assert.False(reconciled);
        var receipt = File.ReadAllText(output);
        using var envelope = JsonDocument.Parse(receipt);
        Assert.Equal(stage, envelope.RootElement.GetProperty("stage").GetString());
        if (scenario == "public-with-reference-file")
            Assert.Equal(nameof(InvalidDataException), envelope.RootElement.GetProperty("type").GetString());
        Assert.DoesNotContain(syntheticPrivateValue, receipt);
        Assert.DoesNotContain(syntheticPrivateValue, error.ToString());
    }

    [Fact]
    public async Task ConfiguredWorkerReadsQualifiedFileCaptureAndOnlyReportsTheOutcome()
    {
        if (!OperatingSystem.IsMacOS() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            != System.Runtime.InteropServices.Architecture.Arm64) return;

        const string secret = "file-capture-only-value";
        var runId = Guid.NewGuid().ToString("N");
        var custody = TestPathUtils.PathUnder("/private/tmp", "keycloak-worker-file-custody-" + runId);
        Directory.CreateDirectory(custody, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            using var files = new TestFiles();
            RealmClientCapture.Capture(custody, runId, secret);
            var desired = JsonNode.Parse(DesiredJson)!.AsObject();
            desired["confidential"] = true;
            var bindings = JsonNode.Parse(BindingsJson)!.AsObject();
            bindings["captureRoot"] = custody;
            bindings["captureRunId"] = runId;
            var desiredPath = files.Write("desired.json", desired.ToJsonString());
            var bindingsPath = files.Write("bindings.json", bindings.ToJsonString());
            File.SetUnixFileMode(bindingsPath, UnixFileMode.UserRead);
            var resultPath = files.FilePath("result.json");
            using var error = new StringWriter();
            var exit = await AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
                ["--appsurface-keycloak-reconcile", desiredPath, bindingsPath, resultPath], _ => null,
                TextWriter.Null, error, CancellationToken.None,
                reconcile: (_, _, observed, _) =>
                {
                    Assert.Equal(secret, observed.ClientSecret);
                    return Task.FromResult(new RealmReconcileResult(true, "Ready", "Observed", 0, 0, 0, 1,
                        "digest", "realm-id", "client-id", "NoChange", "None"));
                });

            Assert.Equal(0, exit);
            var rawResult = File.ReadAllText(resultPath);
            using var result = JsonDocument.Parse(rawResult);
            Assert.True(result.RootElement.GetProperty("result").GetProperty("Ready").GetBoolean());
            Assert.DoesNotContain(secret, rawResult, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(custody, rawResult, StringComparison.Ordinal);
        }
        finally
        {
            RealmClientCapture.UnsealForOwnedCleanup(custody);
            Directory.Delete(custody, recursive: true);
        }
    }

    [Fact]
    public async Task WorkerPassesExplicitConfidentialAndBrokerCredentialsWithoutPublishingThem()
    {
        if (OperatingSystem.IsWindows()) return;

        using var files = new TestFiles();
        const string clientSecret = "fixture-explicit-client-secret";
        const string brokerSecret = "fixture-explicit-broker-secret";
        var desired = JsonNode.Parse(DesiredJson)!.AsObject();
        desired["confidential"] = true;
        var bindings = JsonNode.Parse(BindingsJson)!.AsObject();
        bindings["clientSecret"] = clientSecret;
        bindings["brokerSecrets"] = new JsonObject { ["google"] = brokerSecret };
        var desiredPath = files.Write("desired.json", desired.ToJsonString());
        var bindingPath = files.Write("bindings.json", bindings.ToJsonString());
        File.SetUnixFileMode(bindingPath, UnixFileMode.UserRead);
        var output = files.FilePath("result.json");
        using var error = new StringWriter();
        RealmReconcileCredentials? observed = null;

        var exit = await AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
            ["--appsurface-keycloak-reconcile", desiredPath, bindingPath, output], _ => null,
            TextWriter.Null, error, CancellationToken.None,
            reconcile: (_, _, credentials, _) =>
            {
                observed = credentials;
                return Task.FromResult(new RealmReconcileResult(
                    true, "Ready", "Observed", 0, 0, 0, 1, "digest", "realm-id", "client-id", "NoChange", "None"));
            });

        Assert.Equal(0, exit);
        Assert.NotNull(observed);
        Assert.Equal(clientSecret, observed.ClientSecret);
        Assert.Equal(brokerSecret, observed.BrokerSecrets?["google"]);
        var receipt = File.ReadAllText(output);
        foreach (var secret in new[] { clientSecret, brokerSecret })
        {
            Assert.DoesNotContain(secret, receipt);
            Assert.DoesNotContain(secret, error.ToString());
        }
    }

    [Fact]
    public async Task ConfiguredWorkerCancellationReturnsBoundedNonzeroAndNoReadyResult()
    {
        if (OperatingSystem.IsWindows()) return;

        using var files = new TestFiles();
        var desired = files.Write("desired.json", DesiredJson);
        var bindings = files.Write("bindings.json", BindingsJson);
        File.SetUnixFileMode(bindings, UnixFileMode.UserRead);
        using var cancellation = new CancellationTokenSource();
        using var error = new StringWriter();
        var output = files.FilePath("result.json");

        var exit = await AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
            ["--appsurface-keycloak-reconcile", desired, bindings, output], _ => null, TextWriter.Null, error,
            cancellation.Token, reconcile: (_, _, _, cancellationToken) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<RealmReconcileResult>(cancellationToken);
            });

        Assert.Equal(124, exit);
        Assert.False(File.Exists(output));
        Assert.Contains("was cancelled", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("matching", true)]
    [InlineData("different", false)]
    [InlineData("wrong-host", false)]
    [InlineData("expired", false)]
    [InlineData("not-yet-valid", false)]
    public async Task BaselineHttpClientRequiresExactPinValidDatesAndMatchingHost(string scenario, bool accepted)
    {
        using var files = new TestFiles();
        using var serverCertificate = CreateCertificate(scenario);
        using var alternateCertificate = scenario == "different" ? CreateCertificate("matching") : null;
        var pinFile = files.Write("tls-pin.pem", (alternateCertificate ?? serverCertificate).ExportCertificatePem());
        var environment = BaselineEnvironment("unused");
        environment["APPSURFACE_KEYCLOAK_CA_CERTIFICATE_FILE"] = pinFile;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var error = new StringWriter();
        var server = RespondAsync(listener, serverCertificate, accepted, timeout.Token);

        try
        {
            var exit = await AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
                ["--appsurface-keycloak-realm-ready"], environment.GetValueOrDefault, TextWriter.Null, error, timeout.Token,
                async (http, _, _, ct) =>
                {
                    using var response = await http.GetAsync(new Uri($"https://127.0.0.1:{endpoint.Port}/"), ct);
                    response.EnsureSuccessStatusCode();
                });

            Assert.Equal(accepted ? 0 : 1, exit);
            if (accepted)
                Assert.Contains("gate completed", error.ToString());
            else
                Assert.Contains("Code: ASKEYC001", error.ToString());
        }
        finally
        {
            timeout.Cancel();
            await server;
        }
    }

    private static X509Certificate2 CreateCertificate(string scenario)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        if (scenario == "wrong-host")
            names.AddDnsName("fixture.invalid");
        else
            names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        var now = DateTimeOffset.UtcNow;
        var notBefore = scenario == "not-yet-valid" ? now.AddDays(1) : now.AddDays(-2);
        var notAfter = scenario == "expired" ? now.AddDays(-1) : now.AddDays(2);
        return request.CreateSelfSigned(notBefore, notAfter);
    }

    private static async Task RespondAsync(TcpListener listener, X509Certificate2 certificate, bool accepted, CancellationToken token)
    {
        try
        {
            using var client = await listener.AcceptTcpClientAsync(token);
            await using var tls = new SslStream(client.GetStream());
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, token);
            using var reader = new StreamReader(tls, leaveOpen: true);
            while (await reader.ReadLineAsync(token) is { Length: > 0 }) { }
            await tls.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), token);
        }
        catch (Exception error) when (!accepted && error is AuthenticationException or IOException or OperationCanceledException)
        {
            // A client that rejects the pinned peer may abandon the TLS exchange at any point.
        }
    }

    private static int GetAvailableOwnerPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static byte[] OwnerHeartbeatFrame(string owner) => Encoding.ASCII.GetBytes(owner + "\n");

    private static async Task<byte> ReadOwnerAcknowledgementAsync(NetworkStream stream)
    {
        var acknowledgement = new byte[1];
        var count = await stream.ReadAsync(acknowledgement.AsMemory()).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, count);
        return acknowledgement[0];
    }

    private static KeycloakClientCredentialReference CreateTestAnchorReference(string runId) =>
        KeycloakClientCredentialReference.ForSealedMemory(
            new KeycloakClientCredentialMemoryIdentity(Environment.ProcessId, 1, 42, 10), runId,
            new Uri("https://localhost:8443/realms/demo"), "fixture-client");

    private static string WriteTestAnchorDeclaration(TestFiles files, KeycloakClientCredentialReference reference)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var declaration = new AppSurfaceKeycloakRealmReadyEntryPoint.MemoryAnchorInput(
            reference.RunId, reference.Authority.AbsoluteUri, reference.ClientId, "fixture-only-anchor-custody");
        var inputPath = files.Write("anchor-declaration.json", JsonSerializer.Serialize(declaration));
        File.SetUnixFileMode(inputPath, UnixFileMode.UserRead);
        return inputPath;
    }

    private static Dictionary<string, string?> BaselineEnvironment(string importPath) => new()
    {
        ["APPSURFACE_KEYCLOAK_REALM_READY_AUTHORITY"] = "https://localhost:8443/realms/demo",
        ["APPSURFACE_KEYCLOAK_REALM_READY_CLIENT_ID"] = "public-client",
        ["APPSURFACE_KEYCLOAK_REALM_READY_CALLBACK_PATH"] = "/callback",
        ["APPSURFACE_KEYCLOAK_REALM_READY_SIGNED_OUT_CALLBACK_PATH"] = "/signed-out",
        ["APPSURFACE_KEYCLOAK_REALM_READY_REDIRECT_URIS"] = "[\"http://localhost:5001/callback\"]",
        ["APPSURFACE_KEYCLOAK_REALM_READY_POST_LOGOUT_REDIRECT_URIS"] = "[\"http://localhost:5001/signed-out\"]",
        ["APPSURFACE_KEYCLOAK_REALM_READY_SEEDED_USER_NAMES"] = "alice; bob",
        ["APPSURFACE_KEYCLOAK_REALM_READY_REALM_IMPORT_DIRECTORY"] = importPath,
        ["APPSURFACE_KEYCLOAK_REALM_READY_LOGIN_THEME_NAME"] = ""
    };

    private const string DesiredJson = """
        {"realm":"demo","clientId":"public-client","authority":"https://localhost:8443/realms/demo","confidential":false,"allowPublicDowngrade":false,"redirectUris":["http://localhost:5001/callback"],"postLogoutRedirectUris":["http://localhost:5001/signed-out"],"webOrigins":["http://localhost:5001"],"mappers":[],"brokers":[],"removeMappers":[],"removeBrokers":[]}
        """;

    private const string BindingsJson = """
        {"baseAddress":"https://localhost:8443/","adminUsername":"admin","adminPassword":"sensitive-admin-password","clientSecret":null,"captureRoot":null,"captureRunId":null,"brokerSecrets":null}
        """;

    private sealed class TestCustody : IDisposable
    {
        private int _disposeCount;

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class CancelOnWriteLineTextWriter : StringWriter
    {
        private readonly CancellationTokenSource _cancellation;

        internal CancelOnWriteLineTextWriter(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        internal bool WroteLine { get; private set; }

        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            WroteLine = true;
            _cancellation.Cancel();
        }
    }

    private sealed class TestFiles : IDisposable
    {
        internal string Root { get; } = TestPathUtils.PathUnder(System.IO.Path.GetTempPath(), "realm-ready-entry-" + Guid.NewGuid().ToString("N"));

        internal string FilePath(string name) => TestPathUtils.PathUnder(Root, name);

        internal string Write(string name, string contents)
        {
            Directory.CreateDirectory(Root);
            var path = FilePath(name);
            File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
