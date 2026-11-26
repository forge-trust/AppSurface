using ForgeTrust.AppSurface.Testing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

public sealed class RealmClientCaptureTests
{
    [Theory]
    [InlineData("invalid-run", null, null, "Invalid capture run.")]
    [InlineData(null, "https://identity.example/realms/demo", null, "Capture identity is incomplete.")]
    [InlineData(null, null, "client", "Capture identity is incomplete.")]
    public void InvalidIdentityDoesNotCreateCustody(string? runId, string? authority, string? clientId, string diagnostic)
    {
        var missingRoot = TestPathUtils.PathUnder(System.IO.Path.GetTempPath(), "missing-capture-" + Guid.NewGuid().ToString("N"));
        var error = Assert.Throws<InvalidDataException>(() => RealmClientCapture.Capture(
            missingRoot, runId ?? Guid.NewGuid().ToString("N"), "fixture-secret", authority, clientId));

        Assert.Equal(diagnostic, error.Message);
        Assert.False(Directory.Exists(missingRoot));
    }

    [Fact]
    public void QualifiedReadNeverFollowsARootLinkAndRejectsMalformedRunIdentifiers()
    {
        if (!Supported()) return;
        using var folder = new PrivateCapture();
        using var target = new PrivateCapture();
        var run = Guid.NewGuid().ToString("N");
        RealmClientCapture.Capture(target.Path, run, "fixture-secret");
        var link = TestPathUtils.PathUnder(folder.Path, "linked-root");
        Directory.CreateSymbolicLink(link, target.Path);

        Assert.Throws<IOException>(() => RealmClientCapture.Capture(link, run, "never-written"));
        Assert.Throws<IOException>(() => RealmClientCapture.Read(link, run));
        Assert.Throws<InvalidDataException>(() => RealmClientCapture.Read(target.Path, "malformed-run"));
        Assert.Equal("fixture-secret", RealmClientCapture.Read(target.Path, run));
    }

    [Fact]
    public void SealedCaptureReadsSameValueRejectsWrongRunAndCannotBeOverwritten()
    {
        if (!Supported()) return;
        using var folder = new PrivateCapture();
        var run = Guid.NewGuid().ToString("N");
        var value = new string('A', 64);
        RealmClientCapture.Capture(folder.Path, run, value);
        Assert.Equal(value, RealmClientCapture.Read(folder.Path, run));
        Assert.Throws<InvalidDataException>(() => RealmClientCapture.Read(folder.Path, Guid.NewGuid().ToString("N")));
        Assert.ThrowsAny<IOException>(() => RealmClientCapture.Capture(folder.Path, run, new string('B', 64)));
        Assert.ThrowsAny<Exception>(() => File.WriteAllText(TestPathUtils.PathUnder(folder.Path, "client-custody", "client.secret"), new string('B', 64)));
        Assert.Equal(value, RealmClientCapture.Read(folder.Path, run));
    }

    [Fact]
    public void CapturesOpaqueUtf8AndRecordsOnlyLengthAndClientIdentity()
    {
        if (!Supported()) return;
        using var folder = new PrivateCapture();
        var run = Guid.NewGuid().ToString("N");
        const string value = "consumer-secret-π-🔐";
        const string authority = "https://identity.example/realms/sample";
        const string clientId = "web-client";

        RealmClientCapture.Capture(folder.Path, run, value, authority, clientId);

        Assert.Equal(value, RealmClientCapture.Read(folder.Path, run));
        Assert.Equal(value, new KeycloakClientCredentialReference(folder.Path, run, new Uri(authority), clientId).Read());
        var metadataJson = File.ReadAllText(TestPathUtils.PathUnder(folder.Path, "client-custody", "identity.json"));
        var metadata = JsonNode.Parse(metadataJson)!;
        Assert.Equal(Encoding.UTF8.GetByteCount(value), metadata["SecretByteLength"]!.GetValue<int>());
        Assert.Equal(authority, metadata["Authority"]!.GetValue<string>());
        Assert.Equal(clientId, metadata["ClientId"]!.GetValue<string>());
        Assert.DoesNotContain(value, metadataJson, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretHash", metadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public void CapturesOneByteAndMaximumSizeOpaqueCredentials()
    {
        if (!Supported()) return;
        using (var shortest = new PrivateCapture())
        {
            var run = Guid.NewGuid().ToString("N");
            RealmClientCapture.Capture(shortest.Path, run, "x");
            Assert.Equal("x", RealmClientCapture.Read(shortest.Path, run));
        }

        using (var maximum = new PrivateCapture())
        {
            var run = Guid.NewGuid().ToString("N");
            var value = new string('é', 2048);
            Assert.Equal(4096, Encoding.UTF8.GetByteCount(value));
            RealmClientCapture.Capture(maximum.Path, run, value);
            Assert.Equal(value, RealmClientCapture.Read(maximum.Path, run));
        }
    }

    [Fact]
    public void RejectsEmptyWhitespaceOversizedControlAndInvalidSurrogateCredentialsBeforePlatformIo()
    {
        var root = TestPathUtils.PathUnder(System.IO.Path.GetTempPath(), "issue888-invalid-capture-" + Guid.NewGuid().ToString("N"));
        string?[] invalidValues = [null, "", " \t", new string('x', 4097), "credential\0tail", "credential\ntail", "\ud800"];

        foreach (var value in invalidValues)
        {
            var error = Assert.Throws<InvalidDataException>(() => RealmClientCapture.Capture(root, Guid.NewGuid().ToString("N"), value!));
            if (!string.IsNullOrEmpty(value))
            {
                Assert.DoesNotContain(value, error.Message, StringComparison.Ordinal);
            }
        }

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task BoundReadRejectsWrongAuthorityAndClientWhileLegacyReadStillWorks()
    {
        if (!Supported()) return;
        using var folder = new PrivateCapture();
        var run = Guid.NewGuid().ToString("N");
        const string value = "opaque-client-value";
        const string authority = "https://identity.example/realms/sample";
        const string clientId = "web-client";
        RealmClientCapture.Capture(folder.Path, run, value, authority, clientId);

        Assert.Equal(value, RealmClientCapture.Read(folder.Path, run));
        var reference = new KeycloakClientCredentialReference(folder.Path, run, new Uri(authority), clientId);
        Assert.Equal(value, reference.Read());
        Assert.Equal(value, await reference.ReadAsync());
        Assert.Throws<InvalidDataException>(() => new KeycloakClientCredentialReference(
            folder.Path, run, new Uri("https://identity.example/realms/other"), clientId).Read());
        Assert.Throws<InvalidDataException>(() => new KeycloakClientCredentialReference(
            folder.Path, run, new Uri(authority), "another-client").Read());
    }

    [Theory]
    [InlineData("missing-length")]
    [InlineData("invalid-json")]
    [InlineData("null-json")]
    [InlineData("oversized-json")]
    public void MalformedLegacyCaptureDescriptorIsRejected(string mutation)
    {
        if (!Supported()) return;
        using var folder = new PrivateCapture();
        var run = Guid.NewGuid().ToString("N");
        RealmClientCapture.Capture(folder.Path, run, "legacy-secret");
        var descriptor = TestPathUtils.PathUnder(folder.Path, "client-custody", "identity.json");
        File.SetUnixFileMode(descriptor, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        if (mutation == "missing-length")
        {
            var metadata = JsonNode.Parse(File.ReadAllText(descriptor))!;
            metadata["SecretByteLength"] = 0;
            File.WriteAllText(descriptor, metadata.ToJsonString());
            File.SetUnixFileMode(descriptor, UnixFileMode.UserRead);
            Assert.Throws<InvalidDataException>(() => RealmClientCapture.Read(folder.Path, run));
        }
        else if (mutation == "invalid-json")
        {
            File.WriteAllText(descriptor, "{\"RunId\":");
            File.SetUnixFileMode(descriptor, UnixFileMode.UserRead);
            Assert.Throws<JsonException>(() => RealmClientCapture.Read(folder.Path, run));
        }
        else
        {
            File.WriteAllText(descriptor, mutation == "null-json" ? "null" : new string(' ', 16_385));
            File.SetUnixFileMode(descriptor, UnixFileMode.UserRead);
            Assert.Throws<InvalidDataException>(() => RealmClientCapture.Read(folder.Path, run));
        }
    }

    [Fact]
    public void WritableDescriptorIsRejectedEvenWhenItsContentsMatch()
    {
        if (!Supported()) return;
        using var folder = new PrivateCapture();
        var run = Guid.NewGuid().ToString("N");
        const string value = "writable-descriptor-fixture-secret";
        RealmClientCapture.Capture(folder.Path, run, value);
        var descriptor = TestPathUtils.PathUnder(folder.Path, "client-custody", "identity.json");
        File.SetUnixFileMode(descriptor, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var error = Assert.Throws<InvalidDataException>(() => RealmClientCapture.Read(folder.Path, run));

        Assert.Equal("Capture descriptor rejected.", error.Message);
        Assert.DoesNotContain(value, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DescriptorWithCharacterCountInsteadOfUtf8ByteLengthIsRejected()
    {
        if (!Supported()) return;
        using var folder = new PrivateCapture();
        var run = Guid.NewGuid().ToString("N");
        const string value = "capture-fixture-é";
        RealmClientCapture.Capture(folder.Path, run, value);
        var descriptor = TestPathUtils.PathUnder(folder.Path, "client-custody", "identity.json");
        var metadata = JsonNode.Parse(File.ReadAllText(descriptor))!;
        metadata["SecretByteLength"] = value.Length;
        File.SetUnixFileMode(descriptor, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(descriptor, metadata.ToJsonString());
        File.SetUnixFileMode(descriptor, UnixFileMode.UserRead);

        var error = Assert.Throws<InvalidDataException>(() => RealmClientCapture.Read(folder.Path, run));

        Assert.Equal("Capture incarnation changed.", error.Message);
        Assert.DoesNotContain(value, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DescriptorFromAnotherSealedCaptureWithSameRunAndLengthIsRejected()
    {
        if (!Supported()) return;
        using var folder = new PrivateCapture();
        using var peer = new PrivateCapture();
        var run = Guid.NewGuid().ToString("N");
        const string value = "left-capture-fixture-secret";
        const string peerValue = "peer-capture-fixture-secret";
        const string authority = "https://identity.example/realms/sample";
        const string clientId = "web-client";
        RealmClientCapture.Capture(folder.Path, run, value, authority, clientId);
        RealmClientCapture.Capture(peer.Path, run, peerValue, authority, clientId);
        var descriptor = TestPathUtils.PathUnder(folder.Path, "client-custody", "identity.json");
        var peerDescriptor = TestPathUtils.PathUnder(peer.Path, "client-custody", "identity.json");
        File.SetUnixFileMode(descriptor, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(descriptor, File.ReadAllText(peerDescriptor));
        File.SetUnixFileMode(descriptor, UnixFileMode.UserRead);

        var error = Assert.Throws<InvalidDataException>(() => RealmClientCapture.Read(folder.Path, run, authority, clientId));

        Assert.Equal("Capture incarnation changed.", error.Message);
        Assert.DoesNotContain(value, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(peerValue, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CustodyDirectoryWithGroupAccessIsRejectedBeforeReadingSecret()
    {
        if (!Supported()) return;
        using var folder = new PrivateCapture();
        var run = Guid.NewGuid().ToString("N");
        const string value = "custody-directory-fixture-secret";
        RealmClientCapture.Capture(folder.Path, run, value);
        var custody = TestPathUtils.PathUnder(folder.Path, "client-custody");
        const UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(custody, privateMode | UnixFileMode.GroupExecute);

        try
        {
            var error = Assert.Throws<InvalidDataException>(() => RealmClientCapture.Read(folder.Path, run));

            Assert.Equal("Private directory rejected.", error.Message);
            Assert.DoesNotContain(value, error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(custody, privateMode);
        }
    }

    [Fact]
    public void ReplacedCaptureFileIsRejected()
    {
        if (!Supported()) return;
        using var folder = new PrivateCapture();
        var run = Guid.NewGuid().ToString("N");
        var value = new string('A', 64);
        RealmClientCapture.Capture(folder.Path, run, value);
        RealmClientCapture.UnsealForOwnedCleanup(folder.Path);

        var capture = TestPathUtils.PathUnder(folder.Path, "client-custody", "client.secret");
        File.Delete(capture);
        File.WriteAllText(capture, new string('B', 64));
        File.SetUnixFileMode(capture, UnixFileMode.UserRead);

        Assert.Throws<InvalidDataException>(() => RealmClientCapture.Read(folder.Path, run));
    }

    [Fact]
    public void GroupReadableCaptureFileIsRejected()
    {
        if (!Supported()) return;
        using var folder = new PrivateCapture();
        var run = Guid.NewGuid().ToString("N");
        RealmClientCapture.Capture(folder.Path, run, "mode-fixture-secret");
        RealmClientCapture.UnsealForOwnedCleanup(folder.Path);
        var capture = TestPathUtils.PathUnder(folder.Path, "client-custody", "client.secret");
        File.SetUnixFileMode(capture, UnixFileMode.UserRead | UnixFileMode.GroupRead);

        var error = Assert.Throws<InvalidDataException>(() => RealmClientCapture.Read(folder.Path, run));

        Assert.Equal("Capture file rejected.", error.Message);
    }

    [Theory]
    [InlineData(0xff, "Capture credential is not valid UTF-8.")]
    [InlineData(0x0a, "Capture credential is invalid.")]
    [InlineData(0x20, "Capture credential is invalid.")]
    public void ReadRejectsCorruptedSealedCaptureEvenWithUnchangedFileIdentity(int replacement, string diagnostic)
    {
        if (!Supported()) return;
        using var folder = new PrivateCapture();
        var run = Guid.NewGuid().ToString("N");
        RealmClientCapture.Capture(folder.Path, run, "A");
        var secret = TestPathUtils.PathUnder(folder.Path, "client-custody", "client.secret");

        // Model corruption by the credential owner while retaining the recorded inode, birth time, and length.
        RealmClientCapture.UnsealForOwnedCleanup(folder.Path);
        File.SetUnixFileMode(secret, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using (var file = File.OpenWrite(secret))
        {
            file.WriteByte((byte)replacement);
            file.Flush(flushToDisk: true);
        }
        File.SetUnixFileMode(secret, UnixFileMode.UserRead);
        Assert.Equal(0, SetFileFlags(secret, 2));

        var error = Assert.Throws<InvalidDataException>(() => RealmClientCapture.Read(folder.Path, run));

        Assert.Equal(diagnostic, error.Message);
    }

    [Fact]
    public void OwnedCleanupDoesNotUnsealTheTargetOfACaptureLink()
    {
        if (!Supported()) return;
        using var folder = new PrivateCapture();
        using var target = new PrivateCapture();
        var run = Guid.NewGuid().ToString("N");
        RealmClientCapture.Capture(target.Path, run, "original-capture");
        var custody = Directory.CreateDirectory(TestPathUtils.PathUnder(folder.Path, "client-custody"));
        var capture = TestPathUtils.PathUnder(custody.FullName, "client.secret");
        var original = TestPathUtils.PathUnder(target.Path, "client-custody", "client.secret");
        File.CreateSymbolicLink(capture, original);

        RealmClientCapture.UnsealForOwnedCleanup(folder.Path);

        Assert.Equal(original, new FileInfo(capture).LinkTarget);
        Assert.Equal("original-capture", RealmClientCapture.Read(target.Path, run));
    }

    [Fact]
    public void ExpiredDescriptorAndSymlinkedCaptureAreRejected()
    {
        if (!Supported()) return;
        using var folder = new PrivateCapture();
        var run = Guid.NewGuid().ToString("N");
        RealmClientCapture.Capture(folder.Path, run, new string('A', 64));
        var descriptor = TestPathUtils.PathUnder(folder.Path, "client-custody", "identity.json");
        File.SetUnixFileMode(descriptor, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var node = JsonNode.Parse(File.ReadAllText(descriptor))!;
        node["ExpiresUnixSeconds"] = 0;
        File.WriteAllText(descriptor, node.ToJsonString());
        File.SetUnixFileMode(descriptor, UnixFileMode.UserRead);
        Assert.Throws<InvalidDataException>(() => RealmClientCapture.Read(folder.Path, run));
        RealmClientCapture.UnsealForOwnedCleanup(folder.Path);
        var capture = TestPathUtils.PathUnder(folder.Path, "client-custody", "client.secret");
        File.SetUnixFileMode(descriptor, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        node["ExpiresUnixSeconds"] = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds();
        File.WriteAllText(descriptor, node.ToJsonString());
        File.SetUnixFileMode(descriptor, UnixFileMode.UserRead);
        File.Move(capture, capture + ".original");
        File.CreateSymbolicLink(capture, capture + ".original");
        Assert.ThrowsAny<Exception>(() => RealmClientCapture.Read(folder.Path, run));
    }

    [System.Runtime.Versioning.SupportedOSPlatformGuard("macos")]
    private static bool Supported() => OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    [DllImport("libSystem.B.dylib", EntryPoint = "chflags")]
    private static extern int SetFileFlags(string path, uint flags);

    private sealed class PrivateCapture : IDisposable
    {
        internal string Path { get; } = TestPathUtils.PathUnder("/private/tmp", "issue888-capture-" + Guid.NewGuid().ToString("N"));
        internal PrivateCapture()
        {
            if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
            Directory.CreateDirectory(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        public void Dispose() { RealmClientCapture.UnsealForOwnedCleanup(Path); Directory.Delete(Path, true); }
    }
}
