using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Auth.Aspire.Keycloak;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class AppSurfaceKeycloakLinuxArm64WorkerTests
{
    private const string RunId = "0123456789abcdef0123456789abcdef";
    private const string Authority = "https://localhost:8443/realms/demo";
    private const string ClientId = "web-client";
    private const string Secret = "linux-arm64-worker-secret";

    [LinuxArm64Fact]
    public void NativeExpiringCaptureUsesTheSuppliedClockAndClosesItsDescriptor()
    {
        var uri = new Uri(Authority);
        var clock = new FixedClock(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        KeycloakClientCredentialReference reference;
        using (var capture = KeycloakClientCredentialMemoryCapture.Create(RunId, uri, ClientId, Secret, clock))
        {
            reference = capture.Reference;
            Assert.Equal(Secret, KeycloakClientCredentialMemoryCapture.Read(reference, clock));
            Assert.Throws<InvalidDataException>(() => KeycloakClientCredentialMemoryCapture.Read(reference, TimeProvider.System));
        }

        Assert.True(Record.Exception(() => KeycloakClientCredentialMemoryCapture.Read(reference, clock)) is IOException or InvalidDataException);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    [Theory]
    [LinuxArm64InlineData(false, false)]
    [LinuxArm64InlineData(true, false)]
    [LinuxArm64InlineData(true, true)]
    public async Task RealWorkerCreatesSealedMemfdAndConfiguredWorkerReadsSameRunCustody(bool fenced, bool referenceFromFile)
    {
        using var files = new TestFiles();
        var input = files.Write("anchor-input.json", JsonSerializer.Serialize(new
        {
            RunId,
            Authority,
            ClientId,
            ClientSecret = Secret
        }), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var ready = files.FilePath("anchor-reference.json");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var error = new StringWriter();

        var port = fenced ? ReserveLoopbackPort() : 0;
        string[] arguments = fenced
            ? ["--appsurface-keycloak-fenced-memory-anchor", input, ready, port.ToString(System.Globalization.CultureInfo.InvariantCulture), RunId]
            : ["--appsurface-keycloak-memory-anchor", input, ready];
        using var owner = fenced ? new TcpClient() : null;
        var anchor = AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(arguments, _ => null,
            TextWriter.Null, error, cancellation.Token);
        try
        {
            if (owner is not null)
            {
                while (!File.Exists(ready + ".listening"))
                {
                    Assert.False(anchor.IsCompleted, "The worker exited before opening the owner fence.");
                    await Task.Delay(TimeSpan.FromMilliseconds(20), cancellation.Token);
                }
                await owner.ConnectAsync(IPAddress.Loopback, port, cancellation.Token);
                var connection = owner.GetStream();
                await connection.WriteAsync(Encoding.ASCII.GetBytes(RunId + "\n"), cancellation.Token);
                var acknowledgement = new byte[1];
                await connection.ReadExactlyAsync(acknowledgement, cancellation.Token);
                Assert.Equal((byte)'R', acknowledgement[0]);
            }
            await WaitForReferenceAsync(ready, anchor, cancellation.Token);
            var referenceJson = await File.ReadAllTextAsync(ready, cancellation.Token);
            Assert.DoesNotContain(Secret, referenceJson, StringComparison.Ordinal);
            var reference = JsonSerializer.Deserialize<KeycloakClientCredentialReference>(referenceJson);
            Assert.NotNull(reference);
            Assert.Equal(RunId, reference.RunId);
            Assert.Equal(Authority, reference.Authority.AbsoluteUri);
            Assert.Equal(ClientId, reference.ClientId);
            Assert.Equal(string.Empty, reference.RunDirectory);
            Assert.NotNull(reference.MemoryIdentity);
            Assert.True(reference.MemoryIdentity.Inode > 0);
            Assert.Equal(Secret, reference.Read());
            Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(ready));

            var desiredNode = JsonNode.Parse(DesiredJson)!.AsObject();
            desiredNode["confidential"] = true;
            var desired = files.Write("desired.json", desiredNode.ToJsonString());
            var bindingsNode = JsonNode.Parse(BindingsJson)!.AsObject();
            if (referenceFromFile)
                bindingsNode["clientCredentialReferenceFile"] = ready;
            else
                bindingsNode["clientCredentialReference"] = JsonNode.Parse(referenceJson);
            bindingsNode["expectedCaptureRunId"] = RunId;
            var bindings = files.Write("bindings.json", bindingsNode.ToJsonString(), UnixFileMode.UserRead);
            var output = files.FilePath("worker-result.json");
            RealmReconcileCredentials? observed = null;

            var exit = await AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
                ["--appsurface-keycloak-reconcile", desired, bindings, output], _ => null,
                TextWriter.Null, TextWriter.Null, CancellationToken.None,
                reconcile: (_, observedDesired, credentials, _) =>
                {
                    Assert.True(observedDesired.Confidential);
                    observed = credentials;
                    return Task.FromResult(new RealmReconcileResult(
                        true, "Ready", "Observed", 0, 0, 0, 1, "digest", "realm-id", ClientId, "NoChange", "None"));
                });

            Assert.Equal(0, exit);
            Assert.NotNull(observed);
            Assert.Equal(Secret, observed.ClientSecret);
            Assert.True(File.Exists(output));
            using var envelope = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            Assert.Equal("Ready", envelope.RootElement.GetProperty("result").GetProperty("Code").GetString());
            Assert.DoesNotContain(Secret, await File.ReadAllTextAsync(output));
            if (owner is not null)
            {
                owner.Dispose();
                await anchor.WaitAsync(cancellation.Token);
            }
        }
        finally
        {
            cancellation.Cancel();
            await anchor.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(fenced ? 71 : 0, await anchor);
        var diagnostics = error.ToString();
        Assert.Contains("AppSurface Keycloak sealed-memory anchor ready.", diagnostics, StringComparison.Ordinal);
        Assert.Equal(fenced, diagnostics.Contains("AppSurface Keycloak owner connection lost; namespace fencing.", StringComparison.Ordinal));
        Assert.DoesNotContain(Secret, diagnostics, StringComparison.Ordinal);
        var published = JsonSerializer.Deserialize<KeycloakClientCredentialReference>(await File.ReadAllTextAsync(ready));
        Assert.NotNull(published);
        Assert.True(Record.Exception(() => published.Read()) is IOException or InvalidDataException);
    }

    [LinuxArm64Fact]
    public async Task WorkerRejectsInvalidOwnerFencePortBeforeListeningOrPublishing()
    {
        using var files = new TestFiles();
        var ready = files.FilePath("owner-reference.json");
        using var error = new StringWriter();

        var exit = await AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
            ["--appsurface-keycloak-owner-fence", ready, "not-a-port", RunId], _ => null,
            TextWriter.Null, error, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.False(File.Exists(ready));
        Assert.False(File.Exists(ready + ".listening"));
        Assert.Equal("AppSurface Keycloak realm-ready gate failed. Code: ASKEYC001.", error.ToString().Trim());
    }

    [Theory]
    [LinuxArm64InlineData("missing")]
    [LinuxArm64InlineData("empty")]
    [LinuxArm64InlineData("oversized")]
    [LinuxArm64InlineData("linked")]
    [LinuxArm64InlineData("group-readable")]
    [LinuxArm64InlineData("null-descriptor")]
    public async Task MemoryAnchorRejectsUnqualifiedCustodyBeforePublishing(string scenario)
    {
        using var files = new TestFiles();
        var input = scenario switch
        {
            "missing" => files.FilePath("missing.json"),
            "empty" => files.Write("empty.json", ""),
            "oversized" => files.Write("oversized.json", new string('x', 16 * 1024 + 1)),
            "group-readable" => files.Write("group-readable.json", AnchorInput(RunId), UnixFileMode.UserRead | UnixFileMode.GroupRead),
            "null-descriptor" => files.Write("null.json", "null"),
            "linked" => File.CreateSymbolicLink(files.FilePath("input-link.json"), files.Write("input.json", AnchorInput(RunId))).FullName,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var ready = files.FilePath("anchor-reference.json");
        using var error = new StringWriter();

        var exit = await AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
            ["--appsurface-keycloak-memory-anchor", input, ready], _ => null,
            TextWriter.Null, error, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.False(File.Exists(ready));
        Assert.False(File.Exists(ready + ".listening"));
        Assert.Equal("AppSurface Keycloak realm-ready gate failed. Code: ASKEYC001.", error.ToString().Trim());
    }

    [LinuxArm64Fact]
    public async Task MemoryAnchorCannotReplaceAnExistingReference()
    {
        using var files = new TestFiles();
        var input = files.Write("input.json", AnchorInput(RunId));
        const string original = "previous reference";
        var ready = files.Write("occupied-reference.json", original, UnixFileMode.UserRead);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var error = new StringWriter();

        var exit = await AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
            ["--appsurface-keycloak-memory-anchor", input, ready], _ => null,
            TextWriter.Null, error, cancellation.Token);

        Assert.Equal(1, exit);
        Assert.Equal(original, await File.ReadAllTextAsync(ready));
        Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(ready));
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
    }

    [LinuxArm64Fact]
    public async Task FencedAnchorRejectsMismatchedCaptureOwnerBeforeAcknowledging()
    {
        using var files = new TestFiles();
        var input = files.Write("stale-input.json", AnchorInput(Guid.Empty.ToString("N")));
        var ready = files.FilePath("anchor-reference.json");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var error = new StringWriter();
        using var owner = new TcpClient();
        var port = ReserveLoopbackPort();
        var anchor = AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
            ["--appsurface-keycloak-fenced-memory-anchor", input, ready, port.ToString(System.Globalization.CultureInfo.InvariantCulture), RunId],
            _ => null, TextWriter.Null, error, cancellation.Token);
        try
        {
            while (!File.Exists(ready + ".listening"))
            {
                Assert.False(anchor.IsCompleted);
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellation.Token);
            }
            await owner.ConnectAsync(IPAddress.Loopback, port, cancellation.Token);
            var stream = owner.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(RunId + "\n"), cancellation.Token);

            Assert.Equal(1, await anchor.WaitAsync(cancellation.Token));
            var acknowledgement = new byte[1];
            Assert.Equal(0, await stream.ReadAsync(acknowledgement, cancellation.Token));
            Assert.False(File.Exists(ready));
            Assert.Equal("AppSurface Keycloak realm-ready gate failed. Code: ASKEYC001.", error.ToString().Trim());
        }
        finally
        {
            cancellation.Cancel();
            await anchor.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [LinuxArm64Fact]
    public async Task FencedAnchorDoesNotPublishReferenceWhenOwnerDisconnectsBeforeFirstHeartbeat()
    {
        using var files = new TestFiles();
        var input = files.Write("input.json", AnchorInput(RunId));
        var ready = files.FilePath("anchor-reference.json");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var error = new StringWriter();
        using var owner = new TcpClient();
        var port = ReserveLoopbackPort();
        var anchor = AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
            ["--appsurface-keycloak-fenced-memory-anchor", input, ready,
                port.ToString(System.Globalization.CultureInfo.InvariantCulture), RunId],
            _ => null, TextWriter.Null, error, cancellation.Token);
        try
        {
            while (!File.Exists(ready + ".listening"))
            {
                Assert.False(anchor.IsCompleted);
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellation.Token);
            }

            await owner.ConnectAsync(IPAddress.Loopback, port, cancellation.Token);
            var stream = owner.GetStream();
            owner.Client.Shutdown(SocketShutdown.Send);

            Assert.Equal(0, await stream.ReadAsync(new byte[1], cancellation.Token));
            Assert.Equal(1, await anchor.WaitAsync(cancellation.Token));
            Assert.False(File.Exists(ready));
            Assert.Equal("AppSurface Keycloak realm-ready gate failed. Code: ASKEYC001.", error.ToString().Trim());
        }
        finally
        {
            cancellation.Cancel();
            await anchor.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [LinuxArm64Fact]
    public void PinnedDatabaseOwnershipReturnsNullWhenTheDatabasePathIsIncomplete()
    {
        using var volume = new TempDirectory();
        Assert.Null(KeycloakVolumeIncarnation.ReadPinnedDatabaseOwnership(volume.Path));

        var h2 = TestPathUtils.PathUnder(volume.Path, "h2");
        Directory.CreateDirectory(h2);
        Assert.Null(KeycloakVolumeIncarnation.ReadPinnedDatabaseOwnership(volume.Path));
    }

    [LinuxArm64Fact]
    public void PinnedDatabaseOwnershipRequiresAnAbsolutePathWithNoTraversal()
    {
        var relative = Assert.Throws<ArgumentException>(() => KeycloakVolumeIncarnation.ReadPinnedDatabaseOwnership("relative"));
        Assert.Equal("volumeRoot", relative.ParamName);
        Assert.Throws<InvalidDataException>(() => KeycloakVolumeIncarnation.ReadPinnedDatabaseOwnership("/tmp/../tmp"));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [LinuxArm64Fact]
    public void PinnedDatabaseOwnershipReturnsTheRegularDatabaseFilesOwner()
    {
        using var volume = new TempDirectory();
        var h2 = TestPathUtils.PathUnder(volume.Path, "h2");
        Directory.CreateDirectory(h2);
        File.WriteAllText(TestPathUtils.PathUnder(h2, "keycloakdb.mv.db"), "synthetic database file");

        var ownership = KeycloakVolumeIncarnation.ReadPinnedDatabaseOwnership(volume.Path);

        Assert.Equal<uint?>(GetUid(), ownership?.Uid);
        Assert.Equal<uint?>(GetGid(), ownership?.Gid);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [Theory]
    [LinuxArm64InlineData("root-link")]
    [LinuxArm64InlineData("h2-link")]
    [LinuxArm64InlineData("database-link")]
    [LinuxArm64InlineData("database-directory")]
    public void PinnedDatabaseOwnershipRejectsLinksAndNonFileDatabase(string scenario)
    {
        using var temporary = new TempDirectory();
        var volume = TestPathUtils.PathUnder(temporary.Path, "volume");
        var external = TestPathUtils.PathUnder(temporary.Path, "external");
        var externalH2 = TestPathUtils.PathUnder(external, "h2");
        Directory.CreateDirectory(externalH2);
        File.WriteAllText(TestPathUtils.PathUnder(externalH2, "keycloakdb.mv.db"), "outside database");

        string selectedRoot;
        if (scenario == "root-link")
        {
            Directory.CreateSymbolicLink(volume, external);
            selectedRoot = volume;
        }
        else
        {
            Directory.CreateDirectory(volume);
            var h2 = TestPathUtils.PathUnder(volume, "h2");
            if (scenario == "h2-link")
                Directory.CreateSymbolicLink(h2, externalH2);
            else
            {
                Directory.CreateDirectory(h2);
                var database = TestPathUtils.PathUnder(h2, "keycloakdb.mv.db");
                if (scenario == "database-link")
                    File.CreateSymbolicLink(database, TestPathUtils.PathUnder(externalH2, "keycloakdb.mv.db"));
                else
                    Directory.CreateDirectory(database);
            }
            selectedRoot = volume;
        }

        Assert.Throws<InvalidDataException>(() => KeycloakVolumeIncarnation.ReadPinnedDatabaseOwnership(selectedRoot));
    }

    [Theory]
    [LinuxArm64InlineData(false)]
    [LinuxArm64InlineData(true)]
    public async Task VolumeDispatchReturnsTheSameIncarnationAndOptionalPinnedDatabaseOwner(bool databaseExists)
    {
        using var volume = new TempDirectory();
        if (databaseExists)
        {
            var h2 = TestPathUtils.PathUnder(volume.Path, "h2");
            Directory.CreateDirectory(h2);
            File.WriteAllText(TestPathUtils.PathUnder(h2, "keycloakdb.mv.db"), "synthetic database");
        }

        var identity = await RunVolumeCommandAsync(volume.Path, "--appsurface-keycloak-volume-incarnation");
        var ownership = await RunVolumeCommandAsync(volume.Path, "--appsurface-keycloak-volume-ownership");

        Assert.Equal(0, identity.Exit);
        Assert.Empty(identity.Error);
        Assert.Equal(0, ownership.Exit);
        Assert.Empty(ownership.Error);
        var incarnation = Assert.IsType<KeycloakVolumeIncarnation>(JsonSerializer.Deserialize<KeycloakVolumeIncarnation>(identity.Output));
        var owner = Assert.IsType<KeycloakWorkerVolumeOwnership>(JsonSerializer.Deserialize<KeycloakWorkerVolumeOwnership>(ownership.Output));
        Assert.True(incarnation.Inode > 0);
        Assert.Equal(incarnation, owner.Incarnation);
        Assert.Equal(GetUid(), owner.Uid);
        Assert.Equal(GetGid(), owner.Gid);
        Assert.Equal(databaseExists ? new KeycloakWorkerDatabaseOwnership(GetUid(), GetGid()) : null, owner.Database);
    }

    [Theory]
    [LinuxArm64InlineData("--appsurface-keycloak-volume-incarnation", "AppSurface Keycloak volume identity unavailable.")]
    [LinuxArm64InlineData("--appsurface-keycloak-volume-ownership", "AppSurface Keycloak volume ownership unavailable.")]
    public async Task VolumeDispatchFailsClosedForAnUnavailableRoot(string command, string diagnostic)
    {
        using var temporary = new TempDirectory();
        var missing = TestPathUtils.PathUnder(temporary.Path, "missing");
        var result = await RunVolumeCommandAsync(missing, command);

        Assert.Equal(1, result.Exit);
        Assert.Empty(result.Output);
        Assert.Equal(diagnostic, result.Error);
        Assert.False(Directory.Exists(missing));
    }

    [Theory]
    [LinuxArm64InlineData("not-a-number", false)]
    [LinuxArm64InlineData("0", false)]
    [LinuxArm64InlineData("1000", true)]
    public async Task VolumeInitializationPreservesVolumeWithInvalidOwnerOrExistingData(string uid, bool existingData)
    {
        using var volume = new TempDirectory();
        var marker = TestPathUtils.PathUnder(volume.Path, "existing");
        if (existingData) File.WriteAllText(marker, "preserve");
        var permissions = File.GetUnixFileMode(volume.Path);
        var owner = KeycloakVolumeIncarnation.ReadOwnership(volume.Path);

        var result = await RunVolumeCommandAsync(volume.Path, "--appsurface-keycloak-initialize-volume", uid);

        Assert.Equal(1, result.Exit);
        Assert.Empty(result.Output);
        Assert.Empty(result.Error);
        Assert.Equal(owner, KeycloakVolumeIncarnation.ReadOwnership(volume.Path));
        Assert.Equal(permissions, File.GetUnixFileMode(volume.Path));
        if (existingData) Assert.Equal("preserve", File.ReadAllText(marker));
        else Assert.Empty(Directory.EnumerateFileSystemEntries(volume.Path));
    }

    [LinuxArm64Fact]
    public async Task VolumeInitializationReportsAnUnavailableRootWithoutCreatingIt()
    {
        using var temporary = new TempDirectory();
        var missing = TestPathUtils.PathUnder(temporary.Path, "missing");
        var result = await RunVolumeCommandAsync(missing, "--appsurface-keycloak-initialize-volume", "1000");

        Assert.Equal(1, result.Exit);
        Assert.Empty(result.Output);
        Assert.Equal("AppSurface Keycloak new-volume initialization failed.", result.Error);
        Assert.False(Directory.Exists(missing));
    }

    [LinuxArm64Fact]
    public async Task EmptyVolumeInitializationEitherSetsQualifiedOwnershipOrLeavesItIntact()
    {
        using var volume = new TempDirectory();
        var prior = KeycloakVolumeIncarnation.ReadOwnership(volume.Path);
        var initialMode = File.GetUnixFileMode(volume.Path);
        const uint targetUid = 1000;
        var result = await RunVolumeCommandAsync(volume.Path, "--appsurface-keycloak-initialize-volume", "1000");
        var after = KeycloakVolumeIncarnation.ReadOwnership(volume.Path);

        Assert.Empty(result.Output);
        Assert.Empty(result.Error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(volume.Path));
        Assert.Equal(prior.Incarnation, after.Incarnation);
        if (GetUid() == 0 || result.Exit == 0)
        {
            Assert.Equal(0, result.Exit);
            Assert.Equal(targetUid, after.Uid);
            Assert.Equal(0u, after.Gid);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute,
                File.GetUnixFileMode(volume.Path));
        }
        else
        {
            Assert.Equal(1, result.Exit);
            Assert.Equal(prior, after);
            Assert.Equal(initialMode, File.GetUnixFileMode(volume.Path));
        }
    }

    private static async Task<(int Exit, string Output, string Error)> RunVolumeCommandAsync(string root, params string[] arguments)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await AppSurfaceKeycloakRealmReadyEntryPoint.RunAsync(
            arguments, _ => null, output, error, CancellationToken.None, volumeRoot: root);
        return (exit, output.ToString(), error.ToString().TrimEnd());
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getgid")]
    private static extern uint GetGid();

    private static string AnchorInput(string runId) => JsonSerializer.Serialize(new { RunId = runId, Authority, ClientId, ClientSecret = Secret });

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static async Task WaitForReferenceAsync(string path, Task<int> anchor, CancellationToken cancellationToken)
    {
        while (!File.Exists(path) || File.GetUnixFileMode(path) != UnixFileMode.UserRead)
        {
            Assert.False(anchor.IsCompleted, "The anchor exited before publishing its sealed reference.");
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
    }

    private const string DesiredJson = """
        {"realm":"demo","clientId":"web-client","authority":"https://localhost:8443/realms/demo","confidential":false,"allowPublicDowngrade":false,"redirectUris":["http://localhost:5001/callback"],"postLogoutRedirectUris":["http://localhost:5001/signed-out"],"webOrigins":["http://localhost:5001"],"mappers":[],"brokers":[],"removeMappers":[],"removeBrokers":[]}
        """;

    private const string BindingsJson = """
        {"baseAddress":"https://localhost:8443/","adminUsername":"admin","adminPassword":"synthetic-admin-password","clientSecret":null,"captureRoot":null,"captureRunId":null,"brokerSecrets":null}
        """;

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private sealed class TestFiles : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("keycloak-linux-arm64-worker-").FullName;

        internal string FilePath(string name) => TestPathUtils.PathUnder(_root, name);

        internal string Write(string name, string contents, UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite)
        {
            var path = FilePath(name);
            File.WriteAllText(path, contents);
            File.SetUnixFileMode(path, mode);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
