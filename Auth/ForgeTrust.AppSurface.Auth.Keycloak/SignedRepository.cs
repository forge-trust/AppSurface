using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace ForgeTrust.AppSurface.Auth.Keycloak;
/// <summary>Private bounded verifier for an exact Debian signed repository snapshot.</summary>
internal static class SignedRepository
{
    internal const long MaximumReleaseBytes = 1024 * 1024;
    internal const long MaximumKeyringBytes = 8 * 1024 * 1024;
    internal const long MaximumPackagesCompressedBytes = 8 * 1024 * 1024;
    internal const long MaximumPackagesExpandedBytes = 32 * 1024 * 1024;
    internal const int MaximumParagraphBytes = 64 * 1024;
    internal sealed record VerificationPolicy(string Suite, ImmutableArray<string> Components, ImmutableArray<string> Architectures, DateTimeOffset? ValidUntil, DateTimeOffset? VerificationTime, bool AllowHistoricalExactRelease, string? HistoricalReleaseSha256)
    {
        internal static VerificationPolicy Exact(string suite, IEnumerable<string> components, IEnumerable<string> architectures) => new(suite, components.ToImmutableArray(), architectures.ToImmutableArray(), null, null, false, null);
    }

    internal sealed record RequestedPackage(string Package, string Version, string Architecture);
    internal sealed record ReleaseFile(string Path, long Size, string Sha256);
    internal sealed record PackageIndexEntry(string Package, string Version, string Architecture, string Path, long Size, string Sha256, ImmutableArray<string> Dependencies);
    internal sealed record DependencyFact(string Package, string Version, string Architecture, ImmutableArray<string> Dependencies);
    internal sealed record VerificationResult(string ReleaseSha256, string KeyringSha256, string SignerFingerprint, string Suite, ImmutableArray<string> Components, ImmutableArray<string> Architectures, ImmutableArray<ReleaseFile> SignedFiles, ImmutableArray<PackageIndexEntry> Packages, ImmutableArray<DependencyFact> DependencyFacts);
    /// <summary>Verifies canonical text, signer policy, and the exact signed package index records.</summary>
    internal static VerificationResult VerifyRelease(Stream boundedCapturedRelease, Stream boundedKeyring, string expectedReleaseSha256, string expectedKeyringSha256, string exactSignerFingerprint, VerificationPolicy policy, IEnumerable<RequestedPackage> requestedPackages, CancellationToken cancellationToken = default) => VerifyRelease(boundedCapturedRelease, boundedKeyring, expectedReleaseSha256, expectedKeyringSha256, exactSignerFingerprint, policy, requestedPackages, null, cancellationToken);
    /// <summary>Verifies a snapshot and reads only the captured package indexes named by its signed SHA256 table.</summary>
    internal static VerificationResult VerifyRelease(Stream boundedCapturedRelease, Stream boundedKeyring, string expectedReleaseSha256, string expectedKeyringSha256, string exactSignerFingerprint, VerificationPolicy policy, IEnumerable<RequestedPackage> requestedPackages, IReadOnlyDictionary<string, Stream>? capturedPackageIndexes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(boundedCapturedRelease);
        ArgumentNullException.ThrowIfNull(boundedKeyring);
        cancellationToken.ThrowIfCancellationRequested();
        var release = ReadBounded(boundedCapturedRelease, MaximumReleaseBytes, cancellationToken);
        var keyring = ReadBounded(boundedKeyring, MaximumKeyringBytes, cancellationToken);
        RequireSha256(release, expectedReleaseSha256, "release");
        RequireSha256(keyring, expectedKeyringSha256, "keyring");
        var text = VerifyOpenPgp(release, keyring, exactSignerFingerprint, policy, cancellationToken);
        var parsed = ParseRelease(text, policy, capturedPackageIndexes, cancellationToken);
        var requested = requestedPackages.ToImmutableArray();
        var byTuple = parsed.Packages.GroupBy(p => (p.Package, p.Version, p.Architecture)).ToDictionary(g => g.Key, g => g.ToArray());
        var selected = ImmutableArray.CreateBuilder<PackageIndexEntry>();
        foreach (var request in requested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (request.Package, request.Version, request.Architecture);
            if (!byTuple.TryGetValue(key, out var matches) || matches.Length != 1)
                throw new InvalidDataException("Requested package tuple is absent or ambiguous.");
            selected.Add(matches[0]);
        }

        return parsed with
        {
            ReleaseSha256 = Convert.ToHexString(SHA256.HashData(release)).ToLowerInvariant(),
            KeyringSha256 = Convert.ToHexString(SHA256.HashData(keyring)).ToLowerInvariant(),
            SignerFingerprint = exactSignerFingerprint.ToUpperInvariant(),
            Packages = selected.ToImmutable(),
            DependencyFacts = selected.Select(p => new DependencyFact(p.Package, p.Version, p.Architecture, p.Dependencies)).ToImmutableArray()
        };
    }

    private static string VerifyOpenPgp(byte[] bytes, byte[] keyringBytes, string fingerprint, VerificationPolicy policy, CancellationToken cancellationToken)
    {
        using var input = new ArmoredInputStream(new MemoryStream(bytes, writable: false));
        using var clear = new MemoryStream();
        while (input.IsClearText())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var b = input.ReadByte();
            if (b < 0 || !input.IsClearText())
                break;
            clear.WriteByte((byte)b);
            if (clear.Length > MaximumReleaseBytes)
                throw new InvalidDataException("Cleartext release exceeds limit.");
        }

        var objects = new PgpObjectFactory(input);
        var signatures = objects.NextPgpObject() as PgpSignatureList ?? throw new InvalidDataException("Release signature is missing.");
        if (signatures.Count != 1)
            throw new InvalidDataException("Exactly one release signature is required.");
        var signature = signatures[0];
        if (signature.SignatureType != PgpSignature.CanonicalTextDocument || signature.HashAlgorithm != HashAlgorithmTag.Sha512 || signature.KeyAlgorithm != PublicKeyAlgorithmTag.RsaGeneral)
            throw new InvalidDataException("Release signature algorithm is not approved.");
        using var keyringInput = new MemoryStream(keyringBytes, writable: false);
        using var decodedKeyring = PgpUtilities.GetDecoderStream(keyringInput);
        var ring = new PgpPublicKeyRingBundle(decodedKeyring);
        var key = ring.GetPublicKey(signature.KeyId) ?? throw new InvalidDataException("Release signer is absent from the keyring.");
        var actual = Convert.ToHexString(key.GetFingerprint());
        if (!actual.Equals(fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Release signer fingerprint mismatch.");
        if (key.HasRevocation())
            throw new InvalidDataException("Release signer is revoked.");
        if (key.CreationTime > (policy.VerificationTime ?? DateTimeOffset.UtcNow).UtcDateTime)
            throw new InvalidDataException("Release signer is not yet valid.");
        if (key.GetValidSeconds() is > 0 and var validSeconds && key.CreationTime.AddSeconds(validSeconds) <= (policy.VerificationTime ?? DateTimeOffset.UtcNow).UtcDateTime)
            throw new InvalidDataException("Release signer key is expired.");
        if (policy.ValidUntil is null && !policy.AllowHistoricalExactRelease)
            throw new InvalidDataException("Release validity policy is incomplete.");
        if (policy.AllowHistoricalExactRelease && !string.Equals(policy.HistoricalReleaseSha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Historical release exception does not match exact bytes.");
        signature.InitVerify(key);
        var canonical = Encoding.UTF8.GetString(clear.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
        var lines = canonical.Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0)
            lines = lines[..^1];
        for (var i = 0; i < lines.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (i > 0)
                signature.Update([13, 10]);
            var line = lines[i].TrimEnd(' ', '\t');
            signature.Update(Encoding.UTF8.GetBytes(line));
        }

        if (!signature.Verify())
            throw new InvalidDataException("Release signature verification failed.");
        return canonical;
    }

    private static VerificationResult ParseRelease(string text, VerificationPolicy policy, IReadOnlyDictionary<string, Stream>? capturedPackageIndexes, CancellationToken cancellationToken)
    {
        var fields = ParseParagraph(text, allowContinuation: true);
        RequireUniqueKnown(fields, "Origin", "Label", "Suite", "Version", "Codename", "Date", "Components", "Architectures", "Description", "MD5Sum", "SHA1", "SHA256", "Valid-Until", "Acquire-By-Hash", "NotAutomatic", "ButAutomaticUpgrades");
        if (!fields.TryGetValue("Suite", out var suite) || suite != policy.Suite)
            throw new InvalidDataException("Signed suite mismatch.");
        var components = SplitWords(fields, "Components");
        var architectures = SplitWords(fields, "Architectures");
        if (!components.SequenceEqual(policy.Components, StringComparer.Ordinal) || !architectures.SequenceEqual(policy.Architectures, StringComparer.Ordinal))
            throw new InvalidDataException("Signed repository scope mismatch.");
        if (fields.TryGetValue("Valid-Until", out var validUntil))
        {
            if (!DateTimeOffset.TryParse(validUntil, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) || policy.ValidUntil is not null && parsed != policy.ValidUntil || !policy.AllowHistoricalExactRelease && parsed <= (policy.VerificationTime ?? DateTimeOffset.UtcNow))
                throw new InvalidDataException("Signed validity date mismatch.");
        }
        else if (policy.ValidUntil is not null)
            throw new InvalidDataException("Signed validity date is absent.");
        if (!fields.TryGetValue("SHA256", out var shaSection))
            throw new InvalidDataException("Signed SHA256 section is absent.");
        var files = ParseReleaseFiles(shaSection);
        var packages = ImmutableArray.CreateBuilder<PackageIndexEntry>();
        foreach (var file in files.Where(f => f.Path.EndsWith("Packages.gz", StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (capturedPackageIndexes is null || !capturedPackageIndexes.TryGetValue(file.Path, out var index))
                continue;
            packages.AddRange(ReadPackagesIndex(index, file, cancellationToken));
        }

        return new VerificationResult(string.Empty, string.Empty, string.Empty, suite, components.ToImmutableArray(), architectures.ToImmutableArray(), files.ToImmutableArray(), packages.ToImmutable(), ImmutableArray<DependencyFact>.Empty);
    }

    private static List<ReleaseFile> ParseReleaseFiles(string section)
    {
        var result = new List<ReleaseFile>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in section.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length != 3 || p[0].Length != 64 || !p[0].All(char.IsAsciiHexDigit) || !long.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size < 0 || !paths.Add(p[2]) || p[2].Contains("..", StringComparison.Ordinal) || p[2].StartsWith('/'))
                throw new InvalidDataException("Signed SHA256 entry is malformed, duplicated, or unsafe.");
            Convert.FromHexString(p[0]);
            result.Add(new ReleaseFile(p[2], size, p[0].ToLowerInvariant()));
        }

        return result;
    }

    private static IEnumerable<PackageIndexEntry> ReadPackagesIndex(Stream input, ReleaseFile expected, CancellationToken cancellationToken)
    {
        var compressedBytes = ReadBounded(input, MaximumPackagesCompressedBytes, cancellationToken);
        if (compressedBytes.LongLength != expected.Size || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(compressedBytes), Convert.FromHexString(expected.Sha256)))
            throw new InvalidDataException("Packages.gz bytes do not match signed Release metadata.");
        using var compressed = new BoundedReadStream(new MemoryStream(compressedBytes, writable: false), MaximumPackagesCompressedBytes);
        using var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionMode.Decompress);
        using var expanded = new BoundedReadStream(gzip, MaximumPackagesExpandedBytes);
        var paragraph = new StringBuilder();
        while (ReadParagraph(expanded, paragraph, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (paragraph.Length == 0)
                continue;
            var fields = ParseParagraph(paragraph.ToString(), allowContinuation: true);
            paragraph.Clear();
            if (!fields.TryGetValue("Package", out var package) || !fields.TryGetValue("Version", out var version) || !fields.TryGetValue("Architecture", out var architecture) || !fields.TryGetValue("Filename", out var path) || !fields.TryGetValue("Size", out var sizeText) || !fields.TryGetValue("SHA256", out var sha) || !long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var size) || sha.Length != 64 || !sha.All(char.IsAsciiHexDigit))
                throw new InvalidDataException("Packages.gz record is incomplete.");
            if (!path.Equals(path.Trim(), StringComparison.Ordinal) || path.StartsWith('/') || path.Contains("..", StringComparison.Ordinal) || Convert.FromHexString(sha).Length != 32 || size < 0)
                throw new InvalidDataException("Packages.gz record is unsafe.");
            var dependencies = fields.TryGetValue("Depends", out var depends) ? depends.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToImmutableArray() : ImmutableArray<string>.Empty;
            yield return new PackageIndexEntry(package, version, architecture, path, size, sha.ToLowerInvariant(), dependencies);
        }

        if (paragraph.Length != 0)
            throw new InvalidDataException("Truncated Packages.gz paragraph.");
        if (compressed.Total != expected.Size)
            throw new InvalidDataException("Packages.gz size does not match signed Release metadata.");
    }

    private static bool ReadParagraph(Stream stream, StringBuilder paragraph, CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        paragraph.Clear();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = stream.ReadByte();
            if (value < 0)
            {
                if (line.Length > 0)
                    AppendLine(paragraph, line);
                return paragraph.Length > 0;
            }

            if (value == '\n')
            {
                if (line.Length == 0)
                {
                    if (paragraph.Length > 0)
                        return true;
                    continue;
                }

                AppendLine(paragraph, line);
                line.Clear();
                continue;
            }

            if (value != '\r')
                line.Append((char)value);
            if (line.Length > MaximumParagraphBytes)
                throw new InvalidDataException("Packages.gz paragraph line exceeded.");
        }
    }

    private static void AppendLine(StringBuilder paragraph, StringBuilder line)
    {
        if (paragraph.Length + line.Length + 1 > MaximumParagraphBytes)
            throw new InvalidDataException("Packages.gz paragraph exceeded.");
        paragraph.Append(line).Append('\n');
    }

    private static Dictionary<string, string> ParseParagraph(string text, bool allowContinuation)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0)
                continue;
            if (line[0] is ' ' or '\t')
            {
                if (!allowContinuation || current is null)
                    throw new InvalidDataException("Invalid metadata continuation.");
                result[current] += "\n" + line.Trim();
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0 || !result.TryAdd(line[..colon], line[(colon + 1)..].Trim()))
                throw new InvalidDataException("Duplicate or malformed signed metadata.");
            current = line[..colon];
        }

        return result;
    }

    private static void RequireUniqueKnown(Dictionary<string, string> fields, params string[] known)
    {
        if (fields.Keys.Any(k => !known.Contains(k, StringComparer.Ordinal)))
            throw new InvalidDataException("Unknown signed metadata is ambiguous.");
    }

    private static ImmutableArray<string> SplitWords(Dictionary<string, string> fields, string key) => fields.TryGetValue(key, out var value) ? value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToImmutableArray() : throw new InvalidDataException("Required signed metadata is absent.");
    private static byte[] ReadBounded(Stream stream, long maximum, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream((int)Math.Min(maximum, 1024 * 1024));
        var buffer = new byte[32 * 1024];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (total > maximum - read)
                throw new InvalidDataException("Bounded input exceeded.");
            output.Write(buffer, 0, read);
            total += read;
        }

        return output.ToArray();
    }

    private static void RequireSha256(byte[] bytes, string expected, string name)
    {
        if (expected.Length != 64 || !expected.All(char.IsAsciiHexDigit) || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(expected)))
            throw new InvalidDataException($"{name} digest mismatch.");
    }

    internal sealed class BoundedReadStream(Stream inner, long cap) : Stream
    {
        internal long Total { get; private set; }

        public override int Read(byte[] b, int o, int c)
        {
            var n = inner.Read(b, o, c);
            Total += n;
            if (Total > cap)
                throw new InvalidDataException("Bounded package index stream exceeded.");
            return n;
        }

        public override int Read(Span<byte> b)
        {
            var n = inner.Read(b);
            Total += n;
            if (Total > cap)
                throw new InvalidDataException("Bounded package index stream exceeded.");
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Total; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
