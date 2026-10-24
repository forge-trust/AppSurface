using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Security;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

// Test metadata: protects SignedRepository's bounded signed-index trust boundary.
// These tests use ephemeral keys and in-memory bytes; they do not represent a repository approval.
public sealed class SignedRepositoryTests
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ValidHistoricalReleaseAndPackageIndexPass()
    {
        var fixture = SignedRepositoryFixture.Create();
        var result = Verify(fixture, fixture.ReleaseSha256, fixture.Fingerprint, fixture.Policy(Clock, historical: true));
        Assert.Equal(fixture.Fingerprint, result.SignerFingerprint);
        Assert.Single(result.Packages);
        Assert.Single(result.DependencyFacts);
    }

    [Fact]
    public void ReleaseAndKeyringHashesAreRequired()
    {
        var fixture = SignedRepositoryFixture.Create();
        AssertInvalidMessage("release digest mismatch", () => Verify(fixture, new string('0', 64), fixture.Fingerprint, fixture.Policy(Clock, true)));
        AssertInvalidMessage("keyring digest mismatch", () => SignedRepository.VerifyRelease(
            fixture.Release(), new MemoryStream(Encoding.UTF8.GetBytes("wrong")), fixture.ReleaseSha256, fixture.KeyringSha256,
            fixture.Fingerprint, fixture.Policy(Clock, true), fixture.Requested(), fixture.Indexes()));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("nonhex")]
    public void ExpectedReleaseDigestMustBeCanonicalBeforeHexDecoding(string kind)
    {
        var fixture = SignedRepositoryFixture.Create();
        AssertInvalidMessage("release digest mismatch", () => Verify(fixture, kind == "short" ? "ab" : new string('g', 64), fixture.Fingerprint, fixture.Policy(Clock, true)));
    }

    [Fact]
    public void WrongFingerprintAndUnknownSignerAreRejected()
    {
        var fixture = SignedRepositoryFixture.Create();
        AssertInvalidMessage("fingerprint", () => Verify(fixture, fixture.ReleaseSha256, new string('A', 40), fixture.Policy(Clock, true)));
        var unknown = SignedRepositoryFixture.Create();
        AssertInvalidMessage("absent", () => SignedRepository.VerifyRelease(fixture.Release(), unknown.Keyring(), fixture.ReleaseSha256, unknown.KeyringSha256,
            fixture.Fingerprint, fixture.Policy(Clock, true), fixture.Requested(), fixture.Indexes()));
    }

    [Fact]
    public void HistoricalExceptionMustNameExactReleaseBytes()
    {
        var fixture = SignedRepositoryFixture.Create();
        AssertInvalidMessage("historical", () => Verify(fixture, fixture.ReleaseSha256, fixture.Fingerprint, fixture.Policy(Clock, true) with { HistoricalReleaseSha256 = new string('1', 64) }));
        AssertInvalidMessage("validity policy", () => Verify(fixture, fixture.ReleaseSha256, fixture.Fingerprint,
            fixture.Policy(Clock, false) with { ValidUntil = null, AllowHistoricalExactRelease = false }));
    }

    [Fact]
    public void SignedScopeAndCapturedIndexAssociationAreRequired()
    {
        var fixture = SignedRepositoryFixture.Create();
        AssertInvalidMessage("scope", () => Verify(fixture, fixture.ReleaseSha256, fixture.Fingerprint,
            fixture.Policy(Clock, true) with { Components = ["universe"] }));
        using var release = fixture.Release();
        using var keyring = fixture.Keyring();
        AssertInvalidMessage("absent or ambiguous", () => SignedRepository.VerifyRelease(release, keyring, fixture.ReleaseSha256, fixture.KeyringSha256,
            fixture.Fingerprint, fixture.Policy(Clock, true), fixture.Requested(), capturedPackageIndexes: null));
    }

    [Fact]
    public void ValidUntilAndSignerCreationAreBoundToVerificationClock()
    {
        var fixture = SignedRepositoryFixture.Create(validUntil: Clock.AddDays(-1));
        AssertInvalidMessage("validity", () => Verify(fixture, fixture.ReleaseSha256, fixture.Fingerprint, fixture.Policy(Clock, false, validUntil: Clock)));
        var future = SignedRepositoryFixture.Create(keyCreation: Clock.AddHours(1));
        AssertInvalidMessage("not yet valid", () => Verify(future, future.ReleaseSha256, future.Fingerprint, future.Policy(Clock, true)));
    }

    [Fact]
    public void ExpiredSignerKeyIsRejectedAtVerificationClock()
    {
        var fixture = SignedRepositoryFixture.Create(keyCreation: Clock.AddDays(-1), keyExpirationSeconds: 1);
        AssertInvalidMessage("key is expired", () => Verify(fixture, fixture.ReleaseSha256, fixture.Fingerprint, fixture.Policy(Clock, true)));
    }

    [Fact]
    public void MissingValidUntilIsRejectedWhenPolicyRequiresIt()
    {
        var fixture = SignedRepositoryFixture.Create();
        var release = fixture.ReleaseText.Replace($"Valid-Until: {fixture.ValidUntil:R}\n", string.Empty, StringComparison.Ordinal);
        var changed = SignedRepositoryFixture.FromRelease(release, fixture);
        AssertInvalidMessage("validity date is absent", () => Verify(changed, changed.ReleaseSha256, changed.Fingerprint, changed.Policy(Clock, true)));
    }

    [Fact]
    public void MissingSignedShaSectionIsRejected()
    {
        var fixture = SignedRepositoryFixture.Create();
        var release = fixture.ReleaseText[..fixture.ReleaseText.IndexOf("SHA256:", StringComparison.Ordinal)].TrimEnd() + "\n";
        var changed = SignedRepositoryFixture.FromRelease(release, fixture);
        AssertInvalidMessage("SHA256 section is absent", () => Verify(changed, changed.ReleaseSha256, changed.Fingerprint, changed.Policy(Clock, true)));
    }

    [Fact]
    public void InvalidSignedValidityDateIsRejected()
    {
        var fixture = SignedRepositoryFixture.Create();
        var release = fixture.ReleaseText.Replace($"Valid-Until: {fixture.ValidUntil:R}", "Valid-Until: not-a-date", StringComparison.Ordinal);
        var changed = SignedRepositoryFixture.FromRelease(release, fixture);
        AssertInvalidMessage("validity date mismatch", () => Verify(changed, changed.ReleaseSha256, changed.Fingerprint, changed.Policy(Clock, true)));
    }

    [Fact]
    public void UnsupportedSignerAlgorithmIsRejectedBeforeAcceptance()
    {
        var fixture = SignedRepositoryFixture.Create(signatureHash: HashAlgorithmTag.Sha256);
        AssertInvalidMessage("algorithm", () => Verify(fixture, fixture.ReleaseSha256, fixture.Fingerprint, fixture.Policy(Clock, true)));
        var binary = SignedRepositoryFixture.Create(signatureType: PgpSignature.BinaryDocument);
        AssertInvalidMessage("algorithm", () => Verify(binary, binary.ReleaseSha256, binary.Fingerprint, binary.Policy(Clock, true)));
    }

    [Fact]
    public void RevokedSignerIsRejected()
    {
        var fixture = SignedRepositoryFixture.Create(revoked: true);
        AssertInvalidMessage("revoked", () => Verify(fixture, fixture.ReleaseSha256, fixture.Fingerprint, fixture.Policy(Clock, true)));
    }

    [Fact]
    public void MalformedAndDuplicateSignedShaRecordsAreRejected()
    {
        AssertInvalidRelease("malformed", (release, fixture) => release.Replace(fixture.IndexSha256, "not-a-sha", StringComparison.Ordinal));
        AssertInvalidRelease("malformed", (release, fixture) => release.Replace(
            $"{fixture.IndexSha256} {fixture.IndexBytes.Length} {fixture.IndexPath}",
            $"{new string('a', 64)} {fixture.IndexBytes.Length} {fixture.IndexPath}\n {new string('b', 64)} {fixture.IndexBytes.Length} {fixture.IndexPath}", StringComparison.Ordinal));
    }

    [Fact]
    public void SixtyFourCharacterNonHexReleaseHashIsRejectedAsMalformedSha256()
    {
        var fixture = SignedRepositoryFixture.Create();
        AssertInvalidRelease("malformed", (release, source) => release.Replace(source.IndexSha256, new string('g', 64), StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicatePackageParagraphAndWrongTupleAreRejected()
    {
        var fixture = SignedRepositoryFixture.Create(packageIndexText: SignedRepositoryFixture.PackageParagraph + "\n" + SignedRepositoryFixture.PackageParagraph);
        AssertInvalidMessage("ambiguous", () => Verify(fixture, fixture.ReleaseSha256, fixture.Fingerprint, fixture.Policy(Clock, true)));
        var mismatch = SignedRepositoryFixture.Create();
        using var release = mismatch.Release(); using var keyring = mismatch.Keyring();
        var exception = Assert.Throws<InvalidDataException>(() => SignedRepository.VerifyRelease(release, keyring, mismatch.ReleaseSha256, mismatch.KeyringSha256,
            mismatch.Fingerprint, mismatch.Policy(Clock, true), [new("missing", "1", "amd64")], mismatch.Indexes()));
        Assert.Contains("absent or ambiguous", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SameSizeCompressedIndexCorruptionIsRejected()
    {
        var fixture = SignedRepositoryFixture.Create();
        var corrupted = fixture.IndexBytes.ToArray(); corrupted[corrupted.Length / 2] ^= 1;
        var indexes = new Dictionary<string, Stream>(StringComparer.Ordinal) { [fixture.IndexPath] = new MemoryStream(corrupted) };
        using var release = fixture.Release(); using var keyring = fixture.Keyring();
        AssertInvalidMessage("do not match", () => SignedRepository.VerifyRelease(release, keyring, fixture.ReleaseSha256, fixture.KeyringSha256,
            fixture.Fingerprint, fixture.Policy(Clock, true), fixture.Requested(), indexes));
    }

    [Fact]
    public void SignedNonPackagePathIsIgnoredAndRequestedTupleIsAbsent()
    {
        var fixture = SignedRepositoryFixture.Create();
        var release = fixture.ReleaseText.Replace(fixture.IndexPath, "main/source/Sources.gz", StringComparison.Ordinal);
        var changed = SignedRepositoryFixture.FromRelease(release, fixture);
        AssertInvalidMessage("absent or ambiguous", () => Verify(changed, changed.ReleaseSha256, changed.Fingerprint, changed.Policy(Clock, true)));
    }

    [Fact]
    public void MalformedPackagesIndexRecordIsRejected()
    {
        var malformed = "Package: fixture-package\nVersion: 1.0\nArchitecture: amd64\nFilename: pool/main/f/fixture.deb\nSize: 4\nSHA256: not-a-sha\n";
        var fixture = SignedRepositoryFixture.Create(packageIndexText: malformed);
        AssertInvalidMessage("record is incomplete", () => Verify(fixture, fixture.ReleaseSha256, fixture.Fingerprint, fixture.Policy(Clock, true)));
    }

    [Fact]
    public void CancellationIsObservedBeforeReadingInputs()
    {
        var fixture = SignedRepositoryFixture.Create();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var release = fixture.Release(); using var keyring = fixture.Keyring();
        Assert.Throws<OperationCanceledException>(() => SignedRepository.VerifyRelease(release, keyring, fixture.ReleaseSha256, fixture.KeyringSha256,
            fixture.Fingerprint, fixture.Policy(Clock, true), fixture.Requested(), fixture.Indexes(), cancellation.Token));
    }

    private static SignedRepository.VerificationResult Verify(SignedRepositoryFixture fixture, string releaseSha, string fingerprint, SignedRepository.VerificationPolicy policy)
    {
        using var release = fixture.Release(); using var keyring = fixture.Keyring();
        return SignedRepository.VerifyRelease(release, keyring, releaseSha, fixture.KeyringSha256, fingerprint, policy, fixture.Requested(), fixture.Indexes());
    }

    private static void AssertInvalidMessage(string expectedFragment, Action action)
    {
        var exception = Assert.Throws<InvalidDataException>(action);
        Assert.Contains(expectedFragment, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertInvalidRelease(string expectedFragment, Func<string, SignedRepositoryFixture, string> mutate)
    {
        var fixture = SignedRepositoryFixture.Create();
        var release = mutate(fixture.ReleaseText, fixture);
        var changed = SignedRepositoryFixture.FromRelease(release, fixture);
        AssertInvalidMessage(expectedFragment, () => Verify(changed, changed.ReleaseSha256, changed.Fingerprint, changed.Policy(Clock, true)));
    }
}

internal sealed record SignedRepositoryFixture(
    byte[] ReleaseBytes,
    byte[] KeyringBytes,
    byte[] IndexBytes,
    string ReleaseText,
    string ReleaseSha256,
    string KeyringSha256,
    string Fingerprint,
    string IndexPath,
    string IndexSha256,
    DateTimeOffset KeyCreation,
    DateTimeOffset ValidUntil)
{
    internal PgpKeyPair SigningPair { get; init; } = null!;
    internal const string PackageParagraph = "Package: fixture-package\nVersion: 1.0\nArchitecture: amd64\nFilename: pool/main/f/fixture-package_1.0_amd64.deb\nSize: 4\nSHA256: 1111111111111111111111111111111111111111111111111111111111111111\nDepends: libc6 (>= 2.0)\n";

    internal static SignedRepositoryFixture Create(DateTimeOffset? keyCreation = null, DateTimeOffset? validUntil = null, string? packageIndexText = null,
        HashAlgorithmTag signatureHash = HashAlgorithmTag.Sha512, int signatureType = PgpSignature.CanonicalTextDocument, bool revoked = false, long? keyExpirationSeconds = null)
    {
        var creation = keyCreation ?? new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var expiry = validUntil ?? new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var keys = new RsaKeyPairGenerator(); keys.Init(new Org.BouncyCastle.Crypto.KeyGenerationParameters(new SecureRandom(), 2048));
        var pair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, keys.GenerateKeyPair(), creation.UtcDateTime);
        var hashed = new PgpSignatureSubpacketGenerator();
        if (keyExpirationSeconds is { } seconds) hashed.SetKeyExpirationTime(true, seconds);
        var ring = new PgpKeyRingGenerator(PgpSignature.PositiveCertification, pair, "signed-repository-fixture", SymmetricKeyAlgorithmTag.Aes256,
            HashAlgorithmTag.Sha512, Array.Empty<char>(), true, hashed.Generate(), null, new SecureRandom());
        var publicRing = ring.GeneratePublicKeyRing();
        if (revoked)
        {
            var revocation = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha512);
            revocation.InitSign(PgpSignature.KeyRevocation, pair.PrivateKey, new SecureRandom());
            var revokedKey = PgpPublicKey.AddCertification(publicRing.GetPublicKey(), revocation.Generate());
            publicRing = PgpPublicKeyRing.InsertPublicKey(publicRing, revokedKey);
        }
        using var keyStream = new MemoryStream(); publicRing.Encode(keyStream);
        var index = Gzip(packageIndexText ?? PackageParagraph);
        var path = "main/binary-amd64/Packages.gz";
        var release = Release(pair, creation, expiry, path, index, signatureHash, signatureType);
        var fixture = new SignedRepositoryFixture(release.Bytes, keyStream.ToArray(), index, release.Text, Digest(release.Bytes), Digest(keyStream.ToArray()), Convert.ToHexString(pair.PublicKey.GetFingerprint()), path, Digest(index), creation, expiry) { SigningPair = pair };
        return fixture;
    }

    internal static SignedRepositoryFixture FromRelease(string text, SignedRepositoryFixture source)
    {
        var bytes = Signed(text, source.SigningPair, HashAlgorithmTag.Sha512, PgpSignature.CanonicalTextDocument);
        return source with { ReleaseBytes = bytes, ReleaseText = text, ReleaseSha256 = Digest(bytes) };
    }
    internal SignedRepository.VerificationPolicy Policy(DateTimeOffset clock, bool historical, DateTimeOffset? validUntil = null) => new("resolute-updates", ["main"], ["amd64"], validUntil ?? ValidUntil, clock, historical, historical ? ReleaseSha256 : null);
    internal IReadOnlyList<SignedRepository.RequestedPackage> Requested() => [new("fixture-package", "1.0", "amd64")];
    internal IReadOnlyDictionary<string, Stream> Indexes() => new Dictionary<string, Stream>(StringComparer.Ordinal) { [IndexPath] = new MemoryStream(IndexBytes, writable: false) };
    internal Stream Release() => new MemoryStream(ReleaseBytes, writable: false);
    internal Stream Keyring() => new MemoryStream(KeyringBytes, writable: false);

    private static (string Text, byte[] Bytes) Release(PgpKeyPair pair, DateTimeOffset creation, DateTimeOffset expiry, string path, byte[] index, HashAlgorithmTag signatureHash, int signatureType)
    {
        var digest = Digest(index); var text = $"Origin: Fixture\nLabel: Fixture\nSuite: resolute-updates\nVersion: 1\nCodename: resolute\nDate: {creation:R}\nComponents: main\nArchitectures: amd64\nValid-Until: {expiry:R}\nSHA256:\n {digest} {index.Length} {path}\n";
        return (text, Signed(text, pair, signatureHash, signatureType));
    }

    private static byte[] Signed(string text, PgpKeyPair pair, HashAlgorithmTag hash = HashAlgorithmTag.Sha512, int signatureType = PgpSignature.CanonicalTextDocument)
    {
        var signature = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, hash);
        signature.InitSign(signatureType, pair.PrivateKey, new SecureRandom());
        using var output = new MemoryStream(); using (var armor = new ArmoredOutputStream(output))
        {
            armor.BeginClearText(hash);
            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
            for (var i = 0; i < lines.Length; i++) { if (i > 0) { armor.WriteByte((byte)'\n'); signature.Update(13); signature.Update(10); } var bytes = Encoding.UTF8.GetBytes(lines[i]); armor.Write(bytes, 0, bytes.Length); signature.Update(bytes); }
            armor.WriteByte((byte)'\n');
            armor.EndClearText();
            using var packets = new BcpgOutputStream(armor);
            signature.Generate().Encode(packets);
            packets.Flush();
        }
        return output.ToArray();
    }

    private static byte[] Gzip(string text) { using var output = new MemoryStream(); using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) using (var writer = new StreamWriter(gzip, new UTF8Encoding(false))) writer.Write(text); return output.ToArray(); }
    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
