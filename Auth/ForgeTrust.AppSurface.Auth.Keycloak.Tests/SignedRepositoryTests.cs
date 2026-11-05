using System.Globalization;
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
    public void ValidNonHistoricalReleaseUsesTheSignedValidityDate()
    {
        var fixture = SignedRepositoryFixture.Create();
        var result = Verify(fixture, fixture.ReleaseSha256, fixture.Fingerprint, fixture.Policy(Clock, historical: false));
        Assert.Equal(fixture.ValidUntil, fixture.Policy(Clock, historical: false).ValidUntil);
        Assert.Single(result.Packages);
    }

    [Fact]
    public void EmptyRequestedPackageSetStillVerifiesSignedScope()
    {
        var fixture = SignedRepositoryFixture.Create();
        var result = VerifyWith(fixture, fixture.Policy(Clock, historical: false), []);
        Assert.Empty(result.Packages);
        Assert.Empty(result.DependencyFacts);
    }

    [Fact]
    public void PackageIndexRecordWithoutDependenciesProducesEmptyDependencyFacts()
    {
        var fixture = SignedRepositoryFixture.Create(packageIndexText: SignedRepositoryFixture.PackageParagraph.Replace("Depends: libc6 (>= 2.0)\n", string.Empty, StringComparison.Ordinal));
        var result = Verify(fixture, fixture.ReleaseSha256, fixture.Fingerprint, fixture.Policy(Clock, true));
        Assert.Single(result.Packages);
        Assert.Empty(result.DependencyFacts.Single().Dependencies);
    }

    [Fact]
    public void ZeroSizedSignedFileEntryIsAcceptedAsAWellFormedMetadataRecord()
    {
        var fixture = SignedRepositoryFixture.Create();
        var release = fixture.ReleaseText + $" {new string('0', 64)} 0 main/source/Release\n";
        var changed = SignedRepositoryFixture.FromRelease(release, fixture);
        var result = Verify(changed, changed.ReleaseSha256, changed.Fingerprint, changed.Policy(Clock, true));
        Assert.Equal(2, result.SignedFiles.Length);
    }

    [Fact]
    public void NoCapturedIndexesOverloadVerifiesReleaseWithoutClaimingPackageClosure()
    {
        var fixture = SignedRepositoryFixture.Create();
        using var release = fixture.Release();
        using var keyring = fixture.Keyring();
        var result = SignedRepository.VerifyRelease(release, keyring, fixture.ReleaseSha256, fixture.KeyringSha256,
            fixture.Fingerprint, fixture.Policy(Clock, historical: true), [], CancellationToken.None);
        Assert.Empty(result.Packages); Assert.Single(result.SignedFiles);
    }

    [Fact]
    public void BoundedReadStreamAllowsExactArrayAndSpanCap()
    {
        using var arrayStream = new SignedRepository.BoundedReadStream(new MemoryStream([1, 2, 3]), 3);
        var array = new byte[2];
        Assert.Equal(2, arrayStream.Read(array, 0, array.Length));
        Assert.Equal(3, arrayStream.ReadByte());
        Assert.Equal(-1, arrayStream.ReadByte());
        Assert.Equal(3, arrayStream.Total);

        using var spanStream = new SignedRepository.BoundedReadStream(new MemoryStream([4, 5, 6]), 3);
        Span<byte> span = stackalloc byte[3];
        Assert.Equal(3, spanStream.Read(span));
        Assert.Equal(3, spanStream.Total);
    }

    [Fact]
    public void BoundedReadStreamRejectsArrayAndSpanReadsThatCrossCap()
    {
        using var arrayStream = new SignedRepository.BoundedReadStream(new MemoryStream([1, 2, 3, 4]), 3);
        Assert.Throws<InvalidDataException>(() => arrayStream.Read(new byte[4], 0, 4));

        using var spanStream = new SignedRepository.BoundedReadStream(new MemoryStream([1, 2, 3, 4]), 3);
        Span<byte> span = stackalloc byte[4];
        var threw = false;
        try
        {
            _ = spanStream.Read(span);
        }
        catch (InvalidDataException)
        {
            threw = true;
        }

        Assert.True(threw);
    }

    [Fact]
    public void BoundedReadStreamKeepsUnsupportedOperationsUnsupported()
    {
        using var bounded = new SignedRepository.BoundedReadStream(new MemoryStream([1]), 1);
        Assert.True(bounded.CanRead);
        Assert.False(bounded.CanSeek);
        Assert.False(bounded.CanWrite);
        Assert.Throws<NotSupportedException>(() => _ = bounded.Length);
        Assert.Throws<NotSupportedException>(() => bounded.Position = 0);
        Assert.Throws<NotSupportedException>(() => bounded.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => bounded.SetLength(0));
        Assert.Throws<NotSupportedException>(() => bounded.Write([1], 0, 1));
        bounded.Flush();
    }

    [Fact]
    public void NullCapturedStreamsAreRejectedBeforeReading()
    {
        var fixture = SignedRepositoryFixture.Create();
        Assert.Throws<ArgumentNullException>(() => SignedRepository.VerifyRelease(null!, fixture.Keyring(), fixture.ReleaseSha256,
            fixture.KeyringSha256, fixture.Fingerprint, fixture.Policy(Clock, true), fixture.Requested(), fixture.Indexes()));
        Assert.Throws<ArgumentNullException>(() => SignedRepository.VerifyRelease(fixture.Release(), null!, fixture.ReleaseSha256,
            fixture.KeyringSha256, fixture.Fingerprint, fixture.Policy(Clock, true), fixture.Requested(), fixture.Indexes()));
    }

    [Fact]
    public void DuplicateReleaseSignaturesAreRejected()
    {
        var fixture = SignedRepositoryFixture.Create(duplicateSignature: true);
        AssertInvalidMessage("exactly one release signature", () => Verify(fixture, fixture.ReleaseSha256, fixture.Fingerprint, fixture.Policy(Clock, true)));
    }

    [Fact]
    public void UnapprovedReleaseTrailerFailsExactOwnerDigest()
    {
        var fixture = SignedRepositoryFixture.Create();
        var bytes = fixture.ReleaseBytes.Concat(Encoding.UTF8.GetBytes("unsigned-trailer")).ToArray();
        var changed = fixture with { ReleaseBytes = bytes };
        AssertInvalidMessage("release digest mismatch", () => Verify(changed, fixture.ReleaseSha256, changed.Fingerprint, changed.Policy(Clock, true)));
    }

    [Fact]
    public void ReleaseAndKeyringHashesAreRequired()
    {
        var fixture = SignedRepositoryFixture.Create();
        AssertInvalidMessage("release digest mismatch", () => Verify(fixture, new string('0', 64), fixture.Fingerprint, fixture.Policy(Clock, true)));
        using var release = fixture.Release();
        using var wrongKeyring = new MemoryStream(Encoding.UTF8.GetBytes("wrong"));
        AssertInvalidMessage("keyring digest mismatch", () => SignedRepository.VerifyRelease(
            release, wrongKeyring, fixture.ReleaseSha256, fixture.KeyringSha256,
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
    public void SignedSuiteMustMatchPolicy()
    {
        AssertInvalidRelease("suite mismatch", (release, fixture) => release.Replace("Suite: resolute-updates", "Suite: other-suite", StringComparison.Ordinal));
    }

    [Fact]
    public void ExpiredSignedValidityIsRejectedForNonHistoricalPolicy()
    {
        var fixture = SignedRepositoryFixture.Create(validUntil: Clock.AddMinutes(-1));
        AssertInvalidMessage("validity date mismatch", () => Verify(fixture, fixture.ReleaseSha256, fixture.Fingerprint, fixture.Policy(Clock, historical: false)));
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

    [Theory]
    [InlineData("-1", "negative size")]
    [InlineData("/absolute/path", "unsafe path")]
    public void SignedFileSizeAndPathAreBounded(string replacement, string _)
    {
        AssertInvalidRelease("malformed", (release, fixture) => release.Replace(
            $"{fixture.IndexSha256} {fixture.IndexBytes.Length} {fixture.IndexPath}",
            $"{fixture.IndexSha256} {(replacement == "-1" ? replacement : fixture.IndexBytes.Length.ToString(CultureInfo.InvariantCulture))} {(replacement == "-1" ? fixture.IndexPath : replacement)}", StringComparison.Ordinal));
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

    [Theory]
    [InlineData("Filename: /absolute/path.deb\n", "record is unsafe")]
    [InlineData("Filename: ../relative/path.deb\n", "record is unsafe")]
    [InlineData("Package: fixture-package\nPackage: duplicate\n", "duplicate or malformed")]
    public void PackageIndexPathSizeAndDuplicateFieldsAreRejected(string replacement, string expected)
    {
        var text = replacement switch
        {
            "Filename: /absolute/path.deb\n" or "Filename: ../relative/path.deb\n" => SignedRepositoryFixture.PackageParagraph.Replace("Filename: pool/main/f/fixture-package_1.0_amd64.deb\n", replacement, StringComparison.Ordinal),
            _ => SignedRepositoryFixture.PackageParagraph.Replace("Package: fixture-package\n", replacement, StringComparison.Ordinal)
        };
        var fixture = SignedRepositoryFixture.Create(packageIndexText: text);
        AssertInvalidMessage(expected, () => Verify(fixture, fixture.ReleaseSha256, fixture.Fingerprint, fixture.Policy(Clock, true)));
    }

    [Fact]
    public void PackageIndexSupportsCrLfAndAnUnterminatedFinalLine()
    {
        var fixture = SignedRepositoryFixture.Create(packageIndexText: SignedRepositoryFixture.PackageParagraph.TrimEnd('\n').Replace("\n", "\r\n", StringComparison.Ordinal));
        var result = Verify(fixture, fixture.ReleaseSha256, fixture.Fingerprint, fixture.Policy(Clock, true));
        Assert.Single(result.Packages);
    }

    [Fact]
    public void PackageIndexContinuationWithoutFieldIsRejected()
    {
        var fixture = SignedRepositoryFixture.Create(packageIndexText: " continuation-before-field\n" + SignedRepositoryFixture.PackageParagraph);
        AssertInvalidMessage("metadata continuation", () => Verify(fixture, fixture.ReleaseSha256, fixture.Fingerprint, fixture.Policy(Clock, true)));
    }

    [Fact]
    public void OversizedPackageIndexLineIsRejectedBeforeMetadataAcceptance()
    {
        var fixture = SignedRepositoryFixture.Create(packageIndexText: "Package: " + new string('x', SignedRepository.MaximumParagraphBytes + 1) + "\n");
        AssertInvalidMessage("line exceeded", () => Verify(fixture, fixture.ReleaseSha256, fixture.Fingerprint, fixture.Policy(Clock, true)));
    }

    [Fact]
    public void UnknownSignedMetadataIsRejected()
    {
        AssertInvalidRelease("unknown signed metadata", (release, _) => "Unexpected: value\n" + release);
    }

    [Fact]
    public void MissingArchitectureScopeIsRejected()
    {
        AssertInvalidRelease("required signed metadata is absent", (release, _) => release.Replace("Architectures: amd64\n", string.Empty, StringComparison.Ordinal));
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

    [Fact]
    public void ReleaseInputOverMaximumIsRejectedBeforeSignatureVerification()
    {
        var fixture = SignedRepositoryFixture.Create();
        var oversized = fixture.ReleaseBytes.Concat(new byte[checked((int)(SignedRepository.MaximumReleaseBytes + 1L - fixture.ReleaseBytes.LongLength))]).ToArray();
        using var release = new MemoryStream(oversized, writable: false);
        using var keyring = fixture.Keyring();
        AssertInvalidMessage("bounded input exceeded", () => SignedRepository.VerifyRelease(release, keyring, fixture.ReleaseSha256, fixture.KeyringSha256,
            fixture.Fingerprint, fixture.Policy(Clock, true), fixture.Requested(), fixture.Indexes()));
    }

    [Fact]
    public void KeyringInputOverMaximumIsRejectedBeforeSignerLookup()
    {
        var fixture = SignedRepositoryFixture.Create();
        using var release = fixture.Release();
        using var keyring = new MemoryStream(fixture.KeyringBytes.Concat(new byte[checked((int)(SignedRepository.MaximumKeyringBytes + 1L - fixture.KeyringBytes.LongLength))]).ToArray(), writable: false);
        AssertInvalidMessage("bounded input exceeded", () => SignedRepository.VerifyRelease(release, keyring, fixture.ReleaseSha256, fixture.KeyringSha256,
            fixture.Fingerprint, fixture.Policy(Clock, true), fixture.Requested(), fixture.Indexes()));
    }

    [Fact]
    public void SignedCleartextMutationFailsEvenWhenMutatedBytesAreIndependentlyApproved()
    {
        var fixture = SignedRepositoryFixture.Create();
        var original = Encoding.UTF8.GetBytes("Suite: resolute-updates");
        var replacement = Encoding.UTF8.GetBytes("Suite: resolute-updatxs");
        var mutated = fixture.ReleaseBytes.ToArray();
        var offset = mutated.AsSpan().IndexOf(original);
        Assert.True(offset >= 0);
        replacement.CopyTo(mutated, offset);
        var changed = fixture with { ReleaseBytes = mutated, ReleaseSha256 = Convert.ToHexStringLower(SHA256.HashData(mutated)) };
        AssertInvalidMessage("signature verification failed", () => Verify(changed, changed.ReleaseSha256, changed.Fingerprint, changed.Policy(Clock, true)));
    }

    [Fact]
    public void DuplicateKnownReleaseFieldIsRejected()
    {
        var fixture = SignedRepositoryFixture.Create();
        var release = fixture.ReleaseText.Replace("Label: Fixture\n", "Label: Fixture\nLabel: Duplicate\n", StringComparison.Ordinal);
        var changed = SignedRepositoryFixture.FromRelease(release, fixture);
        AssertInvalidMessage("duplicate or malformed", () => Verify(changed, changed.ReleaseSha256, changed.Fingerprint, changed.Policy(Clock, true)));
    }

    [Fact]
    public void MalformedReleaseFieldIsRejected()
    {
        var fixture = SignedRepositoryFixture.Create();
        var changed = SignedRepositoryFixture.FromRelease("Malformed release field\n" + fixture.ReleaseText, fixture);
        AssertInvalidMessage("duplicate or malformed", () => Verify(changed, changed.ReleaseSha256, changed.Fingerprint, changed.Policy(Clock, true)));
    }

    [Fact]
    public void OversizedMultiLinePackageParagraphIsRejected()
    {
        var packageIndex = string.Concat(Enumerable.Repeat("Description: xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\n", 1200));
        var fixture = SignedRepositoryFixture.Create(packageIndexText: packageIndex);
        AssertInvalidMessage("paragraph exceeded", () => Verify(fixture, fixture.ReleaseSha256, fixture.Fingerprint, fixture.Policy(Clock, true)));
    }

    [Fact]
    public void ExactPolicyWithoutValidityApprovalIsRejected()
    {
        var fixture = SignedRepositoryFixture.Create();
        AssertInvalidMessage("validity policy is incomplete", () => Verify(fixture, fixture.ReleaseSha256, fixture.Fingerprint,
            SignedRepository.VerificationPolicy.Exact("resolute-updates", ["main"], ["amd64"])));
    }

    // protects: signed metadata paths and syntax remain closed even when their bytes are validly signed.
    // fails_when: path traversal or a tab continuation is treated as a new authority.
    // seam: public verification with ephemeral signatures; no live repository approval is claimed.
    [Theory]
    [InlineData("relative/../Packages.gz")]
    [InlineData("../Packages.gz")]
    public void SignedIndexRejectsParentSegmentsInsideOtherwiseValidPath(string path)
    {
        AssertInvalidRelease("malformed", (release, fixture) => release.Replace(fixture.IndexPath, path, StringComparison.Ordinal));
    }

    [Fact]
    public void TabContinuationAndLeadingBlankParagraphKeepTheSignedDependencyFacts()
    {
        var text = "\n\n" + SignedRepositoryFixture.PackageParagraph.Replace(
            "Depends: libc6 (>= 2.0)\n", "Depends: libc6\n\t(>= 2.0)\n", StringComparison.Ordinal);
        var fixture = SignedRepositoryFixture.Create(packageIndexText: text);
        var result = Verify(fixture, fixture.ReleaseSha256, fixture.Fingerprint, fixture.Policy(Clock, true));
        Assert.Single(result.Packages);
        Assert.Equal("libc6\n(>= 2.0)", Assert.Single(result.DependencyFacts.Single().Dependencies));
    }

    [Theory]
    [InlineData("Size: 4", "Size: invalid", "incomplete")]
    [InlineData("SHA256: 1111111111111111111111111111111111111111111111111111111111111111", "SHA256: g111111111111111111111111111111111111111111111111111111111111111", "incomplete")]
    public void SignedPackageRejectsNonNumericSizeAndNonHexDigest(string original, string replacement, string expected)
    {
        var fixture = SignedRepositoryFixture.Create(packageIndexText: SignedRepositoryFixture.PackageParagraph.Replace(original, replacement, StringComparison.Ordinal));
        AssertInvalidMessage(expected, () => Verify(fixture, fixture.ReleaseSha256, fixture.Fingerprint, fixture.Policy(Clock, true)));
    }

    private static SignedRepository.VerificationResult Verify(SignedRepositoryFixture fixture, string releaseSha, string fingerprint, SignedRepository.VerificationPolicy policy)
    {
        using var release = fixture.Release(); using var keyring = fixture.Keyring();
        return SignedRepository.VerifyRelease(release, keyring, releaseSha, fixture.KeyringSha256, fingerprint, policy, fixture.Requested(), fixture.Indexes());
    }

    private static SignedRepository.VerificationResult VerifyWith(SignedRepositoryFixture fixture, SignedRepository.VerificationPolicy policy, IReadOnlyList<SignedRepository.RequestedPackage> requested)
    {
        using var release = fixture.Release(); using var keyring = fixture.Keyring();
        return SignedRepository.VerifyRelease(release, keyring, fixture.ReleaseSha256, fixture.KeyringSha256, fixture.Fingerprint, policy, requested, fixture.Indexes());
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
        HashAlgorithmTag signatureHash = HashAlgorithmTag.Sha512, int signatureType = PgpSignature.CanonicalTextDocument, bool revoked = false, long? keyExpirationSeconds = null, bool duplicateSignature = false)
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
        var release = Release(pair, creation, expiry, path, index, signatureHash, signatureType, duplicateSignature);
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

    private static (string Text, byte[] Bytes) Release(PgpKeyPair pair, DateTimeOffset creation, DateTimeOffset expiry, string path, byte[] index, HashAlgorithmTag signatureHash, int signatureType, bool duplicateSignature)
    {
        var digest = Digest(index); var text = $"Origin: Fixture\nLabel: Fixture\nSuite: resolute-updates\nVersion: 1\nCodename: resolute\nDate: {creation:R}\nComponents: main\nArchitectures: amd64\nValid-Until: {expiry:R}\nSHA256:\n {digest} {index.Length} {path}\n";
        return (text, Signed(text, pair, signatureHash, signatureType, duplicateSignature));
    }

    private static byte[] Signed(string text, PgpKeyPair pair, HashAlgorithmTag hash = HashAlgorithmTag.Sha512, int signatureType = PgpSignature.CanonicalTextDocument, bool duplicateSignature = false)
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
            var generated = signature.Generate();
            generated.Encode(packets);
            if (duplicateSignature) generated.Encode(packets);
            packets.Flush();
        }
        return output.ToArray();
    }

    private static byte[] Gzip(string text) { using var output = new MemoryStream(); using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) using (var writer = new StreamWriter(gzip, new UTF8Encoding(false))) writer.Write(text); return output.ToArray(); }
    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
