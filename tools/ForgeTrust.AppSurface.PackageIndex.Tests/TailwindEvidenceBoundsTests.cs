using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

/// <summary>Verifies bounded proof input and create-new output at the evidence boundary.</summary>
public sealed class TailwindEvidenceBoundsTests : IDisposable
{
    private readonly string _root = TestPathUtils.PathUnder("/private/tmp", "tailwind-evidence-bounds", Guid.NewGuid().ToString("N"));

    [Fact]
    public void StrictJson_RejectsNestedDuplicateFieldsAndOversizedDocuments()
    {
        var duplicate = Encoding.UTF8.GetBytes("{\"hosts\":[{\"rid\":\"linux-x64\",\"rid\":\"osx-x64\"}]}");
        var error = Assert.Throws<PackageIndexException>(() => TailwindEvidenceWorkflow.ParseStrict(duplicate, "native receipt"));
        Assert.Contains("duplicate field 'rid'", error.Message, StringComparison.Ordinal);

        var oversized = new byte[TailwindProofSubjectService.MaximumDocumentBytes + 1];
        var limit = Assert.Throws<PackageIndexException>(() => TailwindEvidenceWorkflow.ParseStrict(oversized, "native receipt"));
        Assert.Contains("16 MiB", limit.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BoundedFileReadsAndHashes_AllowExactLimitAndRejectLargerInput()
    {
        Directory.CreateDirectory(_root);
        var path = TestPathUtils.PathUnder(_root, "evidence.bin");
        var bytes = Encoding.UTF8.GetBytes("frozen-evidence");
        await File.WriteAllBytesAsync(path, bytes);

        Assert.Equal(bytes, TailwindEvidenceWorkflow.ReadBounded(path, bytes.Length));
        Assert.Equal(Sha256(bytes), TailwindEvidenceWorkflow.HashFileBounded(path, bytes.Length));
        Assert.Equal(Sha256(bytes), await TailwindEvidenceWorkflow.HashFileBoundedAsync(path, bytes.Length, CancellationToken.None));
        Assert.Throws<PackageIndexException>(() => TailwindEvidenceWorkflow.ReadBounded(path, bytes.Length - 1));
        Assert.Throws<PackageIndexException>(() => TailwindEvidenceWorkflow.HashFileBounded(path, bytes.Length - 1));
        await Assert.ThrowsAsync<PackageIndexException>(() =>
            TailwindEvidenceWorkflow.HashFileBoundedAsync(path, bytes.Length - 1, CancellationToken.None));
    }

    [Fact]
    public async Task EvidenceJsonOutput_IsCreateNewAndNeverReplacesACompletedReceipt()
    {
        Directory.CreateDirectory(_root);
        var path = TestPathUtils.PathUnder(_root, "receipt.json");
        await TailwindEvidenceWorkflow.WriteCreateNewJsonAsync(path, new { schema = "v1", status = "succeeded" }, CancellationToken.None);
        var original = await File.ReadAllBytesAsync(path);

        await Assert.ThrowsAnyAsync<IOException>(() =>
            TailwindEvidenceWorkflow.WriteCreateNewJsonAsync(path, new { schema = "v1", status = "changed" }, CancellationToken.None));

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        using var json = JsonDocument.Parse(original);
        Assert.Equal("succeeded", json.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void ManifestZipEntryRead_RejectsDeclaredOversizeBeforeInflation()
    {
        Directory.CreateDirectory(_root);
        var archivePath = TestPathUtils.PathUnder(_root, "manifest.nupkg");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            using var writer = archive.CreateEntry("build/tailwind.release.json").Open();
            writer.Write(new byte[TailwindProofSubjectService.MaximumDocumentBytes + 1]);
        }
        using var zip = ZipFile.OpenRead(archivePath);

        var error = Assert.Throws<PackageIndexException>(() =>
            TailwindEvidenceWorkflow.ReadZipEntryBounded(zip.Entries.Single()));

        Assert.Contains("16 MiB", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BoundedCaptureStream_ImplementsWriteOnlyStreamContractAndDrainsOverflow()
    {
        using var capture = new BoundedCaptureStream(4);
        Assert.False(capture.CanRead);
        Assert.False(capture.CanSeek);
        Assert.True(capture.CanWrite);
        Assert.Equal(0, capture.Length);
        Assert.Equal(0, capture.Position);
        capture.Flush();

        capture.Write(Encoding.UTF8.GetBytes("ab"), 0, 2);
        await capture.WriteAsync(Encoding.UTF8.GetBytes("cd"), 0, 2, CancellationToken.None);
        Assert.Equal("abcd", capture.GetText());
        Assert.False(capture.Truncated);
        Assert.Equal(4, capture.Length);
        Assert.Throws<NotSupportedException>(() => capture.Read(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => capture.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => capture.SetLength(0));
        Assert.Throws<NotSupportedException>(() => capture.Position = 0);

        await capture.WriteAsync(Encoding.UTF8.GetBytes("overflow"), CancellationToken.None);
        Assert.True(capture.Truncated);
        Assert.Equal(4, capture.Length);
        Assert.StartsWith("abcd", capture.GetText(), StringComparison.Ordinal);
        Assert.Contains("output truncated", capture.GetText(), StringComparison.Ordinal);
        Assert.Equal(4 * 1024 * 1024, ExternalCapturePolicy.ReleaseProof.MaximumBytesPerStream);
    }

    [Fact]
    public async Task UnboundedCommandCapture_PreservesLegacyBufferedResult()
    {
        var result = await new CliWrapCommandRunner().RunAsync(
            new ExternalCommandRequest("dotnet", ["--version"], Directory.GetCurrentDirectory(),
                "dotnet version", "reading SDK version", 10_000), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(result.StandardOutput.Trim());
        Assert.False(result.StandardOutputTruncated);
        Assert.False(result.StandardErrorTruncated);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
