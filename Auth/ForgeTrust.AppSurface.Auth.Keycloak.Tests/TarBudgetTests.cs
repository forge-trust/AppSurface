using System.Formats.Tar;
using System.Text;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

/// <summary>Protects bounded tar transport accounting and header parsing; fails before oversized, truncated, or malformed members are consumed; claims exact payload and consumed-byte behavior.</summary>
public sealed class TarBudgetTests
{
    [Fact]
    public void Reads_exact_payload_and_reports_consumed_budget()
    {
        using var input = Tar("file.txt", "payload"); using var bounded = new TarBudgetReadStream(input, 4096, 64, 4); using var reader = new TarReader(bounded);
        var entry = reader.GetNextEntry(); Assert.NotNull(entry); using var text = new StreamReader(entry!.DataStream!); Assert.Equal("payload", text.ReadToEnd()); bounded.Finish(); Assert.True(bounded.Consumed >= 1024);
    }

    [Fact]
    public void Rejects_entry_count_payload_and_truncated_terminator_budgets()
    {
        using (var input = Tar("file.txt", "payload")) using (var bounded = new TarBudgetReadStream(input, 4096, 2, 4))
            Assert.Throws<InvalidDataException>(() => bounded.Finish());
        using (var input = Tar("file.txt", "payload")) using (var bounded = new TarBudgetReadStream(input, 4096, 2, 4))
            Assert.Throws<InvalidDataException>(() =>
            {
                using var reader = new TarReader(bounded);
                reader.GetNextEntry()!.DataStream!.CopyTo(Stream.Null);
            });
        using (var input = new MemoryStream(Tar("file.txt", "payload").ToArray()[..1024])) using (var bounded = new TarBudgetReadStream(input, 4096, 64, 4))
            Assert.Throws<InvalidDataException>(() => bounded.Finish());
    }

    [Fact]
    public void Rejects_invalid_octal_header_without_allocating_large_data()
    {
        var bytes = Tar("file.txt", "payload").ToArray(); Encoding.ASCII.GetBytes("zzzzzzzzzzz").CopyTo(bytes, 124);
        using var bounded = new TarBudgetReadStream(new MemoryStream(bytes), 4096, 64, 4); Assert.Throws<InvalidDataException>(() => bounded.Finish());
    }

    [Fact]
    public void Empty_reads_are_noops_and_exact_budget_still_allows_complete_tar()
    {
        // TarWriter emits its own two end blocks; Tar() appends one additional
        // two-block terminator, so this fixture consumes 512 + 512 + 2048 bytes.
        using var input = Tar("file.txt", "payload"); using var bounded = new TarBudgetReadStream(input, 3072, 64, 4);
        Assert.Equal(0, bounded.Read(Span<byte>.Empty));
        bounded.Finish(); Assert.Equal(3072, bounded.Consumed);
    }

    [Fact]
    public void Accepts_a_clean_two_zero_block_tar_end_without_an_extra_header()
    {
        using var bounded = new TarBudgetReadStream(new MemoryStream(new byte[1024]), 1024, 64, 4);
        bounded.Finish();
        Assert.Equal(1024, bounded.Consumed);
        Assert.Equal(0, bounded.Read(Span<byte>.Empty));
    }

    [Fact]
    public void Rejects_budget_crossing_at_header_and_payload_boundaries()
    {
        using (var input = Tar("file.txt", "payload")) using (var bounded = new TarBudgetReadStream(input, 511, 64, 4))
            Assert.Throws<InvalidDataException>(() => bounded.Finish());
        using (var input = Tar("file.txt", "payload")) using (var bounded = new TarBudgetReadStream(input, 2048, 6, 4))
            Assert.Throws<InvalidDataException>(() =>
            {
                using var reader = new TarReader(bounded);
                reader.GetNextEntry()!.DataStream!.CopyTo(Stream.Null);
            });
    }

    [Fact]
    public void Rejects_zero_entry_budget_and_truncated_payload_without_terminator()
    {
        using (var input = Tar("file.txt", "payload")) using (var bounded = new TarBudgetReadStream(input, 4096, 64, 0))
            Assert.Throws<InvalidDataException>(() => bounded.Finish());

        var truncated = Tar("file.txt", "payload").ToArray()[..517];
        using var input2 = new MemoryStream(truncated);
        using var bounded2 = new TarBudgetReadStream(input2, 4096, 64, 4);
        Assert.Throws<InvalidDataException>(() => bounded2.Finish());
    }

    [Fact]
    public void Rejects_numeric_field_with_digits_after_padding()
    {
        var bytes = Tar("file.txt", "payload").ToArray();
        bytes[124] = (byte)'7'; bytes[125] = (byte)' '; bytes[126] = (byte)'0';
        using var bounded = new TarBudgetReadStream(new MemoryStream(bytes), 4096, 64, 4);
        Assert.Throws<InvalidDataException>(() => bounded.Finish());
    }

    [Fact]
    public void Rejects_base256_numeric_encoding_and_oversized_pax_metadata()
    {
        var base256 = Tar("file.txt", "payload").ToArray();
        base256[124] = 0x80;
        using (var bounded = new TarBudgetReadStream(new MemoryStream(base256), 4096, 64, 4))
            Assert.Throws<InvalidDataException>(() => bounded.Finish());

        var pax = Tar("file.txt", "payload").ToArray();
        pax[156] = (byte)'x';
        WriteOctal(pax.AsSpan(124, 12), 65 * 1024);
        using var metadata = new TarBudgetReadStream(new MemoryStream(pax), 4096, 64, 4);
        Assert.Throws<InvalidDataException>(() => metadata.Finish());
    }

    [Fact]
    public void Supports_unseekable_inner_stream_and_rejects_unsupported_operations()
    {
        using var inner = new NonSeekableStream(Tar("file.txt", "payload"));
        using var bounded = new TarBudgetReadStream(inner, 4096, 64, 4);
        var buffer = new byte[512]; Assert.Equal(512, bounded.Read(buffer, 0, buffer.Length));
        Assert.False(bounded.CanSeek); Assert.True(bounded.CanRead); Assert.False(bounded.CanWrite);
        Assert.Throws<NotSupportedException>(() => _ = bounded.Length);
        Assert.Throws<NotSupportedException>(() => _ = bounded.Position);
        Assert.Throws<NotSupportedException>(() => bounded.Flush());
        Assert.Throws<NotSupportedException>(() => bounded.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => bounded.SetLength(0));
    }

    [Fact]
    public void AggregateSpecialMetadataAndTrailingNonzeroHeaderAreRejected()
    {
        var special = Tar("file.txt", "payload").ToArray(); special[156] = (byte)'x';
        using (var stream = new TarBudgetReadStream(new MemoryStream(special), 4096, 64, 4, maximumMetadataBytes: 6))
            Assert.Throws<InvalidDataException>(() => stream.Finish());
        var trailing = Tar("file.txt", "payload").ToArray();
        var member = trailing[..1024]; var zero = new byte[512];
        using var malformed = new MemoryStream(member.Concat(zero).Concat(member).Concat(new byte[1024]).ToArray());
        using var bounded = new TarBudgetReadStream(malformed, 8192, 64, 4);
        Assert.Throws<InvalidDataException>(() => bounded.Finish());
    }

    [Fact]
    public void TarHeaderRejectsPartialEndBlockAndWriteOrPositionChanges()
    {
        using (var truncated = new TarBudgetReadStream(new MemoryStream(new byte[513]), 4096, 64, 4))
            Assert.Throws<InvalidDataException>(() => truncated.Finish());
        using var input = new TarBudgetReadStream(Tar("file.txt", "payload"), 4096, 64, 4);
        Assert.Throws<NotSupportedException>(() => input.Position = 0);
        Assert.Throws<NotSupportedException>(() => input.Write(new byte[1], 0, 1));
    }

    // protects: every supported tar metadata header is charged against its separate byte ceiling.
    // fails_when: GNU long-name/link or global PAX metadata bypasses the metadata bound.
    // seam: intentionally exposed streaming transport with independently authored raw header bytes.
    // no_claim: transport accounting does not authorize the metadata's archive semantics.
    [Theory]
    [InlineData('x')]
    [InlineData('g')]
    [InlineData('L')]
    [InlineData('K')]
    public void EverySpecialHeaderEnforcesExactAndOneOverMetadataBudgets(char type)
    {
        var bytes = Tar("metadata", "payload").ToArray();
        bytes[156] = (byte)type;
        using (var exact = new TarBudgetReadStream(new MemoryStream(bytes), bytes.Length, 64, 4, 7, 7))
        {
            exact.Finish();
            Assert.Equal(bytes.Length, exact.Consumed);
            Assert.Equal(0, exact.Read(new byte[1], 0, 1));
        }
        using var rejected = new TarBudgetReadStream(new MemoryStream(bytes), bytes.Length, 64, 4, 7, 6);
        var failure = Assert.Throws<InvalidDataException>(() => rejected.Finish());
        Assert.Contains("member exceeds", failure.Message, StringComparison.Ordinal);
        Assert.Equal(512, rejected.Consumed);
    }

    [Fact]
    public void FragmentedHeaderAndPayloadReadsPreserveExactBytesAcrossBoundaries()
    {
        var bytes = Tar("fragmented", "payload").ToArray();
        using var stream = new TarBudgetReadStream(new MemoryStream(bytes), bytes.Length, 64, 4);
        using var actual = new MemoryStream();
        var single = new byte[1];
        while (stream.Read(single, 0, 1) != 0) actual.WriteByte(single[0]);
        Assert.Equal(bytes, actual.ToArray());
        Assert.Equal(bytes.Length, stream.Consumed);
    }

    [Theory]
    [InlineData('/')]
    [InlineData('8')]
    public void NumericHeaderRejectsDigitsOutsideTheOctalAlphabet(char value)
    {
        var bytes = Tar("invalid-number", "payload").ToArray();
        bytes[124] = (byte)value;
        using var stream = new TarBudgetReadStream(new MemoryStream(bytes), bytes.Length, 64, 4);
        Assert.Throws<InvalidDataException>(() => stream.Finish());
        Assert.Equal(512, stream.Consumed);
    }

    private static MemoryStream Tar(string name, string value)
    {
        var output = new MemoryStream(); using (var writer = new TarWriter(output, leaveOpen: true)) writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(value)) }); output.Write(new byte[1024]); output.Position = 0; return output;
    }

    private static void WriteOctal(Span<byte> field, long value)
    {
        field.Fill((byte)'0'); var text = Convert.ToString(value, 8)!; Encoding.ASCII.GetBytes(text).CopyTo(field[(field.Length - text.Length - 1)..]); field[^1] = 0;
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
