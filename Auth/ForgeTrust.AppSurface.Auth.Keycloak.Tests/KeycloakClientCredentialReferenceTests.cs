using ForgeTrust.AppSurface.Testing;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

public sealed class KeycloakClientCredentialReferenceTests
{
    private static readonly Uri Authority = new("https://identity.example/realms/sample");
    private static readonly string RunId = Guid.NewGuid().ToString("N");

    // protects: memory descriptors round-trip without reads or secret state.
    // fails_when: serialization loses native identity or construction resolves a nonexistent holder.
    // no_claim: construction is not native Linux or process-namespace acceptance.
    [Fact]
    public void SealedMemoryReferenceConstructionIsEffectFreeAndRoundTripsOnlyMetadata()
    {
        var identity = new KeycloakClientCredentialMemoryIdentity(1, 100, 3, 42);
        var reference = KeycloakClientCredentialReference.ForSealedMemory(identity, RunId, Authority, "web-client");

        var json = JsonSerializer.Serialize(reference);
        var copy = JsonSerializer.Deserialize<KeycloakClientCredentialReference>(json)!;

        Assert.Equal(string.Empty, copy.RunDirectory);
        Assert.Equal(identity, copy.MemoryIdentity);
        Assert.Equal(reference.Authority, copy.Authority);
        Assert.Equal(RunId, copy.RunId);
        Assert.Equal("web-client", copy.ClientId);
    }

    // protects: bad process/file declarations fail without native access.
    // fails_when: nonpositive identities or standard-stream/unbounded descriptor numbers are accepted.
    // no_claim: these in-memory declarations do not establish holder liveness or namespace containment.
    [Theory]
    [InlineData(0, 100, 3, 42UL)]
    [InlineData(-1, 100, 3, 42UL)]
    [InlineData(1, 0, 3, 42UL)]
    [InlineData(1, -1, 3, 42UL)]
    [InlineData(1, 100, 2, 42UL)]
    [InlineData(1, 100, 1_048_576, 42UL)]
    [InlineData(1, 100, 3, 0UL)]
    public void InvalidMemoryIdentityFailsBeforeNativeRead(int pid, long start, int fd, ulong inode)
    {
        var error = Assert.Throws<ArgumentException>(() => KeycloakClientCredentialReference.ForSealedMemory(
            new(pid, start, fd, inode), RunId, Authority, "web-client"));
        Assert.Equal("memoryIdentity", error.ParamName);
    }

    // protects: a descriptor always selects exactly one custody transport.
    // fails_when: JSON-style reconstruction permits both a directory and sealed-memory identity.
    // no_claim: rejection does not test filesystem ownership or native seals.
    [Fact]
    public void MixedCustodyDeclarationIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new KeycloakClientCredentialReference(
            Path.GetTempPath(), RunId, Authority, "web-client", new(1, 100, 3, 42)));
        Assert.Throws<ArgumentNullException>(() => KeycloakClientCredentialReference.ForSealedMemory(
            null!, RunId, Authority, "web-client"));
    }

    [Fact]
    public void ConstructorValidatesAndJsonRoundTripsOnlyDescriptorMetadata()
    {
        var runDirectory = TestPathUtils.PathUnder(System.IO.Path.GetTempPath(), "issue888-reference-" + Guid.NewGuid().ToString("N"));
        var reference = new KeycloakClientCredentialReference(runDirectory, RunId, Authority, "web-client");

        Assert.False(Directory.Exists(runDirectory));
        Assert.Equal(runDirectory, reference.RunDirectory);
        Assert.Equal(RunId, reference.RunId);
        Assert.Equal(Authority, reference.Authority);
        Assert.Equal("web-client", reference.ClientId);

        const string credentialMarker = "credential-must-never-be-on-reference";
        var serialized = JsonSerializer.Serialize(reference);
        var roundTripped = JsonSerializer.Deserialize<KeycloakClientCredentialReference>(serialized)!;
        Assert.Equal(reference.RunDirectory, roundTripped.RunDirectory);
        Assert.Equal(reference.RunId, roundTripped.RunId);
        Assert.Equal(reference.Authority, roundTripped.Authority);
        Assert.Equal(reference.ClientId, roundTripped.ClientId);
        Assert.DoesNotContain(credentialMarker, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(credentialMarker, reference.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsRelativeRunDirectory()
    {
        Assert.Throws<ArgumentException>(() => new KeycloakClientCredentialReference(
            "relative-run-directory", RunId, Authority, "web-client"));
    }

    [Fact]
    public void RejectsNonNRunIdentifier()
    {
        Assert.Throws<ArgumentException>(() => new KeycloakClientCredentialReference(
            System.IO.Path.GetTempPath(), "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", Authority, "web-client"));
    }

    [Fact]
    public void SharedValidatorsRejectMissingAndUnparseableMetadata()
    {
        var missingAuthority = Assert.Throws<ArgumentException>(
            () => KeycloakClientCredentialReference.NormalizeAuthority(null!));
        Assert.Equal("authority", missingAuthority.ParamName);
        Assert.Throws<ArgumentException>(() => KeycloakClientCredentialReference.NormalizeAuthority("not an absolute URI"));

        var missingClientId = Assert.Throws<ArgumentNullException>(
            () => KeycloakClientCredentialReference.ValidateClientId(null!));
        Assert.Equal("clientId", missingClientId.ParamName);
    }

    [Theory]
    [InlineData("http://identity.example/realms/sample")]
    [InlineData("https://identity.example/")]
    [InlineData("https://identity.example/realms/sample?query=1")]
    [InlineData("https://identity.example/realms/sample#fragment")]
    [InlineData("https://user@identity.example/realms/sample")]
    [InlineData("https://identity.example/realms/sample%2Fother")]
    [InlineData("https://identity.example/realms/sample%5Cother")]
    [InlineData("https://identity.example/realms/%20")]
    [InlineData("https://identity.example/not-a-realm/sample")]
    public void RejectsNonHttpsOrNonRealmAuthority(string authority)
    {
        Assert.Throws<ArgumentException>(() => new KeycloakClientCredentialReference(
            System.IO.Path.GetTempPath(), RunId, new Uri(authority), "web-client"));
    }

    [Fact]
    public void RejectsRelativeRealmAuthority()
    {
        var authority = new Uri("/realms/sample", UriKind.Relative);

        Assert.Throws<ArgumentException>(() => new KeycloakClientCredentialReference(
            System.IO.Path.GetTempPath(), RunId, authority, "web-client"));
    }

    [Fact]
    public void AuthorityUsesTheInclusiveUtf8ByteBound()
    {
        const string authorityPrefix = "https://identity.example/";
        const string realmSuffix = "/realms/sample";
        var paddingLength = 2048 - authorityPrefix.Length - realmSuffix.Length;
        var atLimit = new Uri(authorityPrefix + new string('a', paddingLength) + realmSuffix);
        var accepted = new KeycloakClientCredentialReference(
            System.IO.Path.GetTempPath(), RunId, atLimit, "web-client");

        Assert.Equal(2048, accepted.Authority.AbsoluteUri.Length);

        var tooLong = new Uri(authorityPrefix + new string('a', paddingLength + 1) + realmSuffix);

        var error = Assert.Throws<ArgumentException>(() => new KeycloakClientCredentialReference(
            System.IO.Path.GetTempPath(), RunId, tooLong, "web-client"));

        Assert.Contains("2048-byte limit", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData("web\nclient")]
    public void RejectsEmptyWhitespaceAndControlClientIdentifiers(string clientId)
    {
        Assert.Throws<ArgumentException>(() => new KeycloakClientCredentialReference(
            System.IO.Path.GetTempPath(), RunId, Authority, clientId));
    }

    [Fact]
    public void RejectsInvalidUtf8ClientIdentifier()
    {
        const string invalidUtf8ClientId = "\ud800";

        Assert.Throws<ArgumentException>(() => new KeycloakClientCredentialReference(
            System.IO.Path.GetTempPath(), RunId, Authority, invalidUtf8ClientId));
    }

    [Fact]
    public void ClientIdentifierUsesInclusiveUtf8ByteBound()
    {
        var accepted = new string('x', 256);
        var rejected = new string('x', 257);
        Assert.Equal(accepted, new KeycloakClientCredentialReference(
            System.IO.Path.GetTempPath(), RunId, Authority, accepted).ClientId);
        Assert.Throws<ArgumentException>(() => new KeycloakClientCredentialReference(
            System.IO.Path.GetTempPath(), RunId, Authority, rejected));
    }

    [Fact]
    public async Task ReadAsyncHonorsCancellationBeforeQueuingNativeRead()
    {
        var reference = new KeycloakClientCredentialReference(
            System.IO.Path.GetTempPath(), RunId, Authority, "web-client");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reference.ReadAsync(cancellation.Token));
    }

    [Fact]
    public void UnsupportedPlatformErrorNamesMacOsArm64Requirement()
    {
        if (Supported()) return;
        var reference = new KeycloakClientCredentialReference(
            System.IO.Path.GetTempPath(), RunId, Authority, "web-client");

        var error = Assert.Throws<PlatformNotSupportedException>(() => reference.Read());
        Assert.Contains("macOS ARM64", error.Message, StringComparison.Ordinal);
    }

    [System.Runtime.Versioning.SupportedOSPlatformGuard("macos")]
    private static bool Supported() => OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
}
