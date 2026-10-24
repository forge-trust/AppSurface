using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

/// <summary>Protects Debian ar framing, compression boundaries, tar identity, names, hashes, and cancellation; fails on malformed or trailing package content; claims exact package inventory or typed rejection.</summary>
public sealed class DebPayloadTests
{
    [Fact]
    public void Reads_valid_gzip_and_zstd_packages_with_exact_inventory()
    {
        foreach (var compression in new[] { "gz", "zst" })
        {
            using var deb = BuildDeb(compression, Array.Empty<byte>(), "ok");
            var expected = new DebPayloadReader.PackageIdentity("demo", "1", "amd64");
            var result = DebPayloadReader.Read(deb, Hash(deb), expected);
            Assert.Equal(expected, result.Identity);
            Assert.Contains(result.Files, x => x.Path == "usr/bin/demo" && x.Kind == "file" && x.Length == 2);
            Assert.Equal("demo", result.Control["Package"]);
        }
    }

    [Fact]
    public void Rejects_ar_shape_debian_binary_tail_tar_name_and_compressed_trailing_bytes()
    {
        using (var badBinary = BuildDeb("gz", Array.Empty<byte>(), "ok", binary: Encoding.ASCII.GetBytes("2.0\nX")))
            Assert.Throws<InvalidDataException>(() => Read(badBinary));
        using (var badName = BuildDeb("gz", Array.Empty<byte>(), "ok", dataName: "data.tar.gz/unsafe"))
            Assert.Throws<InvalidDataException>(() => Read(badName));
        using (var gzipTail = BuildDeb("gz", Encoding.ASCII.GetBytes("tail"), "ok"))
            Assert.Throws<InvalidDataException>(() => Read(gzipTail));
        using (var longName = BuildDeb("gz", Array.Empty<byte>(), "ok", dataPath: new string('a', 1025)))
            Assert.Throws<InvalidDataException>(() => Read(longName));
    }

    [Fact]
    public void Cancellation_is_observed_before_and_during_read()
    {
        using var deb = BuildDeb("gz", Array.Empty<byte>(), new string('x', 100_000));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => DebPayloadReader.Read(deb, Hash(deb), new("demo", "1", "amd64"), cancelled.Token));
    }

    [Fact]
    public void CompareInstalledInventory_accepts_only_declared_exact_changes()
    {
        DebPayloadReader.InstalledFile[] baseline = [new("etc/keep", "old", "file"), new("etc/removed", "gone", "file")];
        DebPayloadReader.InstalledFile[] actual = [new("etc/keep", "new", "file"), new("etc/added", "add", "file")];
        DebPayloadReader.CompareInstalledInventory(baseline, actual, ["var/**"], ["etc/**"], [new("etc/keep", "file", "new"), new("etc/removed", "file", null), new("etc/added", "file", "add")]);
        Assert.Throws<InvalidDataException>(() => DebPayloadReader.CompareInstalledInventory(baseline, actual, [], [], [new("etc/keep", "file", "wrong")]));
        Assert.Throws<InvalidDataException>(() => DebPayloadReader.CompareInstalledInventory(baseline, actual, [], [], []));
        Assert.Throws<InvalidDataException>(() => DebPayloadReader.CompareInstalledInventory(baseline, actual, [], [], [new("etc/missing", "file", "x")]));
    }

    [Theory]
    [InlineData("gz")]
    [InlineData("zst")]
    public void Control_identity_conffile_and_link_records_are_parsed_and_bounded(string compression)
    {
        var control = "Package: demo\nVersion: 1\nArchitecture: amd64\nConffiles: ignored\n";
        using var deb = BuildCustomDeb(compression, control, [new("conffiles", "/etc/demo.conf\n"), new("data", "")], links: true);
        var result = DebPayloadReader.Read(deb, Hash(deb), new("demo", "1", "amd64"));
        Assert.Contains("etc/demo.conf", result.Conffiles); Assert.Contains(result.Files, x => x.Kind == "symlink" && x.LinkTarget == "target"); Assert.Contains(result.Files, x => x.Kind == "hardlink" && x.LinkTarget == "target");
        using var wrongIdentity = BuildCustomDeb(compression, control, [], false); Assert.Throws<InvalidDataException>(() => DebPayloadReader.Read(wrongIdentity, Hash(wrongIdentity), new("other", "1", "amd64")));
        using var malformed = BuildCustomDeb(compression, "Package: demo\nPackage: duplicate\nVersion: 1\nArchitecture: amd64\n", [], false); Assert.Throws<InvalidDataException>(() => DebPayloadReader.Read(malformed, Hash(malformed), new("demo", "1", "amd64")));
        using var badConf = BuildCustomDeb(compression, control, [new("conffiles", "/etc/../unsafe\n")], false); Assert.Throws<InvalidDataException>(() => DebPayloadReader.Read(badConf, Hash(badConf), new("demo", "1", "amd64")));
        using var oversizedControl = BuildCustomDeb(compression, "Package: demo\nVersion: 1\nArchitecture: amd64\nDescription: " + new string('x', 70_000) + "\n", [], false); Assert.Throws<InvalidDataException>(() => DebPayloadReader.Read(oversizedControl, Hash(oversizedControl), new("demo", "1", "amd64")));
    }

    [Theory]
    [InlineData("relative.conf")]
    [InlineData("/etc//demo.conf")]
    [InlineData("/etc/../unsafe")]
    public void Rejects_malformed_conffile_records(string conffile)
    {
        using var deb = BuildCustomDeb("gz", "Package: demo\nVersion: 1\nArchitecture: amd64\n", [new("conffiles", conffile + "\n")], false);
        Assert.Throws<InvalidDataException>(() => DebPayloadReader.Read(deb, Hash(deb), new("demo", "1", "amd64")));
    }

    [Fact]
    public void Rejects_invalid_control_field_continuation_and_noncanonical_package_path()
    {
        using var malformed = BuildCustomDeb("gz", "Package: demo\nmalformed-field\nVersion: 1\nArchitecture: amd64\n", [], false); Assert.Throws<InvalidDataException>(() => DebPayloadReader.Read(malformed, Hash(malformed), new("demo", "1", "amd64")));
        using var path = BuildCustomDeb("gz", "Package: demo\nVersion: 1\nArchitecture: amd64\n", [new("conffiles", "\\etc\\demo.conf\n")], false); Assert.Throws<InvalidDataException>(() => DebPayloadReader.Read(path, Hash(path), new("demo", "1", "amd64")));
    }

    [Fact]
    public void Rejects_ar_header_name_size_marker_and_truncation_shapes()
    {
        using (var badMarker = BuildDeb("gz", Array.Empty<byte>(), "ok")) { badMarker.Position = 8 + 58; badMarker.WriteByte(0x61); badMarker.Position = 0; Assert.Throws<InvalidDataException>(() => Read(badMarker)); }
        using (var badName = BuildDeb("gz", Array.Empty<byte>(), "ok")) { badName.Position = 8; badName.WriteByte((byte)'?'); badName.Position = 0; Assert.Throws<InvalidDataException>(() => Read(badName)); }
        using (var badSize = BuildDeb("gz", Array.Empty<byte>(), "ok")) { badSize.Position = 8 + 48; badSize.Write(Encoding.ASCII.GetBytes("9999999999")); badSize.Position = 0; Assert.Throws<InvalidDataException>(() => Read(badSize)); }
        using (var truncated = BuildDeb("gz", Array.Empty<byte>(), "ok")) { truncated.SetLength(truncated.Length - 1); truncated.Position = 0; Assert.Throws<InvalidDataException>(() => Read(truncated)); }
    }

    [Fact]
    public void Rejects_wrong_odd_member_padding_without_accepting_shifted_offsets()
    {
        using var deb = BuildDeb("gz", new byte[] { 1 }, "ok");
        var bytes = deb.ToArray();
        var position = 8;
        for (var member = 0; member < 3; member++)
        {
            var size = int.Parse(Encoding.ASCII.GetString(bytes, position + 48, 10).Trim());
            if ((size & 1) != 0) { bytes[position + 60 + size] = 0; deb.SetLength(0); deb.Write(bytes); deb.Position = 0; Assert.Throws<InvalidDataException>(() => Read(deb)); return; }
            position += 60 + size + (size & 1);
        }
        throw new InvalidOperationException("Fixture did not contain an odd ar member.");
    }

    [Theory]
    [InlineData("magic")]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("nonhex")]
    [InlineData("truncated-header")]
    [InlineData("empty-name")]
    [InlineData("negative-size")]
    [InlineData("text-size")]
    [InlineData("extra-member")]
    [InlineData("wrong-order")]
    [InlineData("control-name")]
    [InlineData("data-name")]
    public void DebianFramingRejectsEachMalformedBoundary(string mutation)
    {
        using var valid = BuildDeb("gz", [], "ok"); var bytes = valid.ToArray();
        var control = Gzip(Tar(new Dictionary<string, byte[]> { ["control"] = Encoding.UTF8.GetBytes("Package: demo\nVersion: 1\nArchitecture: amd64\n") }));
        var data = Gzip(Tar(new Dictionary<string, byte[]> { ["usr/bin/demo"] = Encoding.UTF8.GetBytes("ok") }));
        switch (mutation)
        {
            case "magic": bytes[0] = 0; break;
            case "version": bytes[68] = (byte)'3'; break;
            case "hash": case "nonhex": break;
            case "truncated-header": bytes = bytes[..20]; break;
            case "empty-name": Encoding.ASCII.GetBytes(new string(' ', 16)).CopyTo(bytes, 8); break;
            case "negative-size": Encoding.ASCII.GetBytes("        -1").CopyTo(bytes, 56); break;
            case "text-size": Encoding.ASCII.GetBytes("     nope ").CopyTo(bytes, 56); break;
            case "extra-member": using (var malformed = Ar([("debian-binary", Encoding.ASCII.GetBytes("2.0\n")), ("control.tar.gz", control), ("data.tar.gz", data), ("extra", [])])) bytes = malformed.ToArray(); break;
            case "wrong-order": using (var malformed = Ar([("control.tar.gz", control), ("debian-binary", Encoding.ASCII.GetBytes("2.0\n")), ("data.tar.gz", data)])) bytes = malformed.ToArray(); break;
            case "control-name": using (var malformed = Ar([("debian-binary", Encoding.ASCII.GetBytes("2.0\n")), ("control.tar.xz", control), ("data.tar.gz", data)])) bytes = malformed.ToArray(); break;
            default: using (var malformed = Ar([("debian-binary", Encoding.ASCII.GetBytes("2.0\n")), ("control.tar.gz", control), ("data.tar.xz", data)])) bytes = malformed.ToArray(); break;
        }
        using var input = new MemoryStream(bytes);
        var hash = mutation == "hash" ? new string('0', 64) : mutation == "nonhex" ? new string('g', 64) : Hash(input);
        Assert.Throws<InvalidDataException>(() => DebPayloadReader.Read(input, hash, new("demo", "1", "amd64")));
    }

    [Theory]
    [InlineData("Package: demo\nVersion: 1\n")]
    [InlineData("Package: demo\nArchitecture: amd64\n")]
    [InlineData("Version: 1\nArchitecture: amd64\n")]
    [InlineData("Package: demo\nVersion: 2\nArchitecture: amd64\n")]
    [InlineData("Package: demo\nVersion: 1\nArchitecture: arm64\n")]
    public void MissingOrWrongControlTupleCannotProducePackageEvidence(string control)
    {
        using var input = BuildCustomDeb("gz", control, [], false);
        Assert.Throws<InvalidDataException>(() => Read(input));
    }

    [Theory]
    [InlineData("/absolute")]
    [InlineData("a/../escape")]
    [InlineData("a//alias")]
    [InlineData("a/./alias")]
    [InlineData("a\\bad")]
    public void PackagePayloadNamesStayRelativeAndCanonical(string path)
    {
        using var input = BuildDeb("gz", [], "ok", dataPath: path);
        Assert.Throws<InvalidDataException>(() => Read(input));
    }

    [Theory]
    [InlineData("usr/**", "usr/lib/nested/item")]
    [InlineData("usr/*/item", "usr/lib/item")]
    [InlineData("**/item", "usr/lib/item")]
    [InlineData("usr/*", "usr/lib")]
    public void ExplicitDpkgIncludeOverridesEveryMatchingExclude(string pattern, string path)
    {
        DebPayloadReader.InstalledFile[] before = [new(path, "old", "file")];
        DebPayloadReader.InstalledFile[] after = [new(path, "new", "file")];
        DebPayloadReader.CompareInstalledInventory(before, after, [pattern], [], []);
        Assert.Throws<InvalidDataException>(() => DebPayloadReader.CompareInstalledInventory(before, after, [pattern], [path], []));
        DebPayloadReader.CompareInstalledInventory(before, after, [pattern], [path], [new(path, "file", "new")]);
    }

    private static DebPayloadReader.PackageResult Read(MemoryStream deb) => DebPayloadReader.Read(deb, Hash(deb), new("demo", "1", "amd64"));
    private static string Hash(Stream stream) { stream.Position = 0; return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }

    private static MemoryStream BuildDeb(string compression, byte[] trailing, string content, byte[]? binary = null, string dataName = "data.tar.gz", string? dataPath = null)
    {
        var controlTar = Tar(new Dictionary<string, byte[]> { ["control"] = Encoding.UTF8.GetBytes("Package: demo\nVersion: 1\nArchitecture: amd64\n") });
        var dataTar = Tar(new Dictionary<string, byte[]> { [dataPath ?? "usr/bin/demo"] = Encoding.UTF8.GetBytes(content) });
        byte[] control = Gzip(controlTar), data = Gzip(dataTar);
        if (compression == "zst") { dataName = "data.tar.zst"; data = Zstd(dataTar); }
        data = data.Concat(trailing).ToArray();
        var members = new[] { ("debian-binary", binary ?? Encoding.ASCII.GetBytes("2.0\n")), ("control.tar.gz", control), (dataName, data) };
        var output = new MemoryStream(); output.Write(Encoding.ASCII.GetBytes("!<arch>\n"));
        foreach (var (name, bytes) in members) { var h = new byte[60]; Encoding.ASCII.GetBytes((name + "/").PadRight(16)).CopyTo(h, 0); Encoding.ASCII.GetBytes("0           ").CopyTo(h, 16); Encoding.ASCII.GetBytes("0     ").CopyTo(h, 28); Encoding.ASCII.GetBytes("0     ").CopyTo(h, 34); Encoding.ASCII.GetBytes("100644  ").CopyTo(h, 40); Encoding.ASCII.GetBytes(bytes.Length.ToString().PadLeft(10)).CopyTo(h, 48); h[58] = 0x60; h[59] = 10; output.Write(h); output.Write(bytes); if ((bytes.Length & 1) != 0) output.WriteByte(10); }
        output.Position = 0; return output;
    }

    private static MemoryStream BuildCustomDeb(string compression, string controlText, (string Name, string Value)[] extraControl, bool links)
    {
        var controlFiles = new Dictionary<string, byte[]> { ["control"] = Encoding.UTF8.GetBytes(controlText) }; foreach (var item in extraControl) controlFiles[item.Name] = Encoding.UTF8.GetBytes(item.Value);
        var controlTar = Tar(controlFiles); byte[] control = Gzip(controlTar); var dataStream = new MemoryStream(); using (var writer = new TarWriter(dataStream, leaveOpen: true)) { var regular = new PaxTarEntry(TarEntryType.RegularFile, "usr/bin/demo") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("ok")) }; writer.WriteEntry(regular); if (links) { writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "usr/bin/link") { LinkName = "target" }); writer.WriteEntry(new PaxTarEntry(TarEntryType.HardLink, "usr/bin/hard") { LinkName = "target" }); } } dataStream.Write(new byte[1024]); var dataTar = dataStream.ToArray(); var dataName = compression == "zst" ? "data.tar.zst" : "data.tar.gz"; var data = compression == "zst" ? Zstd(dataTar) : Gzip(dataTar); var members = new[] { ("debian-binary", Encoding.ASCII.GetBytes("2.0\n")), ("control.tar.gz", control), (dataName, data) }; return Ar(members);
    }
    private static MemoryStream Ar((string Name, byte[] Bytes)[] members)
    { var output = new MemoryStream(); output.Write(Encoding.ASCII.GetBytes("!<arch>\n")); foreach (var (name, bytes) in members) { var h = new byte[60]; Encoding.ASCII.GetBytes((name + "/").PadRight(16)).CopyTo(h, 0); Encoding.ASCII.GetBytes("0           ").CopyTo(h, 16); Encoding.ASCII.GetBytes("0     ").CopyTo(h, 28); Encoding.ASCII.GetBytes("0     ").CopyTo(h, 34); Encoding.ASCII.GetBytes("100644  ").CopyTo(h, 40); Encoding.ASCII.GetBytes(bytes.Length.ToString().PadLeft(10)).CopyTo(h, 48); h[58] = 0x60; h[59] = 10; output.Write(h); output.Write(bytes); if ((bytes.Length & 1) != 0) output.WriteByte(10); } output.Position = 0; return output; }

    private static byte[] Tar(Dictionary<string, byte[]> files)
    { using var ms = new MemoryStream(); using (var writer = new TarWriter(ms, leaveOpen: true)) foreach (var (name, bytes) in files) { var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(bytes) }; writer.WriteEntry(entry); } return ms.ToArray(); }
    private static byte[] Gzip(byte[] bytes) { using var ms = new MemoryStream(); using (var g = new GZipStream(ms, CompressionLevel.SmallestSize, true)) g.Write(bytes); return ms.ToArray(); }
    private static byte[] Zstd(byte[] bytes) { using var c = new ZstdSharp.Compressor(); return c.Wrap(bytes).ToArray(); }
}
