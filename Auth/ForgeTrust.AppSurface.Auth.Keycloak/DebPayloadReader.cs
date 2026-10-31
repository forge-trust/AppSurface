using System.Collections.Immutable;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ZstdSharp;

namespace ForgeTrust.AppSurface.Auth.Keycloak;
/// <summary>Private bounded Debian archive reader. It hashes payload streams and never extracts them.</summary>
internal static class DebPayloadReader
{
    internal const long CompressedLimit = 8 * 1024 * 1024;
    internal const long ExpandedLimit = 32 * 1024 * 1024;
    internal const long EntryLimit = 64 * 1024 * 1024;
    internal const int EntryCountLimit = 16384;
    private const int TarNameLimitBytes = 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal sealed record PackageIdentity(string Package, string Version, string Architecture);
    internal sealed record FileRecord(string Path, string Kind, long Length, string? Sha256, int Mode, int Uid, int Gid, string? LinkTarget);
    internal sealed record PackageResult(PackageIdentity Identity, ImmutableDictionary<string, string> Control, ImmutableArray<string> Conffiles, ImmutableArray<FileRecord> Files, long DebBytes, long DataExpandedBytes);
    internal sealed record InstalledFile(string Path, string? Sha256, string Kind);
    internal sealed record ExpectedChange(string Path, string Kind, string? Sha256);
    /// <summary>Verifies one captured package and parses control, conffile, regular, and link records.</summary>
    internal static PackageResult Read(Stream deb, string expectedSha256, PackageIdentity expected, CancellationToken cancellationToken = default)
    {
        if (!deb.CanSeek)
            throw new InvalidDataException("A bounded seekable package capture is required.");
        cancellationToken.ThrowIfCancellationRequested();
        deb.Position = 0;
        using (var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[32768];
            long total = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = deb.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;
                total = checked(total + read);
                if (total > CompressedLimit * 4)
                    throw new InvalidDataException("Deb limit exceeded.");
                h.AppendData(buffer, 0, read);
            }

            if (expectedSha256.Length != 64 || !expectedSha256.All(char.IsAsciiHexDigit) || !CryptographicOperations.FixedTimeEquals(h.GetHashAndReset(), Convert.FromHexString(expectedSha256)))
                throw new InvalidDataException("Deb digest mismatch.");
        }

        deb.Position = 0;
        if (Ascii(deb, 8, cancellationToken) != "!<arch>\n")
            throw new InvalidDataException("Ar magic rejected.");
        var members = new List<Member>();
        while (deb.Position < deb.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (members.Count == 3)
                throw new InvalidDataException("Extra ar member.");
            if (deb.Length - deb.Position < 60)
                throw new InvalidDataException("Truncated ar header.");
            var header = Bytes(deb, 60, cancellationToken);
            if (header[58] != 0x60 || header[59] != 10)
                throw new InvalidDataException("Ar header rejected.");
            var name = Encoding.ASCII.GetString(header, 0, 16).Trim().TrimEnd('/');
            if (name.Length == 0 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-'))
                throw new InvalidDataException("Ar name rejected.");
            if (!long.TryParse(Encoding.ASCII.GetString(header, 48, 10).Trim(), out var size) || size < 0 || size > CompressedLimit)
                throw new InvalidDataException("Ar size rejected.");
            var offset = deb.Position;
            var padding = size & 1;
            if (size > deb.Length - offset || padding > deb.Length - offset - size)
                throw new InvalidDataException("Ar member is truncated.");
            members.Add(new(name, offset, size));
            deb.Position = checked(offset + size);
            if (padding != 0 && deb.ReadByte() != '\n')
                throw new InvalidDataException("Ar member padding rejected.");
        }

        if (deb.Position != deb.Length || members.Count != 3 || members[0].Name != "debian-binary" || members[1].Name is not ("control.tar.gz" or "control.tar.zst") || members[2].Name is not ("data.tar.gz" or "data.tar.zst"))
            throw new InvalidDataException("Ar member set rejected.");
        if (members[0].Length != 4)
            throw new InvalidDataException("Debian version member length rejected.");
        deb.Position = members[0].Offset;
        if (Ascii(deb, 4, cancellationToken) != "2.0\n")
            throw new InvalidDataException("Debian version rejected.");
        var control = ReadTar(deb, members[1], 64, 1024 * 1024, true, cancellationToken);
        if (!control.Fields.TryGetValue("Package", out var package) || !control.Fields.TryGetValue("Version", out var version) || !control.Fields.TryGetValue("Architecture", out var architecture) || package != expected.Package || version != expected.Version || architecture != expected.Architecture)
            throw new InvalidDataException("Control identity mismatch.");
        var data = ReadTar(deb, members[2], EntryCountLimit, EntryLimit, false, cancellationToken);
        return new(expected, control.Fields.ToImmutableDictionary(StringComparer.Ordinal), control.Conffiles.ToImmutableArray(), data.Files.ToImmutableArray(), deb.Length, data.Expanded);
    }

    /// <summary>Compares every included baseline path and permits only the explicit expected change set.</summary>
    internal static void CompareInstalledInventory(IEnumerable<InstalledFile> baseline, IEnumerable<InstalledFile> actual, IEnumerable<string> excludes, IEnumerable<string> includes, IEnumerable<ExpectedChange> expectedChanges)
    {
        var ex = excludes.Select(Canonical).ToArray();
        var inc = includes.Select(Canonical).ToHashSet(StringComparer.Ordinal);
        var want = expectedChanges.ToDictionary(x => Canonical(x.Path), StringComparer.Ordinal);
        var oldSet = baseline.Where(x => Included(x.Path, ex, inc)).ToDictionary(x => Canonical(x.Path), StringComparer.Ordinal);
        var newSet = actual.Where(x => Included(x.Path, ex, inc)).ToDictionary(x => Canonical(x.Path), StringComparer.Ordinal);
        foreach (var path in oldSet.Keys.Union(newSet.Keys, StringComparer.Ordinal))
        {
            oldSet.TryGetValue(path, out var o);
            newSet.TryGetValue(path, out var n);
            var changed = o is null || n is null || o.Kind != n.Kind || o.Sha256 != n.Sha256;
            if (changed != want.ContainsKey(path))
                throw new InvalidDataException("Inventory delta is incomplete or unexpected.");
            if (changed && want[path].Kind == "file" && want[path].Sha256 != n?.Sha256)
                throw new InvalidDataException("Inventory hash delta mismatch.");
        }

        if (want.Keys.Any(x => !oldSet.ContainsKey(x) && !newSet.ContainsKey(x)))
            throw new InvalidDataException("Expected inventory change is absent.");
    }

    private static bool Included(string path, string[] ex, HashSet<string> inc)
    {
        var p = Canonical(path);
        return inc.Contains(p) || !ex.Any(x => Glob(x, p));
    }

    private static bool Glob(string p, string v)
    {
        var a = p.Split('/');
        var b = v.Split('/');
        return G(a, 0, b, 0);
    }

    private static bool G(string[] a, int i, string[] b, int j)
    {
        if (i == a.Length)
            return j == b.Length;
        return a[i] == "**" ? G(a, i + 1, b, j) || j < b.Length && G(a, i, b, j + 1) : j < b.Length && (a[i] == "*" || a[i] == b[j]) && G(a, i + 1, b, j + 1);
    }

    private static TarResult ReadTar(Stream input, Member member, int maxEntries, long maxEntry, bool control, CancellationToken cancellationToken)
    {
        input.Position = member.Offset;
        using var slice = new Slice(input, member.Length);
        using var throttled = new SingleByteReadStream(slice, cancellationToken);
        using var zstd = member.Name.EndsWith(".gz", StringComparison.Ordinal) ? null : new Decompressor();
        if (zstd is not null)
            zstd.SetParameter(ZstdSharp.Unsafe.ZSTD_dParameter.ZSTD_d_windowLogMax, 23);
        using Stream decoded = member.Name.EndsWith(".gz", StringComparison.Ordinal) ? new GZipStream(throttled, CompressionMode.Decompress) : new DecompressionStream(throttled, zstd!);
        using var cancellable = new TokenCheckingStream(decoded, cancellationToken);
        using var preflight = new TarBudgetReadStream(cancellable, ExpandedLimit, maxEntry, checked(maxEntries), 16 * 1024 * 1024, 64 * 1024);
        using var tar = new TarReader(preflight, leaveOpen: true);
        var files = new List<FileRecord>();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var conffiles = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Canonical(entry.Name);
            if (!names.Add(path))
                throw new InvalidDataException("Duplicate canonical tar path.");
            var kind = entry.EntryType switch
            {
                TarEntryType.RegularFile or TarEntryType.V7RegularFile => "file",
                TarEntryType.Directory => "directory",
                TarEntryType.SymbolicLink => "symlink",
                TarEntryType.HardLink => "hardlink",
                _ => "other"
            };
            string? sha = null;
            using var bytes = new MemoryStream(control && path is "control" or "conffiles" ? 65536 : 0);
            if (entry.DataStream is not null)
            {
                using var limited = new Limited(entry.DataStream, control && path is "control" or "conffiles" ? 65536 : maxEntry);
                if (control && path is "control" or "conffiles")
                    limited.CopyTo(bytes);
                else if (kind == "file")
                {
                    using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    limited.CopyTo(new Sink(hasher));
                    sha = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                }
                else
                    limited.CopyTo(Stream.Null);
            }

            if (control && path == "control")
                ParseControl(bytes, fields);
            if (control && path == "conffiles")
                ParseConf(bytes, conffiles);
            files.Add(new(path, kind, entry.Length, sha, (int)entry.Mode, entry.Uid, entry.Gid, entry.LinkName));
        }

        preflight.Finish();
        cancellationToken.ThrowIfCancellationRequested();
        decoded.Dispose();
        if (slice.Remaining != 0)
            throw new InvalidDataException("Compressed ar member has trailing bytes.");
        return new(fields, conffiles, files, preflight.Consumed);
    }

    private static void ParseControl(MemoryStream bytes, Dictionary<string, string> fields)
    {
        if (bytes.Length > 65536)
            throw new InvalidDataException("Control cap.");
        foreach (var line in StrictUtf8.GetString(bytes.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length == 0 || line[0] is ' ' or '\t')
                continue;
            var colon = line.IndexOf(':');
            if (colon <= 0 || !fields.TryAdd(line[..colon], line[(colon + 1)..].Trim()))
                throw new InvalidDataException("Control field duplicate/malformed.");
        }
    }

    private static void ParseConf(MemoryStream bytes, List<string> conffiles)
    {
        if (bytes.Length > 65536)
            throw new InvalidDataException("Conffile cap.");
        foreach (var line in StrictUtf8.GetString(bytes.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var path = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            if (!path.StartsWith('/') || path.Contains('\\'))
                throw new InvalidDataException("Conffile path rejected.");
            var relative = path.TrimStart('/');
            if (relative.Length == 0 || relative.Split('/').Any(x => x is "" or "." or ".."))
                throw new InvalidDataException("Conffile path rejected.");
            conffiles.Add(relative);
        }
    }

    private static string Canonical(string value)
    {
        if (StrictUtf8.GetByteCount(value) > TarNameLimitBytes)
            throw new InvalidDataException("Tar name exceeds 1024 UTF-8 bytes.");
        if (value.Contains('\\'))
            throw new InvalidDataException("Backslash tar path rejected.");
        if (value.StartsWith('/'))
            throw new InvalidDataException("Absolute tar path rejected.");
        var path = value;
        while (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        if (path.Length == 0 || path.EndsWith('/'))
            path = path.TrimEnd('/');
        if (path.Length == 0)
            return ".";
        if (path.Split('/').Any(x => x is "" or "." or ".."))
            throw new InvalidDataException("Unsafe tar path: " + value);
        return path;
    }

    private static byte[] Bytes(Stream stream, int count, CancellationToken token)
    {
        var bytes = new byte[count];
        ReadExactly(stream, bytes, token);
        return bytes;
    }

    private static string Ascii(Stream stream, int count, CancellationToken token) => Encoding.ASCII.GetString(Bytes(stream, count, token));
    private static void ReadExactly(Stream stream, byte[] bytes, CancellationToken token)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
                throw new EndOfStreamException();
            offset += read;
        }
    }

    private sealed record Member(string Name, long Offset, long Length);
    private sealed record TarResult(Dictionary<string, string> Fields, List<string> Conffiles, List<FileRecord> Files, long Expanded);
    internal sealed class Slice(Stream inner, long length) : Stream
    {
        private long left = length;
        internal long Remaining => left;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (left == 0)
                return 0;
            var read = inner.Read(buffer, offset, (int)Math.Min(count, left));
            left -= read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            if (left == 0)
                return 0;
            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, left)]);
            left -= read;
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    internal sealed class SingleByteReadStream(Stream inner, CancellationToken token) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            token.ThrowIfCancellationRequested();
            var read = inner.Read(buffer, offset, Math.Min(count, 1));
            token.ThrowIfCancellationRequested();
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            var read = inner.Read(buffer[..Math.Min(buffer.Length, 1)]);
            token.ThrowIfCancellationRequested();
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    internal sealed class TokenCheckingStream(Stream inner, CancellationToken token) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            token.ThrowIfCancellationRequested();
            var read = inner.Read(buffer, offset, count);
            token.ThrowIfCancellationRequested();
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            var read = inner.Read(buffer);
            token.ThrowIfCancellationRequested();
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    internal sealed class Limited(Stream inner, long cap) : Stream
    {
        private long total;
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            total = checked(total + read);
            if (total > cap)
                throw new InvalidDataException("Entry cap.");
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            total = checked(total + read);
            if (total > cap)
                throw new InvalidDataException("Entry cap.");
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => total; set => throw new NotSupportedException(); }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    internal sealed class Sink(IncrementalHash hash) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => hash.AppendData(buffer);
        public override bool CanWrite => true;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
