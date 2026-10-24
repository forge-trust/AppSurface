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
            Assert.Throws<InvalidDataException>(() => new TarReader(bounded).GetNextEntry()!.DataStream!.CopyTo(Stream.Null));
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
    public void Rejects_budget_crossing_at_header_and_payload_boundaries()
    {
        using (var input = Tar("file.txt", "payload")) using (var bounded = new TarBudgetReadStream(input, 511, 64, 4))
            Assert.Throws<InvalidDataException>(() => bounded.Finish());
        using (var input = Tar("file.txt", "payload")) using (var bounded = new TarBudgetReadStream(input, 2048, 6, 4))
            Assert.Throws<InvalidDataException>(() => new TarReader(bounded).GetNextEntry()!.DataStream!.CopyTo(Stream.Null));
    }

    private static MemoryStream Tar(string name, string value)
    {
        var output = new MemoryStream(); using (var writer = new TarWriter(output, leaveOpen: true)) writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(value)) }); output.Write(new byte[1024]); output.Position = 0; return output;
    }
}
