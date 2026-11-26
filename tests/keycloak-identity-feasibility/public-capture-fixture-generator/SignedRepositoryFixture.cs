using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Security;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

// Preparation-only signing helper copied from the existing signed-repository test fixture.
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
