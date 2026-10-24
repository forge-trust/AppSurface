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
    public void Reads_v7_regular_control_and_data_entries_with_recomputed_digest_for_both_compressions()
    {
        var controlTar = TarEntries((TarEntryType.V7RegularFile, "control", "Package: demo\nVersion: 1\nArchitecture: amd64\n", null, null));
        var dataTar = TarEntries((TarEntryType.V7RegularFile, "usr/bin/v7-demo", "v7", null, null));
        foreach (var compression in new[] { "gz", "zst" })
        {
            using var deb = BuildDebWithTars(compression, controlTar, dataTar);
            var result = Read(deb);
            Assert.Equal(new DebPayloadReader.PackageIdentity("demo", "1", "amd64"), result.Identity);
            Assert.Contains(result.Files, file => file.Path == "usr/bin/v7-demo" && file.Kind == "file" && file.Length == 2 && file.Sha256 == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("v7"))).ToLowerInvariant());
            Assert.Equal(deb.Length, result.DebBytes);
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

    [Fact]
    public void Parses_directory_link_and_fifo_tar_kinds_with_exact_names_and_targets()
    {
        var dataTar = TarEntries(
            (TarEntryType.Directory, "dir/", null, null, null),
            (TarEntryType.SymbolicLink, "link", null, "target", null),
            (TarEntryType.HardLink, "hard", null, "target", null),
            (TarEntryType.Fifo, "fifo", null, null, null));
        using var deb = BuildDebWithDataTar(dataTar);
        var result = Read(deb);
        Assert.Contains(result.Files, x => x.Path == "dir" && x.Kind == "directory");
        Assert.Contains(result.Files, x => x.Path == "link" && x.Kind == "symlink" && x.LinkTarget == "target");
        Assert.Contains(result.Files, x => x.Path == "hard" && x.Kind == "hardlink" && x.LinkTarget == "target");
        Assert.Contains(result.Files, x => x.Path == "fifo" && x.Kind == "other");
    }

    [Fact]
    public void Rejects_duplicate_tar_names_after_dot_prefix_canonicalization()
    {
        var dataTar = TarEntries(
            (TarEntryType.RegularFile, "usr/bin/demo", "one", null, null),
            (TarEntryType.RegularFile, "./usr/bin/demo", "two", null, null));
        using var deb = BuildDebWithDataTar(dataTar);
        Assert.Throws<InvalidDataException>(() => Read(deb));
    }

    [Fact]
    public void Accepts_trailing_directory_names_after_canonicalization()
    {
        using var deb = BuildDebWithDataTar(TarEntries(
            (TarEntryType.Directory, "dir/", null, null, null)));
        var result = Read(deb);
        Assert.Equal(1, result.Files.Count(x => x.Path == "dir" && x.Kind == "directory"));
    }

    [Fact]
    public void Rejects_data_tar_when_one_file_crosses_the_expanded_budget()
    {
        var dataTar = TarEntries((TarEntryType.RegularFile, "large", null, null, new byte[32 * 1024 * 1024 + 1]));
        using var deb = BuildDebWithDataTar(dataTar);
        Assert.Throws<InvalidDataException>(() => Read(deb));
    }

    [Fact]
    public void Rejects_a_package_larger_than_the_hashing_budget_before_ar_parsing()
    {
        using var deb = new MemoryStream(new byte[32 * 1024 * 1024 + 1]);
        Assert.Throws<InvalidDataException>(() => DebPayloadReader.Read(deb, Hash(deb), new("demo", "1", "amd64")));
    }

    [Fact]
    public void Accepts_tab_control_continuation_without_treating_it_as_a_new_field()
    {
        using var deb = BuildCustomDeb("gz", "Package: demo\nVersion: 1\nArchitecture: amd64\nDescription: first\n\tcontinued\n", [], false);
        var result = Read(deb);
        Assert.Equal("first", result.Control["Description"]);
    }

    [Fact]
    public void Rejects_a_conffiles_member_over_its_metadata_cap()
    {
        using var deb = BuildCustomDeb("gz", "Package: demo\nVersion: 1\nArchitecture: amd64\n", [("conffiles", "/etc/" + new string('a', 70_000) + "\n")], false);
        Assert.Throws<InvalidDataException>(() => Read(deb));
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
    public void Rejects_invalid_utf8_in_control_without_replacing_the_identity()
    {
        var control = Tar(new Dictionary<string, byte[]>
        {
            ["control"] = Encoding.UTF8.GetBytes("Package: demo\nVersion: 1\nArchitecture: amd64\nDescription: ").Concat(new byte[] { 0xC3, 0x28 }).Concat(new byte[] { 0x0A }).ToArray()
        });
        var data = Gzip(Tar(new Dictionary<string, byte[]> { ["usr/bin/demo"] = Encoding.UTF8.GetBytes("ok") }));
        using var deb = Ar([("debian-binary", Encoding.ASCII.GetBytes("2.0\n")), ("control.tar.gz", Gzip(control)), ("data.tar.gz", data)]);
        Assert.Throws<DecoderFallbackException>(() => Read(deb));
    }

    [Fact]
    public void Rejects_control_field_with_an_empty_name()
    {
        using var deb = BuildCustomDeb("gz", ": value\nPackage: demo\nVersion: 1\nArchitecture: amd64\n", [], false);
        Assert.Throws<InvalidDataException>(() => Read(deb));
    }

    [Fact]
    public void Rejects_zstd_member_trailing_bytes_at_the_ar_boundary()
    {
        using var deb = BuildDeb("zst", Encoding.ASCII.GetBytes("trailing"), "ok");
        Assert.Throws<ZstdSharp.ZstdException>(() => Read(deb));
    }

    [Theory]
    [InlineData(63)]
    [InlineData(65)]
    public void Rejects_digest_arguments_that_are_not_exact_sha256_hex_length(int length)
    {
        using var deb = BuildDeb("gz", [], "ok");
        Assert.Throws<InvalidDataException>(() => DebPayloadReader.Read(deb, new string('0', length), new("demo", "1", "amd64")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("./../escape")]
    public void Rejects_canonical_payload_paths_that_collapse_to_unsafe_segments(string path)
    {
        using var deb = BuildDeb("gz", [], "ok", dataPath: path);
        Assert.Throws<InvalidDataException>(() => Read(deb));
    }

    [Fact]
    public void Accepts_canonical_dot_prefix_without_changing_the_recorded_path()
    {
        using var deb = BuildDeb("gz", [], "ok", dataPath: "./usr/bin/demo");
        var result = Read(deb);
        Assert.Contains(result.Files, file => file.Path == "usr/bin/demo" && file.Length == 2);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/etc/./demo.conf")]
    public void Rejects_conffile_records_with_empty_or_dot_segments(string path)
    {
        using var deb = BuildCustomDeb("gz", "Package: demo\nVersion: 1\nArchitecture: amd64\n", [("conffiles", path + "\n")], false);
        Assert.Throws<InvalidDataException>(() => Read(deb));
    }

    [Fact]
    public void Rejects_nonseekable_package_stream_before_consuming_it()
    {
        using var deb = BuildDeb("gz", [], "ok");
        using var input = new NonSeekableStream(deb);
        Assert.Throws<InvalidDataException>(() => DebPayloadReader.Read(input, new string('0', 64), new("demo", "1", "amd64")));
    }

    [Fact]
    public void Cancellation_is_observed_during_package_hashing()
    {
        using var deb = BuildDeb("gz", [], "ok");
        using var cancellation = new CancellationTokenSource();
        using var input = new CancelAfterReadStream(deb, cancellation);
        Assert.Throws<OperationCanceledException>(() => DebPayloadReader.Read(input, Hash(deb), new("demo", "1", "amd64"), cancellation.Token));
    }

    [Fact]
    public void Accepts_debian_control_continuations_and_conffile_options()
    {
        const string control = "Package: demo\nVersion: 1\nArchitecture: amd64\nDescription: first\n continuation\n";
        using var deb = BuildCustomDeb("gz", control, [new("conffiles", "/etc/demo.conf 123\r\n\n")], false);
        var result = DebPayloadReader.Read(deb, Hash(deb), new("demo", "1", "amd64"));
        Assert.Equal("first", result.Control["Description"]);
        Assert.Equal(new[] { "etc/demo.conf" }, result.Conffiles.ToArray());
    }

    [Fact]
    public void CompareInstalledInventory_rejects_kind_changes_and_accepts_declared_kind_change()
    {
        DebPayloadReader.InstalledFile[] before = [new("etc/demo", "hash", "file")];
        DebPayloadReader.InstalledFile[] after = [new("etc/demo", null, "symlink")];
        Assert.Throws<InvalidDataException>(() => DebPayloadReader.CompareInstalledInventory(before, after, [], [], []));
        DebPayloadReader.CompareInstalledInventory(before, after, [], [], [new("etc/demo", "symlink", null)]);
    }

    [Fact]
    public void Inventory_rejects_an_expected_change_when_both_sides_are_empty()
    {
        Assert.Throws<InvalidDataException>(() => DebPayloadReader.CompareInstalledInventory(
            [], [], [], [], [new("etc/missing", "file", "deadbeef")]));
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
        var control = Gzip(Tar(new Dictionary<string, byte[]> { ["control"] = Encoding.UTF8.GetBytes("Package: demo\nVersion: 1\nArchitecture: amd64\n") }));
        var data = Gzip(Tar(new Dictionary<string, byte[]> { ["usr/bin/demo"] = Encoding.UTF8.GetBytes("ok") }));
        if ((data.Length & 1) == 0)
        {
            // A valid one-byte gzip extra field changes framing parity without corrupting payload bytes.
            data = data[..10].Concat(new byte[] { 1, 0, 0 }).Concat(data[10..]).ToArray();
            data[3] |= 4;
        }
        using var deb = Ar([("debian-binary", Encoding.ASCII.GetBytes("2.0\n")), ("control.tar.gz", control), ("data.tar.gz", data)]);
        Read(deb); // The independent positive control proves the fixture reaches ar padding validation.
        var bytes = deb.ToArray();
        Assert.Equal(10, bytes[^1]);
        bytes[^1] = 0;
        using var malformed = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => Read(malformed));
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

    [Fact]
    public void Slice_enforces_exact_remaining_length_for_array_and_span_reads()
    {
        using var arraySlice = new DebPayloadReader.Slice(new MemoryStream([1, 2, 3, 4]), 3);
        var array = new byte[8];
        Assert.Equal(3, arraySlice.Read(array, 0, array.Length));
        Assert.Equal(new byte[] { 1, 2, 3 }, array[..3]);
        Assert.Equal(0, arraySlice.Read(array, 0, array.Length));
        Assert.Equal(0, arraySlice.Remaining);

        using var spanSlice = new DebPayloadReader.Slice(new MemoryStream([5, 6, 7]), 2);
        Span<byte> span = stackalloc byte[4];
        Assert.Equal(2, spanSlice.Read(span));
        Assert.Equal(new byte[] { 5, 6 }, span[..2].ToArray());
        Assert.Equal(0, spanSlice.Remaining);
        AssertReadOnlyStream(spanSlice);
    }

    [Fact]
    public void Single_byte_and_token_streams_check_cancellation_before_and_after_reads()
    {
        using var before = new CancellationTokenSource(); before.Cancel();
        using var single = new DebPayloadReader.SingleByteReadStream(new MemoryStream([1, 2]), before.Token);
        Assert.Throws<OperationCanceledException>(() => single.Read(new byte[2], 0, 2));

        using var singleSpan = new DebPayloadReader.SingleByteReadStream(new MemoryStream([7, 8]), CancellationToken.None);
        Span<byte> singleBuffer = stackalloc byte[2];
        Assert.Equal(1, singleSpan.Read(singleBuffer)); Assert.Equal(7, singleBuffer[0]);
        AssertReadOnlyStream(singleSpan);

        using var after = new CancellationTokenSource();
        using var cancelling = new CancelOnReadStream(new MemoryStream([3, 4]), after);
        using var token = new DebPayloadReader.TokenCheckingStream(cancelling, after.Token);
        Assert.Throws<OperationCanceledException>(() => token.Read(new byte[2], 0, 2));

        using var tokenSuccess = new DebPayloadReader.TokenCheckingStream(new MemoryStream([9]), CancellationToken.None);
        Span<byte> tokenBuffer = stackalloc byte[1];
        Assert.Equal(1, tokenSuccess.Read(tokenBuffer)); Assert.Equal(9, tokenBuffer[0]);

        using var spanCancel = new CancellationTokenSource(); spanCancel.Cancel();
        using var tokenSpan = new DebPayloadReader.TokenCheckingStream(new MemoryStream([5]), spanCancel.Token);
        Assert.Throws<OperationCanceledException>(() => tokenSpan.Read(new byte[1].AsSpan()));
        AssertReadOnlyStream(tokenSpan);
    }

    [Fact]
    public void Limited_allows_exact_cap_and_rejects_one_over_for_array_and_span_reads()
    {
        using var arrayLimited = new DebPayloadReader.Limited(new MemoryStream([1, 2, 3]), 2);
        var array = new byte[2]; Assert.Equal(2, arrayLimited.Read(array, 0, array.Length)); Assert.Equal(2, arrayLimited.Position);
        Assert.Throws<InvalidDataException>(() => arrayLimited.ReadByte());

        using var spanLimited = new DebPayloadReader.Limited(new MemoryStream([4, 5, 6]), 2);
        Span<byte> span = stackalloc byte[2]; Assert.Equal(2, spanLimited.Read(span)); Assert.Equal(2, spanLimited.Position);
        Assert.Throws<InvalidDataException>(() => spanLimited.Read(new byte[1].AsSpan()));
        AssertReadOnlyStream(spanLimited);
    }

    [Fact]
    public void Sink_hashes_both_write_overloads_and_exposes_only_write_capability()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sink = new DebPayloadReader.Sink(hash);
        sink.Write(new byte[] { 1, 2 }, 0, 2); sink.Write(new byte[] { 3, 4 }.AsSpan()); sink.Flush();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3, 4 })).ToLowerInvariant(), Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        Assert.True(sink.CanWrite); Assert.False(sink.CanRead); Assert.False(sink.CanSeek); Assert.Equal(0, sink.Length); Assert.Equal(0, sink.Position);
        Assert.Throws<NotSupportedException>(() => sink.Position = 1);
        Assert.Throws<NotSupportedException>(() => sink.ReadByte());
        Assert.Throws<NotSupportedException>(() => sink.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => sink.SetLength(1));
    }

    private static void AssertReadOnlyStream(Stream stream)
    {
        Assert.True(stream.CanRead); Assert.False(stream.CanSeek); Assert.False(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => _ = stream.Length);
        if (stream is DebPayloadReader.Limited limited) Assert.Equal(limited.Position, stream.Position);
        else Assert.Throws<NotSupportedException>(() => _ = stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Position = 0);
        Assert.Throws<NotSupportedException>(() => stream.Flush());
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
        Assert.Throws<NotSupportedException>(() => stream.Write(Array.Empty<byte>(), 0, 0));
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
        var controlTar = Tar(controlFiles); byte[] control = Gzip(controlTar); var dataStream = new MemoryStream(); using (var writer = new TarWriter(dataStream, leaveOpen: true)) { var regular = new PaxTarEntry(TarEntryType.RegularFile, "usr/bin/demo") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("ok")) }; writer.WriteEntry(regular); if (links) { writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "usr/bin/link") { LinkName = "target" }); writer.WriteEntry(new PaxTarEntry(TarEntryType.HardLink, "usr/bin/hard") { LinkName = "target" }); } }
        dataStream.Write(new byte[1024]); var dataTar = dataStream.ToArray(); var dataName = compression == "zst" ? "data.tar.zst" : "data.tar.gz"; var data = compression == "zst" ? Zstd(dataTar) : Gzip(dataTar); var members = new[] { ("debian-binary", Encoding.ASCII.GetBytes("2.0\n")), ("control.tar.gz", control), (dataName, data) }; return Ar(members);
    }
    private static MemoryStream Ar((string Name, byte[] Bytes)[] members)
    { var output = new MemoryStream(); output.Write(Encoding.ASCII.GetBytes("!<arch>\n")); foreach (var (name, bytes) in members) { var h = new byte[60]; Encoding.ASCII.GetBytes((name + "/").PadRight(16)).CopyTo(h, 0); Encoding.ASCII.GetBytes("0           ").CopyTo(h, 16); Encoding.ASCII.GetBytes("0     ").CopyTo(h, 28); Encoding.ASCII.GetBytes("0     ").CopyTo(h, 34); Encoding.ASCII.GetBytes("100644  ").CopyTo(h, 40); Encoding.ASCII.GetBytes(bytes.Length.ToString().PadLeft(10)).CopyTo(h, 48); h[58] = 0x60; h[59] = 10; output.Write(h); output.Write(bytes); if ((bytes.Length & 1) != 0) output.WriteByte(10); } output.Position = 0; return output; }

    private static byte[] Tar(Dictionary<string, byte[]> files)
    { using var ms = new MemoryStream(); using (var writer = new TarWriter(ms, leaveOpen: true)) foreach (var (name, bytes) in files) { var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(bytes) }; writer.WriteEntry(entry); } return ms.ToArray(); }
    private static byte[] TarEntries(params (TarEntryType Type, string Name, string? Text, string? Link, byte[]? Data)[] entries)
    { using var ms = new MemoryStream(); var format = entries.Any(x => x.Type == TarEntryType.V7RegularFile) ? TarEntryFormat.V7 : TarEntryFormat.Pax; using (var writer = new TarWriter(ms, format, leaveOpen: true)) foreach (var item in entries) { TarEntry entry = item.Type == TarEntryType.V7RegularFile ? new V7TarEntry(item.Type, item.Name) : new PaxTarEntry(item.Type, item.Name); if (item.Type is TarEntryType.SymbolicLink or TarEntryType.HardLink) entry.LinkName = item.Link ?? string.Empty; if (item.Type is TarEntryType.RegularFile or TarEntryType.V7RegularFile) entry.DataStream = new MemoryStream(item.Data ?? Encoding.UTF8.GetBytes(item.Text ?? "")); writer.WriteEntry(entry); } return ms.ToArray(); }
    private static MemoryStream BuildDebWithDataTar(byte[] dataTar)
    { var control = Gzip(Tar(new Dictionary<string, byte[]> { ["control"] = Encoding.UTF8.GetBytes("Package: demo\nVersion: 1\nArchitecture: amd64\n") })); return Ar([("debian-binary", Encoding.ASCII.GetBytes("2.0\n")), ("control.tar.gz", control), ("data.tar.gz", Gzip(dataTar))]); }
    private static MemoryStream BuildDebWithTars(string compression, byte[] controlTar, byte[] dataTar)
    { var control = compression == "zst" ? Zstd(controlTar) : Gzip(controlTar); var data = compression == "zst" ? Zstd(dataTar) : Gzip(dataTar); var suffix = compression == "zst" ? ".zst" : ".gz"; return Ar([("debian-binary", Encoding.ASCII.GetBytes("2.0\n")), ("control.tar" + suffix, control), ("data.tar" + suffix, data)]); }
    private static byte[] Gzip(byte[] bytes) { using var ms = new MemoryStream(); using (var g = new GZipStream(ms, CompressionLevel.SmallestSize, true)) g.Write(bytes); return ms.ToArray(); }
    private static byte[] Zstd(byte[] bytes) { using var c = new ZstdSharp.Compressor(); return c.Wrap(bytes).ToArray(); }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CancelAfterReadStream(Stream inner, CancellationTokenSource cancellation) : Stream
    {
        private bool cancelled;
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, Math.Min(count, 1));
            if (!cancelled) { cancelled = true; cancellation.Cancel(); }
            return read;
        }
        public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CancelOnReadStream(Stream inner, CancellationTokenSource cancellation) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) { var read = inner.Read(buffer, offset, count); cancellation.Cancel(); return read; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
