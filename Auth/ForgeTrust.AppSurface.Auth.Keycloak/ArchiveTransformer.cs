using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ForgeTrust.AppSurface.Auth.Keycloak;
// Private feasibility implementation. Keep internal until the parent contract is promoted.
internal sealed record ArchiveTransformSpec(string SourcePath, string OutputPath, string ExpectedSourceSha256, ImmutableArray<ArchiveModuleSpec> ModuleSpecs, ArchiveBounds Bounds, bool AllowMultiRelease = false);
internal sealed record ArchiveModuleSpec(string Group, string Artifact, string OriginalVersion, string ReplacementVersion, string ClassPrefix, string ServiceName, string ReplacementExpectedSha256, string? Classifier = null, string? ReplacementPath = null);
internal sealed record ArchiveBounds(long MaxInputBytes = 256L * 1024 * 1024, long MaxEntryBytes = 64L * 1024 * 1024, long MaxExpandedBytes = 1024L * 1024 * 1024, long MaxOutputBytes = 1024L * 1024 * 1024, int MaxEntries = 65536, int MaxMetadataBytes = 16 * 1024 * 1024, int MaxNameBytes = 1024);
internal sealed record ArchiveResult(string SourceSha256, string OutputSha256, int EntryCount, long ExpandedBytes, bool ManifestPreserved, bool UnrelatedPayloadsPreserved);
internal static class ArchiveTransformer
{
    private const int EocdMin = 22;
    private const int EocdSearch = 65557;
    private const uint EocdSignature = 0x06054b50;
    private const uint CentralSignature = 0x02014b50;
    private const uint LocalSignature = 0x04034b50;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] AcceptedManifestAttributes = ["Manifest-Version", "Created-By", "Java-Version", "Build-Jdk-Spec"];
    internal static async Task<ArchiveResult> TransformAsync(ArchiveTransformSpec spec, CancellationToken cancellationToken = default)
    {
        ValidateSpec(spec);
        cancellationToken.ThrowIfCancellationRequested();
        var source = Path.GetFullPath(spec.SourcePath);
        var output = Path.GetFullPath(spec.OutputPath);
        Require(!File.Exists(output), "Destination must be absent");
        Require(!PathEquals(source, output), "Source/output alias");
        var sourceSha = spec.ExpectedSourceSha256;
        var privateInputs = new List<string>();
        string? temporary = null;
        try
        {
            privateInputs.Add(await CopyVerifiedAsync(source, spec.ExpectedSourceSha256, spec.Bounds, cancellationToken).ConfigureAwait(false));
            var sourceCopy = privateInputs[^1];
            foreach (var module in spec.ModuleSpecs)
            {
                var path = ResolveReplacementPath(spec, module);
                privateInputs.Add(await CopyVerifiedAsync(path, module.ReplacementExpectedSha256, spec.Bounds, cancellationToken).ConfigureAwait(false));
            }

            var original = Preflight(privateInputs[0], spec.Bounds, cancellationToken);
            var replacements = new List<ZipInventory>();
            for (var i = 0; i < spec.ModuleSpecs.Length; i++)
                replacements.Add(Preflight(privateInputs[i + 1], spec.Bounds, cancellationToken));
            using var sourceZip = ZipFile.OpenRead(sourceCopy);
            var replacementZips = replacements.Select((_, i) => ZipFile.OpenRead(privateInputs[i + 1])).ToArray();
            try
            {
                var manifest = ValidateManifest(sourceZip, spec.AllowMultiRelease, spec.Bounds, cancellationToken);
                var selected = new Dictionary<string, (ZipArchive Archive, ZipArchiveEntry Entry)>(StringComparer.Ordinal);
                var unrelated = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
                foreach (var entry in sourceZip.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.FullName.EndsWith('/'))
                        continue;
                    if (!BelongsToAny(entry.FullName, spec.ModuleSpecs, spec.AllowMultiRelease))
                        unrelated.Add(entry.FullName, entry);
                }

                for (var i = 0; i < spec.ModuleSpecs.Length; i++)
                {
                    var module = spec.ModuleSpecs[i];
                    CheckModule(sourceZip, module, original, isReplacement: false, spec.AllowMultiRelease, spec.Bounds, cancellationToken);
                    CheckModule(replacementZips[i], module, replacements[i], isReplacement: true, spec.AllowMultiRelease, spec.Bounds, cancellationToken);
                    foreach (var entry in replacementZips[i].Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Require(entry.FullName.EndsWith('/') || Belongs(entry.FullName, module, spec.AllowMultiRelease) || IsPreservedReplacementMetadata(entry.FullName, module), "Undeclared replacement payload");
                        if (!entry.FullName.EndsWith('/') && Belongs(entry.FullName, module, spec.AllowMultiRelease))
                        {
                            Require(selected.TryAdd(entry.FullName, (replacementZips[i], entry)), "Replacement entry collision");
                        }
                    }

                    ValidateDeclaredServices(replacementZips[i], spec.ModuleSpecs, spec.Bounds, cancellationToken);
                }

                Require(selected.Count > 0, "No selected replacement payload");
                var entries = unrelated.Select(p => (p.Key, Archive: sourceZip, Entry: p.Value)).Concat(selected.Select(p => (p.Key, p.Value.Archive, p.Value.Entry))).OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
                temporary = CreateTemporaryPath(output);
                long expanded = 0;
                using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.SequentialScan))
                {
                    using var boundedDestination = new BoundedSeekableWriteStream(destination, spec.Bounds.MaxOutputBytes);
                    using (var archive = new ZipArchive(boundedDestination, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8))
                    {
                        foreach (var item in entries)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var created = archive.CreateEntry(item.Key, CompressionLevel.SmallestSize);
                            created.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                            created.ExternalAttributes = unchecked((int)0x81a40000);
                            (expanded, _) = await CopyEntryAsync(item.Entry, created, spec.Bounds, expanded, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    Require(destination.Length <= spec.Bounds.MaxOutputBytes, "Output budget exceeded");
                    destination.Flush(flushToDisk: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                var written = Preflight(temporary, spec.Bounds, cancellationToken);
                using var check = ZipFile.OpenRead(temporary);
                Require(ReadSmall(check.GetEntry("META-INF/MANIFEST.MF")!, spec.Bounds, cancellationToken).AsSpan().SequenceEqual(manifest), "Manifest changed");
                foreach (var module in spec.ModuleSpecs)
                    CheckModule(check, module with { OriginalVersion = module.ReplacementVersion }, written, true, spec.AllowMultiRelease, spec.Bounds, cancellationToken);
                VerifyInventory(sourceZip, check, unrelated.Keys, spec.Bounds, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var verifiedOutputHash = await HashFileAsync(temporary, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                AtomicFilePublication.Publish(temporary, output);
                temporary = null;
                return new ArchiveResult(sourceSha, verifiedOutputHash, entries.Length, expanded, true, true);
            }
            finally
            {
                foreach (var zip in replacementZips)
                    zip.Dispose();
            }
        }
        finally
        {
            if (temporary is not null)
                TryDelete(temporary);
            foreach (var path in privateInputs)
                TryDelete(path);
        }
    }

    // The reviewed Jackson jars contain their own build manifest and licensing notices.
    // The source CLI manifest/notices remain authoritative; these exact metadata roles
    // are validated inputs, rather than executable/resources silently discarded by prefix.
    private static bool IsPreservedReplacementMetadata(string name, ArchiveModuleSpec module) => name is "META-INF/MANIFEST.MF" or "META-INF/LICENSE" or "META-INF/NOTICE" or "META-INF/versions/9/module-info.class" || (module.Artifact == "jackson-core" && name is "META-INF/FastDoubleParser-LICENSE" or "META-INF/FastDoubleParser-ThirdParty-LICENSE" or "META-INF/Schubfach-LICENSE");
    private static void ValidateSpec(ArchiveTransformSpec spec)
    {
        Require(spec.ModuleSpecs.Length > 0 && spec.ModuleSpecs.Length <= 64, "Module specification count unsupported");
        Require(spec.Bounds.MaxEntries is > 0 and <= 65536 && spec.Bounds.MaxNameBytes is > 0 and <= 1024 && spec.Bounds.MaxInputBytes is > 0 and <= 256L * 1024 * 1024 && spec.Bounds.MaxEntryBytes is > 0 and <= 64L * 1024 * 1024 && spec.Bounds.MaxExpandedBytes is > 0 and <= 1024L * 1024 * 1024 && spec.Bounds.MaxOutputBytes is > 0 and <= 1024L * 1024 * 1024 && spec.Bounds.MaxMetadataBytes is > 0 and <= 16 * 1024 * 1024, "Archive bounds unsupported");
        Require(IsSha256(spec.ExpectedSourceSha256), "Source digest must be SHA-256");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in spec.ModuleSpecs)
        {
            Require(!string.IsNullOrWhiteSpace(module.Group) && !string.IsNullOrWhiteSpace(module.Artifact), "Module coordinate missing");
            Require(IsSafeCoordinate(module.Group) && IsSafeCoordinate(module.Artifact), "Module coordinate unsafe");
            Require(IsSafeVersion(module.OriginalVersion) && IsSafeVersion(module.ReplacementVersion), "Module version unsafe");
            Require(module.ClassPrefix.EndsWith('/') && module.ClassPrefix.IndexOfAny(['\\', '\r', '\n']) < 0, "Class prefix unsafe");
            Require(names.Add(module.Group + ":" + module.Artifact + ":" + module.Classifier), "Duplicate module coordinate");
            Require(IsSha256(module.ReplacementExpectedSha256), "Replacement digest must be SHA-256");
        }
    }

    private static string ResolveReplacementPath(ArchiveTransformSpec spec, ArchiveModuleSpec module)
    {
        // The draft contract keeps paths internal; this profile maps coordinates to sibling files only.
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(spec.SourcePath))!;
        var file = module.ReplacementPath ?? (module.Artifact + "-" + module.ReplacementVersion + (string.IsNullOrEmpty(module.Classifier) ? "" : "-" + module.Classifier) + ".jar");
        var path = Path.IsPathRooted(file) ? Path.GetFullPath(file) : Path.GetFullPath(Path.Combine(baseDir, file));
        Require(module.ReplacementPath is null || Path.IsPathRooted(file) || path.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Replacement path escaped source context");
        Require(File.Exists(path), "Replacement archive missing");
        return path;
    }

    // Read-only metadata inspection accepts existing upstream signatures without modifying
    // them. Archive transformation still rejects signatures in both source and replacements.
    internal static void ValidateReadOnlyInput(string path, CancellationToken token) => _ = Preflight(path, new ArchiveBounds(), token, allowSignatures: true);
    private static ZipInventory Preflight(string path, ArchiveBounds bounds, CancellationToken token, bool allowSignatures = false)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        Require(file.Length >= EocdMin && file.Length <= bounds.MaxInputBytes, "Compressed input budget exceeded or truncated");
        var trailerLength = (int)Math.Min(file.Length, EocdSearch);
        var trailer = new byte[trailerLength];
        file.Position = file.Length - trailerLength;
        file.ReadExactly(trailer);
        var eocd = -1;
        for (var i = trailer.Length - EocdMin; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(trailer.AsSpan(i, 4)) == EocdSignature && i + EocdMin + BinaryPrimitives.ReadUInt16LittleEndian(trailer.AsSpan(i + 20, 2)) == trailer.Length)
            {
                eocd = i;
                break;
            }

        Require(eocd >= 0, "EOCD missing");
        var end = trailer.AsSpan(eocd);
        Require(BinaryPrimitives.ReadUInt16LittleEndian(end[4..]) == 0 && BinaryPrimitives.ReadUInt16LittleEndian(end[6..]) == 0, "Split archive unsupported");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
        Require(count > 0 && count <= bounds.MaxEntries && size <= bounds.MaxMetadataBytes && (long)offset + size == file.Length - trailerLength + eocd, "Central directory budget/shape unsupported");
        file.Position = offset;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<ZipItem>(count);
        long expanded = 0;
        var metadata = 0L;
        Span<byte> header = stackalloc byte[46];
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            file.ReadExactly(header);
            Require(BinaryPrimitives.ReadUInt32LittleEndian(header) == CentralSignature, "Central header invalid");
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
            var method = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
            var compressed = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
            var extra = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);
            var comment = BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
            var localOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[42..]);
            Require((flags & 1) == 0 && method is 0 or 8, "Encryption/unsupported compression");
            Require(nameLength > 0 && nameLength <= bounds.MaxNameBytes && length <= bounds.MaxEntryBytes, "Entry budget exceeded");
            var bytes = new byte[nameLength];
            file.ReadExactly(bytes);
            var name = StrictUtf8.GetString(bytes);
            ValidateName(name, bounds);
            Require(names.Add(name), "Duplicate/aliased entry");
            Require(allowSignatures || !IsSignature(name), "Signed archive unsupported");
            metadata = checked(metadata + nameLength + extra + comment + 46);
            Require(metadata <= bounds.MaxMetadataBytes && file.Position + extra + comment <= (long)offset + size, "Metadata budget exceeded");
            expanded = checked(expanded + length);
            Require(expanded <= bounds.MaxExpandedBytes, "Expanded budget exceeded");
            ValidateLocalHeader(file, localOffset, name, method, compressed, length, crc, token);
            file.Position = checked(file.Position + extra + comment);
            items.Add(new ZipItem(name, length, localOffset));
        }

        Require(file.Position == (long)offset + size, "Central directory length mismatch");
        return new ZipInventory(items, expanded, file.Length);
    }

    private static void ValidateLocalHeader(FileStream file, uint offset, string name, ushort method, uint compressed, uint length, uint crc, CancellationToken token)
    {
        var save = file.Position;
        file.Position = offset;
        Span<byte> h = stackalloc byte[30];
        file.ReadExactly(h);
        Require(BinaryPrimitives.ReadUInt32LittleEndian(h) == LocalSignature, "Local header invalid");
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(h[6..]);
        Require((flags & 1) == 0, "Local encryption unsupported");
        var descriptor = (flags & 0x0008) != 0;
        Require(BinaryPrimitives.ReadUInt16LittleEndian(h[8..]) == method, "Local compression mismatch");
        Require((descriptor && BinaryPrimitives.ReadUInt32LittleEndian(h[14..]) == 0 && BinaryPrimitives.ReadUInt32LittleEndian(h[18..]) == 0 && BinaryPrimitives.ReadUInt32LittleEndian(h[22..]) == 0) || (BinaryPrimitives.ReadUInt32LittleEndian(h[14..]) == crc && BinaryPrimitives.ReadUInt32LittleEndian(h[18..]) == compressed && BinaryPrimitives.ReadUInt32LittleEndian(h[22..]) == length), "Local header inconsistent");
        var nl = BinaryPrimitives.ReadUInt16LittleEndian(h[26..]);
        var el = BinaryPrimitives.ReadUInt16LittleEndian(h[28..]);
        Require(nl == StrictUtf8.GetByteCount(name), "Local name length mismatch");
        var bytes = new byte[nl];
        file.ReadExactly(bytes);
        Require(StrictUtf8.GetString(bytes) == name, "Local name mismatch");
        file.Position = save;
        token.ThrowIfCancellationRequested();
    }

    private static byte[] ValidateManifest(ZipArchive archive, bool allowMultiRelease, ArchiveBounds bounds, CancellationToken token)
    {
        var entry = archive.GetEntry("META-INF/MANIFEST.MF");
        Require(entry is not null, "Manifest missing");
        var bytes = ReadSmall(entry!, bounds, token);
        var text = StrictUtf8.GetString(bytes);
        var logical = new List<string>();
        var current = new StringBuilder();
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            token.ThrowIfCancellationRequested();
            if (line.StartsWith(' '))
            {
                Require(current.Length > 0, "Manifest continuation without attribute");
                current.Append(line.AsSpan(1));
            }
            else
            {
                if (current.Length > 0)
                    logical.Add(current.ToString());
                current.Clear();
                current.Append(line);
            }
        }

        if (current.Length > 0)
            logical.Add(current.ToString());
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in logical)
        {
            var colon = item.IndexOf(':');
            Require(colon > 0 && colon + 1 < item.Length && item[colon + 1] == ' ', "Malformed manifest attribute");
            var key = item[..colon];
            Require(seen.Add(key), "Duplicate manifest attribute");
            if (!AcceptedManifestAttributes.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                Require(key.Equals("Multi-Release", StringComparison.OrdinalIgnoreCase) && allowMultiRelease && item[(colon + 1)..].Trim().Equals("true", StringComparison.OrdinalIgnoreCase), "Unsupported manifest attribute");
            }
        }

        return bytes;
    }

    private static void CheckModule(ZipArchive archive, ArchiveModuleSpec module, ZipInventory inventory, bool isReplacement, bool allowMultiRelease, ArchiveBounds bounds, CancellationToken token)
    {
        var prefix = "META-INF/maven/" + module.Group + "/" + module.Artifact + "/";
        var propsName = prefix + "pom.properties";
        var props = archive.GetEntry(propsName);
        Require(props is not null, "Maven metadata missing");
        var fields = ParseProperties(StrictUtf8.GetString(ReadSmall(props!, bounds, token)));
        Require(fields.Count == 3 && fields.TryGetValue("groupId", out var group) && group == module.Group && fields.TryGetValue("artifactId", out var artifact) && artifact == module.Artifact && fields.TryGetValue("version", out var version) && version == (isReplacement ? module.ReplacementVersion : module.OriginalVersion), "Maven provenance mismatch");
        var matched = 0;
        var service = "META-INF/services/" + module.ServiceName;
        var providerPrefix = module.ClassPrefix.Replace('/', '.');
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/'))
                continue;
            if (Belongs(entry.FullName, module, allowMultiRelease))
                matched++;
            if (entry.FullName.Equals(service, StringComparison.Ordinal))
            {
                var content = StrictUtf8.GetString(ReadSmall(entry, bounds, token));
                Require(content.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).All(x => x.StartsWith(providerPrefix, StringComparison.Ordinal)), "Undeclared service provider");
            }
        }

        Require(matched > 0, "Module payload missing");
    }

    private static void ValidateDeclaredServices(ZipArchive archive, ImmutableArray<ArchiveModuleSpec> modules, ArchiveBounds bounds, CancellationToken token)
    {
        var allowed = modules.Select(x => "META-INF/services/" + x.ServiceName).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in archive.Entries.Where(x => x.FullName.StartsWith("META-INF/services/", StringComparison.Ordinal) && !x.FullName.EndsWith('/')))
        {
            token.ThrowIfCancellationRequested();
            Require(allowed.Contains(entry.FullName), "Undeclared service");
            var module = modules.First(x => ("META-INF/services/" + x.ServiceName).Equals(entry.FullName, StringComparison.Ordinal));
            var prefix = module.ClassPrefix.Replace('/', '.');
            var content = StrictUtf8.GetString(ReadSmall(entry, bounds, token));
            Require(content.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).All(x => x.StartsWith(prefix, StringComparison.Ordinal)), "Undeclared service provider");
        }
    }

    private static Dictionary<string, string> ParseProperties(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var eq = line.IndexOf('=');
            Require(eq > 0 && result.TryAdd(line[..eq], line[(eq + 1)..]), "Malformed Maven properties");
        }

        return result;
    }

    private static bool Belongs(string name, ArchiveModuleSpec module, bool allowMultiRelease)
    {
        var runtime = name;
        if (runtime.StartsWith("META-INF/versions/", StringComparison.Ordinal))
        {
            Require(allowMultiRelease, "Multi-release payload unsupported");
            var slash = runtime.IndexOf('/', 18);
            Require(slash > 18 && int.TryParse(runtime[18..slash], out var version) && version > 0, "Malformed multi-release path");
            runtime = runtime[(slash + 1)..];
        }

        return runtime.StartsWith(module.ClassPrefix, StringComparison.Ordinal) || name.StartsWith("META-INF/maven/" + module.Group + "/" + module.Artifact + "/", StringComparison.Ordinal) || name == "META-INF/services/" + module.ServiceName;
    }

    private static bool BelongsToAny(string name, ImmutableArray<ArchiveModuleSpec> modules, bool allow) => modules.Any(m => Belongs(name, m, allow));
    private static bool IsSignature(string name) => name.StartsWith("META-INF/SIG-", StringComparison.OrdinalIgnoreCase) || (name.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) && (name.EndsWith(".SF", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".RSA", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".DSA", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".EC", StringComparison.OrdinalIgnoreCase)));
    private static void ValidateName(string name, ArchiveBounds bounds)
    {
        var parts = name.Split('/');
        var last = name.EndsWith('/') ? parts.Length - 1 : parts.Length;
        Require(!name.StartsWith('/') && !name.Contains('\\') && !name.Any(char.IsControl) && parts[..last].All(x => x.Length > 0 && x is not "." and not ".."), "Unsafe entry name");
        Require(name.Normalize(NormalizationForm.FormC) == name, "Noncanonical Unicode name");
    }

    private static async Task<(long Expanded, long Length)> CopyEntryAsync(ZipArchiveEntry source, ZipArchiveEntry destination, ArchiveBounds bounds, long expanded, CancellationToken token)
    {
        using var input = source.Open();
        using var output = destination.Open();
        var buffer = new byte[64 * 1024];
        long length = 0;
        uint crc = uint.MaxValue;
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            length = checked(length + read);
            expanded = checked(expanded + read);
            Require(length <= bounds.MaxEntryBytes && expanded <= bounds.MaxExpandedBytes, "Expanded budget exceeded");
            crc = UpdateCrc(crc, buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }

        Require(length == source.Length && ~crc == source.Crc32, "Entry CRC/length mismatch");
        return (expanded, length);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        }

        return crc;
    }

    private static byte[] ReadSmall(ZipArchiveEntry entry, ArchiveBounds bounds, CancellationToken token)
    {
        var cap = Math.Min(128 * 1024, bounds.MaxMetadataBytes);
        Require(entry.Length <= cap, "Metadata budget exceeded");
        using var input = entry.Open();
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        uint crc = uint.MaxValue;
        int read;
        while ((read = input.Read(buffer)) != 0)
        {
            token.ThrowIfCancellationRequested();
            Require(memory.Length + read <= cap, "Actual metadata budget exceeded");
            crc = UpdateCrc(crc, buffer.AsSpan(0, read));
            memory.Write(buffer, 0, read);
        }

        Require(memory.Length == entry.Length && ~crc == entry.Crc32, "Metadata CRC/length mismatch");
        return memory.ToArray();
    }

    private static void VerifyInventory(ZipArchive before, ZipArchive after, IEnumerable<string> names, ArchiveBounds bounds, CancellationToken token)
    {
        foreach (var name in names)
        {
            token.ThrowIfCancellationRequested();
            var a = before.GetEntry(name);
            var b = after.GetEntry(name);
            Require(a is not null && b is not null, "Unrelated entry missing");
            using var x = a!.Open();
            using var y = b!.Open();
            Require(SHA256.HashData(x).AsSpan().SequenceEqual(SHA256.HashData(y)), "Unrelated payload changed");
        }
    }

    private static async Task<string> CopyVerifiedAsync(string path, string expected, ArchiveBounds bounds, CancellationToken token)
    {
        var full = Path.GetFullPath(path);
        using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        Require(input.Length <= bounds.MaxInputBytes, "Input budget exceeded");
        var temp = Path.Combine(Path.GetDirectoryName(full)!, ".archive-owned-" + Guid.NewGuid().ToString("N"));
        var retained = false;
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    total = checked(total + read);
                    Require(total <= bounds.MaxInputBytes, "Growing input exceeds budget");
                    hash.AppendData(buffer, 0, read);
                    TemporaryDiskBudget.Account(read);
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }

                output.Flush(true);
                Require(Convert.ToHexStringLower(hash.GetHashAndReset()) == expected, "Input digest mismatch");
            }

            retained = true;
            return temp;
        }
        finally
        {
            if (!retained)
                TryDelete(temp);
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static bool PathEquals(string a, string b) => StringComparer.Ordinal.Equals(a, b) || (File.Exists(a) && File.Exists(b) && new FileInfo(a).Length == new FileInfo(b).Length && File.GetLastWriteTimeUtc(a) == File.GetLastWriteTimeUtc(b) && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase));
    private static string CreateTemporaryPath(string output) => output + ".owned-" + Guid.NewGuid().ToString("N");
    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool IsSafeCoordinate(string value) => value.Length <= 256 && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_');
    private static bool IsSafeVersion(string value) => value.Length <= 128 && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '+');
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }

    /// <summary>Deletes an engine-owned archive staging file, tolerating expected I/O and access failures.</summary>
    /// <param name="path">An engine-created staging-file path owned by the current operation.</param>
    /// <remarks>Internal visibility permits real filesystem failure tests; unexpected path or programming errors propagate.</remarks>
    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record ZipItem(string Name, long Length, long LocalOffset);
    private sealed record ZipInventory(IReadOnlyList<ZipItem> Items, long ExpandedBytes, long Length);
}

/// <summary>Seekable output guard that rejects any operation which could extend the owned file past its cap.</summary>
internal sealed class BoundedSeekableWriteStream(Stream inner, long maximumLength) : Stream
{
    private readonly Stream inner = inner;
    private readonly long maximumLength = maximumLength > 0 ? maximumLength : throw new ArgumentOutOfRangeException(nameof(maximumLength));
    private void RequirePosition(long position)
    {
        if (position < 0 || position > maximumLength)
            throw new InvalidDataException("Output budget exceeded.");
    }

    private void RequireGrowth(long count)
    {
        if (count < 0 || checked(Position + count) > maximumLength)
            throw new InvalidDataException("Output budget exceeded.");
        TemporaryDiskBudget.Account(count);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        RequireGrowth(count);
        inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        RequireGrowth(buffer.Length);
        inner.Write(buffer);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        RequireGrowth(buffer.Length);
        return inner.WriteAsync(buffer, cancellationToken);
    }

    public override void SetLength(long value)
    {
        RequirePosition(value);
        TemporaryDiskBudget.Account(Math.Max(0, value - Length));
        inner.SetLength(value);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(Position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        RequirePosition(position);
        return inner.Seek(offset, origin);
    }

    public override long Position
    {
        get => inner.Position;
        set
        {
            RequirePosition(value);
            inner.Position = value;
        }
    }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }
}
