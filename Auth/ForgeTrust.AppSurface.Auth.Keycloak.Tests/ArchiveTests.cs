using System.Collections.Immutable;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

/// <summary>Protects archive provenance, CRC, manifest, replacement, and atomic-output invariants; fails on undeclared or corrupted archive content; claims exact transformed bytes and typed rejection.</summary>
public sealed class ArchiveTests
{
    [Fact]
    public async Task Transforms_module_while_preserving_manifest_and_unrelated_entries()
    {
        using var root = new TempDirectory();
        var source = Path.Combine(root.Path, "source.jar"); var replacement = Path.Combine(root.Path, "jackson-core-2.jar"); var output = Path.Combine(root.Path, "out.jar");
        WriteJar(source, "1", "old"); WriteJar(replacement, "2", "new", includeUnrelated: false);
        var result = await ArchiveTransformer.TransformAsync(new ArchiveTransformSpec(source, output, Hash(source), [new("com.example", "jackson-core", "1", "2", "com/example/", "example", Hash(replacement))], new()));
        Assert.Equal(Hash(source), result.SourceSha256); Assert.Equal(Hash(output), result.OutputSha256); Assert.True(result.ManifestPreserved); Assert.True(result.UnrelatedPayloadsPreserved);
        using var zip = ZipFile.OpenRead(output); Assert.Equal("new", Read(zip, "com/example/Main.class")); Assert.Equal("keep", Read(zip, "unrelated.txt"));
    }

    [Fact]
    public async Task Rejects_signature_entry_limits_cancellation_and_existing_output_atomically()
    {
        using var root = new TempDirectory(); var source = Path.Combine(root.Path, "source.jar"); var replacement = Path.Combine(root.Path, "jackson-core-2.jar");
        WriteJar(source, "1", "old", signed: true); WriteJar(replacement, "2", "new", includeUnrelated: false);
        var spec = new ArchiveTransformSpec(source, Path.Combine(root.Path, "out.jar"), Hash(source), [new("com.example", "jackson-core", "1", "2", "com/example/", "example", Hash(replacement))], new());
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveTransformer.TransformAsync(spec));
        WriteJar(source, "1", "old"); var existing = Path.Combine(root.Path, "existing.jar"); File.WriteAllText(existing, "sentinel");
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveTransformer.TransformAsync(spec with { OutputPath = existing })); Assert.Equal("sentinel", File.ReadAllText(existing));
        using var c = new CancellationTokenSource(); c.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => ArchiveTransformer.TransformAsync(spec with { OutputPath = Path.Combine(root.Path, "cancel.jar") }, c.Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveTransformer.TransformAsync(spec with { OutputPath = Path.Combine(root.Path, "limited.jar"), Bounds = new ArchiveBounds(MaxEntryBytes: 2) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveTransformer.TransformAsync(spec with { OutputPath = Path.Combine(root.Path, "output-limited.jar"), Bounds = new ArchiveBounds(MaxOutputBytes: 64) }));
        Assert.False(File.Exists(Path.Combine(root.Path, "output-limited.jar")));
    }

    [Fact]
    public async Task Bounded_output_stream_rejects_growth_without_mutating_underlying_length()
    {
        using var inner = new MemoryStream(); using var bounded = new BoundedSeekableWriteStream(inner, 4); bounded.Write([1, 2, 3, 4]); Assert.Equal(4, inner.Length);
        await Assert.ThrowsAsync<InvalidDataException>(() => bounded.WriteAsync(new byte[] { 5 }, CancellationToken.None).AsTask()); Assert.Equal(4, inner.Length);
        Assert.Throws<InvalidDataException>(() => bounded.SetLength(5)); Assert.Equal(4, inner.Length); Assert.Throws<InvalidDataException>(() => bounded.Seek(5, SeekOrigin.Begin)); Assert.Equal(4, inner.Length);
    }

    [Fact]
    public async Task Two_modules_use_independent_replacement_hashes_and_reject_manifest_duplicates_collisions_and_bad_crc()
    {
        using var root = new TempDirectory(); var source = Path.Combine(root.Path, "source.jar"); var first = Path.Combine(root.Path, "jackson-core-2.jar"); var second = Path.Combine(root.Path, "other-2.jar");
        WriteJar(source, "1", "old"); CreateModuleJar(first, "jackson-core", "2", "com/example/", "example", "new"); CreateModuleJar(second, "other", "2", "com/other/", "other", "new-other"); AddModule(source, "other", "1", "com/other/"); AddEntry(source, "com/other/Main.class", "old-other");
        var output = Path.Combine(root.Path, "two.jar"); var modules = ImmutableArray.Create(new ArchiveModuleSpec("com.example", "jackson-core", "1", "2", "com/example/", "example", Hash(first)), new("com.example", "other", "1", "2", "com/other/", "other", Hash(second), ReplacementPath: second));
        var result = await ArchiveTransformer.TransformAsync(new(source, output, Hash(source), modules, new()));
        Assert.Equal(Hash(source), result.SourceSha256); using var zip = ZipFile.OpenRead(output); Assert.Equal("new-other", Read(zip, "com/other/Main.class"));

        var duplicate = Path.Combine(root.Path, "duplicate.jar"); WriteJar(duplicate, "1", "old", manifest: "Manifest-Version: 1.0\r\nCreated-By: one\r\nCreated-By: two\r\n");
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveTransformer.TransformAsync(new(duplicate, Path.Combine(root.Path, "dup-out.jar"), Hash(duplicate), [modules[0]], new())));
        var collision = Path.Combine(root.Path, "collision.jar"); WriteJar(collision, "1", "old"); AddEntry(collision, "com/example/Main.class", "second");
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveTransformer.TransformAsync(new(collision, Path.Combine(root.Path, "collision-out.jar"), Hash(collision), [modules[0]], new())));
        var corrupt = Path.Combine(root.Path, "corrupt.jar"); WriteJar(corrupt, "1", "old"); CorruptEntry(corrupt, "unrelated.txt");
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveTransformer.TransformAsync(new(corrupt, Path.Combine(root.Path, "crc-out.jar"), Hash(corrupt), [modules[0]], new())));
        var localMismatch = Path.Combine(root.Path, "local-mismatch.jar"); WriteJar(localMismatch, "1", "old"); CorruptLocalHeaderOnly(localMismatch, "unrelated.txt");
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveTransformer.TransformAsync(new(localMismatch, Path.Combine(root.Path, "local-mismatch-out.jar"), Hash(localMismatch), [modules[0]], new())));
    }

    [Fact]
    public async Task Accepts_populated_and_zero_local_values_with_genuine_data_descriptors()
    {
        using var root = new TempDirectory(); var replacement = Path.Combine(root.Path, "jackson-core-2.jar"); WriteJar(replacement, "2", "new", includeUnrelated: false);
        foreach (var zeroLocalValues in new[] { false, true })
        {
            var source = Path.Combine(root.Path, zeroLocalValues ? "source-zero.jar" : "source-populated.jar"); WriteDescriptorJar(source, zeroLocalValues);
            var output = Path.Combine(root.Path, zeroLocalValues ? "out-zero.jar" : "out-populated.jar");
            var result = await ArchiveTransformer.TransformAsync(new(source, output, Hash(source), [new("com.example", "jackson-core", "1", "2", "com/example/", "example", Hash(replacement))], new()));
            Assert.Equal(Hash(source), result.SourceSha256); using var zip = ZipFile.OpenRead(output); Assert.Equal("new", Read(zip, "com/example/Main.class"));
        }
    }

    [Fact]
    public async Task ValidateSpec_rejects_each_widened_or_invalid_bound_before_reading_inputs()
    {
        using var root = new TempDirectory(); var source = Path.Combine(root.Path, "source.jar"); var replacement = Path.Combine(root.Path, "jackson-core-2.jar"); WriteJar(source, "1", "old"); WriteJar(replacement, "2", "new", includeUnrelated: false); var module = new ArchiveModuleSpec("com.example", "jackson-core", "1", "2", "com/example/", "example", Hash(replacement));
        foreach (var bounds in new[] { new ArchiveBounds(MaxEntries: 0), new ArchiveBounds(MaxNameBytes: 1025), new ArchiveBounds(MaxInputBytes: 256L * 1024 * 1024 + 1), new ArchiveBounds(MaxEntryBytes: 64L * 1024 * 1024 + 1), new ArchiveBounds(MaxExpandedBytes: 1024L * 1024 * 1024 + 1), new ArchiveBounds(MaxOutputBytes: 1024L * 1024 * 1024 + 1), new ArchiveBounds(MaxMetadataBytes: 16 * 1024 * 1024 + 1) })
            await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveTransformer.TransformAsync(new(source, Path.Combine(root.Path, Guid.NewGuid() + ".jar"), Hash(source), [module], bounds)));
    }

    [Fact]
    public async Task Archive_rejects_unsupported_manifest_path_alias_and_zip_header_shapes()
    {
        using var root = new TempDirectory(); var replacement = Path.Combine(root.Path, "jackson-core-2.jar"); WriteJar(replacement, "2", "new", includeUnrelated: false); var module = new ArchiveModuleSpec("com.example", "jackson-core", "1", "2", "com/example/", "example", Hash(replacement));
        var unsupported = Path.Combine(root.Path, "unsupported.jar"); WriteJar(unsupported, "1", "old", manifest: "Manifest-Version: 1.0\r\nX-Unsupported: true\r\n"); await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveTransformer.TransformAsync(new(unsupported, Path.Combine(root.Path, "unsupported-out.jar"), Hash(unsupported), [module], new())));
        var alias = Path.Combine(root.Path, "alias.jar"); WriteJar(alias, "1", "old"); AddEntry(alias, "./alias.txt", "bad"); await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveTransformer.TransformAsync(new(alias, Path.Combine(root.Path, "alias-out.jar"), Hash(alias), [module], new())));
        var header = Path.Combine(root.Path, "header.jar"); WriteJar(header, "1", "old"); CorruptLocalMethod(header, "unrelated.txt"); await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveTransformer.TransformAsync(new(header, Path.Combine(root.Path, "header-out.jar"), Hash(header), [module], new())));
    }

    [Fact]
    public async Task ValidateSpec_rejects_empty_or_oversized_module_sets_before_opening_files()
    {
        using var root = new TempDirectory();
        var source = Path.Combine(root.Path, "source.jar"); WriteJar(source, "1", "old");
        var replacement = Path.Combine(root.Path, "jackson-core-2.jar"); WriteJar(replacement, "2", "new", includeUnrelated: false);
        var module = new ArchiveModuleSpec("com.example", "jackson-core", "1", "2", "com/example/", "example", Hash(replacement));
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveTransformer.TransformAsync(new(source, Path.Combine(root.Path, "empty.jar"), Hash(source), [], new())));
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveTransformer.TransformAsync(new(source, Path.Combine(root.Path, "many.jar"), Hash(source), Enumerable.Repeat(module, 65).ToImmutableArray(), new())));
    }

    [Fact]
    public async Task ValidateSpec_rejects_unsafe_coordinates_versions_prefixes_and_digests()
    {
        using var root = new TempDirectory();
        var source = Path.Combine(root.Path, "source.jar"); WriteJar(source, "1", "old");
        var replacement = Path.Combine(root.Path, "jackson-core-2.jar"); WriteJar(replacement, "2", "new", includeUnrelated: false);
        var valid = new ArchiveModuleSpec("com.example", "jackson-core", "1", "2", "com/example/", "example", Hash(replacement));
        var cases = new[]
        {
            valid with { Group = "../escape" }, valid with { Artifact = "bad/name" }, valid with { OriginalVersion = "1/unsafe" },
            valid with { ClassPrefix = "com\\example/" }, valid with { ReplacementExpectedSha256 = new string('0', 64 - 1) }
        };
        foreach (var item in cases)
            await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveTransformer.TransformAsync(new(source, Path.Combine(root.Path, Guid.NewGuid() + ".jar"), Hash(source), [item], new())));
    }

    [Fact]
    public async Task ValidateSpec_rejects_duplicate_coordinates_and_replacement_escape()
    {
        using var root = new TempDirectory();
        var source = Path.Combine(root.Path, "source.jar"); WriteJar(source, "1", "old");
        var replacement = Path.Combine(root.Path, "jackson-core-2.jar"); WriteJar(replacement, "2", "new", includeUnrelated: false);
        var module = new ArchiveModuleSpec("com.example", "jackson-core", "1", "2", "com/example/", "example", Hash(replacement));
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveTransformer.TransformAsync(new(source, Path.Combine(root.Path, "duplicate.jar"), Hash(source), [module, module], new())));
        var escaped = module with { ReplacementPath = "../outside.jar" };
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveTransformer.TransformAsync(new(source, Path.Combine(root.Path, "escaped.jar"), Hash(source), [escaped], new())));
    }

    [Theory]
    [InlineData("META-INF/LICENSE")]
    [InlineData("META-INF/NOTICE")]
    [InlineData("META-INF/versions/9/module-info.class")]
    [InlineData("META-INF/FastDoubleParser-LICENSE")]
    [InlineData("META-INF/FastDoubleParser-ThirdParty-LICENSE")]
    [InlineData("META-INF/Schubfach-LICENSE")]
    public async Task ReviewedReplacementMetadataRolesPreserveSourceManifestAndNotices(string metadata)
    {
        using var root = new TempDirectory();
        var source = Path.Combine(root.Path, "source.jar"); var replacement = Path.Combine(root.Path, "jackson-core-2.jar");
        WriteJar(source, "1", "old"); WriteJar(replacement, "2", "new", includeUnrelated: false);
        AddEntry(replacement, metadata, "reviewed-metadata");
        var result = await ArchiveTransformer.TransformAsync(new(source, Path.Combine(root.Path, "out.jar"), Hash(source), [new("com.example", "jackson-core", "1", "2", "com/example/", "example", Hash(replacement))], new(), AllowMultiRelease: true));
        Assert.True(result.ManifestPreserved);
        using var archive = ZipFile.OpenRead(Path.Combine(root.Path, "out.jar"));
        Assert.Equal("Manifest-Version: 1.0\r\n", Read(archive, "META-INF/MANIFEST.MF"));
        Assert.Null(archive.GetEntry(metadata));
        Assert.Equal("new", Read(archive, "com/example/Main.class"));
    }

    [Theory]
    [InlineData("zero-entries")]
    [InlineData("many-entries")]
    [InlineData("zero-name")]
    [InlineData("zero-input")]
    [InlineData("zero-entry")]
    [InlineData("zero-expanded")]
    [InlineData("zero-output")]
    [InlineData("zero-metadata")]
    [InlineData("blank-group")]
    [InlineData("blank-artifact")]
    [InlineData("long-group")]
    [InlineData("long-version")]
    [InlineData("bad-new-version")]
    [InlineData("short-prefix")]
    [InlineData("newline-prefix")]
    [InlineData("source-hash")]
    public async Task UnsupportedArchiveEnvelopeFailsBeforeInputAcquisition(string mutation)
    {
        var module = new ArchiveModuleSpec("com.example", "jackson-core", "1", "2", "com/example/", "example", new string('a', 64));
        var bounds = new ArchiveBounds(); var hash = new string('b', 64);
        switch (mutation)
        {
            case "zero-entries": bounds = bounds with { MaxEntries = 0 }; break;
            case "many-entries": bounds = bounds with { MaxEntries = 65537 }; break;
            case "zero-name": bounds = bounds with { MaxNameBytes = 0 }; break;
            case "zero-input": bounds = bounds with { MaxInputBytes = 0 }; break;
            case "zero-entry": bounds = bounds with { MaxEntryBytes = 0 }; break;
            case "zero-expanded": bounds = bounds with { MaxExpandedBytes = 0 }; break;
            case "zero-output": bounds = bounds with { MaxOutputBytes = 0 }; break;
            case "zero-metadata": bounds = bounds with { MaxMetadataBytes = 0 }; break;
            case "blank-group": module = module with { Group = "" }; break;
            case "blank-artifact": module = module with { Artifact = "" }; break;
            case "long-group": module = module with { Group = new string('a', 257) }; break;
            case "long-version": module = module with { OriginalVersion = new string('a', 129) }; break;
            case "bad-new-version": module = module with { ReplacementVersion = "2/bad" }; break;
            case "short-prefix": module = module with { ClassPrefix = "com/example" }; break;
            case "newline-prefix": module = module with { ClassPrefix = "com/\n/" }; break;
            default: hash = "bad"; break;
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveTransformer.TransformAsync(new("never-opened", "never-written", hash, [module], bounds)));
    }

    private static void WriteJar(string path, string version, string payload, bool signed = false, string? manifest = null, bool includeUnrelated = true)
    { if (File.Exists(path)) File.Delete(path); using var zip = ZipFile.Open(path, ZipArchiveMode.Create); Add(zip, "META-INF/MANIFEST.MF", manifest ?? "Manifest-Version: 1.0\r\n"); Add(zip, "META-INF/maven/com.example/jackson-core/pom.properties", $"groupId=com.example\nartifactId=jackson-core\nversion={version}\n"); Add(zip, "com/example/Main.class", payload); Add(zip, "META-INF/services/example", "com.example.Main\n"); if (includeUnrelated) AddStored(zip, "unrelated.txt", "keep"); if (signed) Add(zip, "META-INF/EVIL.SF", "signature"); }
    private static void AddModule(string path, string artifact, string version, string prefix) { using var zip = ZipFile.Open(path, ZipArchiveMode.Update); Add(zip, $"META-INF/maven/com.example/{artifact}/pom.properties", $"groupId=com.example\nartifactId={artifact}\nversion={version}\n"); Add(zip, $"META-INF/services/{artifact}", prefix.Replace('/', '.') + "Main\n"); }
    private static void CreateModuleJar(string path, string artifact, string version, string prefix, string service, string payload) { using var zip = ZipFile.Open(path, ZipArchiveMode.Create); Add(zip, "META-INF/MANIFEST.MF", "Manifest-Version: 1.0\r\n"); Add(zip, $"META-INF/maven/com.example/{artifact}/pom.properties", $"groupId=com.example\nartifactId={artifact}\nversion={version}\n"); Add(zip, prefix + "Main.class", payload); Add(zip, $"META-INF/services/{service}", prefix.Replace('/', '.') + "Main\n"); }
    private static void AddEntry(string path, string name, string text) { using var zip = ZipFile.Open(path, ZipArchiveMode.Update); Add(zip, name, text); }
    private static void Add(ZipArchive zip, string name, string text) { using var s = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false), leaveOpen: false); s.Write(text); }
    private static void AddStored(ZipArchive zip, string name, string text) { using var s = new StreamWriter(zip.CreateEntry(name, CompressionLevel.NoCompression).Open(), new UTF8Encoding(false), leaveOpen: false); s.Write(text); }
    private static string Read(ZipArchive zip, string name) { using var reader = new StreamReader(zip.GetEntry(name)!.Open()); return reader.ReadToEnd(); }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void CorruptEntry(string path, string wanted)
    {
        var bytes = File.ReadAllBytes(path); var found = false;
        for (var i = 0; i + 30 < bytes.Length; i++) if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4)) == 0x04034b50) { var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 26, 2)); var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 28, 2)); if (Encoding.UTF8.GetString(bytes, i + 30, nameLength) == wanted) { var crc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i + 14, 4)) ^ 1u; BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 14, 4), crc); found = true; break; } }
        var centralChanged = false; for (var i = 0; found && i + 46 < bytes.Length; i++) if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4)) == 0x02014b50) { var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 28, 2)); if (Encoding.UTF8.GetString(bytes, i + 46, nameLength) == wanted) { var crc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i + 16, 4)); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 16, 4), crc ^ 1u); centralChanged = true; break; } }
        if (!found) throw new InvalidOperationException("Entry not found.");
        Assert.True(centralChanged);
        var payloadChanged = false; for (var i = 0; i + 30 < bytes.Length; i++) if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4)) == 0x04034b50) { var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 26, 2)); var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 28, 2)); if (Encoding.UTF8.GetString(bytes, i + 30, nameLength) == wanted) { bytes[i + 30 + nameLength + extraLength] ^= 0x01; payloadChanged = true; break; } }
        Assert.True(payloadChanged);
        File.WriteAllBytes(path, bytes);
    }
    private static void CorruptLocalHeaderOnly(string path, string wanted)
    {
        var bytes = File.ReadAllBytes(path);
        for (var i = 0; i + 30 < bytes.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4)) != 0x04034b50) continue;
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 26, 2));
            if (Encoding.UTF8.GetString(bytes, i + 30, nameLength) != wanted) continue;
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i + 14, 4));
            var replacement = crc ^ 1u; Assert.NotEqual(crc, replacement); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 14, 4), replacement);
            File.WriteAllBytes(path, bytes);
            return;
        }
        throw new InvalidOperationException("Entry not found.");
    }
    private static void CorruptLocalMethod(string path, string wanted)
    {
        var bytes = File.ReadAllBytes(path); for (var i = 0; i + 30 < bytes.Length; i++) if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4)) == 0x04034b50) { var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 26, 2)); if (Encoding.UTF8.GetString(bytes, i + 30, nameLength) == wanted) { BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i + 8, 2), 99); File.WriteAllBytes(path, bytes); return; } } throw new InvalidOperationException("Entry not found.");
    }
    private static void WriteDescriptorJar(string path, bool zeroLocalValues)
    {
        var entries = new[] { ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\r\n"), ("META-INF/maven/com.example/jackson-core/pom.properties", "groupId=com.example\nartifactId=jackson-core\nversion=1\n"), ("com/example/Main.class", "old"), ("META-INF/services/example", "com.example.Main\n"), ("unrelated.txt", "keep") };
        var bytes = new List<byte>(); var central = new List<byte>();
        foreach (var (name, text) in entries)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name); var data = Encoding.UTF8.GetBytes(text); var crc = Crc32(data); var offset = bytes.Count;
            U32(bytes, 0x04034b50); U16(bytes, 20); U16(bytes, 8); U16(bytes, 0); U16(bytes, 0); U16(bytes, 0); U32(bytes, zeroLocalValues ? 0u : crc); U32(bytes, zeroLocalValues ? 0u : (uint)data.Length); U32(bytes, zeroLocalValues ? 0u : (uint)data.Length); U16(bytes, (ushort)nameBytes.Length); U16(bytes, 0); bytes.AddRange(nameBytes); bytes.AddRange(data); U32(bytes, 0x08074b50); U32(bytes, crc); U32(bytes, (uint)data.Length); U32(bytes, (uint)data.Length);
            U32(central, 0x02014b50); U16(central, 20); U16(central, 20); U16(central, 8); U16(central, 0); U16(central, 0); U16(central, 0); U32(central, crc); U32(central, (uint)data.Length); U32(central, (uint)data.Length); U16(central, (ushort)nameBytes.Length); U16(central, 0); U16(central, 0); U16(central, 0); U16(central, 0); U32(central, 0); U32(central, (uint)offset); central.AddRange(nameBytes);
        }
        var centralOffset = bytes.Count; bytes.AddRange(central); U32(bytes, 0x06054b50); U16(bytes, 0); U16(bytes, 0); U16(bytes, (ushort)entries.Length); U16(bytes, (ushort)entries.Length); U32(bytes, (uint)central.Count); U32(bytes, (uint)centralOffset); U16(bytes, 0); File.WriteAllBytes(path, bytes.ToArray());
    }
    private static uint Crc32(byte[] data) { uint crc = uint.MaxValue; foreach (var value in data) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u); } return ~crc; }
    private static void U16(List<byte> bytes, ushort value) { bytes.Add((byte)value); bytes.Add((byte)(value >> 8)); }
    private static void U32(List<byte> bytes, uint value) { U16(bytes, (ushort)value); U16(bytes, (ushort)(value >> 16)); }
    private sealed class TempDirectory : IDisposable { internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "keycloak-tests-" + Guid.NewGuid().ToString("N")); internal TempDirectory() => Directory.CreateDirectory(Path); public void Dispose() { try { Directory.Delete(Path, true); } catch { } } }
}
